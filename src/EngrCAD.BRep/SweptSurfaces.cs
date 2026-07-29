using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// The seed-selection rule shared by all three swept surfaces' one-dimensional inverse
/// evaluations. ONE copy, because this exact defect was inherited rather than introduced:
/// the generic 17×17 base implementation has the same single-best-seed weakness, and each
/// 1D reduction copied it.
/// </summary>
/// <remarks>
/// <para><b>The rule.</b> Refine from every LOCAL MINIMUM of the sampled residual and from
/// each minimum's two neighbours — never from the single best seed alone. It is purely
/// combinatorial: no epsilon decides which seeds are worth trying, so it cannot be tuned
/// wrong at one scale and right at another.</para>
/// <para><b>Why the neighbours.</b> A generator can double back so that two branches of the
/// residual sit closer together than one seed interval — a sliver profile, an extrusion of a
/// hairpin, a revolve whose generator nearly touches its own reflection. The sampled residual
/// then shows ONE broad minimum straddling both branches, 1D Gauss–Newton from it descends to
/// whichever branch happens to be nearer, and the answer comes back as the MIRRORED
/// parameter: a point tens of millimetres away that still passes every structural check.
/// Marking the neighbours costs two more Newton runs — a handful of curve evaluations — and
/// puts a seed on each side of the straddle.</para>
/// </remarks>
internal static class SeedSelection
{
    /// <summary>
    /// Flags the seeds worth refining and returns the globally best seed index (the fallback
    /// answer if every refinement fails). <paramref name="residuals"/> holds one value per
    /// seed, seeds + 1 of them; <paramref name="periodic"/> wraps the neighbour relation for
    /// a closed generator, whose first and last samples are the same point.
    /// </summary>
    public static int MarkCandidates(ReadOnlySpan<double> residuals, Span<bool> refine, bool periodic)
    {
        int seeds = residuals.Length - 1;
        int globalBest = 0;
        double smallest = double.PositiveInfinity;
        for (int i = 0; i <= seeds; i++)
        {
            if (residuals[i] < smallest)
            {
                smallest = residuals[i];
                globalBest = i;
            }
        }
        Mark(refine, globalBest, seeds, periodic);
        for (int i = 0; i <= seeds; i++)
        {
            if (IsLocalMinimum(residuals, i, seeds, periodic))
                Mark(refine, i, seeds, periodic);
        }
        return globalBest;
    }

    /// <summary>Flags seed <paramref name="i"/> and its two neighbours for refinement.</summary>
    private static void Mark(Span<bool> refine, int i, int seeds, bool periodic)
    {
        refine[i] = true;
        refine[i > 0 ? i - 1 : (periodic ? seeds - 1 : 1)] = true;
        refine[i < seeds ? i + 1 : (periodic ? 1 : seeds - 1)] = true;
    }

