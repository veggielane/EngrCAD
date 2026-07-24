using EngrCAD.Modeling;
using EngrCAD.Viewer;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// <see cref="EngrCad.Configure()"/> / <see cref="EngrCadBuilder"/> — headless paths
/// only (no Avalonia lifetime): configuration accumulation, quality precedence through
/// exports, and the <see cref="IEngrCadLog"/> seam.
/// </summary>
public class BuilderTests
{
    private static string TempFile(string extension) =>
        Path.Combine(Path.GetTempPath(), $"engrcad-test-{Guid.NewGuid():N}{extension}");

    private sealed class ListLog : IEngrCadLog
    {
        public List<string> Infos { get; } = [];
        public List<string> Errors { get; } = [];
        public void Info(string message) => Infos.Add(message);
        public void Error(string message) => Errors.Add(message);
    }

    // Fresh scene/part per call: Part.GetMesh caches the first caller's quality.
    private static Scene CylinderScene() =>
        Fill(new Scene());

    private static Scene Fill(Scene scene)
    {
        scene.Add(new Part("cyl", Shape.Cylinder(1, 2)));
        return scene;
    }

    [Fact]
    public void Configure_AccumulatesOptions()
    {
        var quality = new MeshQuality { SdfResolution = 128 };
        var log = new ListLog();
        Action<ViewportControl> ready = _ => { };

        var options = EngrCad.Configure()
            .WithTitle("bracket")
            .WithQuality(quality)
            .WithRenderSize(1920, 1080)
            .WithLog(log)
            .WithViewportReady(ready)
            .Options;

        Assert.Equal("bracket", options.Title);
        Assert.Same(quality, options.Quality);
        Assert.Equal(1920, options.RenderWidth);
        Assert.Equal(1080, options.RenderHeight);
        Assert.Same(log, options.Log);
        Assert.Same(ready, options.OnViewportReady);
    }

    [Fact]
    public void Configure_WrapsAnExistingOptionsInstance()
    {
        // The IOptions<EngrCadOptions> pattern: DI provides the POCO, Configure uses it.
        var provided = new EngrCadOptions { Title = "from-di", RenderWidth = 640 };
        var builder = EngrCad.Configure(provided);
        Assert.Same(provided, builder.Options);

        builder.WithRenderSize(800, 600);
        Assert.Equal(800, provided.RenderWidth); // mutates the same instance
    }

    [Fact]
    public void Builder_RejectsInvalidConfiguration()
    {
        Assert.Throws<ArgumentException>(() => EngrCad.Configure().WithTitle(" "));
        Assert.Throws<ArgumentOutOfRangeException>(() => EngrCad.Configure().WithRenderSize(0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => EngrCad.Configure().WithRenderSize(100, -1));
        Assert.Throws<ArgumentNullException>(() => EngrCad.Configure().WithQuality(null!));
        Assert.Throws<ArgumentNullException>(() => EngrCad.Configure().WithLog((IEngrCadLog)null!));
        Assert.Throws<ArgumentNullException>(() => EngrCad.Configure(null!));
    }

    [Fact]
    public void Run_RoutesMessagesThroughTheLogSeam()
    {
        var log = new ListLog();
        var builder = EngrCad.Configure().WithLog(log);

        // Usage error: reported through the seam, not the console.
        Assert.Equal(2, builder.Run(["--export"], CylinderScene));
        Assert.Contains(log.Errors, m => m.Contains("--export requires"));
        Assert.Empty(log.Infos);

        // Success: the "wrote ..." confirmation goes through the seam too.
        var path = TempFile(".stl");
        try
        {
            Assert.Equal(0, builder.Run(["--export", path], CylinderScene));
            Assert.Contains(log.Infos, m => m.Contains("wrote") && m.Contains(".stl"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WithLog_DelegateOverload_CapturesMessages()
    {
        var messages = new List<string>();
        int code = EngrCad.Configure()
            .WithLog(messages.Add)
            .Run(["--render"], CylinderScene); // missing path → usage error
        Assert.Equal(2, code);
        Assert.Contains(messages, m => m.Contains("--render requires"));
    }

    [Fact]
    public void ExportObj_UsesBuilderQuality_WhenSceneHasNoExplicitOptions()
    {
        int coarse = ObjVertexCount(q => q.WithQuality(new MeshQuality { SegmentsPerCircle = 8 }), new Scene());
        int fine = ObjVertexCount(q => q.WithQuality(new MeshQuality { SegmentsPerCircle = 48 }), new Scene());
        Assert.True(coarse < fine, $"expected builder quality to drive tessellation ({coarse} vs {fine})");

        // A scene that chose its own quality wins over the builder's.
        int sceneWins = ObjVertexCount(
            q => q.WithQuality(new MeshQuality { SegmentsPerCircle = 48 }),
            new Scene(new MeshQuality { SegmentsPerCircle = 8 }));
        Assert.Equal(coarse, sceneWins);
    }

    private static int ObjVertexCount(Func<EngrCadBuilder, EngrCadBuilder> configure, Scene scene)
    {
        var path = TempFile(".obj");
        try
        {
            var builder = configure(EngrCad.Configure().WithLog(new ListLog()));
            Assert.Equal(0, builder.Run(["--export", path], () => Fill(scene)));
            return File.ReadAllLines(path).Count(l => l.StartsWith("v "));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
