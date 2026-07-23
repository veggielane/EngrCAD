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

    private static Func<Scene>? _liveFactory;
    private static ViewportControl? _liveViewport;
    private static int _reloadScheduled;

    /// <summary>
    /// Opens the viewer showing <paramref name="scene"/> and blocks until it is closed.
    /// Avalonia allows one application lifetime per process, so call this at most once;
    /// hosts that need live updates use the <paramref name="onViewportReady"/> callback
    /// to capture the viewport and later call <see cref="ViewportControl.SetScene"/>.
    /// </summary>
    public static void Show(Scene scene, string title = "EngrCAD", Action<ViewportControl>? onViewportReady = null)
    {
        scene.PreMesh(); // tessellate here, not on the render thread
        InitialScene = scene;
        WindowTitle = title;
        OnViewportReady = onViewportReady;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
    }

    /// <summary>
    /// The live-modeling loop: shows the scene built by <paramref name="sceneFactory"/>
    /// and re-invokes it whenever <c>dotnet watch</c> hot-reloads the process, swapping
    /// the result in with the camera preserved. If the factory throws, the last good
    /// scene stays and the error appears in the overlay. Rude edits restart the process;
    /// the camera pose is persisted per title and restored, so the view survives those
    /// too. Blocks until the window closes.
    /// </summary>
    public static void ShowLive(Func<Scene> sceneFactory, string title = "EngrCAD")
    {
        _liveFactory = sceneFactory;

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

        Show(scene, title, viewport =>
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
    /// <c>--export path.step|path.obj</c> → headless export, no window (CI-friendly).
    /// Returns a process exit code.
    /// </summary>
    public static int Run(string[] args, Func<Scene> sceneFactory, string title = "EngrCAD")
    {
        int exportIndex = Array.IndexOf(args, "--export");
        if (exportIndex >= 0)
        {
            if (exportIndex + 1 >= args.Length)
            {
                Console.Error.WriteLine("--export requires a file path (.step or .obj)");
                return 2;
            }
            return Export(sceneFactory(), args[exportIndex + 1]);
        }

        if (args.Contains("--view"))
        {
            Show(sceneFactory(), title);
            return 0;
        }

        ShowLive(sceneFactory, title);
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
            try
            {
                var scene = factory();
                scene.PreMesh(); // heavy lifting stays on this worker thread
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Host?.SetScene(scene));
                viewport.ShowStatus($"reloaded at {DateTime.Now:HH:mm:ss} — {scene.AllParts.Count()} part(s)");
            }
            catch (Exception e)
            {
                viewport.ShowStatus($"model error: {Describe(e)} (keeping last good scene)");
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

    // ---- headless export ----

    private static int Export(Scene scene, string path)
    {
        if (!scene.AllParts.Any())
        {
            Console.Error.WriteLine("The scene has no parts to export.");
            return 1;
        }

        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".obj":
                WriteMergedObj(scene, path);
                Console.WriteLine($"wrote {path} ({scene.AllParts.Count()} part(s), merged)");
                return 0;

            case ".stl":
                StlWriter.WriteFile(
                    [.. scene.AllParts.Select(p => (p.GetMesh(scene.Options), p.Transform))], path);
                Console.WriteLine($"wrote {path} ({scene.AllParts.Count()} part(s), merged binary STL)");
                return 0;

            case ".step" or ".stp":
                return ExportStep(scene, path);

            default:
                Console.Error.WriteLine($"Unsupported export format '{Path.GetExtension(path)}' — use .step, .stl, or .obj.");
                return 2;
        }
    }

    private static int ExportStep(Scene scene, string path)
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
                    Console.Error.WriteLine($"skipping '{part.Name}': not B-Rep-representable (STEP needs exact solids)");
                    break;
            }
        }
        if (solids.Count == 0)
        {
            Console.Error.WriteLine("No B-Rep-representable parts; nothing exported.");
            return 1;
        }

        if (solids.Count == 1)
        {
            StepWriter.WriteFile(solids[0].Solid, path, solids[0].Name);
            Console.WriteLine($"wrote {path} ('{solids[0].Name}')");
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
            Console.WriteLine($"wrote {partPath} ('{name}')");
        }
        return 0;
    }

    /// <summary>All parts merged into one OBJ, with each part's transform applied.</summary>
    private static void WriteMergedObj(Scene scene, string path)
    {
        var culture = CultureInfo.InvariantCulture;
        using var writer = new StreamWriter(path);
        int offset = 1; // OBJ is 1-based
        foreach (var part in scene.AllParts)
        {
            writer.WriteLine($"o {part.Name.Replace(' ', '_')}");
            var (positions, faces) = part.GetMesh(scene.Options).ToIndexed();
            foreach (var position in positions)
            {
                var p = part.Transform.TransformPoint(position);
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
