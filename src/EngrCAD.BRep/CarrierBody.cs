using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// Offsetting a solid whose faces are not all planes. The shape of the algorithm is the
/// polyhedral one — offset every carrier, re-solve every vertex, rebuild every edge, carry
/// the topology over verbatim — with the three-plane solve generalized to
/// <see cref="SurfaceCorner"/> and the straight edge generalized to a constructed conic.
///
/// <para><b>Why this is a separate class rather than a generalization in place.</b> The
/// polyhedral path is exact, fast, and its output is pinned bit for bit by tests that have
/// caught real regressions. Routing it through a solver that happens to reduce to the same
/// arithmetic would be a claim, not a guarantee; keeping the paths apart makes the
/// guarantee structural. <see cref="IsCurved"/> is the switch, and it is a property of the
/// input, not of the request.</para>
///
/// <para><b>New rim edges are CONSTRUCTED and then VERIFIED, never intersected and
/// trusted.</b> A rim circle built from its own axis and phase is exact and lands on the
/// offset surfaces' tessellation grids by construction; the same circle recovered from a
/// surface–surface intersection would be right in the cases the analytic tier covers and
/// silently chordal in the cases it does not (a partial revolve's cap rim falls straight
/// through to the marching tracer). So the circle is built, then every sample is measured
/// against BOTH carriers, and a rim the construction cannot reproduce is refused by name
/// rather than approximated.</para>
/// </summary>
internal sealed class CarrierBody
{
    // Verification tier for a constructed rim: the 1e-9 absolute weld tolerance, scaled by
    // the feature's own size, because a rim built exactly must sit on its carriers exactly.
    private static double VerificationTolerance => Tolerance.Default.Linear;

    public required BrepSolid Solid { get; init; }
    public required BrepFace[] Faces { get; init; }
    public required BrepVertex[] Vertices { get; init; }
    public required BrepEdge[] Edges { get; init; }
    public required Dictionary<BrepFace, int> FaceIndex { get; init; }
    public required Dictionary<BrepVertex, int> VertexIndex { get; init; }
    public required Dictionary<BrepEdge, int> EdgeIndex { get; init; }

    /// <summary>Distinct faces meeting at each vertex, in first-use order.</summary>
    public required int[][] VertexFaces { get; init; }

    /// <summary>The two faces sharing each edge.</summary>
    public required (int A, int B)[] EdgeFaces { get; init; }

    public required (int Start, int End)[] EdgeVertices { get; init; }

    /// <summary>True when any face is not a plane, so the curved path applies.</summary>
    public static bool IsCurved(BrepSolid solid)
    {
        ArgumentNullException.ThrowIfNull(solid);
        foreach (var face in solid.Faces)
        {
            if (!face.IsPlanar(out _, out _))
                return true;
        }
        return false;
    }

    public static CarrierBody Recognize(BrepSolid solid) => Recognize(solid, allowReversedFaces: false);

