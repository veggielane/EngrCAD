using System.Globalization;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

// The drawing FRAME — the furniture every sheet the kernel emits shares: the paper, the
// border, and a title block. It was extracted from two places that each carried their own
// copy of it — the mechanical DrawingSheet (Drawings.cs) and the ECAD SchematicSheet
// (EngrCAD.Ecad/SchematicSheet.cs) — so a drawing and a schematic of one project can no
// longer look inconsistent or drift. The extraction is ADDITIVE: the frame is parameterised
// enough that each sheet reproduces its own current border and title block byte-for-byte,
// and it is ONE pure function of its parameters, so the two sheets provably cannot disagree.

/// <summary>
/// A drawing sheet's FURNITURE — paper size, border and title block — as one value that both
/// the mechanical <see cref="DrawingSheet"/> and the ECAD <c>SchematicSheet</c> configure and
/// consume.
///
/// <para><b>It is ONE pure function of its parameters.</b> <see cref="Compute"/> returns the
/// border and title-block geometry, and two sheets built with the same <see cref="Format"/>,
/// the same <see cref="Title"/> fields and the same style (the same <see cref="Layout"/> and
/// layer names) produce byte-identical furniture because they call one function — that is the
/// whole point, and the extraction's oracle.</para>
///
/// <para><b>The two title blocks differ TODAY, and the frame carries both parameterisations
/// rather than unifying their appearance.</b> The mechanical block is a three-band engineering
/// layout (<see cref="EngineeringTitleBlock"/>) on the <see cref="SheetLayers"/> layers with
/// its own lettering; the schematic block is a two-band layout
/// (<see cref="SchematicTitleBlock"/>) on the ECAD schematic layers. So each sheet passes the
/// <see cref="Layout"/>, the layer names and the fields that reproduce its OWN look exactly —
/// what is unified is the CODE and the value TYPE, not the default appearance.</para>
///
/// <para><b>The body is not the frame's.</b> A schematic keeps its own caller-placed line
/// work; a mechanical sheet keeps its projected views. The frame draws only the paper's
/// border and the title block, plus the opt-in <see cref="Standards"/> furniture.</para>
/// </summary>
public sealed record DrawingFrame
{
    /// <summary>The paper size, in millimetres.</summary>
    public required SheetFormat Format { get; init; }

    /// <summary>Distance from the paper edge to the border, mm.</summary>
    public double Margin { get; init; } = 10;

    /// <summary>The title-block field values (title, drawing number, author, …).</summary>
    public TitleBlock Title { get; init; } = new();

    /// <summary>Layer name the border rectangle (and any <see cref="Standards"/> furniture)
    /// writes to. Different sheets use different layer vocabularies, which is why it is a
    /// parameter rather than a constant.</summary>
    public string BorderLayer { get; init; } = SheetLayers.Border;

    /// <summary>Layer name the title block writes to.</summary>
    public string TitleBlockLayer { get; init; } = SheetLayers.TitleBlock;

    /// <summary>How the title block is laid out — the one place the mechanical and schematic
    /// frames genuinely diverge. Defaults to the engineering three-band layout.</summary>
    public TitleBlockLayout Layout { get; init; } = EngineeringTitleBlock.Default;

    /// <summary>Opt-in sheet-standard furniture (ISO 5457 zone grid and centring marks).
    /// Default <see cref="FrameStandards.None"/> adds nothing, so the frame is byte-identical
    /// to a sheet that predates it.</summary>
    public FrameStandards Standards { get; init; } = FrameStandards.None;

    /// <summary>
    /// The projection angle the sheet's views are laid out in, for
    /// <see cref="FrameStandards.ProjectionSymbol"/> to draw. Null (the default) means the
    /// frame does not know — a schematic has no projection at all — and no symbol is drawn
    /// whatever the standards say.
    /// </summary>
    public ProjectionAngle? Projection { get; init; }

    /// <summary>The border rectangle, sheet mm (bottom-left, top-right).</summary>
    public Aabb Border => new(
        new Vector3d(Margin, Margin, 0),
        new Vector3d(Format.Width - Margin, Format.Height - Margin, 0));

