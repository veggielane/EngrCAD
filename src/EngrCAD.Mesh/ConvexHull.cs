using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// 3D convex hull by quickhull (Barber–Dobkin–Huhdanpaa): initial extreme tetrahedron,
/// then repeatedly lift the farthest conflict point, remove the faces it sees, and
/// re-triangulate the horizon. Output is a closed, outward-wound triangle
/// <see cref="HalfEdgeMesh"/> whose vertices are a subset of the input points.
///
/// Robustness policy: all visibility tests use a single absolute epsilon scaled to the
/// input extent (qhull's approach). Points within epsilon of a face plane are treated
/// as NOT visible, so coplanar input points are absorbed into existing facets instead
/// of spawning sliver faces — coplanar hull regions come out as a consistent
/// triangulation of true hull vertices (a cube's face is two triangles, not a fan over
/// mid-face points). Fully degenerate inputs (fewer than 4 points, all coincident,
/// collinear, or coplanar) throw <see cref="ArgumentException"/> with the reason.
/// </summary>
public static class ConvexHull
{
    private sealed class Face
    {
        public int A, B, C;
        public Vector3d Normal;      // unit
        public double Offset;        // plane: Normal · p = Offset
        public bool Alive = true;
        public List<int>? Outside;   // conflict points strictly above this face
        public int Farthest = -1;
        public double FarthestDistance;

        public double DistanceTo(in Vector3d p) => Normal.Dot(p) - Offset;
    }

    /// <summary>Computes the convex hull of <paramref name="points"/>. See the class
    /// remarks for the epsilon policy and degeneracy behavior.</summary>
    public static HalfEdgeMesh Compute(IReadOnlyList<Vector3d> points)
    {
        if (points.Count < 4)
            throw new ArgumentException("A 3D convex hull needs at least 4 points.", nameof(points));

        // Extent-scaled tolerance: below it, a point is "on" a plane.
        var min = points[0];
        var max = points[0];
        for (int i = 1; i < points.Count; i++)
        {
            min = Vector3d.Min(min, points[i]);
            max = Vector3d.Max(max, points[i]);
        }
        double extent = (max - min).Length;
        if (extent <= Tolerance.Default.Linear)
            throw new ArgumentException("All points are coincident; the hull is degenerate.", nameof(points));
        double epsilon = Math.Max(1e-12, Tolerance.Default.Linear * extent);

        var faces = BuildInitialTetrahedron(points, epsilon);

        // Conflict assignment: each remaining point goes to one face that sees it.
        for (int i = 0; i < points.Count; i++)
            AssignToConflictList(faces, points, i, epsilon);

        var pending = new Stack<Face>(faces.Where(f => f.Outside is { Count: > 0 }));
        while (pending.Count > 0)
        {
            var seed = pending.Pop();
            if (!seed.Alive || seed.Outside is not { Count: > 0 })
                continue;
            int apex = seed.Farthest;
            var apexPoint = points[apex];

            // Visible region: every alive face the apex sees (strictly beyond epsilon).
            var visible = faces.Where(f => f.Alive && f.DistanceTo(apexPoint) > epsilon).ToList();

            // Horizon: directed edges of the visible region that don't cancel against
            // another visible face (interior edges appear in both directions).
            var directed = new HashSet<(int, int)>();
            foreach (var f in visible)
            {
                foreach (var (a, b) in FaceEdges(f))
                {
                    if (!directed.Remove((b, a)))
                        directed.Add((a, b));
                }
            }

            var orphaned = new List<int>();
            foreach (var f in visible)
            {
                f.Alive = false;
                if (f.Outside is not null)
                    orphaned.AddRange(f.Outside);
                f.Outside = null;
            }

            // One new face per horizon edge, keeping the visible side's winding so the
            // new triangle pairs correctly with the surviving neighbor and faces outward.
            var newFaces = new List<Face>(directed.Count);
            foreach (var (a, b) in directed)
            {
                var face = MakeFace(points, a, b, apex);
                if (face is null)
                    throw new InvalidOperationException(
                        "Quickhull produced a degenerate horizon triangle; the input is too ill-conditioned for the extent-scaled epsilon.");
                newFaces.Add(face);
            }
            faces.RemoveAll(f => !f.Alive);
            faces.AddRange(newFaces);

            foreach (int index in orphaned)
            {
                if (index != apex)
                    AssignToConflictList(newFaces, points, index, epsilon);
            }
            foreach (var f in newFaces)
            {
                if (f.Outside is { Count: > 0 })
                    pending.Push(f);
            }
        }

        // Compact the used vertices and hand the triangles to the manifold-validating
        // half-edge builder (a safety net for any residual inconsistency).
        var remap = new Dictionary<int, int>();
        var positions = new List<Vector3d>();
        var triangles = new List<int[]>();
        foreach (var f in faces)
        {
            triangles.Add([Remap(f.A), Remap(f.B), Remap(f.C)]);
        }
        return HalfEdgeMesh.Build(positions, triangles);

        int Remap(int index)
        {
            if (!remap.TryGetValue(index, out int compact))
            {
                compact = positions.Count;
                positions.Add(points[index]);
                remap[index] = compact;
            }
            return compact;
        }
    }

