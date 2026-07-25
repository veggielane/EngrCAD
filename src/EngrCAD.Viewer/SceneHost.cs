using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Mesh;
using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

/// <summary>
/// The CAD chrome around the GL viewport: toolbar (standard views, fit), tab strip,
/// model tree (loose parts and assembly hierarchies per tab — indented occurrence
/// rows, visibility toggles at any level with parent-off hiding the subtree,
/// selection sync on occurrence paths), properties panel (occurrence path, part
/// info, world pose of the selection), and a status bar. One shared viewport; each
/// tab keeps its own camera (auto-framed on first visit, remembered after).
/// </summary>
internal sealed class SceneHost
{
    private static readonly IBrush PanelBrush = new SolidColorBrush(Color.FromRgb(0x24, 0x26, 0x2b));
    private static readonly IBrush ChromeBrush = new SolidColorBrush(Color.FromRgb(0x1d, 0x1f, 0x24));
    private static readonly IBrush DimText = new SolidColorBrush(Color.FromRgb(0x9a, 0xa0, 0xaa));
    private static readonly IBrush BrightText = new SolidColorBrush(Color.FromRgb(0xdd, 0xe1, 0xe6));

    // Construction-tree row colors: a modeling step, a sketch (matching the preview
    // overlay's construction cyan), a suppressed feature, and the previewed row.
    private static readonly IBrush StepText = new SolidColorBrush(Color.FromRgb(0xc3, 0xc9, 0xd2));
    private static readonly IBrush SketchText = new SolidColorBrush(Color.FromRgb(0x6f, 0xd0, 0xe6));
    private static readonly IBrush DisabledText = new SolidColorBrush(Color.FromRgb(0x6b, 0x70, 0x79));
    private static readonly IBrush PreviewText = new SolidColorBrush(Color.FromRgb(0x59, 0xea, 0xff));

    private readonly StackPanel _tabStrip;
    private readonly StackPanel _tree;
    private readonly StackPanel _properties;
    private readonly TextBlock _statusText;
    private readonly Dictionary<string, CameraState> _tabCameras = [];
    private Scene? _scene;
    private string? _currentTab;

    /// <summary>One model-tree row backed by a viewport instance (assembly header rows
    /// are not instances — they only contribute an ancestor checkbox).</summary>
    private sealed record PartRow(
        int Index, Part Part, CheckBox Own, IReadOnlyList<CheckBox> Ancestors, Button Label, Button ModeButton);

    private readonly List<PartRow> _partRows = [];
    private IReadOnlyList<PartInstance> _instances = [];

    // ---- construction tree (expandable build history under each part row) ----

    /// <summary>Expanded row keys ("P{index}" for a part row, "N{index}#{path}" for a
    /// construction node). Kept across rebuilds so expansion survives a tab switch or a
    /// live reload of the same design.</summary>
    private readonly HashSet<string> _expanded = [];

    /// <summary>Construction rows currently drawn (with each row's resting color), so
    /// the previewed one can be highlighted without another rebuild.</summary>
    private readonly List<(string Key, Button Label, IBrush Idle)> _constructionRows = [];

    /// <summary>Previews are lowered ONCE per graph node and memoized here (survives
    /// tab switches; a sub-shape shared by several rows is lowered once).</summary>
    private readonly ConstructionPreviewCache _previewCache = new();

    private string? _previewKey;
    private Tab? _currentTabContent;

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

