using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Modeling;

namespace EngrCAD.Ecad;

/// <summary>Named line-work layers a <see cref="PcbLayerPlotDrawing"/> writes to, so an SVG or DXF
/// consumer can toggle the layer's geometry apart from the board outline and the label. The board
/// outline rides the SHARED <see cref="FabricationLayers.Outline"/> and the label the SHARED
/// <see cref="FabricationLayers.Notes"/>, so a plot and the fab drawing style them alike; the four
/// geometry layers are distinct so a copper plot and a mask plot draw on their own class. Fixed
/// strings rather than an enum because they cross into files (the <see cref="SheetLayers"/>
/// rule).</summary>
public static class FabricationPlotLayers
{
    /// <summary>The layer's copper region boundaries (pads, via pads, traces, pours).</summary>
    public const string Copper = "copper";

    /// <summary>The layer's solder-mask window boundaries.</summary>
    public const string Mask = "mask";

    /// <summary>The layer's silkscreen line-work.</summary>
    public const string Silk = "silk";

    /// <summary>The layer's solder-paste aperture boundaries.</summary>
    public const string Paste = "paste";
}

/// <summary>Which fabrication layer a <see cref="PcbLayerPlot"/> draws — one of the four layer classes
/// a fab package plots per side.</summary>
public enum PcbPlotLayerKind
{
    /// <summary>A copper layer (the pads, via pads, traces and pours on one copper plane).</summary>
    Copper,

    /// <summary>A solder-mask layer (the window openings over the solderable pads on one side).</summary>
    SolderMask,

    /// <summary>A silkscreen layer (the reference / value / courtyard line-work on one side).</summary>
    Silkscreen,

    /// <summary>A solder-paste (stencil) layer (the SMD-pad apertures on one side).</summary>
    SolderPaste,
}

