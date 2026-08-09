using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Interop;

/// <summary>
/// A boolean whose exact B-Rep result could not be closed into a two-manifold solid.
/// Distinct from the generic failures so callers that own a fallback route (the
/// <c>Shape</c> API's implicit lowering) can recognize it and say so.
/// </summary>
public sealed class BrepBooleanException(string message) : Exception(message);

/// <summary>
/// B-Rep boolean operations, orchestrating the whole pipeline: surface–surface
/// intersection per face pair, seam-aligned face splitting on both solids (each side
/// breaks its seam segments at the other side's crossings too, so tessellation welds),
/// fragment classification by probing the other solid's mesh SDF, and reassembly —
/// with subtracted-tool faces reversed. A hybrid-kernel operation by design: exact
/// B-Rep surfaces and curves, mesh-backed point classification.
///
/// v1 contract: input solids may intersect transversally or share coincident PLANAR faces
/// (<see cref="CoplanarFaces"/> — flush embossing, stacked plates, a pocket floor flush with
/// a face); coincident or tangent CURVED faces are refused by name. Inputs are consumed
/// (their faces are split in place). The result is sealed
/// both geometrically and topologically (<see cref="TopologyEditor.SealSeams"/>), and
/// every operation ENFORCES that before returning: a result that is not two-manifold
/// throws <see cref="BrepBooleanException"/> rather than handing back a solid that
/// tessellates open and exports an unprintable mesh.
/// </summary>
public static class BrepBoolean
{
    public static BrepSolid Union(BrepSolid a, BrepSolid b, Microsoft.Extensions.Logging.ILogger? logger = null) =>
        Execute(a, b, "Union", keepAOutside: true, keepBOutside: true, reverseB: false, difference: false, logger);

    public static BrepSolid Intersection(BrepSolid a, BrepSolid b, Microsoft.Extensions.Logging.ILogger? logger = null) =>
        Execute(a, b, "Intersection", keepAOutside: false, keepBOutside: false, reverseB: false, difference: false, logger);

    public static BrepSolid Difference(BrepSolid a, BrepSolid b, Microsoft.Extensions.Logging.ILogger? logger = null) =>
        Execute(a, b, "Difference", keepAOutside: true, keepBOutside: false, reverseB: true, difference: true, logger);

    private static BrepSolid Execute(
        BrepSolid a, BrepSolid b, string operation, bool keepAOutside, bool keepBOutside, bool reverseB,
        bool difference, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        // Timing is opt-in observation only: every finding of the operation itself stays
        // a return value or an exception (BrepBooleanException names its own facts).
        var stopwatch = logger is null ? null : System.Diagnostics.Stopwatch.StartNew();
        int facesA = a.Faces.Count(), facesB = b.Faces.Count();

        // Classification geometry is captured before any splitting mutates the inputs.
        var sdfA = new MeshSdf(BRepTessellator.Tessellate(a, logger: logger), logger: logger);
        var sdfB = new MeshSdf(BRepTessellator.Tessellate(b, logger: logger), logger: logger);
        var bounds = sdfA.Bounds.Union(sdfB.Bounds);
        var region = bounds.Expanded(bounds.Size[bounds.LongestAxis] * 0.1 + 0.1);
        // The marching tracer's own step (region extent / StepDivisor): how far short of a
        // bounded domain's edge a traced curve can stop, and therefore how far a snapped
        // landing may legitimately sit from the polyline's last vertex.
        double step = region.Size[region.LongestAxis] / 150.0;

        // Intersection curves per original face pair; each side records the other side's
        // crossing parameters as mandatory seam breaks.
        var curvesA = a.Faces.ToDictionary(f => f, _ => new List<(Curve3d Curve, IReadOnlyList<double> Breaks)>());
        var curvesB = b.Faces.ToDictionary(f => f, _ => new List<(Curve3d Curve, IReadOnlyList<double> Breaks)>());
        // Conservative prefilter expansion at the inverse-evaluation scale; a too-small
        // value could only skip a genuinely-touching face pair, never corrupt geometry.
        var boundsA = curvesA.Keys.ToDictionary(f => f, f => f.Bounds().Expanded(1e-6));
        var boundsB = curvesB.Keys.ToDictionary(f => f, f => f.Bounds().Expanded(1e-6));
        var planarA = CoplanarFaces.For(a);
        var planarB = CoplanarFaces.For(b);
        // Planes the two solids genuinely SHARE (same carrier, overlapping trims). Empty for
        // every transversal input, which is what keeps this tier inert for existing models.
        var sharedPlanes = new List<(Vector3d Origin, Vector3d Normal)>();
        var coplanarPairs = new List<(BrepFace A, BrepFace B)>();
        var pending = new List<(BrepFace A, BrepFace B, Curve3d Curve)>();
        foreach (var fa in curvesA.Keys)
        {
            foreach (var fb in curvesB.Keys)
            {
                // Carrier surfaces are unbounded (planes, cylinders): skip pairs whose
                // actual faces cannot meet, so spurious carrier intersections far from
                // either face never reach the splitter.
                if (!boundsA[fa].Intersects(boundsB[fb]))
                    continue;
                var curves = SurfaceIntersection.Intersect(fa.Surface, fb.Surface, region);
                if (curves.Count == 0)
                {
                    // Two surfaces whose bounds overlap and which cross in NO curve either
                    // miss each other or are coincident. Coincidence is the whole question
                    // here, and it is decided before any splitting so a refusal is a clean
                    // one (rim surgery's all-or-nothing rule, applied to booleans).
                    SurveyCoincidence(fa, fb, sharedPlanes, coplanarPairs, operation);
                    continue;
                }
                foreach (var curve in curves)
                    pending.Add((fa, fb, SnapTracerEnds(curve, fa, fb, step)));
            }
        }

        // Flush operands need two extra filters; both are gated on a shared plane actually
        // existing, so a purely transversal boolean distributes its curves exactly as before.
        bool flush = sharedPlanes.Count > 0;
        foreach (var (fa, fb, wholeCurve) in pending)
        {
            bool usable = !flush || MeetBeyondAPoint(fa, fb);
            if (!usable)
                continue;
            // The breakpoints are the SAME list for both sides, so wherever the two faces
            // genuinely share the curve they cut it at identical parameters and the seams pair.
            var cuts = ClipBreakpoints(wholeCurve, fa, fb);
            var piecesA = ClipToFace(wholeCurve, fa, fb, cuts);
            var piecesB = ClipToFace(wholeCurve, fb, fa, cuts);
            // The closed-curve seam anchor is only meaningful when BOTH sides still see the
            // closed curve — see SeamBreaks.
            bool anchorSeam = piecesA is [var onlyA] && ReferenceEquals(onlyA, wholeCurve)
                && piecesB is [var onlyB] && ReferenceEquals(onlyB, wholeCurve);
            foreach (var curve in piecesA)
            {
                // Identical carriers on repeated geometry (patterned faces sharing a plane)
                // produce the same curve once per pair; splitting a face twice along the same
                // curve breaks the arrangement tracer.
                if ((!flush || ReachesInterior(fa, curve)) &&
                    !curvesA[fa].Any(existing => SameCurve(existing.Curve, curve)))
                    curvesA[fa].Add((curve, SeamBreaks(curve, FaceSplitter.CrossingParameters(fb, curve), anchorSeam)));
            }
            foreach (var curve in piecesB)
            {
                if ((!flush || ReachesInterior(fb, curve)) &&
                    !curvesB[fb].Any(existing => SameCurve(existing.Curve, curve)))
                    curvesB[fb].Add((curve, SeamBreaks(curve, FaceSplitter.CrossingParameters(fa, curve), anchorSeam)));
            }
        }
        foreach (var (fa, fb) in coplanarPairs)
        {
            ImprintRim(curvesA[fa], fa, fb);
            ImprintRim(curvesB[fb], fb, fa);
        }

        // Disjoint or fully nested operands: no intersection curves anywhere. Classify
        // whole bodies and combine shells — a multi-shell result for disjoint unions,
        // a cavity (reversed inner shell) for a fully swallowed Difference tool.
        //
        // A shared plane disqualifies the fast path even when it leaves no curves. Two
        // stacked plates of the SAME footprint intersect only along their own boundary
        // edges, so every curve is dropped above and the operands look disjoint — they are
        // not: they share a whole face, and taking the fast path returns them as two
        // touching shells, which is precisely the fusion failure this tier exists to fix.
        if (sharedPlanes.Count == 0 && curvesA.Values.All(list => list.Count == 0))
        {
            bool aInside = sdfB.Evaluate(ProbePoint(a.Faces.First())) < 0;
            bool bInside = sdfA.Evaluate(ProbePoint(b.Faces.First())) < 0;
            var shells = new List<BrepShell>();
            if (keepAOutside ? !aInside : aInside)
                shells.AddRange(a.Shells);
            if (keepBOutside ? !bInside : bInside)
                shells.AddRange(reverseB ? b.Shells.Select(CloneReversedShell) : b.Shells);
            if (shells.Count == 0)
                throw new InvalidOperationException("Boolean result is empty.");
            var disjoint = Verified(new BrepSolid(shells), operation);
            if (logger is not null)
                KernelLog.BooleanCompleted(logger, operation, facesA, facesB,
                    disjoint.Faces.Count(), stopwatch!.Elapsed.TotalMilliseconds);
            return disjoint;
        }

        var kept = new List<BrepFace>();
        foreach (var fragment in SplitAll(curvesA))
        {
            var probe = ProbePoint(fragment);
            var coincidence = Coincident(fragment, probe, planarB, sharedPlanes);
            bool keep = coincidence == Coincidence.None
                ? (sdfB.Evaluate(probe) < 0) != keepAOutside
                : CoplanarFaces.Keep(coincidence, difference, fromA: true);
            if (keep)
                kept.Add(fragment);
        }
        foreach (var fragment in SplitAll(curvesB))
        {
            var probe = ProbePoint(fragment);
            var coincidence = Coincident(fragment, probe, planarA, sharedPlanes);
            bool keep = coincidence == Coincidence.None
                ? (sdfA.Evaluate(probe) < 0) != keepBOutside
                : CoplanarFaces.Keep(coincidence, difference, fromA: false);
            if (keep)
                kept.Add(reverseB ? ReverseFace(fragment) : fragment);
        }
        if (kept.Count == 0)
            throw new InvalidOperationException("Boolean result is empty.");
        TopologyEditor.SealSeams(kept);
        var verified = Verified(new BrepSolid([new BrepShell(kept)]), operation);
        if (logger is not null)
            KernelLog.BooleanCompleted(logger, operation, facesA, facesB,
                verified.Faces.Count(), stopwatch!.Elapsed.TotalMilliseconds);
        return verified;
    }

