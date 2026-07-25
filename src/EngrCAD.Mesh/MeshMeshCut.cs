using EngrCAD.Core;
using EngrCAD.Core.Spatial;

namespace EngrCAD.Mesh;

/// <summary>
/// Two meshes cut along their exact common intersection curve — the input of an
/// imprint-style boolean (see <see cref="MeshBoolean"/> with
/// <see cref="BooleanMethod.Exact"/>). Both meshes carry the SAME seam: every
/// intersection point exists as a vertex of both, at bit-identical coordinates, and
/// every segment exists as an edge of both. No face of either mesh straddles the other
/// mesh's surface any more, so face-level inside/outside classification is well posed.
/// </summary>
public sealed class MeshImprint
{
    private IReadOnlyList<IReadOnlyList<int>>? _polylines;

    internal MeshImprint(
        HalfEdgeMesh meshA, HalfEdgeMesh meshB, Vector3d[] points, (int Start, int End)[] segments)
    {
        MeshA = meshA;
        MeshB = meshB;
        Points = points;
        Segments = segments;
    }

    /// <summary>The first mesh, triangulated and cut along the intersection curve.</summary>
    public HalfEdgeMesh MeshA { get; }

    /// <summary>The second mesh, triangulated and cut along the same curve.</summary>
    public HalfEdgeMesh MeshB { get; }

    /// <summary>
    /// Intersection-curve vertices. Each is a vertex of both <see cref="MeshA"/> and
    /// <see cref="MeshB"/> at exactly these coordinates — the weld invariant the boolean
    /// relies on (the two sides join by equality, not by tolerance).
    /// </summary>
    public IReadOnlyList<Vector3d> Points { get; }

    /// <summary>
    /// Intersection segments as index pairs into <see cref="Points"/>; each is an edge of
    /// both meshes. Order within a pair is unspecified; pairs are unique and undirected.
    /// </summary>
    public IReadOnlyList<(int Start, int End)> Segments { get; }

    /// <summary>Total length of the intersection curve.</summary>
    public double Length
    {
        get
        {
            double total = 0;
            foreach (var (s, e) in Segments)
                total += Points[s].DistanceTo(Points[e]);
            return total;
        }
    }

    /// <summary>
    /// The segments chained into polylines. A closed loop repeats its first index as the
    /// last element; an open chain (only possible for open input meshes) does not.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<int>> Polylines => _polylines ??= ChainPolylines();

    private IReadOnlyList<IReadOnlyList<int>> ChainPolylines()
    {
        var adjacency = new Dictionary<int, List<int>>();
        foreach (var (s, e) in Segments)
        {
            if (!adjacency.TryGetValue(s, out var listS))
                adjacency[s] = listS = [];
            listS.Add(e);
            if (!adjacency.TryGetValue(e, out var listE))
                adjacency[e] = listE = [];
            listE.Add(s);
        }

        var used = new HashSet<(int, int)>();
        var chains = new List<IReadOnlyList<int>>();

        // Open ends first (degree 1), so open chains come out whole rather than as two
        // halves discovered from an interior point.
        var starts = adjacency.Where(kv => kv.Value.Count != 2).Select(kv => kv.Key)
            .Concat(adjacency.Keys).ToList();
        foreach (int start in starts)
        {
            foreach (int first in adjacency[start])
            {
                if (used.Contains(Key(start, first)))
                    continue;
                var chain = new List<int> { start };
                int previous = start, current = first;
                while (true)
                {
                    used.Add(Key(previous, current));
                    chain.Add(current);
                    if (current == start)
                        break; // closed loop
                    int next = -1;
                    foreach (int candidate in adjacency[current])
                    {
                        if (candidate != previous && !used.Contains(Key(current, candidate)))
                        {
                            next = candidate;
                            break;
                        }
                    }
                    if (next < 0)
                        break; // open end
                    previous = current;
                    current = next;
                }
                chains.Add(chain);
            }
        }
        return chains;

        static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);
    }
}