/// <summary>
/// The set of per-layer fabrication PLOTS for a board — the human-readable sheets a fab package
/// includes BESIDE the Gerbers, one per copper / solder-mask / silkscreen / solder-paste layer, each
/// rendering that layer's own geometry as line-work on the SHARED engineering frame.
///
/// <para><b>It consumes the copper model's own regions; it never re-derives copper.</b>
/// <see cref="For"/> builds one <see cref="PcbCopperModel"/> and reads each copper layer's features
/// (<see cref="PcbCopperModel.Copper"/>) and the mask / silk / paste content (<see cref="PcbMask"/> /
/// <see cref="PcbSilkscreen"/> / <see cref="PcbPaste"/>) — the SAME geometry the Gerber exporter
/// consumes — so a plot and its Gerber cannot disagree about what is on a layer (the ECAD
/// one-declaration rule, applied to a plot).</para>
///
/// <para><b>Mask / silk / paste plots appear only when the layout DECLARES those layers</b>
/// (<see cref="PcbLayout.MaskSettings"/> / <see cref="PcbLayout.SilkscreenSettings"/> /
/// <see cref="PcbLayout.PasteSettings"/>, write-only-when-stated), so a bare copper board plots just
/// its copper layers.</para>
///
/// <para><b>The bottom-mirror convention.</b> A bottom-side layer is plotted VIEWED FROM THE BOTTOM —
/// mirrored about the board's vertical axis — as a fab drawing set always is, so the plot reads the
/// way the fabricator looks at that side; the top and inner layers are viewed from the top. Each plot
/// states its <see cref="PcbLayerPlot.ViewSide"/> and <see cref="PcbLayerPlot.Mirrored"/>.</para>
/// </summary>
public static class PcbFabricationPlots
{
    /// <summary>
    /// Builds the plot set for a layout — one plot per copper layer (in stackup order), then one per
    /// declared solder-mask / silkscreen / solder-paste side. A null title defaults to the schematic's
    /// name; a null format uses <see cref="PcbLayerPlot.DefaultFormat"/>.
    /// </summary>
    /// <param name="layout">The board layout to plot — read only.</param>
    /// <param name="format">The paper size (default <see cref="PcbLayerPlot.DefaultFormat"/>).</param>
    /// <param name="title">Title-block fields (default: title = the schematic's name).</param>
    /// <param name="standards">Opt-in ISO 5457 frame furniture (default none).</param>
    public static IReadOnlyList<PcbLayerPlot> For(
        PcbLayout layout,
        SheetFormat? format = null,
        TitleBlock? title = null,
        FrameStandards? standards = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var model = PcbCopperModel.FromLayout(layout);
        string bottomCopper = model.Board.Stackup.Bottom.Name;

        var plots = new List<PcbLayerPlot>();

        // One plot per copper layer, in stackup order. The bottom copper is a bottom-side layer
        // (viewed mirrored); every other copper layer (top, inner) is viewed from the top.
        foreach (var layerName in model.Layers)
        {
            var side = layerName == bottomCopper ? CopperSide.Bottom : CopperSide.Top;
            // The layer's copper is the copper MODEL's own region objects — no re-derivation.
            var regions = model.Copper.Where(f => f.Layer == layerName).Select(f => f.Region).ToList();
            plots.Add(new PcbLayerPlot(
                layout, PcbPlotLayerKind.Copper, layerName, side, regions, [], format, title, standards));
        }

        // Solder mask — the pad-window openings on each outer side, when declared.
        if (layout.MaskSettings is { } maskSettings)
            foreach (var m in PcbMask.For(model, maskSettings).Layers)
                plots.Add(new PcbLayerPlot(
                    layout, PcbPlotLayerKind.SolderMask, m.Layer, m.Side,
                    [.. m.Openings.Select(o => o.Region)], [], format, title, standards));

        // Silkscreen — the reference / value / courtyard line-work on each outer side, when declared.
        if (layout.SilkscreenSettings is { } silkSettings)
            foreach (var s in PcbSilkscreen.For(layout, silkSettings).Layers)
                plots.Add(new PcbLayerPlot(
                    layout, PcbPlotLayerKind.Silkscreen, s.Layer, s.Side,
                    [], [.. s.Strokes.Select(st => st.Points)], format, title, standards));

        // Solder paste — the SMD-pad apertures on each outer side, when declared.
        if (layout.PasteSettings is { } pasteSettings)
            foreach (var p in PcbPaste.For(model, pasteSettings).Layers)
                plots.Add(new PcbLayerPlot(
                    layout, PcbPlotLayerKind.SolderPaste, p.Layer, p.Side,
                    [.. p.Apertures.Select(a => a.Region)], [], format, title, standards));

        return plots;
    }
}

/// <summary>
/// One layer's fabrication PLOT — the layer name, its <see cref="ViewSide"/> and the geometry to draw,
/// on the SHARED engineering frame. It reads the layer; it never edits the board (the fab-drawing
/// rule), and the geometry it draws is the copper model's / mask's / silk's / paste's OWN regions and
/// strokes, so a plot and its Gerber cannot disagree.
///
/// <para><b>The frame is the fab drawing's frame.</b> <see cref="Frame"/> given the same paper and
/// title-block fields as a <see cref="PcbFabricationSheet"/> — or a mechanical <see cref="DrawingSheet"/>
/// — produces byte-identical furniture, since all three configure ONE shared <see cref="DrawingFrame"/>.</para>
/// </summary>
public sealed class PcbLayerPlot
{
    /// <summary>The default paper: A4 landscape (a single layer wants less room than the fab drawing's
    /// map + tables).</summary>
    public static SheetFormat DefaultFormat => SheetFormat.A4;

    /// <summary>Distance from the paper edge to the border, mm — the shared frame default.</summary>
    public const double DefaultMargin = 10;

    // Layout constants (sheet mm): the layer fills the drawing area minus a small inset and a band
    // at the top for the layer label.
    private const double BoardPad = 8;      // inset of the layer within the drawing area
    private const double LabelBand = 14;    // room at the top for the label
    private const double LabelHeight = 4;   // the layer-name label height
    private const double NoteHeight = 2.5;  // the view-note height

    private readonly PcbLayout _layout;
    private readonly PcbBoard _board;
    private readonly IReadOnlyList<CurvedRegion2d> _regions;
    private readonly IReadOnlyList<IReadOnlyList<Vector2d>> _strokes;

