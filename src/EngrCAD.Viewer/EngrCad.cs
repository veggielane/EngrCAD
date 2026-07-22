using Avalonia;
using EngrCAD.Interop;

namespace EngrCAD.Viewer;

/// <summary>
/// The library entry point for design code: build a <see cref="Scene"/>, call
/// <see cref="Show"/>. Blocks until the viewer window closes.
/// </summary>
public static class EngrCad
{
    internal static Scene? InitialScene;
    internal static string WindowTitle = "EngrCAD";
    internal static Action<ViewportControl>? OnViewportReady;

    /// <summary>
    /// Opens the viewer showing <paramref name="scene"/> and blocks until it is closed.
    /// Avalonia allows one application lifetime per process, so call this at most once;
    /// hosts that need live updates use the <paramref name="onViewportReady"/> callback
    /// to capture the viewport and later call <see cref="ViewportControl.SetScene"/>.
    /// </summary>
    public static void Show(Scene scene, string title = "EngrCAD", Action<ViewportControl>? onViewportReady = null)
    {
        InitialScene = scene;
        WindowTitle = title;
        OnViewportReady = onViewportReady;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