/// <summary>
/// Exact mesh–mesh intersection: triangle–triangle segments plus the imprint that makes
/// those segments real edges of both meshes.
/// <para>
/// Broad phase is <see cref="Bvh.QueryOverlap"/> over per-triangle boxes; the narrow
/// phase is the Möller interval test — signed distances of each triangle's corners to the
/// other's plane, the two triangles' intervals on the common line, and the overlap of the
/// two intervals. Every intersection point is therefore <em>one mesh edge crossing one
/// face plane of the other mesh</em>; computing it from the edge's lower-indexed endpoint
/// makes the value depend only on (edge, plane), so the two faces sharing that edge — and
/// the two meshes — get bit-identical coordinates for it. That, not a weld tolerance, is
/// what makes both sides of the seam agree.
/// </para>
/// <para>
/// All degeneracy tests are relative to the operands' extent (the scale-free tier of the
/// epsilon ladder), never the absolute 1e-9 weld tier: the BSP path's absolute constants
/// are exactly why it drops small geometry and cracks on near-tangent pairs.
/// </para>
/// <para>
/// Coplanar overlapping faces are <em>rejected</em>, not approximated: an overlap region
/// has no intersection curve to imprint and its faces sit on the other solid's surface,
/// where the winding number is undefined (exactly ½). Callers with mating coplanar faces
/// should nudge one operand or use <see cref="BooleanMethod.Bsp"/>.
/// </para>
/// </summary>
public static class MeshMeshCut
{
    /// <summary>
    /// Relative degeneracy guard. Plane distances are evaluated as dot products of
    /// coordinate-sized quantities, so their floating-point floor is ~1e-16 times the
    /// operand extent; 1e-13 keeps three decades of margin while staying four decades
    /// below the 1e-9 weld tier at unit scale. Scale-free by construction: multiplying
    /// both meshes by any factor produces the same decisions.
    /// </summary>
    internal const double RelativeEpsilon = 1e-13;

    /// <summary>
    /// Cuts both meshes along their exact intersection curve. Inputs are triangulated
    /// first; the result's two meshes share the seam vertex-for-vertex.
    /// </summary>
    /// <exception cref="NotSupportedException">Faces of the two meshes overlap while
    /// coplanar (see the type remarks).</exception>
    /// <exception cref="InvalidOperationException">The imprint could not be realized;
    /// both meshes are left untouched (the edit is transactional).</exception>
    public static MeshImprint Imprint(HalfEdgeMesh a, HalfEdgeMesh b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var triA = a.Triangulated();
        var triB = b.Triangulated();
        var meshA = new CutOperand(triA);
        var meshB = new CutOperand(triB);

        double scale = Math.Max(Extent(meshA), Extent(meshB));
        if (scale <= 0)
            throw new ArgumentException("Boolean operands are degenerate (zero extent).");
        double epsilon = RelativeEpsilon * scale;

        var pairs = new List<(int Item, int OtherItem)>();
        Bvh.Build(meshA.Boxes).QueryOverlap(Bvh.Build(meshB.Boxes), pairs);

        var points = new SeamPointTable(epsilon);
        var planA = new ImprintPlan();
        var planB = new ImprintPlan();
        var segments = new HashSet<(int, int)>();
        int coplanarOverlaps = 0;

        Span<Crossing> crossA = stackalloc Crossing[3];
        Span<Crossing> crossB = stackalloc Crossing[3];
        foreach (var (fa, fb) in pairs)
        {
            var outcome = Intersect(meshA, fa, meshB, fb, epsilon, points, crossA, crossB,
                out var start, out var end);
            if (outcome == PairOutcome.CoplanarOverlap)
            {
                coplanarOverlaps++;
                continue;
            }
            if (outcome != PairOutcome.Segment)
                continue;

            Register(planA, planB, meshA, fa, meshB, fb, start);
            Register(planA, planB, meshA, fa, meshB, fb, end);
            planA.AddSegment(fa, start.Point, end.Point);
            planB.AddSegment(fb, start.Point, end.Point);
            segments.Add(start.Point < end.Point
                ? (start.Point, end.Point)
                : (end.Point, start.Point));
        }

        if (coplanarOverlaps > 0)
            throw new NotSupportedException(
                $"Exact mesh boolean: {coplanarOverlaps} face pair(s) overlap while coplanar. " +
                "A coplanar overlap has no intersection curve to imprint and its faces lie on " +
                "the other solid's surface, where inside/outside is undefined. Offset one " +
                "operand past the shared plane, or use BooleanMethod.Bsp.");

        var positions = points.Points;
        SnapToExistingVertices(meshA, planA, meshB, planB, positions, epsilon);
        var cutA = MeshImprinter.Imprint(triA, planA, positions, epsilon, "first");
        var cutB = MeshImprinter.Imprint(triB, planB, positions, epsilon, "second");

        // The table also interns crossings that lost the interval overlap; the reported
        // curve is only what became a segment.
        var used = new Dictionary<int, int>();
        var seam = new List<Vector3d>();
        var curve = new (int, int)[segments.Count];
        int next = 0;
        foreach (var (s, e) in segments)
            curve[next++] = (Intern(s), Intern(e));
        return new MeshImprint(cutA, cutB, [.. seam], curve);

        int Intern(int point)
        {
            if (used.TryGetValue(point, out int index))
                return index;
            used[point] = seam.Count;
            seam.Add(positions[point]);
            return seam.Count - 1;
        }
    }