    internal PcbLayerPlot(
        PcbLayout layout, PcbPlotLayerKind kind, string layerName, CopperSide side,
        IReadOnlyList<CurvedRegion2d> regions, IReadOnlyList<IReadOnlyList<Vector2d>> strokes,
        SheetFormat? format, TitleBlock? title, FrameStandards? standards)
    {
        _layout = layout;
        _board = layout.Board;
        _regions = regions;
        _strokes = strokes;
        Kind = kind;
        LayerName = layerName;
        ViewSide = side;
        Format = format ?? DefaultFormat;
        Title = title ?? new TitleBlock { Title = layout.Schematic.Name };
        Standards = standards ?? FrameStandards.None;
    }

    /// <summary>The layout this plots (read only).</summary>
    public PcbLayout Layout => _layout;

    /// <summary>Which fabrication layer this plots.</summary>
    public PcbPlotLayerKind Kind { get; }

    /// <summary>The layer's name (<c>"Top"</c>, <c>"Bottom"</c>, <c>"In1"</c>).</summary>
    public string LayerName { get; }

    /// <summary>The board side this layer belongs to, so a caller knows which face it documents.</summary>
    public CopperSide ViewSide { get; }

    /// <summary>Whether the plot is MIRRORED — true for a bottom-side layer (viewed from the bottom, the
    /// fabrication convention), false for a top or inner layer.</summary>
    public bool Mirrored => ViewSide == CopperSide.Bottom;

    /// <summary>The paper.</summary>
    public SheetFormat Format { get; }

    /// <summary>Distance from the paper edge to the border, mm.</summary>
    public double Margin { get; init; } = DefaultMargin;

    /// <summary>The projection angle printed in the shared title block. A layer plot is not an
    /// orthographic projection, but the shared engineering title block always names one, so it is
    /// carried (default third angle) rather than a plot-only block that would forfeit the byte-identity.</summary>
    public ProjectionAngle Projection { get; init; } = ProjectionAngle.Third;

    /// <summary>The title-block field values.</summary>
    public TitleBlock Title { get; }

    /// <summary>Opt-in sheet-standard furniture; default <see cref="FrameStandards.None"/> leaves the
    /// frame byte-identical to a sheet predating it.</summary>
    public FrameStandards Standards { get; }

    /// <summary>The board-to-sheet scale (sheet mm per board mm) the layer is drawn at — the largest
    /// standard ISO 5455 ratio that fits the drawing area. Printed in the title block.</summary>
    public double Scale => ComputeScale();

    /// <summary>
    /// The shared <see cref="DrawingFrame"/> this plot draws its border and title block from — the SAME
    /// frame value the fab drawing and a mechanical <see cref="DrawingSheet"/> use, on the SAME
    /// <see cref="SheetLayers"/> with the SAME three-band <see cref="EngineeringTitleBlock"/>. So a
    /// layer plot, a fab drawing and a mechanical drawing of one board, at the same paper and fields,
    /// draw byte-identical furniture — one function, so they cannot disagree.
    /// </summary>
    public DrawingFrame Frame() => new()
    {
        Format = Format,
        Margin = Margin,
        Title = Title,
        BorderLayer = SheetLayers.Border,
        TitleBlockLayer = SheetLayers.TitleBlock,
        Layout = new EngineeringTitleBlock(DrawingScales.Format(ComputeScale()), Projection),
        Standards = Standards,
    };

