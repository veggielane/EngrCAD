using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// Rim chamfering and filleting: the closed boundary rim of a planar face is replaced
/// by a bevel or blend band. Chamfer supports straight edges with sharp corners (they
/// miter exactly — planar trapezoid strips share miter edges) and full circular rims
/// (exact cone bands). Fillet supports full circular rims (exact quarter-torus),
/// tangent-continuous line+arc rims (quarter-cylinder and quarter-torus-segment bands
/// sharing junction arcs — e.g. rounded-rectangle plate tops), and <b>sharp corners
/// between straight rim edges</b>, which miter on an exact ellipse: two equal-radius
/// quarter cylinders whose axes intersect are a bicylinder, and the branch through the
/// corner's inset top point and its dropped bottom point is the conic
/// <c>Ellipse3d(M, up·r, bottom − M)</c> with <c>M</c> the axis crossing. Only two of
/// the three edges meeting at such a corner are blended (the side faces keep their
/// sharp shared edge), and the miter is the only surface that closes that
/// configuration: a spherical corner patch would leave the cross-section jumping from
/// a rounded corner to a sharp one at the tangency plane.
/// <para>Chamfers also come in a VARIABLE-setback form (<see
/// cref="ChamferRim(BrepSolid, BrepFace, Func{Vector3d, double})"/>): a law evaluated at
/// rim corners, linear along each edge. A linearly varying inset of a straight edge is
/// still a straight line and a constant top:side ratio keeps each strip's four corners
/// coplanar, so the strips stay exact planes and the miters exact intersections; arcs
/// and full circles demand a locally constant law, because a circle offset by a varying
/// amount is a spiral. Variable-RADIUS fillets stay refused: the bands themselves would
/// be exact degree-(2,1) NURBS, but two such bands meet in a non-conic curve with no
/// exact miter (the corner blocks it, not the band).</para>
/// </summary>
public static class Filleting
{
    /// <summary>45° chamfer of the outer rim of a planar face.</summary>
    public static BrepSolid ChamferRim(BrepSolid solid, BrepFace face, double setback) =>
        ChamferRim(solid, face, setback, setback);

    /// <summary>Chamfers the outer rim of a planar face: the face shrinks by
    /// <paramref name="topSetback"/>, neighbors drop by <paramref name="sideSetback"/>,
    /// and planar strips (mitered at corners) or cone bands (circular rims) fill in.</summary>
    public static BrepSolid ChamferRim(BrepSolid solid, BrepFace face, double topSetback, double sideSetback)
    {
        if (topSetback <= 0 || sideSetback <= 0)
            throw new ArgumentOutOfRangeException(nameof(topSetback));
        return RimSurgeon.Apply(solid, face, topSetback, sideSetback, fillet: false);
    }

