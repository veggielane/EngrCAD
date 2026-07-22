using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;

namespace EngrCAD.Viewer;

// Code-only Avalonia app (no XAML) until the UI grows enough to warrant it.
public sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Window
            {
                Title = "EngrCAD",
                Width = 1280,
                Height = 800,
                Content = new Panel
                {
                    Children =
                    {
                        new ViewportControl(),
                        new TextBlock
                        {
                            Text = "drag orbit · shift+drag pan · ctrl+drag or scroll zoom · click select",
                            Foreground = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
                            Margin = new Thickness(12, 8),
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Top,
                            IsHitTestVisible = false,
                        },
                    },
                },
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
