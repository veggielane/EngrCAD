using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

// Code-only Avalonia app (no XAML) hosting the viewport; configured via EngrCad.Show.
public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var host = new SceneHost(EngrCad.WindowTitle);
            host.SetScene(EngrCad.InitialScene ?? new Scene());
            EngrCad.Host = host;

            desktop.MainWindow = new Window
            {
                Title = EngrCad.WindowTitle,
                Width = 1280,
                Height = 800,
                Content = host.Root,
            };
            EngrCad.OnViewportReady?.Invoke(host.Viewport);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