    /// <summary>Renders the plot — the deterministic function of the layer geometry and the paper.</summary>
    public PcbLayerPlotDrawing Compute()
    {
        var segments = new List<(Vector2d A, Vector2d B, string Layer)>();
        var texts = new List<SheetText>();

        // The border and title block come from the shared DrawingFrame, merged AHEAD of the body as
        // the frame's own line work always is.
        var frame = Frame().Compute();
        foreach (var line in frame.Lines)
            segments.Add((line.A, line.B, line.Layer));
        texts.AddRange(frame.Texts);

        var area = DrawingArea();
        var boardArea = new Aabb(
            area.Min, new Vector3d(area.Max.X, area.Max.Y - LabelBand, 0));
        double scale = ComputeScale();
        var (bmin, bmax) = PcbFabricationSheet.BoardBounds(_board);
        var boardCenter = (bmin + bmax) * 0.5;
        var columnCenter = new Vector2d(
            (boardArea.Min.X + boardArea.Max.X) * 0.5,
            (boardArea.Min.Y + boardArea.Max.Y) * 0.5);

        Vector2d Reflect(Vector2d d) => Mirrored ? new Vector2d(-d.X, d.Y) : d;
        Vector2d Project(Vector2d p) => columnCenter + Reflect(p - boardCenter) * scale;

        // The board outline for context (the plot is read against the board's own shape).
        var outline = new List<Vector2d>(_board.OutlinePoints.Count);
        foreach (var p in _board.OutlinePoints)
            outline.Add(Project(p));
        for (int i = 0; i < outline.Count; i++)
            segments.Add((outline[i], outline[(i + 1) % outline.Count], FabricationLayers.Outline));

        // The layer's geometry: region boundaries (copper / mask / paste) or open strokes (silk).
        string geometryLayer = GeometryLayer(Kind);
        foreach (var region in _regions)
        {
            DrawLoop(segments, region.Outer, Project, geometryLayer);
            foreach (var hole in region.Holes)
                DrawLoop(segments, hole, Project, geometryLayer);
        }
        foreach (var stroke in _strokes)
            for (int i = 0; i + 1 < stroke.Count; i++)
                segments.Add((Project(stroke[i]), Project(stroke[i + 1]), geometryLayer));

        // The label: the layer name + kind, and the view note stating the mirror convention.
        double labelX = area.Min.X + BoardPad;
        double labelTop = area.Max.Y - BoardPad;
        texts.Add(new SheetText(
            new Vector2d(labelX, labelTop - LabelHeight), LabelText(),
            LabelHeight, SheetTextAnchor.Left, FabricationLayers.Notes));
        texts.Add(new SheetText(
            new Vector2d(labelX, labelTop - LabelHeight - NoteHeight * 1.6), ViewNote(),
            NoteHeight, SheetTextAnchor.Left, FabricationLayers.Notes));

        return new PcbLayerPlotDrawing(
            Format: Format,
            LayerName: LayerName,
            Kind: Kind,
            ViewSide: ViewSide,
            Mirrored: Mirrored,
            Scale: scale,
            BoardCenter: boardCenter,
            ColumnCenter: columnCenter,
            Segments: segments,
            Texts: texts,
            Outline: outline,
            Regions: _regions,
            Strokes: _strokes);
    }

    /// <summary>The layer-name label a plot prints (<c>"TOP COPPER"</c>, <c>"BOTTOM SOLDER MASK"</c>).</summary>
    public string LabelText() => $"{LayerName.ToUpperInvariant()} {KindLabel(Kind)}";

    /// <summary>The view note a plot prints, stating the mirror convention.</summary>
    public string ViewNote() =>
        Mirrored ? "VIEWED FROM BOTTOM (MIRRORED)" : "VIEWED FROM TOP";

    internal static string KindLabel(PcbPlotLayerKind kind) => kind switch
    {
        PcbPlotLayerKind.Copper => "COPPER",
        PcbPlotLayerKind.SolderMask => "SOLDER MASK",
        PcbPlotLayerKind.Silkscreen => "SILKSCREEN",
        PcbPlotLayerKind.SolderPaste => "SOLDER PASTE",
        _ => "LAYER",
    };

    internal static string GeometryLayer(PcbPlotLayerKind kind) => kind switch
    {
        PcbPlotLayerKind.Copper => FabricationPlotLayers.Copper,
        PcbPlotLayerKind.SolderMask => FabricationPlotLayers.Mask,
        PcbPlotLayerKind.Silkscreen => FabricationPlotLayers.Silk,
        PcbPlotLayerKind.SolderPaste => FabricationPlotLayers.Paste,
        _ => FabricationPlotLayers.Copper,
    };