    /// <summary>The area a body may occupy: inside the border, above the title block.</summary>
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
    /// The frame's line work and text, in sheet millimetres — the border, any opt-in standard
    /// furniture, and the title block's frame, rules and fields. The ORDER is deliberate and
    /// load-bearing (border rectangle, then standards, then the title-block rectangle, then its
    /// rules and text), because the two sheets both merge this list ahead of their own body and
    /// the writers group it by layer.
    /// </summary>
    public FrameGeometry Compute()
    {
        var lines = new List<(Vector2d A, Vector2d B, string Layer)>();
        var texts = new List<SheetText>();

        var border = Border;
        AddRectangle(lines, border, BorderLayer);

        // ISO 5457 zone grid and centring marks — opt-in, so the default path adds nothing
        // between the border and the title block and existing output stays byte-identical.
        var paper = new Aabb(new Vector3d(0, 0, 0), new Vector3d(Format.Width, Format.Height, 0));
        Standards.Apply(lines, texts, border, paper, BorderLayer);

        double blockWidth = Math.Min(Title.Width, border.Size.X);
        var block = new Aabb(
            new Vector3d(border.Max.X - blockWidth, border.Min.Y, 0),
            new Vector3d(border.Max.X, border.Min.Y + Title.Height, 0));
        AddRectangle(lines, block, TitleBlockLayer);
        Layout.Build(block, Title, TitleBlockLayer, lines, texts);

        // The ISO 128 projection symbol sits beside the title block, so it is drawn after
        // the block whose rectangle places it. Opt-in twice over (the standard must ask for
        // it AND the frame must know its angle), so nothing that predates it moves.
        if (Projection is { } angle)
            Standards.AddProjectionSymbol(lines, block, border, angle, BorderLayer);

        return new FrameGeometry(lines, texts);
    }

    /// <summary>Adds a rectangle as four segments in the order bottom, right, top, left — the
    /// order both incumbent sheets emitted, kept so their output is byte-identical.</summary>
    internal static void AddRectangle(
        List<(Vector2d A, Vector2d B, string Layer)> lines, in Aabb box, string layer)
    {
        var bl = new Vector2d(box.Min.X, box.Min.Y);
        var br = new Vector2d(box.Max.X, box.Min.Y);
        var tr = new Vector2d(box.Max.X, box.Max.Y);
        var tl = new Vector2d(box.Min.X, box.Max.Y);
        lines.Add((bl, br, layer));
        lines.Add((br, tr, layer));
        lines.Add((tr, tl, layer));
        lines.Add((tl, bl, layer));
    }
}

/// <summary>The frame's drawable primitives, in sheet millimetres.</summary>
/// <param name="Lines">Border, standards and title-block segments, each with a layer.</param>
/// <param name="Texts">Title-block (and zone-label) text.</param>
public sealed record FrameGeometry(
    IReadOnlyList<(Vector2d A, Vector2d B, string Layer)> Lines,
    IReadOnlyList<SheetText> Texts);

/// <summary>
/// How a <see cref="DrawingFrame"/>'s title block is laid out. A strategy rather than a
/// declarative record because the mechanical and schematic blocks diverge substantially
/// (three bands versus two, different field sets, different lettering) and the safest way to
/// keep each byte-identical to its incumbent is to transcribe its own arithmetic.
/// </summary>
public abstract class TitleBlockLayout
{
    /// <summary>Emits the title block into <paramref name="lines"/> and
    /// <paramref name="texts"/> given the block rectangle, the field values and the layer the
    /// block draws on. The block rectangle itself is drawn by the frame; this adds the interior
    /// rules and the text.</summary>
    internal abstract void Build(
        in Aabb block, TitleBlock title, string layer,
        List<(Vector2d A, Vector2d B, string Layer)> lines, List<SheetText> texts);

    /// <summary>The classic engineering three-band block at 1:1, third angle — a sensible
    /// default for a bare frame (the mechanical sheet supplies its own live scale and angle).</summary>
    public static TitleBlockLayout Engineering => EngineeringTitleBlock.Default;
}

/// <summary>
/// The mechanical three-band engineering title block: a title/company band over a
/// DWG/MATERIAL/FINISH row over a DRAWN/DATE/REV row, with the drawing scale and projection
/// angle printed right-aligned. Lettering comes from <see cref="SheetLettering"/> so a sheet's
/// furniture and its dimensions share one convention.
/// </summary>
public sealed class EngineeringTitleBlock : TitleBlockLayout
{
    private readonly string _scale;
    private readonly ProjectionAngle _projection;

