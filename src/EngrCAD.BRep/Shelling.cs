using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// Offset solids and shelling (OCCT's <c>BRepOffsetAPI_MakeOffsetShape</c> /
/// <c>MakeThickSolid</c>): move every face along its own normal and re-intersect.
///
/// <para><b>What is exact.</b> For a <b>polyhedral</b> solid — all faces planar, all edges
/// straight — offsetting is exact and needs no approximation at all: an offset plane is a
/// plane, and each offset vertex is the algebraic intersection of the three planes that met
/// there. That is the whole algorithm; the topology is carried over unchanged, so holes and
/// genus survive. <see cref="Shell"/> adds the hollowing structure: the offset copy becomes
/// an inward-facing inner boundary, and each face named as an opening contributes a
/// <b>rim</b> face (the removed face's loop as its outer boundary, the inner opening as its
/// hole) instead of a wall — the classic tray or box-with-a-lid-off.</para>
///
/// <para><b>What is rejected.</b> Curved faces. Exact offsets of cylinders and surfaces of
/// revolution are within reach (a cylinder offsets to a cylinder, a revolve to the revolve
/// of an <see cref="OffsetCurve3d"/> generator), but their <i>corners</i> need
/// surface–surface re-intersection rather than a three-plane solve, so v1 refuses them
/// loudly rather than approximating. Also rejected: vertices where more than three faces
/// meet (the offset corner is over-determined), adjacent openings (their rim would have
/// zero width), multi-shell inputs, and an offset that locally folds the solid.</para>
///
/// <para><b>What is NOT checked.</b> Local folding is caught — a collapsed or reversed edge,
/// a loop that turned inside out — but a thickness larger than a distant feature can still
/// make surfaces pass through each other without any local symptom. That is the same
/// contract OCCT offers, and the same one <see cref="OffsetCurve3d"/> already documents for
/// curves.</para>
/// </summary>
public static class Shelling
{
    /// <summary>
    /// Offsets every face of a polyhedral solid by <paramref name="distance"/> along its
    /// outward normal (positive grows the solid, negative shrinks it). Corners are mitred:
    /// each vertex becomes the intersection of its three offset planes.
    /// </summary>
    public static BrepSolid Offset(BrepSolid solid, double distance)
    {
        var polyhedron = Polyhedron.Recognize(solid);
        var offsets = new double[polyhedron.Faces.Length];
        Array.Fill(offsets, distance);
        var positions = polyhedron.OffsetVertices(offsets);
        polyhedron.ValidateOffset(positions, "offset");

        var vertices = new BrepVertex[positions.Length];
        for (int i = 0; i < positions.Length; i++)
            vertices[i] = new BrepVertex(positions[i]);
        var edges = polyhedron.BuildEdges(vertices);

        var faces = new BrepFace[polyhedron.Faces.Length];
        for (int f = 0; f < faces.Length; f++)
        {
            var plane = polyhedron.OffsetPlane(f, offsets[f]);
            faces[f] = new BrepFace(
                polyhedron.PlaneSurfaceFor(f, plane, positions, flipped: false),
                polyhedron.MapLoops(f, edges, reverse: false));
        }
        return new BrepSolid([new BrepShell(faces)]);
    }