    /// <summary>
    /// The recognized body. <paramref name="allowReversedFaces"/> is the OFFSET path's escape
    /// hatch: an OFFSET moves a face along its OUTWARD normal, which <see cref="Lift"/> spells
    /// exactly for a reversed face (a difference marks the subtracted tool's walls
    /// <see cref="BrepFace.IsReversed"/>), so a bore this kernel cut can be offset. A TAPER
    /// (<see cref="Draft"/>) and a SHELL do not, because their carrier construction and cavity
    /// rules are not yet sense-aware, so they keep the refusal by asking with the flag off.
    /// </summary>
    public static CarrierBody Recognize(BrepSolid solid, bool allowReversedFaces)
    {
        if (solid.Shells.Count != 1)
            throw new NotSupportedException(
                $"Offsetting needs a single-shell solid; this one has {solid.Shells.Count} shells.");
        solid.Validate();

        var faces = solid.Faces.ToArray();
        var faceIndex = new Dictionary<BrepFace, int>(faces.Length);
        for (int f = 0; f < faces.Length; f++)
        {
            if (faces[f].IsReversed && !allowReversedFaces)
                throw new NotSupportedException(
                    "Offsetting needs forward-oriented faces; a reversed face's outward normal is the " +
                    "reverse of its surface's, so its offset direction is ambiguous.");
            faceIndex[faces[f]] = f;
        }

        var vertices = solid.Vertices.ToArray();
        var vertexIndex = new Dictionary<BrepVertex, int>(vertices.Length);
        for (int i = 0; i < vertices.Length; i++)
            vertexIndex[vertices[i]] = i;

        var edges = solid.Edges.ToArray();
        var edgeIndex = new Dictionary<BrepEdge, int>(edges.Length);
        var edgeVertices = new (int, int)[edges.Length];
        for (int e = 0; e < edges.Length; e++)
        {
            edgeIndex[edges[e]] = e;
            edgeVertices[e] = (vertexIndex[edges[e].StartVertex], vertexIndex[edges[e].EndVertex]);
        }

        // First-use order rather than a set: the corner solve's seed and its residual are
        // deterministic only if the carrier list is.
        var incident = new List<int>[vertices.Length];
        for (int i = 0; i < incident.Length; i++)
            incident[i] = [];
        var sharing = new List<int>[edges.Length];
        for (int e = 0; e < sharing.Length; e++)
            sharing[e] = [];

        foreach (var coedge in solid.Coedges)
        {
            int f = faceIndex[coedge.Loop.Face];
            foreach (int v in (ReadOnlySpan<int>)
                     [vertexIndex[coedge.StartVertex], vertexIndex[coedge.EndVertex]])
            {
                if (!incident[v].Contains(f))
                    incident[v].Add(f);
            }
            if (!sharing[edgeIndex[coedge.Edge]].Contains(f))
                sharing[edgeIndex[coedge.Edge]].Add(f);
        }

        var edgeFaces = new (int, int)[edges.Length];
        for (int e = 0; e < edges.Length; e++)
        {
            if (sharing[e].Count != 2)
                throw new NotSupportedException(
                    "Offsetting needs each edge to separate two distinct faces (no seam edges).");
            edgeFaces[e] = (sharing[e][0], sharing[e][1]);
        }

        return new CarrierBody
        {
            Solid = solid,
            Faces = faces,
            Vertices = vertices,
            Edges = edges,
            FaceIndex = faceIndex,
            VertexIndex = vertexIndex,
            EdgeIndex = edgeIndex,
            VertexFaces = [.. incident.Select(list => list.ToArray())],
            EdgeFaces = edgeFaces,
            EdgeVertices = edgeVertices,
        };
    }

    public BrepSolid Offset(double distance) => Offset(_ => distance);

    /// <summary>
    /// Offsetting with a per-face distance — the curved twin of
    /// <see cref="Shelling.Offset(BrepSolid, Func{BrepFace, double})"/>. A face whose law
    /// returns zero keeps its carrier VERBATIM (<see cref="SurfaceOffset.TryOffset"/> returns
    /// the very same object), so a selective offset re-solves only the corners it disturbs and
    /// leaves every other surface the input's own.
    /// </summary>
    public BrepSolid Offset(Func<BrepFace, double> distance)
    {
        var offsets = new double[Faces.Length];
        for (int f = 0; f < offsets.Length; f++)
            offsets[f] = distance(Faces[f]);
        return Rebuild(Lift(offsets, "offset"), "offset");
    }

    /// <summary>
    /// The solid rebuilt on new carriers: one per face, in this body's face order. Every
    /// vertex is re-solved and every edge reconstructed against them, and the topology is
    /// carried over verbatim — so a caller supplies only "where each surface went" and gets
    /// back a valid solid. <see cref="Shelling"/> supplies offset carriers and
    /// <see cref="Draft"/> tapered ones; the machinery between is identical, which is the
    /// point of having it once.
    ///
    /// <para>Face <c>f</c> out is face <c>f</c> in, moved — so it inherits that face's
    /// provenance. The correspondence is this body's own face ORDER, which every table here
    /// (<see cref="FaceIndex"/>, <see cref="VertexFaces"/>, the carrier array the caller
    /// supplies) is already keyed on, so a tag can only land where its carrier did.</para>
    /// </summary>
    /// <param name="rimFromCarriers">
    /// Where a circular rim's AXIS comes from. False (the offset family's answer) reads it
    /// from a fit of the ORIGINAL edge, which is right there because
    /// <see cref="SurfaceOffset"/> carries every frame over verbatim — the offset carrier's
    /// axis IS the original's. True reads it from the NEW carriers, which is what a
    /// <see cref="DirectEdit.MoveFaces"/> or a replaced surface needs, because those move the
    /// axis and a rim built about the old one lands on neither surface. The two agree
    /// mathematically wherever the axis did not move; the flag exists so the offset path's
    /// arithmetic — and therefore every shell, draft and offset already committed — stays bit
    /// for bit what it was.
    /// </param>
    public BrepSolid Rebuild(Surface[] carriers, string what, bool rimFromCarriers = false)
    {
        var layer = Build(carriers, what, rimFromCarriers);
        var vertices = layer.Positions.Select(p => new BrepVertex(p)).ToArray();
        var edges = layer.BuildEdges(vertices);
        var faces = new BrepFace[Faces.Length];
        for (int f = 0; f < Faces.Length; f++)
            faces[f] = new BrepFace(layer.Surfaces[f], MapLoops(f, edges, reverse: false), Faces[f].IsReversed)
                .DescendsFrom(Faces[f]);
        return new BrepSolid([new BrepShell(faces)]);
    }