    /// <summary>Builds the layout with the scale text (e.g. "1:2") and projection angle it
    /// prints — the sheet's own facts, never the caller's to mistype.</summary>
    public EngineeringTitleBlock(string scale, ProjectionAngle projection)
    {
        _scale = scale ?? throw new ArgumentNullException(nameof(scale));
        _projection = projection;
    }

    /// <summary>A 1:1, third-angle default for a frame built outside a sheet.</summary>
    public static EngineeringTitleBlock Default { get; } = new("1:1", ProjectionAngle.Third);

    internal override void Build(
        in Aabb block, TitleBlock title, string layer,
        List<(Vector2d A, Vector2d B, string Layer)> lines, List<SheetText> texts)
    {
        double pad = SheetLettering.TitleBlockPadding;
        double x = block.Min.X + pad;
        double right = block.Max.X - pad;
        double top = block.Max.Y;
        double h = block.Size.Y;

        // Two horizontal rules split the block into a title band and two data rows.
        double rule1 = block.Min.Y + h * 2 / 3;
        double rule2 = block.Min.Y + h / 3;
        lines.Add((new Vector2d(block.Min.X, rule1), new Vector2d(block.Max.X, rule1), layer));
        lines.Add((new Vector2d(block.Min.X, rule2), new Vector2d(block.Max.X, rule2), layer));

        texts.Add(new SheetText(
            new Vector2d(x, rule1 + (top - rule1 - SheetLettering.TitleHeight) / 2),
            title.Title, SheetLettering.TitleHeight, SheetTextAnchor.Left, layer));
        if (title.Company.Length > 0)
            texts.Add(new SheetText(
                new Vector2d(right, rule1 + (top - rule1 - SheetLettering.TextHeight) / 2),
                title.Company, SheetLettering.TextHeight, SheetTextAnchor.Right, layer));

        double row1 = rule2 + (rule1 - rule2 - SheetLettering.TextHeight) / 2;
        double row2 = block.Min.Y + (rule2 - block.Min.Y - SheetLettering.TextHeight) / 2;
        double column = (right - x) / 3;

        AddField(x, row1, "DWG", title.DrawingNumber);
        AddField(x + column, row1, "MATERIAL", title.Material);
        AddField(x + 2 * column, row1, "FINISH", title.Finish);
        AddField(x, row2, "DRAWN", title.Author);
        AddField(x + column, row2, "DATE", title.Date);
        AddField(x + 2 * column, row2, "REV", title.Revision);

        // The scale and projection angle are the sheet's own facts, never the caller's to
        // mistype: they are read from the layout that produced the views.
        texts.Add(new SheetText(
            new Vector2d(right, row2), $"SCALE {_scale}", SheetLettering.TextHeight, SheetTextAnchor.Right, layer));
        texts.Add(new SheetText(
            new Vector2d(right, row1),
            _projection == ProjectionAngle.Third ? "THIRD ANGLE" : "FIRST ANGLE",
            SheetLettering.TextHeight, SheetTextAnchor.Right, layer));

        void AddField(double fx, double fy, string label, string value)
        {
            texts.Add(new SheetText(
                new Vector2d(fx, fy + SheetLettering.TextHeight * 1.1), label,
                SheetLettering.SmallTextHeight, SheetTextAnchor.Left, layer));
            if (value.Length > 0)
                texts.Add(new SheetText(
                    new Vector2d(fx, fy), value, SheetLettering.TextHeight, SheetTextAnchor.Left, layer));
        }
    }
}

/// <summary>
/// The ECAD schematic two-band title block: a title/company band over a single
/// DWG/DRAWN/DATE/REV row, and no scale or projection angle (a schematic is not a scaled
/// projection). Lettering is scaled to one text height, so a schematic's furniture matches its
/// symbol and label text.
/// </summary>
public sealed class SchematicTitleBlock : TitleBlockLayout
{
    private readonly double _textHeight;

    /// <summary>Builds the layout with the field text height (the schematic uses its own,
    /// smaller than the mechanical sheet's).</summary>
    public SchematicTitleBlock(double textHeight) => _textHeight = textHeight;

    /// <summary>The schematic default (2 mm field text) for a frame built outside a sheet.</summary>
    public static SchematicTitleBlock Default { get; } = new(2.0);

