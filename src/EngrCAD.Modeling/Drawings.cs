using System.Globalization;
using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Interop;

namespace EngrCAD.Modeling;

// The 2D drawing sheet: paper, views, and a title block. Everything here works in
// SHEET COORDINATES — millimetres, origin at the bottom-left corner of the paper, y up
// — which is the same convention SvgDrawing already writes in and the one a machinist's
// ruler uses. A view owns its own projection (HiddenLineRemoval) and knows where on the
// paper it sits; the sheet owns the layout, the border and the title block.

/// <summary>Layer names the sheet writes to, so a DXF or SVG consumer can toggle whole
/// classes of line work. Fixed strings rather than an enum because they cross into
/// files, where a renamed enum member would silently change a layer name.</summary>
public static class SheetLayers
{
    public const string Visible = "visible";
    public const string Hidden = "hidden";
    public const string Section = "section";
    public const string Hatch = "hatch";
    public const string Dimensions = "dimensions";
    public const string Border = "border";
    public const string TitleBlock = "titleblock";
}

/// <summary>
/// A paper size in millimetres, landscape by default (width &gt; height) — the ISO 216 A
/// series plus whatever a caller names. A record so a format is a value: two A3s are
/// the same A3.
/// </summary>
/// <param name="Name">Displayed in the title block ("A3").</param>
/// <param name="Width">Sheet width, mm.</param>
/// <param name="Height">Sheet height, mm.</param>
public sealed record SheetFormat(string Name, double Width, double Height)
{
    // The one paper-size table every frame reads. ISO 216 A series, ISO 216 B series and the
    // ANSI/ASME Y14.1 A-E sizes, all stated LANDSCAPE (width > height); .Portrait turns one
    // over. The B and ANSI sizes are additive — nothing uses them by default — so they cannot
    // move any existing output.

    public static SheetFormat A4 { get; } = new("A4", 297, 210);
    public static SheetFormat A3 { get; } = new("A3", 420, 297);
    public static SheetFormat A2 { get; } = new("A2", 594, 420);
    public static SheetFormat A1 { get; } = new("A1", 841, 594);
    public static SheetFormat A0 { get; } = new("A0", 1189, 841);

    /// <summary>ISO 216 B series (rounded to millimetres, landscape).</summary>
    public static SheetFormat B0 { get; } = new("B0", 1414, 1000);
    /// <inheritdoc cref="B0"/>
    public static SheetFormat B1 { get; } = new("B1", 1000, 707);
    /// <inheritdoc cref="B0"/>
    public static SheetFormat B2 { get; } = new("B2", 707, 500);
    /// <inheritdoc cref="B0"/>
    public static SheetFormat B3 { get; } = new("B3", 500, 353);
    /// <inheritdoc cref="B0"/>
    public static SheetFormat B4 { get; } = new("B4", 353, 250);
    /// <inheritdoc cref="B0"/>
    public static SheetFormat B5 { get; } = new("B5", 250, 176);

    // ANSI/ASME Y14.1 sizes are defined in inches; the millimetre figures are exact
    // (25.4 mm/in): A = 11 x 8.5, B = 17 x 11, C = 22 x 17, D = 34 x 22, E = 44 x 34.
    /// <summary>ANSI/ASME Y14.1 A (11 x 8.5 in), landscape.</summary>
    public static SheetFormat AnsiA { get; } = new("ANSI A", 279.4, 215.9);
    /// <summary>ANSI/ASME Y14.1 B (17 x 11 in), landscape.</summary>
    public static SheetFormat AnsiB { get; } = new("ANSI B", 431.8, 279.4);
    /// <summary>ANSI/ASME Y14.1 C (22 x 17 in), landscape.</summary>
    public static SheetFormat AnsiC { get; } = new("ANSI C", 558.8, 431.8);
    /// <summary>ANSI/ASME Y14.1 D (34 x 22 in), landscape.</summary>
    public static SheetFormat AnsiD { get; } = new("ANSI D", 863.6, 558.8);
    /// <summary>ANSI/ASME Y14.1 E (44 x 34 in), landscape.</summary>
    public static SheetFormat AnsiE { get; } = new("ANSI E", 1117.6, 863.6);

    /// <summary>Every named size, A series then B series then ANSI — the table a picker reads.</summary>
    public static IReadOnlyList<SheetFormat> All { get; } =
    [
        A4, A3, A2, A1, A0,
        B0, B1, B2, B3, B4, B5,
        AnsiA, AnsiB, AnsiC, AnsiD, AnsiE,
    ];

    /// <summary>The same sheet turned on its side.</summary>
    public SheetFormat Portrait => Width >= Height ? new(Name, Height, Width) : this;

