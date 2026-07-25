using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// Ruled surface P(u, v) = C(u) + v·direction for v ∈ [0, 1]: the side surface of an
/// extrusion. With the generator wound counter-clockwise about the extrude direction,
/// ∂u × ∂v points outward.
/// </summary>
public sealed class ExtrudedSurface(Curve3d generator, Vector3d direction) : Surface
{
    public Curve3d Generator => generator;
    public Vector3d Direction => direction;

    public override Interval DomainU => generator.Domain;
    public override Interval DomainV => Interval.Unit;

    public override Vector3d PointAt(double u, double v) => generator.PointAt(u) + direction * v;

    public override Vector3d NormalAt(double u, double v) =>
        generator.TangentAt(u).Cross(direction).Normalized();

    /// <summary>
    /// Inverse evaluation reduced to ONE dimension. P(u, v) = C(u) + v·direction, so the
    /// component of (P − point) along the direction is whatever v makes it — only the
    /// perpendicular component constrains u. Solving Q(C(u) − point) = 0 (Q = the
    /// projector that removes the direction component) then gives v in closed form.
    ///
    /// The base class instead scans a 17x17 (u, v) grid and Gauss–Newtons in 2D, which
    /// re-evaluates the SAME generator point once per v column: 289 curve evaluations
    /// where 17 carry all the information. Inverse evaluation is the inner loop of every
    /// face pullback (<see cref="FaceGeometry.PullLoops"/>, <c>Contains</c>, splitting,
    /// trimmed tessellation), so this is the hot leaf of B-Rep booleans on
    /// extrusion-heavy tools — sketch pockets and engraved text, whose every profile
    /// segment is its own extruded face.
    ///
    /// Robustness is not traded away: the seed scan uses the base class's own u
    /// resolution, ranked by the exactly-optimal v instead of a quantized one, and the
    /// refinement runs on the true 1D manifold of the problem with the generator's exact
    /// <see cref="Curve3d.DerivativeAt"/> rather than a damped 2D step. As in the base,
    /// a point that cannot be brought within <paramref name="tolerance"/> returns false.
    /// </summary>
    public override bool TryProjectPoint(in Vector3d point, out Vector2d uv, double tolerance = 1e-8)
    {
        double directionLengthSquared = direction.LengthSquared;
        // Exact-zero guard: a collapsed extrusion has no v axis to solve for, so the
        // generic grid search is the only meaningful answer.
        if (directionLengthSquared <= 0)
            return base.TryProjectPoint(point, out uv, tolerance);

        // Static local functions: no closure allocation on this very hot path.
        static Vector3d Perpendicular(in Vector3d v, in Vector3d axis, double axisLengthSquared) =>
            v - axis * (v.Dot(axis) / axisLengthSquared);

        var target = point;
        var domain = generator.Domain;
        bool periodic = generator.IsClosed;
        double period = domain.Length;

        const int seeds = 16; // the base class's u resolution
        double best = double.PositiveInfinity, parameter = domain.Start;
        for (int i = 0; i <= seeds; i++)
        {
            double u = domain.ParameterAt((double)i / seeds);
            double squared = Perpendicular(generator.PointAt(u) - target, direction, directionLengthSquared)
                .LengthSquared;
            if (squared < best)
            {
                best = squared;
                parameter = u;
            }
        }

        for (int iteration = 0; iteration < 25; iteration++)
        {
            var residual = Perpendicular(generator.PointAt(parameter) - target, direction, directionLengthSquared);
            if (residual.Length < tolerance)
                break;
            var slope = Perpendicular(generator.DerivativeAt(parameter), direction, directionLengthSquared);
            double denominator = slope.LengthSquared;
            // Degenerate-Jacobian guard (generator tangent parallel to the extrude
            // direction): a scale-free near-underflow test, not a model tolerance.
            if (denominator < 1e-30)
                break;
            double next = FoldIntoDomain(parameter - slope.Dot(residual) / denominator, domain, periodic);
            // Stall guard at relative machine precision — the step has stopped moving,
            // so further iterations cannot improve the residual.
            if (Math.Abs(next - parameter) <= period * 1e-15)
            {
                parameter = next;
                break;
            }
            parameter = next;
        }

        double along = (target - generator.PointAt(parameter)).Dot(direction) / directionLengthSquared;
        uv = new Vector2d(parameter, Interval.Unit.Clamp(along));
        return (PointAt(uv.X, uv.Y) - target).Length < tolerance;
    }
}