    internal override void Build(
        in Aabb block, TitleBlock title, string layer,
        List<(Vector2d A, Vector2d B, string Layer)> lines, List<SheetText> texts)
    {
        // A schematic title block is simpler than a mechanical one: one rule, no scale and no
        // projection angle. blockH is the field height rather than block.Size.Y so the
        // arithmetic matches the schematic sheet's incumbent bit for bit.
        double blockH = title.Height;
        double rule = block.Min.Y + blockH * 0.55;
        lines.Add((new Vector2d(block.Min.X, rule), new Vector2d(block.Max.X, rule), layer));

        double pad = 2.5;
        double titleHeight = _textHeight * 1.4;
        texts.Add(new SheetText(
            new Vector2d(block.Min.X + pad, rule + (block.Max.Y - rule - titleHeight) / 2),
            title.Title, titleHeight, SheetTextAnchor.Left, layer));
        if (title.Company.Length > 0)
            texts.Add(new SheetText(
                new Vector2d(block.Max.X - pad, rule + (block.Max.Y - rule - _textHeight) / 2),
                title.Company, _textHeight, SheetTextAnchor.Right, layer));

        // Bottom row: DWG / DRAWN / DATE / REV, evenly across the block.
        double row = block.Min.Y + (rule - block.Min.Y - _textHeight) / 2;
        double x = block.Min.X + pad;
        double column = (block.Max.X - pad - x) / 4;
        Field(x, row, "DWG", title.DrawingNumber);
        Field(x + column, row, "DRAWN", title.Author);
        Field(x + 2 * column, row, "DATE", title.Date);
        Field(x + 3 * column, row, "REV", title.Revision);

        void Field(double fx, double fy, string label, string value)
        {
            texts.Add(new SheetText(
                new Vector2d(fx, fy + _textHeight * 1.15), label, _textHeight * 0.72,
                SheetTextAnchor.Left, layer));
            if (value.Length > 0)
                texts.Add(new SheetText(
                    new Vector2d(fx, fy), value, _textHeight, SheetTextAnchor.Left, layer));
        }
    }
}

/// <summary>
/// Opt-in sheet-standard furniture a <see cref="DrawingFrame"/> draws in its margin band: the
/// ISO 5457 zone grid (row letters down the sides, column numbers across) and centring marks.
///
/// <para>Everything here is OFF by default (<see cref="None"/>), so a frame that says nothing
/// draws exactly what it always did. When on, the furniture lives in the margin between the
/// border and the paper edge, so it never touches the drawing area, and it is emitted on the
/// frame's <c>BorderLayer</c>.</para>
///
/// <para><b>Where the zone COUNT comes from.</b> ISO 5457 fixes a count per sheet size, and
/// <see cref="Iso5457Zones"/> transcribes that small table; a sheet whose paper matches an
/// A-series size takes the standard's own count, and any other paper falls back to rounding the
/// border to a nominal field size (<see cref="NominalZone"/>). The fallback is not a lesser
/// answer for a custom sheet — it IS what the standard says a field should be, 25–75 mm — but
/// for a standard sheet the table is the standard's word rather than our arithmetic.</para>
/// </summary>
public sealed record FrameStandards
{
    /// <summary>Draw the ISO 5457 zone grid (column numbers, row letters, dividing lines).</summary>
    public bool ZoneGrid { get; init; }

    /// <summary>Draw the ISO 5457 centring marks at the middle of each side.</summary>
    public bool CentringMarks { get; init; }

    /// <summary>
    /// Draw the ISO 128 projection symbol — the truncated cone in two views — beside the title
    /// block. Needs <see cref="DrawingFrame.Projection"/> to be set as well, since the symbol
    /// states the sheet's own projection angle and a frame that does not know it must not guess.
    /// </summary>
    public bool ProjectionSymbol { get; init; }

    /// <summary>Height of the projection symbol, mm — the frustum's LARGE diameter, which is
    /// what the symbol's overall height is.</summary>
    public double SymbolHeight { get; init; } = 9;

    /// <summary>Nominal zone field size, mm — ISO 5457 recommends 25–75 mm; the column and row
    /// counts round the border's width and height to this when the paper matches no size in
    /// <see cref="Iso5457Zones"/>.</summary>
    public double NominalZone { get; init; } = 50;

    /// <summary>Cap height of the zone labels, mm.</summary>
    public double LabelHeight { get; init; } = 3.5;

    /// <summary>How far a centring mark reaches past the border into the drawing area, mm
    /// (ISO 5457 draws it crossing the frame).</summary>
    public double CentringMarkReach { get; init; } = 5;