    /// <summary>The same sheet the wide way round.</summary>
    public SheetFormat Landscape => Width >= Height ? this : new(Name, Height, Width);

    /// <summary>An arbitrary sheet.</summary>
    public static SheetFormat Custom(string name, double width, double height) =>
        width > 0 && height > 0
            ? new SheetFormat(name, width, height)
            : throw new ArgumentOutOfRangeException(nameof(width), "A sheet needs positive dimensions.");
}

/// <summary>
/// Which side of the front view its neighbours sit on. Third angle (ISO symbol: the
/// truncated cone seen small-end-first) puts the top view ABOVE and the right view to
/// the RIGHT — each view sits on the side of the object it looks at. First angle puts
/// them on the opposite sides. Naming it on the sheet is not optional in either
/// standard, which is why the title block always prints it.
/// </summary>
public enum ProjectionAngle
{
    /// <summary>ISO/European: top view below, right view to the left.</summary>
    First,

    /// <summary>ASME/North American: top view above, right view to the right.</summary>
    Third,
}

/// <summary>
/// The standard drawing scales (ISO 5455): 1:1, the reduction series and the
/// enlargement series. A drawing whose scale is 1:3.7 is not a drawing anyone will
/// measure off, so <see cref="Fit"/> always lands on a listed ratio.
/// </summary>
public static class DrawingScales
{
    /// <summary>Every listed ratio (sheet mm per model mm), ascending.</summary>
    public static IReadOnlyList<double> Standard { get; } =
    [
        1 / 1000.0, 1 / 500.0, 1 / 200.0, 1 / 100.0, 1 / 50.0, 1 / 20.0,
        1 / 10.0, 1 / 5.0, 1 / 2.0, 1, 2, 5, 10, 20, 50,
    ];

    /// <summary>The largest standard ratio no bigger than <paramref name="maximum"/>;
    /// the smallest listed one when even that will not fit (a drawing that cannot be
    /// scaled to the paper is the caller's problem to see, not ours to hide).</summary>
    public static double Fit(double maximum)
    {
        double best = Standard[0];
        foreach (double ratio in Standard)
        {
            if (ratio <= maximum)
                best = ratio;
        }
        return best;
    }

    /// <summary>A ratio as a drawing scale ("1:2", "1:1", "5:1").</summary>
    public static string Format(double ratio)
    {
        var culture = CultureInfo.InvariantCulture;
        if (ratio >= 1)
            return string.Create(culture, $"{ratio:0.###}:1");
        return string.Create(culture, $"1:{1 / ratio:0.###}");
    }
}

/// <summary>Where a piece of sheet text sits relative to its anchor point.</summary>
public enum SheetTextAnchor
{
    /// <summary>Anchor at the text's left baseline.</summary>
    Left,

    /// <summary>Anchor at the text's baseline centre.</summary>
    Center,

    /// <summary>Anchor at the text's right baseline.</summary>
    Right,
}

/// <summary>A run of text placed on the sheet (title-block fields, view labels,
/// dimension values). Height is the cap height in sheet millimetres — ISO 3098's
/// nominal, which <see cref="SheetStyle"/> owns.</summary>
/// <param name="Position">Anchor point, sheet mm.</param>
/// <param name="Text">The characters.</param>
/// <param name="Height">Cap height, sheet mm.</param>
/// <param name="Anchor">Which end of the text the position is.</param>
/// <param name="Layer">Layer name.</param>
public sealed record SheetText(
    Vector2d Position, string Text, double Height,
    SheetTextAnchor Anchor = SheetTextAnchor.Left, string Layer = SheetLayers.TitleBlock);

/// <summary>
/// The block in the sheet's bottom-right corner. Every field is optional text; the
/// scale and projection angle are filled in by the sheet itself, so they cannot
/// contradict the drawing they label.
/// </summary>
public sealed record TitleBlock
{
    public string Title { get; init; } = "";
    public string DrawingNumber { get; init; } = "";
    public string Author { get; init; } = "";
    public string Date { get; init; } = "";
    public string Material { get; init; } = "";
    public string Finish { get; init; } = "";
    public string Revision { get; init; } = "";
    public string Company { get; init; } = "";

    /// <summary>The project this drawing belongs to. Carried so the type is complete; the
    /// engineering and schematic title-block layouts do not render it (an ISO 7200 layout,
    /// filed, would). The SCALE is deliberately NOT a field: it is derived from the sheet's own
    /// layout so it cannot contradict the views it labels.</summary>
    public string Project { get; init; } = "";

    /// <summary>Sheet number within a set, e.g. "1 / 3". Carried for completeness; not rendered
    /// by the current layouts (see <see cref="Project"/>).</summary>
    public string Sheet { get; init; } = "";

