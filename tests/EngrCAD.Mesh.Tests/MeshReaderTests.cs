using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class MeshReaderTests
{
    // ---------- helpers ----------

    /// <summary>Triangles of a mesh as position triples (what an STL file carries).</summary>
    private static List<(Vector3d A, Vector3d B, Vector3d C)> Triangles(HalfEdgeMesh mesh)
    {
        var (positions, faces) = mesh.Triangulated().ToIndexed();
        var triangles = new List<(Vector3d, Vector3d, Vector3d)>();
        foreach (var face in faces)
            triangles.Add((positions[face[0]], positions[face[1]], positions[face[2]]));
        return triangles;
    }

    /// <summary>Writes a raw binary STL — 80-byte header, count, 50-byte facets — so
    /// tests can author dirty files that StlWriter (which requires a manifold
    /// HalfEdgeMesh) could never produce.</summary>
    internal static byte[] BinaryStlBytes(
        IReadOnlyList<(Vector3d A, Vector3d B, Vector3d C)> triangles, string header = "test")
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var headerBytes = new byte[80];
        System.Text.Encoding.ASCII.GetBytes(header).CopyTo(headerBytes, 0);
        writer.Write(headerBytes);
        writer.Write((uint)triangles.Count);
        foreach (var (a, b, c) in triangles)
        {
            for (int i = 0; i < 3; i++)
                writer.Write(0.0f); // normal — readers ignore it
            foreach (var p in (ReadOnlySpan<Vector3d>)[a, b, c])
            {
                writer.Write((float)p.X);
                writer.Write((float)p.Y);
                writer.Write((float)p.Z);
            }
            writer.Write((ushort)0);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static MeshReadResult ReadStl(byte[] bytes, double weldTolerance = 1e-9)
    {
        using var stream = new MemoryStream(bytes);
        return StlReader.Read(stream, weldTolerance);
    }

    private static Vector3d ToFloat(in Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);

    // ---------- STL: round trip + detection ----------

    [Fact]
    public void BinaryStl_RoundTrip_IsBitExactAndManifold()
    {
        var box = MeshPrimitives.Box(2, 3, 4);
        using var stream = new MemoryStream();
        StlWriter.Write(box, stream);
        stream.Position = 0;

        var result = StlReader.Read(stream);

        Assert.True(result.IsManifold);
        var mesh = result.RequireMesh();
        Assert.True(mesh.IsClosed);
        // Welding merges bit-identical float vertices back to the box's 8 corners.
        Assert.Equal(8, result.Positions.Count);
        // Positions are exactly the float-truncated originals — no further perturbation.
        var expected = box.Vertices.Select(v => ToFloat(v.Position)).ToHashSet();
        Assert.Equal(expected, result.Positions.ToHashSet());
        Assert.Equal(24.0, mesh.Volume(), 4); // float quantization only
        Assert.Empty(result.Diagnostics.Warnings);
        Assert.True(result.Diagnostics.IsCleanTopology);
    }

    [Fact]
    public void BinaryStl_Sphere_RoundTripsClosed()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, segments: 24, rings: 12);
        using var stream = new MemoryStream();
        StlWriter.Write(sphere, stream);
        stream.Position = 0;

        var result = StlReader.Read(stream);

        var mesh = result.RequireMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(sphere.Volume(), mesh.Volume(), 5);
    }

    [Fact]
    public void BinaryStl_HeaderStartingWithSolid_IsStillDetectedAsBinary()
    {
        // Many binary exporters write "solid ..." into the 80-byte header; the exact
        // 84 + 50 * n size test must win over the prefix sniff.
        var bytes = BinaryStlBytes(Triangles(MeshPrimitives.Box(1, 1, 1)), header: "solid exported-by-cad");
        var result = ReadStl(bytes);

        Assert.True(result.IsManifold);
        Assert.Equal(1.0, result.RequireMesh().Volume(), 6);
    }

    [Fact]
    public void AsciiStl_Parses_Tetrahedron()
    {
        const string text = """
            solid tet
              facet normal 0 0 -1
                outer loop
                  vertex 0 0 0
                  vertex 0 1 0
                  vertex 1 0 0
                endloop
              endfacet
              facet normal 0 -1 0
                outer loop
                  vertex 0 0 0
                  vertex 1 0 0
                  vertex 0 0 1
                endloop
              endfacet
              facet normal -1 0 0
                outer loop
                  vertex 0 0 0
                  vertex 0 0 1
                  vertex 0 1 0
                endloop
              endfacet
              facet normal 1 1 1
                outer loop
                  vertex 1 0 0
                  vertex 0 1 0
                  vertex 0 0 1
                endloop
              endfacet
            endsolid tet
            """;
        var result = ReadStl(System.Text.Encoding.ASCII.GetBytes(text));

        var mesh = result.RequireMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(4, result.Positions.Count);
        Assert.Equal(1.0 / 6.0, mesh.Volume(), 12); // exact double parse, analytic volume
    }

    [Fact]
    public void BinaryStl_Truncated_ReadsAvailableFacetsWithWarning()
    {
        var bytes = BinaryStlBytes(Triangles(MeshPrimitives.Box(1, 1, 1)));
        var truncated = bytes[..(bytes.Length - 25)]; // cut the last record in half
        var result = ReadStl(truncated);

        Assert.Contains(result.Diagnostics.Warnings, w => w.Contains("complete records"));
        Assert.Equal(11, result.Faces.Count); // 12 box triangles minus the truncated one
        // An open manifold still builds — boundary half-edges are explicit.
        var mesh = result.RequireMesh();
        Assert.False(mesh.IsClosed);
        Assert.Equal(3, result.Diagnostics.BoundaryEdges);
    }

    // ---------- STL: dirty-file diagnostics ----------

    [Fact]
    public void DirtyStl_NonManifoldFin_IsDiagnosedNotThrown()
    {
        var triangles = Triangles(MeshPrimitives.Box(1, 1, 1));
        // A fin: a third triangle hanging off an existing edge.
        var (a, b, _) = triangles[0];
        triangles.Add((a, b, new Vector3d(5, 5, 5)));

        var result = ReadStl(BinaryStlBytes(triangles));

        Assert.False(result.IsManifold);
        Assert.NotNull(result.Diagnostics.BuildError);
        Assert.Equal(1, result.Diagnostics.NonManifoldEdges);
        Assert.True(result.Diagnostics.BoundaryEdges > 0); // the fin's free edges
        Assert.Throws<InvalidOperationException>(() => result.RequireMesh());
    }

    [Fact]
    public void DirtyStl_DuplicateTriangles_AreCounted()
    {
        var triangles = Triangles(MeshPrimitives.Box(1, 1, 1));
        triangles.Add(triangles[0]);
        triangles.Add(triangles[0]);

        var result = ReadStl(BinaryStlBytes(triangles));

        Assert.False(result.IsManifold);
        Assert.Equal(2, result.Diagnostics.DuplicateFaces);
        Assert.Equal(3, result.Diagnostics.NonManifoldEdges); // triangle 0's edges now carry four uses
    }

    [Fact]
    public void DirtyStl_FlippedPatch_ReportsInconsistentWinding()
    {
        var triangles = Triangles(MeshPrimitives.Box(1, 1, 1));
        var (a, b, c) = triangles[5];
        triangles[5] = (c, b, a); // flip one triangle

        var result = ReadStl(BinaryStlBytes(triangles));

        Assert.False(result.IsManifold);
        Assert.Equal(3, result.Diagnostics.InconsistentlyWoundEdges);
        Assert.Equal(0, result.Diagnostics.NonManifoldEdges);
    }

    [Fact]
    public void DirtyStl_Crack_ReportsBoundaryEdges()
    {
        var triangles = Triangles(MeshPrimitives.Box(1, 1, 1));
        // Shift one triangle's copy of a corner by a float-representable 1e-5: its
        // seams no longer weld at 1e-9, opening a crack.
        var (a, b, c) = triangles[0];
        triangles[0] = (a + new Vector3d(1e-5, 0, 0), b, c);

        var result = ReadStl(BinaryStlBytes(triangles));

        // The cracked surface is still manifold (open), so it builds — but it is no
        // longer closed, and the crack shows up as boundary edges.
        var mesh = result.RequireMesh();
        Assert.False(mesh.IsClosed);
        Assert.True(result.Diagnostics.BoundaryEdges >= 4);
    }

    [Fact]
    public void DirtyStl_DegenerateTriangles_AreDroppedAndCounted()
    {
        var triangles = Triangles(MeshPrimitives.Box(1, 1, 1));
        var (a, b, _) = triangles[0];
        triangles.Add((a, b, b)); // repeated corner collapses under welding

        var result = ReadStl(BinaryStlBytes(triangles));

        Assert.Equal(1, result.Diagnostics.DegenerateFacesDropped);
        Assert.True(result.IsManifold); // the degenerate facet vanished, box is intact
        Assert.Equal(1.0, result.RequireMesh().Volume(), 6);
    }

    // ---------- OBJ ----------

    [Fact]
    public void Obj_RoundTrip_IsBitExact()
    {
        var box = MeshPrimitives.Box(2, 3, 4); // quad faces — the reader triangulates
        var text = new StringWriter();
        ObjWriter.Write(box, text);

        var result = ObjReader.Read(new StringReader(text.ToString()));

        var mesh = result.RequireMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(8, result.Positions.Count);
        // ObjWriter uses round-trip ("R") formatting, so doubles survive exactly.
        Assert.Equal(box.Vertices.Select(v => v.Position).ToHashSet(), result.Positions.ToHashSet());
        Assert.Equal(12, result.Faces.Count); // 6 quads triangulated
        Assert.Equal(24.0, mesh.Volume(), 12);
    }

    [Fact]
    public void BinaryStl_FromAGrowingStream_DetectsSizeFromTheFileLengthNotTheBuffer()
    {
        // The reader buffers into a MemoryStream and reads GetBuffer() (never ToArray(),
        // which would double peak memory). GetBuffer's capacity is rounded up to a power
        // of two, so every size test must use the stream's Length: a 1-facet binary STL
        // is 134 bytes in a 256-byte buffer, and the 84 + 50 * n detection would fail
        // outright against the buffer length.
        var bytes = BinaryStlBytes([((0, 0, 0), (1, 0, 0), (0, 1, 0))]);
        Assert.Equal(134, bytes.Length);

        using var source = new MemoryStream();
        source.Write(bytes, 0, bytes.Length);
        source.Position = 0;
        var result = StlReader.Read(source);

        Assert.Single(result.Faces);
        Assert.Empty(result.Diagnostics.Warnings); // no "trailing bytes ignored" from padding
    }

    [Fact]
    public void Obj_ManyBackslashContinuations_JoinIntoOneRecord()
    {
        // Continuations accumulate in a StringBuilder; the naive `line = line + next`
        // loop is O(n^2) in the number of continued lines. 200 corners keeps the test
        // fast either way — this locks the semantics, not the speed.
        const int corners = 200;
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < corners; i++)
        {
            double angle = 2 * Math.PI * i / corners;
            text.Append("v ").Append(Math.Cos(angle).ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                .Append(' ').Append(Math.Sin(angle).ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                .AppendLine(" 0");
        }
        text.AppendLine("f \\");
        for (int i = 1; i <= corners; i++)
            text.AppendLine(i == corners ? i.ToString() : i + " \\");

        var result = ObjReader.Read(new StringReader(text.ToString()));

        Assert.Equal(corners, result.Positions.Count);
        // One n-gon, triangulated into n - 2 triangles.
        Assert.Equal(corners - 2, result.Faces.Count);
        Assert.Empty(result.Diagnostics.Warnings);
    }

    [Fact]
    public void Obj_IndexForms_NegativeAndSlashed_AllResolve()
    {
        const string text = """
            # unit right triangle, three ways
            v 0 0 0
            v 1 0 0
            v 0 1 0
            vt 0 0
            vn 0 0 1
            f 1/1/1 2/1/1 3/1/1
            v 0 0 1
            v 1 0 1
            v 0 1 1
            f -3//1 -2// -1
            """;
        var result = ObjReader.Read(new StringReader(text));

        Assert.Equal(2, result.Faces.Count);
        Assert.Equal(6, result.Positions.Count);
        // The relative-index face references the last three vertices.
        Assert.Equal([3, 4, 5], result.Faces[1]);
    }

    [Fact]
    public void Obj_SkippedConstructs_AreReported()
    {
        const string text = """
            mtllib things.mtl
            o thing
            g patch
            usemtl steel
            s 1
            v 0 0 0
            v 1 0 0
            v 0 1 0
            l 1 2
            f 1 2 3
            """;
        var result = ObjReader.Read(new StringReader(text));

        Assert.Single(result.Faces);
        foreach (var keyword in new[] { "mtllib", "o", "g", "usemtl", "s", "l" })
            Assert.Contains(result.Diagnostics.Warnings, w => w.Contains($"'{keyword}'"));
    }

    [Fact]
    public void Obj_PolygonFace_IsTriangulated()
    {
        // A single planar convex pentagon — an open but manifold sheet.
        const string text = """
            v 0 0 0
            v 2 0 0
            v 2.6 1.9 0
            v 1 3 0
            v -0.6 1.9 0
            f 1 2 3 4 5
            """;
        var result = ObjReader.Read(new StringReader(text));

        Assert.Equal(3, result.Faces.Count); // 5 - 2 triangles
        var mesh = result.RequireMesh();
        Assert.False(mesh.IsClosed);
        Assert.Single(mesh.BoundaryLoops());
    }

    // ---------- OFF ----------

    [Fact]
    public void Off_Cube_ParsesClosedWithExactVolume()
    {
        const string text = """
            OFF
            # unit-ish cube
            8 6 12
            0 0 0
            2 0 0
            2 2 0
            0 2 0
            0 0 2
            2 0 2
            2 2 2
            0 2 2
            4 0 3 2 1
            4 4 5 6 7
            4 0 1 5 4
            4 1 2 6 5
            4 2 3 7 6
            4 3 0 4 7
            """;
        var result = OffReader.Read(new StringReader(text));

        var mesh = result.RequireMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(8.0, mesh.Volume(), 12);
        Assert.Equal(12, result.Faces.Count); // quads triangulated
    }

    [Fact]
    public void Off_CountsOnHeaderLine_AndFaceColors_AreHandled()
    {
        const string text = """
            OFF 4 1 0
            0 0 0
            1 0 0
            1 1 0
            0 1 0
            4 0 1 2 3 255 0 0
            """;
        var result = OffReader.Read(new StringReader(text));

        Assert.Equal(2, result.Faces.Count);
        Assert.NotNull(result.Mesh);
        Assert.Contains(result.Diagnostics.Warnings, w => w.Contains("face color"));
    }

    [Fact]
    public void Off_AbsurdHeaderCounts_AreDiagnosedWithoutHugeAllocation()
    {
        // Header-declared counts are untrusted: two billion declared vertices must not
        // pre-allocate storage — parsing is bounded by the lines actually present.
        const string text = """
            OFF
            2000000000 2000000000 0
            0 0 0
            """;
        var result = OffReader.Read(new StringReader(text));

        Assert.Empty(result.Faces);
        Assert.Contains(result.Diagnostics.Warnings, w => w.Contains("declares 2000000000 vertices"));
        Assert.Contains(result.Diagnostics.Warnings, w => w.Contains("declares 2000000000 faces"));
    }

    [Fact]
    public void Obj_OutOfRangeIndices_AreSkippedAsBadFaces()
    {
        // "f 1 2 4" points past the vertex list; "f -4 2 3" resolves to index -1.
        const string text = """
            v 0 0 0
            v 1 0 0
            v 0 1 0
            f 1 2 4
            f -4 2 3
            f 1 2 3
            """;
        var result = ObjReader.Read(new StringReader(text));

        Assert.Single(result.Faces);
        Assert.Contains(result.Diagnostics.Warnings, w => w.Contains("2 malformed or out-of-range 'f' lines"));
    }

    // ---------- facade ----------

    [Fact]
    public void MeshReader_DispatchesByExtension()
    {
        var box = MeshPrimitives.Box(1, 1, 1);
        string dir = Directory.CreateTempSubdirectory("engrcad-reader").FullName;
        try
        {
            string stlPath = Path.Combine(dir, "box.stl");
            string objPath = Path.Combine(dir, "box.obj");
            StlWriter.WriteFile(box, stlPath);
            ObjWriter.WriteFile(box, objPath);

            Assert.Equal(1.0, MeshReader.ReadFile(stlPath).RequireMesh().Volume(), 6);
            Assert.Equal(1.0, MeshReader.ReadFile(objPath).RequireMesh().Volume(), 12);
            Assert.True(MeshReader.SupportsExtension(".STL"));
            Assert.True(MeshReader.SupportsExtension("off"));
            Assert.False(MeshReader.SupportsExtension(".step"));
            Assert.Throws<NotSupportedException>(() => MeshReader.ReadFile(Path.Combine(dir, "box.step")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