    /// <summary>Nothing — the default, so a frame stays byte-identical to one predating it.</summary>
    public static FrameStandards None { get; } = new();

    /// <summary>The ISO 5457 furniture: zone grid and centring marks.</summary>
    public static FrameStandards Iso5457 { get; } = new() { ZoneGrid = true, CentringMarks = true };

    /// <summary>Adds whatever furniture is switched on. A no-op for <see cref="None"/>.</summary>
    internal void Apply(
        List<(Vector2d A, Vector2d B, string Layer)> lines, List<SheetText> texts,
        in Aabb border, in Aabb paper, string layer)
    {
        if (ZoneGrid)
            AddZoneGrid(lines, texts, border, paper, layer);
        if (CentringMarks)
            AddCentringMarks(lines, border, paper, layer);
    }

    /// <summary>Column count across the border for a given width, from the NOMINAL field size —
    /// the fallback for a paper ISO 5457 does not tabulate (see <see cref="ZonesFor"/>).</summary>
    public int Columns(double borderWidth) => Math.Max(1, (int)Math.Round(borderWidth / NominalZone));

    /// <summary>Row count down the border for a given height, from the nominal field size.</summary>
    public int Rows(double borderHeight) => Math.Max(1, (int)Math.Round(borderHeight / NominalZone));

    /// <summary>
    /// The zone counts a sheet of this paper gets: ISO 5457's own tabulated pair when the paper
    /// is one of the sizes the standard covers (in either orientation), else the nominal-field
    /// rounding of the border. The one rule the grid reads, so a caller can ask what a sheet
    /// will get without drawing it.
    /// </summary>
    public (int Columns, int Rows) ZonesFor(
        double paperWidth, double paperHeight, double borderWidth, double borderHeight)
    {
        if (Iso5457Zones.TryFor(paperWidth, paperHeight, out int along, out int across))
            return paperWidth >= paperHeight ? (along, across) : (across, along);
        return (Columns(borderWidth), Rows(borderHeight));
    }

    private void AddZoneGrid(
        List<(Vector2d A, Vector2d B, string Layer)> lines, List<SheetText> texts,
        in Aabb border, in Aabb paper, string layer)
    {
        double left = border.Min.X, right = border.Max.X, bottom = border.Min.Y, top = border.Max.Y;
        var (cols, rows) = ZonesFor(paper.Size.X, paper.Size.Y, border.Size.X, border.Size.Y);
        double cellW = border.Size.X / cols, cellH = border.Size.Y / rows;

        // Dividing lines across the top and bottom margin bands (per column) and the left and
        // right margin bands (per row) — the boundaries between zones.
        for (int i = 1; i < cols; i++)
        {
            double cx = left + i * cellW;
            lines.Add((new Vector2d(cx, top), new Vector2d(cx, paper.Max.Y), layer));
            lines.Add((new Vector2d(cx, bottom), new Vector2d(cx, paper.Min.Y), layer));
        }
        for (int j = 1; j < rows; j++)
        {
            double cy = bottom + j * cellH;
            lines.Add((new Vector2d(left, cy), new Vector2d(paper.Min.X, cy), layer));
            lines.Add((new Vector2d(right, cy), new Vector2d(paper.Max.X, cy), layer));
        }

        // Numbers 1..cols across (left to right), in both the top and bottom bands.
        double topBand = (top + paper.Max.Y) / 2 - LabelHeight / 2;
        double bottomBand = (bottom + paper.Min.Y) / 2 - LabelHeight / 2;
        for (int i = 0; i < cols; i++)
        {
            double cx = left + (i + 0.5) * cellW;
            string number = (i + 1).ToString(CultureInfo.InvariantCulture);
            texts.Add(new SheetText(new Vector2d(cx, topBand), number, LabelHeight, SheetTextAnchor.Center, layer));
            texts.Add(new SheetText(new Vector2d(cx, bottomBand), number, LabelHeight, SheetTextAnchor.Center, layer));
        }

        // Letters A.. down (top to bottom), in both the left and right bands.
        double leftBand = (left + paper.Min.X) / 2;
        double rightBand = (right + paper.Max.X) / 2;
        for (int j = 0; j < rows; j++)
        {
            double cy = bottom + (j + 0.5) * cellH - LabelHeight / 2;
            string letter = ZoneLetter(rows - 1 - j);   // A at the TOP row
            texts.Add(new SheetText(new Vector2d(leftBand, cy), letter, LabelHeight, SheetTextAnchor.Center, layer));
            texts.Add(new SheetText(new Vector2d(rightBand, cy), letter, LabelHeight, SheetTextAnchor.Center, layer));
        }
    }

