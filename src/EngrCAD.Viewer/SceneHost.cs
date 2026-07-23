using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

/// <summary>
/// Hosts the viewport plus a tab strip over a <see cref="Scene"/>'s tabs. Each tab
/// shows its own parts with its own remembered camera (auto-framed on first visit).
/// Scene swaps (live reload) keep the current tab by name and the camera untouched.
/// </summary>
internal sealed class SceneHost
{
    private readonly StackPanel _strip;
    private readonly Dictionary<string, CameraState> _tabCameras = [];
    private Scene? _scene;
    private string? _currentTab;

    public ViewportControl Viewport { get; }
    public Control Root { get; }

    public SceneHost(ViewportControl viewport, Control overlay)
    {
        Viewport = viewport;
        _strip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(8, 6),
            IsVisible = false,
        };
        var dock = new DockPanel();
        DockPanel.SetDock(_strip, Dock.Top);
        dock.Children.Add(_strip);
        dock.Children.Add(new Panel { Children = { viewport, overlay } });
        Root = dock;
    }

    /// <summary>Shows a scene (UI thread; parts should be pre-meshed). Keeps the
    /// current tab when the new scene has one with the same name.</summary>
    public void SetScene(Scene scene)
    {
        _scene = scene;
        _strip.Children.Clear();
        _strip.IsVisible = scene.Tabs.Count > 1;
        foreach (var tab in scene.Tabs)
        {
            var button = new Button { Content = tab.Name, Padding = new Thickness(10, 4) };
            var name = tab.Name;
            button.Click += (_, _) => ShowTab(name);
            _strip.Children.Add(button);
        }

        var target = scene.Tabs.FirstOrDefault(t => t.Name == _currentTab) ?? scene.Tabs.FirstOrDefault();
        ShowTab(target?.Name, keepCamera: target?.Name == _currentTab);
    }

    private void ShowTab(string? name, bool keepCamera = false)
    {
        if (_scene is null)
            return;
        if (_currentTab is not null && !keepCamera)
            _tabCameras[_currentTab] = Viewport.Camera;

        _currentTab = name;
        var tab = _scene.Tabs.FirstOrDefault(t => t.Name == name);
        bool restored = false;
        if (!keepCamera && name is not null && _tabCameras.TryGetValue(name, out var camera))
            restored = true;

        // keepCamera (live reload of the same tab) and a remembered pose both suppress
        // auto-framing; a first visit frames to the tab's bounds.
        Viewport.SetParts(tab?.Parts ?? [], frame: !keepCamera && !restored);
        if (restored)
            Viewport.Camera = _tabCameras[name!];

        for (int i = 0; i < _strip.Children.Count; i++)
        {
            if (_strip.Children[i] is Button button)
                button.FontWeight = _scene.Tabs[i].Name == name ? FontWeight.Bold : FontWeight.Normal;
        }
    }
}