        // Global view style: the classic CAD display-style dropdown. Order matches the
        // ViewStyle enum so the index maps directly; default is shaded with edges.
        var viewStyle = new ComboBox
        {
            ItemsSource = new[] { "Points", "Wireframe", "Shaded", "Shaded + Edges" },
            SelectedIndex = (int)ViewStyle.ShadedWithEdges,
            FontSize = 12,
            MinWidth = 118,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(viewStyle,
            "Global view style. Parts with an explicit display mode (wire/glass in the tree) keep it.");
        viewStyle.SelectionChanged += (_, _) =>
        {
            if (viewStyle.SelectedIndex >= 0)
                Viewport.ViewStyle = (ViewStyle)viewStyle.SelectedIndex;
        };
        toolbar.Children.Add(viewStyle);

        var section = new ToggleButton { Content = "Section", Padding = new Thickness(10, 4), FontSize = 12 };
        section.IsCheckedChanged += (_, _) => Viewport.SectionEnabled = section.IsChecked ?? false;
        toolbar.Children.Add(section);

        // Section axis cycler (X/Y/Z): which world axis the section plane cuts along.
        // Changing it re-centers the plane in the parts' bounds along the new axis.
        var sectionAxis = new Button { Content = "Z", Padding = new Thickness(8, 4), FontSize = 12 };
        ToolTip.SetTip(sectionAxis, "Section plane axis - click to cycle X / Y / Z ([ and ] move the plane)");
        sectionAxis.Click += (_, _) =>
        {
            var next = Viewport.SectionAxis switch
            {
                SectionAxis.X => SectionAxis.Y,
                SectionAxis.Y => SectionAxis.Z,
                _ => SectionAxis.X,
            };
            Viewport.SectionAxis = next;
            sectionAxis.Content = next.ToString();
        };
        toolbar.Children.Add(sectionAxis);
        toolbar.Children.Add(new Border { Width = 8 });

        // Annotations (PMI): on by default — a scene that carries dimensions shows
        // them; the toggle hides them for a clean geometry view.
        var annotations = new ToggleButton
        {
            Content = "Annot",
            Padding = new Thickness(10, 4),
            FontSize = 12,
            IsChecked = true,
        };
        ToolTip.SetTip(annotations, "Show/hide 3D annotations (dimensions, notes, datums)");
        annotations.IsCheckedChanged += (_, _) => Viewport.ShowAnnotations = annotations.IsChecked ?? true;
        toolbar.Children.Add(annotations);

        // Measure tool: while on, two clicks create a transient point-to-point
        // dimension (Escape clears; toggling off clears and exits).
        var measure = new ToggleButton { Content = "Measure", Padding = new Thickness(10, 4), FontSize = 12 };
        ToolTip.SetTip(measure, "Measure: click two surface points to dimension the distance (Esc clears)");
        measure.IsCheckedChanged += (_, _) => Viewport.MeasureMode = measure.IsChecked ?? false;
        toolbar.Children.Add(measure);
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
        // Wide enough for indented construction rows ("Sketch(4 curves, 1 hole)" at
        // depth 3) without truncation.
        var treePanel = SidePanel("MODEL", _tree, width: 225);

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

        ShowProperties(-1);
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
        // auto-framing; a first visit frames to the tab's bounds. The tree walks the
        // same tab structure Instances() flattens, so row instance indices line up.
        var instances = tab?.Instances() ?? [];
        Viewport.SetInstances(instances, frame: !keepCamera && !restored);
        if (restored)
            Viewport.Camera = _tabCameras[name!];

        for (int i = 0; i < _tabStrip.Children.Count; i++)
        {
            if (_tabStrip.Children[i] is Button button)
                button.FontWeight = _scene.Tabs[i].Name == name ? FontWeight.Bold : FontWeight.Normal;
        }

        // A new instance list clears the viewport's preview overlay (ApplyInstances);
        // drop our matching row state so the tree agrees with what is drawn.
        _previewKey = null;
        RebuildTree(tab, instances);
        ShowProperties(-1);
    }

    // ---- model tree ----

    private void RebuildTree(Tab? tab, IReadOnlyList<PartInstance> instances)
    {
        _tree.Children.Clear();
        _partRows.Clear();
        _constructionRows.Clear();
        _instances = instances;
        _currentTabContent = tab;
        if (tab is null)
            return;

        // Walk the tab exactly like Tab.Instances(): loose parts first, then each
        // assembly depth-first — so the running instance index matches the viewport.
        int next = 0;
        foreach (var part in tab.Parts)
            AddPartRow(part.Name, part, next++, depth: 0, ancestors: []);
        foreach (var assembly in tab.Assemblies)
            AddAssemblyRows(assembly, assembly.Name, depth: 0, ancestors: [], ref next);

        HighlightConstructionRow();
    }

