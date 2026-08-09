using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// The B-Rep → mesh provenance seam: <see cref="BRepTessellator.TessellateWithProvenance"/> and
/// <see cref="BRepTessellator.TessellateForTetMesh"/>. Provenance must be a by-product that
/// changes NOTHING about the tessellation (bit-identity), and it must attribute every mesh
/// face to exactly the B-Rep face it came from.
/// </summary>
public class BRepTessellatorProvenanceTests
{
    private static void AssertMeshesIdentical(HalfEdgeMesh a, HalfEdgeMesh b)
    {
        Assert.Equal(a.VertexCount, b.VertexCount);
        Assert.Equal(a.FaceCount, b.FaceCount);
        var (pa, fa) = a.ToIndexed();
        var (pb, fb) = b.ToIndexed();
        for (int i = 0; i < pa.Length; i++)
            Assert.Equal(pa[i], pb[i]);              // Vector3d equality is exact-bit here
        for (int f = 0; f < fa.Count; f++)
            Assert.Equal(fa[f], fb[f]);
    }

    [Fact]
    public void Provenance_MeshIsBitIdenticalToPlainTessellate()
    {
        var solid = Shape.Box(30, 20, 6).Subtract(Shape.Cylinder(3, 20)).ToBrep();
        var plain = BRepTessellator.Tessellate(solid);
        var (mesh, provenance) = BRepTessellator.TessellateWithProvenance(solid);

        AssertMeshesIdentical(plain, mesh);
        Assert.Equal(mesh.FaceCount, provenance.Count);
        int faceCount = solid.Faces.Count();
        Assert.All(provenance, i => Assert.InRange(i, 0, faceCount - 1));
    }

    [Fact]
    public void Provenance_EveryFaceCentroidLiesOnItsAttributedPlane_OnABox()
    {
        // A box is all planar (exact) faces, so "the centroid lies on the attributed face"
        // is a weld-tier equality with no chord sagitta to allow for — the strongest form of
        // "attributed to exactly the face it came from".
        var solid = Shape.Box(30, 20, 6).ToBrep();
        var faces = solid.Faces.ToList();
        var (mesh, provenance) = BRepTessellator.TessellateWithProvenance(solid);
        var (positions, loops) = mesh.ToIndexed();

        for (int f = 0; f < loops.Count; f++)
        {
            var centroid = loops[f].Aggregate(Vector3d.Zero, (s, v) => s + positions[v]) / loops[f].Length;
            var face = faces[provenance[f]];
            Assert.True(face.IsPlanar(out var origin, out var normal), "box faces are planar");
            Assert.True(Math.Abs((centroid - origin).Dot(normal)) < 1e-9,
                $"face {f} attributed to a plane {(centroid - origin).Dot(normal):E2} away from its centroid");
        }
    }

    [Fact]
    public void Provenance_DrilledPlate_BoreWallTagsToTheCylinder_CapsToPlanes()
    {
        const double r = 3, halfZ = 3;
        var solid = Shape.Box(30, 20, 2 * halfZ).Subtract(Shape.Cylinder(r, 20)).ToBrep();
        var faces = solid.Faces.ToList();
        var (mesh, tags) = BRepTessellator.TessellateForTetMesh(solid);
        var (positions, loops) = mesh.ToIndexed();
        Assert.Equal(loops.Count, tags.Length);

        double Radius(Vector3d p) => Math.Sqrt(p.X * p.X + p.Y * p.Y);
        var boreTags = new HashSet<int>();
        var topTags = new HashSet<int>();
        var bottomTags = new HashSet<int>();
        int boreFaces = 0, topFaces = 0, bottomFaces = 0;

        for (int f = 0; f < loops.Count; f++)
        {
            var verts = loops[f].Select(v => positions[v]).ToList();
            if (verts.All(p => Math.Abs(Radius(p) - r) < 0.05))
            {
                boreTags.Add(tags[f]); boreFaces++;
            }
            else if (verts.All(p => Math.Abs(p.Z - halfZ) < 1e-6))
            {
                topTags.Add(tags[f]); topFaces++;
            }
            else if (verts.All(p => Math.Abs(p.Z + halfZ) < 1e-6))
            {
                bottomTags.Add(tags[f]); bottomFaces++;
            }
        }

        Assert.True(boreFaces > 0 && topFaces > 0 && bottomFaces > 0, "the fixture must present all three");

        // Every bore-wall triangle tags to a SINGLE face, and it is THE cylindrical bore.
        int boreTag = Assert.Single(boreTags);
        Assert.True(faces[boreTag].IsCylindrical(out _, out _, out double boreRadius));
        Assert.Equal(r, boreRadius, 6);

        // Each cap tags to a single face, and it is planar with a Z normal.
        int topTag = Assert.Single(topTags);
        int bottomTag = Assert.Single(bottomTags);
        Assert.NotEqual(topTag, bottomTag);
        foreach (int tag in new[] { topTag, bottomTag })
        {
            Assert.True(faces[tag].IsPlanar(out _, out var normal));
            Assert.True(Math.Abs(Math.Abs(normal.Z) - 1) < 1e-9, "a drilled-plate cap normal is ±Z");
        }
    }

    [Fact]
    public void TessellateForTetMesh_IsTriangulatedTessellateWithConsistentPerTriangleTags()
    {
        var solid = Shape.Box(30, 20, 6).Subtract(Shape.Cylinder(3, 20)).ToBrep();
        var faces = solid.Faces.ToList();

        var expected = BRepTessellator.Tessellate(solid).Triangulated();
        var (mesh, tags) = BRepTessellator.TessellateForTetMesh(solid);

        AssertMeshesIdentical(expected, mesh);
        Assert.True(mesh.IsTriangulated);
        Assert.Equal(mesh.FaceCount, tags.Length);

        // Every per-triangle tag is consistent with per-FACE provenance: reconstruct the
        // per-face tags and confirm the degree-based expansion agrees, so the tags cannot be
        // off by a fan without this catching it.
        var (_, wp) = (mesh, BRepTessellator.TessellateWithProvenance(solid).FaceProvenance);
        var (_, weldedLoops) = BRepTessellator.TessellateWithProvenance(solid).Mesh.ToIndexed();
        int t = 0;
        for (int wf = 0; wf < weldedLoops.Count; wf++)
        {
            int triangles = weldedLoops[wf].Length - 2;
            for (int k = 0; k < triangles; k++)
                Assert.Equal(wp[wf], tags[t++]);
        }
        Assert.Equal(tags.Length, t);
        Assert.All(tags, i => Assert.InRange(i, 0, faces.Count - 1));
    }
}
