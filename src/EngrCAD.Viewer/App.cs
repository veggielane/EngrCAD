using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

// Code-only Avalonia app (no XAML) hosting the viewport; configured via EngrCad.Show.
public sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            const string help =
                "drag orbit · shift+drag pan · ctrl+drag/scroll zoom · click select · keys: arrows orbit, +/- zoom, WASD pan";
            var status = new TextBlock
            {
                Text = help,
                Foreground = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
                Margin = new Thickness(12, 8),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
            };
            var viewport = new ViewportControl
            {
                Status = message => status.Text = $"{help}\nlast input: {message}",
                BaseTitle = EngrCad.WindowTitle,
            };
            var host = new SceneHost(viewport, status);
            host.SetScene(EngrCad.InitialScene ?? new Scene());
            EngrCad.Host = host;

            desktop.MainWindow = new Window
            {
                Title = EngrCad.WindowTitle,
                Width = 1280,
                Height = 800,
                Content = host.Root,
            };
            EngrCad.OnViewportReady?.Invoke(viewport);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
