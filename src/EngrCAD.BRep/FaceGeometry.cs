using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// Parameter-space geometry on faces: pulling 3D curves back into a surface's (u, v)
/// space and classifying points against a face's loops. These are the building blocks
/// face splitting and (future) B-Rep booleans operate on.
/// </summary>
public static class FaceGeometry
{
    /// <summary>
    /// Distance tolerance for inverse evaluation (<see cref="Surface.TryProjectPoint"/>)
    /// when pulling on-surface points back to parameters. Deliberately three decades
    /// looser than the 1e-9 weld tolerance: Gauss-Newton projection on curved surfaces
    /// carries ~1e-7 residual, and tracer polylines are on-surface only at their vertices
    /// (see the numerical notes in CLAUDE.md). Do not tighten toward the weld tolerance.
    /// </summary>
    public const double InverseEvaluationTolerance = 1e-6;

    /// <summary>
    /// The epsilon ladder's <b>seam tier</b>: the distance at which geometry constructed
    /// INDEPENDENTLY on the two sides of one shared curve may still be considered
    /// coincident. Two independent constructions (a tracer polyline vs an analytic
    /// carrier, a projected sketch point vs a rebuilt junction, a trimmed loop sample vs
    /// the surface's natural grid) agree only to ~1e-7, so the 1e-9 absolute weld
    /// tolerance would reject genuinely-shared geometry here. Two decades tighter than
    /// <see cref="InverseEvaluationTolerance"/>, two decades looser than the weld tier.
    /// <para><b>Boolean-critical</b>: <see cref="TopologyEditor.SealSeams"/> and the
    /// tessellator's full-domain boundary match both key on this value. Geometry that is
    /// built EXACTLY on both sides (tessellation welds, mandatory seam breaks, clone
    /// dedupe) must stay on the 1e-9 weld tier — do not promote those sites here.</para>
    /// </summary>
    public const double SeamTolerance = 1e-7;

    /// <summary>The u-period of surfaces that close on themselves in u; 0 when aperiodic.</summary>
    public static double PeriodU(Surface surface) => surface switch
    {
        CylinderSurface or SphereSurface => 2 * Math.PI,
        RevolvedSurface r when r.IsFullTurn => 2 * Math.PI,
        ExtrudedSurface e when e.Generator.IsClosed => e.DomainU.Length,
        TwistedSurface t when t.Generator.IsClosed => t.DomainU.Length,
        SweptSurface s when s.Generator.IsClosed => s.DomainU.Length,
        LoftedSurface l when l.IsClosedU => l.DomainU.Length,
        _ => 0,
    };

    /// <summary>
    /// Samples a 3D curve lying on a surface and pulls it into parameter space, unwrapping
    /// the periodic u direction so the polyline is continuous (u may leave the primary
    /// period). Throws when a sample does not lie on the surface. Marching-tracer
    /// polylines (<see cref="PolylineCurve3d"/>) lie on the surface only at their
    /// vertices (chordal between), so they are sampled at exactly those.
    /// </summary>
    public static List<Vector2d> PullCurve(Curve3d curve, Surface surface, int samples = 64)
    {
        List<Vector3d> samplePoints;
        if (curve is PolylineCurve3d polyline)
        {
            var vertices = polyline.Points;
            int vertexCount = polyline.IsClosed ? vertices.Count - 1 : vertices.Count;
            samplePoints = [.. vertices.Take(vertexCount)];
        }
        else
        {
            int count = curve.IsClosed ? samples : samples + 1;
            samplePoints = new List<Vector3d>(count);
            for (int i = 0; i < count; i++)
                samplePoints.Add(curve.PointAt(curve.Domain.ParameterAt((double)i / samples)));
        }

        var result = new List<Vector2d>(samplePoints.Count);
        double period = PeriodU(surface);
        foreach (var p in samplePoints)
        {
            if (!surface.TryProjectPoint(p, out var uv, InverseEvaluationTolerance))
                throw new ArgumentException($"Curve point {p} does not lie on the surface.");
            if (period > 0 && result.Count > 0)
            {
                double previous = result[^1].X;
                double u = uv.X;
                u += period * Math.Round((previous - u) / period);
                uv = new Vector2d(u, uv.Y);
            }
            result.Add(uv);
        }
        return result;
    }

