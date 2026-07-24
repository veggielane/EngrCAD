using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

/// <summary>
/// The library entry point for design code: build a <see cref="Scene"/>, call
/// <see cref="Show"/> — or hand a scene *factory* to <see cref="ShowLive"/> and edit
/// the model under <c>dotnet watch</c> with in-place reload. <see cref="Run"/> wraps
/// both plus headless export behind standard command-line switches.
/// </summary>
public static class EngrCad
{
    internal static Scene? InitialScene;
    internal static string WindowTitle = "EngrCAD";
    internal static Action<ViewportControl>? OnViewportReady;
    internal static SceneHost? Host;

    /// <summary>The options the running session was started with (defaults when the
    /// plain overloads were used) — hot reload and the overlay read these.</summary>
    internal static EngrCadOptions CurrentOptions = new();

    private static Func<Scene>? _liveFactory;
    private static ViewportControl? _liveViewport;
    private static int _reloadScheduled;

    /// <summary>
    /// Starts fluent configuration of the viewer entry points — set defaults such as
    /// title, mesh quality, render size, and a log sink, then finish with
    /// <see cref="EngrCadBuilder.Run"/>/<see cref="EngrCadBuilder.Show"/>/
    /// <see cref="EngrCadBuilder.ShowLive"/>/<see cref="EngrCadBuilder.RenderToImage"/>.
    /// </summary>
    public static EngrCadBuilder Configure() => new(new EngrCadOptions());

    /// <summary>
    /// Fluent configuration seeded from an existing <see cref="EngrCadOptions"/> —
    /// the DI-friendly entry (<c>EngrCad.Configure(options.Value).Run(args, Build)</c>
    /// with <c>IOptions&lt;EngrCadOptions&gt;</c>). The instance is used directly, not
    /// copied.
    /// </summary>
    public static EngrCadBuilder Configure(EngrCadOptions options) =>
        new(options ?? throw new ArgumentNullException(nameof(options)));

    /// <summary>
    /// Opens the viewer showing <paramref name="scene"/> and blocks until it is closed.
    /// Avalonia allows one application lifetime per process, so call this at most once;
    /// hosts that need live updates use the <paramref name="onViewportReady"/> callback
    /// to capture the viewport and later call <see cref="ViewportControl.SetParts"/>.
    /// </summary>
    public static void Show(Scene scene, string title = "EngrCAD", Action<ViewportControl>? onViewportReady = null) =>
        ShowCore(scene, new EngrCadOptions { Title = title, OnViewportReady = onViewportReady });

    internal static void ShowCore(Scene scene, EngrCadOptions options, Action<ViewportControl>? hostReady = null)
    {
        CurrentOptions = options;
        scene.PreMesh(options.Quality); // tessellate here, not on the render thread
        InitialScene = scene;
        WindowTitle = options.Title;
        var userReady = options.OnViewportReady;
        OnViewportReady = hostReady is null && userReady is null
            ? null
            : viewport =>
            {
                hostReady?.Invoke(viewport); // internal plumbing (live loop) first
                userReady?.Invoke(viewport);
            };
        BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
    }

    /// <summary>
    /// Renders <paramref name="scene"/> to a PNG file with no window opened — the
    /// headless screenshot path for tests and AI agents inspecting a design. Uses an
    /// offscreen ANGLE pbuffer; the look matches the viewer (background gradient, grid,
    /// directional light, part colors, feature edges). A null <paramref name="camera"/>
    /// auto-frames an iso view like the viewer's first visit.
    /// <paramref name="style"/> is the global view style (per-part
    /// <c>Part.DisplayMode</c> overrides it where explicitly non-default — same
    /// precedence as the window; see <see cref="ViewStyle"/>); a non-null
    /// <paramref name="sectionOffset"/> enables an axis-aligned section plane
    /// perpendicular to <paramref name="sectionAxis"/>, matching the viewer's Section
    /// toggle. Throws <see cref="InvalidOperationException"/> when no GL context can
    /// be created; query <see cref="CanRenderToImage"/> first to skip gracefully on
    /// headless CI.
    /// </summary>
    public static void RenderToImage(
        Scene scene, string path, int width = 1280, int height = 800, CameraState? camera = null,
        ViewStyle style = ViewStyle.ShadedWithEdges,
        SectionAxis sectionAxis = SectionAxis.Z, double? sectionOffset = null)
    {
        scene.PreMesh(); // tessellate before touching GL
        OffscreenRenderer.RenderToImage([.. scene.AllInstances], path, width, height, camera,
            furniture: true, style, sectionAxis, sectionOffset);
    }

    /// <summary>Whether <see cref="RenderToImage"/> can run on this machine (a GL/EGL
    /// context is obtainable). False on machines with no GPU/ANGLE, with the reason in
    /// <see cref="OffscreenRenderer.UnavailableReason"/>.</summary>
    public static bool CanRenderToImage => OffscreenRenderer.IsAvailable;