    /// <summary>
    /// Hollows a polyhedral solid to walls of <paramref name="thickness"/>. Faces the
    /// <paramref name="openingSelector"/> names are removed, opening the cavity through them;
    /// with no selector the cavity is closed and the result is a two-shell solid (a sealed
    /// void).
    /// </summary>
    /// <remarks>
    /// The inner boundary is the solid offset inward by <paramref name="thickness"/> — except
    /// through the openings, whose planes do not move, so the cavity reaches the opening
    /// exactly and the rim is a flat annulus of the wall thickness. Openings must not be
    /// adjacent: two openings sharing an edge would leave a zero-width rim there.
    /// </remarks>
    public static BrepSolid Shell(BrepSolid solid, double thickness, Func<BrepFace, bool>? openingSelector = null)
    {
        if (!(thickness > 0))
            throw new ArgumentOutOfRangeException(nameof(thickness), "Wall thickness must be positive.");
        var polyhedron = Polyhedron.Recognize(solid);
        int faceCount = polyhedron.Faces.Length;

        var opening = new bool[faceCount];
        int openings = 0;
        if (openingSelector is not null)
        {
            for (int f = 0; f < faceCount; f++)
            {
                if (!openingSelector(polyhedron.Faces[f]))
                    continue;
                if (polyhedron.Faces[f].Loops.Count != 1)
                    throw new NotSupportedException(
                        "An opening face has holes; its rim would need an annulus per hole wound the other " +
                        "way. Open a face without holes, or shell first and cut the opening as a boolean.");
                opening[f] = true;
                openings++;
            }
        }
        if (openings == faceCount)
            throw new ArgumentException("Every face was selected as an opening; nothing would be left.",
                nameof(openingSelector));
        foreach (var (a, b) in polyhedron.FacePairs)
        {
            if (opening[a] && opening[b])
                throw new NotSupportedException(
                    "Two adjacent faces were selected as openings; the rim between them would have zero " +
                    "width. Open them one at a time, or merge them into a single opening first.");
        }

        // Openings do not move: the cavity must reach exactly to the removed face's plane.
        var offsets = new double[faceCount];
        for (int f = 0; f < faceCount; f++)
            offsets[f] = opening[f] ? 0 : -thickness;
        var innerPositions = polyhedron.OffsetVertices(offsets);
        polyhedron.ValidateOffset(innerPositions, "inner");

        var outerVertices = new BrepVertex[polyhedron.Vertices.Length];
        var innerVertices = new BrepVertex[polyhedron.Vertices.Length];
        for (int i = 0; i < outerVertices.Length; i++)
        {
            outerVertices[i] = new BrepVertex(polyhedron.Vertices[i].Position);
            innerVertices[i] = new BrepVertex(innerPositions[i]);
        }
        var outerEdges = polyhedron.BuildEdges(outerVertices);
        var innerEdges = polyhedron.BuildEdges(innerVertices);

        var outerFaces = new List<BrepFace>(faceCount);
        var innerFaces = new List<BrepFace>(faceCount);
        var rimFaces = new List<BrepFace>(openings);
        var outerPositions = new Vector3d[polyhedron.Vertices.Length];
        for (int i = 0; i < outerPositions.Length; i++)
            outerPositions[i] = polyhedron.Vertices[i].Position;

        for (int f = 0; f < faceCount; f++)
        {
            if (opening[f])
            {
                // Rim: the removed face's own loops (their coedges supply the second use of
                // every edge the removed face used to carry) plus the inner opening as a
                // hole, wound opposite.
                var loops = polyhedron.MapLoops(f, outerEdges, reverse: false);
                loops.AddRange(polyhedron.MapLoops(f, innerEdges, reverse: true));
                rimFaces.Add(new BrepFace(
                    polyhedron.PlaneSurfaceFor(f, polyhedron.Planes[f], outerPositions, flipped: false), loops));
                continue;
            }

            outerFaces.Add(new BrepFace(
                polyhedron.PlaneSurfaceFor(f, polyhedron.Planes[f], outerPositions, flipped: false),
                polyhedron.MapLoops(f, outerEdges, reverse: false)));

            // The inner wall faces the cavity: same plane offset inward, normal flipped, and
            // every loop reversed to stay counter-clockwise about that flipped normal.
            var innerPlane = polyhedron.OffsetPlane(f, -thickness);
            innerFaces.Add(new BrepFace(
                polyhedron.PlaneSurfaceFor(f, innerPlane, innerPositions, flipped: true),
                polyhedron.MapLoops(f, innerEdges, reverse: true)));
        }

        // With no opening the cavity is sealed: outer boundary and void are separate shells.
        if (openings == 0)
            return new BrepSolid([new BrepShell(outerFaces), new BrepShell(innerFaces)]);

        var all = new List<BrepFace>(outerFaces.Count + innerFaces.Count + rimFaces.Count);
        all.AddRange(outerFaces);
        all.AddRange(innerFaces);
        all.AddRange(rimFaces);
        return new BrepSolid([new BrepShell(all)]);
    }

    /// <summary>
    /// The polyhedral structure offsetting needs: face planes, a vertex→(exactly three)
    /// faces map, and index tables so the topology can be rebuilt verbatim at new positions.
    /// </summary>
    private sealed class Polyhedron
    {
        public required BrepFace[] Faces { get; init; }
        public required (Vector3d Origin, Vector3d Normal)[] Planes { get; init; }
        public required BrepVertex[] Vertices { get; init; }
        public required BrepEdge[] Edges { get; init; }