    /// <summary>
    /// Resolves seam points that fall on top of an existing vertex — a crossing can land
    /// within the degeneracy guard of a corner of the face it pierces, and poking there
    /// would breed a zero-area sliver. Both meshes must agree on the outcome or the seam
    /// stops welding: the point takes the claiming vertex's exact coordinates, and if the
    /// OTHER mesh also has a vertex there, that vertex is moved onto the same coordinates
    /// (a displacement bounded by the relative degeneracy guard — the only geometry this
    /// algorithm ever perturbs, and the price of a seam that welds by equality).
    /// </summary>
    private static void SnapToExistingVertices(
        CutOperand meshA, ImprintPlan planA, CutOperand meshB, ImprintPlan planB,
        List<Vector3d> points, double epsilon)
    {
        var hostsA = HostFaces(planA);
        var hostsB = HostFaces(planB);
        foreach (var (point, faces) in hostsA)
            Resolve(point, faces, hostsB.GetValueOrDefault(point));
        foreach (var (point, faces) in hostsB)
        {
            if (!hostsA.ContainsKey(point))
                Resolve(point, null, faces);
        }

        static Dictionary<int, List<int>> HostFaces(ImprintPlan plan)
        {
            var map = new Dictionary<int, List<int>>();
            foreach (var (face, ids) in plan.FacePoints)
            {
                foreach (int id in ids)
                {
                    if (!map.TryGetValue(id, out var list))
                        map[id] = list = [];
                    list.Add(face);
                }
            }
            return map;
        }

        void Resolve(int point, List<int>? facesA, List<int>? facesB)
        {
            int vertexA = FindCorner(meshA, facesA, points[point], epsilon);
            int vertexB = FindCorner(meshB, facesB, points[point], epsilon);
            if (vertexA < 0 && vertexB < 0)
                return;
            var canonical = vertexA >= 0 ? meshA.Positions[vertexA] : meshB.Positions[vertexB];
            points[point] = canonical;
            if (vertexA >= 0)
                planA.MapToVertex(point, vertexA);
            if (vertexB >= 0)
            {
                planB.MapToVertex(point, vertexB);
                if (vertexA >= 0 && meshB.Positions[vertexB] != canonical)
                    planB.MoveVertex(vertexB, canonical);
            }
        }

        static int FindCorner(CutOperand mesh, List<int>? faces, in Vector3d p, double epsilon)
        {
            if (faces is null)
                return -1;
            foreach (int face in faces)
            {
                foreach (int corner in mesh.Triangles[face])
                {
                    if (mesh.Positions[corner].DistanceSquaredTo(p) <= epsilon * epsilon)
                        return corner;
                }
            }
            return -1;
        }
    }

    private static double Extent(CutOperand mesh)
    {
        var box = mesh.Bounds;
        return box.IsEmpty ? 0 : box.Size.Length;
    }

