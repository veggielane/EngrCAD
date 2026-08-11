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
/// <para><b>Scope, stated:</b> the zone COUNT is derived from a nominal field size
/// (<see cref="NominalZone"/>) rather than transcribed from ISO 5457's per-size table; the
/// mechanism — a numbered/lettered grid with centring marks — is faithful, the exact per-sheet
/// zone counts are filed as a follow-up.</para>
/// </summary>
public sealed record FrameStandards
{
    /// <summary>Draw the ISO 5457 zone grid (column numbers, row letters, dividing lines).</summary>
    public bool ZoneGrid { get; init; }

    /// <summary>Draw the ISO 5457 centring marks at the middle of each side.</summary>
    public bool CentringMarks { get; init; }

    /// <summary>Nominal zone field size, mm — ISO 5457 recommends 25–75 mm; the column and row
    /// counts round the border's width and height to this.</summary>
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

    /// <summary>Column count across the border for a given width (ISO 5457's field grid).</summary>
    public int Columns(double borderWidth) => Math.Max(1, (int)Math.Round(borderWidth / NominalZone));

    /// <summary>Row count down the border for a given height.</summary>
    public int Rows(double borderHeight) => Math.Max(1, (int)Math.Round(borderHeight / NominalZone));

    private void AddZoneGrid(
        List<(Vector2d A, Vector2d B, string Layer)> lines, List<SheetText> texts,
        in Aabb border, in Aabb paper, string layer)
    {
        double left = border.Min.X, right = border.Max.X, bottom = border.Min.Y, top = border.Max.Y;
        int cols = Columns(border.Size.X), rows = Rows(border.Size.Y);
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