        /// <summary>The three face indices meeting at each vertex.</summary>
        public required int[][] VertexFaces { get; init; }

        /// <summary>Endpoint vertex indices per edge.</summary>
        public required (int Start, int End)[] EdgeVertices { get; init; }

        /// <summary>The two face indices sharing each edge.</summary>
        public required (int A, int B)[] FacePairs { get; init; }

        public required Dictionary<BrepEdge, int> EdgeIndex { private get; init; }
        public required Dictionary<BrepVertex, int> VertexIndex { private get; init; }

        public static Polyhedron Recognize(BrepSolid solid)
        {
            ArgumentNullException.ThrowIfNull(solid);
            if (solid.Shells.Count != 1)
                throw new NotSupportedException(
                    $"Offsetting needs a single-shell solid; this one has {solid.Shells.Count} shells.");
            solid.Validate();

            var faces = solid.Faces.ToArray();
            var planes = new (Vector3d Origin, Vector3d Normal)[faces.Length];
            var faceIndex = new Dictionary<BrepFace, int>(faces.Length);
            for (int f = 0; f < faces.Length; f++)
            {
                if (!faces[f].IsPlanar(out var origin, out var normal))
                    throw new NotSupportedException(
                        $"Offsetting v1 needs an all-planar solid; a face on " +
                        $"{faces[f].Surface.GetType().Name} is not planar. Exact offsets of curved faces " +
                        "need surface-surface re-intersection at the corners and are not implemented.");
                planes[f] = (origin, normal);
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
                if (edges[e].Curve.Underlying is not Line3d)
                    throw new NotSupportedException(
                        "Offsetting v1 needs straight edges; a curved edge would need its two offset " +
                        "surfaces re-intersected rather than a three-plane solve.");
                edgeIndex[edges[e]] = e;
                edgeVertices[e] = (vertexIndex[edges[e].StartVertex], vertexIndex[edges[e].EndVertex]);
            }

            var incident = new HashSet<int>[vertices.Length];
            for (int i = 0; i < incident.Length; i++)
                incident[i] = [];
            var pairs = new HashSet<int>[edges.Length];
            for (int e = 0; e < pairs.Length; e++)
                pairs[e] = [];

            foreach (var coedge in solid.Coedges)
            {
                int f = faceIndex[coedge.Loop.Face];
                incident[vertexIndex[coedge.StartVertex]].Add(f);
                incident[vertexIndex[coedge.EndVertex]].Add(f);
                pairs[edgeIndex[coedge.Edge]].Add(f);
            }

            var vertexFaces = new int[vertices.Length][];
            for (int i = 0; i < vertices.Length; i++)
            {
                if (incident[i].Count != 3)
                    throw new NotSupportedException(
                        $"Offsetting v1 needs exactly three faces at every vertex; one vertex has " +
                        $"{incident[i].Count}. A higher-valence corner's offset position is over-determined " +
                        "(the offset planes do not meet in a point) and needs corner patches.");
                vertexFaces[i] = [.. incident[i]];
            }

            var facePairs = new (int, int)[edges.Length];
            for (int e = 0; e < edges.Length; e++)
            {
                if (pairs[e].Count != 2)
                    throw new NotSupportedException(
                        "Offsetting needs each edge to separate two distinct faces (no seam edges).");
                var both = pairs[e].ToArray();
                facePairs[e] = (both[0], both[1]);
            }

            return new Polyhedron
            {
                Faces = faces,
                Planes = planes,
                Vertices = vertices,
                Edges = edges,
                VertexFaces = vertexFaces,
                EdgeVertices = edgeVertices,
                FacePairs = facePairs,
                EdgeIndex = edgeIndex,
                VertexIndex = vertexIndex,
            };
        }

        public (Vector3d Origin, Vector3d Normal) OffsetPlane(int face, double distance) =>
            (Planes[face].Origin + Planes[face].Normal * distance, Planes[face].Normal);

