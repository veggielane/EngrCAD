using System.Globalization;
using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Ecad;

/// <summary>Named line-work layers a <see cref="PcbFabricationDrawing"/> writes to, so an SVG
/// or DXF consumer can toggle whole classes (the board outline, the drill map, the tables, the
/// notes). Fixed strings rather than an enum because they cross into files, where a renamed
/// enum member would silently change a layer name — the <see cref="SheetLayers"/> rule. The
/// border and title block ride the SHARED <see cref="SheetLayers.Border"/> /
/// <see cref="SheetLayers.TitleBlock"/> so the fab frame is a mechanical drawing's frame.</summary>
public static class FabricationLayers
{
    /// <summary>The drawn board outline (the plated board shape).</summary>
    public const string Outline = "outline";

    /// <summary>Drill-symbol markers, one per drilled feature (the drill map).</summary>
    public const string Drills = "drills";

    /// <summary>The drill and stackup table line work and text.</summary>
    public const string Tables = "tables";

    /// <summary>The fabrication notes block.</summary>
    public const string Notes = "notes";
}

/// <summary>
/// The vector glyph a drill diameter is marked with on the drill map (and in the drill table's
/// symbol column). A small fixed palette — cycled by symbol index when a board carries more
/// distinct drills than the palette has shapes, the <see cref="DrillTableRow.Symbol"/> letter
/// still distinguishing them. A canonical IPC-standardised symbol set is a filed follow-up.
/// </summary>
public enum DrillGlyph
{
    /// <summary>A plus (+).</summary>
    Plus,

    /// <summary>A saltire (×).</summary>
    Cross,

    /// <summary>A square.</summary>
    Square,

    /// <summary>A circle.</summary>
    Circle,

    /// <summary>An upward triangle.</summary>
    Triangle,

    /// <summary>A diamond.</summary>
    Diamond,

    /// <summary>A six-pointed asterisk.</summary>
    Asterisk,

    /// <summary>A hexagon.</summary>
    Hexagon,

    /// <summary>A downward triangle.</summary>
    TriangleDown,

    /// <summary>A pentagon.</summary>
    Pentagon,
}

/// <summary>
/// One row of the drill table — one distinct (diameter, plating) drilled feature. The
/// <see cref="Index"/> is the stable, always-distinct identifier (sorted by diameter, then
/// plating); <see cref="Symbol"/> is its display letter (A, B, …) and <see cref="Glyph"/> its
/// map marker. <see cref="Count"/> is the closed-form oracle: the number of drilled features of
/// this size and plating, so the rows PARTITION the board's holes and vias exactly.
/// </summary>
/// <param name="Index">Zero-based, sorted ascending by diameter then plating — always distinct.</param>
/// <param name="Symbol">Display letter for this size (A for index 0, …).</param>
/// <param name="Glyph">The marker drawn at each such hole and in the table's symbol column.</param>
/// <param name="Diameter">The drill diameter, mm — the board's own value, verbatim.</param>
/// <param name="Plated">True for a plated (PTH) feature, false for a non-plated (NPTH) one.</param>
/// <param name="Count">How many drilled features have this diameter and plating.</param>
public readonly record struct DrillTableRow(
    int Index, char Symbol, DrillGlyph Glyph, double Diameter, bool Plated, int Count);

/// <summary>
/// One drilled feature as a mark on the drill map: the row it belongs to
/// (<see cref="Index"/>/<see cref="Symbol"/>/<see cref="Glyph"/>), its own board location
/// (<see cref="BoardLocation"/>) and where that lands on the sheet (<see cref="SheetLocation"/>,
/// exactly <c>drawing.Project(BoardLocation)</c>). There is exactly one mark per drilled
/// feature — the map cannot omit a hole nor invent one.
/// </summary>
/// <param name="Index">The drill-table row index this feature belongs to.</param>
/// <param name="Symbol">The row's display letter.</param>
/// <param name="Glyph">The row's marker glyph, drawn at <see cref="SheetLocation"/>.</param>
/// <param name="BoardLocation">The feature's centre in board coordinates, mm.</param>
/// <param name="SheetLocation">Where the mark is drawn on the sheet, mm.</param>
public readonly record struct DrillMark(
    int Index, char Symbol, DrillGlyph Glyph, Vector2d BoardLocation, Vector2d SheetLocation);