    /// <summary>Block width, sheet mm (clamped to the drawing area).</summary>
    public double Width { get; init; } = 180;

    /// <summary>Block height, sheet mm.</summary>
    public double Height { get; init; } = 40;
}

/// <summary>
/// The projected, classified, placed content of one view — everything in SHEET
/// coordinates, ready to write.
/// </summary>
/// <param name="Runs">Classified line work.</param>
/// <param name="CutRegions">Faces the section laid open (empty for an ordinary view).</param>
/// <param name="Hatch">Hatch segments filling <paramref name="CutRegions"/>.</param>
/// <param name="Dimensions">Drafting line work (dimension lines, leaders, balloons).</param>
/// <param name="Texts">The view's label and any annotation text.</param>
/// <param name="Bounds">Extent on the sheet, line work only.</param>
public sealed record DrawingViewContent(
    IReadOnlyList<HiddenLineRun> Runs,
    IReadOnlyList<Region2d> CutRegions,
    IReadOnlyList<(Vector2d A, Vector2d B)> Hatch,
    IReadOnlyList<(Vector2d A, Vector2d B)> Dimensions,
    IReadOnlyList<SheetText> Texts,
    Aabb Bounds);

/// <summary>
/// One projected view on a sheet: a set of instances, a direction to look from, a
/// scale, and where on the paper the result sits.
///
/// <para><b>A section view is the same thing with a depth.</b>
/// <see cref="SectionThrough"/> removes everything nearer the viewer than that point;
/// the plane is perpendicular to the view direction by construction (see
/// <see cref="HiddenLineOptions.SectionThrough"/>), so the exposed faces project in
/// true shape and can be hatched and dimensioned honestly.</para>
///
/// <para>The projection is computed once and cached. Changing a placement property
/// (<see cref="Center"/>, <see cref="Scale"/>) does not invalidate it — placement is a
/// sheet transform applied after the fact, which is what makes laying a sheet out cheap
/// even when the geometry is not.</para>
/// </summary>
public sealed class DrawingView
{
    private readonly List<PartInstance> _instances;
    private HiddenLineResult? _projected;
    private IReadOnlyList<Region2d>? _cutRegions;

    /// <summary>A view of an explicit instance list.</summary>
    public DrawingView(IReadOnlyList<PartInstance> instances, in Vector3d direction, string label = "")
    {
        ArgumentNullException.ThrowIfNull(instances);
        _instances = [.. instances];
        Direction = direction.Normalized(Tolerance.Default);
        Frame = StandardViews.SheetFrame(Direction);
        Label = label;
    }

    /// <summary>A view of a whole scene, assemblies flattened.</summary>
    public DrawingView(Scene scene, in Vector3d direction, string label = "")
        : this(scene is not null ? [.. scene.AllInstances] : throw new ArgumentNullException(nameof(scene)),
               direction, label)
    {
        Options = Options with { Quality = scene.ResolveQuality(null) };
    }

    /// <summary>A view of one part, with its own transform applied.</summary>
    public DrawingView(Part part, in Vector3d direction, string label = "")
        : this(part is not null
                   ? [new PartInstance(part, part.Transform, part.Name)]
                   : throw new ArgumentNullException(nameof(part)),
               direction, label)
    {
    }

    /// <summary>A view named from the standard table
    /// (<see cref="StandardViews.Names"/>), which is the SAME table the viewer's Front
    /// button reads — so a drawing and the model on screen cannot disagree about what
    /// "front" means.</summary>
    public static DrawingView Named(Scene scene, string view, string? label = null) =>
        new(scene, DirectionOf(view), label ?? view.ToUpperInvariant());

    /// <inheritdoc cref="Named(Scene, string, string?)"/>
    public static DrawingView Named(Part part, string view, string? label = null) =>
        new(part, DirectionOf(view), label ?? view.ToUpperInvariant());

    private static Vector3d DirectionOf(string view) =>
        StandardViews.DirectionFor(view)
        ?? throw new ArgumentException(
            $"'{view}' is not a standard view; expected one of {string.Join(", ", StandardViews.Names)}.",
            nameof(view));

    /// <summary>The instances this view projects.</summary>
    public IReadOnlyList<PartInstance> Instances => _instances;

    /// <summary>Direction from the model toward the eye.</summary>
    public Vector3d Direction { get; }

    /// <summary>The sheet frame: X right, Y up, Z toward the viewer.</summary>
    public Frame3d Frame { get; }

    /// <summary>Label drawn under the view ("FRONT", "SECTION A-A"); empty draws none.</summary>
    public string Label { get; set; }

    /// <summary>Sheet millimetres per model millimetre.</summary>
    public double Scale { get; set; } = 1;

