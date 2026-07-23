using EngrCAD.Core;
using EngrCAD.Interop;
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
        scene.Add("bracket", Shape.Box(4, 3, 1) - Shape.Cylinder(0.5, 3));
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
                scene.Add("a", MeshPrimitives.Box(1, 1, 1));
                scene.Add("b", MeshPrimitives.Box(1, 1, 1),
                    transform: Matrix4d.CreateTranslation((10, 0, 0)));
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
    public void ExportStep_NonBrepScene_FailsWithExitCode()
    {
        var path = TempFile(".step");
        try
        {
            int code = EngrCad.Run(["--export", path], () =>
            {
                var scene = new Scene();
                scene.Add("blend", Shape.Sphere(1).SmoothUnion(Shape.Sphere(1).Translate(1, 0, 0), 0.3));
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

    [Fact]
    public void Export_MissingPathOrBadExtension_FailsWithUsageCode()
    {
        Assert.Equal(2, EngrCad.Run(["--export"], BracketScene));
        Assert.Equal(2, EngrCad.Run(["--export", TempFile(".stl")], BracketScene));
    }
}