/// <summary>
/// One row of the layer-stackup table — one physical <see cref="StackLayer"/> of a multilayer
/// board (a copper or dielectric layer, top-most first). A copper-only board carries no
/// physical stackup, so its table is empty and a note states the layer count instead.
/// </summary>
/// <param name="Name">The layer's name (<c>"Top"</c>, <c>"Core"</c>, …).</param>
/// <param name="Type">Copper or dielectric, as text.</param>
/// <param name="Thickness">The layer thickness, mm — the board's own value.</param>
public readonly record struct StackupRow(string Name, string Type, double Thickness);

/// <summary>
/// The FABRICATION DRAWING SHEET for a <see cref="PcbLayout"/> — the drawing a board house
/// reads alongside the Gerbers: the board outline at a fitted scale, a DRILL MAP with a symbol
/// at every drilled feature, a DRILL TABLE grouping the board's holes and vias by size, a LAYER
/// STACKUP table, and a fabrication NOTES block, on the SHARED engineering frame.
///
/// <para><b>It reads the board; it never edits it.</b> Everything derives from the layout's own
/// public read surface (its board outline, holes, placed vias and stackup), so the drawing
/// cannot disagree with the board it documents — the ECAD one-declaration rule, applied to a
/// drawing.</para>
///
/// <para><b>The frame is the THIRD consumer of the shared <see cref="DrawingFrame"/></b> (after
/// the mechanical <see cref="DrawingSheet"/> and the ECAD <c>SchematicSheet</c>): a fab drawing
/// is an engineering drawing, so it uses the same three-band <see cref="EngineeringTitleBlock"/>
/// on the same <see cref="SheetLayers"/> — which is why <see cref="Frame"/> given the same paper
/// and title-block fields as a mechanical drawing produces byte-identical furniture. That is the
/// payoff of one shared frame, and it is asserted directly.</para>
/// </summary>
public sealed class PcbFabricationSheet
{
    /// <summary>The default paper: A3 landscape (a fab drawing needs room for a map and tables).</summary>
    public static SheetFormat DefaultFormat => SheetFormat.A3;

    /// <summary>Distance from the paper edge to the border, mm — the mechanical
    /// <see cref="DrawingSheet"/>'s default, so a matched frame is byte-identical.</summary>
    public const double DefaultMargin = 10;

    // Layout constants (sheet mm). The board map takes the left of the drawing area, the tables
    // and notes the right; everything else follows from the paper size.
    private const double BoardColumnFraction = 0.58;   // share of the drawing-area width for the map
    private const double ColumnGap = 10;               // gap between the map column and the tables
    private const double BoardPad = 8;                  // inset of the board within its column

    /// <summary>Builds a fab drawing sheet. A null title defaults its title to the schematic's
    /// name; a null format uses <see cref="DefaultFormat"/>.</summary>
    /// <param name="layout">The board layout to document — read only.</param>
    /// <param name="format">The paper size (default <see cref="DefaultFormat"/>).</param>
    /// <param name="title">Title-block fields (default: title = the schematic's name).</param>
    /// <param name="standards">Opt-in ISO 5457 frame furniture (default none).</param>
    public PcbFabricationSheet(
        PcbLayout layout,
        SheetFormat? format = null,
        TitleBlock? title = null,
        FrameStandards? standards = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Layout = layout;
        Format = format ?? DefaultFormat;
        Title = title ?? new TitleBlock { Title = layout.Schematic.Name };
        Standards = standards ?? FrameStandards.None;
    }

    /// <summary>The layout this documents (read only).</summary>
    public PcbLayout Layout { get; }

    /// <summary>The paper.</summary>
    public SheetFormat Format { get; }

    /// <summary>Distance from the paper edge to the border, mm.</summary>
    public double Margin { get; init; } = DefaultMargin;

    /// <summary>The projection angle printed in the title block. A PCB fab drawing is not an
    /// orthographic projection, but the shared engineering title block always names one, so it is
    /// carried (default third angle, the mechanical default) rather than a fab-only title block
    /// that would forfeit the shared-frame byte-identity.</summary>
    public ProjectionAngle Projection { get; init; } = ProjectionAngle.Third;

    /// <summary>The title-block field values (title, drawing number, author, …).</summary>
    public TitleBlock Title { get; }

    /// <summary>Opt-in sheet-standard furniture (ISO 5457 zone grid and centring marks); default
    /// <see cref="FrameStandards.None"/> leaves the output byte-identical.</summary>
    public FrameStandards Standards { get; }

    /// <summary>The board-to-sheet scale (sheet mm per board mm) the outline is drawn at — the
    /// largest standard ISO 5455 ratio that fits the map column. Printed in the title block.</summary>
    public double Scale => ComputeScale();