/// <summary>
/// Surface of revolution: the generator curve rotated about an axis; u is the rotation
/// angle [0, 2π] (periodic), v the generator parameter. With the generator traversed
/// counter-clockwise in (radius, height) coordinates, ∂u × ∂v points outward.
/// </summary>
public sealed class RevolvedSurface : Surface
{
    public Curve3d Generator { get; }
    public Vector3d AxisOrigin { get; }
    public Vector3d AxisDirection { get; }

    /// <summary>Total swept angle; 2π for a full surface of revolution.</summary>
    public double Angle { get; }

    // Angular guard at the Tolerance.Default.Angular scale (kept literal: full-turn
    // detection is topology-critical and must not drift with a caller's tolerance).
    public bool IsFullTurn => Math.Abs(Angle - 2 * Math.PI) < 1e-9;

    public RevolvedSurface(Curve3d generator, in Vector3d axisOrigin, in Vector3d axisDirection, double angle = 2 * Math.PI)
    {
        if (angle <= 0 || angle > 2 * Math.PI + 1e-9)
            throw new ArgumentOutOfRangeException(nameof(angle));
        Generator = generator;
        AxisOrigin = axisOrigin;
        AxisDirection = axisDirection.Normalized();
        Angle = angle;
    }

    public override Interval DomainU => new(0, Angle);
    public override Interval DomainV => Generator.Domain;

    public override Vector3d PointAt(double u, double v)
    {
        var rotation = Quaterniond.FromAxisAngle(AxisDirection, u);
        return AxisOrigin + rotation.Rotate(Generator.PointAt(v) - AxisOrigin);
    }

    /// <summary>
    /// Inverse evaluation reduced to ONE dimension, the mirror of
    /// <see cref="ExtrudedSurface.TryProjectPoint"/>. A revolve is the generator's
    /// (radius, axial) profile rotated about the axis: u is the point's azimuth —
    /// available in closed form once v is known — so only the profile match constrains
    /// v. Solving the 2-residual (r(v) − r, z(v) − z) by 1D Gauss–Newton with the
    /// generator's exact derivative replaces the base class's 17x17 (u, v) grid scan,
    /// which re-evaluates the same generator point once per angle column. This is the
    /// hot leaf of drilled-hole booleans, whose tools are revolved sketches.
    ///
    /// The azimuth is measured from the generator's own radial direction, so the
    /// returned u is phase-consistent with <see cref="PointAt"/> by construction. Points
    /// on the axis (poles), where the azimuth is undefined, defer to the base class.
    /// </summary>
    public override bool TryProjectPoint(in Vector3d point, out Vector2d uv, double tolerance = 1e-8)
    {
        var axis = AxisDirection; // normalized at construction
        var offset = point - AxisOrigin;
        double height = offset.Dot(axis);
        var radial = offset - axis * height;
        double radius = radial.Length;
        if (radius <= tolerance)
            return base.TryProjectPoint(point, out uv, tolerance); // on the axis: no azimuth

        var domain = Generator.Domain;
        bool periodic = Generator.IsClosed;
        double period = domain.Length;

        // The generator's (radius, axial) profile and its exact derivatives. Static so
        // this very hot path allocates no closure.
        static (double R, double Z, double DR, double DZ) Profile(
            Curve3d generator, in Vector3d axisOrigin, in Vector3d axis, double v)
        {
            var q = generator.PointAt(v) - axisOrigin;
            double z = q.Dot(axis);
            var r = q - axis * z;
            double length = r.Length;
            var slope = generator.DerivativeAt(v);
            double dz = slope.Dot(axis);
            var dr = slope - axis * dz;
            // On the axis the radius is not differentiable; report a zero radial slope so
            // the Gauss–Newton step falls back to the axial residual alone.
            return (length, z, length > 0 ? r.Dot(dr) / length : 0, dz);
        }

        const int seeds = 16; // the base class's v resolution
        double best = double.PositiveInfinity, parameter = domain.Start;
        for (int i = 0; i <= seeds; i++)
        {
            double v = domain.ParameterAt((double)i / seeds);
            var (r, z, _, _) = Profile(Generator, AxisOrigin, axis, v);
            double squared = (r - radius) * (r - radius) + (z - height) * (z - height);
            if (squared < best)
            {
                best = squared;
                parameter = v;
            }
        }

        for (int iteration = 0; iteration < 25; iteration++)
        {
            var (r, z, dr, dz) = Profile(Generator, AxisOrigin, axis, parameter);
            double fr = r - radius, fz = z - height;
            if (Math.Sqrt(fr * fr + fz * fz) < tolerance)
                break;
            double denominator = dr * dr + dz * dz;
            // Degenerate-Jacobian guard: a scale-free near-underflow test.
            if (denominator < 1e-30)
                break;
            double next = FoldIntoDomain(parameter - (dr * fr + dz * fz) / denominator, domain, periodic);
            // Stall guard at relative machine precision.
            if (Math.Abs(next - parameter) <= period * 1e-15)
            {
                parameter = next;
                break;
            }
            parameter = next;
        }

        // Azimuth from the generator point at the solved v to the query point.
        var generatorOffset = Generator.PointAt(parameter) - AxisOrigin;
        var generatorRadial = generatorOffset - axis * generatorOffset.Dot(axis);
        if (generatorRadial.LengthSquared <= 0)
            return base.TryProjectPoint(point, out uv, tolerance); // generator on the axis here
        double angle = Math.Atan2(generatorRadial.Cross(radial).Dot(axis), generatorRadial.Dot(radial));
        if (angle < 0)
            angle += 2 * Math.PI;
        // A partial revolve's domain is [0, Angle]; an angle just past 2π - epsilon is
        // nearer 0 than the far end, so offer both branches before clamping.
        if (!IsFullTurn && angle > Angle && 2 * Math.PI - angle < angle - Angle)
            angle -= 2 * Math.PI;

        uv = new Vector2d(DomainU.Clamp(angle), parameter);
        return (PointAt(uv.X, uv.Y) - point).Length < tolerance;
    }
}