    /// <summary>
    /// Every face's carrier displaced along its own OUTWARD normal by its own distance.
    ///
    /// <para><see cref="SurfaceOffset.TryOffset"/> moves a surface along its own normal
    /// (∂u × ∂v), which is the OUTWARD normal only for a forward face; a reversed face's
    /// outward normal is the negative of its surface's, so its surface is offset by
    /// <c>−distance</c>. Every consumer states the offset in OUTWARD terms — a positive
    /// <c>OffsetFaces</c> grows the solid, a shell's inner layer is a negative outward offset
    /// — so the sign lives here once and the callers stay orientation-free. The shell and
    /// draft paths never pass a reversed face (they ask <see cref="Recognize(BrepSolid)"/>
    /// with the flag off), so their <c>Lift</c> is bit-identical.</para>
    /// </summary>
    private Surface[] Lift(double[] offsets, string what)
    {
        var carriers = new Surface[Faces.Length];
        for (int f = 0; f < Faces.Length; f++)
        {
            double signed = Faces[f].IsReversed ? -offsets[f] : offsets[f];
            if (!SurfaceOffset.TryOffset(Faces[f].Surface, signed, out carriers[f], out var reason))
                throw new NotSupportedException(
                    $"The {what} cannot lift a {Faces[f].Surface.GetType().Name} face: {reason}.");
        }
        return carriers;
    }

    public BrepSolid Shell(Func<BrepFace, double> thickness, Func<BrepFace, bool>? openingSelector)
    {
        int faceCount = Faces.Length;
        var opening = new bool[faceCount];
        int openings = 0;
        if (openingSelector is not null)
        {
            for (int f = 0; f < faceCount; f++)
            {
                if (!openingSelector(Faces[f]))
                    continue;
                if (Faces[f].Loops.Count != 1)
                    throw new NotSupportedException(
                        "An opening face has holes; its rim would need an annulus per hole wound the " +
                        "other way. Open a face without holes, or shell first and cut the opening as a " +
                        "boolean.");
                opening[f] = true;
                openings++;
            }
        }
        if (openings == faceCount)
            throw new ArgumentException("Every face was selected as an opening; nothing would be left.",
                nameof(openingSelector));
        for (int e = 0; e < Edges.Length; e++)
        {
            var (a, b) = EdgeFaces[e];
            if (opening[a] && opening[b])
                throw new NotSupportedException(
                    "Two adjacent faces were selected as openings; the rim between them would have zero " +
                    "width. Open them one at a time, or merge them into a single opening first.");
        }

        // Openings do not move: the cavity must reach exactly to the removed face's carrier.
        var offsets = new double[faceCount];
        for (int f = 0; f < faceCount; f++)
        {
            if (opening[f])
                continue;
            double wall = thickness(Faces[f]);
            if (!(wall > 0))
                throw new ArgumentOutOfRangeException(nameof(thickness),
                    $"Wall thickness must be positive; the selector returned {wall:g4} for a face.");
            offsets[f] = -wall;
        }
        var inner = Build(Lift(offsets, "inner"), "inner");

        var outerVertices = Vertices.Select(v => new BrepVertex(v.Position)).ToArray();
        var innerVertices = inner.Positions.Select(p => new BrepVertex(p)).ToArray();
        var outerEdges = new BrepEdge[Edges.Length];
        for (int e = 0; e < Edges.Length; e++)
        {
            var (start, end) = EdgeVertices[e];
            outerEdges[e] = new BrepEdge(
                Edges[e].Curve, Edges[e].Domain, outerVertices[start], outerVertices[end]);
        }
        var innerEdges = inner.BuildEdges(innerVertices);

        var outerFaces = new List<BrepFace>(faceCount);
        var innerFaces = new List<BrepFace>(faceCount);
        var rimFaces = new List<BrepFace>(openings);
        for (int f = 0; f < faceCount; f++)
        {
            if (opening[f])
            {
                // Rim: the removed face's own loops (supplying the second use of every edge
                // it carried) plus the inner opening as a hole, wound the other way.
                var loops = MapLoops(f, outerEdges, reverse: false);
                loops.AddRange(MapLoops(f, innerEdges, reverse: true));
                rimFaces.Add(new BrepFace(Faces[f].Surface, loops, Faces[f].IsReversed)
                    .DescendsFrom(Faces[f]));
                continue;
            }

            // One parent, TWO children: a wall and its inward twin both descend from the
            // face that generated them, which provenance allows because a tag names a SET.
            // A query for "the boss" therefore returns the cavity wall behind it as well —
            // correct rather than surprising, since that wall exists only because this one
            // does, and narrowing to the outer skin is an ordinary Within(...) away.
            outerFaces.Add(new BrepFace(
                Faces[f].Surface, MapLoops(f, outerEdges, reverse: false), Faces[f].IsReversed)
                .DescendsFrom(Faces[f]));
            innerFaces.Add(Flipped(inner.Surfaces[f], MapLoops(f, innerEdges, reverse: true))
                .DescendsFrom(Faces[f]));
        }

        if (openings == 0)
            return new BrepSolid([new BrepShell(outerFaces), new BrepShell(innerFaces)]);

        var all = new List<BrepFace>(outerFaces.Count + innerFaces.Count + rimFaces.Count);
        all.AddRange(outerFaces);
        all.AddRange(innerFaces);
        all.AddRange(rimFaces);
        return new BrepSolid([new BrepShell(all)]);
    }

