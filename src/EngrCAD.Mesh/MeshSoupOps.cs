using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Shared indexed-soup helpers for the mesh readers (<see cref="StlReader"/>,
/// <see cref="ObjReader"/>, <see cref="OffReader"/>) and <see cref="MeshRepair"/>:
/// spatial-hash vertex welding, face-loop cleanup, polygon triangulation, and
/// unreferenced-vertex compaction. These operate on raw positions + index lists
/// so they work on dirty (non-manifold) data that <see cref="HalfEdgeMesh.Build"/>
/// would reject.
/// </summary>
internal static class MeshSoupOps
{
    /// <summary>
    /// Incremental spatial-hash vertex welder (same scheme as <see cref="MeshWelder"/>):
    /// <see cref="Add"/> returns the index of an existing point within
    /// <c>tolerance</c>, or appends the point unchanged. Representatives keep their
    /// exact original coordinates — welding never moves geometry.
    /// </summary>
    internal sealed class VertexWelder
    {
        private readonly double _tolerance;
        private readonly Dictionary<(long, long, long), List<int>> _cells = [];

        public List<Vector3d> Points { get; } = [];

        public VertexWelder(double tolerance)
        {
            if (tolerance <= 0)
                throw new ArgumentOutOfRangeException(nameof(tolerance), "Weld tolerance must be positive.");
            _tolerance = tolerance;
        }

        public int Add(in Vector3d p)
        {
            var key = (
                (long)Math.Floor(p.X / _tolerance),
                (long)Math.Floor(p.Y / _tolerance),
                (long)Math.Floor(p.Z / _tolerance));
            for (long dx = -1; dx <= 1; dx++)
            {
                for (long dy = -1; dy <= 1; dy++)
                {
                    for (long dz = -1; dz <= 1; dz++)
                    {
                        if (!_cells.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var bucket))
                            continue;
                        foreach (int index in bucket)
                        {
                            if (Points[index].DistanceSquaredTo(p) <= _tolerance * _tolerance)
                                return index;
                        }
                    }
                }
            }
            Points.Add(p);
            int id = Points.Count - 1;
            if (!_cells.TryGetValue(key, out var own))
                _cells[key] = own = [];
            own.Add(id);
            return id;
        }
    }

    /// <summary>
    /// Cleans one face loop after welding: collapses consecutive duplicate indices
    /// (including the wrap-around), and rejects loops left with fewer than three
    /// vertices or with a non-consecutively repeated vertex (a pinched face carries
    /// no area worth keeping — the <see cref="MeshWelder"/> rule). Returns null when
    /// the face is degenerate.
    /// </summary>
    public static int[]? CleanLoop(IReadOnlyList<int> loop)
    {
        var cleaned = new List<int>(loop.Count);
        foreach (int v in loop)
        {
            if (cleaned.Count == 0 || cleaned[^1] != v)
                cleaned.Add(v);
        }
        while (cleaned.Count > 1 && cleaned[0] == cleaned[^1])
            cleaned.RemoveAt(cleaned.Count - 1);
        if (cleaned.Count < 3)
            return null;
        if (cleaned.Distinct().Count() != cleaned.Count)
            return null;
        return [.. cleaned];
    }

    /// <summary>
    /// Triangulates one polygonal face loop in its own Newell plane via
    /// <see cref="PolygonTriangulator"/>. If earcut drops vertices (its collinear
    /// filtering), the fan fallback is used instead so every loop vertex stays
    /// referenced — a dropped vertex that a neighboring face still uses would open
    /// an unweldable T-junction crack. Returns null when the polygon is degenerate
    /// (zero Newell normal).
    /// </summary>
    public static List<int[]>? TriangulatePolygon(int[] loop, IReadOnlyList<Vector3d> positions, out bool usedFanFallback)
    {
        usedFanFallback = false;

        // Newell normal: robust for non-convex (and slightly non-planar) polygons.
        double nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < loop.Length; i++)
        {
            var p = positions[loop[i]];
            var q = positions[loop[(i + 1) % loop.Length]];
            nx += (p.Y - q.Y) * (p.Z + q.Z);
            ny += (p.Z - q.Z) * (p.X + q.X);
            nz += (p.X - q.X) * (p.Y + q.Y);
        }
        if (!new Vector3d(nx, ny, nz).TryNormalize(Tolerance.Default, out var normal))
            return null; // zero-area polygon

        var u = normal.ArbitraryPerpendicular(Tolerance.Default);
        var v = normal.Cross(u); // u x v = normal, so the loop projects CCW

        var projected = new Vector2d[loop.Length];
        for (int i = 0; i < loop.Length; i++)
        {
            var p = positions[loop[i]];
            projected[i] = (p.Dot(u), p.Dot(v));
        }

        var triangles = PolygonTriangulator.Triangulate(projected);
        var used = new bool[loop.Length];
        foreach (var (a, b, c) in triangles)
            used[a] = used[b] = used[c] = true;

        var result = new List<int[]>(loop.Length - 2);
        if (triangles.Count == loop.Length - 2 && Array.IndexOf(used, false) < 0)
        {
            foreach (var (a, b, c) in triangles)
                result.Add([loop[a], loop[b], loop[c]]);
            return result;
        }

        // Earcut filtered exactly-collinear vertices or bailed on a degenerate
        // projection; fall back to a fan, which references every vertex.
        usedFanFallback = true;
        for (int i = 1; i + 1 < loop.Length; i++)
            result.Add([loop[0], loop[i], loop[i + 1]]);
        return result;
    }

    /// <summary>
    /// Drops positions no face references and remaps face indices, returning the
    /// number of dropped vertices. Isolated vertices would fail
    /// <see cref="HalfEdgeMesh.Build"/>'s structural validation.
    /// </summary>
    public static (List<Vector3d> Positions, List<int[]> Faces, int Dropped) Compact(
        IReadOnlyList<Vector3d> positions, IReadOnlyList<int[]> faces)
    {
        var referenced = new bool[positions.Count];
        foreach (var face in faces)
        {
            foreach (int v in face)
                referenced[v] = true;
        }

        var remap = new int[positions.Count];
        var compacted = new List<Vector3d>(positions.Count);
        for (int i = 0; i < positions.Count; i++)
        {
            if (referenced[i])
            {
                remap[i] = compacted.Count;
                compacted.Add(positions[i]);
            }
            else
            {
                remap[i] = -1;
            }
        }

        var newFaces = new List<int[]>(faces.Count);
        foreach (var face in faces)
        {
            var mapped = new int[face.Length];
            for (int i = 0; i < face.Length; i++)
                mapped[i] = remap[face[i]];
            newFaces.Add(mapped);
        }
        return (compacted, newFaces, positions.Count - compacted.Count);
    }

    /// <summary>
    /// Canonical key for duplicate-face detection: the loop rotated so its smallest
    /// vertex index comes first, in whichever direction reads lexicographically
    /// smaller. Two faces over the same vertex cycle — in either winding — map to
    /// the same key.
    /// </summary>
    public static string CanonicalFaceKey(int[] loop)
    {
        int n = loop.Length;
        int start = 0;
        for (int i = 1; i < n; i++)
        {
            if (loop[i] < loop[start])
                start = i;
        }

        var forward = new int[n];
        var backward = new int[n];
        for (int i = 0; i < n; i++)
        {
            forward[i] = loop[(start + i) % n];
            backward[i] = loop[(start - i + n) % n];
        }

        var canonical = forward;
        for (int i = 0; i < n; i++)
        {
            if (backward[i] != forward[i])
            {
                if (backward[i] < forward[i])
                    canonical = backward;
                break;
            }
        }
        return string.Join(',', canonical);
    }
}