/// <summary>
/// Profile swept along a path with rotation-minimizing frames (Wang et al.'s double
/// reflection). u is the profile-generator parameter, v the path parameter. Frames are
/// computed at discrete path samples; between samples the frame is interpolated and
/// re-orthonormalized against the exact path tangent — evaluation at the sample
/// parameters themselves is exact, which is what tessellation uses.
/// </summary>
public sealed class SweptSurface : Surface
{
    private readonly Curve3d _profileGenerator;
    private readonly Curve3d _path;
    private readonly Vector3d _profileOrigin;   // path start point; profile offsets are relative to it
    private readonly Vector3d _startX;
    private readonly Vector3d _startY;
    private readonly double[] _frameParams;
    private readonly Vector3d[] _frameX;

    public Curve3d Generator => _profileGenerator;
    public Curve3d Path => _path;

    public SweptSurface(Curve3d profileGenerator, Curve3d path, in Vector3d startX, int frameCount = 64)
    {
        if (frameCount < 2)
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        _profileGenerator = profileGenerator;
        _path = path;
        _profileOrigin = path.PointAt(path.Domain.Start);

        var t0 = path.TangentAt(path.Domain.Start);
        _startX = (startX - t0 * startX.Dot(t0)).Normalized();
        _startY = t0.Cross(_startX);

        // Double-reflection rotation-minimizing frames.
        _frameParams = new double[frameCount];
        _frameX = new Vector3d[frameCount];
        var points = new Vector3d[frameCount];
        var tangents = new Vector3d[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            _frameParams[i] = path.Domain.ParameterAt((double)i / (frameCount - 1));
            points[i] = path.PointAt(_frameParams[i]);
            tangents[i] = path.TangentAt(_frameParams[i]);
        }
        _frameX[0] = _startX;
        for (int i = 0; i < frameCount - 1; i++)
        {
            var v1 = points[i + 1] - points[i];
            double c1 = v1.LengthSquared;
            if (c1 <= Tolerance.Default.Linear * Tolerance.Default.Linear)
            {
                _frameX[i + 1] = _frameX[i];
                continue;
            }
            var rL = _frameX[i] - v1 * (2 / c1 * v1.Dot(_frameX[i]));
            var tL = tangents[i] - v1 * (2 / c1 * v1.Dot(tangents[i]));
            var v2 = tangents[i + 1] - tL;
            double c2 = v2.LengthSquared;
            // Near-underflow guard: the second reflection is the identity when the
            // reflected tangent already matches (straight path); not a model tolerance.
            var x = c2 <= 1e-30 ? rL : rL - v2 * (2 / c2 * v2.Dot(rL));
            _frameX[i + 1] = (x - tangents[i + 1] * x.Dot(tangents[i + 1])).Normalized();
        }
    }