    /// <summary>
    /// A cavity face: the same carrier with its outward normal turned around. A plane can
    /// do that EXACTLY in its own frame — swapping X and Y negates X × Y with no
    /// arithmetic at all — while a cylinder's or a revolve's normal is intrinsic, so those
    /// carry <see cref="BrepFace.IsReversed"/> instead, which the tessellator and the STEP
    /// writer both already honour (<c>same_sense</c>).
    /// </summary>
    private static BrepFace Flipped(Surface surface, IReadOnlyList<BrepLoop> loops) =>
        surface is PlaneSurface plane
            ? new BrepFace(new PlaneSurface(plane.Origin, plane.YDirection, plane.XDirection), loops)
            : new BrepFace(surface, loops, isReversed: true);

    /// <summary>
    /// The face's loops rebuilt over <paramref name="edges"/>. Reversing walks each loop
    /// backwards with flipped senses, which is exactly what a face whose normal has been
    /// turned around needs to stay counter-clockwise.
    /// </summary>
    private List<BrepLoop> MapLoops(int face, BrepEdge[] edges, bool reverse)
    {
        var loops = new List<BrepLoop>(Faces[face].Loops.Count);
        foreach (var loop in Faces[face].Loops)
        {
            var coedges = new List<BrepCoedge>(loop.Coedges.Count);
            for (int i = 0; i < loop.Coedges.Count; i++)
            {
                var coedge = reverse ? loop.Coedges[loop.Coedges.Count - 1 - i] : loop.Coedges[i];
                coedges.Add(new BrepCoedge(edges[EdgeIndex[coedge.Edge]], reverse != coedge.SameSense));
            }
            loops.Add(new BrepLoop(coedges));
        }
        return loops;
    }

    /// <summary>Everything one offset layer needs: carriers, corner positions, rim curves.</summary>
    private sealed class Layer
    {
        public required Surface[] Surfaces { get; init; }
        public required Vector3d[] Positions { get; init; }
        public required (Curve3d Curve, Interval Domain)[] Curves { get; init; }
        public required (int Start, int End)[] EdgeVertices { get; init; }

        public BrepEdge[] BuildEdges(BrepVertex[] vertices)
        {
            var edges = new BrepEdge[Curves.Length];
            for (int e = 0; e < edges.Length; e++)
            {
                var (start, end) = EdgeVertices[e];
                edges[e] = new BrepEdge(Curves[e].Curve, Curves[e].Domain, vertices[start], vertices[end]);
            }
            return edges;
        }
    }

