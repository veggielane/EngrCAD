using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using EngrCAD.BRep;
using EngrCAD.Implicit;
using EngrCAD.Mesh;
using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

/// <summary>
/// The CAD chrome around the GL viewport: toolbar (standard views, fit), tab strip,
/// model tree (parts per tab, visibility toggles, selection sync), properties panel
/// (type/volume/area/bounds of the selection), and a status bar. One shared viewport;
/// each tab keeps its own camera (auto-framed on first visit, remembered after).
/// </summary>
internal sealed class SceneHost
{
    private static readonly IBrush PanelBrush = new SolidColorBrush(Color.FromRgb(0x24, 0x26, 0x2b));
    private static readonly IBrush ChromeBrush = new SolidColorBrush(Color.FromRgb(0x1d, 0x1f, 0x24));
    private static readonly IBrush DimText = new SolidColorBrush(Color.FromRgb(0x9a, 0xa0, 0xaa));
    private static readonly IBrush BrightText = new SolidColorBrush(Color.FromRgb(0xdd, 0xe1, 0xe6));

    private readonly StackPanel _tabStrip;
    private readonly StackPanel _tree;
    private readonly StackPanel _properties;
    private readonly TextBlock _statusText;
    private readonly Dictionary<string, CameraState> _tabCameras = [];
    private Scene? _scene;
    private string? _currentTab;

    public ViewportControl Viewport { get; }
    public Control Root { get; }

    public SceneHost(string title)
    {
        Viewport = new ViewportControl { BaseTitle = title };
        Viewport.SelectionChanged += OnViewportSelection;

        // ---- toolbar ----
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(8, 6),
        };
        toolbar.Children.Add(ToolButton("Fit", () => Viewport.Frame()));
        toolbar.Children.Add(new Border { Width = 8 });
        toolbar.Children.Add(ToolButton("Front", () => SetView(-Math.PI / 2, 0)));
        toolbar.Children.Add(ToolButton("Top", () => SetView(-Math.PI / 2, Math.PI / 2)));
        toolbar.Children.Add(ToolButton("Right", () => SetView(0, 0)));
        toolbar.Children.Add(ToolButton("Iso", () => SetView(-Math.PI / 4, Math.Asin(1 / Math.Sqrt(3)))));
        toolbar.Children.Add(new Border { Width = 8 });
        var projection = new ToggleButton { Content = "Ortho", Padding = new Thickness(10, 4), FontSize = 12 };
        projection.IsCheckedChanged += (_, _) => Viewport.Orthographic = projection.IsChecked ?? false;
        toolbar.Children.Add(projection);
        var section = new ToggleButton { Content = "Section", Padding = new Thickness(10, 4), FontSize = 12 };
        section.IsCheckedChanged += (_, _) => Viewport.SectionEnabled = section.IsChecked ?? false;
        toolbar.Children.Add(section);
        toolbar.Children.Add(new Border { Width = 8 });
        var capture = ToolButton("Capture", () => Viewport.SaveScreenshot());
        ToolTip.SetTip(capture, "Save the current view as a PNG (path appears in the status bar)");
        toolbar.Children.Add(capture);