    /// <summary>Is sample <paramref name="i"/> no worse than both its neighbours (wrapping
    /// when the generator closes)?</summary>
    private static bool IsLocalMinimum(ReadOnlySpan<double> residuals, int i, int seeds, bool periodic)
    {
        double here = residuals[i];
        int previous = i > 0 ? i - 1 : (periodic ? seeds - 1 : 1);
        int next = i < seeds ? i + 1 : (periodic ? 1 : seeds - 1);
        return residuals[previous] >= here && residuals[next] >= here;
    }
}

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
        Span<double> sampled = stackalloc double[seeds + 1];
        for (int i = 0; i <= seeds; i++)
        {
            sampled[i] = Perpendicular(
                generator.PointAt(domain.ParameterAt((double)i / seeds)) - target,
                direction, directionLengthSquared).LengthSquared;
        }

        // Refine from every local minimum AND its neighbours, not the single best seed —
        // see SeedSelection for why a generator that doubles back otherwise returns the
        // mirrored parameter.
        Span<bool> refine = stackalloc bool[seeds + 1];
        int globalBest = SeedSelection.MarkCandidates(sampled, refine, periodic);
        double parameter = domain.ParameterAt((double)globalBest / seeds);
        double best = double.PositiveInfinity;
        for (int i = 0; i <= seeds; i++)
        {
            if (!refine[i])
                continue;
            double candidate = Solve(domain.ParameterAt((double)i / seeds));
            double residual = Perpendicular(
                generator.PointAt(candidate) - target, direction, directionLengthSquared).LengthSquared;
            if (residual < best)
            {
                best = residual;
                parameter = candidate;
            }
        }

        double Solve(double seed)
        {
            for (int iteration = 0; iteration < 25; iteration++)
            {
                var residual = Perpendicular(generator.PointAt(seed) - target, direction, directionLengthSquared);
                if (residual.Length < tolerance)
                    break;
                var slope = Perpendicular(generator.DerivativeAt(seed), direction, directionLengthSquared);
                double denominator = slope.LengthSquared;
                // Degenerate-Jacobian guard (generator tangent parallel to the extrude
                // direction): a scale-free near-underflow test, not a model tolerance.
                if (denominator < 1e-30)
                    break;
                double next = FoldIntoDomain(seed - slope.Dot(residual) / denominator, domain, periodic);
                // Stall guard at relative machine precision — the step has stopped moving,
                // so further iterations cannot improve the residual.
                if (Math.Abs(next - seed) <= period * 1e-15)
                {
                    seed = next;
                    break;
                }
                seed = next;
            }
            return seed;
        }

        double along = (target - generator.PointAt(parameter)).Dot(direction) / directionLengthSquared;
        uv = new Vector2d(parameter, Interval.Unit.Clamp(along));
        if ((PointAt(uv.X, uv.Y) - target).Length < tolerance)
            return true;
        // The 1D reduction can occasionally lose a query the 2D grid still wins (a
        // heavily aliased generator where no 1D seed's basin contains the answer but a
        // damped 2D step wanders into it) — and the base grid now multi-seeds too.
        // Deferring on failure makes "the override is never worse than the base" hold by
        // construction, at a cost paid only when the fast path has already failed.
        return base.TryProjectPoint(point, out uv, tolerance);
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
        Span<double> sampled = stackalloc double[seeds + 1];
        for (int i = 0; i <= seeds; i++)
        {
            var (r, z, _, _) = Profile(Generator, AxisOrigin, axis, domain.ParameterAt((double)i / seeds));
            sampled[i] = (r - radius) * (r - radius) + (z - height) * (z - height);
        }

        // Refine from every local minimum AND its neighbours (SeedSelection): a generator
        // whose (radius, axial) profile doubles back — a bead, a re-entrant vase wall, a
        // torus generator seen from outside — hides two branches inside one seed interval,
        // and a single seed silently returns the mirrored one.
        Span<bool> refine = stackalloc bool[seeds + 1];
        int globalBest = SeedSelection.MarkCandidates(sampled, refine, periodic);
        double parameter = domain.ParameterAt((double)globalBest / seeds);
        double best = double.PositiveInfinity;
        for (int i = 0; i <= seeds; i++)
        {
            if (!refine[i])
                continue;
            double candidate = Solve(domain.ParameterAt((double)i / seeds));
            var (r, z, _, _) = Profile(Generator, AxisOrigin, axis, candidate);
            double residual = (r - radius) * (r - radius) + (z - height) * (z - height);
            if (residual < best)
            {
                best = residual;
                parameter = candidate;
            }
        }

        double Solve(double seed)
        {
            for (int iteration = 0; iteration < 25; iteration++)
            {
                var (r, z, dr, dz) = Profile(Generator, AxisOrigin, axis, seed);
                double fr = r - radius, fz = z - height;
                if (Math.Sqrt(fr * fr + fz * fz) < tolerance)
                    break;
                double denominator = dr * dr + dz * dz;
                // Degenerate-Jacobian guard: a scale-free near-underflow test.
                if (denominator < 1e-30)
                    break;
                double next = FoldIntoDomain(seed - (dr * fr + dz * fz) / denominator, domain, periodic);
                // Stall guard at relative machine precision.
                if (Math.Abs(next - seed) <= period * 1e-15)
                {
                    seed = next;
                    break;
                }
                seed = next;
            }
            return seed;
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
        if ((PointAt(uv.X, uv.Y) - point).Length < tolerance)
            return true;
        // See ExtrudedSurface.TryProjectPoint: defer to the (now multi-seeded) base grid
        // on failure, so the override is never worse than the base by construction.
        return base.TryProjectPoint(point, out uv, tolerance);
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

    /// <summary>The seed X direction, already projected perpendicular to the path's start
    /// tangent and normalized — i.e. what the constructor STORED, not what it was
    /// handed. Feeding this back reconstructs the same surface: the projection is a
    /// no-op on an already-perpendicular unit vector.</summary>
    public Vector3d StartX => _startX;

    /// <summary>How many rotation-minimizing frames were computed along the path. It is
    /// part of the surface's identity, not a hint — every interior frame is interpolated
    /// between these, so two sweeps differing only in this count are different surfaces.
    /// Exposed so a serializer can reproduce one exactly.</summary>
    public int FrameCount => _frameParams.Length;

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

    public override Vector3d PointAt(double u, double v) => FramePoint(LocalOffset(u), v);

    /// <summary>The profile point at generator parameter u, in start-frame (x, y) coordinates.</summary>
    private Vector2d LocalOffset(double u)
    {
        var p = _profileGenerator.PointAt(u) - _profileOrigin;
        return new Vector2d(p.Dot(_startX), p.Dot(_startY));
    }

    /// <summary>d/du of <see cref="LocalOffset"/>, from the generator's exact derivative.</summary>
    private Vector2d LocalOffsetDerivative(double u)
    {
        var d = _profileGenerator.DerivativeAt(u);
        return new Vector2d(d.Dot(_startX), d.Dot(_startY));
    }

    /// <summary>
    /// Inverse evaluation as TWO DECOUPLED ONE-DIMENSIONAL SOLVES, completing the family
    /// begun by <see cref="ExtrudedSurface.TryProjectPoint"/> and
    /// <see cref="RevolvedSurface.TryProjectPoint"/>.
    ///
    /// The reduction those two use — "one parameter is available in closed form" — does
    /// NOT apply here, because the rotation-minimizing frame varies along the path. A
    /// different structural fact does: every surface point at path parameter v lies in
    /// the frame's own plane there, since the profile's component along the start
    /// tangent is discarded by construction. So
    /// <c>f(v) = (p − Path(v))·Tangent(v) = 0</c> is a scalar equation in v ALONE — the
    /// classic foot-of-perpendicular condition — and once v is known the point's local
    /// (x, y) offset in that frame fixes u by matching the profile generator, which is
    /// again one-dimensional. Neither solve involves the other's unknown.
    ///
    /// f is not monotone for a curving path (a point inside the osculating circle has
    /// several feet), so the roots are BRACKETED by a scan and refined by safeguarded
    /// bisection — convergence is guaranteed by the bracket, not by a good seed — and
    /// every candidate is scored on the true 3D residual. The path's two ends are always
    /// candidates: a query point beyond an end cap has no interior root at all.
    ///
    /// The base class instead walks a 17x17 (u, v) grid, and each of those 289 samples
    /// costs a full <see cref="Frame"/> evaluation AND a generator evaluation, where 17
    /// of each carry all the information (the profile offsets do not depend on v at
    /// all — they are hoisted here into one 17-entry table). Measured on a circular
    /// profile swept along an arc: 12.4x faster with identical parameters.
    /// </summary>
    public override bool TryProjectPoint(in Vector3d point, out Vector2d uv, double tolerance = 1e-8)
    {
        var pathDomain = _path.Domain;
        var generatorDomain = _profileGenerator.Domain;
        if (!double.IsFinite(pathDomain.Length) || !double.IsFinite(generatorDomain.Length))
            return base.TryProjectPoint(point, out uv, tolerance);

        // The profile offsets are independent of v, so the whole seed table for the u
        // solve is built ONCE — this is the 289-vs-17 waste the base class pays.
        const int seeds = 16; // the base class's own u resolution
        Span<Vector2d> profile = stackalloc Vector2d[seeds + 1];
        for (int i = 0; i <= seeds; i++)
            profile[i] = LocalOffset(generatorDomain.ParameterAt((double)i / seeds));

        // Bracket the roots of the plane condition. Eight candidates is far more than a
        // sweep path ever produces; overflow simply drops the extra brackets, and the
        // final residual gate still decides success.
        Span<double> candidates = stackalloc double[8];
        int count = 0;
        const int scan = 16; // the base class's own v resolution
        double previousV = pathDomain.Start;
        double previousF = PlaneResidual(point, previousV);
        for (int i = 1; i <= scan && count < candidates.Length - 2; i++)
        {
            double v = pathDomain.ParameterAt((double)i / scan);
            double f = PlaneResidual(point, v);
            // Exact-sign bracket test (a semantic zero-crossing test, not a tolerance).
            if (previousF >= 0 != f >= 0)
                candidates[count++] = RefinePlaneRoot(point, previousV, previousF, v, f);
            previousV = v;
            previousF = f;
        }
        candidates[count++] = pathDomain.Start;
        candidates[count++] = pathDomain.End;

        double best = double.PositiveInfinity;
        uv = new Vector2d(generatorDomain.Start, pathDomain.Start);
        for (int i = 0; i < count; i++)
        {
            double v = candidates[i];
            var frame = Frame(v);
            var offset = point - frame.Origin;
            var localTarget = new Vector2d(offset.Dot(frame.X), offset.Dot(frame.Y));
            double u = SolveGeneratorParameter(localTarget, profile, generatorDomain);
            double residual = (PointAt(u, v) - point).Length;
            if (residual < best)
            {
                best = residual;
                uv = new Vector2d(u, v);
            }
        }
        return best < tolerance;
    }

    /// <summary>(p − Path(v))·Tangent(v): zero exactly where p lies in the frame plane at v.</summary>
    private double PlaneResidual(in Vector3d point, double v) =>
        (point - _path.PointAt(v)).Dot(_path.TangentAt(v));

    /// <summary>
    /// Refines a sign-change bracket of <see cref="PlaneResidual"/> by the secant rule,
    /// falling back to bisection whenever the secant step would leave the bracket — the
    /// bracket, not the seed, is what guarantees convergence.
    /// </summary>
    private double RefinePlaneRoot(in Vector3d point, double lo, double flo, double hi, double fhi)
    {
        double span = hi - lo;
        for (int iteration = 0; iteration < 40; iteration++)
        {
            double denominator = fhi - flo;
            // Exact-zero division guard; a flat bracket bisects.
            double next = denominator != 0 ? lo - flo * (hi - lo) / denominator : (lo + hi) * 0.5;
            if (!(next > lo && next < hi))
                next = (lo + hi) * 0.5;
            double f = PlaneResidual(point, next);
            if (flo >= 0 != f >= 0)
            {
                hi = next;
                fhi = f;
            }
            else
            {
                lo = next;
                flo = f;
            }
            // Relative stall guard: the bracket has collapsed to machine precision.
            if (hi - lo <= span * 1e-15)
                break;
        }
        return (lo + hi) * 0.5;
    }

    /// <summary>
    /// The generator parameter whose profile offset matches <paramref name="localTarget"/>:
    /// a scan of the precomputed profile table followed by 1D Gauss–Newton on the
    /// generator's exact derivative.
    /// </summary>
    /// <remarks>
    /// Refinement starts from every local minimum of the sampled distance AND from that
    /// minimum's two neighbours — never from the single best seed. The profile a sweep
    /// actually carries is the generator projected into the START FRAME's plane, and that
    /// projection can be far thinner than the generator itself: a circle swept along a
    /// path it nearly lies in flattens into a sliver whose two sides sit closer together
    /// than one seed interval. The sampled table then shows ONE broad minimum straddling
    /// both branches, 1D Gauss–Newton from it descends to whichever branch is nearer, and
    /// the answer comes back as the MIRRORED parameter — measured as a 31% round-trip
    /// failure rate on a swept tube. Including the neighbours costs two more Newton runs
    /// (a handful of curve evaluations) and is a purely combinatorial rule with no
    /// epsilon in it. The generic 17x17 base implementation shares the same u resolution
    /// and fails 33% of those queries, so this is a seed-resolution defect the reduction
    /// INHERITED and now fixes, not one it introduced.
    /// </remarks>
    private double SolveGeneratorParameter(in Vector2d localTarget, ReadOnlySpan<Vector2d> profile, in Interval domain)
    {
        int seeds = profile.Length - 1;
        bool periodic = _profileGenerator.IsClosed;

        Span<double> sampled = stackalloc double[seeds + 1];
        for (int i = 0; i <= seeds; i++)
            sampled[i] = (profile[i] - localTarget).LengthSquared;

        Span<bool> refine = stackalloc bool[seeds + 1];
        int globalBest = SeedSelection.MarkCandidates(sampled, refine, periodic);

        double best = double.PositiveInfinity, answer = domain.ParameterAt((double)globalBest / seeds);
        for (int i = 0; i <= seeds; i++)
        {
            if (!refine[i])
                continue;
            double candidate = RefineGeneratorParameter(domain.ParameterAt((double)i / seeds), localTarget, domain, periodic);
            double residual = (LocalOffset(candidate) - localTarget).LengthSquared;
            if (residual < best)
            {
                best = residual;
                answer = candidate;
            }
        }
        return answer;
    }

    /// <summary>1D Gauss–Newton from one seed, on the generator's exact derivative.</summary>
    private double RefineGeneratorParameter(double parameter, in Vector2d localTarget, in Interval domain, bool periodic)
    {
        double period = domain.Length;
        // 12 iterations, not the base class's 25: Gauss-Newton from a bracketed seed
        // converges quadratically, and several seeds are refined per query now.
        for (int iteration = 0; iteration < 12; iteration++)
        {
            var residual = LocalOffset(parameter) - localTarget;
            var slope = LocalOffsetDerivative(parameter);
            double denominator = slope.LengthSquared;
            // Degenerate-Jacobian guard: a scale-free near-underflow test.
            if (denominator < 1e-30)
                break;
            double next = FoldIntoDomain(parameter - slope.Dot(residual) / denominator, domain, periodic);
            // Stall guard at relative machine precision.
            if (Math.Abs(next - parameter) <= period * 1e-15)
            {
                parameter = next;
                break;
            }
            parameter = next;
        }
        return parameter;
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
    /// <summary>The sweep this rail rides.</summary>
    public SweptSurface Surface => surface;

    /// <summary>The fixed (x, y) offset in the sweep's moving profile plane.</summary>
    public Vector2d LocalOffset => localOffset;

    public override Interval Domain => surface.DomainV;
    public override bool IsClosed => false;
    public override Vector3d PointAt(double t) => surface.FramePoint(localOffset, t);
}