    /// <summary>
    /// Fillets the outer rim of a planar face with the given radius. The rim must be a
    /// full circle, a tangent-continuous chain of lines and arcs, or a chain whose sharp
    /// corners join two straight edges (those miter on an exact ellipse). A sharp corner
    /// where an arc meets anything is rejected — that blend has no exact conic.
    /// </summary>
    public static BrepSolid FilletRim(BrepSolid solid, BrepFace face, double radius)
    {
        if (radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius));
        return RimSurgeon.Apply(solid, face, radius, radius, fillet: true);
    }

    /// <summary>
    /// Chamfers the outer rim of a planar face by a setback measured IN that face and an
    /// angle measured FROM it — the "distance and angle" spelling every CAD system offers
    /// beside two setbacks. 45° is the symmetric chamfer; a larger angle bites deeper into
    /// the neighbours, a smaller one wider across the face.
    /// </summary>
    public static BrepSolid ChamferRimAtAngle(BrepSolid solid, BrepFace face, double setback, double angleDegrees)
    {
        if (setback <= 0)
            throw new ArgumentOutOfRangeException(nameof(setback));
        // Open interval: 0° would lie in the face and 90° would run parallel to the
        // neighbours, and either produces a zero-width strip rather than a chamfer.
        if (angleDegrees <= 0 || angleDegrees >= 90)
            throw new ArgumentOutOfRangeException(nameof(angleDegrees),
                "The chamfer angle is measured from the chamfered face and must lie strictly between 0° and 90°.");
        return ChamferRim(solid, face, setback, setback * Math.Tan(angleDegrees * Math.PI / 180));
    }

    /// <summary>
    /// Variable-setback chamfer of the outer rim of a planar face: the law is evaluated
    /// at each rim CORNER and the setback interpolates linearly between corner values
    /// along each edge. The inset top boundary of a straight edge is then still a
    /// straight LINE (merely tilted), so sharp corners keep exact miters — the corner
    /// segment from the mitered top point to the dropped bottom point is a boundary
    /// ruling of both adjacent strips — and every strip stays an exact plane, because a
    /// symmetric (or constant-angle) law keeps the top and side offsets proportional.
    /// Arc rim edges are accepted only where the law is constant along the arc (a circle
    /// offset by a varying amount is a spiral, not a circle), and a full circular rim
    /// only for an everywhere-constant law, for the same reason.
    /// </summary>
    public static BrepSolid ChamferRim(BrepSolid solid, BrepFace face, Func<Vector3d, double> setbackAt)
    {
        ArgumentNullException.ThrowIfNull(setbackAt);
        // Side ratio exactly 1 — the symmetric chamfer — rather than Tan(45 deg),
        // which is 1 minus an ulp.
        return RimSurgeon.ApplyVariable(solid, face, setbackAt, 1.0);
    }

    /// <summary>
    /// Variable-setback chamfer by a law measured IN the face and an angle measured FROM
    /// it (see <see cref="ChamferRim(BrepSolid, BrepFace, Func{Vector3d, double})"/>).
    /// The angle is constant along the rim, which is exactly what keeps every strip
    /// planar: the side drop stays proportional to the top setback.
    /// </summary>
    public static BrepSolid ChamferRimAtAngle(
        BrepSolid solid, BrepFace face, Func<Vector3d, double> setbackAt, double angleDegrees)
    {
        ArgumentNullException.ThrowIfNull(setbackAt);
        if (angleDegrees <= 0 || angleDegrees >= 90)
            throw new ArgumentOutOfRangeException(nameof(angleDegrees),
                "The chamfer angle is measured from the chamfered face and must lie strictly between 0° and 90°.");
        return RimSurgeon.ApplyVariable(solid, face, setbackAt, Math.Tan(angleDegrees * Math.PI / 180));
    }

    /// <summary>Variable-setback chamfer of a set of EDGES; the selection resolves to
    /// complete rims exactly as <see cref="ChamferEdges(BrepSolid, IEnumerable{BrepEdge}, double)"/>.</summary>
    public static BrepSolid ChamferEdges(
        BrepSolid solid, IEnumerable<BrepEdge> edges, Func<Vector3d, double> setbackAt)
    {
        ArgumentNullException.ThrowIfNull(setbackAt);
        var solidUnderConstruction = solid;
        foreach (var face in RimFacesFor(solid, edges))
            solidUnderConstruction = ChamferRim(solidUnderConstruction, face, setbackAt);
        return solidUnderConstruction;
    }

    /// <summary>
    /// Fillets a set of EDGES rather than a face: the selection is resolved to the rim
    /// features that reproduce it (see <see cref="RimFacesFor"/>) and applied in turn.
    /// </summary>
    public static BrepSolid FilletEdges(BrepSolid solid, IEnumerable<BrepEdge> edges, double radius)
    {
        if (radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius));
        var solidUnderConstruction = solid;
        foreach (var face in RimFacesFor(solid, edges))
            solidUnderConstruction = FilletRim(solidUnderConstruction, face, radius);
        return solidUnderConstruction;
    }

    /// <summary>Chamfers a set of EDGES; see <see cref="FilletEdges"/> and <see cref="RimFacesFor"/>.</summary>
    public static BrepSolid ChamferEdges(BrepSolid solid, IEnumerable<BrepEdge> edges, double setback) =>
        ChamferEdges(solid, edges, setback, setback);

    /// <inheritdoc cref="ChamferEdges(BrepSolid, IEnumerable{BrepEdge}, double)"/>
    public static BrepSolid ChamferEdges(
        BrepSolid solid, IEnumerable<BrepEdge> edges, double topSetback, double sideSetback)
    {
        if (topSetback <= 0 || sideSetback <= 0)
            throw new ArgumentOutOfRangeException(nameof(topSetback));
        var solidUnderConstruction = solid;
        foreach (var face in RimFacesFor(solid, edges))
            solidUnderConstruction = ChamferRim(solidUnderConstruction, face, topSetback, sideSetback);
        return solidUnderConstruction;
    }

    /// <summary>
    /// Fillets EVERY edge of a convex polyhedron at once — the case whose vertices need
    /// spherical corner patches, and the one <see cref="FilletEdges"/> refuses.
    /// <para>The construction is the morphological opening (K ⊖ B_r) ⊕ B_r, which for a
    /// convex polyhedron is exact and needs no booleans: erode by moving every face plane
    /// inward by r, then dilate. Each face keeps its own plane and gains a shrunk
    /// boundary; each edge becomes a cylindrical band of radius r about the ERODED edge
    /// line (the intersection of its two shifted planes); each vertex becomes a spherical
    /// patch of radius r centred on the ERODED vertex (the intersection of its three
    /// shifted planes), bounded by great-circle arcs. Every boundary curve is shared by
    /// exactly two of those faces by construction, so nothing has to be intersected,
    /// classified or sealed.</para>
    /// <para>Restrictions, all checked and refused loudly: the solid must be a convex
    /// polyhedron (planar hole-free faces, straight edges, every edge convex) with
    /// three-valent vertices. Corners where one face is perpendicular to the other two
    /// (every box and convex prism) get the exact LUNE — a full-domain surface of
    /// revolution. GENERAL trihedral corners (a tetrahedron's, a drafted block's) get a
    /// trimmed spherical-triangle patch: one face normal becomes the pole axis, the two
    /// arcs meeting its tangency are exact meridians on the u domain boundaries, and
    /// only the third arc trims the interior — see <see cref="GeneralCornerPatch"/>.</para>
    /// </summary>
    public static BrepSolid FilletAllEdges(BrepSolid solid, double radius)
    {
        if (radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius));
        return ConvexOpening.Apply(solid, radius);
    }

    /// <summary>
    /// Resolves an edge selection into the planar faces whose complete outer rims it
    /// covers — the unit rim surgery operates on, and therefore the unit an edge-set
    /// fillet or chamfer can honour exactly. A run of edges bounding one planar face
    /// (including a lone closed circular rim) resolves; anything else throws, naming the
    /// edges that could not be grouped.
    /// <para>The whole selection is resolved BEFORE any surgery runs, so a rejected set
    /// leaves the input solid untouched (rim surgery rewrites loops in place).</para>
    /// <para>Why not arbitrary runs: a fillet that stops partway along a face's rim has to
    /// terminate the band somewhere, and every exact termination (a cliff, a setback, a
    /// vertex blend) is a different surface. A rim is the case whose corners all close on
    /// exact conics — the miter ellipses — with nothing left dangling.</para>
    /// <para>Filleting EVERY edge of a solid is the other classic case and is deliberately
    /// NOT accepted here: three blended edges meeting at a vertex need a spherical corner
    /// patch, which only closes when the vertex's third edge is blended too. That is a
    /// different construction from rim surgery — see <see cref="FilletAllEdges"/>.</para>
    /// </summary>
    public static IReadOnlyList<BrepFace> RimFacesFor(BrepSolid solid, IEnumerable<BrepEdge> edges)
    {
        var remaining = new List<BrepEdge>();
        foreach (var edge in edges)
        {
            if (!remaining.Any(e => ReferenceEquals(e, edge)))
                remaining.Add(edge);
        }
        if (remaining.Count == 0)
            throw new ArgumentException("No edges were selected.", nameof(edges));

        var targets = new List<BrepFace>();
        while (remaining.Count > 0)
        {
            var face = solid.Faces.FirstOrDefault(f =>
                !f.IsReversed && f.IsPlanar(out _, out _) &&
                !targets.Any(t => ReferenceEquals(t, f)) &&
                f.OuterLoop.Coedges.All(c => remaining.Any(e => ReferenceEquals(e, c.Edge))));
            if (face is null)
            {
                var stranded = remaining[0];
                throw new NotSupportedException(
                    $"The edge from {stranded.Curve.PointAt(stranded.Domain.Start)} to " +
                    $"{stranded.Curve.PointAt(stranded.Domain.End)} is not part of a fully selected planar " +
                    "face rim. Edge fillets and chamfers are exact for complete rims (every edge bounding " +
                    "one planar face, or a lone closed circular rim); select the rest of that face's rim, " +
                    "or use the face-based overload. To blend every edge of a convex solid " +
                    "(the case whose vertices need spherical corner patches), use FilletAllEdges.");
            }
            targets.Add(face);
            remaining.RemoveAll(e => face.OuterLoop.Coedges.Any(c => ReferenceEquals(c.Edge, e)));
        }
        return targets;
    }

    /// <summary>
    /// Whole-solid filleting of a convex polyhedron as the exact morphological opening.
    /// Nothing here is surgery: the output is built from scratch, and every shared curve
    /// is created ONCE and handed to both of its faces, so senses follow mechanically and
    /// the result is manifold by construction rather than by sealing.
    /// </summary>
    private static class ConvexOpening
    {
        public static BrepSolid Apply(BrepSolid solid, double radius)
        {
            var planes = ValidateAndPlanes(solid);
            var incidence = VertexIncidence(solid);

            // Eroded vertices: the three shifted planes' meeting point. Every eroded EDGE
            // line then joins the two eroded vertices at its ends (each lies on both of
            // the edge's shifted planes), so band axes need no separate solve.
            var eroded = new Dictionary<BrepVertex, Vector3d>();
            foreach (var (vertex, faces) in incidence)
                eroded[vertex] = ErodedVertex(vertex, faces, planes, radius);

            // One new vertex per (original vertex, incident face): where that face's
            // tangent lines and the corner sphere meet.
            var corners = new Dictionary<(BrepVertex, BrepFace), BrepVertex>();
            foreach (var (vertex, faces) in incidence)
            {
                foreach (var face in faces)
                    corners[(vertex, face)] = new BrepVertex(eroded[vertex] + planes[face].Normal * radius);
            }

            var straightEdges = new Dictionary<(BrepEdge, BrepFace), BrepEdge>();
            var sphereArcs = new Dictionary<BrepVertex, List<BrepCoedge>>();
            foreach (var vertex in incidence.Keys)
                sphereArcs[vertex] = [];

            var bands = new List<BrepFace>();
            foreach (var edge in solid.Edges)
            {
                // Orient the edge by the first use: face F walks a → b, face G walks b → a.
                var useF = edge.Uses[0];
                var faceF = useF.Loop.Face;
                var faceG = edge.Uses[1].Loop.Face;
                var a = useF.StartVertex;
                var b = useF.EndVertex;

                var centreA = eroded[a];
                var centreB = eroded[b];
                var axis = centreB - centreA;
                if (axis.Dot(b.Position - a.Position) <= 0 || axis.Length <= Tolerance.Default.Linear)
                    throw new ArgumentOutOfRangeException(nameof(radius),
                        $"The fillet radius consumes the edge from {a.Position} to {b.Position}.");

                var normalF = planes[faceF].Normal;
                var normalG = planes[faceG].Normal;
                // The corner circle lives in the plane spanned by the two face normals,
                // built on normalF so u = 0 is the F tangency: the SAME frame the band's
                // generator uses, which is what makes the two ends' arcs weld with it.
                double sweep = Math.Atan2(normalF.Cross(normalG).Dot(axis.Normalized()), normalF.Dot(normalG));
                var inPlane = (normalG - normalF * normalF.Dot(normalG)).Normalized();
                if (sweep < 0)
                {
                    // The circle's own sense must run F → G; flip the frame, not the span.
                    sweep = -sweep;
                    inPlane = -inPlane;
                }

                var arcA = CornerArc(centreA, normalF, inPlane, radius, sweep,
                    corners[(a, faceF)], corners[(a, faceG)]);
                var arcB = CornerArc(centreB, normalF, inPlane, radius, sweep,
                    corners[(b, faceF)], corners[(b, faceG)]);
                sphereArcs[a].Add(new BrepCoedge(arcA, false));
                sphereArcs[b].Add(new BrepCoedge(arcB, true));

                var lineF = Straight(corners[(a, faceF)], corners[(b, faceF)]);
                var lineG = Straight(corners[(b, faceG)], corners[(a, faceG)]);
                straightEdges[(edge, faceF)] = lineF;
                straightEdges[(edge, faceG)] = lineG;

                // Band loop, CCW about the outward normal: down F's tangent line, around
                // the corner at a, back along G's tangent line, around the corner at b.
                bands.Add(new BrepFace(
                    new ExtrudedSurface(arcA.Curve, axis),
                    [new BrepLoop([
                        new BrepCoedge(lineF, false),
                        new BrepCoedge(arcA, true),
                        new BrepCoedge(lineG, false),
                        new BrepCoedge(arcB, false),
                    ])]));
            }

            var shrunk = new List<BrepFace>();
            foreach (var face in solid.Faces)
            {
                var coedges = face.OuterLoop.Coedges
                    .Select(c => new BrepCoedge(straightEdges[(c.Edge, face)],
                        ReferenceEquals(straightEdges[(c.Edge, face)].StartVertex, corners[(c.StartVertex, face)])))
                    .ToList();
                shrunk.Add(new BrepFace(face.Surface, [new BrepLoop(coedges)]));
            }

            var spheres = incidence.Keys
                .Select(vertex => CornerPatch(vertex, incidence[vertex], planes, eroded[vertex], radius, sphereArcs[vertex]))
                .ToList();

            return new BrepSolid([new BrepShell([.. shrunk, .. bands, .. spheres])]);
        }

        private static BrepEdge Straight(BrepVertex from, BrepVertex to) =>
            new(new Line3d(from.Position, to.Position), Interval.Unit, from, to);

        /// <summary>
        /// The great-circle arc a band's end shares with a corner patch. Its parameter IS
        /// the angle (a <see cref="Circle3d"/> under a <see cref="CurveSegment"/>), which
        /// is load-bearing: the corner patch is a surface of revolution sampled at even
        /// ANGLES, so an arc parameterized any other way (a rational NURBS arc, say)
        /// would sample to different points and the patch would not weld to the band.
        /// </summary>
        private static BrepEdge CornerArc(
            in Vector3d centre, in Vector3d xDirection, in Vector3d yDirection, double radius, double sweep,
            BrepVertex from, BrepVertex to) =>
            new(new CurveSegment(new Circle3d(centre, xDirection, yDirection, radius), 0, sweep),
                Interval.Unit, from, to);

        /// <summary>
        /// The spherical corner patch: the lune between the meridians of the two faces
        /// perpendicular to the third, closed by the equatorial arc between them. The
        /// generator runs equator → pole so that ∂u × ∂v points out of the solid.
        /// </summary>
        private static BrepFace CornerPatch(
            BrepVertex vertex, IReadOnlyList<BrepFace> faces,
            Dictionary<BrepFace, (Vector3d Origin, Vector3d Normal)> planes,
            in Vector3d centre, double radius, List<BrepCoedge> arcs)
        {
            var normals = faces.Select(f => planes[f].Normal).ToList();
            int poleIndex = -1;
            for (int i = 0; i < 3 && poleIndex < 0; i++)
            {
                if (Math.Abs(normals[i].Dot(normals[(i + 1) % 3])) < 1e-9 &&
                    Math.Abs(normals[i].Dot(normals[(i + 2) % 3])) < 1e-9)
                    poleIndex = i;
            }
            if (poleIndex < 0)
                return GeneralCornerPatch(vertex, normals, centre, radius, arcs);

            var pole = normals[poleIndex];
            var first = normals[(poleIndex + 1) % 3];
            var second = normals[(poleIndex + 2) % 3];
            double sweep = Math.Atan2(first.Cross(second).Dot(pole), first.Dot(second));
            if (sweep < 0)
            {
                (first, second) = (second, first);
                sweep = -sweep;
            }

            // Equator → pole, so the revolve's ∂u × ∂v is outward; the parameter is the
            // angle, matching the band arcs' sampling.
            var generator = new CurveSegment(
                new Circle3d(centre, first, pole, radius), 0, Math.PI / 2);
            var surface = new RevolvedSurface(generator, centre, pole, sweep);

            return new BrepFace(surface, [new BrepLoop(Chain(arcs, vertex))]);
        }

        /// <summary>
        /// The GENERAL trihedral corner patch: a trimmed spherical triangle. When no
        /// incident face is perpendicular to the other two, the patch is not a lune —
        /// but it is still a region of the sphere about the eroded vertex, bounded by
        /// the three great-circle arcs the bands already built. Pick one face normal as
        /// the POLE axis: the two arcs that end at its tangency point are then exact
        /// MERIDIANS of that axis (a great circle through the pole lies in a plane
        /// containing the axis), so they land on the revolve's u = 0 and u = sweep
        /// domain boundaries, the pole tangency is the v-top boundary, and only the
        /// third (diagonal) arc genuinely trims the interior — a pole-bounded trimmed
        /// face for the tessellator, not a new surface type.
        /// <para>The generator's interior-side extent is NOT a weld boundary (nothing
        /// meets the patch there), so it takes the diagonal's sampled elevation minimum
        /// with a fixed angular margin; the three weld boundaries — both meridians and
        /// the pole — are exact by construction. The pole is chosen to maximize the
        /// worse-conditioned of the two azimuthal projections, and a diagonal whose
        /// azimuth leaves the [0, sweep] wedge (a reflex corner wedge) is refused by
        /// name rather than mis-trimmed.</para>
        /// </summary>
        private static BrepFace GeneralCornerPatch(
            BrepVertex vertex, IReadOnlyList<Vector3d> normals,
            in Vector3d centre, double radius, List<BrepCoedge> arcs)
        {
            int best = 0;
            double bestScore = -1;
            for (int i = 0; i < 3; i++)
            {
                double score = Math.Min(
                    normals[i].Cross(normals[(i + 1) % 3]).Length,
                    normals[i].Cross(normals[(i + 2) % 3]).Length);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }
            // Scale-free degeneracy guard on unit normals (an area, so ~angle here).
            if (bestScore < 1e-9)
                throw new NotSupportedException(
                    $"The corner at {vertex.Position} has two parallel face normals; its spherical patch " +
                    "is degenerate.");

            var pole = normals[best];
            var n1 = normals[(best + 1) % 3];
            var n2 = normals[(best + 2) % 3];
            var x1 = (n1 - pole * pole.Dot(n1)).Normalized();
            var x2 = (n2 - pole * pole.Dot(n2)).Normalized();
            double sweep = Math.Atan2(x1.Cross(x2).Dot(pole), x1.Dot(x2));
            if (sweep < 0)
            {
                (n1, n2) = (n2, n1);
                (x1, x2) = (x2, x1);
                sweep = -sweep;
            }

            // Generator elevation t: P(t) = centre + r·(x1·cos t + pole·sin t), so the
            // n1 tangency sits at t1 = atan2(pole·n1, x1·n1) and the pole at t = π/2.
            double t1 = Math.Atan2(pole.Dot(n1), x1.Dot(n1));
            double t2 = Math.Atan2(pole.Dot(n2), x2.Dot(n2));
            double tMin = Math.Min(t1, t2);

            // The diagonal great arc n1 → n2 can dip below both endpoint elevations
            // (its elevation is sinusoidal along the circle); sample it for the extent
            // and validate its azimuth stays inside the wedge.
            double omega = Math.Atan2(n1.Cross(n2).Length, n1.Dot(n2));
            double sinOmega = Math.Sin(omega);
            for (int k = 0; k <= 64; k++)
            {
                double s = k / 64.0;
                var d = (n1 * Math.Sin((1 - s) * omega) + n2 * Math.Sin(s * omega)) / sinOmega;
                var azimuthal = d - pole * pole.Dot(d);
                double azimuth = Math.Atan2(x1.Cross(azimuthal).Dot(pole), x1.Dot(azimuthal));
                if (azimuth < -1e-9 || azimuth > sweep + 1e-9)
                    throw new NotSupportedException(
                        $"The corner at {vertex.Position} subtends a reflex azimuth wedge about every " +
                        "candidate pole; its spherical patch cannot be parameterized as one revolve.");
                tMin = Math.Min(tMin, Math.Atan2(pole.Dot(d), azimuthal.Length));
            }

            // Fixed interior margin (radians): covers the ≤ (π/64)²/8 sampling sag and
            // keeps every loop point strictly inside the domain for pullback/parity.
            double startElevation = tMin - 0.05;
            if (startElevation <= -Math.PI / 2 + 0.05)
                throw new NotSupportedException(
                    $"The corner at {vertex.Position} reaches the antipode of its own patch pole; " +
                    "the corner is too oblique to round.");

            var generator = new CurveSegment(
                new Circle3d(centre, x1, pole, radius), startElevation, Math.PI / 2);
            var surface = new RevolvedSurface(generator, centre, pole, sweep);
            return new BrepFace(surface, [new BrepLoop(Chain(arcs, vertex))]);
        }

        /// <summary>Orders the corner's arcs end-to-start. Each arc's SENSE is already
        /// forced by the band that shares it, so a chain that closes is the only possible
        /// loop — and its winding is then correct by the same construction.</summary>
        private static IReadOnlyList<BrepCoedge> Chain(List<BrepCoedge> arcs, BrepVertex vertex)
        {
            var ordered = new List<BrepCoedge> { arcs[0] };
            var remaining = arcs.Skip(1).ToList();
            while (remaining.Count > 0)
            {
                var tail = ordered[^1].EndVertex;
                int next = remaining.FindIndex(c => ReferenceEquals(c.StartVertex, tail));
                if (next < 0)
                    throw new InvalidOperationException(
                        $"The corner patch at {vertex.Position} does not chain — its incident bands disagree.");
                ordered.Add(remaining[next]);
                remaining.RemoveAt(next);
            }
            if (!ReferenceEquals(ordered[^1].EndVertex, ordered[0].StartVertex))
                throw new InvalidOperationException($"The corner patch at {vertex.Position} does not close.");
            return ordered;
        }

        private static Dictionary<BrepFace, (Vector3d Origin, Vector3d Normal)> ValidateAndPlanes(BrepSolid solid)
        {
            var planes = new Dictionary<BrepFace, (Vector3d, Vector3d)>();
            foreach (var face in solid.Faces)
            {
                if (face.IsReversed || face.Loops.Count != 1 || !face.IsPlanar(out var origin, out var normal))
                    throw new NotSupportedException(
                        "Filleting every edge needs a convex polyhedron: all faces planar, hole-free and " +
                        "forward-oriented.");
                planes[face] = (origin, normal.Normalized());
            }
            foreach (var edge in solid.Edges)
            {
                if (edge.Uses.Count != 2 || edge.Curve.Underlying is not Line3d)
                    throw new NotSupportedException("Filleting every edge needs straight, two-manifold edges.");
                if (!solid.IsConvex(edge))
                    throw new NotSupportedException(
                        $"The edge from {edge.Curve.PointAt(edge.Domain.Start)} to " +
                        $"{edge.Curve.PointAt(edge.Domain.End)} is not convex; whole-solid filleting is the " +
                        "convex opening, which cannot round a concave edge.");
            }
            return planes;
        }

        private static Dictionary<BrepVertex, List<BrepFace>> VertexIncidence(BrepSolid solid)
        {
            var incidence = new Dictionary<BrepVertex, List<BrepFace>>();
            foreach (var face in solid.Faces)
            {
                foreach (var coedge in face.OuterLoop.Coedges)
                {
                    if (!incidence.TryGetValue(coedge.StartVertex, out var faces))
                        incidence[coedge.StartVertex] = faces = [];
                    faces.Add(face);
                }
            }
            foreach (var (vertex, faces) in incidence)
            {
                if (faces.Count != 3)
                    throw new NotSupportedException(
                        $"The vertex at {vertex.Position} joins {faces.Count} faces; whole-solid filleting " +
                        "handles three-valent vertices (one spherical patch bounded by three arcs).");
            }
            return incidence;
        }

        /// <summary>Where a vertex's three face planes meet after each is shifted inward
        /// by the radius — the corner sphere's centre.</summary>
        private static Vector3d ErodedVertex(
            BrepVertex vertex, IReadOnlyList<BrepFace> faces,
            Dictionary<BrepFace, (Vector3d Origin, Vector3d Normal)> planes, double radius)
        {
            var (o0, n0) = planes[faces[0]];
            var (o1, n1) = planes[faces[1]];
            var (o2, n2) = planes[faces[2]];
            double d0 = n0.Dot(o0) - radius, d1 = n1.Dot(o1) - radius, d2 = n2.Dot(o2) - radius;

            // Cramer over the 3x3 normal matrix; the determinant is the normals' scalar
            // triple product, so this is a scale-free degeneracy test on unit vectors.
            double determinant = n0.Dot(n1.Cross(n2));
            if (Math.Abs(determinant) < 1e-12)
                throw new NotSupportedException(
                    $"The three faces at {vertex.Position} are nearly coplanar, so the corner has no centre.");
            return (n1.Cross(n2) * d0 + n2.Cross(n0) * d1 + n0.Cross(n1) * d2) / determinant;
        }
    }

    /// <summary>
    /// Fillets a closed circular edge between a planar cap and a cylindrical band with
    /// the given radius. The cap shrinks, the band shortens, and an exact quarter-torus
    /// (a revolved circular arc) joins them. Returns the new solid; untouched faces are
    /// reused (the input solid is consumed).
    /// </summary>
    public static BrepSolid FilletEdge(BrepSolid solid, BrepEdge edge, double radius)
    {
        if (radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius));
        if (!edge.IsClosedEdge || edge.Curve.Underlying is not Circle3d)
            throw new NotSupportedException("Only closed circular edges can be filleted yet.");
        if (edge.Uses.Count != 2)
            throw new ArgumentException("The edge must be interior to a solid.", nameof(edge));
        // Geometry from the edge itself — wrapped curves (translated extrusion tops)
        // have Underlying circles at the wrong height.
        var rim = ActualCircle(edge);

        var capUse = edge.Uses.FirstOrDefault(u => u.Loop.Face.Surface is PlaneSurface);
        var bandUse = edge.Uses.FirstOrDefault(u => u.Loop.Face.IsCylindrical(out _, out _, out _));
        if (capUse is null || bandUse is null)
            throw new NotSupportedException("Filleting expects the edge to join a planar cap and a cylindrical band.");

        var cap = (PlaneSurface)capUse.Loop.Face.Surface;
        var bandFace = bandUse.Loop.Face;
        bandFace.IsCylindrical(out _, out var bandAxis, out _);

        var axis = bandAxis.Normalized();
        var capNormal = cap.Normal.Normalized();
        if (!capNormal.IsParallelTo(axis, new Tolerance(1e-9, 1e-6)))
            throw new NotSupportedException("The cap must be perpendicular to the band's axis.");
        var outward = capNormal; // outward normal of the cap, ±axis

        double bigRadius = rim.Radius;
        if (radius >= bigRadius)
            throw new ArgumentOutOfRangeException(nameof(radius), "Fillet radius must be smaller than the rim radius.");

        // Band length check: distance to the band's far ring.
        var farLoop = bandFace.Loops.First(l => !l.Coedges.Contains(bandUse));
        if (farLoop.Coedges is not [{ Edge: { Curve.Underlying: Circle3d } farEdge }])
            throw new NotSupportedException("The band must be bounded by two circular rings.");
        var farCircle = ActualCircle(farEdge);
        double bandLength = Math.Abs((rim.Center - farCircle.Center).Dot(axis));
        if (radius >= bandLength)
            throw new ArgumentOutOfRangeException(nameof(radius), "Fillet radius exceeds the band length.");

        // Geometry: arc center sits radius inward both radially and axially.
        var radial = rim.XDirection;
        var around = axis.Cross(radial);
        var arcCircle = new Circle3d(
            rim.Center + radial * (bigRadius - radius) - outward * radius,
            radial, outward, radius);
        var arc = new CurveSegment(arcCircle, 0, Math.PI / 2); // band tangent → cap tangent

        var bandRing = new Circle3d(rim.Center - outward * radius, radial, around, bigRadius);
        var capRing = new Circle3d(rim.Center, radial, around, bigRadius - radius);
        var bandSeam = new BrepVertex(bandRing.PointAt(0));
        var capSeam = new BrepVertex(capRing.PointAt(0));
        var bandRingEdge = new BrepEdge(bandRing, new Interval(0, 2 * Math.PI), bandSeam, bandSeam);
        var capRingEdge = new BrepEdge(capRing, new Interval(0, 2 * Math.PI), capSeam, capSeam);

        // Sense bookkeeping: the new rings wind about +axis; the old rim wound about
        // ±axis. Preserve each face's traversal orientation, and give the torus the
        // opposite uses.
        int oldWinding = Math.Sign(rim.Axis.Dot(axis));
        bool bandSense = oldWinding > 0 ? bandUse.SameSense : !bandUse.SameSense;
        bool capSense = oldWinding > 0 ? capUse.SameSense : !capUse.SameSense;

        bandUse.Loop.ReplaceCoedge(bandUse, [new BrepCoedge(bandRingEdge, bandSense)]);
        capUse.Loop.ReplaceCoedge(capUse, [new BrepCoedge(capRingEdge, capSense)]);

        var torus = new BrepFace(
            new RevolvedSurface(arc, rim.Center, axis),
            [
                new BrepLoop([new BrepCoedge(bandRingEdge, !bandSense)]),
                new BrepLoop([new BrepCoedge(capRingEdge, !capSense)]),
            ]);

        // Domain-driven band surfaces (extruded/revolved) must be trimmed to the
        // shortened extent — their grids ignore the loops.
        var trimmedBand = TrimNeighborBand(bandFace, bandRing.PointAt(0));
        var faces = solid.Faces
            .Select(f => ReferenceEquals(f, bandFace) && trimmedBand is not null ? trimmedBand : f)
            .ToList();
        faces.Add(torus);
        return new BrepSolid([new BrepShell(faces)]);
    }

    /// <summary>Circle geometry recovered from an edge's actual points (circumcenter of
    /// three samples), immune to wrapper curves whose Underlying sits elsewhere. The
    /// frame starts at the edge's start point; the axis follows increasing parameter.</summary>
    private static Circle3d ActualCircle(BrepEdge edge)
    {
        var domain = edge.Domain;
        var p0 = edge.Curve.PointAt(domain.Start);
        var p1 = edge.Curve.PointAt(domain.ParameterAt(1.0 / 3));
        var p2 = edge.Curve.PointAt(domain.ParameterAt(2.0 / 3));

        var u = p1 - p0;
        var v = p2 - p0;
        var plane = u.Cross(v);
        double planeLengthSq = plane.LengthSquared;
        // (1e-12)² floor on the squared cross product — collinear-sample degeneracy
        // guard in area² units, not the model-unit tolerance.
        if (planeLengthSq < 1e-24)
            throw new NotSupportedException("Degenerate circular edge.");
        // Circumcenter: p0 + (|v|²(plane×u) + |u|²(v×plane)) / (2|plane|²).
        var center = p0 + (plane.Cross(u) * v.LengthSquared + v.Cross(plane) * u.LengthSquared) / (2 * planeLengthSq);
        double radius = center.DistanceTo(p0);
        var axis = plane.Normalized(); // winding of p0→p1→p2 = increasing parameter
        var x = (p0 - center).Normalized();
        return new Circle3d(center, x, axis.Cross(x), radius);
    }

    /// <summary>
    /// Rebuilds a neighbor band face whose surface is domain-driven in the tessellator
    /// (extruded/revolved) so its extent ends at the lowered rim. Loop-driven surfaces
    /// (planes, cylinders) return null — no trim needed.
    /// </summary>
    private static BrepFace? TrimNeighborBand(BrepFace neighbor, in Vector3d loweredRimPoint)
    {
        switch (neighbor.Surface)
        {
            case ExtrudedSurface extruded:
            {
                if (!extruded.TryProjectPoint(loweredRimPoint, out var uv, FaceGeometry.InverseEvaluationTolerance))
                    throw new NotSupportedException("Could not locate the lowered rim on a neighbor band.");
                double vLow = uv.Y;
                Surface trimmed = vLow > 0.5
                    ? new ExtrudedSurface(extruded.Generator, extruded.Direction * vLow)
                    : new ExtrudedSurface(
                        extruded.Generator.Transformed(Matrix4d.CreateTranslation(extruded.Direction * vLow)),
                        extruded.Direction * (1 - vLow));
                return new BrepFace(trimmed, [.. neighbor.Loops], neighbor.IsReversed);
            }
            case RevolvedSurface revolved when revolved.Generator.Underlying is Line3d:
            {
                if (!revolved.TryProjectPoint(loweredRimPoint, out var uv, FaceGeometry.InverseEvaluationTolerance))
                    throw new NotSupportedException("Could not locate the lowered rim on a neighbor band.");
                var domain = revolved.Generator.Domain;
                bool rimAtEnd = Math.Abs(uv.Y - domain.End) < Math.Abs(uv.Y - domain.Start);
                var trimmedGenerator = rimAtEnd
                    ? new CurveSegment(revolved.Generator, domain.Start, uv.Y)
                    : new CurveSegment(revolved.Generator, uv.Y, domain.End);
                return new BrepFace(
                    new RevolvedSurface(trimmedGenerator, revolved.AxisOrigin, revolved.AxisDirection, revolved.Angle),
                    [.. neighbor.Loops], neighbor.IsReversed);
            }
            default:
                return null;
        }
    }

    /// <summary>Shared machinery for rim chamfer/fillet topology surgery. All new rim
    /// edges are built in the top face's traversal direction, which fixes every sense:
    /// the shrunk top loop uses them forward, neighbors backward, and each band is
    /// [top backward, corner forward, bottom forward, next-corner backward].</summary>
    private static class RimSurgeon
    {
        public static BrepSolid Apply(BrepSolid solid, BrepFace face, double top, double side, bool fillet)
        {
            if (!face.IsPlanar(out _, out var normal))
                throw new NotSupportedException("Rim features apply to planar faces.");
            if (face.IsReversed)
                throw new NotSupportedException("Rim features on reversed faces are not supported yet.");

            var outer = OuterLoop(face);
            if (outer.Coedges is [{ Edge: { IsClosedEdge: true } rimEdge }]
                && rimEdge.Curve.Underlying is Circle3d)
            {
                return fillet
                    ? FilletEdge(solid, rimEdge, top)
                    : ChamferCircularRim(solid, face, rimEdge, top, side, normal);
            }
            return PolygonRim(solid, face, outer, top, side, fillet, normal, topLaw: null, sideRatio: 1);
        }

        /// <summary>Variable-setback chamfer entry: the law anchors at rim corners; a
        /// full circular rim (no corners) is accepted only when the law is constant
        /// around it, where it reduces to the exact cone band.</summary>
        public static BrepSolid ApplyVariable(
            BrepSolid solid, BrepFace face, Func<Vector3d, double> setbackAt, double sideRatio)
        {
            if (!face.IsPlanar(out _, out var normal))
                throw new NotSupportedException("Rim features apply to planar faces.");
            if (face.IsReversed)
                throw new NotSupportedException("Rim features on reversed faces are not supported yet.");

            var outer = OuterLoop(face);
            if (outer.Coedges is [{ Edge: { IsClosedEdge: true } rimEdge }]
                && rimEdge.Curve.Underlying is Circle3d)
            {
                var domain = rimEdge.Domain;
                double reference = setbackAt(rimEdge.Curve.PointAt(domain.Start));
                for (int i = 1; i < 8; i++)
                {
                    double sample = setbackAt(rimEdge.Curve.PointAt(domain.ParameterAt(i / 8.0)));
                    // 1e-9 weld tier: "constant in effect" means the cone band's rings
                    // land where an exactly-constant law would put them.
                    if (Math.Abs(sample - reference) > Tolerance.Default.Linear)
                        throw new NotSupportedException(
                            "A variable setback on a full circular rim has no exact form (a circle offset " +
                            "by a varying amount is a spiral, not a circle). Use a constant setback, or a " +
                            "rim with corners for the law to anchor to.");
                }
                if (reference <= 0 || !double.IsFinite(reference))
                    throw new ArgumentOutOfRangeException(nameof(setbackAt),
                        $"The setback law must be positive and finite on the rim; it returned {reference}.");
                return ChamferCircularRim(solid, face, rimEdge, reference, reference * sideRatio, normal);
            }
            return PolygonRim(solid, face, outer, 0, 0, fillet: false, normal, setbackAt, sideRatio);
        }

        private static BrepLoop OuterLoop(BrepFace face)
        {
            BrepLoop? best = null;
            double bestArea = -1;
            foreach (var loop in face.Loops)
            {
                var points = new List<Vector3d>();
                foreach (var coedge in loop.Coedges)
                {
                    var domain = coedge.Edge.Domain;
                    for (int i = 0; i < 16; i++)
                    {
                        double f = coedge.SameSense ? i / 16.0 : 1 - i / 16.0;
                        points.Add(coedge.Edge.Curve.PointAt(domain.ParameterAt(f)));
                    }
                }
                double nx = 0, ny = 0, nz = 0;
                for (int i = 0; i < points.Count; i++)
                {
                    var p = points[i];
                    var q = points[(i + 1) % points.Count];
                    nx += (p.Y - q.Y) * (p.Z + q.Z);
                    ny += (p.Z - q.Z) * (p.X + q.X);
                    nz += (p.X - q.X) * (p.Y + q.Y);
                }
                double area = new Vector3d(nx, ny, nz).Length;
                if (area > bestArea)
                {
                    bestArea = area;
                    best = loop;
                }
            }
            return best ?? throw new InvalidOperationException("Face has no loops.");
        }

        // ---- circular rims (chamfer): exact cone band ----

        private static BrepSolid ChamferCircularRim(
            BrepSolid solid, BrepFace capFace, BrepEdge rimEdge, double top, double side, in Vector3d capNormal)
        {
            var rim = ActualCircle(rimEdge); // wrapper-immune (translated extrusion tops)
            var capUse = rimEdge.Uses.First(u => ReferenceEquals(u.Loop.Face, capFace));
            var bandUse = rimEdge.Uses.First(u => !ReferenceEquals(u.Loop.Face, capFace));
            if (!bandUse.Loop.Face.IsCylindrical(out _, out var bandAxis, out _)
                || !bandAxis.IsParallelTo(capNormal, Tolerance.Default))
                throw new NotSupportedException("A circular rim's neighbor must be a coaxial cylindrical band.");
            if (top >= rim.Radius)
                throw new ArgumentOutOfRangeException(nameof(top), "Chamfer exceeds the rim radius.");

            var outward = capNormal;
            var radial = rim.XDirection;
            var around = outward.Cross(radial);

            var capRing = new Circle3d(rim.Center, radial, around, rim.Radius - top);
            var bandRing = new Circle3d(rim.Center - outward * side, radial, around, rim.Radius);
            var capSeam = new BrepVertex(capRing.PointAt(0));
            var bandSeam = new BrepVertex(bandRing.PointAt(0));
            var capRingEdge = new BrepEdge(capRing, new Interval(0, 2 * Math.PI), capSeam, capSeam);
            var bandRingEdge = new BrepEdge(bandRing, new Interval(0, 2 * Math.PI), bandSeam, bandSeam);

            int oldWinding = Math.Sign(rim.Axis.Dot(outward));
            bool capSense = oldWinding > 0 ? capUse.SameSense : !capUse.SameSense;
            bool bandSense = oldWinding > 0 ? bandUse.SameSense : !bandUse.SameSense;
            capUse.Loop.ReplaceCoedge(capUse, [new BrepCoedge(capRingEdge, capSense)]);
            bandUse.Loop.ReplaceCoedge(bandUse, [new BrepCoedge(bandRingEdge, bandSense)]);

            // Generator runs band → cap (bottom → top) so ∂u × ∂v points outward,
            // matching FilletEdge's quarter-torus convention.
            var slant = new Line3d(bandRing.PointAt(0), capRing.PointAt(0));
            var cone = new BrepFace(
                new RevolvedSurface(slant, rim.Center, outward),
                [
                    new BrepLoop([new BrepCoedge(capRingEdge, !capSense)]),
                    new BrepLoop([new BrepCoedge(bandRingEdge, !bandSense)]),
                ]);

            var bandFace = bandUse.Loop.Face;
            var trimmedBand = TrimNeighborBand(bandFace, bandRing.PointAt(0));
            var faces = solid.Faces
                .Select(f => ReferenceEquals(f, bandFace) && trimmedBand is not null ? trimmedBand : f)
                .ToList();
            faces.Add(cone);
            return new BrepSolid([new BrepShell(faces)]);
        }

        // ---- polygonal / tangent-continuous rims ----

        private sealed record RimEdgeInfo(
            BrepCoedge Use, BrepEdge Edge, BrepFace Neighbor, BrepCoedge NeighborUse,
            Vector3d Start, Vector3d End, Vector3d DownDir, Circle3d? Arc, double ArcStart, double ArcSweep);

        private static BrepSolid PolygonRim(
            BrepSolid solid, BrepFace face, BrepLoop outer,
            double top, double side, bool fillet, in Vector3d normal,
            Func<Vector3d, double>? topLaw, double sideRatio)
        {
            var up = normal;
            int n = outer.Coedges.Count;
            if (n < 2)
                throw new NotSupportedException("Rim features need a multi-edge rim or a full circle.");

            var edges = new List<RimEdgeInfo>(n);
            foreach (var use in outer.Coedges)
                edges.Add(EdgeInfoFor(use, fillet, up));

            for (int i = 0; i < n; i++)
            {
                if (edges[i].End.DistanceTo(edges[(i + 1) % n].Start) > 1e-9)
                    throw new InvalidOperationException(
                        $"Rim loop does not chain: edge {i} ends at {edges[i].End} but edge {(i + 1) % n} starts at {edges[(i + 1) % n].Start}.");
            }

            // Per-corner setbacks: corner i is the START of edge i. A null law is the
            // uniform case and fills the arrays with the caller's constants, so every
            // shared expression below computes bit-identical values to the pre-law code.
            bool variable = topLaw is not null;
            var topAt = new double[n];
            var sideAt = new double[n];
            for (int i = 0; i < n; i++)
            {
                topAt[i] = topLaw is null ? top : topLaw(edges[i].Start);
                if (topAt[i] <= 0 || !double.IsFinite(topAt[i]))
                    throw new ArgumentOutOfRangeException(nameof(topLaw),
                        $"The setback law must be positive and finite at every rim corner; " +
                        $"it returned {topAt[i]} at {edges[i].Start}.");
                sideAt[i] = topLaw is null ? side : topAt[i] * sideRatio;
            }
            if (variable)
            {
                for (int i = 0; i < n; i++)
                {
                    if (edges[i].Arc is null)
                        continue;
                    int next = (i + 1) % n;
                    // Weld tier: the concentric offset arc exists only for one value.
                    if (Math.Abs(topAt[i] - topAt[next]) > Tolerance.Default.Linear)
                        throw new NotSupportedException(
                            $"An arc rim edge's setback must be constant along the arc (a circle offset by " +
                            $"a varying amount is a spiral, not a circle); the law returned {topAt[i]} and " +
                            $"{topAt[next]} at the ends of the arc starting at {edges[i].Start}.");
                }
            }

            // Interior loops (holes) must stay clear of the band: the shrunk boundary
            // may not cut through them.
            foreach (var holeLoop in face.Loops.Where(l => !ReferenceEquals(l, outer)))
            {
                foreach (var coedge in holeLoop.Coedges)
                {
                    var domain = coedge.Edge.Domain;
                    for (int s = 0; s < 16; s++)
                    {
                        var point = coedge.Edge.Curve.PointAt(domain.ParameterAt(s / 16.0));
                        double clearance = edges.Min(e =>
                        {
                            double best = double.PositiveInfinity;
                            var rimDomain = e.Edge.Domain;
                            for (int r = 0; r <= 16; r++)
                                best = Math.Min(best,
                                    point.DistanceTo(e.Edge.Curve.PointAt(rimDomain.ParameterAt(r / 16.0))));
                            return best;
                        });
                        if (clearance < topAt.Max() - 1e-9)
                            throw new NotSupportedException(
                                "The rim feature would cut into an interior loop (hole); reduce the size or move the holes inward.");
                    }
                }
            }

            // Corner classification. Tangent-continuous corners share one offset point
            // and blend with a circular junction arc; sharp corners MITER — two
            // equal-radius quarter cylinders whose axes intersect form a bicylinder,
            // whose branch through the corner's inset top point and dropped bottom point
            // is an exact ellipse. An arc rim edge meeting anything sharply would pair a
            // torus with a cylinder, whose intersection is not a conic.
            var mitered = new bool[n];
            for (int i = 0; i < n; i++)
            {
                var previous = edges[(i + n - 1) % n];
                mitered[i] = !IsSmoothCorner(previous, edges[i], up);
                if (!mitered[i])
                    continue;
                if (fillet && (previous.Arc is not null || edges[i].Arc is not null))
                    throw new NotSupportedException(
                        $"The rim corner at {edges[i].Start} joins an arc sharply, and that blend is " +
                        "a torus-cylinder intersection — not a conic, so there is no exact miter to build (straight-" +
                        "straight corners miter on the exact bicylinder ellipse). Three exact ways out: " +
                        "make the rim tangent-continuous there (enlarge the arc until it meets the " +
                        "neighbours tangentially, or add a corner arc to the sketch); chamfer this face " +
                        "instead (straight strips and cone bands miter); or accept an approximate blend " +
                        "explicitly via the implicit route (Shape.From(shape.ToImplicit())). A traced, " +
                        "chordal corner curve is deliberately not built here: it would bake a fixed " +
                        "sampling floor into a primary feature's B-Rep.");
                if (variable && (previous.Arc is not null || edges[i].Arc is not null))
                    throw new NotSupportedException(
                        "A variable chamfer's sharp corners must join two straight edges: the miter of a " +
                        "tilted inset line against a concentric offset arc is not the arc's own endpoint, " +
                        "so the corner has no exact closure. Make the rim tangent-continuous there, or use " +
                        "a constant setback.");
            }

            var topPoints = new Vector3d[n];
            var bottomPoints = new Vector3d[n];
            for (int i = 0; i < n; i++)
            {
                var previous = edges[(i + n - 1) % n];
                var current = edges[i];
                var corner = current.Start;
                topPoints[i] = variable
                    ? VariableTopCorner(previous, current, corner,
                        topAt[(i + n - 1) % n], topAt[i], topAt[(i + 1) % n], up, mitered[i])
                    : TopOffsetCorner(previous, current, corner, top, up, mitered[i]);

                var dropA = corner + previous.DownDir * sideAt[i];
                var dropB = corner + current.DownDir * sideAt[i];
                if (dropA.DistanceTo(dropB) > 1e-9)
                    throw new NotSupportedException(
                        "Rim corners must descend consistently on both neighbors (uniform side geometry).");
                bottomPoints[i] = dropB;
            }

            // A miter that runs past the far corner has consumed the edge: the shrunk top
            // loop would fold back on itself and the band would invert. Reject that here,
            // where the offending edge can still be named, rather than deep in
            // tessellation.
            for (int i = 0; i < n; i++)
            {
                int next = (i + 1) % n;
                if (!mitered[i] && !mitered[next])
                    continue;
                var tangent = (edges[i].End - edges[i].Start).Normalized();
                if ((topPoints[next] - topPoints[i]).Dot(tangent) <= Tolerance.Default.Linear)
                    throw new ArgumentOutOfRangeException(nameof(top),
                        $"The rim feature consumes the edge from {edges[i].Start} to {edges[i].End}: " +
                        "its mitered corner offsets cross. Reduce the size.");
            }

            var topVertices = topPoints.Select(p => new BrepVertex(p)).ToArray();
            var bottomVertices = bottomPoints.Select(p => new BrepVertex(p)).ToArray();
            var topEdges = new BrepEdge[n];
            var bottomEdges = new BrepEdge[n];
            var cornerEdges = new BrepEdge[n];
            for (int i = 0; i < n; i++)
            {
                int next = (i + 1) % n;
                var info = edges[i];
                topEdges[i] = OffsetEdge(info, topPoints[i], topPoints[next], topAt[i], up, atTop: true,
                    topVertices[i], topVertices[next]);
                bottomEdges[i] = OffsetEdge(info, bottomPoints[i], bottomPoints[next], sideAt[i], up, atTop: false,
                    bottomVertices[i], bottomVertices[next]);
            }
            for (int i = 0; i < n; i++)
            {
                cornerEdges[i] = fillet
                    ? JunctionCurve(topPoints[i], bottomPoints[i], top, up, topVertices[i], bottomVertices[i])
                    : new BrepEdge(new Line3d(topPoints[i], bottomPoints[i]), Interval.Unit,
                        topVertices[i], bottomVertices[i]);
            }

            // Shrunk top face (holes untouched).
            var newOuter = new BrepLoop([.. Enumerable.Range(0, n).Select(i => new BrepCoedge(topEdges[i], true))]);
            var newFace = new BrepFace(face.Surface,
                [newOuter, .. face.Loops.Where(l => !ReferenceEquals(l, outer))]);

            // Lower the neighbors' rim edges (they traverse opposite the top face).
            for (int i = 0; i < n; i++)
                edges[i].NeighborUse.Loop.ReplaceCoedge(edges[i].NeighborUse,
                    [new BrepCoedge(bottomEdges[i], false)]);

            // Shorten the side edges descending from the old corners.
            var shortened = new Dictionary<BrepEdge, BrepEdge>();
            for (int i = 0; i < n; i++)
            {
                var oldCorner = edges[i].Start;
                foreach (var use in edges[i].Neighbor.Loops.SelectMany(l => l.Coedges))
                {
                    var e = use.Edge;
                    if (shortened.ContainsKey(e) || bottomEdges.Contains(e))
                        continue;
                    bool atStart = e.Curve.PointAt(e.Domain.Start).DistanceTo(oldCorner) < 1e-9;
                    bool atEnd = e.Curve.PointAt(e.Domain.End).DistanceTo(oldCorner) < 1e-9;
                    if (!atStart && !atEnd)
                        continue;
                    if (e.Curve.Underlying is not Line3d)
                        throw new NotSupportedException("Side edges at rim corners must be straight.");
                    var farPoint = e.Curve.PointAt(atStart ? e.Domain.End : e.Domain.Start);
                    var farVertex = atStart ? e.EndVertex : e.StartVertex;
                    if (farPoint.DistanceTo(bottomPoints[i]) < 1e-9)
                        throw new NotSupportedException("The side setback consumes an entire neighbor edge.");
                    shortened[e] = atStart
                        ? new BrepEdge(new Line3d(bottomPoints[i], farPoint), Interval.Unit, bottomVertices[i], farVertex)
                        : new BrepEdge(new Line3d(farPoint, bottomPoints[i]), Interval.Unit, farVertex, bottomVertices[i]);
                }
            }
            foreach (var anyFace in solid.Faces)
            {
                foreach (var loop in anyFace.Loops)
                {
                    foreach (var use in loop.Coedges.ToList())
                    {
                        if (shortened.TryGetValue(use.Edge, out var replacement))
                            loop.ReplaceCoedge(use, [new BrepCoedge(replacement, use.SameSense)]);
                    }
                }
            }

            // Band faces.
            var bands = new List<BrepFace>(n);
            for (int i = 0; i < n; i++)
            {
                int next = (i + 1) % n;
                var loop = new BrepLoop(
                [
                    new BrepCoedge(topEdges[i], false),
                    new BrepCoedge(cornerEdges[i], true),
                    new BrepCoedge(bottomEdges[i], true),
                    new BrepCoedge(cornerEdges[next], false),
                ]);
                bands.Add(new BrepFace(
                    BandSurface(edges[i], topPoints[i], bottomPoints[i], topPoints[next], bottomPoints[next],
                        top, up, fillet, mitered[i], mitered[next], variable), [loop]));
            }

            // Domain-driven neighbor surfaces must be trimmed to the lowered extent. A
            // VARIABLE side drop tilts the lowered rim, so the trim must keep the
            // HIGHEST remaining corner (trimming at a lower one would cut the surface
            // below its own loop); for a uniform drop every corner is that corner, and
            // the first is the pre-law behavior verbatim.
            var trims = new Dictionary<BrepFace, BrepFace>();
            if (variable)
            {
                var highest = new Dictionary<BrepFace, Vector3d>();
                for (int i = 0; i < n; i++)
                {
                    int next = (i + 1) % n;
                    foreach (var candidate in new[] { bottomPoints[i], bottomPoints[next] })
                    {
                        if (!highest.TryGetValue(edges[i].Neighbor, out var current)
                            || candidate.Dot(up) > current.Dot(up))
                            highest[edges[i].Neighbor] = candidate;
                    }
                }
                foreach (var (neighbor, point) in highest)
                {
                    if (TrimNeighborBand(neighbor, point) is { } trimmedNeighbor)
                        trims[neighbor] = trimmedNeighbor;
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    if (!trims.ContainsKey(edges[i].Neighbor)
                        && TrimNeighborBand(edges[i].Neighbor, bottomPoints[i]) is { } trimmedNeighbor)
                        trims[edges[i].Neighbor] = trimmedNeighbor;
                }
            }

            var faces = solid.Faces
                .Where(f => !ReferenceEquals(f, face))
                .Select(f => trims.GetValueOrDefault(f, f))
                .ToList();
            faces.Add(newFace);
            faces.AddRange(bands);
            return new BrepSolid([new BrepShell(faces)]);
        }

        // ---- helpers ----

        /// <summary>Per-rim-edge geometry and validation, shared verbatim by the closed
        /// rim path and the open (partial-run) path: neighbor resolution, traversal
        /// endpoints, the descent direction into the neighbor, and wrapper-immune arc
        /// geometry with the traversal-ordered span.</summary>
        private static RimEdgeInfo EdgeInfoFor(BrepCoedge use, bool fillet, in Vector3d up)
        {
            var edge = use.Edge;
            if (edge.Uses.Count != 2)
                throw new NotSupportedException("Rim edges must be interior (two uses).");
            var neighborUse = edge.Uses.First(u => !ReferenceEquals(u, use));
            var neighbor = neighborUse.Loop.Face;
            var start = edge.Curve.PointAt(use.SameSense ? edge.Domain.Start : edge.Domain.End);
            var end = edge.Curve.PointAt(use.SameSense ? edge.Domain.End : edge.Domain.Start);

            Vector3d downDir;
            Circle3d? arc = null;
            double arcStart = 0, arcSweep = 0;
            if (edge.Curve.Underlying is Line3d)
            {
                if (!neighbor.IsPlanar(out _, out var sideNormal))
                    throw new NotSupportedException("Straight rim edges need planar neighbor faces.");
                if (fillet && Math.Abs(sideNormal.Dot(up)) > 1e-6)
                    throw new NotSupportedException("Fillet rims need neighbors perpendicular to the face.");
                var raw = -up + sideNormal * up.Dot(sideNormal);
                if (!raw.TryNormalize(Tolerance.Default, out downDir))
                    throw new NotSupportedException("A rim neighbor is parallel to the face.");
            }
            else if (edge.Curve.Underlying is Circle3d)
            {
                if (!neighbor.IsCylindrical(out _, out var axis, out _)
                    || !axis.IsParallelTo(up, Tolerance.Default))
                    throw new NotSupportedException("Arc rim edges need coaxial cylindrical neighbor faces.");
                downDir = -up;
                var actual = ActualCircle(edge); // wrapper-immune geometry
                arc = actual;
                // start/end are already traversal-ordered and the midpoint is
                // direction-agnostic, so this span follows the loop directly.
                (arcStart, arcSweep) = ArcSpan(actual, start, end,
                    edge.Curve.PointAt(edge.Domain.Mid));
            }
            else
            {
                throw new NotSupportedException(
                    "Rim edges must be lines or circular arcs (straighten or round other curves first).");
            }

            return new RimEdgeInfo(use, edge, neighbor, neighborUse, start, end, downDir, arc, arcStart, arcSweep);
        }

        /// <summary>Signed angular span of an arc edge measured in the circle's own
        /// frame, resolved through the edge's midpoint (handles &gt; π sweeps).</summary>
        private static (double Start, double Sweep) ArcSpan(
            Circle3d circle, in Vector3d start, in Vector3d end, in Vector3d mid)
        {
            double Angle(in Vector3d p)
            {
                var offset = p - circle.Center;
                return Math.Atan2(offset.Dot(circle.YDirection), offset.Dot(circle.XDirection));
            }
            double s = Angle(start);
            double ccwToEnd = Wrap(Angle(end) - s);
            double ccwToMid = Wrap(Angle(mid) - s);
            double sweep = ccwToMid <= ccwToEnd + 1e-9 ? ccwToEnd : ccwToEnd - 2 * Math.PI;
            return (s, sweep);

            static double Wrap(double a) => a - 2 * Math.PI * Math.Floor(a / (2 * Math.PI));
        }

        /// <summary>Whether an arc rim edge curves convexly about the face normal
        /// (material inside the arc ⇒ offsets shrink its radius).</summary>
        private static bool ConvexAboutUp(RimEdgeInfo info, in Vector3d up) =>
            info.Arc is { } arc && arc.Axis.Dot(up) * Math.Sign(info.ArcSweep) > 0;

        private static Vector3d TangentAtStart(RimEdgeInfo info)
        {
            var t = info.Edge.Curve.TangentAt(info.Use.SameSense ? info.Edge.Domain.Start : info.Edge.Domain.End);
            return info.Use.SameSense ? t : -t;
        }

        private static Vector3d TangentAtEnd(RimEdgeInfo info)
        {
            var t = info.Edge.Curve.TangentAt(info.Use.SameSense ? info.Edge.Domain.End : info.Edge.Domain.Start);
            return info.Use.SameSense ? t : -t;
        }

        /// <summary>Whether consecutive rim edges leave the corner tangent-continuous, so
        /// one inward offset point serves both. The 1e-6 band is the angular-agreement
        /// tier: exactly-tangent sketch corners agree far better, and anything a designer
        /// meant as a corner is orders of magnitude coarser.</summary>
        private static bool IsSmoothCorner(RimEdgeInfo previous, RimEdgeInfo current, in Vector3d up) =>
            up.Cross(TangentAtEnd(previous)).Normalized()
                .Cross(up.Cross(TangentAtStart(current)).Normalized()).Length < 1e-6;

        private static Vector3d TopOffsetCorner(
            RimEdgeInfo previous, RimEdgeInfo current, in Vector3d corner, double amount, in Vector3d up, bool miter)
        {
            var inPrev = up.Cross(TangentAtEnd(previous)).Normalized();   // interior is left of travel
            var inCurrent = up.Cross(TangentAtStart(current)).Normalized();
            if (!miter)
            {
                // Arc corners offset exactly along the radial — finite-difference
                // tangents carry ~1e-9 angular error, which is enough to rotate band
                // generators past the weld tolerance at scale.
                var arcInfo = current.Arc is not null ? current : previous.Arc is not null ? previous : null;
                if (arcInfo?.Arc is Circle3d arc)
                {
                    double newRadius = arc.Radius + (ConvexAboutUp(arcInfo, up) ? -amount : amount);
                    return arc.Center + (corner - arc.Center) * (newRadius / arc.Radius);
                }
                return corner + inCurrent * amount;
            }

            // Miter: intersect the offset lines of the two edges within the top plane.
            var d1 = TangentAtEnd(previous);
            var p1 = corner + inPrev * amount;
            var d2 = TangentAtStart(current);
            var p2 = corner + inCurrent * amount;
            var w = p2 - p1;
            double a = d1.Dot(d1), b = d1.Dot(d2), c = d2.Dot(d2);
            double d = d1.Dot(w), e = d2.Dot(w);
            double denominator = a * c - b * b;
            // Gram-determinant parallelism guard (unit-tangent scale, so ~angle²):
            // round-off cutoff, not the model-unit tolerance.
            if (Math.Abs(denominator) < 1e-15)
                throw new NotSupportedException("Degenerate rim corner (parallel edges).");
            return p1 + d1 * ((d * c - b * e) / denominator);
        }

        /// <summary>
        /// Top corner point under a per-corner setback law. Smooth corners share one
        /// perpendicular offset (or the arc's exact radial); mitered corners intersect
        /// the two edges' inset TOP LINES — each through the perpendicular offsets at
        /// its own two corners, so a linearly varying setback tilts the line but keeps
        /// it straight, and the miter stays an exact line–line intersection in the top
        /// plane.
        /// </summary>
        private static Vector3d VariableTopCorner(
            RimEdgeInfo previous, RimEdgeInfo current, in Vector3d corner,
            double prevCornerAmount, double amount, double nextCornerAmount, in Vector3d up, bool miter)
        {
            var inPrev = up.Cross(TangentAtEnd(previous)).Normalized();
            var inCurrent = up.Cross(TangentAtStart(current)).Normalized();
            if (!miter)
            {
                // Arc corners offset exactly along the radial — the same rule as the
                // uniform path, with this corner's own value.
                var arcInfo = current.Arc is not null ? current : previous.Arc is not null ? previous : null;
                if (arcInfo?.Arc is Circle3d arc)
                {
                    double newRadius = arc.Radius + (ConvexAboutUp(arcInfo, up) ? -amount : amount);
                    return arc.Center + (corner - arc.Center) * (newRadius / arc.Radius);
                }
                return corner + inCurrent * amount;
            }

            // Straight-edge inward normals at the FAR corners: for a line the tangent is
            // constant along the edge, so the end normals reuse the corner ones.
            var p1 = corner + inPrev * amount;
            var d1 = (p1 - (previous.Start + inPrev * prevCornerAmount)).Normalized();
            var p2 = corner + inCurrent * amount;
            var d2 = ((current.End + inCurrent * nextCornerAmount) - p2).Normalized();
            var w = p2 - p1;
            double a = d1.Dot(d1), b = d1.Dot(d2), c = d2.Dot(d2);
            double d = d1.Dot(w), e = d2.Dot(w);
            double denominator = a * c - b * b;
            // Gram-determinant parallelism guard (unit-tangent scale, so ~angle²):
            // round-off cutoff, not the model-unit tolerance.
            if (Math.Abs(denominator) < 1e-15)
                throw new NotSupportedException("Degenerate rim corner (parallel inset lines).");
            return p1 + d1 * ((d * c - b * e) / denominator);
        }

        /// <summary>New rim edge (top: inset; bottom: dropped), built in traversal
        /// direction — lines directly, arcs as concentric trimmed circles.</summary>
        private static BrepEdge OffsetEdge(
            RimEdgeInfo info, in Vector3d from, in Vector3d to, double amount, in Vector3d up, bool atTop,
            BrepVertex fromVertex, BrepVertex toVertex)
        {
            if (info.Arc is Circle3d arc)
            {
                double newRadius = atTop ? arc.Radius + (ConvexAboutUp(info, up) ? -amount : amount) : arc.Radius;
                if (newRadius <= 1e-9)
                    throw new ArgumentOutOfRangeException(nameof(amount), "The offset consumes an arc radius.");
                var center = atTop ? arc.Center : arc.Center - up * amount;
                var concentric = new Circle3d(center, arc.XDirection, arc.YDirection, newRadius);
                var curve = new CurveSegment(concentric, info.ArcStart, info.ArcStart + info.ArcSweep);
                return new BrepEdge(curve, Interval.Unit, fromVertex, toVertex);
            }
            return new BrepEdge(new Line3d(from, to), Interval.Unit, fromVertex, toVertex);
        }

        /// <summary>
        /// Fillet junction curve at a corner, built top → bottom (matching the loop
        /// convention and the extruded band generators' sampling).
        /// <para>At a tangent-continuous corner the two bands share one cross-section: a
        /// quarter circle of the fillet radius.</para>
        /// <para>At a sharp corner between straight edges the two bands are equal-radius
        /// quarter cylinders whose axes cross at <c>center</c> (both axes sit one radius
        /// below the top plane, on the edges' inward offset lines, and those lines meet at
        /// the mitered top point). Two equal-radius cylinders with intersecting axes meet
        /// in two ellipses; the branch through this corner has semi-axes <c>up·r</c> and
        /// <c>bottom − center</c> — perpendicular by construction, since the drop is
        /// straight down and the miter offset is horizontal — so the exact conic is read
        /// straight off the two points the surgery already computed, with no trigonometry
        /// to round off. The circular case is literally the |bottom − center| = r
        /// specialization; it keeps its own rational arc so existing rims stay
        /// bit-identical.</para>
        /// </summary>
        private static BrepEdge JunctionCurve(
            in Vector3d topPoint, in Vector3d bottomPoint, double radius, in Vector3d up,
            BrepVertex topVertex, BrepVertex bottomVertex)
        {
            var center = topPoint - up * radius;
            var toBottom = bottomPoint - center;
            if (Math.Abs(toBottom.Length - radius) <= Tolerance.Default.Linear)
            {
                var arc = NurbsCurve.Arc(center, up, toBottom.Normalized(), radius, 0, Math.PI / 2);
                return new BrepEdge(arc, arc.Domain, topVertex, bottomVertex);
            }

            var ellipse = new Ellipse3d(center, up * radius, toBottom);
            return new BrepEdge(ellipse, new Interval(0, Math.PI / 2), topVertex, bottomVertex);
        }

        private static Surface BandSurface(
            RimEdgeInfo info, in Vector3d topPoint, in Vector3d bottomPoint,
            in Vector3d topNext, in Vector3d bottomNext,
            double amount, in Vector3d up, bool fillet, bool startMitered, bool endMitered,
            bool variable = false)
        {
            // Orientation notes: the tessellator's outward convention is ∂u × ∂v.
            // Revolved bands (∂u tangential along traversal) need bottom → top
            // generators; extruded bands (∂u = generator tangent, ∂v = edge) need
            // top → bottom cross-sections.
            if (info.Arc is Circle3d arc)
            {
                var axis = info.ArcSweep * arc.Axis.Dot(up) > 0 ? up : -up;
                if (!fillet)
                    return new RevolvedSurface(new Line3d(bottomPoint, topPoint), arc.Center, axis, Math.Abs(info.ArcSweep));
                var torusCenter = topPoint - up * amount;
                var torusX = (bottomPoint - torusCenter).Normalized();
                var generator = NurbsCurve.Arc(torusCenter, torusX, up, amount, 0, Math.PI / 2);
                return new RevolvedSurface(generator, arc.Center, axis, Math.Abs(info.ArcSweep));
            }

            if (!fillet)
            {
                // The strip's frame x runs along the edge for the uniform case (the
                // pre-law expression, kept verbatim) and along the TILTED top boundary
                // for a variable setback — the strip is still an exact plane, because a
                // constant top:side ratio keeps the four corner points coplanar
                // (s0·d1 = d0·s1), but the edge direction itself leaves that plane.
                var x = variable
                    ? (topNext - topPoint).Normalized()
                    : (info.End - info.Start).Normalized();
                var toBottom = bottomPoint - topPoint;
                var y = (toBottom - x * toBottom.Dot(x)).Normalized();
                var strip = new PlaneSurface(topPoint, x, y);
                // Flip the frame if the normal came out inward (rim strips face upward).
                return strip.Normal.Dot(up) < 0 ? new PlaneSurface(topPoint, x, -y) : strip;
            }

            // Quarter-cylinder band: the axis runs one radius inside the top face and one
            // radius inside the (perpendicular) side face. A tangent-continuous start
            // anchors the generator on the shared offset point, which for an arc neighbor
            // was derived EXACTLY from that arc's radial — finite-difference tangents
            // carry ~1e-9 of angular error, enough to rotate the generator past the weld
            // tolerance. A mitered start has no such shared point, so the generator sits
            // on this edge's own perpendicular inset instead.
            var travel = info.End - info.Start;
            var tangent = travel.Normalized();
            var arcCenter = (startMitered ? info.Start + up.Cross(tangent).Normalized() * amount : topPoint)
                - up * amount;
            var arcY = (bottomPoint - arcCenter).Normalized();

            if (!startMitered && !endMitered)
            {
                // Untouched path: the loop covers the whole parameter rectangle, so the
                // face still tessellates on the natural grid. Keep the extent verbatim.
                var full = NurbsCurve.Arc(arcCenter, up, arcY, amount, 0, Math.PI / 2);
                return new ExtrudedSurface(full, travel);
            }

            // Mitered ends cut across the parallelogram, so the face is genuinely trimmed
            // — but the surface's grid is domain-driven and must still SPAN every loop
            // point. Convex miters stop exactly at the original corners (the drop is
            // straight down), so both extensions are exactly zero and the direction stays
            // bit-identical to the edge's own vector; a reflex corner pushes the mitered
            // top point past the edge end and the band grows to reach it.
            double sEnd = travel.Dot(tangent);
            double sMin = 0, sMax = sEnd;
            foreach (var point in new[] { topPoint, topNext, bottomPoint, bottomNext })
            {
                double s = (point - arcCenter).Dot(tangent);
                sMin = Math.Min(sMin, s);
                sMax = Math.Max(sMax, s);
            }
            double extendStart = Math.Max(0, -sMin);
            double extendEnd = Math.Max(0, sMax - sEnd);
            var crossSection = NurbsCurve.Arc(
                arcCenter - tangent * extendStart, up, arcY, amount, 0, Math.PI / 2);
            return new ExtrudedSurface(crossSection, travel + tangent * (extendStart + extendEnd));
        }
    }
}