    /// <summary>
    /// Curve parameters (ascending, spanning [start, end] inclusive) at which the curve
    /// evaluates exactly on its carrier surface. Marching-tracer polylines are exact only
    /// at their VERTICES — between them the polyline is chordal, a sagitta off the
    /// surface, so inverse evaluation rejects mid-chord samples — hence polyline-backed
    /// curves (raw, or wrapped in a reparameterizing <see cref="CurveSegment"/>) sample
    /// at vertex parameters; everything else samples uniformly.
    /// </summary>
    /// <remarks>
    /// <b>This is the single rule</b> — public so the tessellator, the face splitter's
    /// tightest-turn probes and pullback all ask the same question instead of patching
    /// the sagitta per call site, which is how the cross-drilled-bore defect got in.
    /// <see cref="IsPolylineBacked"/> says whether a curve needs it.
    /// </remarks>
    /// <summary>
    /// Whether <paramref name="curve"/> is a marching-tracer polyline (raw, or wrapped in
    /// a reparameterizing <see cref="CurveSegment"/>) — i.e. exact only at its vertices,
    /// so anything sampling it must go through
    /// <see cref="ExactSampleParameters"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT a test on <see cref="Curve3d.Underlying"/>: a segment's underlying
    /// curve is its base's, which says nothing about the parameter mapping this rule turns
    /// on — and `Underlying` is a type hint, never a source of position.
    /// </remarks>
    public static bool IsPolylineBacked(Curve3d curve) =>
        curve is PolylineCurve3d or CurveSegment { Base: PolylineCurve3d };

    public static List<double> ExactSampleParameters(Curve3d curve, double start, double end, int uniformSamples)
    {
        var result = new List<double> { start };
        double interiorEpsilon = Math.Max(1e-12, Math.Abs(end - start) * 1e-9);
        void AddInterior(double t)
        {
            if (t > start + interiorEpsilon && t < end - interiorEpsilon)
                result.Add(t);
        }
        switch (curve)
        {
            case PolylineCurve3d polyline:
                foreach (double t in polyline.VertexParameters)
                    AddInterior(t);
                break;
            case CurveSegment { Base: PolylineCurve3d basePolyline } segment:
            {
                // Segment parameter t maps to base parameter s = s0 + (s1 − s0)·t, with
                // s wrapping past a closed base's domain end (CurveSegment wraps too).
                double s0 = segment.BaseStart;
                double length = segment.BaseEnd - segment.BaseStart;
                double baseLength = basePolyline.Domain.Length;
                foreach (double s in basePolyline.VertexParameters)
                {
                    AddInterior((s - s0) / length);
                    if (basePolyline.IsClosed)
                        AddInterior((s + baseLength - s0) / length);
                }
                result.Sort();
                break;
            }
            default:
                for (int i = 1; i < uniformSamples; i++)
                    result.Add(start + (end - start) * i / uniformSamples);
                break;
        }
        result.Add(end);

        // Near-duplicate parameters (a vertex within epsilon of an endpoint) would make
        // degenerate sampling segments downstream.
        for (int i = result.Count - 1; i > 0; i--)
        {
            if (result[i] - result[i - 1] < interiorEpsilon)
                result.RemoveAt(i == result.Count - 1 ? i - 1 : i);
        }
        return result;
    }

