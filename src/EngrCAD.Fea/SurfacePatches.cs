using EngrCAD.Core;

namespace EngrCAD.Fea;

/// <summary>
/// A maximal set of connected, coplanar, equally-tagged input triangles — the unit boundary
/// recovery actually works in.
///
/// <para><b>Why the patch and not the triangle is the right unit.</b> A Delaunay
/// tetrahedralization is free to triangulate a planar region however it likes, and on a
/// square face (four cocircular corners) both diagonals are equally Delaunay. Demanding that
/// the INPUT triangle appear as a face therefore fails on the most ordinary geometry there
/// is — a box — and no amount of refinement fixes it, because every refinement of a
/// cocircular configuration is cocircular again. Measured: asking for input triangles on a
/// unit cube blew through a 500 000-point Steiner budget without converging.</para>
///
/// <para>What the boundary must actually satisfy is that the tet mesh's skin IS the input
/// surface as a POINT SET. A patch states exactly that and nothing more: the triangulation
/// is free to choose diagonals inside a flat region, and recovery is the question of
/// whether the faces lying in the patch tile it completely.</para>
/// </summary>
internal sealed class SurfacePatch
{
    /// <summary>Unit normal of the patch's plane, from the input winding (outward).</summary>
    public Vector3d Normal { get; init; }

    /// <summary>A point on the patch's plane.</summary>
    public Vector3d Origin { get; init; }

    /// <summary>Local frame for 2D containment tests.</summary>
    public Frame3d Frame { get; init; }

    /// <summary>Indices of the input triangles making up this patch.</summary>
    public List<int> Triangles { get; } = [];

    /// <summary>The tag every triangle in this patch shares — the invariant that keeps
    /// boundary-condition attribution honest, and what a recovery failure names.</summary>
    public int Tag { get; init; }
}

/// <summary>Groups input triangles into coplanar, equally-tagged connected patches.</summary>
internal static class SurfacePatches
{
    /// <summary>
    /// Builds the patch decomposition. Triangles merge when they share an edge, their unit
    /// normals agree to <paramref name="planeTolerance"/>, their planes have the same offset,
    /// and they carry the same tag — so a patch never straddles two B-Rep faces and
    /// attribution stays honest.
    /// </summary>
    public static List<SurfacePatch> Build(
        IReadOnlyList<Vector3d> positions,
        IReadOnlyList<int[]> triangles,
        IReadOnlyList<int> tags,
        double planeTolerance,
        double extent)
    {
        int n = triangles.Count;
        var normals = new Vector3d[n];
        for (int i = 0; i < n; i++)
        {
            var t = triangles[i];
            var raw = (positions[t[1]] - positions[t[0]]).Cross(positions[t[2]] - positions[t[0]]);
            double length = raw.Length;
            normals[i] = length > 0 ? raw / length : new Vector3d(0, 0, 1);
        }

        // Union-find over edge adjacency, merging only coplanar same-tag neighbours.
        var parent = new int[n];
        for (int i = 0; i < n; i++)
            parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x)
                x = parent[x] = parent[parent[x]];
            return x;
        }

        var edgeOwner = new Dictionary<(int, int), int>();
        for (int i = 0; i < n; i++)
        {
            var t = triangles[i];
            for (int e = 0; e < 3; e++)
            {
                int a = t[e], b = t[(e + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                if (!edgeOwner.TryGetValue(key, out int other))
                {
                    edgeOwner[key] = i;
                    continue;
                }
                if (tags[i] != tags[other])
                    continue;
                if (normals[i].Dot(normals[other]) < 1 - planeTolerance)
                    continue;
                // Same plane, not merely parallel: the neighbour's third vertex must lie in
                // this triangle's plane.
                double offset = Math.Abs((positions[triangles[other][0]] - positions[t[0]]).Dot(normals[i]));
                offset = Math.Max(offset,
                    Math.Abs((positions[triangles[other][1]] - positions[t[0]]).Dot(normals[i])));
                offset = Math.Max(offset,
                    Math.Abs((positions[triangles[other][2]] - positions[t[0]]).Dot(normals[i])));
                if (offset > planeTolerance * extent)
                    continue;

                int ra = Find(i), rb = Find(other);
                if (ra != rb)
                    parent[ra] = rb;
            }
        }

        var byRoot = new Dictionary<int, SurfacePatch>();
        var result = new List<SurfacePatch>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(i);
            if (!byRoot.TryGetValue(root, out var patch))
            {
                patch = new SurfacePatch
                {
                    Normal = normals[root],
                    Origin = positions[triangles[root][0]],
                    Frame = Frame3d.FromNormal(positions[triangles[root][0]], normals[root]),
                    Tag = tags[root],
                };
                byRoot[root] = patch;
                result.Add(patch);
            }
            patch.Triangles.Add(i);
        }
        return result;
    }
}