    /// <summary>Where the view's own bounding-box centre lands on the sheet.</summary>
    public Vector2d Center { get; set; }

    /// <summary>Cut everything nearer the viewer than this point; null for an ordinary
    /// view. See <see cref="HiddenLineOptions.SectionThrough"/>.</summary>
    public Vector3d? SectionThrough
    {
        get => Options.SectionThrough;
        set
        {
            Options = Options with { SectionThrough = value };
            Invalidate();
        }
    }

    /// <summary>Projection settings; assigning discards the cached projection.</summary>
    public HiddenLineOptions Options
    {
        get;
        set
        {
            field = value ?? throw new ArgumentNullException(nameof(value));
            Invalidate();
        }
    } = new();

    /// <summary>Hatch spacing on the SHEET, mm (so a section reads the same at any
    /// drawing scale). ISO 128 hatching is 45 degrees at a legible pitch.</summary>
    public double HatchSpacing { get; set; } = 3;

    /// <summary>Hatch angle from the sheet's x axis, degrees.</summary>
    public double HatchAngleDegrees { get; set; } = 45;

    /// <summary>Dimensions and notes on this view. Their anchors are in the view's own
    /// PROJECTED MODEL coordinates (what <see cref="Projected"/> reports), so a
    /// dimension measures the part rather than the paper; the drawn anatomy is sized in
    /// sheet millimetres by <see cref="Style"/>.</summary>
    public List<SheetAnnotation> Annotations { get; } = [];

    /// <summary>Drafting style for this view's annotations.</summary>
    public SheetStyle Style { get; set; } = SheetStyle.Default;

    /// <summary>Adds a dimension or note and returns it, so a call can stay one
    /// expression.</summary>
    public T Annotate<T>(T annotation) where T : SheetAnnotation
    {
        ArgumentNullException.ThrowIfNull(annotation);
        Annotations.Add(annotation);
        return annotation;
    }

    /// <summary>Discards the cached projection (after a geometry or option change).</summary>
    public void Invalidate()
    {
        _projected = null;
        _cutRegions = null;
    }

    /// <summary>The raw projection in VIEW-LOCAL model coordinates (unscaled,
    /// unplaced) — computed once and reused across layout passes.</summary>
    public HiddenLineResult Projected =>
        _projected ??= HiddenLineRemoval.Project(
            _instances, Frame,
            [.. HiddenLineRemoval.CutLoops(_instances, Frame, Options)], EdgeSource.Cut, Options);

    /// <summary>The view's size on the sheet (line work only), mm.</summary>
    public Vector2d Size
    {
        get
        {
            var bounds = Projected.Bounds;
            return bounds.IsEmpty
                ? Vector2d.Zero
                : new Vector2d(bounds.Size.X * Scale, bounds.Size.Y * Scale);
        }
    }

    /// <summary>The view's content placed on the sheet.</summary>
    public DrawingViewContent Compute()
    {
        var projected = Projected;
        if (projected.Bounds.IsEmpty)
            return new DrawingViewContent([], [], [], [], [], Aabb.Empty);

        var origin = new Vector2d(projected.Bounds.Center.X, projected.Bounds.Center.Y);
        double scale = Scale;
        var center = Center;
        // The one placement rule, as a value so an annotation can be handed it: model
        // space to paper is a uniform scale about the view's own centre.
        Func<Vector2d, Vector2d> ToSheet = p => (p - origin) * scale + center;

        var runs = new List<HiddenLineRun>(projected.Runs.Count);
        var bounds = Aabb.Empty;
        foreach (var run in projected.Runs)
        {
            var points = new Vector2d[run.Points.Count];
            for (int i = 0; i < run.Points.Count; i++)
            {
                points[i] = ToSheet(run.Points[i]);
                bounds = bounds.Union(new Vector3d(points[i].X, points[i].Y, 0));
            }
            runs.Add(run with { Points = points });
        }

        var regions = new List<Region2d>();
        foreach (var region in CutRegions())
        {
            var loops = new List<IReadOnlyList<Vector2d>> { Map(region.Outer) };
            foreach (var hole in region.Holes)
                loops.Add(Map(hole));
            regions.AddRange(Region2d.FromLoops(loops));

            IReadOnlyList<Vector2d> Map(IReadOnlyList<Vector2d> loop)
            {
                var mapped = new Vector2d[loop.Count];
                for (int i = 0; i < loop.Count; i++)
                    mapped[i] = ToSheet(loop[i]);
                return mapped;
            }
        }

        var hatch = SheetHatch.Fill(regions, HatchSpacing, HatchAngleDegrees);

        var texts = new List<SheetText>();
        var dimensions = new List<(Vector2d A, Vector2d B)>();
        foreach (var annotation in Annotations)
            annotation.Build(ToSheet, Style, dimensions, texts);

        if (!string.IsNullOrEmpty(Label))
        {
            // The label goes below EVERYTHING the view drew, dimension text included —
            // a label that clears the line work but lands on a dimension value is the
            // same collision, one step further down.
            double labelY = bounds.IsEmpty ? Center.Y : bounds.Min.Y;
            foreach (var (a, b) in dimensions)
                labelY = Math.Min(labelY, Math.Min(a.Y, b.Y));
            foreach (var text in texts)
                labelY = Math.Min(labelY, text.Position.Y);
            texts.Add(new SheetText(
                new Vector2d(Center.X, labelY - SheetLettering.LabelGap),
                Label, SheetLettering.LabelHeight, SheetTextAnchor.Center, SheetLayers.Visible));
        }

        return new DrawingViewContent(runs, regions, hatch, dimensions, texts, bounds);
    }

