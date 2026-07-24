using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

/// <summary>
/// The minimal logging seam for viewer entry points (<see cref="EngrCad.Run"/>,
/// exports, the live-reload overlay). Deliberately not <c>Microsoft.Extensions.Logging</c>
/// so the viewer carries no framework dependency — a consumer that uses
/// <c>ILogger</c> adapts it in a few lines (see the README's "Configuring" section).
/// The default when none is configured is <see cref="EngrCadLog.Console"/>.
/// </summary>
public interface IEngrCadLog
{
    /// <summary>Progress and success messages ("wrote part.step", "reloaded at ...").</summary>
    void Info(string message);

    /// <summary>Failures and warnings (usage errors, model exceptions, skipped parts).</summary>
    void Error(string message);
}

/// <summary>Ready-made <see cref="IEngrCadLog"/> implementations.</summary>
public static class EngrCadLog
{
    /// <summary>The default log: Info to stdout, Error to stderr — the historical
    /// console behavior of <see cref="EngrCad.Run"/>.</summary>
    public static IEngrCadLog Console { get; } = new ConsoleLog();

    /// <summary>Adapts plain delegates to <see cref="IEngrCadLog"/>; with only
    /// <paramref name="info"/> given, errors go through the same delegate.</summary>
    public static IEngrCadLog From(Action<string> info, Action<string>? error = null) =>
        new DelegateLog(info, error ?? info);

    private sealed class ConsoleLog : IEngrCadLog
    {
        public void Info(string message) => System.Console.WriteLine(message);
        public void Error(string message) => System.Console.Error.WriteLine(message);
    }

    private sealed class DelegateLog(Action<string> info, Action<string> error) : IEngrCadLog
    {
        public void Info(string message) => info(message);
        public void Error(string message) => error(message);
    }
}

/// <summary>
/// Host-level defaults for the viewer entry points — a plain POCO so it binds
/// directly as <c>IOptions&lt;EngrCadOptions&gt;</c> in a generic-host app (no
/// Microsoft.Extensions dependency here; delegate/interface properties are simply
/// left unbound by configuration). Build one by hand, from DI, or fluently via
/// <see cref="EngrCad.Configure()"/>.
/// </summary>
public sealed class EngrCadOptions
{
    /// <summary>Window title (also keys the persisted live-reload camera pose).</summary>
    public string Title { get; set; } = "EngrCAD";

    /// <summary>
    /// Default mesh quality for display and mesh export. Precedence: a
    /// <see cref="Scene"/> constructed with explicit options always wins; otherwise
    /// this quality; otherwise <c>MeshQuality</c>'s defaults
    /// (<see cref="Scene.ResolveQuality"/> implements the rule).
    /// </summary>
    public MeshQuality? Quality { get; set; }

    /// <summary>Image width in pixels for <c>--render</c> /
    /// <see cref="EngrCadBuilder.RenderToImage"/>.</summary>
    public int RenderWidth { get; set; } = 1280;

    /// <summary>Image height in pixels for <c>--render</c> /
    /// <see cref="EngrCadBuilder.RenderToImage"/>.</summary>
    public int RenderHeight { get; set; } = 800;

    /// <summary>Where status/error reporting goes (exports, headless renders, the
    /// live-reload messages that also appear in the overlay). Null = console.</summary>
    public IEngrCadLog? Log { get; set; }

    /// <summary>Callback invoked once the GL viewport exists — custom hosts capture
    /// the <see cref="ViewportControl"/> here.</summary>
    public Action<ViewportControl>? OnViewportReady { get; set; }
}

/// <summary>
/// Fluent configuration for the viewer entry points:
/// <code>
/// return EngrCad.Configure()
///     .WithTitle("bracket")
///     .WithQuality(new MeshQuality { SegmentsPerCircle = 48 })
///     .WithRenderSize(1920, 1080)
///     .Run(args, BuildScene);
/// </code>
/// Terminal methods (<see cref="Run"/>, <see cref="Show"/>, <see cref="ShowLive"/>,
/// <see cref="RenderToImage"/>) mirror the static <see cref="EngrCad"/> methods with
/// the accumulated <see cref="Options"/> applied.
/// </summary>
public sealed class EngrCadBuilder
{
    /// <summary>The options being accumulated — the same instance passed to
    /// <see cref="EngrCad.Configure(EngrCadOptions)"/>, so DI-provided options flow
    /// through unchanged.</summary>
    public EngrCadOptions Options { get; }

    internal EngrCadBuilder(EngrCadOptions options) => Options = options;

    /// <summary>Sets the window title.</summary>
    public EngrCadBuilder WithTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title must be non-empty.", nameof(title));
        Options.Title = title;
        return this;
    }

    /// <summary>Sets the default mesh quality (scenes constructed with their own
    /// explicit options still win — see <see cref="Scene.ResolveQuality"/>).</summary>
    public EngrCadBuilder WithQuality(MeshQuality quality)
    {
        Options.Quality = quality ?? throw new ArgumentNullException(nameof(quality));
        return this;
    }

    /// <summary>Sets the headless render image size in pixels.</summary>
    public EngrCadBuilder WithRenderSize(int width, int height)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "Render width must be positive.");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), height, "Render height must be positive.");
        Options.RenderWidth = width;
        Options.RenderHeight = height;
        return this;
    }

    /// <summary>Routes status/error reporting through <paramref name="log"/> instead
    /// of the console.</summary>
    public EngrCadBuilder WithLog(IEngrCadLog log)
    {
        Options.Log = log ?? throw new ArgumentNullException(nameof(log));
        return this;
    }

    /// <summary>Routes status/error reporting through a delegate (e.g.
    /// <c>logger.LogInformation</c> via a one-line lambda).</summary>
    public EngrCadBuilder WithLog(Action<string> log) => WithLog(EngrCadLog.From(log));

    /// <summary>Registers a callback invoked once the GL viewport exists.</summary>
    public EngrCadBuilder WithViewportReady(Action<ViewportControl> callback)
    {
        Options.OnViewportReady = callback ?? throw new ArgumentNullException(nameof(callback));
        return this;
    }

    /// <summary>Standard main-method wrapper — <see cref="EngrCad.Run"/> with these
    /// options (no args → live, <c>--view</c>, <c>--export</c>, <c>--render</c>).</summary>
    public int Run(string[] args, Func<Scene> sceneFactory) =>
        EngrCad.RunCore(args, sceneFactory, Options);

    /// <summary>Opens the viewer on <paramref name="scene"/> and blocks —
    /// <see cref="EngrCad.Show"/> with these options.</summary>
    public void Show(Scene scene) => EngrCad.ShowCore(scene, Options);

    /// <summary>The live-modeling loop — <see cref="EngrCad.ShowLive"/> with these
    /// options.</summary>
    public void ShowLive(Func<Scene> sceneFactory) => EngrCad.ShowLiveCore(sceneFactory, Options);

    /// <summary>Headless PNG render — <see cref="EngrCad.RenderToImage"/> at the
    /// configured size and quality.</summary>
    public void RenderToImage(Scene scene, string path, CameraState? camera = null)
    {
        scene.PreMesh(Options.Quality); // meshes cache, so the inner PreMesh is a no-op
        EngrCad.RenderToImage(scene, path, Options.RenderWidth, Options.RenderHeight, camera);
    }
}