        /// <summary>Each vertex moved to the intersection of its three offset planes.</summary>
        public Vector3d[] OffsetVertices(double[] offsets)
        {
            var positions = new Vector3d[Vertices.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                var faces = VertexFaces[i];
                positions[i] = IntersectPlanes(
                    OffsetPlane(faces[0], offsets[faces[0]]),
                    OffsetPlane(faces[1], offsets[faces[1]]),
                    OffsetPlane(faces[2], offsets[faces[2]]));
            }
            return positions;
        }

        /// <summary>
        /// Local sanity of an offset: no edge may collapse or reverse, and no loop may turn
        /// inside out. Global self-intersection from an over-large offset is not detected
        /// (documented on the class).
        /// </summary>
        public void ValidateOffset(Vector3d[] positions, string what)
        {
            for (int e = 0; e < Edges.Length; e++)
            {
                var (start, end) = EdgeVertices[e];
                var moved = positions[end] - positions[start];
                var original = Vertices[end].Position - Vertices[start].Position;
                if (moved.Dot(original) <= 0)
                    throw new ArgumentException(
                        $"The {what} collapses or reverses an edge: the distance exceeds what the solid's " +
                        "local size allows.");
            }
            for (int f = 0; f < Faces.Length; f++)
            {
                double area = 0;
                var loop = Faces[f].OuterLoop;
                for (int i = 0; i < loop.Coedges.Count; i++)
                {
                    var a = positions[IndexOfVertex(loop.Coedges[i].StartVertex)];
                    var b = positions[IndexOfVertex(loop.Coedges[i].EndVertex)];
                    area += a.Cross(b).Dot(Planes[f].Normal);
                }
                if (!(area > 0))
                    throw new ArgumentException(
                        $"The {what} turns a face inside out: the distance exceeds what the solid's local " +
                        "size allows.");
            }
        }

        public BrepEdge[] BuildEdges(BrepVertex[] vertices)
        {
            var edges = new BrepEdge[Edges.Length];
            for (int e = 0; e < edges.Length; e++)
            {
                var (start, end) = EdgeVertices[e];
                edges[e] = new BrepEdge(
                    new Line3d(vertices[start].Position, vertices[end].Position),
                    Interval.Unit, vertices[start], vertices[end]);
            }
            return edges;
        }

        /// <summary>
        /// The face's loops rebuilt over <paramref name="edges"/>. Reversing walks each loop
        /// backwards with flipped senses, which is exactly what a face whose normal has been
        /// flipped needs to stay counter-clockwise.
        /// </summary>
        public List<BrepLoop> MapLoops(int face, BrepEdge[] edges, bool reverse)
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

        /// <summary>
        /// A plane surface whose normal is the face's (or its negation when flipped), with X
        /// taken along the loop's first edge so no arbitrary frame enters.
        /// </summary>
        public PlaneSurface PlaneSurfaceFor(
            int face, (Vector3d Origin, Vector3d Normal) plane, Vector3d[] positions, bool flipped)
        {
            var loop = Faces[face].OuterLoop;
            var start = positions[IndexOfVertex(loop.Coedges[0].StartVertex)];
            var end = positions[IndexOfVertex(loop.Coedges[0].EndVertex)];
            var x = (end - start).Normalized();
            var normal = flipped ? -plane.Normal : plane.Normal;
            return new PlaneSurface(plane.Origin, x, normal.Cross(x));
        }

        private int IndexOfVertex(BrepVertex vertex) => VertexIndex[vertex];

        private static Vector3d IntersectPlanes(
            in (Vector3d Origin, Vector3d Normal) a,
            in (Vector3d Origin, Vector3d Normal) b,
            in (Vector3d Origin, Vector3d Normal) c)
        {
            var bc = b.Normal.Cross(c.Normal);
            double determinant = a.Normal.Dot(bc);
            // Unit normals, so the determinant is a product of sines: a scale-free
            // near-degeneracy test for "these three planes do not meet in a point".
            if (Math.Abs(determinant) < 1e-12)
                throw new NotSupportedException(
                    "Three faces meeting at a vertex are parallel or tangent, so the offset corner is not " +
                    "a point.");
            return (bc * a.Normal.Dot(a.Origin)
                  + c.Normal.Cross(a.Normal) * b.Normal.Dot(b.Origin)
                  + a.Normal.Cross(b.Normal) * c.Normal.Dot(c.Origin)) / determinant;
        }
    }
}