    /// <summary>Rebuilds the tree in place (an expander toggled). The model tree is a
    /// plain StackPanel of rows — a few dozen controls — so a rebuild is cheaper than
    /// maintaining incremental insertions, and expansion state lives in
    /// <see cref="_expanded"/> rather than in the controls.</summary>
    private void RefreshTree() => RebuildTree(_currentTabContent, _instances);

    /// <summary>An assembly header row (checkbox hides the whole subtree) plus its
    /// occurrences, indented one level per nesting depth (always expanded in v1).</summary>
    private void AddAssemblyRows(
        Assembly assembly, string label, int depth, IReadOnlyList<CheckBox> ancestors, ref int next)
    {
        var check = new CheckBox
        {
            IsChecked = true,
            MinWidth = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        check.IsCheckedChanged += (_, _) => ApplyVisibility();
        ToolTip.SetTip(check, "Show/hide the whole assembly");

        var title = new TextBlock
        {
            Text = label,
            Foreground = DimText,
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Padding = new Thickness(4, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _tree.Children.Add(new DockPanel
        {
            Margin = new Thickness(depth * 14, 0, 0, 0),
            Children = { check, title },
        });

        var groupAncestors = new List<CheckBox>(ancestors) { check };
        foreach (var occurrence in assembly.Occurrences)
        {
            if (occurrence.Part is { } part)
                AddPartRow(occurrence.Name, part, next++, depth + 1, groupAncestors);
            else
                AddAssemblyRows(occurrence.SubAssembly!, occurrence.Name, depth + 1, groupAncestors, ref next);
        }
    }

    private void AddPartRow(string name, Part part, int index, int depth, IReadOnlyList<CheckBox> ancestors)
    {
        var check = new CheckBox
        {
            IsChecked = true,
            MinWidth = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        check.IsCheckedChanged += (_, _) => ApplyVisibility();

        // Display-mode cycler: a tiny per-row button, CAD-tree style. It writes
        // through Part.DisplayMode (shared by every instance of the part), so the
        // mode sticks across tab switches and sibling rows update together.
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
            var nextMode = NextDisplayMode(part.DisplayMode);
            Viewport.SetDisplayMode(index, nextMode);
            foreach (var row in _partRows)
            {
                if (ReferenceEquals(row.Part, part))
                    row.ModeButton.Content = ModeLabel(nextMode);
            }
            int selected = Viewport.Selected;
            if (selected >= 0 && selected < _instances.Count && ReferenceEquals(_instances[selected].Part, part))
                ShowProperties(selected); // keep the Display row current
        };
        DockPanel.SetDock(mode, Dock.Right);

        var label = new Button
        {
            Content = name,
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

        // Construction expander: parts that know how they were built (a Shape graph or
        // a FeatureHistory) get a disclosure triangle that reveals the build steps.
        var construction = part.ConstructionTree();
        string partKey = $"P{RowId(index)}";
        var expander = ExpanderButton(
            construction is not null, _expanded.Contains(partKey),
            "Show how this part was built",
            () =>
            {
                Toggle(partKey);
                RefreshTree();
            });

        _tree.Children.Add(new DockPanel
        {
            Margin = new Thickness(depth * 14, 0, 0, 0),
            Children = { expander, check, mode, label },
        });
        _partRows.Add(new PartRow(index, part, check, ancestors, label, mode));

        if (construction is not null && _expanded.Contains(partKey))
            AddConstructionRows(index, construction, depth + 1);
    }

    /// <summary>
    /// One construction-tree row and (when expanded) its children. A row maps to a
    /// graph node by reference plus its positional <c>Path</c>; clicking it previews
    /// that node — a sketch drawn on its plane, or the geometry of that sub-graph
    /// (a rollback view) — and clicking it again clears the preview.
    /// </summary>
    private void AddConstructionRows(int instanceIndex, ConstructionNode node, int depth)
    {
        string key = NodeKey(instanceIndex, node);
        bool expanded = _expanded.Contains(key);
        var expander = ExpanderButton(node.Children.Count > 0, expanded, null, () =>
        {
            Toggle(key);
            RefreshTree();
        });

        var panel = new DockPanel { Margin = new Thickness(depth * 14, 0, 0, 0) };
        panel.Children.Add(expander);

        if (node.Detail is { Length: > 0 } detail)
        {
            var value = new TextBlock
            {
                Text = detail,
                Foreground = DimText,
                FontSize = 10,
                Padding = new Thickness(4, 3),
                VerticalAlignment = VerticalAlignment.Center,
                [DockPanel.DockProperty] = Dock.Right,
            };
            panel.Children.Add(value);
        }

        var idle = NodeBrush(node);
        if (!node.CanPreview)
        {
            // Value rows (a [Param]) are text, not a disabled button — a greyed-out
            // button reads as "broken control" rather than "this is a value".
            panel.Children.Add(new TextBlock
            {
                Text = node.Label,
                Foreground = idle,
                FontSize = 11,
                Padding = new Thickness(4, 3),
                VerticalAlignment = VerticalAlignment.Center,
            });
            _tree.Children.Add(panel);
            AddChildRows(instanceIndex, node, depth, expanded);
            return;
        }

        var label = new Button
        {
            Content = node.Label,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 1),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Foreground = idle,
        };
        ToolTip.SetTip(label, $"{node.Kind}: click to preview this step in the viewport");
        label.Click += (_, _) => PreviewNode(instanceIndex, node, key);
        panel.Children.Add(label);

        _tree.Children.Add(panel);
        _constructionRows.Add((key, label, idle));
        AddChildRows(instanceIndex, node, depth, expanded);
    }

    private void AddChildRows(int instanceIndex, ConstructionNode node, int depth, bool expanded)
    {
        if (!expanded)
            return;
        foreach (var child in node.Children)
            AddConstructionRows(instanceIndex, child, depth + 1);
    }

    /// <summary>Expansion/preview keys are built from the occurrence PATH, not the
    /// instance index, so expanded rows survive a live reload that reorders parts.</summary>
    private string RowId(int instanceIndex) =>
        instanceIndex >= 0 && instanceIndex < _instances.Count
            ? _instances[instanceIndex].Path
            : instanceIndex.ToString();

    private string NodeKey(int instanceIndex, ConstructionNode node) =>
        $"N{RowId(instanceIndex)}#{node.Path}";

    private void Toggle(string key)
    {
        if (!_expanded.Add(key))
            _expanded.Remove(key);
    }

    /// <summary>Row colors that say what a row IS: sketches read as construction
    /// geometry (the preview color), suppressed features dim out, parameters are
    /// values, everything else is a modeling step.</summary>
    private static IBrush NodeBrush(ConstructionNode node) => node switch
    {
        { Suppressed: true } => DisabledText,
        { Kind: ConstructionNodeKind.Sketch } => SketchText,
        { Kind: ConstructionNodeKind.Parameter } => DimText,
        _ => StepText,
    };

    /// <summary>A disclosure triangle; an invisible placeholder of the same width when
    /// the row has nothing to expand, so labels stay aligned.</summary>
    private static Control ExpanderButton(bool enabled, bool expanded, string? tip, Action onClick)
    {
        if (!enabled)
            return new Border { Width = 14 };
        var button = new Button
        {
            Content = expanded ? "▾" : "▸",   // down / right pointing triangle
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Width = 14,
            FontSize = 9,
            Foreground = DimText,
            VerticalAlignment = VerticalAlignment.Center,
            [DockPanel.DockProperty] = Dock.Left,
        };
        if (tip is not null)
            ToolTip.SetTip(button, tip);
        button.Click += (_, _) => onClick();
        return button;
    }

    // ---- construction preview ----

    /// <summary>
    /// Previews one construction row in the viewport (clicking the showing row clears
    /// it). The heavy part — lowering the sub-shape — runs on a background task and is
    /// memoized per graph node, exactly the discipline <c>Scene.PreMesh</c> follows:
    /// nothing that tessellates ever touches the UI or render thread, and a second
    /// click on the same row is instant.
    /// </summary>
    private void PreviewNode(int instanceIndex, ConstructionNode node, string key)
    {
        if (!node.CanPreview)
            return;
        if (_previewKey == key)
        {
            ClearPreview();
            return;
        }

        _previewKey = key;
        HighlightConstructionRow();
        var world = instanceIndex >= 0 && instanceIndex < _instances.Count
            ? _instances[instanceIndex].World
            : Matrix4d.Identity;

        if (_previewCache.TryGet(node, out var cached))
        {
            ApplyPreview(node, cached, world);
            return;
        }

        _statusText.Text = $"preview: building '{node.Label}' ...";
        var quality = _scene?.ResolveQuality(EngrCad.CurrentOptions.Quality);
        Task.Run(() =>
        {
            var preview = _previewCache.Get(node, quality);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_previewKey == key)   // the user moved on while we lowered
                    ApplyPreview(node, preview, world);
            });
        });
    }