    // The drawing area (inside the border, above the title block) is a function of the paper and the
    // title-block height only, never of the title-block LAYOUT, so a probe frame carrying a placeholder
    // layout reads it, which is what lets Frame() ask for the scale it prints.
    private Aabb DrawingArea() => new DrawingFrame
    {
        Format = Format,
        Margin = Margin,
        Title = Title,
        Layout = EngineeringTitleBlock.Default,
    }.DrawingArea;

    private double ComputeScale()
    {
        var area = DrawingArea();
        double columnWidth = area.Size.X - 2 * BoardPad;
        double columnHeight = area.Size.Y - LabelBand - 2 * BoardPad;
        var (bmin, bmax) = PcbFabricationSheet.BoardBounds(_board);
        double boardWidth = bmax.X - bmin.X;
        double boardHeight = bmax.Y - bmin.Y;
        if (boardWidth <= 0 || boardHeight <= 0)
            return 1;
        return DrawingScales.Fit(Math.Min(columnWidth / boardWidth, columnHeight / boardHeight));
    }

    // Draws one closed loop of a curved region as line-work: a line emits one segment, an arc (or a
    // Bézier fallback) is flattened to a chord polyline at a density set by its sweep.
    private static void DrawLoop(
        List<(Vector2d A, Vector2d B, string Layer)> segments,
        IReadOnlyList<CurvedEdge2d> loop, Func<Vector2d, Vector2d> project, string layer)
    {
        var points = new List<Vector2d>();
        foreach (var edge in loop)
        {
            if (edge.Kind == CurvedEdgeKind.Line)
            {
                points.Add(edge.Start);
            }
            else
            {
                // Sample [0, 1) — the edge's start plus interior points; its end is the next edge's
                // start (or, at the loop's last edge, the loop's start), so the chain stays closed.
                int n = edge.Kind == CurvedEdgeKind.Arc
                    ? Math.Max(2, (int)Math.Ceiling(Math.Abs(edge.SweepAngle) / (Math.PI / 32)))
                    : 24;
                for (int i = 0; i < n; i++)
                    points.Add(edge.PointAt(i / (double)n));
            }
        }
        if (points.Count < 2)
            return;
        for (int i = 0; i < points.Count; i++)
            segments.Add((project(points[i]), project(points[(i + 1) % points.Count]), layer));
    }
}