    public override Interval DomainU => _profileGenerator.Domain;
    public override Interval DomainV => _path.Domain;

    public override Vector3d PointAt(double u, double v)
    {
        var p = _profileGenerator.PointAt(u) - _profileOrigin;
        return FramePoint(new Vector2d(p.Dot(_startX), p.Dot(_startY)), v);
    }

    /// <summary>Maps a profile-plane offset through the frame at path parameter <paramref name="v"/>.</summary>
    public Vector3d FramePoint(in Vector2d localOffset, double v)
    {
        var frame = Frame(v);
        return frame.Origin + frame.X * localOffset.X + frame.Y * localOffset.Y;
    }

    /// <summary>
    /// The rotation-minimizing frame at the path parameter (Z is the path tangent);
    /// exact at the internal sample parameters. The axes are built from the sweep's own
    /// interpolation, so <see cref="Frame3d.FromOrthonormal(in Vector3d, in Vector3d, in Vector3d)"/>
    /// keeps them bit-for-bit (no re-orthonormalization drift against tessellation).
    /// </summary>
    public Frame3d Frame(double v)
    {
        double s = _path.Domain.NormalizedParameterOf(_path.Domain.Clamp(v)) * (_frameParams.Length - 1);
        int k = Math.Clamp((int)s, 0, _frameParams.Length - 2);
        double f = s - k;

        var tangent = _path.TangentAt(v);
        var x = Vector3d.Lerp(_frameX[k], _frameX[k + 1], f);
        x = (x - tangent * x.Dot(tangent)).Normalized();
        return Frame3d.FromOrthonormal(_path.PointAt(v), x, tangent.Cross(x));
    }

    /// <summary>Frame (origin, x, y) at the path parameter; exact at the internal sample parameters.</summary>
    public (Vector3d Origin, Vector3d X, Vector3d Y) FrameAt(double v)
    {
        var frame = Frame(v);
        return (frame.Origin, frame.X, frame.Y);
    }

    /// <summary>Rigid transform mapping start-frame geometry to the frame at <paramref name="v"/>.</summary>
    public Matrix4d TransformTo(double v)
    {
        var t0 = _path.TangentAt(_path.Domain.Start);
        var frame = Frame(v);
        var (origin, x, y) = (frame.Origin, frame.X, frame.Y);
        var t = frame.Z;

        // Columns are the frame axes: B maps local (x, y, t) coordinates into world space.
        var basisStart = new Matrix4d(
            _startX.X, _startY.X, t0.X, 0,
            _startX.Y, _startY.Y, t0.Y, 0,
            _startX.Z, _startY.Z, t0.Z, 0,
            0, 0, 0, 1);
        var basisEnd = new Matrix4d(
            x.X, y.X, t.X, 0,
            x.Y, y.Y, t.Y, 0,
            x.Z, y.Z, t.Z, 0,
            0, 0, 0, 1);
        return Matrix4d.CreateTranslation(origin)
             * basisEnd * basisStart.Transposed()
             * Matrix4d.CreateTranslation(-_profileOrigin);
    }
}

/// <summary>The path traced by a fixed profile-plane offset during a sweep (a sweep "rail").</summary>
public sealed class SweptRailCurve(SweptSurface surface, Vector2d localOffset) : Curve3d
{
    public override Interval Domain => surface.DomainV;
    public override bool IsClosed => false;
    public override Vector3d PointAt(double t) => surface.FramePoint(localOffset, t);
}