    /// <summary>
    /// Final acceptance test: the assembled result must be two-manifold — every edge used
    /// by exactly two coedges, every loop chaining end-to-start. An unclosed result is the
    /// project's worst failure mode: it tessellates into an open mesh with no complaint,
    /// exports an unprintable STL, and only surfaces when someone happens to call
    /// <see cref="BrepSolid.Validate"/>. Silence is not an option here — a boolean the
    /// exact kernel cannot close fails LOUDLY, naming the operation and where the crack is.
    /// </summary>
    private static BrepSolid Verified(BrepSolid result, string operation)
    {
        var uses = new Dictionary<BrepEdge, int>();
        foreach (var coedge in result.Coedges)
            uses[coedge.Edge] = uses.GetValueOrDefault(coedge.Edge) + 1;
        var unpaired = uses.Where(entry => entry.Value != 2).Select(entry => entry.Key).ToList();
        if (unpaired.Count > 0)
        {
            var sample = unpaired[0];
            var detail = string.Join("; ", unpaired.Take(8).Select(e =>
                $"{e.Curve.GetType().Name}[{uses[e]} use(s)] " +
                $"{e.Curve.PointAt(e.Domain.Start)}->{e.Curve.PointAt(e.Domain.End)}"));
            throw new BrepBooleanException(
                $"B-Rep {operation} produced an unclosed solid: {unpaired.Count} of {uses.Count} edges are " +
                $"used by {string.Join('/', unpaired.Select(e => uses[e]).Distinct().Order())} face(s) instead " +
                $"of 2, so the result has cracks (one runs through {sample.Curve.PointAt(sample.Domain.Mid)}). " +
                "Coplanar PLANAR face pairs are supported (flush embossing, stacked plates, a pocket floor " +
                "flush with a face); the usual remaining causes are coincident or tangent CURVED faces — a " +
                "shaft in a bore of its own diameter, a band tangent to a wall — or intersection curves that " +
                "do not close into loops. Returning this solid would " +
                $"tessellate into an open mesh with no error, so it fails here instead. Unpaired: {detail}.");
        }

        foreach (var loop in result.Loops)
        {
            var coedges = loop.Coedges;
            for (int i = 0; i < coedges.Count; i++)
            {
                if (!ReferenceEquals(coedges[i].EndVertex, coedges[(i + 1) % coedges.Count].StartVertex))
                    throw new BrepBooleanException(
                        $"B-Rep {operation} produced a face loop whose coedges do not chain end-to-start " +
                        $"(near {coedges[i].EndVertex.Position}) — the assembled result is not a valid solid.");
            }
        }
        return result;
    }

