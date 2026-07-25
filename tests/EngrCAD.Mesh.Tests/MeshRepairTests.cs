using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class MeshRepairTests
{
    private static List<(Vector3d A, Vector3d B, Vector3d C)> Triangles(HalfEdgeMesh mesh)
    {
        var (positions, faces) = mesh.Triangulated().ToIndexed();
        var triangles = new List<(Vector3d, Vector3d, Vector3d)>();
        foreach (var face in faces)
            triangles.Add((positions[face[0]], positions[face[1]], positions[face[2]]));
        return triangles;
    }

    private static (IReadOnlyList<Vector3d> Positions, List<int[]> Faces) Soup(
        List<(Vector3d A, Vector3d B, Vector3d C)> triangles)
    {
        // Verbatim soup (no welding) so tests control exactly what repair sees.
        var positions = new List<Vector3d>();
        var faces = new List<int[]>();
        foreach (var (a, b, c) in triangles)
        {
            faces.Add([positions.Count, positions.Count + 1, positions.Count + 2]);
            positions.Add(a);
            positions.Add(b);
            positions.Add(c);
        }
        return (positions, faces);
    }

    // ---------- individual passes ----------

    [Fact]
    public void Clean_WeldsSoupIntoClosedMesh()
    {
        var (positions, faces) = Soup(Triangles(MeshPrimitives.Box(1, 2, 3)));
        var (mesh, report) = MeshRepair.Clean(positions, faces);

        Assert.True(mesh.IsClosed);
        Assert.Equal(6.0, mesh.Volume(), 12);
        Assert.Equal(positions.Count - 8, report.VerticesMerged);
        Assert.True(report.IsClosed);
        Assert.Equal(1, report.ComponentCount);
        Assert.Equal(0, report.ComponentsFlipped);
    }

    [Fact]
    public void Clean_RemovesDuplicateFaces_EitherWinding()
    {
        var triangles = Triangles(MeshPrimitives.Box(1, 1, 1));
        triangles.Add(triangles[0]);
        var (a, b, c) = triangles[4];
        triangles.Add((c, b, a)); // duplicate with opposite winding

        var (positions, faces) = Soup(triangles);
        var (mesh, report) = MeshRepair.Clean(positions, faces);

        Assert.Equal(2, report.DuplicateFacesRemoved);
        Assert.True(mesh.IsClosed);
        Assert.Equal(1.0, mesh.Volume(), 12);
    }

    [Fact]
    public void Clean_RemovesDegenerateFaces()
    {
        var triangles = Triangles(MeshPrimitives.Box(1, 1, 1));
        var (a, b, c) = triangles[0];
        triangles.Add((a, b, b + new Vector3d(1e-10, 0, 0))); // needle: apex welds into b
        triangles.Add((a, a, b));                              // exactly collapsed
        // Sliver with distinct vertices but altitude 5e-8 < the 1e-7 weld distance:
        // exercises the area/altitude test (its apex ends up unreferenced, compacted).
        var mid = (a + b) / 2 + new Vector3d(0, 0, 5e-8);
        triangles.Add((a, b, mid));

        var (positions, faces) = Soup(triangles);
        var (mesh, report) = MeshRepair.Clean(positions, faces);

        Assert.Equal(3, report.DegenerateFacesRemoved);
        Assert.True(mesh.IsClosed);
        Assert.Equal(1.0, mesh.Volume(), 12);
    }

    [Fact]
    public void Clean_RewindsFlippedPatch()
    {
        var triangles = Triangles(MeshPrimitives.Box(1, 1, 1));
        for (int i = 0; i < 4; i++)
        {
            var (a, b, c) = triangles[i];
            triangles[i] = (c, b, a);
        }

        var (positions, faces) = Soup(triangles);
        var (mesh, report) = MeshRepair.Clean(positions, faces);

        Assert.True(mesh.IsClosed);
        Assert.Equal(1.0, mesh.Volume(), 12); // positive: outward after repair
        Assert.True(report.FacesRewound > 0);
    }

    [Fact]
    public void Clean_FlipsInwardWoundComponent_ByWindingVote()
    {
        // A whole sphere wound inward: BFS finds it consistent; only the outward
        // vote (winding/signed volume) can right it.
        var triangles = Triangles(MeshPrimitives.UvSphere(1.0, segments: 16, rings: 8))
            .Select(t => (t.C, t.B, t.A)).ToList();

        var (positions, faces) = Soup(triangles);
        var (mesh, report) = MeshRepair.Clean(positions, faces);

        Assert.True(mesh.IsClosed);
        Assert.Equal(1, report.ComponentsFlipped);
        Assert.True(mesh.Volume() > 0);
    }

    [Fact]
    public void Clean_OrientsOpenComponent_ByWindingProbes()
    {
        // A sphere with one triangle removed — open, so the signed-volume shortcut
        // doesn't apply and the winding probes must decide.
        var triangles = Triangles(MeshPrimitives.UvSphere(1.0, segments: 16, rings: 8))
            .Select(t => (t.C, t.B, t.A)).ToList();
        triangles.RemoveAt(40);

        var (positions, faces) = Soup(triangles);
        var (mesh, report) = MeshRepair.Clean(positions, faces);

        Assert.False(mesh.IsClosed);
        Assert.Equal(1, report.ComponentsFlipped);
        Assert.True(mesh.SignedVolume() > 0);
    }

    [Fact]
    public void Clean_ClosesCracks_ByWelding()
    {
        var triangles = Triangles(MeshPrimitives.Box(1, 1, 1));
        // Shift one triangle's private copies of its corners by 5e-8 — inside the
        // 1e-7 repair weld, outside the readers' 1e-9 exact weld.
        var (a, b, c) = triangles[0];
        var jitter = new Vector3d(5e-8, 0, 0);
        triangles[0] = (a + jitter, b + jitter, c);

        var (positions, faces) = Soup(triangles);
        var (mesh, report) = MeshRepair.Clean(positions, faces);

        Assert.True(mesh.IsClosed);
        Assert.True(report.IsClosed);
        Assert.Equal(1.0, mesh.Volume(), 6);
    }

    [Fact]
    public void Clean_ExistingMesh_ReorientsInwardWinding()
    {
        // A manifold-but-inside-out mesh via the HalfEdgeMesh overload.
        var box = MeshPrimitives.Box(2, 2, 2);
        var (positions, faces) = box.ToIndexed();
        var flipped = faces.Select(f => f.Reverse().ToArray()).ToList();
        var inward = HalfEdgeMesh.Build(positions, flipped);
        Assert.True(inward.Volume() < 0);

        var (mesh, report) = MeshRepair.Clean(inward);

        Assert.Equal(8.0, mesh.Volume(), 12);
        Assert.Equal(1, report.ComponentsFlipped);
        Assert.Equal(4, mesh.Faces.First().Degree); // polygon faces preserved
    }

    [Fact]
    public void Clean_UnrepairableFin_ThrowsWithDiagnostics()
    {
        var triangles = Triangles(MeshPrimitives.Box(1, 1, 1));
        var (a, b, _) = triangles[0];
        triangles.Add((a, b, new Vector3d(5, 5, 5))); // fin: needs topological surgery

        var (positions, faces) = Soup(triangles);
        var exception = Assert.Throws<InvalidOperationException>(() => MeshRepair.Clean(positions, faces));
        Assert.Contains("non-manifold edges: 1", exception.Message);
    }

    // ---------- the filthy-file end-to-end ----------

    [Fact]
    public void ReadAndRepair_FilthyStl_RecoversClosedMeshWithCorrectVolume()
    {
        // Clean originals: a box and a separated sphere (two components).
        var box = MeshPrimitives.Box(2, 2, 2);
        var sphere = MeshPrimitives.UvSphere(1.0, segments: 24, rings: 12)
            .Transformed(Matrix4d.CreateTranslation(new Vector3d(5, 0, 0)));
        double cleanVolume = box.Volume() + sphere.Volume();

        var boxTriangles = Triangles(box);
        // Crack: give one triangle private, jittered copies of its corners. Binary
        // STL is float32, so the jitter must exceed float ulp (~2.4e-7 at |coord| 1)
        // and the repair weld must be at or above it.
        var (ca, cb, cc) = boxTriangles[0];
        boxTriangles[0] = (ca + new Vector3d(2.5e-7, 0, 0), cb, cc + new Vector3d(0, 2.5e-7, 0));
        // Flipped patch inside the box component.
        for (int i = 2; i < 6; i++)
        {
            var (a, b, c) = boxTriangles[i];
            boxTriangles[i] = (c, b, a);
        }
        // Duplicates.
        boxTriangles.Add(boxTriangles[7]);
        boxTriangles.Add(boxTriangles[9]);
        // Degenerate needle: the apex offset must survive float32 export (> ulp) yet
        // sit inside the repair weld so the needle collapses there, not at read time.
        var (da, db, _) = boxTriangles[10];
        boxTriangles.Add((da, db, db + new Vector3d(0, 0, 3e-7)));

        // The sphere component arrives entirely inside-out.
        var dirty = new List<(Vector3d A, Vector3d B, Vector3d C)>(boxTriangles);
        dirty.AddRange(Triangles(sphere).Select(t => (t.C, t.B, t.A)));

        string path = Path.Combine(Path.GetTempPath(), $"engrcad-filthy-{Guid.NewGuid():N}.stl");
        try
        {
            File.WriteAllBytes(path, MeshReaderTests.BinaryStlBytes(dirty, header: "solid filthy"));

            // Sanity: the raw read is NOT manifold.
            var raw = MeshReader.ReadFile(path);
            Assert.False(raw.IsManifold);
            Assert.False(raw.Diagnostics.IsCleanTopology);

            var (mesh, report) = MeshReader.ReadAndRepair(path, new MeshRepairOptions
            {
                WeldTolerance = 5e-7, // above float quantization — see MeshRepairOptions docs
            });

            Assert.True(mesh.IsClosed);
            Assert.True(report.IsClosed);
            Assert.Equal(2, report.ComponentCount);
            Assert.Equal(2, report.DuplicateFacesRemoved);
            Assert.True(report.DegenerateFacesRemoved >= 1);
            Assert.True(report.FacesRewound > 0);       // the flipped patch
            Assert.Equal(1, report.ComponentsFlipped);  // the inside-out sphere
            Assert.True(report.VerticesMerged > 0);     // STL soup + the crack
            // Volume matches the clean original to float32 export precision.
            Assert.Equal(cleanVolume, mesh.Volume(), 4);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
