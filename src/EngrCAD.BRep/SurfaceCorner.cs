using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>How a corner was solved — the honesty channel for the exactness brand.</summary>
public enum CornerTier
{
    /// <summary>All carriers are planes: one algebraic solve, no iteration.</summary>
    Planar,

    /// <summary>
    /// Every carrier has a closed-form implicit distance (plane, cylinder, sphere, cone or
    /// circular revolve, straight or circular extrusion), so the corner is the root of an
    /// exact system reached by Newton — machine-precision, not approximate.
    /// </summary>
    Analytic,

    /// <summary>
    /// The marching tracer supplied the curve: exact only at its vertices, CHORDAL between
    /// them. Never reached unless a caller opts in, and the deviation is always reported.
    /// </summary>
    Traced,
}

/// <summary>Where a corner solve landed, and what it cost.</summary>
/// <param name="Point">The corner position.</param>
/// <param name="Residual">
/// The largest distance from <paramref name="Point"/> to any of the carriers. Machine-scale
/// for a converged solve; a value the caller should refuse otherwise.
/// </param>
/// <param name="Tier">Which tier answered.</param>
public readonly record struct CornerPoint(Vector3d Point, double Residual, CornerTier Tier);

/// <summary>
/// Corner re-intersection: where offset surfaces meet, and the curve along which two of them
/// run. This is the machinery three operations were blocked on — curved-face
/// <see cref="Shelling"/>, curved-face <see cref="Draft"/>, and variable-radius
/// <see cref="Filleting"/> — so it is built once, here, rather than three times.
///
/// <para><b>A corner POINT is never approximate, whatever the surfaces are.</b> It is the root
/// of a small square system, and Newton on exact carriers converges to machine precision. The
/// tiers below differ in how the residual and its gradient are obtained, not in the quality of
/// the answer: <see cref="CornerTier.Planar"/> solves three plane equations by Cramer in one
/// step, and <see cref="CornerTier.Analytic"/> Newtons on closed-form implicit distances. The
/// residual is always reported, so a caller can refuse a solve that did not converge instead
/// of building a solid around a point that is not on its own faces.</para>
///
/// <para><b>A corner CURVE is where the exactness brand bites.</b> Two offset surfaces meet in
/// an analytic conic only for particular pairs; everything else is the marching tracer, whose
/// output is a chordal <see cref="PolylineCurve3d"/> with a sampling floor no tessellation
/// density can lower. Baking one into a primary feature's B-Rep is the same mistake the
/// arc-rim corner policy already refuses (see design.md §5), so <see cref="CornerPolicy"/>
/// defaults to <see cref="CornerPolicy.ExactOnly"/> and no kernel consumer passes anything
/// else. When a caller does opt in, the curve is still made to TERMINATE exactly: its ends are
/// replaced by the corner points solved above, so the chordal error stays strictly interior
/// and never reaches a vertex — the one place the weld tier is absolute.</para>
/// </summary>
public static class SurfaceCorner
{
    /// <summary>Whether a caller will accept a chordal (traced) corner curve.</summary>
    public enum CornerPolicy
    {
        /// <summary>Analytic curves only; anything else is refused by name.</summary>
        ExactOnly,

        /// <summary>
        /// Accept a marching-tracer curve, with <see cref="CornerCurve.Deviation"/> reporting
        /// its chord error. Opt-in, and labelled in the result.
        /// </summary>
        AllowTraced,
    }

    /// <summary>A corner curve and what it cost.</summary>
    /// <param name="Curve">The curve, running from the start corner to the end corner.</param>
    /// <param name="Tier">Which tier produced it.</param>
    /// <param name="Deviation">
    /// The largest distance from the curve's INTERIOR samples to the carriers — zero for an
    /// analytic curve, the tracer's chord error otherwise.
    /// </param>
    public readonly record struct CornerCurve(Curve3d Curve, CornerTier Tier, double Deviation);