        _tabStrip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(8, 0, 8, 4),
            IsVisible = false,
        };

        // ---- model tree (left) ----
        _tree = new StackPanel { Spacing = 2 };
        var treePanel = SidePanel("PARTS", _tree, width: 190);

        // ---- properties (right) ----
        _properties = new StackPanel { Spacing = 3 };
        var propertiesPanel = SidePanel("PROPERTIES", _properties, width: 235);

        // ---- status bar (bottom) ----
        _statusText = new TextBlock { Foreground = DimText, FontSize = 11 };
        var statusBar = new Border
        {
            Background = ChromeBrush,
            Padding = new Thickness(10, 4),
            Child = new DockPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "drag orbit · shift+drag pan · ctrl+drag/scroll zoom · click select · [/] section plane",
                        Foreground = DimText,
                        FontSize = 11,
                        [DockPanel.DockProperty] = Dock.Right,
                    },
                    _statusText,
                },
            },
        };
        Viewport.Status = message => _statusText.Text = message;

        // ---- assemble ----
        var chrome = new Border
        {
            Background = ChromeBrush,
            Child = new StackPanel { Children = { toolbar, _tabStrip } },
        };
        var root = new DockPanel();
        DockPanel.SetDock(chrome, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        DockPanel.SetDock(treePanel, Dock.Left);
        DockPanel.SetDock(propertiesPanel, Dock.Right);
        root.Children.Add(chrome);
        root.Children.Add(statusBar);
        root.Children.Add(treePanel);
        root.Children.Add(propertiesPanel);
        root.Children.Add(Viewport);
        Root = root;

        ShowProperties(null);
    }

    private void SetView(double yaw, double pitch)
    {
        var camera = Viewport.Camera;
        Viewport.Camera = new CameraState(yaw, pitch, camera.Distance, camera.Target);
    }

    /// <summary>Shows a scene (UI thread; parts should be pre-meshed). Keeps the
    /// current tab when the new scene has one with the same name.</summary>
    public void SetScene(Scene scene)
    {
        _scene = scene;
        _tabStrip.Children.Clear();
        _tabStrip.IsVisible = scene.Tabs.Count > 1;
        foreach (var tab in scene.Tabs)
        {
            var name = tab.Name;
            _tabStrip.Children.Add(ToolButton(name, () => ShowTab(name)));
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
        bool restored = !keepCamera && name is not null && _tabCameras.ContainsKey(name);

        // keepCamera (live reload of the same tab) and a remembered pose both suppress
        // auto-framing; a first visit frames to the tab's bounds.
        Viewport.SetParts(tab?.Parts ?? [], frame: !keepCamera && !restored);
        if (restored)
            Viewport.Camera = _tabCameras[name!];

        for (int i = 0; i < _tabStrip.Children.Count; i++)
        {
            if (_tabStrip.Children[i] is Button button)
                button.FontWeight = _scene.Tabs[i].Name == name ? FontWeight.Bold : FontWeight.Normal;
        }

        RebuildTree(tab);
        ShowProperties(null);
    }

    // ---- model tree ----

    private void RebuildTree(Tab? tab)
    {
        _tree.Children.Clear();
        if (tab is null)
            return;
        for (int i = 0; i < tab.Parts.Count; i++)
        {
            int index = i;
            var part = tab.Parts[i];
            var check = new CheckBox
            {
                IsChecked = true,
                MinWidth = 0,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            check.IsCheckedChanged += (_, _) => Viewport.SetVisible(index, check.IsChecked ?? true);

            // Display-mode cycler: a tiny per-row button, CAD-tree style. It writes
            // through Part.DisplayMode, so the mode sticks across tab switches.
            var mode = new Button
            {
                Content = ModeLabel(part.DisplayMode),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2),
                FontSize = 10,
                Foreground = DimText,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(mode, "Display mode - click to cycle: shaded / wireframe / translucent");
            mode.Click += (_, _) =>
            {
                var next = (DisplayMode)(((int)part.DisplayMode + 1) % 3);
                Viewport.SetDisplayMode(index, next);
                mode.Content = ModeLabel(next);
                if (Viewport.Selected == index)
                    ShowProperties(part); // keep the Display row current
            };
            DockPanel.SetDock(mode, Dock.Right);

            var label = new Button
            {
                Content = part.Name,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Foreground = BrightText,
            };
            label.Click += (_, _) =>
            {
                Viewport.Select(index == Viewport.Selected ? -1 : index);
                OnViewportSelection(Viewport.Selected);
            };
            _tree.Children.Add(new DockPanel { Children = { check, mode, label } });
        }
    }

    private static string ModeLabel(DisplayMode mode) => mode switch
    {
        DisplayMode.Wireframe => "wire",
        DisplayMode.Translucent => "glass",
        _ => "shade",
    };

    private void OnViewportSelection(int index)
    {
        for (int i = 0; i < _tree.Children.Count; i++)
        {
            if (_tree.Children[i] is DockPanel row && row.Children[2] is Button label)
                label.FontWeight = i == index ? FontWeight.Bold : FontWeight.Normal;
        }
        var tab = _scene?.Tabs.FirstOrDefault(t => t.Name == _currentTab);
        ShowProperties(index >= 0 && tab is not null && index < tab.Parts.Count ? tab.Parts[index] : null);
    }

    // ---- properties ----

    private void ShowProperties(Part? part)
    {
        _properties.Children.Clear();
        if (part is null)
        {
            _properties.Children.Add(new TextBlock
            {
                Text = "nothing selected",
                Foreground = DimText,
                FontSize = 12,
            });
            return;
        }

        var mesh = part.GetMesh();
        AddProperty("Name", part.Name);
        AddProperty("Kind", part.Geometry switch
        {
            Shape => "Shape (unified)",
            BrepSolid => "B-Rep solid",
            HalfEdgeMesh => "mesh",
            Sdf => "implicit (SDF)",
            _ => part.Geometry.GetType().Name,
        });
        AddProperty("Display", part.DisplayMode.ToString().ToLowerInvariant());
        AddProperty("Faces", mesh.FaceCount.ToString("N0"));
        AddProperty("Closed", mesh.IsClosed ? "yes" : "no");
        AddProperty("Volume", mesh.IsClosed ? mesh.Volume().ToString("G6") : "— (open)");
        AddProperty("Area", mesh.SurfaceArea().ToString("G6"));
        var size = part.Bounds().Size;
        AddProperty("Size", $"{size.X:G4} × {size.Y:G4} × {size.Z:G4}");
    }

    private void AddProperty(string label, string value)
    {
        _properties.Children.Add(new TextBlock { Text = label, Foreground = DimText, FontSize = 10 });
        _properties.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = BrightText,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4),
            TextWrapping = TextWrapping.Wrap,
        });
    }

    // ---- widgets ----

    private static Button ToolButton(string text, Action onClick)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 4), FontSize = 12 };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static Border SidePanel(string header, Control content, double width) => new()
    {
        Background = PanelBrush,
        Width = width,
        Padding = new Thickness(10, 8),
        Child = new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Text = header,
                        Foreground = DimText,
                        FontSize = 10,
                        FontWeight = FontWeight.Bold,
                        LetterSpacing = 1.2,
                    },
                    content,
                },
            },
        },
    };
}