    // Records where one segment endpoint sits on each mesh: on an edge of the mesh that
    // produced it (exact position and parameter known), and somewhere on the other mesh's
    // face (classified against that face when the imprint runs).
    private static void Register(
        ImprintPlan planA, ImprintPlan planB,
        CutOperand meshA, int faceA, CutOperand meshB, int faceB, in Crossing crossing)
    {
        var own = crossing.FromA ? planA : planB;
        var other = crossing.FromA ? planB : planA;
        var ownMesh = crossing.FromA ? meshA : meshB;
        int otherFace = crossing.FromA ? faceB : faceA;

        if (crossing.Vertex >= 0)
            own.MapToVertex(crossing.Point, ownMesh.Triangles[crossing.FromA ? faceA : faceB][crossing.Vertex]);
        else
            own.AddEdgePoint(crossing.HalfEdge, crossing.Parameter, crossing.Point);
        other.AddFacePoint(otherFace, crossing.Point);
    }

    private enum PairOutcome
    {
        None,
        Segment,
        CoplanarOverlap,
    }

    /// <summary>One endpoint of a triangle's crossing of the other triangle's plane.</summary>
    private readonly record struct Crossing(
        int Point,        // index into the shared seam point table
        double Along,     // projection on the intersection line, for interval overlap
        bool FromA,       // the crossing lies on an edge (or vertex) of the first mesh
        int HalfEdge,     // half-edge of that mesh carrying the point (-1 when Vertex >= 0)
        double Parameter, // position along that half-edge, measured from its origin
        int Vertex);      // corner 0..2 of the triangle when the point IS a mesh vertex, else -1

    private static PairOutcome Intersect(
        CutOperand meshA, int faceA, CutOperand meshB, int faceB, double epsilon,
        SeamPointTable points, Span<Crossing> bufferA, Span<Crossing> bufferB,
        out Crossing start, out Crossing end)
    {
        start = default;
        end = default;

        var triA = meshA.Triangles[faceA];
        var triB = meshB.Triangles[faceB];
        if (!meshA.TryPlane(faceA, out var normalA, out var originA) ||
            !meshB.TryPlane(faceB, out var normalB, out var originB))
            return PairOutcome.None; // sliver face: contributes no seam

        Span<double> distA = stackalloc double[3];
        Span<double> distB = stackalloc double[3];
        int aboveA = 0, belowA = 0, onA = 0;
        for (int i = 0; i < 3; i++)
        {
            distA[i] = normalB.Dot(meshA.Positions[triA[i]] - originB);
            if (distA[i] > epsilon) aboveA++;
            else if (distA[i] < -epsilon) belowA++;
            else onA++;
        }
        if (aboveA == 3 || belowA == 3)
            return PairOutcome.None;

        int aboveB = 0, belowB = 0, onB = 0;
        for (int i = 0; i < 3; i++)
        {
            distB[i] = normalA.Dot(meshB.Positions[triB[i]] - originA);
            if (distB[i] > epsilon) aboveB++;
            else if (distB[i] < -epsilon) belowB++;
            else onB++;
        }
        if (aboveB == 3 || belowB == 3)
            return PairOutcome.None;

        if (onA == 3 || onB == 3)
        {
            return CoplanarTrianglesOverlap(meshA, triA, meshB, triB, normalA, originA, epsilon)
                ? PairOutcome.CoplanarOverlap
                : PairOutcome.None; // coplanar but only touching along a shared edge/corner
        }

        // Direction of the two planes' common line. Both triangles' crossings project onto
        // it monotonically, which turns the 3D overlap test into a 1D interval overlap.
        var direction = normalA.Cross(normalB);
        double directionLength = direction.Length;
        if (directionLength <= RelativeEpsilon)
            return PairOutcome.None; // parallel planes, and not coplanar (checked above)
        direction /= directionLength;

        int countA = PlaneCrossings(meshA, faceA, distA, epsilon, direction, fromA: true, points, bufferA);
        if (countA < 2)
            return PairOutcome.None; // the triangle only touches the other plane at a point
        int countB = PlaneCrossings(meshB, faceB, distB, epsilon, direction, fromA: false, points, bufferB);
        if (countB < 2)
            return PairOutcome.None;

        var lowA = bufferA[0].Along <= bufferA[1].Along ? bufferA[0] : bufferA[1];
        var highA = bufferA[0].Along <= bufferA[1].Along ? bufferA[1] : bufferA[0];
        var lowB = bufferB[0].Along <= bufferB[1].Along ? bufferB[0] : bufferB[1];
        var highB = bufferB[0].Along <= bufferB[1].Along ? bufferB[1] : bufferB[0];

        start = lowA.Along >= lowB.Along ? lowA : lowB;
        end = highA.Along <= highB.Along ? highA : highB;
        if (end.Along - start.Along <= epsilon)
            return PairOutcome.None; // disjoint intervals, or a touch too short to matter
        return PairOutcome.Segment;
    }

