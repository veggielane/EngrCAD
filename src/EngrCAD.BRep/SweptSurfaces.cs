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

    public RevolvedSurface(Curve3d generator, in Vector3d axisOrigin, in Vector3d axisDirection)
    {
        Generator = generator;
        AxisOrigin = axisOrigin;
        AxisDirection = axisDirection.Normalized();
    }

    public override Interval DomainU => new(0, 2 * Math.PI);
    public override Interval DomainV => Generator.Domain;

    public override Vector3d PointAt(double u, double v)
    {
        var rotation = Quaterniond.FromAxisAngle(AxisDirection, u);
        return AxisOrigin + rotation.Rotate(Generator.PointAt(v) - AxisOrigin);
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
        var (origin, x, y) = FrameAt(v);
        return origin + x * localOffset.X + y * localOffset.Y;
    }

    /// <summary>Frame (origin, x, y) at the path parameter; exact at the internal sample parameters.</summary>
    public (Vector3d Origin, Vector3d X, Vector3d Y) FrameAt(double v)
    {
        double s = _path.Domain.NormalizedParameterOf(_path.Domain.Clamp(v)) * (_frameParams.Length - 1);
        int k = Math.Clamp((int)s, 0, _frameParams.Length - 2);
        double f = s - k;

        var tangent = _path.TangentAt(v);
        var x = Vector3d.Lerp(_frameX[k], _frameX[k + 1], f);
        x = (x - tangent * x.Dot(tangent)).Normalized();
        return (_path.PointAt(v), x, tangent.Cross(x));
    }

    /// <summary>Rigid transform mapping start-frame geometry to the frame at <paramref name="v"/>.</summary>
    public Matrix4d TransformTo(double v)
    {
        var t0 = _path.TangentAt(_path.Domain.Start);
        var (origin, x, y) = FrameAt(v);
        var t = x.Cross(y);

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