    // Newton convergence floor. Two decades below the weld tier, exactly as the boundary
    // landing solve uses: a corner becomes a VERTEX position, so it must not carry a
    // weld-scale error of its own.
    private const double ConvergenceTolerance = 1e-11;

    /// <summary>
    /// The point where <paramref name="surfaces"/> meet, nearest <paramref name="seed"/>.
    /// Exactly three planes take the algebraic path; anything else with a closed-form implicit
    /// distance Newtons onto the root. More than three surfaces are over-determined and solved
    /// in the least-squares sense — the residual then says whether they genuinely meet.
    /// </summary>
    public static bool TrySolvePoint(
        IReadOnlyList<Surface> surfaces, in Vector3d seed, out CornerPoint corner, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        corner = default;
        reason = null;
        if (surfaces.Count < 3)
        {
            reason = $"a corner needs at least three carriers; {surfaces.Count} were given";
            return false;
        }

        if (surfaces.Count == 3 && surfaces[0] is PlaneSurface a && surfaces[1] is PlaneSurface b
            && surfaces[2] is PlaneSurface c)
        {
            if (!TryIntersectPlanes(a, b, c, out var point))
            {
                reason = "three plane carriers are parallel or tangent, so the corner is not a point";
                return false;
            }
            corner = new CornerPoint(point, PlaneResidual(surfaces, point), CornerTier.Planar);
            return true;
        }

        var fields = new ImplicitSurface[surfaces.Count];
        for (int i = 0; i < surfaces.Count; i++)
        {
            if (!ImplicitSurface.TryBuild(surfaces[i], out fields[i], out var why))
            {
                reason = $"carrier {i} ({surfaces[i].GetType().Name}) has no closed-form implicit " +
                         $"distance: {why}";
                return false;
            }
        }

        var p = seed;
        for (int iteration = 0; iteration < 64; iteration++)
        {
            if (!NewtonStep(fields, p, out var step))
            {
                reason = "the carriers' normals are linearly dependent at the corner, so it is not a point";
                return false;
            }
            p += step;
            if (step.Length <= ConvergenceTolerance)
                break;
        }

        double residual = 0;
        foreach (var field in fields)
            residual = Math.Max(residual, Math.Abs(field.Evaluate(p, out _)));
        if (!double.IsFinite(residual) || residual > Math.Sqrt(ConvergenceTolerance))
        {
            reason = $"the corner solve did not converge (residual {residual:E3}); the carriers may not " +
                     "meet near the seed";
            return false;
        }
        corner = new CornerPoint(p, residual, CornerTier.Analytic);
        return true;
    }

    /// <summary>Throwing form of <see cref="TrySolvePoint"/>.</summary>
    public static CornerPoint SolvePoint(IReadOnlyList<Surface> surfaces, in Vector3d seed) =>
        TrySolvePoint(surfaces, seed, out var corner, out var reason)
            ? corner
            : throw new NotSupportedException($"Cannot solve this corner: {reason}.");