    /// <summary>
    /// One offset layer: every carrier lifted, every corner re-solved, every rim rebuilt.
    /// Faces with a zero offset keep their carriers verbatim (an opening does not move), so
    /// a shell's outer skin is the input geometry rather than a re-derivation of it.
    /// </summary>
    private Layer Build(Surface[] carriers, string what, bool rimFromCarriers = false)
    {
        var surfaces = (Surface[])carriers.Clone();
        var positions = new Vector3d[Vertices.Length];
        for (int v = 0; v < Vertices.Length; v++)
            positions[v] = CornerAt(v, surfaces, what);
        if (rimFromCarriers)
            PhaseSeamVertices(surfaces, positions);

        var curves = new (Curve3d, Interval)[Edges.Length];
        for (int e = 0; e < Edges.Length; e++)
            curves[e] = RimCurve(e, surfaces, positions, what, rimFromCarriers);

        // Domain-driven carriers must be re-trimmed to their new loops. An extrusion's or a
        // revolve's grid ignores the loops entirely, so an offset band whose rims moved
        // keeps sampling its OLD extent and the trimmed tessellator refuses the face — the
        // "shorten the loops, shorten the surface too" rule, which bites here because an
        // inward offset moves a cone's rims axially as well as radially.
        for (int f = 0; f < Faces.Length; f++)
            surfaces[f] = TrimToLoops(surfaces[f], f, curves);

        ValidateOffset(positions, what);
        return new Layer
        {
            Surfaces = surfaces,
            Positions = positions,
            Curves = curves,
            EdgeVertices = EdgeVertices,
        };
    }

    /// <summary>
    /// A vertex's offset position: the point where its incident carriers meet again after
    /// the offset, handed straight to <see cref="SurfaceCorner"/>.
    ///
    /// <para>Nothing special is done for seam or tangent vertices, and that is the point:
    /// the corner solve's minimum-norm step already confines the answer to the span of the
    /// carriers' normals, which is exactly the freedom a seam or a tangent junction has.
    /// A cylinder's seam therefore stays in its seam half-plane and an elbow's junction
    /// keeps its angle on the offset profile circle without either case being written
    /// down.</para>
    /// </summary>
    private Vector3d CornerAt(int vertex, Surface[] surfaces, string what)
    {
        var seed = Vertices[vertex].Position;
        var carriers = VertexFaces[vertex].Select(f => surfaces[f]).ToArray();
        if (!SurfaceCorner.TrySolvePoint(carriers, seed, out var corner, out var reason))
            throw new NotSupportedException(
                $"The {what} cannot re-solve the corner at {seed}: {reason}.");
        return corner.Point;
    }

    /// <summary>
    /// The offset of one edge. A pair of planes gives the straight edge the polyhedral tier
    /// builds; a circular edge is reconstructed as a concentric circle about its own axis
    /// and PHASE, then verified against both offset carriers; anything else defers to
    /// <see cref="SurfaceCorner"/>'s exact tier, which refuses a chordal answer.
    /// </summary>
    private (Curve3d Curve, Interval Domain) RimCurve(
        int edge, Surface[] surfaces, Vector3d[] positions, string what, bool rimFromCarriers = false)
    {
        var (a, b) = EdgeFaces[edge];
        var (start, end) = EdgeVertices[edge];
        var original = Edges[edge];

        if (surfaces[a] is PlaneSurface && surfaces[b] is PlaneSurface)
            return (new Line3d(positions[start], positions[end]), Interval.Unit);

        // The EDGE's domain, not the curve's: a partial revolve's rail is a whole Circle3d
        // used over a quarter turn, and fitting the carrier would rebuild it closed.
        if (CircularArc.TryFit(original.Curve, original.Domain, out var fit))
        {
            RimAxis? axis = null;
            if (rimFromCarriers && TryRimAxis(surfaces[a], surfaces[b], out var carried))
                axis = carried;
            var rebuilt = ConcentricRim(fit, original, positions[start], positions[end], axis);
            if (rebuilt is { } result && OnBothCarriers(result.Curve, result.Domain, surfaces[a], surfaces[b]))
                return result;
            throw new NotSupportedException(
                $"The {what} cannot rebuild the circular edge at {original.Curve.PointAt(original.Domain.Start)} " +
                "as a concentric circle on both offset carriers. That happens when the two carriers move " +
                "the rim out of its own plane (a sealed shell of a partial revolve is the standard case), " +
                "and the true offset rim is then not a circle at all. Open that face instead, or offset a " +
                "solid whose curved rims stay perpendicular to their axes.");
        }

        if (SurfaceCorner.TrySolveCurve(
                surfaces[a], surfaces[b], positions[start], positions[end],
                SurfaceCorner.CornerPolicy.ExactOnly, out var corner, out var reason))
            return (corner.Curve, corner.Curve.Domain);

        throw new NotSupportedException(
            $"The {what} cannot rebuild the edge at {original.Curve.PointAt(original.Domain.Start)}: {reason}.");
    }