    private void AddCentringMarks(
        List<(Vector2d A, Vector2d B, string Layer)> lines, in Aabb border, in Aabb paper, string layer)
    {
        double cx = (border.Min.X + border.Max.X) / 2;
        double cy = (border.Min.Y + border.Max.Y) / 2;
        double reach = CentringMarkReach;
        // Each mark runs from the paper edge across the border and a short reach into the sheet.
        lines.Add((new Vector2d(cx, paper.Max.Y), new Vector2d(cx, border.Max.Y - reach), layer));
        lines.Add((new Vector2d(cx, paper.Min.Y), new Vector2d(cx, border.Min.Y + reach), layer));
        lines.Add((new Vector2d(paper.Min.X, cy), new Vector2d(border.Min.X + reach, cy), layer));
        lines.Add((new Vector2d(paper.Max.X, cy), new Vector2d(border.Max.X - reach, cy), layer));
    }

    /// <summary>
    /// The ISO 128 projection symbol, DERIVED rather than transcribed: it is a truncated cone
    /// drawn in two views, so it is built by projecting one.
    ///
    /// <para>Put the frustum's axis along the sheet's x with its SMALL end to the left. The
    /// side view is then the trapezoid, and the other view is the one looking along the axis at
    /// that small end — i.e. a view from the LEFT. The sheet's own projection rule places a view
    /// from the left on the left in THIRD angle and on the right in FIRST angle, so the pair of
    /// concentric circles swaps sides while the trapezoid does not. That is the whole content of
    /// the symbol, and reading it off the layout rule rather than off a picture is what stops the
    /// symbol and the layout disagreeing.</para>
    /// </summary>
    internal void AddProjectionSymbol(
        List<(Vector2d A, Vector2d B, string Layer)> lines,
        in Aabb block, in Aabb border, ProjectionAngle projection, string layer)
    {
        if (!ProjectionSymbol || !(SymbolHeight > 0))
            return;

        double large = SymbolHeight;             // the frustum's large diameter
        double small = large / 2;                // and its small one, the classic 1:2 symbol
        double length = large * 1.5;             // axial length of the frustum
        double gap = large * 0.5;                // between the two views
        double width = length + gap + large;
        double pad = SheetLettering.TitleBlockPadding;

        double rightEdge = block.Min.X - pad;
        double x0 = Math.Max(border.Min.X + pad, rightEdge - width);
        double cy = block.Min.Y + block.Size.Y / 2;

        // Third angle: the view from the left goes on the LEFT. First angle mirrors it.
        bool circlesLeft = projection == ProjectionAngle.Third;
        double circleCx = circlesLeft ? x0 + large / 2 : x0 + width - large / 2;
        double coneLeft = circlesLeft ? x0 + large + gap : x0;

        AddCircle(lines, new Vector2d(circleCx, cy), large / 2, layer);
        AddCircle(lines, new Vector2d(circleCx, cy), small / 2, layer);

        // The trapezoid: small end left, large end right, whichever side it sits on.
        var a = new Vector2d(coneLeft, cy - small / 2);
        var b = new Vector2d(coneLeft, cy + small / 2);
        var c = new Vector2d(coneLeft + length, cy + large / 2);
        var d = new Vector2d(coneLeft + length, cy - large / 2);
        lines.Add((a, b, layer));
        lines.Add((b, c, layer));
        lines.Add((c, d, layer));
        lines.Add((d, a, layer));

        // One axis line through both views, the centre line the symbol is drawn about.
        lines.Add((new Vector2d(x0 - pad / 2, cy), new Vector2d(x0 + width + pad / 2, cy), layer));
    }

    /// <summary>The symbol's circles as chorded polygons (the writers speak segments).</summary>
    private static void AddCircle(
        List<(Vector2d A, Vector2d B, string Layer)> lines, in Vector2d centre, double radius, string layer)
    {
        const int segments = 48;
        var previous = centre + new Vector2d(radius, 0);
        for (int i = 1; i <= segments; i++)
        {
            double angle = 2 * Math.PI * i / segments;
            var point = centre + new Vector2d(Math.Cos(angle), Math.Sin(angle)) * radius;
            lines.Add((previous, point, layer));
            previous = point;
        }
    }