    /// <summary>
    /// The cut faces in view-local 2D, exact from the B-Rep where the parts lower and
    /// mesh-derived otherwise — the same "exact where the kernel has it" rule the edges
    /// follow. A plane flush with a face makes the exact route refuse (that section is
    /// an area, not a curve), and the mesh loops answer instead rather than the view
    /// losing its hatching.
    /// </summary>
    private IReadOnlyList<Region2d> CutRegions()
    {
        if (_cutRegions is not null)
            return _cutRegions;
        if (SectionThrough is not { } through)
            return _cutRegions = [];

        // The section frame shares the view's axes and sits ON the cutting plane, so its
        // 2D coordinates ARE the view's — no second projection, nothing to line up (a
        // shift along the frame's own Z leaves x and y untouched).
        var planeFrame = Frame3d.FromOrthonormal(through, Frame.X, Frame.Y);
        var regions = new List<Region2d>();
        var mesh = new List<IReadOnlyList<Vector2d>>();
        foreach (var instance in DebugFilter.Exported(_instances))
        {
            if (!instance.Part.ClippedBySection)
                continue;   // drawn whole through the cut: it has no cut face
            var exact = TryExactSection(instance, planeFrame);
            if (exact is not null)
                regions.AddRange(exact);
        }

        // Anything the exact route declined falls back to the mesh cut's own loops.
        foreach (var loop in HiddenLineRemoval.CutLoops(_instances, Frame, Options))
        {
            if (regions.Count > 0)
                break;
            var flat = new Vector2d[loop.Count];
            for (int i = 0; i < loop.Count; i++)
            {
                var local = Frame.ToLocal(loop[i]);
                flat[i] = new Vector2d(local.X, local.Y);
            }
            mesh.Add(flat);
        }
        if (mesh.Count > 0)
            regions.AddRange(Region2d.FromLoops(mesh));
        return _cutRegions = regions;
    }

    private static IReadOnlyList<Region2d>? TryExactSection(PartInstance instance, Frame3d planeFrame)
    {
        var solid = instance.Part.TryGetSolid();
        if (solid is null)
            return null;
        try
        {
            // The solid is part-local; move the plane into its frame rather than the
            // solid into the world (a BrepSolid has no cheap transform, and the section
            // is a plane).
            if (!instance.World.TryInvert(out var toLocal))
                return null;
            var localFrame = Frame3d.FromXY(
                toLocal.TransformPoint(planeFrame.Origin),
                toLocal.TransformVector(planeFrame.X),
                toLocal.TransformVector(planeFrame.Y));
            var local = PlanarSection.OfSolid(solid, localFrame);
            if (local.Count == 0)
                return null;
            // Back into the view's own 2D: the local frame's axes came from the view's,
            // so this is a rigid re-expression, not a re-projection.
            var loops = new List<IReadOnlyList<Vector2d>>();
            foreach (var region in local)
            {
                loops.Add(Lift(region.Outer));
                foreach (var hole in region.Holes)
                    loops.Add(Lift(hole));
            }
            return Region2d.FromLoops(loops);

            IReadOnlyList<Vector2d> Lift(IReadOnlyList<Vector2d> loop)
            {
                var lifted = new Vector2d[loop.Count];
                for (int i = 0; i < loop.Count; i++)
                {
                    var world = instance.World.TransformPoint(
                        localFrame.ToWorld(new Vector3d(loop[i].X, loop[i].Y, 0)));
                    var flat = planeFrame.ToLocal(world);
                    lifted[i] = new Vector2d(flat.X, flat.Y);
                }
                return lifted;
            }
        }
        catch (Exception exception) when (exception is NotSupportedException or ArgumentException
                                                   or InvalidOperationException)
        {
            // A plane flush with a face, an unlowerable body: fall back to the mesh.
            return null;
        }
    }
}