    private static IEnumerable<(int, int)> FaceEdges(Face f)
    {
        yield return (f.A, f.B);
        yield return (f.B, f.C);
        yield return (f.C, f.A);
    }

    private static Face? MakeFace(IReadOnlyList<Vector3d> points, int a, int b, int c)
    {
        var normal = (points[b] - points[a]).Cross(points[c] - points[a]);
        double length = normal.Length;
        // Underflow guard against dividing by an (effectively) exactly-zero cross product;
        // real degeneracy is decided by the extent-scaled epsilon in the visibility tests.
        if (length <= 1e-300)
            return null;
        normal /= length;
        return new Face
        {
            A = a,
            B = b,
            C = c,
            Normal = normal,
            Offset = normal.Dot(points[a]),
        };
    }

    private static void AssignToConflictList(List<Face> faces, IReadOnlyList<Vector3d> points, int index, double epsilon)
    {
        Face? best = null;
        double bestDistance = epsilon;
        foreach (var f in faces)
        {
            if (!f.Alive)
                continue;
            double d = f.DistanceTo(points[index]);
            if (d > bestDistance)
            {
                best = f;
                bestDistance = d;
            }
        }
        if (best is null)
            return; // inside (or on) the current hull: dropped
        (best.Outside ??= []).Add(index);
        if (bestDistance > best.FarthestDistance)
        {
            best.FarthestDistance = bestDistance;
            best.Farthest = index;
        }
    }

    private static List<Face> BuildInitialTetrahedron(IReadOnlyList<Vector3d> points, double epsilon)
    {
        // Extremes along the axes; the farthest extreme pair spans the base edge.
        Span<int> extremes = [0, 0, 0, 0, 0, 0];
        for (int i = 1; i < points.Count; i++)
        {
            var p = points[i];
            if (p.X < points[extremes[0]].X) extremes[0] = i;
            if (p.X > points[extremes[1]].X) extremes[1] = i;
            if (p.Y < points[extremes[2]].Y) extremes[2] = i;
            if (p.Y > points[extremes[3]].Y) extremes[3] = i;
            if (p.Z < points[extremes[4]].Z) extremes[4] = i;
            if (p.Z > points[extremes[5]].Z) extremes[5] = i;
        }
        int e0 = 0, e1 = 0;
        double bestSpan = -1;
        for (int i = 0; i < extremes.Length; i++)
        {
            for (int j = i + 1; j < extremes.Length; j++)
            {
                double d = points[extremes[i]].DistanceSquaredTo(points[extremes[j]]);
                if (d > bestSpan)
                {
                    bestSpan = d;
                    e0 = extremes[i];
                    e1 = extremes[j];
                }
            }
        }
        if (Math.Sqrt(Math.Max(bestSpan, 0)) <= epsilon)
            throw new ArgumentException("All points are (nearly) coincident; the hull is degenerate.", nameof(points));

        // Farthest point from the base line.
        var origin = points[e0];
        var direction = (points[e1] - origin).Normalized();
        int e2 = -1;
        double bestLineDistance = epsilon;
        for (int i = 0; i < points.Count; i++)
        {
            var d = points[i] - origin;
            double distance = (d - direction * d.Dot(direction)).Length;
            if (distance > bestLineDistance)
            {
                bestLineDistance = distance;
                e2 = i;
            }
        }
        if (e2 < 0)
            throw new ArgumentException("All points are collinear; the hull is degenerate.", nameof(points));

        // Farthest point from the base plane.
        var baseFace = MakeFace(points, e0, e1, e2)
            ?? throw new ArgumentException("All points are collinear; the hull is degenerate.", nameof(points));
        int e3 = -1;
        double bestPlaneDistance = epsilon;
        for (int i = 0; i < points.Count; i++)
        {
            double distance = Math.Abs(baseFace.DistanceTo(points[i]));
            if (distance > bestPlaneDistance)
            {
                bestPlaneDistance = distance;
                e3 = i;
            }
        }
        if (e3 < 0)
            throw new ArgumentException("All points are coplanar; a 3D hull is degenerate (use a 2D hull).", nameof(points));

        // Wind the four faces so every normal points away from the opposite vertex.
        int[][] triples =
        [
            [e0, e1, e2],
            [e0, e2, e3],
            [e0, e3, e1],
            [e1, e3, e2],
        ];
        int[] opposite = [e3, e1, e2, e0];
        var faces = new List<Face>(4);
        for (int i = 0; i < 4; i++)
        {
            var face = MakeFace(points, triples[i][0], triples[i][1], triples[i][2])!;
            if (face.DistanceTo(points[opposite[i]]) > 0)
                face = MakeFace(points, triples[i][0], triples[i][2], triples[i][1])!;
            faces.Add(face);
        }
        return faces;
    }
}