    private void ApplyPreview(ConstructionNode node, ConstructionPreview preview, in Matrix4d world)
    {
        if (preview.Error is { } error)
        {
            Viewport.SetConstructionPreview(null);
            _statusText.Text = error;
            return;
        }
        Viewport.SetConstructionPreview(preview.Segments, world);
        _statusText.Text = preview.IsEmpty
            ? $"preview: '{node.Label}' has no visible edges"
            : $"preview: {node.Label} ({preview.Segments.Count} edges)";
    }

    private void ClearPreview()
    {
        _previewKey = null;
        Viewport.SetConstructionPreview(null);
        HighlightConstructionRow();
        _statusText.Text = "preview: cleared";
    }

    private void HighlightConstructionRow()
    {
        foreach (var (key, label, idle) in _constructionRows)
        {
            bool active = key == _previewKey;
            label.FontWeight = active ? FontWeight.Bold : FontWeight.Normal;
            label.Foreground = active ? PreviewText : idle;
        }
    }

    /// <summary>Effective visibility per instance: its own checkbox AND every ancestor
    /// assembly checkbox — unchecking a parent hides the subtree without touching the
    /// children's own check state.</summary>
    private void ApplyVisibility()
    {
        foreach (var row in _partRows)
        {
            bool visible = row.Own.IsChecked ?? true;
            foreach (var ancestor in row.Ancestors)
                visible &= ancestor.IsChecked ?? true;
            Viewport.SetVisible(row.Index, visible);
        }
    }