    /// <summary>
    /// The (at most two) points where a triangle meets a plane, given its corners' signed
    /// distances. Corners within <paramref name="epsilon"/> of the plane are reported as
    /// themselves; otherwise a sign change along an edge produces the exact crossing,
    /// always computed from the edge's lower-indexed endpoint so the value depends only on
    /// the undirected edge and the plane.
    /// </summary>
    private static int PlaneCrossings(
        CutOperand mesh, int face, ReadOnlySpan<double> dist, double epsilon,
        in Vector3d direction, bool fromA, SeamPointTable points, Span<Crossing> buffer)
    {
        var triangle = mesh.Triangles[face];
        int count = 0;
        for (int i = 0; i < 3 && count < 2; i++)
        {
            if (Math.Abs(dist[i]) <= epsilon)
            {
                var p = mesh.Positions[triangle[i]];
                buffer[count++] = new Crossing(
                    points.Add(p), direction.Dot(p), fromA, HalfEdge: -1, Parameter: 0, Vertex: i);
            }
        }
        for (int i = 0; i < 3 && count < 2; i++)
        {
            int j = (i + 1) % 3;
            if (Math.Abs(dist[i]) <= epsilon || Math.Abs(dist[j]) <= epsilon)
                continue;
            if (dist[i] > 0 == dist[j] > 0)
                continue;

            // Canonical evaluation: lower vertex index first, so the two faces sharing this
            // edge — and both meshes — see the same bits for this point.
            bool forward = triangle[i] < triangle[j];
            int lo = forward ? i : j, hi = forward ? j : i;
            var pLo = mesh.Positions[triangle[lo]];
            var pHi = mesh.Positions[triangle[hi]];
            double t = dist[lo] / (dist[lo] - dist[hi]);
            var p = pLo + (pHi - pLo) * t;
            // This face's half-edge for corner pair (i, j) runs i → j, so the parameter has
            // to be re-referenced when the canonical evaluation started at j.
            buffer[count++] = new Crossing(
                points.Add(p), direction.Dot(p), fromA,
                HalfEdge: mesh.HalfEdge(face, i),
                Parameter: forward ? t : 1 - t,
                Vertex: -1);
        }
        return count;
    }

