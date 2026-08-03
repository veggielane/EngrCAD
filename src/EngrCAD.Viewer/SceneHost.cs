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

    /// <summary>A part that could not be meshed (its row stays, its geometry cannot).</summary>
    private static readonly IBrush FailedText = new SolidColorBrush(Color.FromRgb(0xe0, 0x7b, 0x6b));

    private readonly StackPanel _tabStrip;
    private readonly StackPanel _tree;
    private readonly StackPanel _properties;
    private readonly TextBlock _statusText;
    private readonly Dictionary<string, CameraState> _tabCameras = [];
    private Scene? _scene;
    private string? _currentTab;

    // ---- on-demand tab meshing (see TabMeshLoader) ----

    /// <summary>Meshes the tab being shown on a background task and hands the geometry
    /// back here as it lands. A tab already meshed (a revisit, or any tab when
    /// <see cref="EngrCadOptions.LazyTabMeshing"/> is off) publishes in one go with no
    /// background work, which is exactly the pre-lazy behavior.</summary>
    private readonly TabMeshLoader _loader;

    private readonly Border _loadingPanel;
    private readonly TextBlock _loadingText;
    private readonly TextBlock _loadingFlavor;
    private readonly ProgressBar _loadingBar;

    /// <summary>The tab's instances as the document lists them — kept because a part
    /// that fails to mesh drops out of what the viewport holds, and the tree then has to
    /// be rebuilt from the full list with that part marked.</summary>
    private IReadOnlyList<PartInstance> _tabInstances = [];

    /// <summary>Parts of the current tab that could not be meshed, with why. Their rows
    /// stay visible (and say so) but carry no viewport index.</summary>
    private Dictionary<Part, string> _failed = [];

    /// <summary>One model-tree row backed by a viewport instance (assembly header rows
    /// are not instances — they only contribute an ancestor checkbox).</summary>
    private sealed record PartRow(
        int Index, Part Part, CheckBox Own, IReadOnlyList<CheckBox> Ancestors, Button Label,
        Button ModeButton, Button ClipButton, TextBlock AoBadge);

    private readonly List<PartRow> _partRows = [];
    private IReadOnlyList<PartInstance> _instances = [];

    // ---- construction tree (expandable build history under each part row) ----

    /// <summary>Expanded row keys ("P{index}" for a part row, "N{index}#{path}" for a
    /// construction node). Kept across rebuilds so expansion survives a tab switch or a
    /// live reload of the same design.</summary>
    private readonly HashSet<string> _expanded = [];

    /// <summary>Rows the user unchecked. The tree rebuilds itself when an expander is
    /// toggled, so visibility must live here rather than in the checkbox controls —
    /// otherwise expanding a part would silently re-show everything it had hidden.
    /// Keyed by occurrence path, so it also survives tab switches and live reloads.</summary>
    private readonly HashSet<string> _hiddenRows = [];

    /// <summary>Assembly rows the user collapsed, keyed by assembly path — the inverse
    /// of <see cref="_expanded"/> because an assembly DEFAULTS to expanded (a fresh tree
    /// should show its contents). Collapsing is pure UI state: the rows under a
    /// collapsed assembly are still built and registered (their viewport visibility and
    /// instance indices must not shift), just not attached to the panel.</summary>
    private readonly HashSet<string> _collapsedAssemblies = [];

    /// <summary>Construction rows currently drawn (with each row's resting color), so
    /// the previewed one can be highlighted without another rebuild.</summary>
    private readonly List<(string Key, Button Label, IBrush Idle)> _constructionRows = [];

    /// <summary>Previews are lowered ONCE per graph node and memoized here (survives
    /// tab switches; a sub-shape shared by several rows is lowered once).</summary>
    private readonly ConstructionPreviewCache _previewCache = new();

    private string? _previewKey;
    private Tab? _currentTabContent;

    // ---- exploded view ----

    /// <summary>The toolbar's explode toggle and factor slider. Both are disabled for a
    /// tab with no assemblies: a loose part belongs to no assembly, so it has nothing to
    /// explode away from and a live control would be a lie.</summary>
    private readonly ToggleButton _explodeToggle;

    private readonly Slider _explodeSlider;

    /// <summary>Current explode factor (0 = assembled). Kept here rather than read back
    /// off the slider so a tab switch can re-apply it without a round trip.</summary>
    private double _explode;

    /// <summary>Whether this tab's occurrence offsets have been derived yet
    /// (<c>Assembly.AutoExplode</c> needs the parts' bounds, so it runs once, on a
    /// background task).</summary>
    private readonly HashSet<string> _explodePlanned = [];

    // ---- animation playback ----

    /// <summary>The toolbar transport (play toggle, loop toggle, time scrubber) —
    /// hidden entirely unless the host gave the window an animation
    /// (<see cref="EngrCadOptions.Animation"/>). Playback STATE lives in the UI-free
    /// <see cref="AnimationPlayback"/>; this class owns only the timer and widgets.</summary>
    private readonly ToggleButton _playToggle;

    private readonly ToggleButton _loopToggle;
    private readonly Slider _timeSlider;
    private AnimationPlayback? _playback;
    private Avalonia.Threading.DispatcherTimer? _playTimer;
    private readonly System.Diagnostics.Stopwatch _playClock = new();

    /// <summary>Guards the scrubber against feedback: playback moves the slider, and
    /// the slider's change handler must not then seek to where playback already is.</summary>
    private bool _updatingTimeSlider;

    public ViewportControl Viewport { get; }
    public Control Root { get; }

    public SceneHost(string title)
    {
        Viewport = new ViewportControl { BaseTitle = title };
        Viewport.SelectionChanged += OnViewportSelection;

        _loader = new TabMeshLoader(
            callback => Avalonia.Threading.Dispatcher.UIThread.Post(callback),
            PrepareForDisplay)
        {
            Ready = OnMeshBatch,
            Progress = OnMeshProgress,
            Completed = OnMeshCompleted,
        };

        // ---- toolbar ----
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(8, 6),
        };
        toolbar.Children.Add(ToolButton("Fit", () => Viewport.Frame()));
        toolbar.Children.Add(new Border { Width = 8 });
        _undoButton = ToolButton("Undo", Undo);
        _redoButton = ToolButton("Redo", Redo);
        toolbar.Children.Add(_undoButton);
        toolbar.Children.Add(_redoButton);
        RefreshUndoButtons();
        toolbar.Children.Add(new Border { Width = 8 });
        // Standard views: the SAME pose source the view cube uses
        // (ViewCubeMath.PoseFor), so the toolbar and the widget can never disagree —
        // a button is just a named cube direction. Top/Bottom keep the current yaw,
        // as the cube's TOP face click does (yaw is unconstrained at the poles).
        toolbar.Children.Add(ToolButton("Front", () => SetView(-Vector3d.UnitY)));
        toolbar.Children.Add(ToolButton("Top", () => SetView(Vector3d.UnitZ)));
        toolbar.Children.Add(ToolButton("Right", () => SetView(Vector3d.UnitX)));
        toolbar.Children.Add(ToolButton("Iso", () => SetView((1, -1, 1))));
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

        // Shading (how fills are LIT — deliberately not a ViewStyle member, which is
        // about what is drawn): the standard light or an analytic matcap. Order matches
        // the ShadingStyle enum so the index maps directly; the host options seed it.
        Viewport.Shading = EngrCad.CurrentOptions.Shading;
        var shading = new ComboBox
        {
            ItemsSource = new[] { "Lit", "Clay", "Metal" },
            SelectedIndex = (int)Viewport.Shading,
            FontSize = 12,
            MinWidth = 76,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(shading,
            "Shading: the standard light, or an analytic matcap (clay / polished metal).");
        shading.SelectionChanged += (_, _) =>
        {
            if (shading.SelectedIndex >= 0)
                Viewport.Shading = (ShadingStyle)shading.SelectedIndex;
        };
        toolbar.Children.Add(shading);

        // Ambient occlusion (baked per part): on unless the host options say otherwise.
        Viewport.AmbientOcclusion = EngrCad.CurrentOptions.AmbientOcclusion;
        var occlusion = new ToggleButton
        {
            Content = "AO",
            Padding = new Thickness(10, 4),
            FontSize = 12,
            IsChecked = Viewport.AmbientOcclusion,
        };
        ToolTip.SetTip(occlusion, "Ambient occlusion - darken pockets, bores and crevices (baked per part)");
        occlusion.IsCheckedChanged += (_, _) =>
        {
            Viewport.AmbientOcclusion = occlusion.IsChecked ?? true;
            RefreshOcclusionBadges();   // pending badges only make sense while AO is on
        };
        toolbar.Children.Add(occlusion);
        // Per-part bake progress: each part's row shows a small "ao" badge until its
        // background bake lands (one status line for the whole job is not progress).
        Viewport.OcclusionBaked += OnOcclusionBaked;

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

        // Section plane count: 1 = the classic single cut, 2 = quarter, 3 = octant.
        var sectionCount = new Button { Content = "1", Padding = new Thickness(8, 4), FontSize = 12 };
        ToolTip.SetTip(sectionCount,
            "Section planes - click to cycle 1 / 2 / 3 (single cut, quarter cut, octant)");
        sectionCount.Click += (_, _) =>
        {
            int next = Viewport.SectionPlaneCount % 3 + 1;
            Viewport.SectionPlaneCount = next;
            sectionCount.Content = next.ToString();
        };
        toolbar.Children.Add(sectionCount);

        // Oblique section plane from the current view: the minimal toolbar affordance
        // for planes the X/Y/Z axis model cannot express (hosts can already set
        // ViewportControl.SectionPlanes directly). The plane passes through the orbit
        // target with the camera's own eye direction as its normal, so it clips away
        // everything between the viewer and the view centre — the classic
        // "section from view". [ and ] still nudge it along its own normal.
        var cutAtView = new Button { Content = "Cut@View", Padding = new Thickness(8, 4), FontSize = 12 };
        ToolTip.SetTip(cutAtView,
            "Oblique section plane from the current view (through the view centre, facing the camera)");
        cutAtView.Click += (_, _) =>
        {
            var camera = Viewport.Camera;
            var normal = (CameraMath.Eye(camera.Yaw, camera.Pitch, camera.Distance, camera.Target)
                - camera.Target).Normalized();
            Viewport.SectionPlanes = [SectionPlane.Through(camera.Target, normal)];
            sectionCount.Content = "1";
            section.IsChecked = true;          // fires the handler that enables sectioning
            Viewport.SectionEnabled = true;    // idempotent when it was already on
            // The click can only run after the constructor finished, so the status
            // field is always assigned by then (hence the suppression).
            _statusText!.Text =
                $"section: oblique plane from view (normal {normal.X:F2}, {normal.Y:F2}, {normal.Z:F2})";
        };
        toolbar.Children.Add(cutAtView);
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

        // How the overlay treats material in front of it — the section-axis cycler's
        // idiom (a button whose label IS the state), beside the toggle it qualifies.
        // Separate from that toggle deliberately: "is this dimension shown" and "which
        // side of the part is it on" are different questions, and folding both into one
        // three-state button would make hiding annotations cost two clicks.
        Viewport.AnnotationDepth = EngrCad.CurrentOptions.AnnotationDepth;
        var annotationDepth = new Button
        {
            Content = Viewport.AnnotationDepth == AnnotationDepth.Occluded ? "Depth" : "Top",
            Padding = new Thickness(8, 4),
            FontSize = 12,
        };
        ToolTip.SetTip(annotationDepth,
            "3D-annotation depth - click to cycle: Top (the whole overlay over the model) / "
            + "Depth (lines behind material recede; the values stay legible)");
        annotationDepth.Click += (_, _) =>
        {
            bool occluded = Viewport.AnnotationDepth == AnnotationDepth.AlwaysOnTop;
            Viewport.AnnotationDepth = occluded ? AnnotationDepth.Occluded : AnnotationDepth.AlwaysOnTop;
            annotationDepth.Content = occluded ? "Depth" : "Top";
            _statusText!.Text = occluded
                ? "annotations: depth-tested (hidden stretches dimmed)"
                : "annotations: always on top";
        };
        toolbar.Children.Add(annotationDepth);

        // Simulation results: on by default — a scene whose parts state a FieldDisplay
        // shows it. Switching off returns every part to its own colour and undeformed
        // shape, which is how a geometry view is taken of a model that carries results.
        // Unlike the other view toggles this one re-uploads (colours are a vertex
        // attribute and a deformed shape is different geometry, not a different pose).
        var fields = new ToggleButton
        {
            Content = "Fields",
            Padding = new Thickness(10, 4),
            FontSize = 12,
            IsChecked = true,
        };
        ToolTip.SetTip(fields,
            "Show simulation results: colour map, legend and deformed shape (re-uploads geometry)");
        fields.IsCheckedChanged += (_, _) =>
        {
            Viewport.ShowFields = fields.IsChecked ?? true;
            _statusText!.Text = Viewport.ShowFields
                ? Viewport.ActiveFieldDisplay is { } display
                    ? $"fields: {display.Label} {display.Range}"
                    : "fields: no part in this tab shows a result"
                : "fields: off";
        };
        toolbar.Children.Add(fields);

        // Measure tool: while on, two clicks create a transient point-to-point
        // dimension (Escape clears; toggling off clears and exits).
        var measure = new ToggleButton { Content = "Measure", Padding = new Thickness(10, 4), FontSize = 12 };
        ToolTip.SetTip(measure, "Measure: click two surface points to dimension the distance (Esc clears)");
        measure.IsCheckedChanged += (_, _) => Viewport.MeasureMode = measure.IsChecked ?? false;
        toolbar.Children.Add(measure);
        toolbar.Children.Add(new Border { Width = 8 });

        // Exploded view: a scalar 0 -> 1 scaling each occurrence's ExplodeOffset. The
        // slider re-flattens and re-poses ONLY (SetInstancePoses), so dragging it never
        // re-uploads a buffer; the offsets themselves are derived once, off the UI thread.
        _explodeSlider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            Width = 96,
            IsEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(_explodeSlider, "Exploded view: drag to pull the assembly apart");
        // Dragging never moves the camera; only switching the toggle on re-frames
        // (ToggleExplode does that explicitly).
        _explodeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                ApplyExplode(_explodeSlider.Value);
        };
        _explodeToggle = new ToggleButton { Content = "Explode", Padding = new Thickness(10, 4), FontSize = 12 };
        ToolTip.SetTip(_explodeToggle, "Exploded view (assemblies only)");
        _explodeToggle.IsCheckedChanged += (_, _) => ToggleExplode(_explodeToggle.IsChecked ?? false);
        toolbar.Children.Add(_explodeToggle);
        toolbar.Children.Add(_explodeSlider);

        // Animation transport: play/pause, loop, and a scrubber, beside the explode
        // slider. All hidden unless the host supplied an animation. Playback drives the
        // same SetInstancePoses route the explode slider uses (matrices only), so a
        // running animation never re-uploads a buffer, and evaluation stays in the
        // UI-free layer (Animation.At / AnimationPlayback) so scrubbing here and a
        // headless export render the same frames.
        _playToggle = new ToggleButton
        {
            Content = "Play",
            Padding = new Thickness(10, 4),
            FontSize = 12,
            IsVisible = false,
        };
        ToolTip.SetTip(_playToggle, "Play/pause the scene's animation");
        _playToggle.IsCheckedChanged += (_, _) => TogglePlay(_playToggle.IsChecked ?? false);
        _loopToggle = new ToggleButton
        {
            Content = "Loop",
            Padding = new Thickness(10, 4),
            FontSize = 12,
            IsChecked = true,
            IsVisible = false,
        };
        ToolTip.SetTip(_loopToggle, "Loop playback at the end of the timeline");
        _loopToggle.IsCheckedChanged += (_, _) =>
        {
            if (_playback is { } playback)
                playback.Loop = _loopToggle.IsChecked ?? true;
        };
        _timeSlider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            Width = 96,
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(_timeSlider, "Animation timeline: drag to scrub");
        _timeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_updatingTimeSlider && _playback is { } playback)
            {
                playback.Seek(_timeSlider.Value);
                ApplyAnimationSample();
            }
        };
        toolbar.Children.Add(_playToggle);
        toolbar.Children.Add(_loopToggle);
        toolbar.Children.Add(_timeSlider);

        var bom = ToolButton("BOM", ShowBom);
        ToolTip.SetTip(bom, "Bill of materials for this tab (quantities per part; CSV saved beside it)");
        toolbar.Children.Add(bom);

        var check = ToolButton("Check", ShowSceneReport);
        ToolTip.SetTip(check, "Model validation report: per-part volume, bounds, watertightness");
        toolbar.Children.Add(check);

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

        // ---- meshing overlay (centered over the viewport, hidden when idle) ----
        // The primary line is the honest count; the secondary names the route the part
        // takes through the kernel (MeshFlavor). The bar is determinate: parts done out
        // of parts to do, refined within a part where the route reports fractions.
        _loadingText = new TextBlock { Foreground = BrightText, FontSize = 13 };
        _loadingFlavor = new TextBlock
        {
            Foreground = DimText,
            FontSize = 11,
            FontStyle = FontStyle.Italic,
            Margin = new Thickness(0, 3, 0, 9),
        };
        _loadingBar = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0, Height = 6, Width = 300 };
        _loadingPanel = new Border
        {
            Background = ChromeBrush,
            BorderBrush = PanelBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(18, 14),
            // Bottom-center, not center: parts appear as they are meshed, and a panel
            // over the middle of the viewport would cover the ones already loaded.
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 26),
            IsVisible = false,
            IsHitTestVisible = false,   // never steals orbit drags from the viewport
            Child = new StackPanel { Children = { _loadingText, _loadingFlavor, _loadingBar } },
        };

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
        // The viewport fills what is left, with the meshing overlay drawn above it.
        root.Children.Add(new Grid { Children = { Viewport, _loadingPanel } });
        Root = root;
        root.AttachedToVisualTree += AttachShortcuts;

        ShowProperties(-1);
    }

    /// <summary>Points the camera along a standard cube direction (distance and target
    /// kept), through <see cref="ViewCubeMath.PoseFor"/> — the one pose source shared
    /// with the view cube.</summary>
    private void SetView(in Vector3d direction)
    {
        var camera = Viewport.Camera;
        var (yaw, pitch) = ViewCubeMath.PoseFor(direction, camera.Yaw);
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
        ArmAnimation(scene);
    }

    // ---- animation playback ----

    /// <summary>
    /// Builds the host-supplied animation for a freshly set scene — per scene rather
    /// than once, because a live reload remakes the scene and tracks pose the
    /// occurrences they captured. The factory runs on a background task
    /// (track construction may mesh parts for bounds — the AutoExplode rule), and a
    /// stale result is dropped if another scene arrived meanwhile (the TabMeshLoader
    /// generation lesson, one token cheaper: the scene reference IS the token).
    /// </summary>
    private void ArmAnimation(Scene scene)
    {
        StopPlayTimer();
        _playback = null;
        _playToggle.IsChecked = false;
        ShowTransport(false);

        if (EngrCad.CurrentOptions.Animation is not { } factory)
            return;
        Task.Run(() =>
        {
            Animation? animation = null;
            string? error = null;
            try
            {
                animation = factory(scene);
            }
            catch (Exception exception)
            {
                error = $"{exception.GetType().Name}: {exception.Message}";
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_scene, scene))
                    return;   // a newer scene arrived while the factory ran
                if (error is not null)
                {
                    _statusText.Text = $"animation: {error}";
                    return;
                }
                if (animation is null)
                    return;
                _playback = new AnimationPlayback(animation) { Loop = _loopToggle.IsChecked ?? true };
                ShowTransport(true);
                _updatingTimeSlider = true;
                _timeSlider.Value = 0;
                _updatingTimeSlider = false;
            });
        });
    }

    private void ShowTransport(bool visible)
    {
        _playToggle.IsVisible = visible;
        _loopToggle.IsVisible = visible;
        _timeSlider.IsVisible = visible;
    }

    private void TogglePlay(bool on)
    {
        if (_playback is not { } playback)
            return;
        if (on)
        {
            playback.Play();
            _playToggle.Content = "Pause";
            StartPlayTimer();
        }
        else
        {
            playback.Pause();
            _playToggle.Content = "Play";
            StopPlayTimer();
        }
    }

    private void StartPlayTimer()
    {
        _playClock.Restart();
        _playTimer ??= new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _playTimer.Tick -= OnPlayTick;
        _playTimer.Tick += OnPlayTick;
        _playTimer.Start();
    }

    private void StopPlayTimer() => _playTimer?.Stop();

    /// <summary>
    /// Seeks the transport to timeline <paramref name="t"/> ∈ [0, 1] and <b>pauses</b>,
    /// returning the applied t — or null when this window has no animation. The
    /// remote-control endpoint's <c>set_animation_time</c>, which is what lets a bridged
    /// <c>viewer_screenshot</c> mean the same thing the headless <c>screenshot</c>'s
    /// <c>t</c> means.
    /// <para><b>Pausing is deliberate, not incidental.</b> The headless tool re-evaluates
    /// <c>Animation.At(t)</c> for one instant, so its answer is exact by construction; a
    /// running window has its own clock, and a capture arrives one frame later — so
    /// leaving the clock running would return a frame near t rather than at it. Callers
    /// are told (<c>"playing": false</c>) rather than left to assume.</para>
    /// <para>Goes through <see cref="_playToggle"/> rather than
    /// <c>AnimationPlayback.Pause</c> so the button, the timer and the playback state
    /// cannot disagree — the toggle's own handler is the one place that stops all three.
    /// Setting it to a value it already holds raises nothing, so a paused transport is
    /// untouched.</para>
    /// </summary>
    internal double? SeekAnimation(double t)
    {
        if (_playback is not { } playback)
            return null;
        _playToggle.IsChecked = false;
        playback.Seek(t);
        ApplyAnimationSample();
        return playback.T;
    }

    private void OnPlayTick(object? sender, EventArgs e)
    {
        if (_playback is not { } playback)
        {
            StopPlayTimer();
            return;
        }
        // Real elapsed time, not the timer interval: a slow frame advances the clock by
        // what actually passed, so playback speed is honest under load.
        double dt = _playClock.Elapsed.TotalSeconds;
        _playClock.Restart();
        if (playback.Advance(dt))
            ApplyAnimationSample();
        if (!playback.Playing)
        {
            // A non-looping run reached the end: reflect it in the toggle (whose
            // change handler pauses and stops the timer — idempotent here).
            _playToggle.IsChecked = false;
        }
    }

    /// <summary>Renders the playback position: slider follows, pose tracks re-pose the
    /// viewport (matrices only), camera tracks move the camera, deformation tracks set
    /// one float uniform. All three touch no buffer, which is what makes playback of a
    /// structural result cost the same as playback of an exploded view.</summary>
    private void ApplyAnimationSample()
    {
        if (_playback is not { } playback)
            return;
        var sample = playback.Animation.At(playback.T);
        _updatingTimeSlider = true;
        _timeSlider.Value = playback.T;
        _updatingTimeSlider = false;
        if (sample.Instances is { } posed)
            ApplyAnimatedPoses(posed);
        if (sample.Camera is { } camera)
            Viewport.Camera = camera;
        Viewport.DeformFactor = sample.DeformFactor;
    }

    /// <summary>
    /// Re-poses the current tab's instances from an animation sample, matched by
    /// occurrence PATH: a track built over the whole scene may carry instances of other
    /// tabs (ignored here), and instances this tab has that the sample lacks keep their
    /// document pose. The list handed to the viewport is always the tab's own instances
    /// in the tab's own order — the SetInstancePoses contract.
    /// </summary>
    private void ApplyAnimatedPoses(IReadOnlyList<PartInstance> sample)
    {
        if (_tabInstances.Count == 0)
            return;
        var worldByPath = new Dictionary<string, Matrix4d>(sample.Count);
        foreach (var instance in sample)
            worldByPath[instance.Path] = instance.World;
        var posed = new PartInstance[_tabInstances.Count];
        for (int i = 0; i < _tabInstances.Count; i++)
        {
            var instance = _tabInstances[i];
            posed[i] = worldByPath.TryGetValue(instance.Path, out var world)
                ? instance with { World = world }
                : instance;
        }
        Viewport.SetInstancePoses(posed);
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

        // The explode control follows the tab: only an assembly has anything to explode.
        // The factor itself is kept (a tab switch does not silently re-assemble), but a
        // tab whose offsets have not been derived simply flattens un-exploded until they
        // are — never a blocking mesh on the UI thread.
        bool explodable = tab?.HasAssemblies ?? false;
        _explodeToggle.IsEnabled = explodable;
        _explodeSlider.IsEnabled = explodable && (_explodeToggle.IsChecked ?? false);

        // keepCamera (live reload of the same tab) and a remembered pose both suppress
        // auto-framing; a first visit frames to the tab's bounds. The tree walks the
        // same tab structure Instances() flattens, so row instance indices line up.
        var instances = tab?.Instances(_explode) ?? [];

        // A new instance list clears the viewport's preview overlay (ApplyInstances).
        // The key is remembered so a live reload can RESTORE the preview by path: node
        // references change with the fresh scene, but occurrence paths and construction
        // paths are stable, so the same row can be found again (see RestorePreview).
        string? previewToRestore = _previewKey;
        _previewKey = null;
        _failed = [];
        _tabInstances = instances;
        // Rows FIRST — the whole tab's rows, including parts still to be meshed, so the
        // tree shows what is coming — then hand the geometry over as the loader
        // publishes it (OnMeshBatch), together with the visibility it implies: the swap
        // happens on the render thread, so per-row SetVisible calls made now would land
        // on the outgoing list and be wiped.
        RebuildTree(tab, instances, _failed);
        _loader.Start(new TabMeshRequest(
            name ?? "",
            instances,
            _scene.ResolveQuality(EngrCad.CurrentOptions.Quality),
            Frame: !keepCamera && !restored));
        if (restored)
            Viewport.Camera = _tabCameras[name!];
        if (previewToRestore is not null)
            RestorePreview(previewToRestore);

        for (int i = 0; i < _tabStrip.Children.Count; i++)
        {
            if (_tabStrip.Children[i] is Button button)
                button.FontWeight = _scene.Tabs[i].Name == name ? FontWeight.Bold : FontWeight.Normal;
        }

        ShowProperties(-1);
    }

    // ---- exploded view ----

    /// <summary>Turns the explode control on or off. Turning it on derives the offsets
    /// once, on a background task: <c>AutoExplode</c> reads the instances' bounds, which
    /// means meshing, and that must never happen on the UI thread — the same rule
    /// construction previews follow.</summary>
    private void ToggleExplode(bool on)
    {
        _explodeSlider.IsEnabled = on && (_currentTabContent?.HasAssemblies ?? false);
        if (!on)
        {
            _explodeSlider.Value = 0;
            ApplyExplode(0);
            return;
        }
        if (_currentTabContent is not { HasAssemblies: true } tab)
        {
            _statusText.Text = "explode: this tab has no assemblies";
            return;
        }
        if (_explodePlanned.Contains(tab.Name))
        {
            FullyExplode();
            return;
        }

        _statusText.Text = "explode: working out where everything goes ...";
        var quality = _scene?.ResolveQuality(EngrCad.CurrentOptions.Quality);
        Task.Run(() =>
        {
            tab.AutoExplode(quality: quality);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _explodePlanned.Add(tab.Name);
                if (_explodeToggle.IsChecked ?? false)
                    FullyExplode();
            });
        });

        // Switching explode ON re-frames ONCE: the parts move outside the assembled
        // framing, so keeping the old camera would show an empty middle. The slider
        // itself never re-frames — a camera chasing the geometry every tick is unusable.
        // Setting the slider queues a pose update; the explicit call right after
        // supersedes it with the framing variant, so the render thread applies one.
        void FullyExplode()
        {
            _explodeSlider.Value = 1;
            ApplyExplode(1, frame: true);
        }
    }

    /// <summary>Re-poses the viewport at a factor. Cheap by construction: the instance
    /// list is the same parts in the same order at every factor, so only the matrices
    /// change and every shared buffer stays put.</summary>
    private void ApplyExplode(double factor, bool frame = false)
    {
        _explode = factor;
        if (_currentTabContent is not { } tab)
            return;
        _tabInstances = tab.Instances(factor);
        Viewport.SetInstancePoses(_tabInstances, frame);
    }

    // ---- bill of materials ----

    /// <summary>Shows the current tab's BOM in a window and drops a CSV beside it, the
    /// same "write a file and report the path" convention the Capture button uses.</summary>
    private void ShowBom()
    {
        if (_currentTabContent is not { } tab)
        {
            _statusText.Text = "BOM: nothing to list";
            return;
        }

        var bom = Bom.For(tab);
        string csvPath = Path.Combine(
            Path.GetTempPath(),
            $"engrcad-bom-{string.Concat(tab.Name.Select(c => char.IsLetterOrDigit(c) ? c : '-'))}.csv");
        string? saved = null;
        try
        {
            File.WriteAllText(csvPath, bom.ToCsv());
            saved = csvPath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A read-only temp directory must not take the window down with it.
        }

        var text = new TextBlock
        {
            Text = bom.ToText(pathsShown: 4),
            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            FontSize = 12,
            Foreground = BrightText,
            TextWrapping = TextWrapping.NoWrap,
        };
        var body = new StackPanel { Spacing = 8, Children = { text } };
        if (saved is not null)
        {
            body.Children.Add(new TextBlock
            {
                Text = $"CSV: {saved}",
                Foreground = DimText,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        new Window
        {
            Title = $"Bill of materials — {tab.Name}",
            Width = 720,
            Height = 460,
            Background = PanelBrush,
            Content = new ScrollViewer
            {
                Padding = new Thickness(16),
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = body,
            },
        }.Show();

        _statusText.Text = saved is null
            ? $"BOM: {bom.LineCount} item(s), {bom.TotalQuantity} occurrence(s)"
            : $"BOM: {bom.LineCount} item(s), {bom.TotalQuantity} occurrence(s) — wrote {saved}";
    }

    // ---- model validation report ----

    /// <summary>Shows the scene's validation report (per-part volume/bounds/
    /// watertightness — the assert/echo analog) in a window, ShowBom's pattern.
    /// Meshing is cached, so a displayed scene reports instantly; a part that cannot
    /// mesh becomes a named note, not a crash (<see cref="SceneReport"/>).</summary>
    private void ShowSceneReport()
    {
        if (_scene is not { } scene)
        {
            _statusText.Text = "Check: no scene";
            return;
        }

        var report = SceneReport.Create(scene, scene.ResolveQuality(EngrCad.CurrentOptions.Quality));
        new Window
        {
            Title = "Model validation report",
            Width = 860,
            Height = 460,
            Background = PanelBrush,
            Content = new ScrollViewer
            {
                Padding = new Thickness(16),
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = report.ToText(),
                    FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                    FontSize = 12,
                    Foreground = BrightText,
                    TextWrapping = TextWrapping.NoWrap,
                },
            },
        }.Show();

        _statusText.Text = report.AllClean
            ? $"Check: {report.Parts.Count} part(s), all clean"
            : $"Check: {report.Parts.Count} part(s), {report.WarningCount} note(s)";
    }

    // ---- on-demand meshing callbacks (all on the UI thread) ----

    /// <summary>One part's worth of preparation, on the loader's worker thread: the
    /// display mesh, feature edges and annotations — everything that must not happen on
    /// the render thread. Ambient occlusion is deliberately NOT baked here: it streams
    /// separately once the geometry is published (`ViewportControl.StartOcclusionBake`),
    /// so a tab appears as soon as it is meshed and darkens a moment later rather than
    /// making the progress bar wait for the most expensive step.</summary>
    private static void PrepareForDisplay(Part part, MeshQuality? quality, ProgressCancel? progress) =>
        part.Prepare(quality, progress);

    /// <summary>Geometry the loader has ready for the tab in front of the user: hand it
    /// to the viewport, which uploads it on the next frame. Batches arrive in tab order
    /// and grow, so the parts already loaded stay put — and stay orbitable — while the
    /// rest is still being meshed.</summary>
    private void OnMeshBatch(TabMeshBatch batch)
    {
        if (!string.Equals(batch.TabName, _currentTab ?? "", StringComparison.Ordinal))
            return;   // belt and braces: the loader already drops superseded jobs
        if (batch.Failed.Count != _failed.Count)
        {
            // A part dropped out of what the viewport can show. Rebuild the rows from
            // the full tab with it marked, so row indices keep matching the instances
            // the viewport actually holds.
            _failed = batch.Failed.ToDictionary(part => part, part => _failed.GetValueOrDefault(part, ""));
            RebuildTree(_currentTabContent, _tabInstances, _failed);
        }
        Viewport.SetInstances(batch.Ready, batch.Frame, EffectiveVisibility());
        if (batch.Final)
            _loadingPanel.IsVisible = false;
    }

    /// <summary>Progress of the tab being meshed: an honest count in the primary line, a
    /// determinate bar, and the kernel route as a secondary line.</summary>
    private void OnMeshProgress(TabMeshProgress progress)
    {
        if (!string.Equals(progress.TabName, _currentTab ?? "", StringComparison.Ordinal))
            return;
        int at = Math.Min(progress.Completed + 1, Math.Max(progress.Total, 1));
        string message = $"meshing '{progress.TabName}' — {at} of {progress.Total} "
                       + $"part{(progress.Total == 1 ? "" : "s")}: '{progress.PartName}'";
        _loadingText.Text = message;
        _loadingFlavor.Text = progress.Flavor;
        _loadingBar.Value = progress.Fraction;
        _loadingPanel.IsVisible = true;
        _statusText.Text = message;
    }

    /// <summary>The tab finished (or finished with casualties). Failures become a status
    /// message and a log line — never a swallowed exception, and never a bar left
    /// spinning.</summary>
    private void OnMeshCompleted(TabMeshCompletion completion)
    {
        _loadingPanel.IsVisible = false;
        if (!string.Equals(completion.TabName, _currentTab ?? "", StringComparison.Ordinal))
            return;

        var log = EngrCadLoggers.Resolve(EngrCad.CurrentOptions);
        if (completion.Failures.Count > 0)
        {
            foreach (var failure in completion.Failures)
            {
                Log.PartFailedToMesh(log, failure.PartName, failure.Message);
                foreach (var instance in _tabInstances)
                {
                    if (instance.Part.Name == failure.PartName)
                        _failed[instance.Part] = failure.Message;
                }
            }
            // Redraw the rows so the failed ones can carry their reason as a tooltip.
            RebuildTree(_currentTabContent, _tabInstances, _failed);
            var first = completion.Failures[0];
            _statusText.Text = completion.Failures.Count == 1
                ? $"'{first.PartName}' failed to mesh: {first.Message}"
                : $"{completion.Failures.Count} parts failed to mesh (first: '{first.PartName}': {first.Message})";
            return;
        }

        if (completion.Cancelled)
        {
            _statusText.Text = $"meshing '{completion.TabName}' stopped";
            return;
        }
        _statusText.Text = $"meshed '{completion.TabName}' — {completion.PartCount} "
                         + $"part{(completion.PartCount == 1 ? "" : "s")} "
                         + $"in {completion.Elapsed.TotalSeconds:F1} s";
    }

    // ---- model tree ----

    private void RebuildTree(
        Tab? tab, IReadOnlyList<PartInstance> instances, IReadOnlyDictionary<Part, string> failed)
    {
        _tree.Children.Clear();
        _partRows.Clear();
        _constructionRows.Clear();
        // A part that failed to mesh is not in the viewport's list, so it must not
        // consume an index here either — everything after it would address the wrong
        // instance. Its row still appears, without an index (see AddPartRow).
        _instances = failed.Count == 0
            ? instances
            : [.. instances.Where(i => !failed.ContainsKey(i.Part))];
        _failedRows = failed;
        _currentTabContent = tab;
        if (tab is null)
            return;

        // Walk the tab exactly like Tab.Instances(): loose parts first, then each
        // assembly depth-first — so the running instance index matches the viewport.
        int next = 0;
        foreach (var part in tab.Parts)
            AddPartRow(part.Name, part, NextIndex(part, ref next), depth: 0, ancestors: []);
        foreach (var assembly in tab.Assemblies)
            AddAssemblyRows(assembly, assembly.Name, assembly.Name, depth: 0, ancestors: [], ref next);

        HighlightConstructionRow();
    }

    /// <summary>Instances the tree currently believes are un-meshable, so rows can say
    /// so (a snapshot of <see cref="_failed"/> at the last rebuild).</summary>
    private IReadOnlyDictionary<Part, string> _failedRows = new Dictionary<Part, string>();

    /// <summary>The viewport index for a part row: the running counter, except for a
    /// part that failed to mesh — it has no instance in the viewport, so it gets −1 and
    /// does not advance the counter.</summary>
    private int NextIndex(Part part, ref int next) => _failedRows.ContainsKey(part) ? -1 : next++;

    /// <summary>Rebuilds the tree in place (an expander toggled). The model tree is a
    /// plain StackPanel of rows — a few dozen controls — so a rebuild is cheaper than
    /// maintaining incremental insertions, and expansion state lives in
    /// <see cref="_expanded"/>/<see cref="_hiddenRows"/> rather than in the controls.
    /// The instance list is unchanged here, so the remembered visibility is pushed
    /// straight through (an expander toggle must not un-hide anything).</summary>
    private void RefreshTree()
    {
        RebuildTree(_currentTabContent, _tabInstances, _failed);
        ApplyVisibility();
    }

    /// <summary>An assembly header row (checkbox hides the whole subtree; disclosure
    /// triangle collapses it) plus its occurrences, indented one level per nesting
    /// depth. A collapsed subtree's rows are still BUILT — visibility state and the
    /// running viewport instance index both have to keep flowing through them — they
    /// are just not attached to the panel.</summary>
    private void AddAssemblyRows(
        Assembly assembly, string label, string path, int depth,
        IReadOnlyList<CheckBox> ancestors, ref int next, bool visible = true)
    {
        var check = VisibilityCheckBox($"A{path}");
        ToolTip.SetTip(check, "Show/hide the whole assembly");

        bool expanded = !_collapsedAssemblies.Contains(path);
        var expander = ExpanderButton(true, expanded, "Collapse/expand this assembly's rows", () =>
        {
            if (!_collapsedAssemblies.Add(path))
                _collapsedAssemblies.Remove(path);
            RefreshTree();
        });

        var title = new TextBlock
        {
            Text = label,
            Foreground = DimText,
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Padding = new Thickness(4, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (visible)
        {
            _tree.Children.Add(new DockPanel
            {
                Margin = new Thickness(depth * 14, 0, 0, 0),
                Children = { expander, check, title },
            });
        }

        var groupAncestors = new List<CheckBox>(ancestors) { check };
        bool childrenVisible = visible && expanded;
        foreach (var occurrence in assembly.Occurrences)
        {
            if (occurrence.Part is { } part)
                AddPartRow(occurrence.Name, part, NextIndex(part, ref next), depth + 1, groupAncestors,
                    childrenVisible);
            else
                AddAssemblyRows(occurrence.SubAssembly!, occurrence.Name, $"{path}/{occurrence.Name}",
                    depth + 1, groupAncestors, ref next, childrenVisible);
        }
    }

    /// <summary>A visibility checkbox whose state lives in <see cref="_hiddenRows"/>,
    /// so tree rebuilds (expander toggles, tab switches, live reloads) preserve it.</summary>
    private CheckBox VisibilityCheckBox(string key)
    {
        var check = new CheckBox
        {
            IsChecked = !_hiddenRows.Contains(key),
            MinWidth = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        check.IsCheckedChanged += (_, _) =>
        {
            if (check.IsChecked ?? true)
                _hiddenRows.Remove(key);
            else
                _hiddenRows.Add(key);
            ApplyVisibility();
        };
        return check;
    }

    private void AddPartRow(
        string name, Part part, int index, int depth, IReadOnlyList<CheckBox> ancestors, bool visible = true)
    {
        // Rows key on the occurrence path; a part with no instance (it failed to mesh)
        // has no path, so its own name keys it instead.
        string rowId = index >= 0 ? RowId(index) : $"!{name}";
        var check = VisibilityCheckBox($"V{rowId}");

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

        // Section-exemption toggle: writes through Part.ClippedBySection (shared by
        // every instance), the drafting convention that fasteners, shafts and pins
        // draw whole inside a cutaway. "cut" = clipped (default), "whole" = exempt.
        var clip = new Button
        {
            Content = ClipLabel(part.ClippedBySection),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2),
            FontSize = 10,
            Foreground = DimText,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(clip,
            "Section clipping - click to toggle: cut by section planes / drawn whole (fastener convention)");
        clip.Click += (_, _) =>
        {
            bool clipped = !part.ClippedBySection;
            Viewport.SetClippedBySection(index, clipped);
            foreach (var row in _partRows)
            {
                if (ReferenceEquals(row.Part, part))
                    row.ClipButton.Content = ClipLabel(clipped);
            }
        };
        DockPanel.SetDock(clip, Dock.Right);

        // Ambient-occlusion progress: visible until this part's background bake lands
        // (Viewport.OcclusionBaked clears it), hidden when AO is off or already baked.
        var aoBadge = new TextBlock
        {
            Text = "ao",
            Foreground = DimText,
            FontSize = 9,
            FontStyle = FontStyle.Italic,
            Padding = new Thickness(2, 3),
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = OcclusionPending(part),
        };
        ToolTip.SetTip(aoBadge, "ambient occlusion: baking in the background...");
        DockPanel.SetDock(aoBadge, Dock.Right);

        bool broken = _failedRows.TryGetValue(part, out string? failure);
        var label = new Button
        {
            Content = name,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Foreground = broken ? FailedText : BrightText,
        };
        if (broken)
            ToolTip.SetTip(label, $"failed to mesh: {failure}");
        label.Click += (_, _) =>
        {
            Viewport.Select(index == Viewport.Selected ? -1 : index);
            OnViewportSelection(Viewport.Selected);
        };

        // Construction expander: parts that know how they were built (a Shape graph or
        // a FeatureHistory) get a disclosure triangle that reveals the build steps.
        var construction = part.ConstructionTree();
        string partKey = $"P{rowId}";
        var expander = ExpanderButton(
            construction is not null, _expanded.Contains(partKey),
            "Show how this part was built",
            () =>
            {
                Toggle(partKey);
                RefreshTree();
            });

        if (visible)
        {
            _tree.Children.Add(new DockPanel
            {
                Margin = new Thickness(depth * 14, 0, 0, 0),
                Children = { expander, check, mode, clip, aoBadge, label },
            });
        }
        // Registered even when a collapsed ancestor hides the row: EffectiveVisibility
        // walks _partRows to push per-instance visibility to the viewport, and a
        // collapsed assembly must not change what renders.
        _partRows.Add(new PartRow(index, part, check, ancestors, label, mode, clip, aoBadge));

        if (visible && construction is not null && _expanded.Contains(partKey))
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

        // Feature rows are editable history steps: a suppress toggle and a rollback
        // marker, the feature-tree affordances every parametric CAD has. Both
        // regenerate through Part.Regenerate on a background task.
        if (node.Feature is { } feature && PartAt(instanceIndex) is { History: not null } featurePart)
        {
            var rollback = new Button
            {
                Content = "‖",   // double vertical bar: the rollback marker
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(3, 2),
                FontSize = 10,
                Foreground = DimText,
                VerticalAlignment = VerticalAlignment.Center,
                [DockPanel.DockProperty] = Dock.Right,
            };
            ToolTip.SetTip(rollback,
                "Roll back to here - suppress every feature below this one "
                + "(click the last feature's marker to restore)");
            rollback.Click += (_, _) => RollBackTo(featurePart, node);
            panel.Children.Add(rollback);

            var suppress = new Button
            {
                Content = node.Suppressed ? "uns" : "sup",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(3, 2),
                FontSize = 10,
                Foreground = DimText,
                VerticalAlignment = VerticalAlignment.Center,
                [DockPanel.DockProperty] = Dock.Right,
            };
            ToolTip.SetTip(suppress,
                "Suppress/unsuppress this feature (a suppressed feature passes the body "
                + "through untouched) and regenerate");
            suppress.Click += (_, _) =>
            {
                // A manual toggle overrides any rollback bookkeeping for this feature.
                if (_rolledBack.TryGetValue(featurePart, out var rolled))
                    rolled.Remove(feature);
                var edit = DocumentEdits.Suppress(featurePart, feature, !feature.Suppressed);
                RunEdit(edit.Description, () => _undo.Do(edit));
            };
            panel.Children.Add(suppress);
        }

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
        ToolTip.SetTip(label, node.Feature is null
            ? $"{node.Kind}: click to preview this step in the viewport"
            : $"{node.Kind}: click to preview this step and edit its parameters");
        label.Click += (_, _) =>
        {
            // A feature row also opens its [Param] values as editable fields in the
            // properties panel (the preview is the rollback view of the same step).
            if (node.Feature is not null && PartAt(instanceIndex) is { History: not null } featureOwner)
                ShowFeatureProperties(featureOwner, node);
            PreviewNode(instanceIndex, node, key);
        };
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
        // Previewing the whole part (the root row) is the part's own geometry, which
        // Scene.PreMesh already lowered — reuse that solid instead of compiling again.
        var part = instanceIndex >= 0 && instanceIndex < _instances.Count
            ? _instances[instanceIndex].Part
            : null;
        Task.Run(() =>
        {
            var known = part is not null && ReferenceEquals(node.Target, part.Geometry)
                ? part.TryGetSolid()
                : null;
            var preview = _previewCache.Get(node, quality, known);
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

    /// <summary>
    /// Re-applies a preview after the scene was replaced (a live reload) or a tab was
    /// revisited, by PATH: the key carries the occurrence path and the construction
    /// path ("N{occurrence}#{node path}"), both of which survive a reload even though
    /// every node reference changes. A key that no longer resolves — the row's part
    /// was removed, or it belonged to another tab — simply restores nothing, which is
    /// the pre-restore behaviour.
    /// </summary>
    private void RestorePreview(string key)
    {
        if (!key.StartsWith('N'))
            return;
        int separator = key.LastIndexOf('#');
        if (separator < 0)
            return;
        string occurrencePath = key[1..separator];
        string nodePath = key[(separator + 1)..];
        for (int i = 0; i < _instances.Count; i++)
        {
            if (_instances[i].Path != occurrencePath)
                continue;
            var root = _instances[i].Part.ConstructionTree();
            if (root?.Find(nodePath) is { CanPreview: true } node)
                PreviewNode(i, node, key);
            return;
        }
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

    // ---- feature editing (suppress, rollback, [Param] fields) ----

    /// <summary>The part behind a viewport instance index, or null (a failed row).</summary>
    private Part? PartAt(int instanceIndex) =>
        instanceIndex >= 0 && instanceIndex < _instances.Count ? _instances[instanceIndex].Part : null;

    /// <summary>Features suppressed by the rollback marker (per part), so restoring the
    /// bar un-suppresses exactly these and never a feature the user suppressed
    /// deliberately.</summary>
    private readonly Dictionary<Part, HashSet<Feature>> _rolledBack = [];

    /// <summary>
    /// The rollback bar as click semantics (the flag logic itself is
    /// <see cref="FeatureRollback.RollBackTo"/>, UI-free and tested): rolling back to a
    /// feature suppresses every feature BELOW it (recorded per part), moving the marker
    /// down restores the ones above it, and the last feature's marker restores the
    /// whole history. Only features the bar suppressed are ever restored.
    /// </summary>
    private void RollBackTo(Part part, ConstructionNode featureNode)
    {
        if (part.History is not { } history || featureNode.Feature is not { } marker)
            return;
        var rolled = _rolledBack.TryGetValue(part, out var existing) ? existing : [];
        _rolledBack[part] = rolled;
        if (!FeatureRollback.RollBackTo(history, marker, rolled))
        {
            _statusText.Text = "rollback: nothing to change";
            return;
        }
        RegenerateAndRefresh(part);
    }

    // ---- undo/redo ----

    /// <summary>
    /// This session's edit history. Model edits made through the tree and the properties
    /// panel go through it as <see cref="DocumentEdit"/>s, so Ctrl+Z takes them back.
    /// <para>Not shared with any other thread: <see cref="RunEdit"/> serializes every
    /// touch behind <see cref="_editInFlight"/> and reports back on the UI thread, which
    /// is what lets the stack keep its single-threaded contract while the regeneration
    /// it triggers runs off the UI thread.</para>
    /// </summary>
    private readonly UndoStack _undo = new();

    private Button _undoButton = null!;
    private Button _redoButton = null!;
    private bool _editInFlight;

    /// <summary>
    /// Runs one document edit off the UI thread and republishes the tab — the
    /// <see cref="RegenerateAndRefresh"/> pattern with the undo stack in front of it. A
    /// refused edit changed nothing (that is <see cref="DocumentEdit"/>'s contract), so
    /// the only thing to do about it is say so in the status bar.
    /// <para><paramref name="republish"/> is false for an edit that cannot move geometry
    /// — a material, say — where re-running the tab's mesh loader would be wasted work AND
    /// would clear the selection out from under the control that was just used. Undo and
    /// redo still republish, deliberately: the stack does not know what it is taking back,
    /// and a republish is always correct where skipping one is not.</para>
    /// </summary>
    private void RunEdit(string description, Action action, bool republish = true)
    {
        if (_editInFlight)
        {
            _statusText.Text = "an edit is still running";
            return;
        }
        _editInFlight = true;
        _statusText.Text = $"{description} ...";
        var scene = _scene;
        Task.Run(() =>
        {
            string message;
            try
            {
                action();
                message = description;
            }
            catch (Exception exception)
            {
                message = $"{description} refused: {exception.Message}";
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _editInFlight = false;
                RefreshUndoButtons();
                if (!ReferenceEquals(_scene, scene))
                    return;   // a live reload replaced the document while we edited
                if (republish)
                {
                    // Old tree nodes (and their preview cache entries) are stale now.
                    _previewCache.Clear();
                    Viewport.SetConstructionPreview(null);
                    _previewKey = null;
                    ShowTab(_currentTab, keepCamera: true);
                }
                else
                {
                    ShowProperties(Viewport.Selected);
                }
                _statusText.Text = message;
            });
        });
    }

    private void Undo()
    {
        if (_undo.UndoDescription is { } what)
            RunEdit($"Undo {what}", _undo.Undo);
    }

    private void Redo()
    {
        if (_undo.RedoDescription is { } what)
            RunEdit($"Redo {what}", _undo.Redo);
    }

    private void RefreshUndoButtons()
    {
        _undoButton.IsEnabled = _undo.CanUndo && !_editInFlight;
        _redoButton.IsEnabled = _undo.CanRedo && !_editInFlight;
        ToolTip.SetTip(_undoButton, _undo.UndoDescription is { } u ? $"Undo {u} (Ctrl+Z)" : "Nothing to undo");
        ToolTip.SetTip(_redoButton, _undo.RedoDescription is { } r ? $"Redo {r} (Ctrl+Y)" : "Nothing to redo");
    }

    /// <summary>
    /// Ctrl+Z / Ctrl+Y (and Ctrl+Shift+Z) at the window level. <b>Bubbling, and NOT
    /// handledEventsToo</b> — deliberately the opposite of the viewport's pointer
    /// handlers, whose lesson was that nothing upstream may starve them of events. A
    /// focused <c>TextBox</c> (the properties panel's parameter fields) has its own
    /// undo and marks the key handled; stealing it would make typing in a field
    /// unrecoverable, which is a worse failure than a missed shortcut.
    /// </summary>
    private void AttachShortcuts(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (TopLevel.GetTopLevel(Root) is not { } top)
            return;
        top.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, (_, args) =>
        {
            if (!args.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
                return;
            bool shift = args.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift);
            if (args.Key == Avalonia.Input.Key.Z && !shift)
                Undo();
            else if (args.Key == Avalonia.Input.Key.Y || (args.Key == Avalonia.Input.Key.Z && shift))
                Redo();
            else
                return;
            args.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    /// <summary>
    /// Regenerates an edited part on a background task (feature Apply bodies lower
    /// geometry — never on the UI thread) and republishes the tab: a successful
    /// regeneration swapped the body and cleared every derived cache, so the loader
    /// re-meshes exactly the changed part while untouched parts republish from cache.
    /// A failure keeps the previous geometry; either way the per-feature statuses land
    /// in the status bar.
    /// <para>Used by the edits that are not (yet) undoable — the rollback bar, which
    /// carries its own per-part suppression bookkeeping.</para>
    /// </summary>
    private void RegenerateAndRefresh(Part part)
    {
        _statusText.Text = $"regenerating '{part.Name}' ...";
        var scene = _scene;
        Task.Run(() =>
        {
            RegenerationResult result;
            try
            {
                result = part.Regenerate();
            }
            catch (Exception exception)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    _statusText.Text = $"regeneration threw: {exception.GetType().Name}: {exception.Message}");
                return;
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_scene, scene))
                    return;   // a live reload replaced the document while we regenerated
                if (result.Succeeded)
                {
                    // Old tree nodes (and their preview cache entries) are stale now.
                    _previewCache.Clear();
                    Viewport.SetConstructionPreview(null);
                    _previewKey = null;
                    ShowTab(_currentTab, keepCamera: true);
                    double elapsed = result.Statuses.Sum(s => s.Elapsed.TotalSeconds);
                    _statusText.Text = $"regenerated '{part.Name}' in {elapsed:F1} s";
                }
                else
                {
                    var failed = result.Statuses.FirstOrDefault(s => s.Outcome == FeatureOutcome.Failed);
                    _statusText.Text = failed is null
                        ? $"regeneration of '{part.Name}' failed"
                        : $"regeneration failed at '{failed.Name}': {failed.Error} "
                          + "(previous geometry kept; the edit stays applied)";
                }
            });
        });
    }

    /// <summary>
    /// The properties panel as a feature editor: one text field per <c>[Param]</c>
    /// (Enter applies), writing through the SAME JSON seam
    /// <c>FeatureHistory.SaveParameters</c>/<c>LoadParameters</c> use — so the accepted
    /// spellings cannot drift from the file format or the MCP <c>set_param</c> tool —
    /// then <see cref="RegenerateAndRefresh"/>.
    /// </summary>
    private void ShowFeatureProperties(Part part, ConstructionNode node)
    {
        if (node.Feature is not { } feature)
            return;
        _properties.Children.Clear();
        AddProperty("Feature", node.Label);
        AddProperty("Part", part.Name);
        AddProperty("Status", feature.Suppressed ? "suppressed" : "active");
        foreach (var parameter in feature.Parameters)
        {
            string caption = parameter.Units is { Length: > 0 } units
                ? $"{parameter.Name} ({units})"
                : parameter.Name;
            _properties.Children.Add(new TextBlock { Text = caption, Foreground = DimText, FontSize = 10 });
            _properties.Children.Add(ParameterEditor(part, feature, parameter));
        }
        _properties.Children.Add(new TextBlock
        {
            Text = "Enter applies a typed value; sliders, dropdowns and checkboxes apply on change.",
            Foreground = DimText,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });
    }

    /// <summary>
    /// The editor for one <c>[Param]</c>, chosen from what its METADATA already knows.
    /// The registry has carried the type and the <c>Min</c>/<c>Max</c> range since
    /// features landed; free text was the placeholder, not the design.
    /// <list type="bullet">
    /// <item><b>bool</b> → a checkbox.</item>
    /// <item><b>enum</b> → a dropdown of its members (no more typing a member name and
    /// discovering the spelling from a status message).</item>
    /// <item><b>a numeric with a FINITE range</b> → a slider beside a text box, both
    /// bound to the same value. The slider is the affordance a bounded parameter was
    /// always asking for; the box stays because a slider cannot express a precise
    /// number, and a designer who wants 12.7 should not have to chase it.</item>
    /// <item><b>anything else</b> → the text box, exactly as before.</item>
    /// </list>
    /// <para><b>Every editor writes through the SAME JSON seam</b>
    /// (<see cref="ApplyParameter"/> → <c>DocumentEdits.SetParameters</c>), so a slider,
    /// a dropdown, the parameter file and the MCP <c>set_param</c> tool cannot disagree
    /// about what a value means — and each edit is one undo step. A typed editor is a
    /// better way to SAY a value, never a second way to apply one.</para>
    /// <para>The slider commits on <b>release</b>, not on every pixel of the drag: an
    /// applied value regenerates the part, so a live drag would queue dozens of
    /// regenerations and each one is an undo step. The label tracks the drag so the
    /// number is still live under the cursor.</para>
    /// </summary>
    private Control ParameterEditor(Part part, Feature feature, ParamInfo parameter)
    {
        string name = parameter.Name;
        var type = Nullable.GetUnderlyingType(parameter.Type) ?? parameter.Type;
        // WHICH editor is ParamEditors.KindFor in Viewer.Core: a pure rule, so it is
        // asserted as a value and a browser properties panel cannot grow a second
        // opinion about what a bounded parameter looks like.
        var kind = ParamEditors.KindFor(parameter);

        if (kind == ParamEditorKind.Toggle)
        {
            var check = new CheckBox
            {
                IsChecked = parameter.Value as bool? ?? false,
                Content = null,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4),
            };
            Describe(check, parameter);
            check.IsCheckedChanged += (_, _) =>
                ApplyParameter(part, feature, name, check.IsChecked == true ? "true" : "false");
            return check;
        }

        if (kind == ParamEditorKind.Choice)
        {
            var combo = new ComboBox
            {
                ItemsSource = Enum.GetNames(type),
                SelectedItem = parameter.Value?.ToString(),
                FontSize = 12,
                Padding = new Thickness(4, 2),
                Margin = new Thickness(0, 0, 0, 4),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            };
            Describe(combo, parameter);
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is string member)
                    ApplyParameter(part, feature, name, member);
            };
            return combo;
        }

        var box = new TextBox
        {
            Text = EditableValue(parameter),
            FontSize = 12,
            Padding = new Thickness(4, 2),
            Margin = new Thickness(0, 0, 0, 4),
        };
        Describe(box, parameter);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
                ApplyParameter(part, feature, name, box.Text ?? "");
        };

        if (kind != ParamEditorKind.Slider)
            return box;

        bool whole = ParamEditors.IsWhole(parameter);
        double current = ParamEditors.Position(parameter);
        var slider = new Slider
        {
            Minimum = parameter.Min,
            Maximum = parameter.Max,
            Value = current,
            TickFrequency = whole ? 1 : 0,
            IsSnapToTickEnabled = whole,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        Describe(slider, parameter);
        // Track the drag in the BOX (cheap, no regeneration) and commit on release.
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
                box.Text = Format(slider.Value, whole);
        };
        slider.PointerCaptureLost += (_, _) => ApplyParameter(part, feature, name, box.Text ?? "");
        slider.KeyUp += (_, _) => ApplyParameter(part, feature, name, box.Text ?? "");

        var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        box.Margin = default;
        box.MinWidth = 64;
        Grid.SetColumn(slider, 0);
        Grid.SetColumn(box, 1);
        row.Children.Add(slider);
        row.Children.Add(box);
        return row;
    }

    private static void Describe(Control control, ParamInfo parameter)
    {
        string? tip = parameter.Description;
        if (double.IsFinite(parameter.Min) && double.IsFinite(parameter.Max))
        {
            string range = FormattableString.Invariant($"{parameter.Min:g6} to {parameter.Max:g6}");
            tip = tip is { Length: > 0 } ? $"{tip}  ({range})" : range;
        }
        if (tip is { Length: > 0 })
            ToolTip.SetTip(control, tip);
    }

    private static string Format(double value, bool whole) =>
        whole
            ? ((long)Math.Round(value)).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A parameter value as editable text that round-trips through the JSON
    /// seam: numbers/bools as JSON literals, vectors as [x, y] / [x, y, z] arrays,
    /// everything else (strings, enum names) as plain text.</summary>
    private static string EditableValue(ParamInfo parameter) => parameter.Value switch
    {
        null => "",
        double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        Vector2d v => FormattableString.Invariant($"[{v.X:R}, {v.Y:R}]"),
        Vector3d v => FormattableString.Invariant($"[{v.X:R}, {v.Y:R}, {v.Z:R}]"),
        bool b => b ? "true" : "false",
        var other => other.ToString() ?? "",
    };

    /// <summary>Applies one typed value: parse the text as JSON when it is JSON (numbers,
    /// booleans, vectors), else treat it as a string (enum names, labels), and push it
    /// through <see cref="DocumentEdits.SetParameters"/> — the SAME JSON seam
    /// <c>SaveParameters</c>/<c>LoadParameters</c> and the MCP <c>set_param</c> tool use,
    /// now with an undo step in front of it. A value that does not convert (or that
    /// breaks the rebuild) leaves the model exactly as it was and becomes a status
    /// message, not a crash.</summary>
    private void ApplyParameter(Part part, Feature feature, string parameterName, string text)
    {
        System.Text.Json.Nodes.JsonNode? value;
        try
        {
            value = System.Text.Json.Nodes.JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            value = System.Text.Json.Nodes.JsonValue.Create(text);
        }
        string json = new System.Text.Json.Nodes.JsonObject { [parameterName] = value }.ToJsonString();
        DocumentEdit edit;
        try
        {
            edit = DocumentEdits.SetParameters(part, feature, json);
        }
        catch (Exception exception)
        {
            _statusText.Text = $"{feature.Name}.{parameterName}: {exception.Message}";
            return;
        }
        RunEdit($"Set {feature.Name}.{parameterName}", () => _undo.Do(edit));
    }

    /// <summary>Effective visibility per instance: its own checkbox AND every ancestor
    /// assembly checkbox — unchecking a parent hides the subtree without touching the
    /// children's own check state — AND the part-level debug modifiers
    /// (<see cref="DebugFilter"/>: Hidden parts never show; when any part in the tab
    /// is Isolated, only isolated parts show).</summary>
    private bool[] EffectiveVisibility()
    {
        var visible = new bool[_instances.Count];
        Array.Fill(visible, true);
        bool anyIsolated = _currentTabContent is { } tab && DebugFilter.AnyIsolated(tab.AllParts);
        for (int i = 0; i < _instances.Count; i++)
            visible[i] = DebugFilter.IsShown(_instances[i].Part, anyIsolated);
        foreach (var row in _partRows)
        {
            if (row.Index < 0 || row.Index >= visible.Length)
                continue;
            bool shown = row.Own.IsChecked ?? true;
            foreach (var ancestor in row.Ancestors)
                shown &= ancestor.IsChecked ?? true;
            visible[row.Index] &= shown;
        }
        return visible;
    }

    /// <summary>Pushes <see cref="EffectiveVisibility"/> into the viewport (for changes
    /// against the instance list it is already showing).</summary>
    private void ApplyVisibility()
    {
        var visible = EffectiveVisibility();
        for (int i = 0; i < visible.Length; i++)
            Viewport.SetVisible(i, visible[i]);
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

    private static string ClipLabel(bool clippedBySection) => clippedBySection ? "cut" : "whole";

    /// <summary>Whether a part's ambient-occlusion bake is still to come: AO is on and
    /// the cache has nothing for its display mesh yet. A part not yet meshed is pending
    /// (its bake queues after its batch lands); a part that failed to mesh is not (there
    /// is nothing to bake). Never meshes: <c>HasMesh</c> gates the <c>GetMesh</c> read.</summary>
    private bool OcclusionPending(Part part)
    {
        if (!Viewport.AmbientOcclusion || _failedRows.ContainsKey(part))
            return false;
        return !part.HasMesh || Viewer.AmbientOcclusion.TryGet(part.GetMesh()) is null;
    }

    /// <summary>A part's background bake landed (UI thread): clear its rows' badges.</summary>
    private void OnOcclusionBaked(Part part)
    {
        foreach (var row in _partRows)
        {
            if (ReferenceEquals(row.Part, part))
                row.AoBadge.IsVisible = false;
        }
    }

    /// <summary>Recomputes every row's pending badge (the AO toggle flipped).</summary>
    private void RefreshOcclusionBadges()
    {
        foreach (var row in _partRows)
            row.AoBadge.IsVisible = OcclusionPending(row.Part);
    }

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
        // A document property, so it sits ABOVE the HasMesh gate below: saying what a part
        // is made of must not wait for it to tessellate. Its consequence - the Mass row -
        // is a measurement and stays below.
        AddMaterialEditor(part);

        // Simulation results: the min/max probe readout. Everything here is resolved
        // WITHOUT meshing (Part.TryResolveFieldDisplay does not), so it is legal above
        // the HasMesh gate below and appears even while the part is still tessellating.
        if (part.Results.Count > 0)
        {
            AddProperty("Results", string.Join(", ", part.Results.Select(f => f.Label)));
            if (part.TryResolveFieldDisplay(out var display, out string? fieldError))
            {
                AddProperty("Showing", display.Label);
                AddProperty("Range", $"{FieldLegend.Format(display.Range.Min)} .. "
                    + $"{FieldLegend.Format(display.Range.Max)}"
                    + (display.Field.Units.Length > 0 ? $" {display.Field.Units}" : ""));
                if (display.Deform is { } deform)
                    AddProperty("Deformed by", $"{deform.Name} x {display.DeformScale:G4}");
            }
            else if (fieldError is not null)
                AddProperty("Field", fieldError);
        }

        // Everything below reads the display mesh. Asking for it here would BLOCK the
        // UI thread on a part the loader is still meshing (and mesh it a second time
        // for one that was never queued), so an unprepared part reports its status
        // instead — the numbers appear when its batch lands.
        if (!part.HasMesh)
        {
            AddProperty("Status", _failedRows.TryGetValue(part, out string? failure)
                ? $"failed to mesh — {failure}"
                : "meshing...");
            return;
        }

        var mesh = part.GetMesh();
        AddProperty("Faces", mesh.FaceCount.ToString("N0"));
        AddProperty("Closed", mesh.IsClosed ? "yes" : "no");
        AddProperty("Volume", mesh.IsClosed ? mesh.Volume().ToString("G6") : "— (open)");
        AddProperty("Area", mesh.SurfaceArea().ToString("G6"));
        if (part.Material is not null)
        {
            // The display mesh's mass, so this row can never lower a B-Rep on the UI thread
            // and always agrees with the Volume row above it (PartMassProperties owns the rule).
            AddProperty("Mass", part.DisplayMassGrams() is { } grams ? $"{grams:G4} g" : "— (open)");
        }
        var size = instance.Bounds().Size;
        AddProperty("Size", $"{size.X:G4} × {size.Y:G4} × {size.Z:G4}");
        var position = instance.World.TransformPoint(Vector3d.Zero);
        AddProperty("Position", $"{position.X:G4}, {position.Y:G4}, {position.Z:G4}");
    }

    /// <summary>
    /// The material editor: a dropdown over <see cref="Materials.All"/> plus "(none)",
    /// following the typed-<c>[Param]</c>-editor precedent exactly — an enum-shaped
    /// choice gets a choice control, and it writes through the one seam
    /// (<see cref="DocumentEdits.SetMaterial"/>) that saving, MCP and the undo stack
    /// already share, so it is one undo step and never a second way to apply a value.
    ///
    /// <para><b>A material the catalogue does not carry is listed too</b> — one a design
    /// built, or a <see cref="FastenerMaterials"/> grade a catalogue component brought
    /// with it — because a dropdown that cannot show the current value would read as
    /// "nothing set" and one wrong click would silently discard it.</para>
    ///
    /// <para>Nothing about the geometry changes, so this does NOT republish the tab (see
    /// <see cref="RunEdit"/>'s <c>republish</c>): a material drives mass, the bill of
    /// materials and — at add time only — the default colour, and a part already on screen
    /// has its colour, so an existing part is never silently recoloured.</para>
    /// </summary>
    private void AddMaterialEditor(Part part)
    {
        // WHICH rows the dropdown offers is ParamEditors.MaterialChoices in Viewer.Core:
        // a pure rule, so it is asserted as a value and a browser panel cannot grow a
        // second opinion about what "(none)" and a custom material mean.
        var options = ParamEditors.MaterialChoices(part.Material);
        int selected = 0;
        for (int i = 0; i < options.Count; i++)
        {
            if (Equals(options[i], part.Material))
            {
                selected = i;
                break;
            }
        }

        var combo = new ComboBox
        {
            ItemsSource = options.Select(ParamEditors.MaterialLabel).ToList(),
            SelectedIndex = selected,
            FontSize = 12,
            Padding = new Thickness(4, 2),
            Margin = new Thickness(0, 0, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        ToolTip.SetTip(combo, part.Material is { } stated
            ? $"{stated.Name} - {stated.DensityKilogramsPerCubicMetre:G4} kg/m3"
            : "What this part is made of - drives its mass and the bill of materials");
        // Subscribe AFTER SelectedIndex, so rebuilding the panel does not fire an edit.
        combo.SelectionChanged += (_, _) =>
        {
            int index = combo.SelectedIndex;
            if (index < 0 || index >= options.Count || Equals(options[index], part.Material))
                return;
            var edit = DocumentEdits.SetMaterial(part, options[index]);
            RunEdit(edit.Description, () => _undo.Do(edit), republish: false);
        };

        _properties.Children.Add(new TextBlock { Text = "Material", Foreground = DimText, FontSize = 10 });
        _properties.Children.Add(combo);
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