    /// <summary>
    /// A closed rim has ONE vertex and it is a SEAM, so where it sits is a phase rather than a
    /// position — and a corner solve, which only knows how to find the nearest point of the
    /// carriers' common locus to a seed, has no way to know that. On the offset family it does
    /// not matter: the carriers keep their frames, so the old seam is still at u = 0 and the
    /// solve lands on it. On a MOVE it does — the rim's centre has gone somewhere else, and
    /// the nearest point to the old seam is at an arbitrary angle on the new circle, which
    /// then disagrees with the circle a carrier-framed reconstruction builds.
    ///
    /// <para>So each closed rim's vertex is TURNED about its own axis onto the carrier's
    /// u = 0 — the phase-alignment rule every analytic intersection in this kernel already
    /// obeys, applied to the one point that carries a phase. The turn is legal for exactly the
    /// reason the rim is a circle at all: both carriers are surfaces of revolution about that
    /// axis (or a plane perpendicular to it), so rotating a point that lies on them about it
    /// leaves it on them. Where that is not true the rebuilt rim fails the
    /// <see cref="OnBothCarriers"/> verification and is refused by name — construct, then
    /// verify, rather than nudge and hope.</para>
    /// </summary>
    private void PhaseSeamVertices(Surface[] surfaces, Vector3d[] positions)
    {
        for (int e = 0; e < Edges.Length; e++)
        {
            if (!Edges[e].IsClosedEdge)
                continue;
            var (a, b) = EdgeFaces[e];
            if (!TryRimAxis(surfaces[a], surfaces[b], out var rim))
                continue;
            int seam = EdgeVertices[e].Start;
            var centre = rim.Origin + rim.Direction * (positions[seam] - rim.Origin).Dot(rim.Direction);
            double radius = (positions[seam] - centre).Length;
            if (radius > Tolerance.Default.Linear)
                positions[seam] = centre + rim.FrameX * radius;
        }
    }

    /// <summary>An axis a CARRIER pins, with the phase direction that carrier's grid uses.</summary>
    private readonly record struct RimAxis(Vector3d Origin, Vector3d Direction, Vector3d FrameX);