/// <summary>
/// A drawing sheet: paper, border, title block and a set of placed views.
///
/// <para>Build one by hand (add views, set their <see cref="DrawingView.Center"/> and
/// <see cref="DrawingView.Scale"/>) or take the standard layout from
/// <see cref="StandardLayout(Scene, SheetFormat, ProjectionAngle, bool)"/>, which sizes
/// and places a three-view set plus an isometric.</para>
/// </summary>
public sealed class DrawingSheet
{
    private readonly List<DrawingView> _views = [];

    public DrawingSheet(SheetFormat? format = null)
    {
        Format = format ?? SheetFormat.A3;
    }

    public SheetFormat Format { get; set; }

    /// <summary>Distance from the paper edge to the border, mm.</summary>
    public double Margin { get; set; } = 10;

    /// <summary>Which side of the front view its neighbours sit on; printed in the
    /// title block.</summary>
    public ProjectionAngle Projection { get; set; } = ProjectionAngle.Third;

    public TitleBlock Title { get; set; } = new();

    /// <summary>Opt-in sheet-standard furniture (ISO 5457 zone grid and centring marks);
    /// default <see cref="FrameStandards.None"/> leaves the sheet's output byte-identical.</summary>
    public FrameStandards Standards { get; set; } = FrameStandards.None;

    public IReadOnlyList<DrawingView> Views => _views;

    /// <summary>
    /// The shared <see cref="DrawingFrame"/> this sheet draws its border and title block from —
    /// the SAME frame value the ECAD schematic sheet uses, so the two cannot disagree about what
    /// a frame looks like. The scale and projection angle are read from the layout, not the
    /// caller, so they cannot contradict the views.
    /// </summary>
    public DrawingFrame Frame()
    {
        string scale = _views.Count > 0
            ? DrawingScales.Format(_views[0].Scale)
            : DrawingScales.Format(1);
        return new DrawingFrame
        {
            Format = Format,
            Margin = Margin,
            Title = Title,
            Layout = new EngineeringTitleBlock(scale, Projection),
            Standards = Standards,
        };
    }

    public DrawingSheet Add(DrawingView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        _views.Add(view);
        return this;
    }

    /// <summary>The border rectangle, sheet mm (bottom-left, top-right).</summary>
    public Aabb Border => new(
        new Vector3d(Margin, Margin, 0),
        new Vector3d(Format.Width - Margin, Format.Height - Margin, 0));

    /// <summary>The area a view may occupy: inside the border, above the title block.</summary>
    public Aabb DrawingArea
    {
        get
        {
            var border = Border;
            return new Aabb(
                new Vector3d(border.Min.X, border.Min.Y + Title.Height, 0),
                border.Max);
        }
    }

