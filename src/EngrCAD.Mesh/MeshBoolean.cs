using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Boolean operations on closed meshes via BSP clipping (csg.js approach). Inputs are
/// triangulated internally; both must be closed with outward winding. BSP clipping
/// tessellates the two sides of an intersection seam independently (T-junctions), so a
/// seam-zipping pass inserts the matching vertices on both sides before rebuilding —
/// results are topologically closed for well-conditioned inputs. Near-degenerate inputs
/// (coincident coplanar faces, tangent surfaces) remain fragile; an exact
/// intersection-based rewrite is on the roadmap.
/// </summary>
public static class MeshBoolean
{
    public static HalfEdgeMesh Union(HalfEdgeMesh a, HalfEdgeMesh b)
    {
        var na = Node(a);
        var nb = Node(b);
        na.ClipTo(nb);
        nb.ClipTo(na);
        nb.Invert();
        nb.ClipTo(na);
        nb.Invert();
        na.Build(nb.AllPolygons());
        return FromPolygons(na.AllPolygons());
    }

    public static HalfEdgeMesh Difference(HalfEdgeMesh a, HalfEdgeMesh b)
    {
        var na = Node(a);
        var nb = Node(b);
        na.Invert();
        na.ClipTo(nb);
        nb.ClipTo(na);
        nb.Invert();
        nb.ClipTo(na);
        nb.Invert();
        na.Build(nb.AllPolygons());
        na.Invert();
        return FromPolygons(na.AllPolygons());
    }

    public static HalfEdgeMesh Intersection(HalfEdgeMesh a, HalfEdgeMesh b)
    {
        var na = Node(a);
        var nb = Node(b);
        na.Invert();
        nb.ClipTo(na);
        nb.Invert();
        na.ClipTo(nb);
        nb.ClipTo(na);
        na.Build(nb.AllPolygons());
        na.Invert();
        return FromPolygons(na.AllPolygons());
    }

    private static CsgNode Node(HalfEdgeMesh mesh)
    {
        if (!mesh.IsClosed)
            throw new ArgumentException("Boolean operations require closed meshes.");

        var polygons = new List<CsgPolygon>(mesh.FaceCount);
        foreach (var face in mesh.Triangulated().Faces)
        {
            var vertices = face.Vertices().Select(v => v.Position).ToList();
            if (CsgPlane.TryFromPoints(vertices[0], vertices[1], vertices[2], out var plane))
                polygons.Add(new CsgPolygon(vertices, plane)); // degenerate slivers are dropped
        }
        return new CsgNode(polygons);
    }

    private static HalfEdgeMesh FromPolygons(List<CsgPolygon> polygons)
    {
        // Weld coincident vertices with a spatial hash (points that should coincide differ
        // only by floating-point noise, far below the cell size).
        const double weld = 10 * CsgPlane.Epsilon;
        var welded = new List<Vector3d>();
        var cells = new Dictionary<(long, long, long), List<int>>();

        int WeldVertex(in Vector3d p)
        {
            var key = (
                (long)Math.Floor(p.X / weld),
                (long)Math.Floor(p.Y / weld),
                (long)Math.Floor(p.Z / weld));
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
                            if (welded[index].DistanceSquaredTo(p) <= weld * weld)
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

        var faces = new List<List<int>>(polygons.Count);
        foreach (var polygon in polygons)
        {
            var loop = new List<int>();
            foreach (var v in polygon.Vertices)
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
            // degenerate byproducts of clipping and carry no area worth keeping.
            if (loop.Distinct().Count() != loop.Count)
                continue;
            faces.Add(loop);
        }

        ZipSeams(welded, faces);
        return HalfEdgeMesh.Build(welded, faces.Select(f => (IReadOnlyList<int>)f));
    }

    /// <summary>
    /// Eliminates seam T-junctions: the two sides of an intersection curve are tessellated
    /// independently by BSP clipping, so an edge on one side may be spanned by several
    /// shorter edges on the other. For every directed edge with no reverse partner, insert
    /// the crack vertices that lie collinearly on it; both sides then carry the identical
    /// subdivision and the surface closes up.
    /// </summary>
    private static void ZipSeams(List<Vector3d> positions, List<List<int>> faces)
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
