using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Builds a <see cref="HalfEdgeMesh"/> from a polygon soup by welding coincident vertices
/// with a spatial hash and dropping degenerate faces. Optionally zips T-junction seams
/// (see <see cref="MeshBoolean"/>) so independently tessellated but geometrically matching
/// boundaries close up.
/// </summary>
public static class MeshWelder
{
    public static HalfEdgeMesh WeldPolygons(
        IEnumerable<IReadOnlyList<Vector3d>> polygons,
        double tolerance = 1e-8,
        bool zipSeams = false)
    {
        if (tolerance <= 0)
            throw new ArgumentOutOfRangeException(nameof(tolerance));

        var welded = new List<Vector3d>();
        var cells = new Dictionary<(long, long, long), List<int>>();

        int WeldVertex(in Vector3d p)
        {
            var key = (
                (long)Math.Floor(p.X / tolerance),
                (long)Math.Floor(p.Y / tolerance),
                (long)Math.Floor(p.Z / tolerance));
            for (long dx = -1; dx <= 1; dx++)
            {
                for (long dy = -1; dy <= 1; dy++)
                {
                    for (long dz = -1; dz <= 1; dz++)
                    {
                        if (!cells.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var bucket))
                            continue;
                        foreach (int index in bucket)
                        {
                            if (welded[index].DistanceSquaredTo(p) <= tolerance * tolerance)
                                return index;
                        }
                    }
                }
            }
            welded.Add(p);
            int id = welded.Count - 1;
            if (!cells.TryGetValue(key, out var own))
                cells[key] = own = [];
            own.Add(id);
            return id;
        }

        var faces = new List<List<int>>();
        foreach (var polygon in polygons)
        {
            var loop = new List<int>();
            foreach (var v in polygon)
            {
                int id = WeldVertex(v);
                if (loop.Count == 0 || loop[^1] != id)
                    loop.Add(id);
            }
            while (loop.Count > 1 && loop[0] == loop[^1])
                loop.RemoveAt(loop.Count - 1);
            if (loop.Count < 3)
                continue; // sliver collapsed by welding
            // A vertex repeated non-consecutively would pinch the surface; such faces are
            // degenerate and carry no area worth keeping.
            if (loop.Distinct().Count() != loop.Count)
                continue;
            faces.Add(loop);
        }

        if (zipSeams)
            ZipSeams(welded, faces);
        return HalfEdgeMesh.Build(welded, faces.Select(f => (IReadOnlyList<int>)f));
    }

    /// <summary>
    /// Eliminates seam T-junctions: an edge on one side of a seam may be spanned by
    /// several shorter edges on the other. For every directed edge with no reverse
    /// partner, insert the crack vertices that lie collinearly on it; both sides then
    /// carry the identical subdivision and the surface closes up.
    /// </summary>
    internal static void ZipSeams(List<Vector3d> positions, List<List<int>> faces)
    {
        const double seamEps = 1e-7;      // distance from a candidate vertex to the edge line
        const double endMargin = 1e-7;    // keep clear of the edge endpoints

        var directed = new HashSet<(int, int)>();
        foreach (var loop in faces)
        {
            for (int i = 0; i < loop.Count; i++)
                directed.Add((loop[i], loop[(i + 1) % loop.Count]));
        }

        // Crack vertices: endpoints of edges whose reverse is missing.
        var crackVertices = new HashSet<int>();
        foreach (var (a, b) in directed)
        {
            if (!directed.Contains((b, a)))
            {
                crackVertices.Add(a);
                crackVertices.Add(b);
            }
        }
        if (crackVertices.Count == 0)
            return;
        var candidates = crackVertices.ToArray();

        var insertions = new List<(double T, int Vertex)>();
        foreach (var loop in faces)
        {
            for (int i = 0; i < loop.Count; i++)
            {
                int a = loop[i];
                int b = loop[(i + 1) % loop.Count];
                if (directed.Contains((b, a)))
                    continue;

                var pa = positions[a];
                var ab = positions[b] - pa;
                double lengthSquared = ab.LengthSquared;
                if (lengthSquared <= 0)
                    continue;

                insertions.Clear();
                foreach (int c in candidates)
                {
                    if (c == a || c == b)
                        continue;
                    var ac = positions[c] - pa;
                    double t = ac.Dot(ab) / lengthSquared;
                    if (t <= 0 || t >= 1)
                        continue;
                    double edgeLength = Math.Sqrt(lengthSquared);
                    if (t * edgeLength < endMargin || (1 - t) * edgeLength < endMargin)
                        continue;
                    if ((ac - ab * t).Length > seamEps)
                        continue;
                    insertions.Add((t, c));
                }
                if (insertions.Count == 0)
                    continue;

                insertions.Sort((x, y) => x.T.CompareTo(y.T));
                loop.InsertRange(i + 1, insertions.Select(x => x.Vertex));
                i += insertions.Count; // skip past what we just inserted
            }
        }
    }
}