    /// <summary>
    /// The curve along which <paramref name="a"/> and <paramref name="b"/> run between the two
    /// corner points, which must already lie on both.
    ///
    /// <para>Two planes give a line; a pair whose analytic intersection
    /// <see cref="SurfaceIntersection"/> knows gives the conic branch through those corners,
    /// trimmed to them. Anything else needs the marching tracer, which
    /// <see cref="CornerPolicy.ExactOnly"/> refuses.</para>
    /// </summary>
    public static bool TrySolveCurve(
        Surface a, Surface b, in Vector3d start, in Vector3d end,
        CornerPolicy policy, out CornerCurve curve, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        curve = default;
        reason = null;

        if (a is PlaneSurface && b is PlaneSurface)
        {
            curve = new CornerCurve(new Line3d(start, end), CornerTier.Planar, 0);
            return true;
        }

        // Search a region generous enough to contain the whole branch between the corners.
        var region = Aabb.FromPoints([start, end]).Expanded(Math.Max(1e-6, start.DistanceTo(end)));
        IReadOnlyList<Curve3d> candidates;
        try
        {
            candidates = SurfaceIntersection.Intersect(a, b, region);
        }
        catch (Exception exception) when (exception is NotSupportedException or ArgumentException)
        {
            reason = exception.Message;
            return false;
        }

        Curve3d? best = null;
        bool traced = false;
        double bestError = double.PositiveInfinity;
        bool closed = start.DistanceTo(end) <= Tolerance.Default.Linear;
        foreach (var candidate in candidates)
        {
            bool candidateTraced = candidate is PolylineCurve3d
                || (candidate is CurveSegment segment && segment.Base is PolylineCurve3d);
            // A traced curve is only ON its carriers at its vertices, so holding its ENDS to
            // the weld tier would reject every one of them. It is admitted at the tracer's
            // own scale and then made to terminate on the exact corners below.
            double allowance = candidateTraced
                ? Math.Max(1e-6, start.DistanceTo(end) * 0.25 + 1e-6)
                : Tolerance.Default.Linear * Math.Max(1, start.DistanceTo(end));
            if (!TryEndParameters(candidate, start, end, allowance, out double t0, out double t1, out double error))
                continue;
            if (error >= bestError)
                continue;
            bestError = error;
            traced = candidateTraced;
            best = closed ? candidate : TrimBetween(candidate, t0, t1);
        }

        if (best is null)
        {
            reason = $"no intersection branch of {a.GetType().Name} and {b.GetType().Name} passes through " +
                     "both corners";
            return false;
        }
        if (traced && policy == CornerPolicy.ExactOnly)
        {
            reason = $"{a.GetType().Name} and {b.GetType().Name} meet in a curve the marching tracer can " +
                     "only sample, and a chordal corner would bake a fixed sampling floor into the solid; " +
                     "pass CornerPolicy.AllowTraced to accept it with its deviation reported";
            return false;
        }
        if (traced && !closed)
            best = LandOnCorners(best, start, end);

        double deviation = InteriorDeviation(best, a, b);
        curve = new CornerCurve(best, traced ? CornerTier.Traced : CornerTier.Analytic, deviation);
        return true;
    }

    /// <summary>
    /// Cramer over the three plane normals, kept in EXACTLY the arithmetic
    /// <see cref="Shelling"/> and <see cref="Draft"/> have always used, so polyhedral output
    /// stays bit-identical as the curved tiers land beside it.
    /// </summary>
    internal static bool TryIntersectPlanes(
        PlaneSurface a, PlaneSurface b, PlaneSurface c, out Vector3d point) =>
        TryIntersectPlanes(
            (a.Origin, a.Normal.Normalized()), (b.Origin, b.Normal.Normalized()),
            (c.Origin, c.Normal.Normalized()), out point);

    /// <inheritdoc cref="TryIntersectPlanes(PlaneSurface, PlaneSurface, PlaneSurface, out Vector3d)"/>
    internal static bool TryIntersectPlanes(
        in (Vector3d Origin, Vector3d Normal) a,
        in (Vector3d Origin, Vector3d Normal) b,
        in (Vector3d Origin, Vector3d Normal) c,
        out Vector3d point)
    {
        var bc = b.Normal.Cross(c.Normal);
        double determinant = a.Normal.Dot(bc);
        // Unit normals, so the determinant is a product of sines: a scale-free
        // near-degeneracy test for "these three planes do not meet in a point".
        if (Math.Abs(determinant) < 1e-12)
        {
            point = default;
            return false;
        }
        point = (bc * a.Normal.Dot(a.Origin)
               + c.Normal.Cross(a.Normal) * b.Normal.Dot(b.Origin)
               + a.Normal.Cross(b.Normal) * c.Normal.Dot(c.Origin)) / determinant;
        return true;
    }

    private static double PlaneResidual(IReadOnlyList<Surface> surfaces, in Vector3d point)
    {
        double residual = 0;
        foreach (var surface in surfaces)
        {
            var plane = (PlaneSurface)surface;
            residual = Math.Max(residual, Math.Abs((point - plane.Origin).Dot(plane.Normal.Normalized())));
        }
        return residual;
    }