    /// <summary>
    /// The classic three-view layout plus an isometric: front, top and right at one
    /// shared scale, placed per <paramref name="projection"/>, sized to the largest
    /// STANDARD ratio that fits the drawing area.
    ///
    /// <para>The three orthographic directions come from
    /// <see cref="StandardViews.DirectionFor"/> — the same table the viewer's toolbar
    /// reads, so a sheet's FRONT is the viewer's Front by construction rather than by
    /// coincidence.</para>
    /// </summary>
    public static DrawingSheet StandardLayout(
        Scene scene, SheetFormat? format = null,
        ProjectionAngle projection = ProjectionAngle.Third, bool isometric = true)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return StandardLayout([.. scene.AllInstances], format, projection, isometric,
            scene.ResolveQuality(null));
    }

    /// <inheritdoc cref="StandardLayout(Scene, SheetFormat?, ProjectionAngle, bool)"/>
    public static DrawingSheet StandardLayout(
        Part part, SheetFormat? format = null,
        ProjectionAngle projection = ProjectionAngle.Third, bool isometric = true)
    {
        ArgumentNullException.ThrowIfNull(part);
        return StandardLayout(
            [new PartInstance(part, part.Transform, part.Name)], format, projection, isometric, null);
    }

    /// <inheritdoc cref="StandardLayout(Scene, SheetFormat?, ProjectionAngle, bool)"/>
    public static DrawingSheet StandardLayout(
        IReadOnlyList<PartInstance> instances, SheetFormat? format = null,
        ProjectionAngle projection = ProjectionAngle.Third, bool isometric = true,
        MeshQuality? quality = null)
    {
        ArgumentNullException.ThrowIfNull(instances);
        var sheet = new DrawingSheet(format) { Projection = projection };

        var front = new DrawingView(instances, StandardViews.DirectionFor("front")!.Value, "FRONT");
        var top = new DrawingView(instances, StandardViews.DirectionFor("top")!.Value, "TOP");
        var right = new DrawingView(instances, StandardViews.DirectionFor("right")!.Value, "RIGHT");
        DrawingView? iso = isometric
            ? new DrawingView(instances, StandardViews.DirectionFor("iso")!.Value, "ISO")
            : null;
        foreach (var view in new[] { front, top, right, iso })
        {
            if (view is not null && quality is not null)
                view.Options = view.Options with { Quality = quality };
        }

        sheet.Arrange(front, top, right, iso);
        sheet.Add(front).Add(top).Add(right);
        if (iso is not null)
            sheet.Add(iso);
        return sheet;
    }

    /// <summary>Chooses one scale for the set and places the views, third or first
    /// angle. Public so a hand-built sheet can re-run the layout after an edit.</summary>
    public void Arrange(DrawingView front, DrawingView top, DrawingView right, DrawingView? isometric = null)
    {
        ArgumentNullException.ThrowIfNull(front);
        ArgumentNullException.ThrowIfNull(top);
        ArgumentNullException.ThrowIfNull(right);

        var area = DrawingArea;
        double gap = ViewGap;
        // Unscaled extents, READ OFF THE PROJECTIONS rather than assumed from the model
        // box, so a rotated part still lays out correctly.
        double frontW = Extent(front).X, frontH = Extent(front).Y;
        double topH = Extent(top).Y;
        double rightW = Extent(right).X;

        // The layout is a 2x2 grid of cells, and the isometric occupies the corner one.
        // Sizing the grid from the three orthographic views alone is the bug that lets a
        // pictorial view — which is always the widest, being a diagonal of the whole box
        // — spill over its neighbours: the corner cell must be as big as whichever of
        // the two claimants needs it.
        double isoW = isometric is not null ? Extent(isometric).X : 0;
        double isoH = isometric is not null ? Extent(isometric).Y : 0;
        double sideW = Math.Max(rightW, isoW);
        double aboveH = Math.Max(topH, isoH);

        double labelBand = SheetLettering.LabelGap + SheetLettering.LabelHeight;
        double availableW = area.Size.X - 2 * gap;
        double availableH = area.Size.Y - 2 * gap - 2 * labelBand;
        double widthRatio = frontW + sideW > 0 ? (availableW - gap) / (frontW + sideW) : 1;
        double heightRatio = frontH + aboveH > 0 ? (availableH - gap) / (frontH + aboveH) : 1;
        double scale = DrawingScales.Fit(Math.Min(widthRatio, heightRatio));

        front.Scale = top.Scale = right.Scale = scale;
        if (isometric is not null)
            isometric.Scale = scale;

        double blockW = scale * (frontW + sideW) + gap;
        double blockH = scale * (frontH + aboveH) + gap;
        double left = area.Min.X + (area.Size.X - blockW) / 2;
        double bottom = area.Min.Y + (area.Size.Y - blockH) / 2;

        // Third angle: top ABOVE front, right to the RIGHT. First angle mirrors both.
        bool third = Projection == ProjectionAngle.Third;
        double frontCx = left + (third ? scale * frontW / 2 : blockW - scale * frontW / 2);
        double frontCy = bottom + (third ? scale * frontH / 2 : blockH - scale * frontH / 2);
        double sideCx = third
            ? left + scale * frontW + gap + scale * sideW / 2
            : left + blockW - scale * frontW - gap - scale * sideW / 2;
        double aboveCy = third
            ? bottom + scale * frontH + gap + scale * aboveH / 2
            : bottom + blockH - scale * frontH - gap - scale * aboveH / 2;

        front.Center = new Vector2d(frontCx, frontCy);
        top.Center = new Vector2d(frontCx, aboveCy);
        right.Center = new Vector2d(sideCx, frontCy);
        if (isometric is not null)
            isometric.Center = new Vector2d(sideCx, aboveCy);
    }

    /// <summary>Space between neighbouring views, sheet mm.</summary>
    public double ViewGap { get; set; } = 20;

    private static Vector2d Extent(DrawingView view)
    {
        var bounds = view.Projected.Bounds;
        return bounds.IsEmpty ? Vector2d.Zero : new Vector2d(bounds.Size.X, bounds.Size.Y);
    }

    /// <summary>
    /// The whole sheet as drawable primitives: the border, the title block's frame and
    /// text, and every view's content, all in sheet coordinates. This is the ONE place
    /// the sheet's appearance is decided, so the SVG and DXF writers cannot disagree
    /// about what a sheet looks like — they differ only in how they spell a polyline.
    /// </summary>
    public SheetContent Compute()
    {
        var lines = new List<(Vector2d A, Vector2d B, string Layer)>();
        var texts = new List<SheetText>();
        var runs = new List<HiddenLineRun>();
        var hatch = new List<(Vector2d A, Vector2d B)>();

        // The border and title block come from the shared frame, so a drawing and a schematic
        // of one project draw the same furniture. The frame's lines/texts are merged AHEAD of
        // the views (border and title-block layers before the dimensions), as before.
        var frame = Frame().Compute();
        lines.AddRange(frame.Lines);
        texts.AddRange(frame.Texts);

        foreach (var view in _views)
        {
            var content = view.Compute();
            runs.AddRange(content.Runs);
            hatch.AddRange(content.Hatch);
            texts.AddRange(content.Texts);
            foreach (var (a, b) in content.Dimensions)
                lines.Add((a, b, SheetLayers.Dimensions));
        }
        return new SheetContent(runs, hatch, lines, texts, Format);
    }
}