    /// <summary>
    /// The live-modeling loop: shows the scene built by <paramref name="sceneFactory"/>
    /// and re-invokes it whenever <c>dotnet watch</c> hot-reloads the process, swapping
    /// the result in with the camera preserved. If the factory throws, the last good
    /// scene stays and the error appears in the overlay. Rude edits restart the process;
    /// the camera pose is persisted per title and restored, so the view survives those
    /// too. Blocks until the window closes.
    /// </summary>
    public static void ShowLive(Func<Scene> sceneFactory, string title = "EngrCAD") =>
        ShowLiveCore(sceneFactory, new EngrCadOptions { Title = title });

    internal static void ShowLiveCore(Func<Scene> sceneFactory, EngrCadOptions options)
    {
        _liveFactory = sceneFactory;
        var log = options.Log ?? EngrCadLog.Console;
        string title = options.Title;

        Scene scene;
        string? startupError = null;
        try
        {
            scene = sceneFactory();
        }
        catch (Exception e)
        {
            scene = new Scene();
            startupError = Describe(e);
        }

        if (startupError is not null)
            log.Error($"model error: {startupError} (showing empty scene)");

        ShowCore(scene, options, viewport =>
        {
            _liveViewport = viewport;
            if (TryLoadCamera(title) is { } camera)
                viewport.Camera = camera;
            if (startupError is not null)
                viewport.ShowStatus($"model error: {startupError} (showing empty scene)");
        });

        // Window closed (or dotnet watch is restarting after a rude edit): persist the
        // camera so the next session picks up where this one left off.
        if (_liveViewport is { } vp)
            SaveCamera(title, vp.Camera);
        _liveViewport = null;
        _liveFactory = null;
    }

    /// <summary>
    /// Standard main-method wrapper for model programs:
    /// no arguments → <see cref="ShowLive"/>; <c>--view</c> → static <see cref="Show"/>;
    /// <c>--export path.step|path.obj</c> → headless export, no window (CI-friendly);
    /// <c>--render path.png</c> → headless offscreen screenshot, no window.
    /// Returns a process exit code.
    /// </summary>
    public static int Run(string[] args, Func<Scene> sceneFactory, string title = "EngrCAD") =>
        RunCore(args, sceneFactory, new EngrCadOptions { Title = title });

    internal static int RunCore(string[] args, Func<Scene> sceneFactory, EngrCadOptions options)
    {
        var log = options.Log ?? EngrCadLog.Console;

        int exportIndex = Array.IndexOf(args, "--export");
        if (exportIndex >= 0)
        {
            if (exportIndex + 1 >= args.Length)
            {
                log.Error("--export requires a file path (.step or .obj)");
                return 2;
            }
            return Export(sceneFactory(), args[exportIndex + 1], options, log);
        }

        int renderIndex = Array.IndexOf(args, "--render");
        if (renderIndex >= 0)
        {
            if (renderIndex + 1 >= args.Length)
            {
                log.Error("--render requires a file path (.png)");
                return 2;
            }
            return RenderHeadless(sceneFactory(), args[renderIndex + 1], options, log);
        }

        if (args.Contains("--view"))
        {
            ShowCore(sceneFactory(), options);
            return 0;
        }

        ShowLiveCore(sceneFactory, options);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    // ---- hot reload ----

    /// <summary>Called by <see cref="HotReloadHandler"/> after dotnet watch patches code.</summary>
    internal static void OnHotReload()
    {
        if (_liveFactory is null || _liveViewport is null)
            return;
        // One rebuild per batch of updates (the handler can fire several times per save).
        if (Interlocked.Exchange(ref _reloadScheduled, 1) == 1)
            return;

        Task.Run(async () =>
        {
            await Task.Delay(150);
            Interlocked.Exchange(ref _reloadScheduled, 0);
            var factory = _liveFactory;
            var viewport = _liveViewport;
            if (factory is null || viewport is null)
                return;
            var log = CurrentOptions.Log ?? EngrCadLog.Console;
            try
            {
                var scene = factory();
                scene.PreMesh(CurrentOptions.Quality); // heavy lifting stays on this worker thread
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Host?.SetScene(scene));
                string status = $"reloaded at {DateTime.Now:HH:mm:ss} — {scene.AllParts.Count()} part(s)";
                viewport.ShowStatus(status);
                log.Info(status);
            }
            catch (Exception e)
            {
                string status = $"model error: {Describe(e)} (keeping last good scene)";
                viewport.ShowStatus(status);
                log.Error(status);
            }
        });
    }

    private static string Describe(Exception e)
    {
        while (e is System.Reflection.TargetInvocationException { InnerException: { } inner })
            e = inner;
        return $"{e.GetType().Name}: {e.Message}";
    }

    // ---- camera persistence (survives dotnet watch process restarts) ----

    private static string CameraFile(string title)
    {
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{Environment.CurrentDirectory}|{title}")))[..16];
        return Path.Combine(Path.GetTempPath(), $"engrcad-camera-{hash}.txt");
    }