/// <summary>
/// The computed per-layer plot: the layer's line-work on the shared frame, the layer's own regions /
/// strokes (the copper model's / mask's / paste's / silk's OWN geometry, not a re-derivation), and the
/// SVG / DXF / PDF writers — over the same primitives, so the three cannot disagree (the drawing-sheet
/// one-Compute rule).
///
/// <para>Everything is already in SHEET coordinates (millimetres, origin at the paper's bottom-left,
/// y up — the <see cref="SvgDrawing"/> convention). <see cref="Project"/> maps a board point onto the
/// sheet the same way the drawn geometry was placed — mirrored for a bottom-side layer — so a test can
/// assert the mirror.</para>
/// </summary>
/// <param name="Format">The paper size the writers size to.</param>
/// <param name="LayerName">The layer's name.</param>
/// <param name="Kind">Which fabrication layer this is.</param>
/// <param name="ViewSide">The board side this layer documents.</param>
/// <param name="Mirrored">True for a bottom-side layer (viewed from the bottom).</param>
/// <param name="Scale">Sheet mm per board mm (printed in the title block).</param>
/// <param name="BoardCenter">The board-bounds centre (board mm) the layer is centred on.</param>
/// <param name="ColumnCenter">The sheet point (mm) the board centre maps to.</param>
/// <param name="Segments">Every drawn line segment with its layer, in draw order.</param>
/// <param name="Texts">Every drawn text run (title block, label), in draw order.</param>
/// <param name="Outline">The drawn board outline as a sheet-space polyline (closed by index).</param>
/// <param name="Regions">The layer's OWN copper / mask / paste region objects (empty for a silk plot) —
/// the copper model's own geometry, so a test can assert the plot IS the layer, not a re-derivation.</param>
/// <param name="Strokes">The layer's OWN silk stroke polylines in board coordinates (empty for a copper
/// / mask / paste plot).</param>
public sealed record PcbLayerPlotDrawing(
    SheetFormat Format,
    string LayerName,
    PcbPlotLayerKind Kind,
    CopperSide ViewSide,
    bool Mirrored,
    double Scale,
    Vector2d BoardCenter,
    Vector2d ColumnCenter,
    IReadOnlyList<(Vector2d A, Vector2d B, string Layer)> Segments,
    IReadOnlyList<SheetText> Texts,
    IReadOnlyList<Vector2d> Outline,
    IReadOnlyList<CurvedRegion2d> Regions,
    IReadOnlyList<IReadOnlyList<Vector2d>> Strokes)
{
    /// <summary>Maps a board point onto the sheet — the same transform the drawn geometry was placed by,
    /// mirrored for a bottom-side layer, so <c>Project(p)</c> is exactly where p was drawn.</summary>
    public Vector2d Project(in Vector2d boardPoint)
    {
        var d = boardPoint - BoardCenter;
        if (Mirrored)
            d = new Vector2d(-d.X, d.Y);
        return ColumnCenter + d * Scale;
    }

    /// <summary>The total drawn geometry AREA — the sum of the layer's own region areas (the region's
    /// own value, not a re-derivation); zero for a silk plot (line-work has no area). The copper model's
    /// same regions have the same total, which is the "the plot IS that layer's geometry" oracle.</summary>
    public double PlottedArea
    {
        get
        {
            double a = 0;
            foreach (var region in Regions)
                a += region.Area;
            return a;
        }
    }

    // ------------------------------------------------------------------- writers

    /// <summary>The plot as an SVG document sized to its paper.</summary>
    public string ToSvg()
    {
        var svg = new SvgDrawing { Sheet = (Format.Width, Format.Height) };
        foreach (var group in Segments.GroupBy(s => s.Layer))
            svg.AddSegments(group.Select(s => (s.A, s.B)), LineClassOf(group.Key), group.Key);
        foreach (var text in Texts)
            svg.AddText(text.Position, text.Text, text.Height, text.Anchor, text.Layer);
        return svg.ToSvg();
    }

    /// <summary>Writes the plot's SVG to a file.</summary>
    public void SaveSvg(string path) => File.WriteAllText(path, ToSvg());

    /// <summary>The plot as a DXF document (millimetres, y up — the same coordinates the SVG carries),
    /// so the DXF and SVG cannot disagree.</summary>
    public DxfDocument ToDxf()
    {
        var dxf = new DxfDocument();
        foreach (var (a, b, layer) in Segments)
            dxf.Add(new DxfLine(a, b, layer));
        foreach (var text in Texts)
            dxf.Add(new DxfText(text.Position, text.Text, text.Height, text.Anchor, text.Layer));
        return dxf;
    }

    /// <summary>Writes the plot's DXF to a file.</summary>
    public void SaveDxf(string path) => ToDxf().SaveFile(path);

    /// <summary>The plot as a PDF file — over the same primitives the SVG writer consumes, so the two
    /// cannot disagree about what the plot looks like.</summary>
    public byte[] ToPdf()
    {
        var pdf = new PdfDrawing { Sheet = (Format.Width, Format.Height) };
        foreach (var group in Segments.GroupBy(s => s.Layer))
            pdf.AddSegments(group.Select(s => (s.A, s.B)), LineClassOf(group.Key));
        foreach (var text in Texts)
            pdf.AddText(text.Position, text.Text, text.Height, text.Anchor);
        return pdf.ToPdf();
    }

    /// <summary>Writes the plot's PDF to a file.</summary>
    public void SavePdf(string path) => File.WriteAllBytes(path, ToPdf());

    /// <summary>The line class (pen) each layer draws with: the layer geometry and the board outline
    /// solid, everything else (border, title block, label) thin. Read by all three writers so they
    /// cannot drift.</summary>
    private static SvgLineClass LineClassOf(string layer) => layer switch
    {
        FabricationPlotLayers.Copper or FabricationPlotLayers.Mask or FabricationPlotLayers.Silk
            or FabricationPlotLayers.Paste or FabricationLayers.Outline => SvgLineClass.Visible,
        _ => SvgLineClass.Thin,
    };
}