/// <summary>Everything a writer needs to put a sheet on paper, in sheet millimetres.</summary>
/// <param name="Runs">Classified view line work.</param>
/// <param name="Hatch">Section hatch segments.</param>
/// <param name="Lines">Border, title-block and dimension segments, each with a layer.</param>
/// <param name="Texts">Every piece of text.</param>
/// <param name="Format">The paper.</param>
public sealed record SheetContent(
    IReadOnlyList<HiddenLineRun> Runs,
    IReadOnlyList<(Vector2d A, Vector2d B)> Hatch,
    IReadOnlyList<(Vector2d A, Vector2d B, string Layer)> Lines,
    IReadOnlyList<SheetText> Texts,
    SheetFormat Format);

/// <summary>
/// Straight-line hatching clipped to a set of regions — the section fill.
///
/// <para>The clip is an exact even-odd scan, not a polygon boolean: intersect the hatch
/// line with every loop edge, sort the crossings along the line and keep alternate
/// spans. The only decision that needs care is a crossing exactly at a vertex, which is
/// settled by a HALF-OPEN rule on the edge's span across the line — count an edge when
/// its lower end is on or above the line and its upper end strictly below (or the
/// mirror) — so a vertex is counted by exactly one of its two edges and parity never
/// double-counts.</para>
/// </summary>
public static class SheetHatch
{
    /// <summary>Hatch segments filling <paramref name="regions"/> at
    /// <paramref name="spacing"/> apart, running at <paramref name="angleDegrees"/> from
    /// the x axis. Line positions are anchored to the ORIGIN rather than to each
    /// region's own bounds, so two adjacent cut faces share one continuous pattern.</summary>
    public static IReadOnlyList<(Vector2d A, Vector2d B)> Fill(
        IReadOnlyList<Region2d> regions, double spacing, double angleDegrees)
    {
        ArgumentNullException.ThrowIfNull(regions);
        var segments = new List<(Vector2d A, Vector2d B)>();
        if (regions.Count == 0 || !(spacing > 0))
            return segments;

        double radians = angleDegrees * Math.PI / 180;
        var direction = new Vector2d(Math.Cos(radians), Math.Sin(radians));
        var normal = new Vector2d(-direction.Y, direction.X);

        foreach (var region in regions)
        {
            var loops = new List<IReadOnlyList<Vector2d>> { region.Outer };
            loops.AddRange(region.Holes);

            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            foreach (var loop in loops)
            {
                foreach (var point in loop)
                {
                    double c = point.Dot(normal);
                    min = Math.Min(min, c);
                    max = Math.Max(max, c);
                }
            }
            if (!(max > min))
                continue;

            int first = (int)Math.Ceiling(min / spacing);
            int last = (int)Math.Floor(max / spacing);
            var crossings = new List<double>();
            for (int k = first; k <= last; k++)
            {
                double c = k * spacing;
                crossings.Clear();
                foreach (var loop in loops)
                {
                    for (int i = 0; i < loop.Count; i++)
                    {
                        var a = loop[i];
                        var b = loop[(i + 1) % loop.Count];
                        double ca = a.Dot(normal), cb = b.Dot(normal);
                        // Half-open span test (see the remarks): a vertex belongs to
                        // exactly one of its two edges, so parity is never doubled.
                        if (ca <= c == cb <= c)
                            continue;
                        double t = (c - ca) / (cb - ca);
                        crossings.Add((a + (b - a) * t).Dot(direction));
                    }
                }
                if (crossings.Count < 2)
                    continue;
                crossings.Sort();
                for (int i = 0; i + 1 < crossings.Count; i += 2)
                {
                    // Exact-zero-length skip: two crossings at one point enclose nothing.
                    if (crossings[i + 1] <= crossings[i])
                        continue;
                    segments.Add((
                        normal * c + direction * crossings[i],
                        normal * c + direction * crossings[i + 1]));
                }
            }
        }
        return segments;
    }
}