    /// <summary>
    /// One Newton step toward the common root: each carrier contributes the linearized
    /// equation ∇f·δ = −f, i.e. "move onto my tangent plane". Three carriers give a square
    /// system solved by Cramer; more give the normal equations, which is the least-squares
    /// answer an over-determined corner deserves — the residual afterwards says whether the
    /// carriers really met.
    /// </summary>
    private static bool NewtonStep(ImplicitSurface[] fields, in Vector3d point, out Vector3d step)
    {
        step = default;
        if (fields.Length == 3)
        {
            var n0 = default(Vector3d);
            var n1 = default(Vector3d);
            var n2 = default(Vector3d);
            double f0 = fields[0].Evaluate(point, out n0);
            double f1 = fields[1].Evaluate(point, out n1);
            double f2 = fields[2].Evaluate(point, out n2);
            return TryIntersectPlanes(
                (point - n0 * f0, n0), (point - n1 * f1, n1), (point - n2 * f2, n2), out var target)
                && Finite(target - point, out step);
        }

        // JᵀJ δ = −Jᵀf over unit gradients: a 3x3 symmetric system.
        double a11 = 0, a12 = 0, a13 = 0, a22 = 0, a23 = 0, a33 = 0;
        double b1 = 0, b2 = 0, b3 = 0;
        foreach (var field in fields)
        {
            double f = field.Evaluate(point, out var n);
            a11 += n.X * n.X; a12 += n.X * n.Y; a13 += n.X * n.Z;
            a22 += n.Y * n.Y; a23 += n.Y * n.Z; a33 += n.Z * n.Z;
            b1 -= n.X * f; b2 -= n.Y * f; b3 -= n.Z * f;
        }
        var row0 = new Vector3d(a11, a12, a13);
        var row1 = new Vector3d(a12, a22, a23);
        var row2 = new Vector3d(a13, a23, a33);
        double determinant = row0.Dot(row1.Cross(row2));
        // Gram determinant of unit gradients: a scale-free rank test, not a model tolerance.
        if (Math.Abs(determinant) < 1e-12)
            return false;
        var rhs = new Vector3d(b1, b2, b3);
        var solution = new Vector3d(
            rhs.Dot(row1.Cross(row2)),
            row0.Dot(rhs.Cross(row2)),
            row0.Dot(row1.Cross(rhs))) / determinant;
        return Finite(solution, out step);

        static bool Finite(in Vector3d value, out Vector3d step)
        {
            step = value;
            return double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
        }
    }

    /// <summary>
    /// The candidate's parameters at the two corners, plus how far off the curve they sit.
    /// A branch that does not reach both corners at the weld tier is not this edge's.
    /// </summary>
    private static bool TryEndParameters(
        Curve3d candidate, in Vector3d start, in Vector3d end, double allowance,
        out double t0, out double t1, out double error)
    {
        t0 = t1 = 0;
        error = double.PositiveInfinity;
        if (!TryParameterAt(candidate, start, out t0, out double e0))
            return false;
        if (!TryParameterAt(candidate, end, out t1, out double e1))
            return false;
        error = Math.Max(e0, e1);
        return error <= allowance;
    }