    /// <summary>A zone row letter (ISO 5457 omits I and O to avoid confusion with 1 and 0, and
    /// doubles a letter — AA, BB — past 24 rows).</summary>
    private static string ZoneLetter(int index)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ";   // 24 letters, no I or O
        int rep = index / alphabet.Length + 1;
        char c = alphabet[index % alphabet.Length];
        return new string(c, rep);
    }
}
/// <summary>
/// ISO 5457's own per-size zone counts: how many fields the grid reference system divides a
/// standard sheet into, along its long side and across its short one.
///
/// <para><b>&#x26A0; Transcribed &#x2014; verify against the datasheet</b> (ISO 5457:1999,
/// the grid reference system), the <c>StandardHoles</c>/<c>SheetMaterials</c> convention. The
/// figures are stated here in the standard's own terms &#x2014; a count per size, long side
/// first &#x2014; so a reader can check them against the table rather than against arithmetic.
/// What DOES check itself is that every row lands inside the standard's own 25&#x2013;75 mm
/// field-size window once the border is taken off the paper, which is the property the counts
/// exist to give and which a mistyped row would break.</para>
///
/// <para>The match is on the PAPER's own dimensions rather than on a format NAME, so a sheet
/// turned <see cref="SheetFormat.Portrait"/> and a custom format that happens to be A3 both get
/// A3's counts &#x2014; the counts are a property of the paper, and a name is not.</para>
/// </summary>
public static class Iso5457Zones
{
    // (long side mm, short side mm, divisions ALONG the long side, divisions across the short).
    private static readonly (double Long, double Short, int Along, int Across)[] Table =
    [
        (1189, 841, 24, 16),   // A0
        (841, 594, 16, 12),    // A1
        (594, 420, 12, 8),     // A2
        (420, 297, 8, 6),      // A3
        (297, 210, 6, 4),      // A4
    ];

    /// <summary>Every tabulated size, as (long mm, short mm, along, across) &#x2014; the
    /// transcription itself, so a test can read the rows rather than restate them.</summary>
    public static IReadOnlyList<(double Long, double Short, int Along, int Across)> Rows => Table;

    /// <summary>
    /// The counts for a paper of these dimensions in either orientation, or false when ISO 5457
    /// does not tabulate it. <paramref name="along"/> is the count on the LONG side.
    /// </summary>
    public static bool TryFor(double width, double height, out int along, out int across)
    {
        double longSide = Math.Max(width, height);
        double shortSide = Math.Min(width, height);
        foreach (var row in Table)
        {
            // The tabulated sizes are whole millimetres and a SheetFormat states them exactly,
            // so this is an exact-semantic match rather than a tolerance: a paper either is one
            // of the standard's sizes or it is a custom sheet the nominal path serves.
            if (row.Long == longSide && row.Short == shortSide)
            {
                along = row.Along;
                across = row.Across;
                return true;
            }
        }
        along = across = 0;
        return false;
    }
}

/// <summary>
/// One ISO 7200 data field: the caption a title block prints for it and whether the standard
/// makes it mandatory.
/// </summary>
/// <param name="Caption">The caption as the block prints it.</param>
/// <param name="Mandatory">True for the standard's mandatory data fields.</param>
public sealed record Iso7200Field(string Caption, bool Mandatory);

/// <summary>
/// The ISO 7200 title block: the standard's own data fields, laid out in three bands.
///
/// <para><b>&#x26A0; The FIELD LIST is transcribed &#x2014; verify against the datasheet</b>
/// (ISO 7200:2004, data fields in title blocks), the <c>StandardHoles</c> convention. It is
/// published as <see cref="Fields"/> so the transcription is the thing a test reads, and the
/// four MANDATORY fields &#x2014; legal owner, identification number, date of issue and sheet
/// number &#x2014; are marked as such, because "which fields must be present" is the one part of
/// the standard a layout can silently get wrong.</para>
///
/// <para>Every field maps onto a <see cref="TitleBlock"/> member; a field whose value is empty
/// prints its caption and nothing else, which is what a blank form does and is honest about
/// what the drawing has not said. Two fields exist only for this layout
/// (<see cref="TitleBlock.DocumentType"/> and <see cref="TitleBlock.ApprovedBy"/>) plus the
/// language code; they default to empty, so a sheet using the engineering layout is
/// byte-identical to one that predates them.</para>
/// </summary>
public sealed class Iso7200TitleBlock : TitleBlockLayout
{
    /// <summary>The transcribed field list, in the order the block prints them.</summary>
    public static IReadOnlyList<Iso7200Field> Fields { get; } =
    [
        new("LEGAL OWNER", Mandatory: true),
        new("TITLE", Mandatory: false),
        new("SUPPLEMENTARY TITLE", Mandatory: false),
        new("IDENTIFICATION NUMBER", Mandatory: true),
        new("DOC. TYPE", Mandatory: false),
        new("REV", Mandatory: false),
        new("CREATED BY", Mandatory: false),
        new("APPROVED BY", Mandatory: false),
        new("DATE OF ISSUE", Mandatory: true),
        new("LANG", Mandatory: false),
        new("SHEET", Mandatory: true),
    ];

