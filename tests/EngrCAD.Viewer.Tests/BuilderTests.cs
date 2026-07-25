using EngrCAD.Modeling;
using EngrCAD.Viewer;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// <see cref="EngrCad.Configure()"/> / <see cref="EngrCadBuilder"/> — headless paths
/// only (no Avalonia lifetime): configuration accumulation, quality precedence through
/// exports, and the <c>ILogger</c> seam.
/// </summary>
public class BuilderTests
{
    private static string TempFile(string extension) =>
        Path.Combine(Path.GetTempPath(), $"engrcad-test-{Guid.NewGuid():N}{extension}");

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
        var log = new ListLogger();
        Action<ViewportControl> ready = _ => { };

        var options = EngrCad.Configure()
            .WithTitle("bracket")
            .WithQuality(quality)
            .WithRenderSize(1920, 1080)
            .WithLogger(log)
            .WithViewportReady(ready)
            .Options;

        Assert.Equal("bracket", options.Title);
        Assert.Same(quality, options.Quality);
        Assert.Equal(1920, options.RenderWidth);
        Assert.Equal(1080, options.RenderHeight);
        Assert.Same(log, options.Logger);
        Assert.Same(ready, options.OnViewportReady);
    }

    [Fact]
    public void Configure_AccumulatesRenderStyleAndSection()
    {
        var options = EngrCad.Configure()
            .WithViewStyle(ViewStyle.Wireframe)
            .WithSection(SectionAxis.X, 2.5)
            .Options;

        Assert.Equal(ViewStyle.Wireframe, options.RenderStyle);
        Assert.Equal(SectionAxis.X, options.SectionAxis);
        Assert.Equal(2.5, options.SectionOffset);

        // Defaults: shaded-with-edges, no section.
        var defaults = EngrCad.Configure().Options;
        Assert.Equal(ViewStyle.ShadedWithEdges, defaults.RenderStyle);
        Assert.Equal(SectionAxis.Z, defaults.SectionAxis);
        Assert.Null(defaults.SectionOffset);
    }

    [Fact]
    public void Run_ParsesRenderStyleSwitch()
    {
        // Valid values land in the options; the render itself is never reached (the
        // arg after --render is not a .png, rejected after parsing succeeds — no GL).
        foreach (var (spelling, expected) in new (string, ViewStyle)[]
        {
            ("points", ViewStyle.Points),
            ("wireframe", ViewStyle.Wireframe),
            ("shaded", ViewStyle.Shaded),
            ("shaded-edges", ViewStyle.ShadedWithEdges),
            ("SHADED-EDGES", ViewStyle.ShadedWithEdges),   // case-insensitive
        })
        {
            var options = new EngrCadOptions { Logger = new ListLogger() };
            Assert.Equal(2, EngrCad.Configure(options).Run(
                ["--render", "--render-style", spelling], CylinderScene));
            Assert.Equal(expected, options.RenderStyle);
        }

        // Invalid or missing values are usage errors (exit 2) with a hint.
        var log = new ListLogger();
        Assert.Equal(2, EngrCad.Configure().WithLogger(log)
            .Run(["--render", "out.png", "--render-style", "bogus"], CylinderScene));
        Assert.Contains(log.Errors, m => m.Contains("--render-style"));

        log = new ListLogger();
        Assert.Equal(2, EngrCad.Configure().WithLogger(log)
            .Run(["--render", "out.png", "--render-style"], CylinderScene));
        Assert.Contains(log.Errors, m => m.Contains("--render-style"));
    }

    [Fact]
    public void Run_ParsesSectionSwitch()
    {
        // Valid switch parses into the options before the (deliberately bad, non-.png)
        // render path is rejected — no GL is touched.
        var options = new EngrCadOptions { Logger = new ListLogger() };
        Assert.Equal(2, EngrCad.Configure(options).Run(
            ["--render", "--section", "y", "-3.5"], CylinderScene));
        Assert.Equal(SectionAxis.Y, options.SectionAxis);
        Assert.Equal(-3.5, options.SectionOffset);

        // Bad axis, non-numeric offset, and missing offset are all usage errors.
        foreach (var args in new string[][]
        {
            ["--render", "out.png", "--section", "q", "5"],
            ["--render", "out.png", "--section", "z", "tall"],
            ["--render", "out.png", "--section", "z"],
        })
        {
            var log = new ListLogger();
            Assert.Equal(2, EngrCad.Configure().WithLogger(log).Run(args, CylinderScene));
            Assert.Contains(log.Errors, m => m.Contains("--section"));
        }
    }

    [Fact]
    public void LazyTabMeshing_IsOnByDefaultAndOptOutIsOneFlag()
    {
        Assert.True(new EngrCadOptions().LazyTabMeshing);
        Assert.True(EngrCad.Configure().Options.LazyTabMeshing);

        Assert.False(EngrCad.Configure().WithLazyTabMeshing(false).Options.LazyTabMeshing);
        Assert.True(EngrCad.Configure().WithLazyTabMeshing(false).WithLazyTabMeshing().Options.LazyTabMeshing);
    }

    [Fact]
    public void Run_ParsesMeshModeSwitch()
    {
        // Parsed before the (deliberately bad, non-.png) render path is rejected — no GL.
        foreach (var (spelling, expected) in new (string, bool)[]
        {
            ("lazy", true),
            ("on-demand", true),
            ("all", false),
            ("eager", false),
            ("ALL", false),   // case-insensitive
        })
        {
            var options = new EngrCadOptions { Logger = new ListLogger() };
            Assert.Equal(2, EngrCad.Configure(options).Run(["--render", "--mesh", spelling], CylinderScene));
            Assert.Equal(expected, options.LazyTabMeshing);
        }

        foreach (var args in new string[][]
        {
            ["--render", "out.png", "--mesh", "sometimes"],
            ["--render", "out.png", "--mesh"],
        })
        {
            var log = new ListLogger();
            Assert.Equal(2, EngrCad.Configure().WithLogger(log).Run(args, CylinderScene));
            Assert.Contains(log.Errors, m => m.Contains("--mesh"));
        }
    }

    [Fact]
    public void HeadlessPathsMeshWhatTheyNeed_WhateverTheLazyFlagSays()
    {
        // --export and --render prepare their own geometry, so the lazy default (which
        // only governs the window) must not leave them with nothing to write.
        var path = TempFile(".stl");
        try
        {
            var log = new ListLogger();
            Assert.Equal(0, EngrCad.Configure().WithLogger(log).Run(["--export", path], CylinderScene));
            Assert.True(new FileInfo(path).Length > 84);   // header + at least one triangle
        }
        finally
        {
            File.Delete(path);
        }
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
        Assert.Throws<ArgumentNullException>(() => EngrCad.Configure().WithLogger(null!));
        Assert.Throws<ArgumentNullException>(() => EngrCad.Configure(null!));
    }

    [Fact]
    public void Run_RoutesMessagesThroughTheLogSeam()
    {
        var log = new ListLogger();
        var builder = EngrCad.Configure().WithLogger(log);

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
    public void NullLogger_SilencesTheEntryPoints()
    {
        // The opt-in silence: the exit code still reports the usage error, nothing is
        // written. (The default is deliberately the console sink, not this -- Run is a
        // program's front door; see EngrCadLoggers.)
        int code = EngrCad.Configure()
            .WithLogger(NullLogger.Instance)
            .Run(["--render"], CylinderScene); // missing path → usage error
        Assert.Equal(2, code);
    }

    [Fact]
    public void WithLoggerFactory_CreatesAnEngrCadCategoryLogger()
    {
        var factory = new RecordingLoggerFactory();
        int code = EngrCad.Configure()
            .WithLoggerFactory(factory)
            .Run(["--render"], CylinderScene);

        Assert.Equal(2, code);
        Assert.Equal("EngrCAD", factory.Category);
        Assert.Contains(factory.Logger.Errors, m => m.Contains("--render requires"));
        Assert.Throws<ArgumentNullException>(() => EngrCad.Configure().WithLoggerFactory(null!));
    }

    [Fact]
    public void MessagesCarryStableEventIds()
    {
        // Event IDs are the contract a structured sink keys on, so they are asserted
        // rather than left to drift with the message text.
        var log = new ListLogger();
        Assert.Equal(2, EngrCad.Configure().WithLogger(log).Run(["--export"], CylinderScene));
        Assert.Equal([14], log.EventIds);

        var path = TempFile(".stl");
        try
        {
            log = new ListLogger();
            Assert.Equal(0, EngrCad.Configure().WithLogger(log).Run(["--export", path], CylinderScene));
            Assert.Equal([22], log.EventIds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class RecordingLoggerFactory : Microsoft.Extensions.Logging.ILoggerFactory
    {
        public ListLogger Logger { get; } = new();
        public string? Category { get; private set; }

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName)
        {
            Category = categoryName;
            return Logger;
        }

        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) { }
        public void Dispose() { }
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
            var builder = configure(EngrCad.Configure().WithLogger(new ListLogger()));
            Assert.Equal(0, builder.Run(["--export", path], () => Fill(scene)));
            return File.ReadAllLines(path).Count(l => l.StartsWith("v "));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