    /// <summary>
    /// True when two coplanar triangles share interior area (as opposed to merely touching
    /// along an edge or at a corner, which is harmless and common between neighbouring
    /// solids).
    /// </summary>
    private static bool CoplanarTrianglesOverlap(
        CutOperand meshA, int[] triA, CutOperand meshB, int[] triB,
        in Vector3d normal, in Vector3d origin, double epsilon)
    {
        var (ex, ey) = Basis(normal);
        Span<Vector2d> a = stackalloc Vector2d[3];
        Span<Vector2d> b = stackalloc Vector2d[3];
        for (int i = 0; i < 3; i++)
        {
            a[i] = Project(meshA.Positions[triA[i]] - origin, ex, ey);
            b[i] = Project(meshB.Positions[triB[i]] - origin, ex, ey);
        }
        // Separating-axis test on the six edge normals; a positive gap (beyond epsilon on
        // either triangle's scale) means no shared area. Touching exactly (gap 0) is not
        // an overlap.
        return !Separated(a, b, epsilon) && !Separated(b, a, epsilon);

        static bool Separated(ReadOnlySpan<Vector2d> polygon, ReadOnlySpan<Vector2d> other, double epsilon)
        {
            double winding = Predicates2d.Orient2d(polygon[0], polygon[1], polygon[2]);
            double sign = winding >= 0 ? 1 : -1;
            for (int i = 0; i < 3; i++)
            {
                int j = (i + 1) % 3;
                bool allOutside = true;
                for (int k = 0; k < 3; k++)
                {
                    if (sign * Predicates2d.Orient2d(polygon[i], polygon[j], other[k]) > -epsilon * epsilon)
                    {
                        allOutside = false;
                        break;
                    }
                }
                if (allOutside)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// A right-handed 2D basis for a unit face normal (the codebase's single
    /// arbitrary-perpendicular convention). Only ever applied to unit normals, so the
    /// tolerance in the perpendicular's normalization is never in play.
    /// </summary>
    internal static (Vector3d X, Vector3d Y) Basis(in Vector3d normal)
    {
        var x = normal.ArbitraryPerpendicular(Tolerance.Default);
        return (x, normal.Cross(x));
    }

    internal static Vector2d Project(in Vector3d v, in Vector3d ex, in Vector3d ey) =>
        new(v.Dot(ex), v.Dot(ey));

    /// <summary>Triangulated operand: positions, corner indices, per-face boxes and planes.</summary>
    private sealed class CutOperand
    {
        private readonly int[] _faceStart; // first half-edge of each face's loop

        public CutOperand(HalfEdgeMesh mesh)
        {
            var (positions, faces) = mesh.ToIndexed();
            Positions = positions;
            Triangles = [.. faces];
            Boxes = new Aabb[Triangles.Length];
            _faceStart = new int[Triangles.Length];
            var bounds = Aabb.Empty;
            for (int f = 0; f < Triangles.Length; f++)
            {
                var tri = Triangles[f];
                if (tri.Length != 3)
                    throw new ArgumentException("Exact mesh cutting operates on triangulated meshes.");
                Boxes[f] = Aabb.Empty
                    .Union(positions[tri[0]]).Union(positions[tri[1]]).Union(positions[tri[2]]);
                bounds = bounds.Union(Boxes[f]);
                _faceStart[f] = mesh.GetFace(f).AnyHalfEdge.Index;
            }
            Bounds = bounds;
        }

        public Vector3d[] Positions { get; }
        public int[][] Triangles { get; }
        public Aabb[] Boxes { get; }
        public Aabb Bounds { get; }

        /// <summary>The half-edge leaving corner <paramref name="corner"/> of the face.</summary>
        public int HalfEdge(int face, int corner) => _faceStart[face] + corner;

        public bool TryPlane(int face, out Vector3d normal, out Vector3d origin)
        {
            var tri = Triangles[face];
            origin = Positions[tri[0]];
            var ab = Positions[tri[1]] - origin;
            var ac = Positions[tri[2]] - origin;
            var cross = ab.Cross(ac);
            double reference = Math.Max(ab.LengthSquared, Math.Max(ac.LengthSquared, (ac - ab).LengthSquared));
            double length = cross.Length;
            if (length <= RelativeEpsilon * reference)
            {
                normal = default;
                return false;
            }
            normal = cross / length;
            return true;
        }
    }

    /// <summary>
    /// Interns intersection points so both meshes reference identical coordinates. The
    /// canonical (edge, plane) evaluation already makes repeated computations bit-equal;
    /// the hash also collapses the rare point reached from both meshes' edges at once.
    /// </summary>
    private sealed class SeamPointTable(double tolerance)
    {
        private readonly List<Vector3d> _points = [];
        private readonly Dictionary<(long, long, long), List<int>> _cells = [];

        public List<Vector3d> Points => _points;

        public int Add(in Vector3d p)
        {
            var key = Cell(p);
            for (long dx = -1; dx <= 1; dx++)
            {
                for (long dy = -1; dy <= 1; dy++)
                {
                    for (long dz = -1; dz <= 1; dz++)
                    {
                        if (!_cells.TryGetValue((key.X + dx, key.Y + dy, key.Z + dz), out var bucket))
                            continue;
                        foreach (int index in bucket)
                        {
                            if (_points[index].DistanceSquaredTo(p) <= tolerance * tolerance)
                                return index;
                        }
                    }
                }
            }
            _points.Add(p);
            int id = _points.Count - 1;
            if (!_cells.TryGetValue(key, out var own))
                _cells[key] = own = [];
            own.Add(id);
            return id;
        }

        private (long X, long Y, long Z) Cell(in Vector3d p) => (
            (long)Math.Floor(p.X / tolerance),
            (long)Math.Floor(p.Y / tolerance),
            (long)Math.Floor(p.Z / tolerance));
    }
}
