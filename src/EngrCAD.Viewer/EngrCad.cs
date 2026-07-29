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
        RemoteControlServer? remote = null;
        Action<ViewportControl>? remoteReady = options.RemoteControl is { } rpc
            ? viewport => remote = StartRemoteControl(viewport, rpc, options)
            : null;
        OnViewportReady = hostReady is null && userReady is null && remoteReady is null
            ? null
            : viewport =>
            {
                hostReady?.Invoke(viewport); // internal plumbing (live loop) first
                remoteReady?.Invoke(viewport);
                userReady?.Invoke(viewport);
            };
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
        }
        finally
        {
            remote?.Dispose();
        }
    }

    /// <summary>Starts the opt-in remote-control endpoint over the live viewport:
    /// loopback-only JSON-RPC, every viewer mutation marshaled onto the UI thread by
    /// <see cref="ViewportRemoteViewer"/>. Failure to bind is reported, never fatal —
    /// a viewer without its remote is degraded, not broken.</summary>
    private static RemoteControlServer? StartRemoteControl(
        ViewportControl viewport, RemoteControlOptions rpc, EngrCadOptions options)
    {
        var log = EngrCadLoggers.Resolve(options);
        try
        {
            var handler = RemoteViewerDispatcher.For(new ViewportRemoteViewer(viewport), options.Title);
            var server = new RemoteControlServer(handler, rpc.Port, rpc.Token);
            int port = server.Start();
            Log.RemoteControlListening(log, port, rpc.Token is null ? "" : " (token required)");
            viewport.ShowStatus($"remote control: 127.0.0.1:{port}");
            return server;
        }
        catch (Exception e)
        {
            Log.RemoteControlFailed(log, $"{e.GetType().Name}: {e.Message}");
            return null;
        }
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
    /// <para><paramref name="explode"/> (0 assembled → 1 fully exploded) poses the
    /// assemblies' occurrences through <c>Scene.Instances(explode)</c>. It is the same
    /// flattening the window animates, so an exploded render and an exploded viewport
    /// agree by construction; the offsets come from <c>Assembly.AutoExplode</c> (called
    /// here when nothing has set them yet).</para>
    /// <para><paramref name="fields"/> (default true) draws parts that carry simulation
    /// results through their <c>Part.FieldDisplay</c> — colour map, legend and deformed
    /// shape. False renders every part in its own colour and undeformed, which is how a
    /// geometry figure is taken of a model that also carries results.</para>
    /// </summary>
    public static void RenderToImage(
        Scene scene, string path, int width = 1280, int height = 800, CameraState? camera = null,
        ViewStyle style = ViewStyle.ShadedWithEdges,
        SectionAxis sectionAxis = SectionAxis.Z, double? sectionOffset = null,
        bool ambientOcclusion = EngrCadOptions.AmbientOcclusionDefault,
        IReadOnlyList<SectionPlane>? sectionPlanes = null,
        SectionCombine sectionCombine = SectionCombine.Intersection,
        ConstructionPreviewRequest? preview = null,
        double explode = 0,
        bool fields = true)
    {
        scene.PreMesh(); // tessellate before touching GL
        // Exact-zero semantic test: only an explode ASKED FOR derives offsets, so a plain
        // render never touches the document.
        if (explode != 0)
            scene.AutoExplode();
        // Debug modifiers: Hidden parts (and non-isolated parts under an active
        // isolate) are excluded; Ghost parts render translucent via
        // EffectiveDisplayMode. With no flags set this is the identity.
        var instances = DebugFilter.Shown([.. scene.Instances(explode)]);
        // Building the preview lowers geometry, so it happens HERE, on the caller's
        // thread, before the GL context exists — the headless mirror of the window's
        // background-task rule.
        var (segments, world) = preview is { } request
            ? request.Build(instances, scene.ResolveQuality(CurrentOptions.Quality))
            : (null, Matrix4d.Identity);
        OffscreenRenderer.RenderToImage(instances, path, width, height, camera,
            furniture: true, style, sectionAxis, sectionOffset, ambientOcclusion,
            sectionPlanes, sectionCombine, segments, world, fields);
    }

    /// <summary>
    /// Renders ONE evaluated instant of <paramref name="animation"/> over
    /// <paramref name="scene"/> as a still — "the mechanism at t = 0.3". The frame comes
    /// from the same pure <c>Animation.At(t)</c> the window scrubs and every export
    /// samples, so a still, a scrubbed viewport and frame ⌊t·N⌋ of an APNG agree by
    /// construction.
    /// <para>The animation's own camera track wins; failing that
    /// <paramref name="camera"/>, failing that the auto-framing over the union of the
    /// first and last frames' bounds — the SAME framing the clip exports would use, so
    /// a still taken of a clip is croppped exactly as the clip is (framing per t would
    /// make a sequence of stills jump).</para>
    /// <para><paramref name="t"/> is the timeline fraction in [0, 1] (clamped, like
    /// <c>Animation.At</c>); use <c>animation.Duration</c> to convert from seconds.</para>
    /// </summary>
    public static void RenderToImage(
        Scene scene, Animation animation, double t, string path, int width = 1280, int height = 800,
        CameraState? camera = null,
        ViewStyle style = ViewStyle.ShadedWithEdges,
        SectionAxis sectionAxis = SectionAxis.Z, double? sectionOffset = null,
        bool ambientOcclusion = EngrCadOptions.AmbientOcclusionDefault,
        IReadOnlyList<SectionPlane>? sectionPlanes = null,
        SectionCombine sectionCombine = SectionCombine.Intersection,
        bool fields = true)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(animation);
        scene.PreMesh();
        var instances = PoseAt(scene, animation, t);
        var resolved = animation.At(t).Camera ?? camera;
        if (resolved is null)
        {
            // Framed over first ∪ last, exactly as AnimationExport does: an explode grows
            // past its assembled bounds, so framing t alone would crop a later frame and
            // make a sequence of stills jump.
            var bounds = Aabb.Empty;
            var defaults = scene.Instances().ToList();
            foreach (var instance in animation.At(0).Instances ?? defaults)
                bounds = bounds.Union(instance.Bounds());
            foreach (var instance in animation.At(1).Instances ?? defaults)
                bounds = bounds.Union(instance.Bounds());
            resolved = CameraMath.DefaultCamera(bounds);
        }
        OffscreenRenderer.RenderToImage(instances, path, width, height, resolved,
            furniture: true, style, sectionAxis, sectionOffset, ambientOcclusion,
            sectionPlanes, sectionCombine, preview: null, previewWorld: null, fields,
            // A deformation track reaches the pass as one scalar; with no such track the
            // factor is 1, so a still of a pose-only animation is unchanged.
            animation.At(t).DeformFactor);
    }

    /// <summary>
    /// The posed, debug-filtered instances of <paramref name="scene"/> at timeline
    /// <paramref name="t"/> — the one seam every "the model at t" consumer goes through
    /// (the still overload above, the MCP <c>screenshot</c> tool's <c>t</c> parameter),
    /// so none of them can disagree about what an instant looks like. An animation with
    /// no pose track returns the scene's own instances, which is the correct answer for
    /// a camera-only clip.
    /// <para>Evaluates no geometry beyond what the scene already has: posing is matrices
    /// (<see cref="Animation"/>'s load-bearing rule), so a caller keeps whatever
    /// meshing policy it had.</para>
    /// </summary>
    public static IReadOnlyList<PartInstance> PoseAt(Scene scene, Animation animation, double t)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(animation);
        return DebugFilter.Shown([.. animation.At(t).Instances ?? scene.Instances()]);
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
    /// <c>--export path.step|.ecb|.stl|.obj|.3mf|.amf|.off|.vtu|.glb|.gltf</c> → headless export, no
    /// window (CI-friendly; <c>.ecb</c> is the native lossless B-Rep archive, <c>.vtu</c>
    /// carries the parts' simulation results as point data for ParaView,
    /// <c>.glb</c>/<c>.gltf</c> the assembly hierarchy and colours for the web);
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

        int explodeIndex = Array.IndexOf(args, "--explode");
        if (explodeIndex >= 0)
        {
            if (explodeIndex + 1 >= args.Length
                || !double.TryParse(args[explodeIndex + 1], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double factor)
                || factor < 0)
            {
                Log.UsageExplode(log);
                return 2;
            }
            options.Explode = factor;
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

        // --rpc [port] (+ --rpc-token <token>): opt-in remote control for the windowed
        // modes. Loopback-only by construction; port 0/omitted = ephemeral (reported).
        int rpcIndex = Array.IndexOf(args, "--rpc");
        if (rpcIndex >= 0)
        {
            int rpcPort = 0;
            if (rpcIndex + 1 < args.Length
                && int.TryParse(args[rpcIndex + 1], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int parsedPort))
            {
                if (parsedPort is < 0 or > 65535)
                {
                    Log.UsageRpc(log);
                    return 2;
                }
                rpcPort = parsedPort;
            }
            string? rpcToken = null;
            int tokenIndex = Array.IndexOf(args, "--rpc-token");
            if (tokenIndex >= 0)
            {
                if (tokenIndex + 1 >= args.Length)
                {
                    Log.UsageRpc(log);
                    return 2;
                }
                rpcToken = args[tokenIndex + 1];
            }
            options.RemoteControl = new RemoteControlOptions { Port = rpcPort, Token = rpcToken };
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

    /// <summary>
    /// For hosts whose model SOURCE is data rather than compiled code — a .csx script
    /// runner watching its file, a parameter-file watcher: re-invokes the live scene
    /// factory exactly as a <c>dotnet watch</c> hot-reload patch does (same debounce,
    /// same keep-the-last-good-scene error path, same camera preservation). A no-op
    /// unless a <see cref="ShowLive"/> window is active, so callers may signal
    /// unconditionally.
    /// </summary>
    public static void NotifySourceChanged() => OnHotReload();

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
            options.SectionPlanes, options.SectionCombine, preview: null, options.Explode);
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
        // Debug modifiers: Hidden and Ghost parts never reach an export file, and an
        // active isolate exports only isolated parts. With no flags this is the
        // identity (same instances, same order).
        var instances = DebugFilter.Exported([.. scene.AllInstances]);
        if (instances.Count == 0)
        {
            Log.NothingToExport(log);
            return 1;
        }
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".obj":
                WriteMergedObj(instances, path, quality);
                Log.WroteObj(log, path, instances.Count);
                return 0;

            case ".stl":
                StlWriter.WriteFile(
                    [.. instances.Select(i => (i.Part.GetMesh(quality), i.World))], path);
                Log.WroteStl(log, path, instances.Count);
                return 0;

            case ".off":
                OffWriter.WriteFile(
                    [.. instances.Select(i => (i.Part.GetMesh(quality), i.World))], path);
                Log.WroteMeshFormat(log, path, instances.Count, "merged OFF");
                return 0;

            case ".3mf":
                ThreeMfWriter.WriteFile(ExportParts(instances, quality), path);
                Log.WroteMeshFormat(log, path, instances.Count, "3MF");
                return 0;

            case ".amf":
                AmfWriter.WriteFile(ExportParts(instances, quality), path);
                Log.WroteMeshFormat(log, path, instances.Count, "AMF");
                return 0;

            case ".glb" or ".gltf":
                return ExportGltf(scene, instances, path, quality, log);

            case ".vtu":
                // VTK unstructured grid for ParaView: the geometry PLUS every part's
                // simulation results. Arrays are the union of the parts' result names
                // and a part lacking one contributes NaN (VtuWriter's rule), so the
                // format name states how many arrays came out — a .vtu with none is a
                // valid geometry file and the difference should be visible in the log.
                VtuWriter.WriteFile(
                    [.. instances.Select(i => (i.Part.GetMesh(quality), i.World, i.Part.Results))],
                    path);
                int arrays = instances
                    .SelectMany(i => i.Part.Results.Select(f => f.Name))
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                Log.WroteMeshFormat(log, path, instances.Count, $"VTU, {arrays} result array(s)");
                return 0;

            case ".step" or ".stp":
                return ExportStep(scene, instances, path, log);

            case BrepArchive.Extension:
                return ExportBrepArchive(instances, path, log);

            default:
                Log.UnsupportedExportFormat(log, Path.GetExtension(path));
                return 2;
        }
    }

    /// <summary>
    /// Native <c>.ecb</c> archive of every B-Rep-representable part — the lossless
    /// round-trip STEP cannot give (helical, lofted and swept surfaces, offset curves,
    /// trimmed domains, <c>CurveSegment</c> mappings).
    /// <para>Parts are written UN-POSED, one root per part, exactly as the pre-assembly
    /// STEP export did: the archive is a geometry format, not a document format — a
    /// scene's structure belongs in the document envelope, not smuggled into a B-Rep
    /// file.</para>
    /// </summary>
    private static int ExportBrepArchive(
        IReadOnlyList<PartInstance> instances, string path, ILogger log)
    {
        var plan = StepAssembly.Plan(instances);
        foreach (var (part, _) in plan.Skipped)
            Log.SkippingNonBrepPart(log, part.Name);
        if (plan.Instances.Count == 0)
        {
            Log.NoBrepParts(log);
            return 1;
        }

        // Distinct solids, in first-use order: a part placed forty times is written once.
        var solids = new List<BrepSolid>();
        var seen = new HashSet<BrepSolid>(ReferenceEqualityComparer.Instance);
        foreach (var instance in plan.Instances)
        {
            if (seen.Add(instance.Solid))
                solids.Add(instance.Solid);
        }

        BrepArchive.WriteFile(solids, path, plan.Instances[0].PartName);
        Log.WroteMeshFormat(
            log, path, solids.Count,
            $"EngrCAD BREP archive v{BrepArchive.FormatVersion}");
        return 0;
    }

    /// <summary>
    /// glTF 2.0 export of a whole scene (<c>.glb</c> binary or self-contained
    /// <c>.gltf</c> JSON) — the web/AR/DCC route.
    /// <para>Unlike the mesh formats above this one keeps the <b>assembly hierarchy</b>:
    /// a glTF node per tab, per sub-assembly and per occurrence, with one mesh per
    /// distinct part however many times it is placed. The debug-filtered instance list is
    /// only used to decide WHICH parts export — the structure comes from the document, so
    /// what a browser shows has the same tree the model tree does.</para>
    /// </summary>
    private static int ExportGltf(
        Scene scene, IReadOnlyList<PartInstance> instances, string path,
        MeshQuality quality, ILogger log)
    {
        var allowed = instances.Select(i => i.Part).Distinct().ToList();
        var plan = GltfScene.Plan(scene, quality, explode: 0, allowed);
        foreach (var (part, reason) in plan.Skipped)
            Log.SkippingPart(log, part.Name, reason);
        if (plan.Geometries.Count == 0)
        {
            Log.NothingToExport(log);
            return 1;
        }

        GltfWriter.WriteFile(
            plan.Geometries, plan.Roots, path,
            GltfOptions.Default with
            {
                SceneName = scene.Tabs.Count == 1 ? scene.Tabs[0].Name : "EngrCAD scene",
            });
        Log.WroteMeshFormat(
            log, path, instances.Count,
            $"glTF 2.0, {plan.Geometries.Count} mesh(es)");
        return 0;
    }

    /// <summary>
    /// STEP export of a whole scene. Anything with more than one solid is written as ONE
    /// <b>assembly</b> file (product structure + NEXT_ASSEMBLY_USAGE_OCCURRENCE), built
    /// from the same <c>PartInstance</c> flattening the viewport renders — so what you
    /// export is posed exactly as what you see, and a part placed N times is one product
    /// referenced N times. A single-solid scene still writes the plain
    /// MANIFOLD_SOLID_BREP file it always did.
    /// </summary>
    private static int ExportStep(
        Scene scene, IReadOnlyList<PartInstance> instances, string path, ILogger log)
    {
        // The parts' shared cached solids (Part.TryGetSolid) — the same lowering the
        // display mesh and edge overlay used, not a fresh compile per export. The
        // instance list arrives already debug-filtered.
        var plan = StepAssembly.Plan(instances);
        foreach (var (part, _) in plan.Skipped)
            Log.SkippingNonBrepPart(log, part.Name);
        if (plan.Instances.Count == 0)
        {
            Log.NoBrepParts(log);
            return 1;
        }

        if (plan.Instances.Count == 1)
        {
            var only = plan.Instances[0];
            StepWriter.WriteFile(only.Solid, path, only.PartName);
            Log.WroteStep(log, path, only.PartName);
            return 0;
        }

        StepWriter.WriteAssemblyFile(
            plan.Instances, path, scene.Tabs.Count == 1 ? scene.Tabs[0].Name : "EngrCAD assembly");
        Log.WroteStepAssembly(log, path, plan.ProductCount, plan.Instances.Count);
        return 0;
    }

    /// <summary>The instances as named, posed, colored export parts — what the
    /// part-aware mesh formats (3MF, AMF) consume. Names are instance paths, so an
    /// assembly's occurrences stay distinguishable in a slicer's object list.</summary>
    private static List<MeshExportPart> ExportParts(
        IReadOnlyList<PartInstance> instances, MeshQuality quality) =>
        [.. instances.Select(i => new MeshExportPart(
            i.Part.GetMesh(quality), i.World, i.Path,
            i.Part.Color is { } c ? (c.R, c.G, c.B) : null))];

    /// <summary>All part instances merged into one OBJ (assemblies flattened), with
    /// each instance's composed world transform applied.</summary>
    private static void WriteMergedObj(
        IReadOnlyList<PartInstance> instances, string path, MeshQuality quality)
    {
        var culture = CultureInfo.InvariantCulture;
        using var writer = new StreamWriter(path);
        int offset = 1; // OBJ is 1-based
        foreach (var instance in instances)
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