    /// <summary>
    /// Decides whether a face pair that produced no intersection curve is COINCIDENT, and
    /// records the plane when it is. Two planar faces sharing a carrier whose trims genuinely
    /// overlap are the supported case; coincident CURVED surface is refused here, by name,
    /// rather than left to fail as an unclosed result three stages later.
    /// <para>Overlap is probed from BOTH sides because either face may be the smaller one: an
    /// emboss's underside sits inside its host's top face, while a host's pocket floor sits
    /// inside the tool's. Two stacked plates' side walls are coplanar and meet along a single
    /// line — neither probe lands inside the other, and they correctly stay ordinary
    /// transversal faces.</para>
    /// </summary>
    private static void SurveyCoincidence(
        BrepFace fa, BrepFace fb, List<(Vector3d Origin, Vector3d Normal)> sharedPlanes,
        List<(BrepFace A, BrepFace B)> coplanarPairs, string operation)
    {
        bool planarA = fa.IsPlanar(out var originA, out var rawA);
        bool planarB = fb.IsPlanar(out var originB, out var rawB);
        if (planarA && planarB)
        {
            if (!rawA.TryNormalize(Tolerance.Default, out var na) ||
                !rawB.TryNormalize(Tolerance.Default, out var nb) ||
                !CoplanarFaces.SamePlane(originA, na, originB, nb))
                return;
            if (!OverlapInArea(fa, fb))
                return; // coplanar carriers, disjoint trims — nothing is shared
            if (!sharedPlanes.Any(p => CoplanarFaces.SamePlane(p.Origin, p.Normal, originA, na)))
                sharedPlanes.Add((originA, na));
            coplanarPairs.Add((fa, fb));
            return;
        }

        // Coaxial equal-radius cylinders are the coincident-curved case that actually occurs
        // (a shaft pressed into its own bore, a flange band tangent to a sheet). Named here
        // because the alternative is a crack along the whole contact patch, reported by
        // Verified as "unclosed" with no hint of which pair caused it.
        if (fa.IsCylindrical(out var axisOriginA, out var axisA, out double radiusA) &&
            fb.IsCylindrical(out var axisOriginB, out var axisB, out double radiusB) &&
            axisA.IsParallelTo(axisB, Tolerance.Default) &&
            Math.Abs(radiusA - radiusB) <= FaceGeometry.SeamTolerance)
        {
            var offset = axisOriginB - axisOriginA;
            var separation = offset - axisA * offset.Dot(axisA);
            if (separation.Length <= FaceGeometry.SeamTolerance)
            {
                throw new BrepBooleanException(
                    $"B-Rep {operation} has coincident CYLINDRICAL faces: two radius-{radiusA:G6} " +
                    $"bands share the axis through {axisOriginA} along {axisA}. The v1 exact boolean " +
                    "handles coincident PLANAR faces (flush embossing, stacked plates, a pocket floor " +
                    "flush with a face) but not coincident or tangent curved surface — deciding the " +
                    "shared region's rim there needs surface–surface re-intersection the kernel does " +
                    "not have. Offset one operand by a working clearance, or take the implicit route.");
            }
        }
    }

    /// <summary>
    /// Extends a marching-tracer polyline so it ENDS on the face boundary it was heading for.
    ///
    /// <para><b>Why the tracer cannot do this itself.</b> It breaks its step only AFTER the
    /// corrector's parameters leave the domain, so a traced curve always stops up to one march
    /// step short of a bounded surface's edge. Where that edge is also the boundary of the face
    /// being split, the polyline never crosses it, <c>FaceSplitter</c> finds ZERO crossings, and
    /// the face is whole-classified — which is how a bore crossing a whole-solid fillet's band
    /// cracked the result along entire tangency edges. Measured on
    /// <c>FilletAllEdges(20x14x8, r2) − Ø6 cylinder</c>: the four band curves ended 5.5e-5 and
    /// 1.1e-2 away from the band's two tangency lines and produced no crossings at all.</para>
    ///
    /// <para><b>The landing is solved exactly, not extrapolated.</b> The boundary edge and the
    /// other solid's carrier are both exact geometry, so E(t) = S(u, v) is a well-posed 3x3
    /// Newton system seeded from the polyline's own last vertex — which already lies on S, so
    /// the seed's (u, v) is exact and only t moves. Extrapolating the chord instead would land
    /// a sagitta off the surface, which is the error this exists to remove.</para>
    ///
    /// <para><b>Both solids get the same endpoint by construction</b>, because the polyline is
    /// ONE object shared by the two faces' curve lists and is snapped once, here, before either
    /// side splits. The alternative — snapping per face during splitting — would give the two
    /// sides endpoints differing by the sagitta, opening a pinhole at every crossing.</para>
    /// </summary>
    private static Curve3d SnapTracerEnds(Curve3d curve, BrepFace fa, BrepFace fb, double step)
    {
        if (curve is not PolylineCurve3d polyline || polyline.IsClosed)
            return curve;
        var points = polyline.Points.ToList();
        if (points.Count < 2)
            return curve;
        bool changed = false;
        if (TryBoundaryLanding(points[0], points[0] - points[1], fa, fb, step, out var atStart))
        {
            points.Insert(0, atStart);
            changed = true;
        }
        if (TryBoundaryLanding(points[^1], points[^1] - points[^2], fa, fb, step, out var atEnd))
        {
            points.Add(atEnd);
            changed = true;
        }
        return changed ? new PolylineCurve3d(points, false, polyline.Carriers) : curve;
    }

    /// <summary>
    /// Where the traced curve would have met one of the two faces' boundaries, or false when it
    /// already ends there (within the weld tier) or no boundary is within reach. Candidates are
    /// ranked by distance from <paramref name="end"/>, so an end near two boundaries takes the
    /// nearer — the one the trace was actually leaving through.
    ///
    /// <para><b>A candidate BEHIND the trace is refused</b>, and the refusal is not a nicety:
    /// on a thread band the two rails are a fraction of a millimetre apart, well inside the
    /// reach, so a curve that already terminates exactly on one rail is within reach of the
    /// OTHER — and appending that lands the polyline back where it started, doubling it over
    /// itself. Measured on an M8 crest flat cross-drilled Ø2: the curve came back with domain
    /// [0, 0.479] whose second half retraced the first, so face splitting found its crossings at
    /// 0 and 0.240 and refused the terminus at 0.479. The trace's own last chord says which way
    /// it was going, and a landing it was moving AWAY from is not the one it was heading for.
    /// The "already there" test alone cannot cover this: it reads the MINIMUM over candidates,
    /// so it is silent whenever the true landing's own Newton misses (a rail helix seeded from
    /// 32 samples over five turns can converge to another root entirely).</para>
    /// </summary>
    private static bool TryBoundaryLanding(
        in Vector3d end, in Vector3d heading, BrepFace fa, BrepFace fb, double step, out Vector3d landing)
    {
        landing = default;
        double best = double.PositiveInfinity;
        // Two march steps of slack: one for the step the tracer breaks after, one for the
        // corrector's own overshoot. Beyond that the "landing" would be a different feature.
        double reach = 2 * step;
        foreach (var (face, other) in (ReadOnlySpan<(BrepFace, BrepFace)>)[(fa, fb), (fb, fa)])
        {
            foreach (var loop in face.Loops)
            {
                foreach (var coedge in loop.Coedges)
                {
                    if (!TryEdgeSurfaceCrossing(coedge.Edge, other.Surface, end, out var point))
                        continue;
                    double distance = point.DistanceTo(end);
                    // Exact-zero sign test on a dot product, never a tolerance: what is being
                    // asked is which SIDE of the terminus the candidate lies on.
                    if ((point - end).Dot(heading) <= 0)
                        continue;
                    if (distance < best && distance <= reach)
                    {
                        best = distance;
                        landing = point;
                    }
                }
            }
        }
        // Already on the boundary: appending a weld-scale chord would only add a degenerate
        // sampling segment for every downstream consumer.
        return best <= reach && best > Tolerance.Default.Linear;
    }

