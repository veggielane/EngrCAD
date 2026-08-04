using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using EngrCAD.Viewer;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>Headless paths of <see cref="EngrCad.Run"/> — no Avalonia lifetime started.</summary>
public class RunTests
{
    private static string TempFile(string extension) =>
        Path.Combine(Path.GetTempPath(), $"engrcad-test-{Guid.NewGuid():N}{extension}");

    private static Scene BracketScene()
    {
        var scene = new Scene();
        scene.Add(new Part("bracket", Shape.Box(4, 3, 1) - Shape.Cylinder(0.5, 3)));
        return scene;
    }

    [Fact]
    public void ExportStep_WritesBrepRepresentableShape()
    {
        var path = TempFile(".step");
        try
        {
            int code = EngrCad.Run(["--export", path], BracketScene);
            Assert.Equal(0, code);
            var text = File.ReadAllText(path);
            Assert.Contains("MANIFOLD_SOLID_BREP", text);
            Assert.Contains("ISO-10303-21", text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExportObj_MergesPartsWithTransforms()
    {
        var path = TempFile(".obj");
        try
        {
            int code = EngrCad.Run(["--export", path], () =>
            {
                var scene = new Scene();
                scene.Add(new Part("a", MeshPrimitives.Box(1, 1, 1)));
                scene.Add(new Part("b", MeshPrimitives.Box(1, 1, 1),
                    transform: Matrix4d.CreateTranslation((10, 0, 0))));
                return scene;
            });
            Assert.Equal(0, code);

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Count(l => l.StartsWith("o ")));
            Assert.Equal(16, lines.Count(l => l.StartsWith("v ")));   // 8 corners × 2 boxes
            // The second box's transform is applied: some vertex sits near x = 10.
            Assert.Contains(lines, l => l.StartsWith("v 10.5 ") || l.StartsWith("v 9.5 "));
            // Face indices reference the offset block, not out-of-range vertices.
            int maxIndex = lines.Where(l => l.StartsWith("f "))
                .SelectMany(l => l[2..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Max(int.Parse);
            Assert.Equal(16, maxIndex);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExportVtu_CarriesTheScenesSimulationResults()
    {
        var path = TempFile(".vtu");
        try
        {
            int code = EngrCad.Run(["--export", path], () =>
            {
                var scene = new Scene();
                var plate = new Part("plate", Shape.Box(4, 3, 1));
                scene.Add(plate);
                var mesh = plate.GetMesh();
                plate.AddResult(MeshField.Sample(mesh, "von Mises", "MPa", p => p.Z * 10));
                plate.AddResult(MeshField.SampleVector(
                    mesh, "displacement", "mm", p => new Vector3d(0, 0, p.X * 0.1)));
                // A second part with NO results: its vertices must contribute NaN to the
                // arrays rather than dropping them or inventing zeros.
                scene.Add(new Part("jig", MeshPrimitives.Box(1, 1, 1),
                    transform: Matrix4d.CreateTranslation((10, 0, 0))));
                return scene;
            });
            Assert.Equal(0, code);

            var root = System.Xml.Linq.XDocument.Load(path).Root!;
            Assert.Equal("UnstructuredGrid", (string?)root.Attribute("type"));
            var arrays = root.Descendants("PointData").Single().Elements("DataArray")
                .ToDictionary(e => (string)e.Attribute("Name")!, e => e);
            Assert.Equal(["von Mises", "displacement"], arrays.Keys);
            Assert.Equal("3", (string?)arrays["displacement"].Attribute("NumberOfComponents"));
            Assert.Contains("NaN", arrays["von Mises"].Value);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ExportStep_NonBrepScene_FailsWithExitCode()
    {
        var path = TempFile(".step");
        try
        {
            int code = EngrCad.Run(["--export", path], () =>
            {
                var scene = new Scene();
                scene.Add(new Part("blend", Shape.Sphere(1).SmoothUnion(Shape.Sphere(1).Translate(1, 0, 0), 0.3)));
                return scene;
            });
            Assert.Equal(1, code);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    // ---- --flat: sheet-metal flat patterns ----

    private static Scene SheetScene(int sheets = 1)
    {
        var scene = new Scene();
        for (int i = 0; i < sheets; i++)
        {
            var body = SheetMetalBody
                .Base(Sketch.Rectangle(80, 50), new SheetMetalSpec(1.5, 2.0))
                .WithFlange(SheetFlangeTarget.BaseEdge(1), 25);
            scene.Add(new Part($"panel{i}", body.Solid));
        }
        scene.Add(new Part("block", Shape.Box(10, 10, 10)));
        return scene;
    }

    /// <summary>
    /// The CLI route exists so a blank can be cut without writing a script, and it must
    /// derive that blank exactly as a script would: the assertions are on the CONTENT
    /// (the bend layer a press brake reads, both tangent lines of the one bend) rather
    /// than on the file existing, since an empty DXF also exists.
    /// </summary>
    [Fact]
    public void Flat_WritesTheBlankAndItsBendLines()
    {
        var path = TempFile(".dxf");
        try
        {
            Assert.Equal(0, EngrCad.Run(["--flat", path], () => SheetScene()));
            string text = File.ReadAllText(path);
            Assert.Contains("CUT", text, StringComparison.Ordinal);
            Assert.Contains("BEND", text, StringComparison.Ordinal);
            // One bend, two tangent lines, and the CENTER line type the layer declares.
            Assert.Contains("CENTER", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Flat_WritesTheBendTableAsText()
    {
        var path = TempFile(".txt");
        try
        {
            Assert.Equal(0, EngrCad.Run(["--flat", path], () => SheetScene()));
            string[] lines = File.ReadAllLines(path);
            Assert.StartsWith("BEND", lines[0], StringComparison.Ordinal);
            Assert.Equal(2, lines.Count(l => l.Trim().Length > 0));   // header plus one bend
            Assert.Contains("UP", lines[1], StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Each sheet part has its OWN blank, so one path cannot serve two: with
    /// several the stem takes the part's name. Guessing one would be a wrong answer
    /// wearing a right one's clothes.</summary>
    [Fact]
    public void Flat_WithSeveralSheetParts_WritesOneFileEach()
    {
        var path = TempFile(".dxf");
        string stem = Path.Combine(
            Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        try
        {
            Assert.Equal(0, EngrCad.Run(["--flat", path], () => SheetScene(2)));
            Assert.False(File.Exists(path));
            Assert.True(File.Exists($"{stem}-panel0.dxf"));
            Assert.True(File.Exists($"{stem}-panel1.dxf"));
        }
        finally
        {
            foreach (string file in new[] { path, $"{stem}-panel0.dxf", $"{stem}-panel1.dxf" })
                File.Delete(file);
        }
    }

    [Fact]
    public void Flat_MissingPathBadExtensionOrNoSheetPart_FailsLoudly()
    {
        Assert.Equal(2, EngrCad.Run(["--flat"], () => SheetScene()));
        Assert.Equal(2, EngrCad.Run(["--flat", TempFile(".step")], () => SheetScene()));
        // A scene with no sheet body has no flat pattern; that is an error, not an
        // empty file (exit 1, the "nothing to export" code).
        Assert.Equal(1, EngrCad.Run(["--flat", TempFile(".dxf")], BracketScene));
    }

    [Fact]
    public void Export_MissingPathOrBadExtension_FailsWithUsageCode()
    {
        Assert.Equal(2, EngrCad.Run(["--export"], BracketScene));
        Assert.Equal(2, EngrCad.Run(["--export", TempFile(".xyz")], BracketScene));
    }

    [Fact]
    public void Export_HonorsDebugModifiers()
    {
        // Three boxes: one normal, one Hidden (* analog), one Ghost (% analog) — only
        // the normal one may reach the file (8 corner vertices in the merged OBJ).
        var path = TempFile(".obj");
        try
        {
            int code = EngrCad.Run(["--export", path], () =>
            {
                var scene = new Scene();
                scene.Add(new Part("keep", MeshPrimitives.Box(1, 1, 1)));
                scene.Add(new Part("hidden", MeshPrimitives.Box(1, 1, 1),
                    transform: Matrix4d.CreateTranslation((5, 0, 0)))).Hidden = true;
                scene.Add(new Part("ghost", MeshPrimitives.Box(1, 1, 1),
                    transform: Matrix4d.CreateTranslation((10, 0, 0)))).Ghost = true;
                return scene;
            });
            Assert.Equal(0, code);
            var lines = File.ReadAllLines(path);
            Assert.Equal(8, lines.Count(l => l.StartsWith("v ")));
            Assert.Single(lines, l => l.StartsWith("o "));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExportOff_WritesAMergedOffFile()
    {
        var path = TempFile(".off");
        try
        {
            int code = EngrCad.Run(["--export", path], BracketScene);
            Assert.Equal(0, code);
            var result = MeshReader.ReadFile(path);
            Assert.NotNull(result.Mesh);
            Assert.True(result.Mesh!.IsClosed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Export3mf_WritesAValidPackage()
    {
        var path = TempFile(".3mf");
        try
        {
            int code = EngrCad.Run(["--export", path], BracketScene);
            Assert.Equal(0, code);
            using var archive = System.IO.Compression.ZipFile.OpenRead(path);
            Assert.NotNull(archive.GetEntry("3D/3dmodel.model"));
            Assert.NotNull(archive.GetEntry("[Content_Types].xml"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExportAmf_WritesObjectsWithNames()
    {
        var path = TempFile(".amf");
        try
        {
            int code = EngrCad.Run(["--export", path], BracketScene);
            Assert.Equal(0, code);
            var document = System.Xml.Linq.XDocument.Load(path);
            Assert.Equal("amf", document.Root!.Name.LocalName);
            Assert.Contains(document.Root.Elements("object"),
                o => o.Element("metadata")?.Value == "bracket");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