    private readonly double _textHeight;

    /// <summary>Builds the layout at a stated field text height (ISO 3098's 3.5 mm by
    /// default, the same lettering the engineering block uses).</summary>
    public Iso7200TitleBlock(double textHeight = SheetLettering.TextHeight) => _textHeight = textHeight;

    /// <summary>The default ISO 7200 layout.</summary>
    public static Iso7200TitleBlock Default { get; } = new();

    internal override void Build(
        in Aabb block, TitleBlock title, string layer,
        List<(Vector2d A, Vector2d B, string Layer)> lines, List<SheetText> texts)
    {
        double pad = SheetLettering.TitleBlockPadding;
        double x = block.Min.X + pad;
        double right = block.Max.X - pad;
        double h = block.Size.Y;
        double rule1 = block.Min.Y + h * 0.55;   // title band above
        double rule2 = block.Min.Y + h * 0.275;  // identification band, then the people band

        lines.Add((new Vector2d(block.Min.X, rule1), new Vector2d(block.Max.X, rule1), layer));
        lines.Add((new Vector2d(block.Min.X, rule2), new Vector2d(block.Max.X, rule2), layer));

        // Band 1 -- the legal owner, the title and its supplementary title.
        double ownerY = block.Max.Y - pad - _textHeight;
        Caption(x, ownerY + _textHeight * 1.1, "LEGAL OWNER");
        Value(x, ownerY, title.Company);
        double titleY = rule1 + pad * 0.5;
        Caption(right, titleY + _textHeight * 1.1, "TITLE", right: true);
        Value(right, titleY, title.Title, right: true, height: SheetLettering.TitleHeight);
        if (title.Project.Length > 0)
            Value(x, titleY, title.Project, height: _textHeight);

        // Band 2 -- identification number, document type, revision.
        double idY = rule2 + (rule1 - rule2 - _textHeight) / 2;
        double column2 = (right - x) / 3;
        Field(x, idY, "IDENTIFICATION NUMBER", title.DrawingNumber);
        Field(x + column2, idY, "DOC. TYPE", title.DocumentType);
        Field(x + 2 * column2, idY, "REV", title.Revision);

        // Band 3 -- who made it, who signed it, when, in what language, which sheet.
        double byY = block.Min.Y + (rule2 - block.Min.Y - _textHeight) / 2;
        double column3 = (right - x) / 5;
        Field(x, byY, "CREATED BY", title.Author);
        Field(x + column3, byY, "APPROVED BY", title.ApprovedBy);
        Field(x + 2 * column3, byY, "DATE OF ISSUE", title.Date);
        Field(x + 3 * column3, byY, "LANG", title.Language);
        Field(x + 4 * column3, byY, "SHEET", title.Sheet);

        void Field(double fx, double fy, string caption, string value)
        {
            Caption(fx, fy + _textHeight * 1.1, caption);
            Value(fx, fy, value);
        }

        void Caption(double cx, double cy, string caption, bool right = false) =>
            texts.Add(new SheetText(
                new Vector2d(cx, cy), caption, SheetLettering.SmallTextHeight,
                right ? SheetTextAnchor.Right : SheetTextAnchor.Left, layer));

        void Value(double vx, double vy, string value, bool right = false, double height = 0)
        {
            if (value.Length == 0)
                return;
            texts.Add(new SheetText(
                new Vector2d(vx, vy), value, height > 0 ? height : _textHeight,
                right ? SheetTextAnchor.Right : SheetTextAnchor.Left, layer));
        }
    }
}
