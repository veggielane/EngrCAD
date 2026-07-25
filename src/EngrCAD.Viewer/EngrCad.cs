using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Microsoft.Extensions.Logging;

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
        // Lazy (the default): the window opens now and each tab meshes on a background
        // task when it is first shown (SceneHost's TabMeshLoader), with a progress bar.
        // Eager: the historical behavior — the whole document is meshed here, off the
        // render thread, and every tab is instant once the window appears.
        if (!options.LazyTabMeshing)
            scene.PreMesh(options.Quality); // tessellate here, not on the render thread
        // Ambient occlusion is deliberately NOT baked here either: it was measured at
        // ~12 s on the demo scene and was the single largest cost of opening a window.
        // The viewport shows the scene flat-lit immediately — which is exactly the AO-off
        // render, not a placeholder — and streams each part's occlusion in as its
        // background bake finishes (see AmbientOcclusion.BakeInBackground).
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
    /// <para>A non-null <paramref name="preview"/> draws one construction-tree row over
    /// the scene exactly as clicking it in the model tree does — the rollback view, in a
    /// still image.</para>
    /// </summary>
    public static void RenderToImage(
        Scene scene, string path, int width = 1280, int height = 800, CameraState? camera = null,
        ViewStyle style = ViewStyle.ShadedWithEdges,
        SectionAxis sectionAxis = SectionAxis.Z, double? sectionOffset = null,
        bool ambientOcclusion = EngrCadOptions.AmbientOcclusionDefault,
        IReadOnlyList<SectionPlane>? sectionPlanes = null,
        SectionCombine sectionCombine = SectionCombine.Intersection,
        ConstructionPreviewRequest? preview = null)
    {
        scene.PreMesh(); // tessellate before touching GL
        var instances = scene.AllInstances.ToList();
        // Building the preview lowers geometry, so it happens HERE, on the caller's
        // thread, before the GL context exists — the headless mirror of the window's
        // background-task rule.
        var (segments, world) = preview is { } request
            ? request.Build(instances, scene.ResolveQuality(CurrentOptions.Quality))
            : (null, Matrix4d.Identity);
        OffscreenRenderer.RenderToImage(instances, path, width, height, camera,
            furniture: true, style, sectionAxis, sectionOffset, ambientOcclusion,
            sectionPlanes, sectionCombine, segments, world);
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
        var log = EngrCadLoggers.Resolve(options);
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
            Log.ModelErrorAtStartup(log, startupError);

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
    /// <c>--render</c> additionally honors
    /// <c>--render-style points|wireframe|shaded|shaded-edges</c> (the global
    /// <see cref="ViewStyle"/> — per-part <c>Part.DisplayMode</c> still overrides where
    /// explicitly non-default) and <c>--section x|y|z &lt;offset&gt;</c> (an
    /// axis-aligned section plane, e.g. <c>--section z 6</c>); both default to the
    /// configured <see cref="EngrCadOptions.RenderStyle"/>/<see cref="EngrCadOptions.SectionOffset"/>.
    /// The windowed modes honor <c>--mesh lazy|all</c> (see
    /// <see cref="EngrCadOptions.LazyTabMeshing"/>). Returns a process exit code.
    /// </summary>
    public static int Run(string[] args, Func<Scene> sceneFactory, string title = "EngrCAD") =>
        RunCore(args, sceneFactory, new EngrCadOptions { Title = title });

    internal static int RunCore(string[] args, Func<Scene> sceneFactory, EngrCadOptions options)
    {
        var log = EngrCadLoggers.Resolve(options);

        // Render options are parsed up front so a typo fails fast with a usage error
        // (exit 2) regardless of which mode was requested; they only affect --render.
        int styleIndex = Array.IndexOf(args, "--render-style");
        if (styleIndex >= 0)
        {
            if (styleIndex + 1 >= args.Length || !TryParseStyle(args[styleIndex + 1], out var style))
            {
                Log.UsageRenderStyle(log);
                return 2;
            }
            options.RenderStyle = style;
        }

        int sectionIndex = Array.IndexOf(args, "--section");
        if (sectionIndex >= 0)
        {
            // One or more axis/offset pairs: "--section z 6" is the single cut,
            // "--section x 0 y 0" the quarter cut, three pairs an octant.
            var planes = new List<SectionPlane>();
            int at = sectionIndex + 1;
            while (at + 1 < args.Length
                   && TryParseAxis(args[at], out var axis)
                   && double.TryParse(args[at + 1], NumberStyles.Float,
                           CultureInfo.InvariantCulture, out double offset))
            {
                planes.Add(SectionPlane.On(axis, offset));
                if (planes.Count == 1)
                {
                    options.SectionAxis = axis;
                    options.SectionOffset = offset;
                }
                at += 2;
            }
            if (planes.Count == 0)
            {
                Log.UsageSection(log);
                return 2;
            }
            options.SectionPlanes = planes.Count > 1 ? planes : null;
        }

        int aoIndex = Array.IndexOf(args, "--ao");
        if (aoIndex >= 0)
        {
            if (aoIndex + 1 >= args.Length || !TryParseSwitch(args[aoIndex + 1], out bool ao))
            {
                Log.UsageAmbientOcclusion(log);
                return 2;
            }
            options.AmbientOcclusion = ao;
        }

        int meshIndex = Array.IndexOf(args, "--mesh");
        if (meshIndex >= 0)
        {
            if (meshIndex + 1 >= args.Length || !TryParseMeshMode(args[meshIndex + 1], out bool lazy))
            {
                Log.UsageMeshMode(log);
                return 2;
            }
            options.LazyTabMeshing = lazy;
        }

        int exportIndex = Array.IndexOf(args, "--export");
        if (exportIndex >= 0)
        {
            if (exportIndex + 1 >= args.Length)
            {
                Log.UsageExport(log);
                return 2;
            }
            return Export(sceneFactory(), args[exportIndex + 1], options, log);
        }

        int renderIndex = Array.IndexOf(args, "--render");
        if (renderIndex >= 0)
        {
            if (renderIndex + 1 >= args.Length)
            {
                Log.UsageRender(log);
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
            var log = EngrCadLoggers.Resolve(CurrentOptions);
            try
            {
                var scene = factory();
                if (!CurrentOptions.LazyTabMeshing)
                    scene.PreMesh(CurrentOptions.Quality); // heavy lifting stays on this worker thread
                // Lazy: SetScene re-shows the CURRENT tab, whose (new) parts mesh on the
                // loader's background task — the reload lands as fast as the tab in
                // front of the user, and the tabs behind it stay unmeshed. Occlusion is
                // not baked here either: the reloaded scene appears flat-lit at once and
                // darkens as the viewport's background bake catches up, which is what
                // keeps a hot-reload edit feeling instant.
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Host?.SetScene(scene));
                // The overlay wants one prose line; the log wants fields. Same facts,
                // formatted for their own audience.
                var now = DateTime.Now;
                int parts = scene.AllParts.Count();
                viewport.ShowStatus($"reloaded at {now:HH:mm:ss} — {parts} part(s)");
                Log.Reloaded(log, now, parts);
            }
            catch (Exception e)
            {
                string error = Describe(e);
                viewport.ShowStatus($"model error: {error} (keeping last good scene)");
                Log.ModelErrorOnReload(log, error);
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

    private static int RenderHeadless(Scene scene, string path, EngrCadOptions options, ILogger log)
    {
        if (!scene.AllParts.Any())
        {
            Log.NothingToRender(log);
            return 1;
        }
        if (!Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            Log.UnsupportedRenderFormat(log, Path.GetExtension(path));
            return 2;
        }
        if (!OffscreenRenderer.IsAvailable)
        {
            Log.OffscreenUnavailable(log, OffscreenRenderer.UnavailableReason);
            return 1;
        }
        scene.PreMesh(options.Quality); // meshes cache, so RenderToImage's PreMesh is a no-op
        RenderToImage(scene, path, options.RenderWidth, options.RenderHeight, camera: null,
            options.RenderStyle, options.SectionAxis, options.SectionOffset, options.AmbientOcclusion,
            options.SectionPlanes, options.SectionCombine);
        Log.WroteImage(log, path, scene.AllParts.Count());
        return 0;
    }

    /// <summary>Parses a <c>--render-style</c> value (the kebab-case CLI spellings).</summary>
    private static bool TryParseStyle(string value, out ViewStyle style)
    {
        switch (value.ToLowerInvariant())
        {
            case "points": style = ViewStyle.Points; return true;
            case "wireframe": style = ViewStyle.Wireframe; return true;
            case "shaded": style = ViewStyle.Shaded; return true;
            case "shaded-edges": style = ViewStyle.ShadedWithEdges; return true;
            default: style = default; return false;
        }
    }

    /// <summary>Parses an on/off switch value (<c>--ao</c>).</summary>
    private static bool TryParseSwitch(string value, out bool enabled)
    {
        switch (value.ToLowerInvariant())
        {
            case "on" or "true" or "1": enabled = true; return true;
            case "off" or "false" or "0": enabled = false; return true;
            default: enabled = false; return false;
        }
    }

    /// <summary>Parses a <c>--mesh</c> value: <c>lazy</c> (per tab, on demand) or
    /// <c>all</c> (the whole document up front).</summary>
    private static bool TryParseMeshMode(string value, out bool lazy)
    {
        switch (value.ToLowerInvariant())
        {
            case "lazy" or "tab" or "ondemand" or "on-demand": lazy = true; return true;
            case "all" or "eager" or "up-front": lazy = false; return true;
            default: lazy = false; return false;
        }
    }

    /// <summary>Parses a <c>--section</c> axis letter.</summary>
    private static bool TryParseAxis(string value, out SectionAxis axis)
    {
        switch (value.ToLowerInvariant())
        {
            case "x": axis = SectionAxis.X; return true;
            case "y": axis = SectionAxis.Y; return true;
            case "z": axis = SectionAxis.Z; return true;
            default: axis = default; return false;
        }
    }

    // ---- headless export ----

    private static int Export(Scene scene, string path, EngrCadOptions options, ILogger log)
    {
        if (!scene.AllParts.Any())
        {
            Log.NothingToExport(log);
            return 1;
        }

        var quality = scene.ResolveQuality(options.Quality);
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".obj":
                WriteMergedObj(scene, path, quality);
                Log.WroteObj(log, path, scene.AllInstances.Count());
                return 0;

            case ".stl":
                StlWriter.WriteFile(
                    [.. scene.AllInstances.Select(i => (i.Part.GetMesh(quality), i.World))], path);
                Log.WroteStl(log, path, scene.AllInstances.Count());
                return 0;

            case ".step" or ".stp":
                return ExportStep(scene, path, log);

            default:
                Log.UnsupportedExportFormat(log, Path.GetExtension(path));
                return 2;
        }
    }

    private static int ExportStep(Scene scene, string path, ILogger log)
    {
        var solids = new List<(string Name, BrepSolid Solid)>();
        foreach (var part in scene.AllParts)
        {
            // The part's shared cached solid (Part.TryGetSolid) — the same lowering the
            // display mesh and edge overlay used, not a fresh compile per export.
            if (part.TryGetSolid() is { } solid)
                solids.Add((part.Name, solid));
            else
                Log.SkippingNonBrepPart(log, part.Name);
        }
        if (solids.Count == 0)
        {
            Log.NoBrepParts(log);
            return 1;
        }

        if (solids.Count == 1)
        {
            StepWriter.WriteFile(solids[0].Solid, path, solids[0].Name);
            Log.WroteStep(log, path, solids[0].Name);
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
            Log.WroteStep(log, partPath, name);
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