    /// <summary>
    /// The shared <see cref="DrawingFrame"/> this sheet draws its border and title block from —
    /// the SAME frame value the mechanical <see cref="DrawingSheet"/> uses, on the SAME
    /// <see cref="SheetLayers"/> with the SAME three-band <see cref="EngineeringTitleBlock"/>. So
    /// a fab drawing and a mechanical drawing of one board, at the same paper and fields, draw
    /// byte-identical furniture — one function, so they cannot disagree.
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

    /// <summary>Renders the drawing — the deterministic function of the layout and the paper.</summary>
    public PcbFabricationDrawing Compute() => new Builder(this).Build();

    // The drawing area (inside the border, above the title block) is a function of the paper and
    // the title-block height only — never of the title-block LAYOUT — so a probe frame carrying a
    // placeholder layout reads it, which is what lets Frame() ask for the scale it prints.
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
        double columnWidth = area.Size.X * BoardColumnFraction;
        double columnHeight = area.Size.Y;
        var (bmin, bmax) = BoardBounds(Layout.Board);
        double boardWidth = bmax.X - bmin.X;
        double boardHeight = bmax.Y - bmin.Y;
        if (boardWidth <= 0 || boardHeight <= 0)
            return 1;
        double maxRatio = Math.Min(
            (columnWidth - 2 * BoardPad) / boardWidth,
            (columnHeight - 2 * BoardPad) / boardHeight);
        return DrawingScales.Fit(maxRatio);
    }

    internal static (Vector2d Min, Vector2d Max) BoardBounds(PcbBoard board)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var p in board.OutlinePoints)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }
        return (new Vector2d(minX, minY), new Vector2d(maxX, maxY));
    }

    // -------------------------------------------------------------------- the builder
    // Separate object so Compute() holds no mutable state and stays a pure function.
    private sealed class Builder
    {
        // Marker and text sizing, sheet mm.
        private const double DrillMarkRadius = 1.4;
        private const double TableTextHeight = 2.5;
        private const double TableTitleHeight = 3.5;
        private const double RowHeight = 5.0;
        private const double NoteTextHeight = 2.5;
        private const double SectionGap = 8.0;

        private readonly PcbFabricationSheet _sheet;
        private readonly PcbLayout _layout;
        private readonly PcbBoard _board;

        private readonly List<(Vector2d A, Vector2d B, string Layer)> _segments = [];
        private readonly List<SheetText> _texts = [];

        internal Builder(PcbFabricationSheet sheet)
        {
            _sheet = sheet;
            _layout = sheet.Layout;
            _board = sheet.Layout.Board;
        }

        internal PcbFabricationDrawing Build()
        {
            // The border and title block come from the shared DrawingFrame, merged AHEAD of the
            // body as the frame's own line work always is.
            var frame = _sheet.Frame().Compute();
            foreach (var line in frame.Lines)
                _segments.Add((line.A, line.B, line.Layer));
            _texts.AddRange(frame.Texts);

            var area = _sheet.DrawingArea();
            double columnWidth = area.Size.X * BoardColumnFraction;
            var mapColumn = new Aabb(
                new Vector3d(area.Min.X, area.Min.Y, 0),
                new Vector3d(area.Min.X + columnWidth, area.Max.Y, 0));
            var tableColumn = new Aabb(
                new Vector3d(area.Min.X + columnWidth + ColumnGap, area.Min.Y, 0),
                new Vector3d(area.Max.X, area.Max.Y, 0));

            double scale = _sheet.Scale;
            var (bmin, bmax) = BoardBounds(_board);
            var boardCenter = (bmin + bmax) * 0.5;
            var columnCenter = new Vector2d(
                (mapColumn.Min.X + mapColumn.Max.X) * 0.5,
                (mapColumn.Min.Y + mapColumn.Max.Y) * 0.5);
            Vector2d Project(Vector2d p) => columnCenter + (p - boardCenter) * scale;

            var outline = DrawOutline(Project);
            var (table, marks) = DrawDrillMap(Project);
            var stackup = DrawTables(tableColumn, table);
            var notes = DrawNotes(tableColumn, table, marks, stackup);

            return new PcbFabricationDrawing(
                Format: _sheet.Format,
                Scale: scale,
                BoardCenter: boardCenter,
                ColumnCenter: columnCenter,
                Segments: _segments,
                Texts: _texts,
                Outline: outline,
                DrillTable: table,
                DrillMarks: marks,
                StackupTable: stackup,
                Notes: notes);
        }

        // ---- the board outline -------------------------------------------

        private IReadOnlyList<Vector2d> DrawOutline(Func<Vector2d, Vector2d> project)
        {
            var points = _board.OutlinePoints;
            var drawn = new List<Vector2d>(points.Count);
            foreach (var p in points)
                drawn.Add(project(p));
            for (int i = 0; i < drawn.Count; i++)
                _segments.Add((drawn[i], drawn[(i + 1) % drawn.Count], FabricationLayers.Outline));
            return drawn;
        }

        // ---- the drill map + table ----------------------------------------

        private (IReadOnlyList<DrillTableRow> Table, IReadOnlyList<DrillMark> Marks) DrawDrillMap(
            Func<Vector2d, Vector2d> project)
        {
            var features = CollectFeatures(_layout);

            // Group by (diameter, plating); an exact key, because a diameter is the board's own
            // value carried verbatim (not a computed quantity), so exact equality IS the right
            // partition and matches the board's declaration bit for bit.
            var groups = new Dictionary<(double Diameter, bool Plated), int>();
            var order = new List<(double Diameter, bool Plated)>();
            foreach (var f in features)
            {
                var key = (f.Diameter, f.Plated);
                if (groups.TryGetValue(key, out int count))
                {
                    groups[key] = count + 1;
                }
                else
                {
                    groups[key] = 1;
                    order.Add(key);
                }
            }

            // Sort ascending by diameter, then non-plated before plated at equal diameter — a
            // deterministic total order, so the symbol assignment is a function of the board.
            order.Sort((a, b) =>
                a.Diameter != b.Diameter ? a.Diameter.CompareTo(b.Diameter) : a.Plated.CompareTo(b.Plated));

            var table = new List<DrillTableRow>(order.Count);
            var index = new Dictionary<(double, bool), int>();
            for (int i = 0; i < order.Count; i++)
            {
                var key = order[i];
                index[key] = i;
                table.Add(new DrillTableRow(i, SymbolFor(i), GlyphFor(i), key.Diameter, key.Plated, groups[key]));
            }

            // One mark per drilled feature, at the feature's own location.
            var marks = new List<DrillMark>(features.Count);
            foreach (var f in features)
            {
                int i = index[(f.Diameter, f.Plated)];
                var sheetPoint = project(f.Center);
                marks.Add(new DrillMark(i, SymbolFor(i), GlyphFor(i), f.Center, sheetPoint));
                foreach (var (a, b) in GlyphSegments(GlyphFor(i), sheetPoint, DrillMarkRadius))
                    _segments.Add((a, b, FabricationLayers.Drills));
            }

            return (table, marks);
        }

        // ---- the drill and stackup tables ---------------------------------

        private IReadOnlyList<StackupRow> DrawTables(Aabb column, IReadOnlyList<DrillTableRow> table)
        {
            double x = column.Min.X;
            double y = column.Max.Y;               // a running y-cursor, top-down
            double width = column.Size.X;

            // ---- drill table ----
            _texts.Add(Text(new Vector2d(x, y - TableTitleHeight), "DRILL TABLE", TableTitleHeight));
            y -= TableTitleHeight + 3;

            double symX = x + 4;
            double diaX = x + 9;
            double typeX = x + 26;
            double qtyX = x + Math.Min(46, width - 8);
            double top = y;

            // Header labels and an underline.
            _texts.Add(Text(new Vector2d(diaX, y - TableTextHeight), "DIA (mm)", TableTextHeight));
            _texts.Add(Text(new Vector2d(typeX, y - TableTextHeight), "PLATED", TableTextHeight));
            _texts.Add(Text(new Vector2d(qtyX, y - TableTextHeight), "QTY", TableTextHeight, SheetTextAnchor.Right));
            y -= RowHeight;
            _segments.Add((new Vector2d(x, y + 1), new Vector2d(x + width, y + 1), FabricationLayers.Tables));

            foreach (var row in table)
            {
                double cy = y - TableTextHeight;
                foreach (var (a, b) in GlyphSegments(row.Glyph, new Vector2d(symX, y - RowHeight / 2), 1.5))
                    _segments.Add((a, b, FabricationLayers.Tables));
                _texts.Add(Text(new Vector2d(diaX, cy), Num(row.Diameter), TableTextHeight));
                _texts.Add(Text(new Vector2d(typeX, cy), row.Plated ? "PTH" : "NPTH", TableTextHeight));
                _texts.Add(Text(new Vector2d(qtyX, cy), row.Count.ToString(CultureInfo.InvariantCulture),
                    TableTextHeight, SheetTextAnchor.Right));
                y -= RowHeight;
            }
            // A light box around the whole drill table.
            Box(x, y, x + width, top + 1);
            y -= SectionGap;

            // ---- stackup table ----
            _texts.Add(Text(new Vector2d(x, y - TableTitleHeight), "LAYER STACKUP", TableTitleHeight));
            y -= TableTitleHeight + 3;

            var stackup = CollectStackup(_board);
            if (stackup.Count == 0)
            {
                // A copper-only board has no physical stackup; state the copper count instead.
                _texts.Add(Text(new Vector2d(x, y - TableTextHeight),
                    $"{_board.Stackup.Coppers.Count}-LAYER (COPPER ONLY;", TableTextHeight));
                y -= RowHeight;
                _texts.Add(Text(new Vector2d(x, y - TableTextHeight), "NO STACKUP STATED)", TableTextHeight));
                y -= RowHeight + SectionGap;
            }
            else
            {
                double nameX = x + 2;
                double typX = x + 24;
                double thkX = x + Math.Min(46, width - 8);
                double stackTop = y;
                _texts.Add(Text(new Vector2d(nameX, y - TableTextHeight), "LAYER", TableTextHeight));
                _texts.Add(Text(new Vector2d(typX, y - TableTextHeight), "TYPE", TableTextHeight));
                _texts.Add(Text(new Vector2d(thkX, y - TableTextHeight), "THK", TableTextHeight, SheetTextAnchor.Right));
                y -= RowHeight;
                _segments.Add((new Vector2d(x, y + 1), new Vector2d(x + width, y + 1), FabricationLayers.Tables));
                foreach (var s in stackup)
                {
                    double cy = y - TableTextHeight;
                    _texts.Add(Text(new Vector2d(nameX, cy), s.Name, TableTextHeight));
                    _texts.Add(Text(new Vector2d(typX, cy), s.Type, TableTextHeight));
                    _texts.Add(Text(new Vector2d(thkX, cy), Num(s.Thickness), TableTextHeight, SheetTextAnchor.Right));
                    y -= RowHeight;
                }
                Box(x, y, x + width, stackTop + 1);
                y -= SectionGap;
            }

            // Stash the cursor for the notes block (built next) via a field-free return: the notes
            // pass re-derives its own top from the same column, so it needs no shared cursor.
            _notesTop = y;
            return stackup;
        }

        private double _notesTop;

        // ---- fabrication notes --------------------------------------------

        private IReadOnlyList<string> DrawNotes(
            Aabb column, IReadOnlyList<DrillTableRow> table,
            IReadOnlyList<DrillMark> marks, IReadOnlyList<StackupRow> stackup)
        {
            var notes = CollectNotes(_layout, _board, table, marks, stackup);

            double x = column.Min.X;
            double y = _notesTop;
            _texts.Add(Text(new Vector2d(x, y - TableTitleHeight), "NOTES", TableTitleHeight));
            y -= TableTitleHeight + 3;
            for (int i = 0; i < notes.Count; i++)
            {
                _texts.Add(Text(new Vector2d(x, y - NoteTextHeight),
                    $"{i + 1}. {notes[i]}", NoteTextHeight));
                y -= NoteTextHeight + 2;
            }
            return notes;
        }

        // ---- small helpers ------------------------------------------------

        private static SheetText Text(Vector2d position, string text, double height,
            SheetTextAnchor anchor = SheetTextAnchor.Left) =>
            new(position, text, height, anchor, FabricationLayers.Tables);

        private void Box(double x0, double y0, double x1, double y1)
        {
            var bl = new Vector2d(x0, y0);
            var br = new Vector2d(x1, y0);
            var tr = new Vector2d(x1, y1);
            var tl = new Vector2d(x0, y1);
            _segments.Add((bl, br, FabricationLayers.Tables));
            _segments.Add((br, tr, FabricationLayers.Tables));
            _segments.Add((tr, tl, FabricationLayers.Tables));
            _segments.Add((tl, bl, FabricationLayers.Tables));
        }
    }

    // ---------------------------------------------------------------- drilled features

    /// <summary>The board's own drilled features — its holes (mounting and via) and the layout's
    /// placed vias — the two things a fab drill table groups. A mounting hole is non-plated
    /// (NPTH), a board via and every placed via are plated (PTH). Component through-hole pad
    /// drills are deliberately NOT included here (they belong to the Excellon program the fab
    /// derives from the copper); a filed follow-up.</summary>
    internal static List<(Vector2d Center, double Diameter, bool Plated)> CollectFeatures(PcbLayout layout)
    {
        var features = new List<(Vector2d, double, bool)>();
        foreach (var hole in layout.Board.Holes)
            features.Add((hole.Center, hole.Diameter, hole.Kind == BoardHoleKind.Via));
        foreach (var via in layout.PlacedVias())
            features.Add((via.Center, via.DrillDiameter, true));
        return features;
    }

    internal static List<StackupRow> CollectStackup(PcbBoard board)
    {
        var rows = new List<StackupRow>();
        if (board.LayerStackup is { } stackup)
            foreach (var layer in stackup.Layers)
                rows.Add(new StackupRow(
                    layer.Name,
                    layer.Kind == StackLayerKind.Copper ? "COPPER" : "DIELECTRIC",
                    layer.Thickness));
        return rows;
    }

    /// <summary>The fabrication notes, WRITE-ONLY-WHEN-STATED: a value nothing carries is omitted
    /// rather than invented. What the board DOES state geometrically — its finished thickness, its
    /// copper-layer count, its copper foil thickness (only when a physical stackup gives one), the
    /// drill summary, and any mask/silk/paste settings the layout declares — is always reported;
    /// and the layout's <see cref="PcbLayout.Fabrication"/> spec fills in the fab-package fields the
    /// geometry cannot carry (material, copper weight, surface finish, mask/silk colours, IPC-6012
    /// class, minimum trace/clearance, free-form notes), each printed only when the spec states it.
    /// A stated finished thickness OVERRIDES the modelled plate thickness in the thickness note,
    /// since the delivered finished thickness (copper + finish) is what a fabricator quotes to; when
    /// no spec states one, the note is the modelled thickness exactly as before (byte-identical).</summary>
    internal static List<string> CollectNotes(
        PcbLayout layout, PcbBoard board, IReadOnlyList<DrillTableRow> table,
        IReadOnlyList<DrillMark> marks, IReadOnlyList<StackupRow> stackup)
    {
        var fab = layout.Fabrication;
        var notes = new List<string>
        {
            "ALL DIMENSIONS IN MILLIMETRES.",
            $"FINISHED BOARD THICKNESS {Num(fab?.FinishedThicknessMm ?? board.Thickness)} mm.",
            $"{board.Stackup.Coppers.Count} COPPER LAYERS.",
        };

        // Copper foil thickness (the closest the board states to a copper weight) — only when a
        // physical stackup actually gives one.
        var copper = stackup.FirstOrDefault(s => s.Type == "COPPER");
        if (copper.Type == "COPPER")
            notes.Add($"COPPER FOIL {Num(copper.Thickness)} mm.");

        if (marks.Count > 0)
        {
            double smallest = double.PositiveInfinity;
            foreach (var row in table)
                smallest = Math.Min(smallest, row.Diameter);
            notes.Add(
                $"{marks.Count} DRILLED HOLES, {table.Count} SIZE(S); SMALLEST DIA {Num(smallest)} mm.");
        }

        if (layout.MaskSettings is { } mask)
            notes.Add(
                $"SOLDERMASK BOTH SIDES (EXPANSION {Num(mask.Expansion)} mm; VIAS {mask.Vias.ToString().ToUpperInvariant()}).");
        if (layout.SilkscreenSettings is not null)
            notes.Add("SILKSCREEN PER FABRICATION DATA.");
        if (layout.PasteSettings is not null)
            notes.Add("SOLDER PASTE STENCIL PER FABRICATION DATA.");

        // The fabrication-requirements block — each field of the spec printed only when stated (an
        // unstated field is omitted, never invented), so with no spec nothing is added and the notes
        // above are byte-identical to before. (Finished thickness is folded into the note above.)
        if (fab is not null)
        {
            if (fab.BaseMaterial is { } material)
                notes.Add($"MATERIAL: {material.ToUpperInvariant()}.");
            if (fab.CopperWeightOz is { } copperOz)
                // 1 oz/ft2 is ~34.79 um; 35 um/oz is the shop-standard rounding a fabricator reads.
                // The micro sign (U+00B5) rides as an escape so the source stays pure ASCII (the
                // Callouts.cs / PdfDrawing convention); it IS in WinAnsi, so ToPdf carries it.
                notes.Add($"COPPER WEIGHT: {Num(copperOz)} oz ({Num(copperOz * 35)} \u00B5m).");
            if (fab.SurfaceFinishLabel is { } finish)
                notes.Add($"SURFACE FINISH: {finish}.");
            if (fab.SolderMaskColour is { } maskColour)
                notes.Add($"SOLDER MASK COLOUR: {maskColour.ToUpperInvariant()}.");
            if (fab.SilkscreenColour is { } silkColour)
                notes.Add($"SILKSCREEN COLOUR: {silkColour.ToUpperInvariant()}.");
            if (fab.Ipc6012Class is { } ipcClass)
                notes.Add($"FABRICATE TO IPC-6012 CLASS {ipcClass}.");
            if (fab.MinTraceWidthMm is { } minTrace)
                notes.Add($"MINIMUM TRACE WIDTH {Num(minTrace)} mm.");
            if (fab.MinClearanceMm is { } minClearance)
                notes.Add($"MINIMUM CLEARANCE {Num(minClearance)} mm.");
            foreach (var note in fab.Notes)
                notes.Add(note);
        }

        return notes;
    }

    // ---------------------------------------------------------------- symbols and glyphs

    internal static char SymbolFor(int index) => (char)('A' + (index % 26));

    internal static DrillGlyph GlyphFor(int index)
    {
        var palette = Palette;
        return palette[index % palette.Length];
    }

    private static readonly DrillGlyph[] Palette =
    [
        DrillGlyph.Plus, DrillGlyph.Cross, DrillGlyph.Square, DrillGlyph.Circle, DrillGlyph.Triangle,
        DrillGlyph.Diamond, DrillGlyph.Asterisk, DrillGlyph.Hexagon, DrillGlyph.TriangleDown, DrillGlyph.Pentagon,
    ];

    /// <summary>The line segments of one drill glyph, centred at <paramref name="c"/> and sized to
    /// radius <paramref name="r"/> (sheet mm). Every glyph is line work, so it draws identically
    /// through the SVG, DXF and PDF writers (one Compute() feeds all three).</summary>
    internal static IReadOnlyList<(Vector2d A, Vector2d B)> GlyphSegments(DrillGlyph glyph, Vector2d c, double r)
    {
        Vector2d P(double x, double y) => c + new Vector2d(x * r, y * r);
        var segments = new List<(Vector2d, Vector2d)>();
        void Loop(params Vector2d[] pts)
        {
            for (int i = 0; i < pts.Length; i++)
                segments.Add((pts[i], pts[(i + 1) % pts.Length]));
        }
        Vector2d[] NGon(int n, double startDeg, double radius)
        {
            var pts = new Vector2d[n];
            for (int i = 0; i < n; i++)
            {
                double a = (startDeg + 360.0 * i / n) * Math.PI / 180;
                pts[i] = P(Math.Cos(a) * radius, Math.Sin(a) * radius);
            }
            return pts;
        }
        switch (glyph)
        {
            case DrillGlyph.Plus:
                segments.Add((P(-1, 0), P(1, 0)));
                segments.Add((P(0, -1), P(0, 1)));
                break;
            case DrillGlyph.Cross:
                segments.Add((P(-0.8, -0.8), P(0.8, 0.8)));
                segments.Add((P(-0.8, 0.8), P(0.8, -0.8)));
                break;
            case DrillGlyph.Square:
                Loop(P(-0.8, -0.8), P(0.8, -0.8), P(0.8, 0.8), P(-0.8, 0.8));
                break;
            case DrillGlyph.Circle:
                Loop(NGon(16, 0, 0.9));
                break;
            case DrillGlyph.Triangle:
                Loop(NGon(3, 90, 1.0));
                break;
            case DrillGlyph.Diamond:
                Loop(P(0, 1), P(1, 0), P(0, -1), P(-1, 0));
                break;
            case DrillGlyph.Asterisk:
                segments.Add((P(-1, 0), P(1, 0)));
                segments.Add((P(-0.5, -0.87), P(0.5, 0.87)));
                segments.Add((P(-0.5, 0.87), P(0.5, -0.87)));
                break;
            case DrillGlyph.Hexagon:
                Loop(NGon(6, 0, 0.95));
                break;
            case DrillGlyph.TriangleDown:
                Loop(NGon(3, -90, 1.0));
                break;
            case DrillGlyph.Pentagon:
                Loop(NGon(5, 90, 0.95));
                break;
        }
        return segments;
    }

    private static string Num(double value)
    {
        // A drawn dimension, trimmed of trailing zeros — "1.6", "0.035", "3".
        string s = value.ToString("0.####", CultureInfo.InvariantCulture);
        return s.Length == 0 ? "0" : s;
    }
}