    /// <summary>
    /// Whether a pulled-back loop <b>wraps</b> the surface's periodic direction — i.e. it is a
    /// band boundary (a bore wall's ring, a cut running right round a cylinder) rather than the
    /// boundary of a contractible region on the same surface.
    /// </summary>
    /// <remarks>
    /// <para><b>The decision is the net u DRIFT over the traversal, not the u SPAN.</b> A loop
    /// that reaches far round the period and comes back is contractible however far it reached:
    /// the chamfer facet on a threaded rod's end cone spans <b>272°</b> and closes with a net
    /// drift of 0.02 rad. A span test calls that a band boundary, and every consumer then does
    /// something structurally wrong with it — <c>BrepBoolean.ProbePoint</c> walks halfway to the
    /// surface's own domain edge and lands outside the fragment entirely (so the facet is
    /// classified away), <c>FaceSplitter.TraceFaces</c> files it for bottom-to-top band pairing,
    /// and <c>SplitByCurve</c> lets a wrapping cut fabricate a phantom band out of it. A genuine
    /// wrap drifts a full period; a contractible loop returns to where it started.</para>
    /// <para>The span survives as the cheap first half of an AND: it rejects almost every loop
    /// without touching the endpoints, and no loop can drift a period without spanning one. Both
    /// halves read the polyline <see cref="PullLoops"/> produces — traversal-ordered, unwrapped
    /// stepwise, and with each coedge's final sample dropped as the junction with the next — so
    /// a full wrap's drift is a period less one sampling step, comfortably past the half-period
    /// threshold at any usable sample count.</para>
    /// </remarks>
    public static bool LoopWrapsPeriod(IReadOnlyList<Vector2d> pulledLoop, double period)
    {
        if (period <= 0 || pulledLoop.Count < 2)
            return false;
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var p in pulledLoop)
        {
            min = Math.Min(min, p.X);
            max = Math.Max(max, p.X);
        }
        if (max - min <= 0.75 * period)
            return false;
        return Math.Abs(pulledLoop[^1].X - pulledLoop[0].X) > 0.5 * period;
    }

    /// <summary>
    /// A parameter strictly inside [<paramref name="s0"/>, <paramref name="s1"/>] at which the
    /// curve is genuinely ON its surface — used wherever a stretch of a curve has to be asked
    /// "are you inside this face?".
    ///
    /// <para><b>The arithmetic midpoint is wrong for a tracer polyline.</b> A polyline is exact
    /// only at its VERTICES, so a mid-chord point sits a sagitta off the surface — measured
    /// 5e-3 on a whole-solid fillet's quarter-arc band at the tracer's own sample density, five
    /// thousand times the 1e-6 inverse-evaluation tolerance. The projection then FAILS and the
    /// stretch is silently discarded as "leaves the surface entirely". A stretch that spans no
    /// vertex has no exact interior sample and keeps the midpoint, and non-polyline curves keep
    /// it bit-for-bit: an analytic curve is exact everywhere, so there is nothing to fix and no
    /// reason to move an existing probe.</para>
    /// </summary>
    public static double InteriorSampleParameter(Curve3d curve, double s0, double s1)
    {
        double midpoint = (s0 + s1) / 2;
        if (!IsPolylineBacked(curve))
            return midpoint;
        var exact = ExactSampleParameters(curve, s0, s1, 2);
        return exact.Count > 2 ? exact[exact.Count / 2] : midpoint;
    }

    /// <summary>Signed area of a pulled-back closed polyline; positive = counter-clockwise in (u, v).</summary>
    public static double LoopSignedArea(IReadOnlyList<Vector2d> loop)
    {
        double area = 0;
        for (int i = 0; i < loop.Count; i++)
        {
            var p = loop[i];
            var q = loop[(i + 1) % loop.Count];
            area += p.Cross(q);
        }
        return area * 0.5;
    }

    /// <summary>Pulls each loop of a face into parameter space (coedge senses respected).</summary>
    public static List<List<Vector2d>> PullLoops(BrepFace face, int samplesPerCurve = 32)
    {
        var loops = new List<List<Vector2d>>();
        double period = PeriodU(face.Surface);
        foreach (var loop in face.Loops)
        {
            var points = new List<Vector2d>();
            foreach (var coedge in loop.Coedges)
            {
                var domain = coedge.Edge.Domain;
                var parameters = ExactSampleParameters(coedge.Edge.Curve, domain.Start, domain.End, samplesPerCurve);
                // The traversal-final sample is skipped: for open edges it is the
                // junction shared with the next coedge, for closed edges the duplicate
                // of the traversal start.
                for (int i = 0; i < parameters.Count - 1; i++)
                {
                    double t = coedge.SameSense ? parameters[i] : parameters[parameters.Count - 1 - i];
                    var p = coedge.Edge.Curve.PointAt(t);
                    if (!face.Surface.TryProjectPoint(p, out var uv, InverseEvaluationTolerance))
                        throw new InvalidOperationException($"Loop point {p} does not lie on the face surface.");
                    if (period > 0 && points.Count > 0)
                    {
                        double u = uv.X + period * Math.Round((points[^1].X - uv.X) / period);
                        uv = new Vector2d(u, uv.Y);
                    }
                    points.Add(uv);
                }
            }
            loops.Add(points);
        }
        return loops;
    }

    /// <summary>
    /// Distance from a point to a face's boundary, measured against the boundary's sampled
    /// POLYLINE rather than against its samples — comparing against isolated sample points
    /// would call a point sitting exactly on a straight edge "interior" whenever it happened
    /// to fall between two of them (measured: a boss wall's own bottom rim read 0.125 away at
    /// 32 samples, so the degenerate split it was meant to suppress went ahead anyway).
    /// <para>For a CURVED edge the chords lie inside the arc, so a point on the boundary can
    /// read up to a sagitta away — which errs toward calling it interior, the conservative
    /// direction for both callers (keeping a boolean's rim curve, and refusing a splitting
    /// curve that terminates in open space).</para>
    /// </summary>
    /// <remarks>
    /// One rule, asked by <c>BrepBoolean.ReachesInterior</c> and by <c>FaceSplitter</c>'s
    /// "does this curve terminate inside the face?" gate. Restating it per call site is how
    /// three separate sagitta defects got in (see <see cref="ExactSampleParameters"/>).
    /// </remarks>
    public static double DistanceToBoundary(BrepFace face, in Vector3d point, int boundarySamples = 32)
    {
        double best = double.PositiveInfinity;
        foreach (var loop in face.Loops)
        {
            foreach (var coedge in loop.Coedges)
            {
                var edge = coedge.Edge;
                var parameters = ExactSampleParameters(
                    edge.Curve, edge.Domain.Start, edge.Domain.End, boundarySamples);
                var previous = edge.Curve.PointAt(parameters[0]);
                for (int i = 1; i < parameters.Count; i++)
                {
                    var next = edge.Curve.PointAt(parameters[i]);
                    best = Math.Min(best, DistanceToSegment(point, previous, next));
                    previous = next;
                }
            }
        }
        return best;
    }

    /// <summary>Distance from a point to a line segment.</summary>
    public static double DistanceToSegment(in Vector3d p, in Vector3d a, in Vector3d b)
    {
        var ab = b - a;
        double lengthSquared = ab.LengthSquared;
        // Exact-zero guard on a division, not a model tolerance: a degenerate sampling
        // segment collapses to its own endpoint.
        if (lengthSquared <= 0)
            return p.DistanceTo(a);
        double t = Math.Clamp((p - a).Dot(ab) / lengthSquared, 0, 1);
        return p.DistanceTo(a + ab * t);
    }

    /// <summary>
    /// Whether a 3D point lies within a face (inside the outer loop, outside the holes),
    /// by parity of an upward-v ray against all pulled-back loops. Periodic u is handled
    /// by shifting each segment into the test point's period.
    /// <para><b>One-sided, and that is a real blind spot</b>: an upward ray cannot see a rim
    /// that lies BELOW the probe, so this calls every point of a pole-bounded face outside
    /// whenever the pole sits at v = max (<see cref="ParityRayPointsDown"/> is the test for
    /// that, and the face splitter's own arrangement asks it). Callers for whom that is the
    /// wrong error — anything deciding what to KEEP — should ask
    /// <see cref="ContainsTwoSided"/>.</para>
    /// <para><b>This spelling deliberately does NOT choose its side</b>, though the rule to
    /// do so exists beside it. Making it pole-aware was built and measured, and it changes
    /// which structures a boolean produces rather than merely which points read as inside: a
    /// sphere's cap then accepts an interior bore circle as a HOLE, where the incumbent
    /// reading sends the same cut down the band path — and a pole-bounded face with a hole
    /// is a trimmed tier that does not exist, so the tessellator refuses the result by name.
    /// Correcting it therefore waits on that tier, and until then the upward ray is
    /// load-bearing rather than merely incumbent.</para>
    /// </summary>
    public static bool Contains(BrepFace face, in Vector3d point, int samplesPerCurve = 32) =>
        CountCrossings(face, point, samplesPerCurve, out int above, out _) && (above & 1) == 1;

    /// <summary>
    /// Whether a one-sided parity ray must be cast DOWNWARD in v — true exactly when the
    /// surface's v = max boundary is a degenerate POINT (a pole) and its v = min boundary is
    /// not, so the closed side of the trim is below the probe rather than above it.
    ///
    /// <para>Which way this breaks is decided by the GENERATOR'S DIRECTION and by nothing
    /// else. A profile leaving the axis puts the rim at v = max, so the upward ray crosses
    /// it and every drill tool's cap has always behaved; one returning to the axis puts the
    /// rim at v = min, the upward ray crosses nothing, and the identical cap reads as having
    /// no interior at all. Measured on a cylinder built as ONE full-turn revolve, whose two
    /// caps differ in exactly that: <see cref="FaceSplitter.SplitByCurve"/> returned a
    /// single fragment for 56 of 112 (offset, azimuth, curve-kind) chords, and they were
    /// precisely the 56 on the cap whose generator ends on the axis.</para>
    ///
    /// <para>The test is on the SURFACE rather than on the loops, because it is the domain
    /// that does or does not close; and it is asked by <see cref="Contains"/> and by the
    /// face splitter's own parity alike, so the two cannot disagree about where a point
    /// is — which they did, briefly, and the disagreement surfaced as a closed bore curve
    /// being routed into a face that then refused to accept it.</para>
    /// </summary>
    public static bool ParityRayPointsDown(Surface surface) =>
        IsDegenerateAt(surface, surface.DomainV.End) && !IsDegenerateAt(surface, surface.DomainV.Start);

    /// <summary>Whether a whole v = const boundary of the surface collapses to one point.</summary>
    private static bool IsDegenerateAt(Surface surface, double v)
    {
        if (!double.IsFinite(v))
            return false;
        var domainU = surface.DomainU;
        if (!double.IsFinite(domainU.Start) || !double.IsFinite(domainU.End))
            return false;
        var first = surface.PointAt(domainU.Start, v);
        for (int i = 1; i <= 4; i++)
        {
            // Weld tier: a pole is CONSTRUCTED exactly on the axis, so anything looser
            // would call a hair-thin rim degenerate.
            if (surface.PointAt(domainU.ParameterAt(i / 4.0), v).DistanceTo(first) > Tolerance.Default.Linear)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Whether a 3D point lies within a face, deciding a POLE-BOUNDED face correctly: the same
    /// even-odd parity as <see cref="Contains"/>, run in BOTH v directions.
    ///
    /// <para>A vertical line crosses a closed trim an even number of times in total, so on a
    /// properly closed face the two directions AGREE and this is exactly <see cref="Contains"/>.
    /// They disagree in one situation only — one side of the parameter domain is a degenerate
    /// POINT rather than a rim (a sphere's or an axis-touching revolve's pole), so a probe
    /// between the rim and the pole has a crossing on one side and none on the other. The probe
    /// is then inside, which is why the disagreement resolves to true.</para>
    ///
    /// <para>The tie-break therefore also makes this the spelling that ERRS TOWARD INSIDE, which
    /// is what a keep-or-drop decision wants: keeping a stretch that should have gone only
    /// reproduces the un-clipped behaviour, while dropping one that should have stayed loses a
    /// seam silently and the boolean returns two touching shells.</para>
    /// </summary>
    public static bool ContainsTwoSided(BrepFace face, in Vector3d point, int samplesPerCurve = 32)
    {
        if (!CountCrossings(face, point, samplesPerCurve, out int above, out int below))
            return false;
        bool aboveOdd = (above & 1) == 1, belowOdd = (below & 1) == 1;
        return aboveOdd == belowOdd ? aboveOdd : true;
    }

    /// <summary>
    /// Crossings of the two v rays from the point's parameters against every pulled-back loop,
    /// or false when the point does not lie on the face's surface at all. Periodic u is handled
    /// by shifting each segment into the test point's period.
    /// </summary>
    private static bool CountCrossings(
        BrepFace face, in Vector3d point, int samplesPerCurve, out int above, out int below)
    {
        above = below = 0;
        if (!face.Surface.TryProjectPoint(point, out var uv, InverseEvaluationTolerance))
            return false;

        double period = PeriodU(face.Surface);
        foreach (var loop in PullLoops(face, samplesPerCurve))
        {
            for (int i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                if (period > 0)
                {
                    // The wrap-around segment connects points stored a period apart —
                    // first make the segment itself compact, then shift it into the test
                    // point's period.
                    b = new Vector2d(b.X + period * Math.Round((a.X - b.X) / period), b.Y);
                    double mid = (a.X + b.X) / 2;
                    double shift = period * Math.Round((uv.X - mid) / period);
                    a = new Vector2d(a.X + shift, a.Y);
                    b = new Vector2d(b.X + shift, b.Y);
                }
                bool straddles = a.X <= uv.X != b.X <= uv.X;
                if (!straddles)
                    continue;
                double t = (uv.X - a.X) / (b.X - a.X);
                if (a.Y + t * (b.Y - a.Y) > uv.Y)
                    above++;
                else
                    below++;
            }
        }
        return true;
    }
}