    /// <summary>
    /// A traced polyline rebuilt to START and END on the exact corners: the vertices outside
    /// the corner-to-corner stretch are dropped and the corners themselves become the
    /// endpoints.
    ///
    /// <para>This is the tracer's documented weakness answered where it matters most. It
    /// breaks its step only after the corrector leaves the domain, so a traced curve always
    /// stops up to one march step short of the boundary it was heading for — and a corner
    /// becomes a VERTEX, the one place the weld tier is absolute. Extrapolating the last chord
    /// would land a sagitta off both carriers; the corner points substituted here were solved
    /// as an exact root, so the chordal error stays strictly interior.</para>
    /// </summary>
    private static Curve3d LandOnCorners(Curve3d curve, in Vector3d start, in Vector3d end)
    {
        var polyline = curve as PolylineCurve3d
            ?? (curve as CurveSegment)?.Base as PolylineCurve3d;
        if (polyline is null)
            return curve;

        var points = new List<Vector3d> { start };
        foreach (var point in polyline.Points)
        {
            // Interior samples only: a vertex within the weld tier of a corner IS that corner
            // and would only add a degenerate chord.
            if (point.DistanceTo(start) <= Tolerance.Default.Linear
                || point.DistanceTo(end) <= Tolerance.Default.Linear)
                continue;
            // Keep the stretch between the corners: a sample is interior when it lies on the
            // far side of both corners' perpendicular bisector half-spaces.
            var axis = end - start;
            double along = (point - start).Dot(axis);
            if (along <= 0 || along >= axis.LengthSquared)
                continue;
            points.Add(point);
        }
        points.Add(end);
        return points.Count >= 2 ? new PolylineCurve3d(points, false) : curve;
    }

    private static bool TryParameterAt(Curve3d curve, in Vector3d point, out double t, out double error)
    {
        var domain = curve.Domain;
        var parameters = FaceGeometry.ExactSampleParameters(curve, domain.Start, domain.End, 64);
        t = parameters[0];
        error = double.PositiveInfinity;
        foreach (double candidate in parameters)
        {
            double distance = curve.PointAt(candidate).DistanceTo(point);
            if (distance < error)
            {
                error = distance;
                t = candidate;
            }
        }
        if (!double.IsFinite(error))
            return false;

        // Refine by 1D Gauss-Newton on the exact derivative; a polyline's derivative is its
        // chord, which is the right answer for a sampled curve too.
        for (int iteration = 0; iteration < 32; iteration++)
        {
            var residual = curve.PointAt(domain.Clamp(t)) - point;
            var slope = curve.DerivativeAt(domain.Clamp(t));
            double denominator = slope.LengthSquared;
            // Scale-free near-underflow guard on a squared derivative, not a model tolerance.
            if (denominator < 1e-30)
                break;
            double next = domain.Clamp(t - slope.Dot(residual) / denominator);
            if (Math.Abs(next - t) <= domain.Length * 1e-15)
            {
                t = next;
                break;
            }
            t = next;
        }
        error = curve.PointAt(domain.Clamp(t)).DistanceTo(point);
        return double.IsFinite(error);
    }

    /// <summary>
    /// The candidate restricted to the corner-to-corner stretch. A whole-domain match keeps
    /// the carrier itself (a closed rim edge carries its own interval); otherwise the stretch
    /// becomes a <see cref="CurveSegment"/>, whose parameter IS the carrier's — the rule
    /// corner patches and revolve generators depend on.
    /// </summary>
    private static Curve3d TrimBetween(Curve3d candidate, double t0, double t1)
    {
        var domain = candidate.Domain;
        if (Math.Abs(t0 - domain.Start) <= domain.Length * 1e-12
            && Math.Abs(t1 - domain.End) <= domain.Length * 1e-12)
            return candidate;
        return new CurveSegment(candidate, t0, t1);
    }

    /// <summary>
    /// The largest distance from the curve's INTERIOR to either carrier — zero for an exact
    /// branch, the tracer's chord error for a sampled one. Endpoints are excluded on purpose:
    /// they are the solved corners, which are exact by construction.
    /// </summary>
    private static double InteriorDeviation(Curve3d curve, Surface a, Surface b)
    {
        if (!ImplicitSurface.TryBuild(a, out var fieldA, out _) || !ImplicitSurface.TryBuild(b, out var fieldB, out _))
            return double.NaN;
        var domain = curve.Domain;
        double deviation = 0;
        const int samples = 32;
        for (int i = 1; i < samples; i++)
        {
            var point = curve.PointAt(domain.ParameterAt((double)i / samples));
            deviation = Math.Max(deviation,
                Math.Max(Math.Abs(fieldA.Evaluate(point, out _)), Math.Abs(fieldB.Evaluate(point, out _))));
        }
        return deviation;
    }
}