/// <summary>
/// The computed fabrication drawing: the board outline (drawn), the drill map and drill TABLE,
/// the layer STACKUP table, the fabrication NOTES, and the SVG / DXF / PDF writers — over the
/// same primitives, so the three cannot disagree (the drawing-sheet one-Compute rule).
///
/// <para>Everything is already in SHEET coordinates (millimetres, origin at the paper's
/// bottom-left, y up — the <see cref="SvgDrawing"/> convention). <see cref="Project"/> maps a
/// board point onto the sheet, so a test can assert the drawn outline and every drill mark sit
/// exactly where the board's own geometry puts them.</para>
/// </summary>
/// <param name="Format">The paper size the writers size to.</param>
/// <param name="Scale">Sheet mm per board mm (the map's scale, printed in the title block).</param>
/// <param name="BoardCenter">The board-bounds centre (board mm) the map is centred on.</param>
/// <param name="ColumnCenter">The sheet point (mm) the board centre maps to.</param>
/// <param name="Segments">Every drawn line segment with its layer, in draw order.</param>
/// <param name="Texts">Every drawn text run (tables, notes, title block), in draw order.</param>
/// <param name="Outline">The drawn board outline as a sheet-space polyline (closed by index).</param>
/// <param name="DrillTable">The drill table — the closed-form partition of the board's drilled features.</param>
/// <param name="DrillMarks">One mark per drilled feature, at the feature's location.</param>
/// <param name="StackupTable">The physical stackup rows (empty for a copper-only board).</param>
/// <param name="Notes">The fabrication notes block.</param>
public sealed record PcbFabricationDrawing(
    SheetFormat Format,
    double Scale,
    Vector2d BoardCenter,
    Vector2d ColumnCenter,
    IReadOnlyList<(Vector2d A, Vector2d B, string Layer)> Segments,
    IReadOnlyList<SheetText> Texts,
    IReadOnlyList<Vector2d> Outline,
    IReadOnlyList<DrillTableRow> DrillTable,
    IReadOnlyList<DrillMark> DrillMarks,
    IReadOnlyList<StackupRow> StackupTable,
    IReadOnlyList<string> Notes)
{
    /// <summary>Maps a board point onto the sheet — the same transform the drawn outline and
    /// every drill mark were placed by, so <c>Project(hole.Center)</c> is exactly where its mark
    /// is drawn.</summary>
    public Vector2d Project(in Vector2d boardPoint) => ColumnCenter + (boardPoint - BoardCenter) * Scale;

    // ------------------------------------------------------------------- writers

    /// <summary>The drawing as an SVG document sized to its paper.</summary>
    public string ToSvg()
    {
        var svg = new SvgDrawing { Sheet = (Format.Width, Format.Height) };
        foreach (var group in Segments.GroupBy(s => s.Layer))
            svg.AddSegments(group.Select(s => (s.A, s.B)), LineClassOf(group.Key), group.Key);
        foreach (var text in Texts)
            svg.AddText(text.Position, text.Text, text.Height, text.Anchor, text.Layer);
        return svg.ToSvg();
    }

    /// <summary>Writes the drawing's SVG to a file.</summary>
    public void SaveSvg(string path) => File.WriteAllText(path, ToSvg());

    /// <summary>The drawing as a DXF document (millimetres, y up — the same coordinates the SVG
    /// carries). Line work is DXF LINEs (drill glyphs included — a symbol is line work, not the
    /// real hole) and text is DXF TEXT, so the DXF and SVG cannot disagree.</summary>
    public DxfDocument ToDxf()
    {
        var dxf = new DxfDocument();
        foreach (var (a, b, layer) in Segments)
            dxf.Add(new DxfLine(a, b, layer));
        foreach (var text in Texts)
            dxf.Add(new DxfText(text.Position, text.Text, text.Height, text.Anchor, text.Layer));
        return dxf;
    }

    /// <summary>Writes the drawing's DXF to a file.</summary>
    public void SaveDxf(string path) => ToDxf().SaveFile(path);

    /// <summary>The drawing as a PDF file — over the same primitives the SVG writer consumes, so
    /// the two cannot disagree about what the drawing looks like.</summary>
    public byte[] ToPdf()
    {
        var pdf = new PdfDrawing { Sheet = (Format.Width, Format.Height) };
        foreach (var group in Segments.GroupBy(s => s.Layer))
            pdf.AddSegments(group.Select(s => (s.A, s.B)), LineClassOf(group.Key));
        foreach (var text in Texts)
            pdf.AddText(text.Position, text.Text, text.Height, text.Anchor);
        return pdf.ToPdf();
    }

    /// <summary>Writes the drawing's PDF to a file.</summary>
    public void SavePdf(string path) => File.WriteAllBytes(path, ToPdf());

    /// <summary>The line class (pen) each layer draws with: the board outline and drill marks
    /// solid, everything else (tables, notes, border, title block) thin. Read by all three
    /// writers so they cannot drift.</summary>
    private static SvgLineClass LineClassOf(string layer) => layer switch
    {
        FabricationLayers.Outline or FabricationLayers.Drills => SvgLineClass.Visible,
        _ => SvgLineClass.Thin,
    };
}