    /// <summary>
    /// The axis a rim between two carriers must be built about, when one of them pins it.
    ///
    /// <para>Read from the CARRIER rather than from a fit of the old edge because a
    /// <see cref="DirectEdit.MoveFaces"/> moves the axis: a cylinder translated sideways
    /// carries its rim with it, and a circle rebuilt about the old axis lies on neither
    /// surface. The FRAME comes from the same carrier, which is the phase rule stated at its
    /// source — a rim built on the carrier's own X lands on that carrier's tessellation grid
    /// by construction, whatever the fit of the old edge would have recovered.</para>
    /// </summary>
    private static bool TryRimAxis(Surface a, Surface b, out RimAxis axis)
    {
        axis = default;
        foreach (var surface in (ReadOnlySpan<Surface>)[a, b])
        {
            switch (surface)
            {
                case CylinderSurface cylinder:
                    axis = new RimAxis(
                        cylinder.Origin, cylinder.Axis.Normalized(), cylinder.XDirection.Normalized());
                    return true;

                case RevolvedSurface revolved:
                {
                    // A revolve's u = 0 half-plane is the one its generator lies in, so the
                    // phase direction is the generator's own radial — the same reading
                    // SurfaceOffset preserves when it carries a generator across. The
                    // generator is SCANNED rather than sampled once: an axis-touching one (a
                    // drill tool's, a pole cap's) has stretches ON the axis where the radial
                    // vanishes and says nothing about the phase.
                    var direction = revolved.AxisDirection.Normalized();
                    var generator = revolved.Generator;
                    var best = Vector3d.Zero;
                    for (int i = 0; i <= 16; i++)
                    {
                        var point = generator.PointAt(generator.Domain.ParameterAt(i / 16.0));
                        var radial = point - revolved.AxisOrigin;
                        radial -= direction * radial.Dot(direction);
                        if (radial.LengthSquared > best.LengthSquared)
                            best = radial;
                    }
                    if (!best.TryNormalize(Tolerance.Default, out var frameX))
                        continue;
                    axis = new RimAxis(revolved.AxisOrigin, direction, frameX);
                    return true;
                }

                case SphereSurface sphere when (a is PlaneSurface || b is PlaneSurface):
                {
                    // A sphere cut by a plane gives a circle whose axis is the plane's
                    // normal through the sphere's centre.
                    var plane = (PlaneSurface)(a is PlaneSurface ? a : b);
                    if (!plane.Normal.TryNormalize(Tolerance.Default, out var normal))
                        continue;
                    axis = new RimAxis(sphere.Center, normal, plane.XDirection.Normalized());
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// The rim rebuilt as a circle about its axis, through the solved corner. Centre and
    /// radius come from the corner; the FRAME comes from the original (or, when
    /// <paramref name="carried"/> is supplied, from the new carrier), so a rebuilt rim's
    /// u = 0 sits where the carrier's grid puts it and nothing has to be re-phased. A closed
    /// rim keeps its full turn; an open arc keeps its measured span.
    /// </summary>
    private static (Curve3d Curve, Interval Domain)? ConcentricRim(
        in CircularArc fit, BrepEdge original, in Vector3d start, in Vector3d end, RimAxis? carried = null)
    {
        var axis = carried?.Direction ?? fit.Circle.Axis.Normalized();
        var anchor = carried?.Origin ?? fit.Circle.Center;
        var centre = anchor + axis * (start - anchor).Dot(axis);
        var radial = start - centre;
        double radius = radial.Length;
        if (radius <= Tolerance.Default.Linear)
            return null;

        // Phase: the rebuilt frame must be the carrier's, or every downstream grid moves.
        // Reading the radius as a signed projection onto that X keeps the frame EXACT rather
        // than recovering it from a solved point.
        var frameX = carried?.FrameX ?? fit.Circle.XDirection;
        var frameY = carried is null ? fit.Circle.YDirection : axis.Cross(carried.Value.FrameX);
        double x = radial.Dot(frameX);
        double y = radial.Dot(frameY);
        double startAngle = 0;
        if (Math.Abs(y) > Tolerance.Default.Linear * Math.Max(1, radius))
        {
            if (original.IsClosedEdge)
                return null; // a closed rim whose seam rotated is not this circle
            startAngle = Math.Atan2(y, x);
        }
        else if (x < 0)
        {
            return null; // the rim turned inside out through its own axis
        }

        var circle = new Circle3d(centre, frameX, axis.Cross(frameX), radius);
        if (original.IsClosedEdge)
            return (circle, new Interval(0, 2 * Math.PI));

        // The SPAN is the original's, taken verbatim rather than re-measured from the two
        // solved corners: a concentric offset preserves angles exactly, and re-deriving
        // one would have to pick a branch at a half turn — where an arc of exactly π has
        // its end angle land on ±π by round-off. The end corner is then a CHECK, which is
        // the honest direction: if it does not land, the concentric hypothesis is false.
        var segment = new CurveSegment(circle, startAngle, startAngle + fit.Sweep);
        if (segment.PointAt(1).DistanceTo(end) > Tolerance.Default.Linear * Math.Max(1, radius))
            return null;
        return (segment, Interval.Unit);
    }

    /// <summary>
    /// Whether every sample of a constructed rim really lies on both offset carriers. This
    /// is what makes construction safe: the circle is a hypothesis about where the two
    /// surfaces meet, and the hypothesis is false whenever the offset moves a rim out of
    /// its own plane.
    /// </summary>
    private static bool OnBothCarriers(Curve3d curve, in Interval domain, Surface a, Surface b)
    {
        if (!ImplicitSurface.TryBuild(a, out var fieldA, out _)
            || !ImplicitSurface.TryBuild(b, out var fieldB, out _))
            return false;
        double scale = 0;
        var anchor = curve.PointAt(domain.Start);
        const int samples = 32;
        for (int i = 0; i <= samples; i++)
            scale = Math.Max(scale, curve.PointAt(domain.ParameterAt((double)i / samples)).DistanceTo(anchor));
        double tolerance = VerificationTolerance * Math.Max(1, scale);
        for (int i = 0; i <= samples; i++)
        {
            var point = curve.PointAt(domain.ParameterAt((double)i / samples));
            if (Math.Abs(fieldA.Evaluate(point, out _)) > tolerance
                || Math.Abs(fieldB.Evaluate(point, out _)) > tolerance)
                return false;
        }
        return true;
    }

    /// <summary>
    /// A domain-driven carrier shortened (or lengthened) to span exactly the face's new
    /// loops.
    ///
    /// <para>The generator parameter is solved GEOMETRICALLY — against the loop points'
    /// own axial or axial-direction coordinate on the exact carrier — never by projecting
    /// them onto the surface. That is the vCut lesson: a projection carries ~1e-7 of error,
    /// which on a cone shifts the trimmed ring RADIALLY by the slope times that, past the
    /// weld tolerance. A straight generator makes the solve closed-form, which is why only
    /// straight ones are re-trimmed; a curved generator's loops run ALONG it (an elbow's
    /// cap rims are full-generator spans) and its domain already fits.</para>
    /// </summary>
    private Surface TrimToLoops(Surface surface, int face, (Curve3d Curve, Interval Domain)[] curves)
    {
        var points = new List<Vector3d>();
        foreach (var loop in Faces[face].Loops)
        {
            foreach (var coedge in loop.Coedges)
            {
                var (curve, domain) = curves[EdgeIndex[coedge.Edge]];
                for (int i = 0; i <= 8; i++)
                    points.Add(curve.PointAt(domain.ParameterAt(i / 8.0)));
            }
        }
        return TrimToPoints(surface, points);
    }

    /// <inheritdoc cref="TrimToLoops"/>
    /// <remarks>The rule itself, over the boundary points a caller has already sampled — so
    /// <see cref="DirectEdit"/>'s heal-by-extension, which rewrites loops rather than carrying
    /// them over, asks the same question rather than restating it.</remarks>
    internal static Surface TrimToPoints(Surface surface, IReadOnlyList<Vector3d> points)
    {
        if (points.Count == 0)
            return surface;

        switch (surface)
        {
            case RevolvedSurface revolved when Straight(revolved.Generator, out var a, out var b):
            {
                var axis = revolved.AxisDirection;
                double axialA = (a - revolved.AxisOrigin).Dot(axis);
                double axialB = (b - revolved.AxisOrigin).Dot(axis);
                // A generator perpendicular to the axis (a disc) has no axial extent to
                // solve against; its loops are rings at one height and the domain is right.
                if (Math.Abs(axialB - axialA) <= Tolerance.Default.Linear)
                    return surface;
                double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
                foreach (var point in points)
                {
                    double t = ((point - revolved.AxisOrigin).Dot(axis) - axialA) / (axialB - axialA);
                    lo = Math.Min(lo, t);
                    hi = Math.Max(hi, t);
                }
                if (Math.Abs(lo) <= 1e-12 && Math.Abs(hi - 1) <= 1e-12)
                    return surface; // already exact: leave the object identical
                return new RevolvedSurface(
                    new Line3d(a + (b - a) * lo, a + (b - a) * hi),
                    revolved.AxisOrigin, revolved.AxisDirection, revolved.Angle);
            }

            case ExtrudedSurface extruded:
            {
                double lengthSquared = extruded.Direction.LengthSquared;
                if (lengthSquared <= 0)
                    return surface;
                var anchor = extruded.Generator.PointAt(extruded.Generator.Domain.Start);
                double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
                foreach (var point in points)
                {
                    double v = (point - anchor).Dot(extruded.Direction) / lengthSquared;
                    lo = Math.Min(lo, v);
                    hi = Math.Max(hi, v);
                }
                if (Math.Abs(lo) <= 1e-12 && Math.Abs(hi - 1) <= 1e-12)
                    return surface;
                return new ExtrudedSurface(
                    extruded.Generator.Transformed(Matrix4d.CreateTranslation(extruded.Direction * lo)),
                    extruded.Direction * (hi - lo));
            }

            default:
                return surface;
        }
    }

    /// <summary>The curve's endpoints when it is straight at the weld tier.</summary>
    private static bool Straight(Curve3d curve, out Vector3d start, out Vector3d end)
    {
        var domain = curve.Domain;
        start = curve.PointAt(domain.Start);
        end = curve.PointAt(domain.End);
        var chord = end - start;
        if (!chord.TryNormalize(Tolerance.Default, out var tangent))
            return false;
        for (int i = 1; i < 8; i++)
        {
            var offset = curve.PointAt(domain.ParameterAt(i / 8.0)) - start;
            if ((offset - tangent * offset.Dot(tangent)).Length
                > Tolerance.Default.Linear * Math.Max(1, chord.Length))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Local sanity of an offset: no edge may collapse or reverse. The polyhedral tier's
    /// loop-area check does not generalize (a closed rim's two endpoints are one point), so
    /// this checks the CHORD of every open edge and the RADIUS of every closed one, both of
    /// which a folding offset destroys.
    /// </summary>
    private void ValidateOffset(Vector3d[] positions, string what)
    {
        for (int e = 0; e < Edges.Length; e++)
        {
            var (start, end) = EdgeVertices[e];
            if (Edges[e].IsClosedEdge)
                continue;
            var moved = positions[end] - positions[start];
            var reference = Vertices[end].Position - Vertices[start].Position;
            if (moved.Dot(reference) <= 0)
                throw new ArgumentException(
                    $"The {what} collapses or reverses an edge: the distance exceeds what the solid's " +
                    "local size allows.");
        }
    }
}