    private static void SaveCamera(string title, CameraState camera)
    {
        try
        {
            File.WriteAllText(CameraFile(title), string.Create(CultureInfo.InvariantCulture,
                $"{camera.Yaw:R} {camera.Pitch:R} {camera.Distance:R} {camera.Target.X:R} {camera.Target.Y:R} {camera.Target.Z:R}"));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static CameraState? TryLoadCamera(string title)
    {
        try
        {
            var file = new FileInfo(CameraFile(title));
            // Only a *recent* pose is a watch-restart continuation; a stale one from an
            // earlier day would fight the auto-framing of a possibly different model.
            if (!file.Exists || DateTime.UtcNow - file.LastWriteTimeUtc > TimeSpan.FromMinutes(30))
                return null;
            var parts = File.ReadAllText(file.FullName).Split(' ');
            if (parts.Length != 6)
                return null;
            double[] v = [.. parts.Select(p => double.Parse(p, CultureInfo.InvariantCulture))];
            return new CameraState(v[0], v[1], v[2], new Vector3d(v[3], v[4], v[5]));
        }
        catch (IOException) { return null; }
        catch (FormatException) { return null; }
    }

    // ---- headless render ----

    private static int RenderHeadless(Scene scene, string path, EngrCadOptions options, IEngrCadLog log)
    {
        if (!scene.AllParts.Any())
        {
            log.Error("The scene has no parts to render.");
            return 1;
        }
        if (!Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            log.Error($"Unsupported render format '{Path.GetExtension(path)}' — use .png.");
            return 2;
        }
        if (!OffscreenRenderer.IsAvailable)
        {
            log.Error($"Offscreen rendering is not available: {OffscreenRenderer.UnavailableReason}");
            return 1;
        }
        scene.PreMesh(options.Quality); // meshes cache, so RenderToImage's PreMesh is a no-op
        RenderToImage(scene, path, options.RenderWidth, options.RenderHeight);
        log.Info($"wrote {path} ({scene.AllParts.Count()} part(s))");
        return 0;
    }

    // ---- headless export ----

    private static int Export(Scene scene, string path, EngrCadOptions options, IEngrCadLog log)
    {
        if (!scene.AllParts.Any())
        {
            log.Error("The scene has no parts to export.");
            return 1;
        }

        var quality = scene.ResolveQuality(options.Quality);
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".obj":
                WriteMergedObj(scene, path, quality);
                log.Info($"wrote {path} ({scene.AllInstances.Count()} instance(s), merged)");
                return 0;

            case ".stl":
                StlWriter.WriteFile(
                    [.. scene.AllInstances.Select(i => (i.Part.GetMesh(quality), i.World))], path);
                log.Info($"wrote {path} ({scene.AllInstances.Count()} instance(s), merged binary STL)");
                return 0;

            case ".step" or ".stp":
                return ExportStep(scene, path, log);

            default:
                log.Error($"Unsupported export format '{Path.GetExtension(path)}' — use .step, .stl, or .obj.");
                return 2;
        }
    }

    private static int ExportStep(Scene scene, string path, IEngrCadLog log)
    {
        var solids = new List<(string Name, BrepSolid Solid)>();
        foreach (var part in scene.AllParts)
        {
            switch (part.Geometry)
            {
                case BrepSolid solid:
                    solids.Add((part.Name, solid));
                    break;
                case Shape shape when shape.CanConvertTo(TargetRep.Brep):
                    solids.Add((part.Name, shape.ToBrep()));
                    break;
                default:
                    log.Error($"skipping '{part.Name}': not B-Rep-representable (STEP needs exact solids)");
                    break;
            }
        }
        if (solids.Count == 0)
        {
            log.Error("No B-Rep-representable parts; nothing exported.");
            return 1;
        }

        if (solids.Count == 1)
        {
            StepWriter.WriteFile(solids[0].Solid, path, solids[0].Name);
            log.Info($"wrote {path} ('{solids[0].Name}')");
            return 0;
        }

        // Multiple solids: one file per part, suffixed with a sanitized part name.
        string directory = Path.GetDirectoryName(path) ?? "";
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        foreach (var (name, solid) in solids)
        {
            var safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
            var partPath = Path.Combine(directory, $"{stem}.{safe}{extension}");
            StepWriter.WriteFile(solid, partPath, name);
            log.Info($"wrote {partPath} ('{name}')");
        }
        return 0;
    }

    /// <summary>All part instances merged into one OBJ (assemblies flattened), with
    /// each instance's composed world transform applied.</summary>
    private static void WriteMergedObj(Scene scene, string path, MeshQuality quality)
    {
        var culture = CultureInfo.InvariantCulture;
        using var writer = new StreamWriter(path);
        int offset = 1; // OBJ is 1-based
        foreach (var instance in scene.AllInstances)
        {
            writer.WriteLine($"o {instance.Path.Replace(' ', '_')}");
            var (positions, faces) = instance.Part.GetMesh(quality).ToIndexed();
            foreach (var position in positions)
            {
                var p = instance.World.TransformPoint(position);
                writer.WriteLine(string.Create(culture, $"v {p.X:R} {p.Y:R} {p.Z:R}"));
            }
            foreach (var face in faces)
            {
                writer.Write('f');
                foreach (int v in face)
                {
                    writer.Write(' ');
                    writer.Write((v + offset).ToString(culture));
                }
                writer.WriteLine();
            }
            offset += positions.Length;
        }
    }
}