    private static readonly DisplayMode[] DisplayModes = Enum.GetValues<DisplayMode>();

    /// <summary>The next mode in declaration order, wrapping — no hardcoded cardinality.</summary>
    private static DisplayMode NextDisplayMode(DisplayMode mode) =>
        DisplayModes[(Array.IndexOf(DisplayModes, mode) + 1) % DisplayModes.Length];

    private static string ModeLabel(DisplayMode mode) => mode switch
    {
        DisplayMode.Wireframe => "wire",
        DisplayMode.Translucent => "glass",
        _ => "shade",
    };

    private void OnViewportSelection(int index)
    {
        foreach (var row in _partRows)
            row.Label.FontWeight = row.Index == index ? FontWeight.Bold : FontWeight.Normal;
        ShowProperties(index);
    }

    // ---- properties ----

    private void ShowProperties(int index)
    {
        _properties.Children.Clear();
        if (index < 0 || index >= _instances.Count)
        {
            _properties.Children.Add(new TextBlock
            {
                Text = "nothing selected",
                Foreground = DimText,
                FontSize = 12,
            });
            return;
        }

        var instance = _instances[index];
        var part = instance.Part;
        var mesh = part.GetMesh();
        AddProperty("Name", instance.Path);
        if (instance.Path != part.Name)
            AddProperty("Part", part.Name);
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
        var size = instance.Bounds().Size;
        AddProperty("Size", $"{size.X:G4} × {size.Y:G4} × {size.Z:G4}");
        var position = instance.World.TransformPoint(Vector3d.Zero);
        AddProperty("Position", $"{position.X:G4}, {position.Y:G4}, {position.Z:G4}");
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