    /// <summary>
    /// Solves E(t) = S(u, v) — an edge curve meeting a surface — by 3x3 Newton from the seed
    /// nearest <paramref name="near"/>. The seed's (u, v) comes from projecting
    /// <paramref name="near"/> onto S, which is exact because a traced point lies ON S.
    /// </summary>
    private static bool TryEdgeSurfaceCrossing(
        BrepEdge edge, Surface surface, in Vector3d near, out Vector3d point)
    {
        point = default;
        if (!surface.TryProjectPoint(near, out var uv, FaceGeometry.InverseEvaluationTolerance))
            return false;

        // Seed t at the sampled point of the edge closest to the traced end.
        var domain = edge.Domain;
        var parameters = FaceGeometry.ExactSampleParameters(edge.Curve, domain.Start, domain.End, 32);
        double t = parameters[0], bestDistance = double.PositiveInfinity;
        foreach (double candidate in parameters)
        {
            double distance = edge.Curve.PointAt(candidate).DistanceTo(near);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                t = candidate;
            }
        }

        double u = uv.X, v = uv.Y;
        for (int iteration = 0; iteration < 24; iteration++)
        {
            var residual = edge.Curve.PointAt(domain.Clamp(t)) - surface.PointAt(u, v);
            // Two decades below the weld tier, as RefineCrossing's own test is: the landing
            // becomes a vertex position, so it must not carry a weld-scale error itself.
            if (residual.Length < 1e-11)
            {
                point = edge.Curve.PointAt(domain.Clamp(t));
                return true;
            }
            var dt = edge.Curve.DerivativeAt(domain.Clamp(t));
            double hu = Math.Max(1e-8, surface.DomainU.Length * 1e-7);
            double hv = Math.Max(1e-8, surface.DomainV.Length * 1e-7);
            if (!double.IsFinite(hu) || !double.IsFinite(hv))
                hu = hv = 1e-7;
            var du = (surface.PointAt(u + hu, v) - surface.PointAt(u - hu, v)) / (2 * hu);
            var dv = (surface.PointAt(u, v + hv) - surface.PointAt(u, v - hv)) / (2 * hv);
            if (!Solve3(dt, -du, -dv, -residual, out double stepT, out double stepU, out double stepV))
                return false;
            t = domain.Clamp(t + stepT);
            u += stepU;
            v += stepV;
        }
        return false;
    }

    /// <summary>Cramer's rule for the 3x3 column system [c0 c1 c2]·x = rhs.</summary>
    private static bool Solve3(
        in Vector3d c0, in Vector3d c1, in Vector3d c2, in Vector3d rhs,
        out double x0, out double x1, out double x2)
    {
        x0 = x1 = x2 = 0;
        double determinant = c0.Dot(c1.Cross(c2));
        // Relative degeneracy guard (the scale-free tier): the determinant is a VOLUME, so an
        // absolute floor would fail cubically with model scale.
        double scale = c0.Length * c1.Length * c2.Length;
        if (scale <= 0 || Math.Abs(determinant) <= 1e-12 * scale)
            return false;
        x0 = rhs.Dot(c1.Cross(c2)) / determinant;
        x1 = c0.Dot(rhs.Cross(c2)) / determinant;
        x2 = c0.Dot(c1.Cross(rhs)) / determinant;
        return true;
    }

    /// <summary>
    /// Imprints the shared region's rim onto a coplanar face, using the PARTNER face's own
    /// boundary curves — which is what the rim is: the line where the other solid's boundary
    /// leaves the shared plane.
    ///
    /// <para><b>Why the transversal path alone is not enough here.</b> The mesh boolean gets
    /// this rim for free because a coplanar patch's neighbour is a transverse pair. In B-Rep
    /// it usually arrives the same way — a <c>MakeBox</c> boss's wall is an unbounded
    /// <c>PlaneSurface</c> and plane∩plane duly returns the rim line — but a SKETCH extrusion's
    /// wall is a bounded patch, and <c>TryPlaneExtrudedSection</c> deliberately reports NO
    /// section when the cutting plane is flush with the generator's rim (splitting the wall
    /// there would fabricate zero-extent slivers). Embossed text is exactly that case: the
    /// glyph's underside was dropped as coincident while the plate's top face was never
    /// imprinted, and the result cracked along every letter's outline.</para>
    ///
    /// <para>Curves already covering a rim edge are left alone rather than doubled — splitting
    /// a face twice along the same geometry breaks the arrangement tracer — and rim edges that
    /// never reach this face's interior are skipped, so a partial overlap adds nothing the
    /// transversal path did not already supply.</para>
    ///
    /// <para>Taking the partner's OWN curves is also the best possible weld: the new edges are
    /// built on the very geometry the other solid already references, so
    /// <c>SealSeams</c> pairs them by construction rather than by tolerance.</para>
    /// </summary>
    private static void ImprintRim(
        List<(Curve3d Curve, IReadOnlyList<double> Breaks)> curves, BrepFace face, BrepFace partner)
    {
        foreach (var loop in partner.Loops)
        {
            foreach (var coedge in loop.Coedges)
            {
                var edge = coedge.Edge;
                var rim = new CurveSegment(edge.Curve, edge.Domain.Start, edge.Domain.End);
                if (!ReachesInterior(face, rim))
                    continue;
                if (curves.Any(existing => Covers(existing.Curve, rim)))
                    continue;
                // A rim is a CurveSegment and never closed, so the anchor never applies.
                curves.Add((rim, SeamBreaks(rim, FaceSplitter.CrossingParameters(partner, rim), anchored: true)));
            }
        }
    }

    /// <summary>Every exact sample of <paramref name="rim"/> lies on <paramref name="existing"/>
    /// (measured against its sampled polyline), i.e. the face is already being split there.</summary>
    private static bool Covers(Curve3d existing, Curve3d rim)
    {
        var parameters = FaceGeometry.ExactSampleParameters(
            existing, existing.Domain.Start, existing.Domain.End, 24);
        foreach (double t in FaceGeometry.ExactSampleParameters(rim, rim.Domain.Start, rim.Domain.End, 8))
        {
            var p = rim.PointAt(t);
            double best = double.PositiveInfinity;
            var previous = existing.PointAt(parameters[0]);
            for (int i = 1; i < parameters.Count; i++)
            {
                var next = existing.PointAt(parameters[i]);
                best = Math.Min(best, FaceGeometry.DistanceToSegment(p, previous, next));
                previous = next;
            }
            if (best > FaceGeometry.SeamTolerance)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Whether two coplanar faces' trims genuinely overlap in AREA, sampled on a grid over
    /// the box their bounds share. Every sample lies on the common plane by construction, so
    /// the only question is the trims.
    ///
    /// <para><b>Centroids are not enough, and that is not a detail.</b> The first version asked
    /// whether either face's probe point fell inside the other, which is true for an emboss
    /// (its underside sits inside the host's top) but FALSE for two plates that overlap in a
    /// strip — neither centroid is in the shared band, the coincidence went unseen, and the
    /// whole flush tier stayed switched off for the case that most needs it.</para>
    ///
    /// <para>Sampling can still miss an overlap thinner than the grid. That direction is safe:
    /// missing it leaves the boolean on its pre-existing path, which fails loudly rather than
    /// producing wrong geometry.</para>
    /// </summary>
    private static bool OverlapInArea(BrepFace fa, BrepFace fb)
    {
        var box = fa.Bounds().Intersection(fb.Bounds());
        if (box.IsEmpty)
            return false;
        var samplesX = AxisSamples(box.Min.X, box.Max.X);
        var samplesY = AxisSamples(box.Min.Y, box.Max.Y);
        var samplesZ = AxisSamples(box.Min.Z, box.Max.Z);
        foreach (double x in samplesX)
        {
            foreach (double y in samplesY)
            {
                foreach (double z in samplesZ)
                {
                    var p = new Vector3d(x, y, z);
                    if (FaceGeometry.Contains(fa, p) && FaceGeometry.Contains(fb, p))
                        return true;
                }
            }
        }
        return false;

        // A flat axis contributes its single value; the coplanar box is flat in one of them.
        static double[] AxisSamples(double min, double max)
        {
            const int steps = 8;
            if (max - min <= FaceGeometry.SeamTolerance)
                return [(min + max) / 2];
            var values = new double[steps - 1];
            for (int i = 1; i < steps; i++)
                values[i - 1] = min + (max - min) * i / steps;
            return values;
        }
    }

    /// <summary>
    /// Whether two faces can share more than a single POINT, read off their bounds. A pair
    /// whose bounds meet in one point can touch at most there, and a point cannot divide
    /// either face, so the carrier curve through it is noise — <c>BrepQueries.Bounds</c> is
    /// deliberately conservative-over, which is exactly what makes this sound.
    ///
    /// <para>Flush operands are full of such pairs: butting a boss against a plate puts the
    /// boss's side wall corner-to-corner with the plate's, and the two carrier planes still
    /// cross in a full line that runs clean through the plate's wall. The line is real; the
    /// contact is not. On transversal input the same pairs are simply far apart, which is why
    /// the boolean got this far without the rule.</para>
    /// </summary>
    private static bool MeetBeyondAPoint(BrepFace fa, BrepFace fb)
    {
        var overlap = fa.Bounds().Intersection(fb.Bounds());
        if (overlap.IsEmpty)
            return false;
        var size = overlap.Size;
        return Math.Max(size.X, Math.Max(size.Y, size.Z)) > FaceGeometry.SeamTolerance;
    }

    /// <summary>
    /// Curve parameters at which a face-pair intersection curve may be clipped: the domain ends
    /// plus every parameter at which the curve crosses EITHER face's boundary.
    ///
    /// <para>Computed once per face pair and shared by both sides' clips, which is what keeps
    /// the two solids welding: wherever the pair genuinely shares a stretch, both cut it at
    /// identical parameters, so the edges built on it have identical endpoints. The parameters
    /// are the exact crossings the splitter itself computes (Newton-refined against the
    /// boundary edges), never sampled guesses — a clipped end becomes a vertex, so it must not
    /// carry a sampling error.</para>
    /// </summary>
    private static List<double> ClipBreakpoints(Curve3d curve, BrepFace fa, BrepFace fb)
    {
        var domain = curve.Domain;
        var breaks = new List<double> { domain.Start, domain.End };
        breaks.AddRange(FaceSplitter.CrossingParameters(fa, curve));
        breaks.AddRange(FaceSplitter.CrossingParameters(fb, curve));
        breaks.Sort();
        // Relative, because a curve parameter carries no model units.
        double epsilon = ClipEpsilon(domain);
        for (int i = breaks.Count - 1; i > 0; i--)
        {
            if (breaks[i] - breaks[i - 1] < epsilon || breaks[i] < domain.Start || breaks[i] > domain.End)
                breaks.RemoveAt(i);
        }
        return breaks;
    }

    private static double ClipEpsilon(in Interval domain) => Math.Max(1e-12, domain.Length * 1e-9);

    /// <summary>
    /// The piece(s) of a face-pair intersection curve that <paramref name="face"/> should be
    /// split along — everything except the stretches that lie inside <paramref name="face"/>
    /// but OUTSIDE its <paramref name="partner"/>.
    ///
    /// <para><b>Why the curve needs clipping at all.</b> <see cref="SurfaceIntersection"/>
    /// intersects CARRIERS, and a carrier is unbounded (a plane) or bounded only by its own
    /// parameter rectangle (a helical band's domain is the bounding rectangle of a
    /// parallelogram-shaped face). The curve it returns therefore runs past both faces. Each
    /// face's splitter already discards the stretches outside ITSELF — but nothing discarded the
    /// stretches outside the OTHER face, so a face was split along geometry the pair does not
    /// actually share: a chamfer cone's cut ran past a threaded rod's cap and arrived at the
    /// cone face as a dangling edge no arrangement can trace, and four wall lines of a pocket
    /// tool cut a host face into 9 fragments where the tool's footprint asks for 2.</para>
    ///
    /// <para><b>The rule is asymmetric, and that is the whole design.</b> The symmetric form —
    /// hand both faces the stretches inside both trims — is wrong wherever the two faces share
    /// a boundary: clipping to the partner then cuts the curve exactly ON this face's own
    /// boundary, turning a transversal crossing into a tangential touch, and the arrangement is
    /// left with an endpoint that no boundary edge owns. Measured on
    /// <c>Box(20,20,10) &amp; Box(10,30,10)</c>, whose side walls meet along their full height:
    /// every vertical curve stopped exactly on both walls' rims and tracing did not close.
    /// Keeping the stretches that lie outside THIS face costs nothing — the splitter drops them
    /// by loop parity anyway — and it restores the crossing. So the clip removes exactly the
    /// over-split it exists to remove, and nothing else.</para>
    ///
    /// <para><b>A curve that survives whole is returned as ITSELF</b>, not as a segment covering
    /// its whole domain — a closed curve must stay closed, since the wrap-splitting and
    /// hole-splitting paths both key on <see cref="Curve3d.IsClosed"/>. Every transversal
    /// boolean whose curves lie inside both trims (a bore circle on a cap, a rim line between
    /// two boxes) therefore gets bit-for-bit what it got before.</para>
    ///
    /// <para><b>The keep/drop test errs toward KEEPING</b> (<see cref="InsideForClip"/>): a
    /// stretch wrongly kept only reproduces the un-clipped behaviour, while one wrongly dropped
    /// loses a seam silently and the boolean returns two touching shells.</para>
    /// </summary>
    private static List<Curve3d> ClipToFace(
        Curve3d curve, BrepFace face, BrepFace partner, List<double> breaks)
    {
        var domain = curve.Domain;
        if (breaks.Count < 2)
            return [curve];
        double epsilon = ClipEpsilon(domain);

        var kept = new List<(double S0, double S1)>();
        for (int i = 0; i + 1 < breaks.Count; i++)
        {
            double s0 = breaks[i], s1 = breaks[i + 1];
            var probe = curve.PointAt(FaceGeometry.InteriorSampleParameter(curve, s0, s1));
            if (InsideForClip(face, probe) && !InsideForClip(partner, probe))
                continue;
            if (kept.Count > 0 && kept[^1].S1 >= s0 - epsilon)
                kept[^1] = (kept[^1].S0, s1);
            else
                kept.Add((s0, s1));
        }
        if (kept.Count == 0)
            return [];
        if (kept.Count == 1 && kept[0].S0 <= domain.Start + epsilon && kept[0].S1 >= domain.End - epsilon)
            return [curve];
        // A closed curve's surviving stretches join across the seam: the piece ending at the
        // domain end continues into the one starting at the domain start, and cutting there
        // would leave two edges where the geometry has one.
        if (curve.IsClosed && kept.Count > 1 &&
            kept[0].S0 <= domain.Start + epsilon && kept[^1].S1 >= domain.End - epsilon)
        {
            var wrapped = (kept[^1].S0, kept[0].S1 + domain.Length);
            kept.RemoveAt(kept.Count - 1);
            kept[0] = wrapped;
        }
        return [.. kept.Select(k => (Curve3d)new CurveSegment(curve, k.S0, k.S1))];
    }

    /// <summary>
    /// The clip's containment test, in three parts — all three of which err toward INSIDE,
    /// because a stretch wrongly kept only reproduces the un-clipped behaviour while one
    /// wrongly dropped loses a seam and returns two touching shells.
    ///
    /// <para><b>A point that does not project onto the face's surface at all counts as
    /// inside.</b> A probe can fail to project for reasons that say nothing about the trim — a
    /// tracer polyline is on its surface only at the vertices, and a stretch spanning none has
    /// no exact interior sample.</para>
    ///
    /// <para><b>Parity is two-sided</b> (<see cref="FaceGeometry.ContainsTwoSided"/>), so a
    /// pole-bounded face answers instead of calling every point on itself outside. Without it a
    /// sphere-through-a-box union loses its whole seam curve and comes back at Euler 4.</para>
    ///
    /// <para><b>A stretch running ALONG the face's boundary counts as inside.</b> Parity there
    /// is decided by rounding, and the geometry is exactly the case the clip must not touch:
    /// where two solids mate, the shared rim IS a face boundary on one side. Measured on
    /// <c>Box(20,20,10) &amp; Box(10,30,10)</c>, whose side walls meet the shared top plane
    /// along their own top edges — dropped, the arrangement had nothing to close with.</para>
    /// </summary>
    private static bool InsideForClip(BrepFace face, in Vector3d probe) =>
        !face.Surface.TryProjectPoint(probe, out _, FaceGeometry.InverseEvaluationTolerance) ||
        FaceGeometry.ContainsTwoSided(face, probe) ||
        FaceGeometry.DistanceToBoundary(face, probe) <= FaceGeometry.SeamTolerance;

    /// <summary>
    /// Whether any exact sample of the curve lies STRICTLY inside the face — on its surface,
    /// clear of its boundary by the seam tier, and inside its trim. Boundary proximity is
    /// tested first because <see cref="FaceGeometry.Contains"/>' ray parity is decided by
    /// rounding for a point sitting on the boundary itself.
    /// </summary>
    private static bool ReachesInterior(BrepFace face, Curve3d curve)
    {
        foreach (double t in FaceGeometry.ExactSampleParameters(curve, curve.Domain.Start, curve.Domain.End, 16))
        {
            var p = curve.PointAt(t);
            if (!face.Surface.TryProjectPoint(p, out _, FaceGeometry.InverseEvaluationTolerance))
                continue; // off this face's surface entirely
            if (FaceGeometry.DistanceToBoundary(face, p) > FaceGeometry.SeamTolerance &&
                FaceGeometry.Contains(face, p))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether a split fragment lies on the other solid's coincident planar surface. Only
    /// planar fragments can qualify, and only when a shared plane was surveyed — so this is a
    /// no-op for transversal booleans.
    /// </summary>
    private static Coincidence Coincident(
        BrepFace fragment, in Vector3d probe, CoplanarFaces other,
        List<(Vector3d Origin, Vector3d Normal)> sharedPlanes)
    {
        if (sharedPlanes.Count == 0)
            return Coincidence.None;
        if (CoplanarFaces.OutwardNormal(fragment) is not { } normal)
            return Coincidence.None;
        return other.Classify(probe, normal);
    }

    private static IEnumerable<BrepFace> SplitAll(
        Dictionary<BrepFace, List<(Curve3d Curve, IReadOnlyList<double> Breaks)>> curvesPerFace)
    {
        foreach (var (face, curves) in curvesPerFace)
        {
            var rest = ExtractInteriorChains(face, curves, out var chains);
            var fragments = new List<BrepFace> { face };
            foreach (var chain in chains)
            {
                fragments = fragments.SelectMany(f =>
                {
                    // With several chains (multiple threaded holes on one face), each
                    // chain splits only the fragment that contains it.
                    if (!FaceGeometry.Contains(f, chain[0].PointAt(chain[0].Domain.Mid)))
                        return (IEnumerable<BrepFace>)[f];
                    var split = FaceSplitter.SplitByClosedCurveChain(f, chain);
                    return [split.FaceWithHole, split.Disk!];
                }).ToList();
            }
            // The whole remaining curve list goes to the face at once. FaceSplitter decides
            // between the curve-at-a-time cascade (what this loop used to be, and still what
            // every curve that crosses the face boundary at both ends gets) and one
            // simultaneous arrangement, which is the only way to place curves that TERMINATE
            // inside the face — see FaceSplitter.SplitByCurves.
            var entries = rest.Select(r => new SplitCurve(r.Curve, r.Breaks)).ToList();
            fragments = fragments.SelectMany(f => FaceSplitter.SplitByCurves(f, entries)).ToList();
            foreach (var fragment in fragments)
                yield return fragment;
        }
    }

    /// <summary>
    /// Pulls out open curves whose endpoints all lie strictly INSIDE the face and pair
    /// end-to-end into closed cycles — a threaded tool's cap-cut chain on the drilled
    /// plane (one spiral arc per helical band, junctions on the shared rails). Such
    /// curves cannot go through <see cref="FaceSplitter.SplitByCurve"/> (open cuts must
    /// cross the boundary); each complete cycle splits as one hole+disk chain instead.
    /// Anything that fails to close stays in the returned list, where SplitByCurve
    /// reports it loudly. Curves not on this face's surface never qualify (containment
    /// projection fails), so all previously supported configurations are untouched.
    /// </summary>
    private static List<(Curve3d Curve, IReadOnlyList<double> Breaks)> ExtractInteriorChains(
        BrepFace face,
        List<(Curve3d Curve, IReadOnlyList<double> Breaks)> curves,
        out List<List<Curve3d>> chains)
    {
        chains = [];
        var rest = new List<(Curve3d Curve, IReadOnlyList<double> Breaks)>();
        var candidates = new List<(Curve3d Curve, IReadOnlyList<double> Breaks)>();
        foreach (var entry in curves)
        {
            var curve = entry.Curve;
            if (!curve.IsClosed
                && FaceGeometry.Contains(face, curve.PointAt(curve.Domain.Start))
                && FaceGeometry.Contains(face, curve.PointAt(curve.Domain.End)))
                candidates.Add(entry);
            else
                rest.Add(entry);
        }

        // Chain junctions match at the inverse-evaluation scale (1e-6): endpoints come
        // from independent tracer/analytic curves, not exactly-shared vertices.
        const double junctionTolerance = 1e-6;
        while (candidates.Count > 0)
        {
            var seed = candidates[0];
            candidates.RemoveAt(0);
            var chain = new List<(Curve3d Curve, IReadOnlyList<double> Breaks)> { seed };
            var start = seed.Curve.PointAt(seed.Curve.Domain.Start);
            var tail = seed.Curve.PointAt(seed.Curve.Domain.End);
            bool closed = false;
            while (!closed)
            {
                if (tail.DistanceTo(start) < junctionTolerance && chain.Count >= 2)
                {
                    closed = true;
                    break;
                }
                int next = candidates.FindIndex(c =>
                    c.Curve.PointAt(c.Curve.Domain.Start).DistanceTo(tail) < junctionTolerance ||
                    c.Curve.PointAt(c.Curve.Domain.End).DistanceTo(tail) < junctionTolerance);
                if (next < 0)
                    break; // dead end — hand the seed back for the loud path
                var link = candidates[next];
                candidates.RemoveAt(next);
                chain.Add(link);
                tail = link.Curve.PointAt(link.Curve.Domain.Start).DistanceTo(tail) < junctionTolerance
                    ? link.Curve.PointAt(link.Curve.Domain.End)
                    : link.Curve.PointAt(link.Curve.Domain.Start);
            }
            var closedChain = closed ? chain.Select(c => c.Curve).ToList() : null;
            // A chain that WRAPS the surface's period bounds a band, not a hole: the chain path
            // would make a face-with-hole plus a disk, and neither side of a wrapping cut is a
            // hole in the other. Hand it back for the arrangement, which pairs wrapping loops
            // bottom-to-top by v. See FaceSplitter.ChainWrapsPeriod for how carrier clipping
            // started producing these.
            if (closedChain is not null && FaceSplitter.ChainWrapsPeriod(face, closedChain))
            {
                rest.AddRange(chain);
                continue;
            }
            if (closedChain is not null)
            {
                chains.Add(closedChain);
            }
            else
            {
                rest.Add(seed);
                // Links consumed by the failed walk go back too.
                rest.AddRange(chain.Skip(1));
            }
        }
        return rest;
    }

    /// <summary>
    /// A properly reversed face: the outward normal flips (IsReversed) and every loop is
    /// re-wound (reversed coedge order and senses), so seam edges end up traversed
    /// oppositely by the two faces meeting there — keeping the result two-manifold.
    /// </summary>
    private static BrepFace ReverseFace(BrepFace face)
    {
        var loops = face.Loops
            .Select(l => new BrepLoop([.. l.Coedges.Reverse().Select(c => new BrepCoedge(c.Edge, !c.SameSense))]))
            .ToList();
        // Re-wound, but the same surface of the same construction step: provenance is
        // inherited so a subtracted tool's wall still names the step that cut it.
        return new BrepFace(face.Surface, loops, isReversed: !face.IsReversed).DescendsFrom(face);
    }

    /// <summary>
    /// A reversed deep copy of a shell for the disjoint fast path. Unlike
    /// <see cref="ReverseFace"/> on split fragments (whose edges are freshly built and
    /// later cleaned by SealSeams), reversing intact faces in place would add duplicate
    /// coedge uses to the original edges — so edges are cloned.
    /// </summary>
    private static BrepShell CloneReversedShell(BrepShell shell)
    {
        var edgeClones = new Dictionary<BrepEdge, BrepEdge>();
        BrepEdge Clone(BrepEdge edge)
        {
            if (!edgeClones.TryGetValue(edge, out var clone))
                edgeClones[edge] = clone = new BrepEdge(edge.Curve, edge.Domain, edge.StartVertex, edge.EndVertex);
            return clone;
        }
        var faces = shell.Faces.Select(f => new BrepFace(
            f.Surface,
            [.. f.Loops.Select(l => new BrepLoop(
                [.. l.Coedges.Reverse().Select(c => new BrepCoedge(Clone(c.Edge), !c.SameSense))]))],
            !f.IsReversed).DescendsFrom(f)).ToList();
        return new BrepShell(faces);
    }

    /// <summary>
    /// Closed intersection curves get a mandatory break at their domain start on both
    /// sides: a side that wrap-splits a band along the curve anchors its closed seam
    /// edge's vertex there, so the side that instead cuts the curve into arc segments
    /// must subdivide at the same point or the seam edges cannot pair up.
    ///
    /// <para><paramref name="anchored"/> is false when the CLIP left one side with open
    /// pieces instead of the closed curve: nothing anchors at the domain start any more, and
    /// adding the break then splits an arc the other side keeps whole. Measured on a slot
    /// through a bore — the bore wall shares with the slot's floor only the arc inside the
    /// slot's width, so it is cut there and never wrap-splits, while the floor still sees the
    /// full circle; the stale anchor left the +x arc as two edges against the wall's one, six
    /// unpaired edges in all.</para>
    /// </summary>
    private static IReadOnlyList<double> SeamBreaks(
        Curve3d curve, IReadOnlyList<double> crossings, bool anchored) =>
        curve.IsClosed && anchored ? [.. crossings, curve.Domain.Start] : crossings;

    private static bool SameCurve(Curve3d a, Curve3d b)
    {
        // Weld-scale (1e-9 = Tolerance.Default.Linear) identity: duplicate split curves
        // from identical carriers are exact clones, so the tight tolerance is safe.
        const double tolerance = 1e-9;
        var a0 = a.PointAt(a.Domain.Start);
        var a1 = a.PointAt(a.Domain.End);
        var b0 = b.PointAt(b.Domain.Start);
        var b1 = b.PointAt(b.Domain.End);
        bool forward = a0.DistanceTo(b0) < tolerance && a1.DistanceTo(b1) < tolerance;
        bool backward = a0.DistanceTo(b1) < tolerance && a1.DistanceTo(b0) < tolerance;
        return (forward || backward)
            && a.PointAt(a.Domain.Mid).DistanceTo(b.PointAt(b.Domain.Mid)) < tolerance;
    }

    /// <summary>A point strictly interior to the face, for inside/outside classification.</summary>
    internal static Vector3d ProbePoint(BrepFace face)
    {
        var loops = FaceGeometry.PullLoops(face);

        // Planar-style faces: centroids of the outer loop's triangles. A loop that WRAPS the
        // surface period bounds a band, not a planar region — projection jitter gives such
        // loops a tiny nonzero area, and triangulating the sliver would put the probe on the
        // fragment boundary. Wrapping is net u DRIFT, never u SPAN: a contractible facet may
        // reach three quarters of the way round and come back (see
        // FaceGeometry.LoopWrapsPeriod), and the band path below would then probe halfway to
        // the surface's own domain edge — outside the fragment, classifying it away.
        var outer = loops[0];
        double periodU = FaceGeometry.PeriodU(face.Surface);
        bool wrapsU = FaceGeometry.LoopWrapsPeriod(outer, periodU);
        if (!wrapsU && Math.Abs(FaceGeometry.LoopSignedArea(outer)) > 1e-12)
        {
            // Largest triangles first: a sliver's centroid hugs the fragment boundary,
            // and near a boundary lying on the other solid's curved surface the
            // classification SDF is only sagitta-accurate — probing there flips signs.
            foreach (var (i0, i1, i2) in PolygonTriangulator.Triangulate(outer)
                .OrderByDescending(t => Math.Abs((outer[t.B] - outer[t.A]).Cross(outer[t.C] - outer[t.A]))))
            {
                var uv = (outer[i0] + outer[i1] + outer[i2]) / 3;
                var p = face.Surface.PointAt(uv.X, uv.Y);
                if (FaceGeometry.Contains(face, p))
                    return p;
            }
        }

        // Band-style faces (loops at constant v wrap the period, pulled area ~0).
        double u = loops.SelectMany(l => l).Average(p => p.X);
        double v = loops.Select(l => l.Average(p => p.Y)).Average();
        if (loops.Count == 1 && wrapsU)
        {
            // Pole-bounded faces (discs of axis-touching revolves) have only their rim
            // loop; averaging would probe ON the rim. Move halfway toward the pole —
            // and skip the parity check, which is unnecessary rather than merely
            // awkward here: a SINGLE loop that wraps the period separates the pole from
            // everything else, and this face is the pole's side, so every v strictly
            // between the pole and the loop is inside AT EVERY u.
            //
            // That is why the loop is measured by its CLOSEST APPROACH to the pole and
            // not by its average. The two agree exactly when the loop sits at one v —
            // every pole cap bounded by nothing but its own rim — and part company as
            // soon as another solid CUTS the cap, which leaves the loop wrapping but no
            // longer level. Measured on a drill tool's flat bottom breaking out of a
            // plate's top face: the average put the probe 0.106 ABOVE that plate's own
            // top plane, i.e. in the fragment on the far side of the cut, so the piece
            // that should have been kept was classified away and the solid cracked along
            // its whole rim (3 of 19 edges used once). Note that the two-sided parity is
            // NOT the fix here — it errs toward inside by design, and duly accepts that
            // very point.
            var domainV = face.Surface.DomainV;
            double far = Math.Abs(v - domainV.Start) > Math.Abs(domainV.End - v) ? domainV.Start : domainV.End;
            if (double.IsFinite(far))
            {
                double nearest = outer[0].Y;
                foreach (var p in outer)
                {
                    if (Math.Abs(p.Y - far) < Math.Abs(nearest - far))
                        nearest = p.Y;
                }
                return face.Surface.PointAt(u, (nearest + far) / 2);
            }
        }
        var mid = face.Surface.PointAt(u, v);
        if (FaceGeometry.Contains(face, mid))
            return mid;

        // Last resort — band fragments with bites (a hemisphere band minus several
        // bulges) have no useful uv centroid: search a coarse grid over the pulled
        // loops' uv extents and keep the containing sample farthest from the loops,
        // where the classification SDF is most trustworthy.
        double uMin = loops.SelectMany(l => l).Min(p => p.X), uMax = loops.SelectMany(l => l).Max(p => p.X);
        double vMin = loops.SelectMany(l => l).Min(p => p.Y), vMax = loops.SelectMany(l => l).Max(p => p.Y);
        var loopPoints = loops.SelectMany(l => l).Select(p => face.Surface.PointAt(p.X, p.Y)).ToList();
        Vector3d? best = null;
        double bestClearance = 0;
        const int grid = 12;
        for (int i = 1; i < grid; i++)
        {
            for (int j = 1; j < grid; j++)
            {
                double gu = uMin + (uMax - uMin) * i / grid;
                double gv = vMin + (vMax - vMin) * j / grid;
                var candidate = face.Surface.PointAt(gu, gv);
                if (!FaceGeometry.Contains(face, candidate))
                    continue;
                double clearance = loopPoints.Min(p => p.DistanceSquaredTo(candidate));
                if (clearance > bestClearance)
                {
                    bestClearance = clearance;
                    best = candidate;
                }
            }
        }
        if (best is { } found)
            return found;

        // Thin fragments: STEP OFF THE BOUNDARY the fragment already has, rather than
        // hunting for it. A uniform uv grid is a statement about the bounding BOX, and a
        // fragment that is thin anywhere can slip between its samples however isotropic
        // the box is — the recorded "a sampling grid in parameter space says nothing about
        // coverage" lesson, here about a region's SHAPE rather than a band's aspect.
        // Measured on a bore grazing a plate's top face (half-chord 0.35 of a Ø6 bore): the
        // discarded wall fragment is an L, a 0.23 rad wedge joined to a 0.048-tall ring,
        // and the 12x12 grid's 0.63 x 0.083 step lands in neither, so the whole boolean
        // refused for want of one point on a face it was about to throw away.
        //
        // The loops ARE the region's own resolution, so a point just inside an edge is
        // inside the region whenever the region is thicker than the step — and stepping
        // perpendicular in uv reaches one side or the other with no orientation convention
        // to get wrong (a reversed face's loops wind the other way; both signs are tried).
        // The ladder shrinks geometrically so an arbitrarily thin fragment is still
        // reached, and the widest clearance still wins, so this only ever probes close to a
        // boundary when there is nowhere else to stand.
        foreach (double fraction in (ReadOnlySpan<double>)[0.25, 0.0625, 0.015625, 1.0 / 256])
        {
            Vector3d? inset = null;
            double insetClearance = 0;
            foreach (var loop in loops)
            {
                for (int i = 0; i < loop.Count; i++)
                {
                    var from = loop[i];
                    var to = loop[(i + 1) % loop.Count];
                    var along = to - from;
                    double length = along.Length;
                    if (!(length > 0))
                        continue;
                    // The perpendicular's magnitude IS the edge's, so one multiply steps a
                    // fraction of the local edge length — the region's own scale there.
                    var offset = new Vector2d(-along.Y, along.X) * fraction;
                    foreach (double sign in (ReadOnlySpan<double>)[1, -1])
                    {
                        var uv = (from + to) / 2 + offset * sign;
                        var candidate = face.Surface.PointAt(uv.X, uv.Y);
                        if (!FaceGeometry.Contains(face, candidate))
                            continue;
                        double clearance = loopPoints.Min(p => p.DistanceSquaredTo(candidate));
                        if (clearance > insetClearance)
                        {
                            insetClearance = clearance;
                            inset = candidate;
                        }
                    }
                }
            }
            if (inset is { } stepped)
                return stepped;
        }

        // Name the fragment (the refusal culture): which carrier, where it sits, and the
        // uv shape the probe search saw — without these the failure reads the same for
        // every face of every boolean.
        throw new InvalidOperationException(
            $"Could not find a probe point on a face fragment: {face.Surface.GetType().Name}" +
            $"{(face.IsReversed ? " (reversed)" : "")} with {loops.Count} loop(s), pulled uv " +
            $"u [{uMin:G6}, {uMax:G6}] x v [{vMin:G6}, {vMax:G6}], anchored at " +
            $"{face.Surface.PointAt(uMin, vMin)}; neither a grid sample nor a step off the " +
            "fragment's own boundary landed inside its trim.");
    }
}
