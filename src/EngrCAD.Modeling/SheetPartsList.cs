using System.Globalization;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

// BOM-linked balloons and the parts list they index into.
//
// SheetBalloon already drew a circled string and Bom already numbered the distinct parts;
// what was missing was the LINK, and the link is two facts. One: the item number is the
// BOM's own line index, read by both the balloon and the table, so a drawing cannot label
// a part with a number its own parts list does not carry. Two: a balloon's leader must
// land on a VISIBLE point of the line work of the occurrence it names -- which is why
// HiddenLineRun carries the instance it came from. A balloon pointing at a hidden edge, or
// at the neighbour that happens to be nearer, is the failure mode this exists to prevent.

/// <summary>Where a parts list grows from, and therefore which way its rows are numbered.</summary>
public enum PartsListOrder
{
    /// <summary>Item 1 at the BOTTOM, above the title block, growing upward — the ISO 7573
    /// convention, so the list reads out of the title block it sits on.</summary>
    BottomUp,

    /// <summary>Item 1 at the TOP, growing downward.</summary>
    TopDown,
}

/// <summary>
/// The parts list drawn above a sheet's title block: one row per distinct part, numbered
/// exactly as <see cref="BomBalloons"/> numbers its balloons.
///
/// <para>The item number is the <see cref="Bom"/>'s own line index plus one, so the table
/// and the balloons read ONE source and cannot disagree. Everything else is layout.</para>
/// </summary>
public sealed class SheetPartsList
{
    /// <summary>Builds a parts list over a bill of materials.</summary>
    public SheetPartsList(Bom bom)
    {
        Bom = bom ?? throw new ArgumentNullException(nameof(bom));
    }

    /// <summary>The bill of materials the rows and the balloon numbers both come from.</summary>
    public Bom Bom { get; }

    /// <summary>Row height, sheet mm.</summary>
    public double RowHeight { get; set; } = 7;

    /// <summary>Table width, sheet mm; 0 takes the title block's own width so the two line
    /// up, which is what makes the list read as part of the block.</summary>
    public double Width { get; set; }

    /// <summary>Text height in the table, sheet mm.</summary>
    public double TextHeight { get; set; } = SheetLettering.TextHeight;

    /// <summary>Which way the rows grow.</summary>
    public PartsListOrder Order { get; set; } = PartsListOrder.BottomUp;

    /// <summary>Draw the QTY / ITEM / MATERIAL captions above (or below) the rows.</summary>
    public bool Header { get; set; } = true;

    /// <summary>The item number for a BOM line index — the ONE rule the table and the
    /// balloons share.</summary>
    public static string ItemNumber(int lineIndex) =>
        (lineIndex + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>The number this list gives a part, or null when the part is not on it.</summary>
    public string? NumberOf(Part part)
    {
        for (int i = 0; i < Bom.Lines.Count; i++)
        {
            if (ReferenceEquals(Bom.Lines[i].Part, part))
                return ItemNumber(i);
        }
        return null;
    }

    /// <summary>Emits the table into the sheet's own line work and text.</summary>
    internal void Build(
        DrawingSheet sheet, List<(Vector2d A, Vector2d B, string Layer)> lines, List<SheetText> texts)
    {
        var border = sheet.Border;
        double width = Width > 0 ? Width : Math.Min(sheet.Title.Width, border.Size.X);
        double right = border.Max.X;
        double left = right - width;
        double baseY = border.Min.Y + sheet.Title.Height;
        int rows = Bom.Lines.Count + (Header ? 1 : 0);
        if (rows == 0)
            return;

        // Columns: item number, quantity, name, material. The last takes what is left, so
        // a long part name has room on a wide sheet and the table never leaves the block.
        double numberW = width * 0.10;
        double quantityW = width * 0.10;
        double materialW = width * 0.25;
        double pad = SheetLettering.TitleBlockPadding;
        double top = baseY + rows * RowHeight;

        // The frame and its rules.
        FrameRectangle(lines, left, baseY, right, top);
        for (int r = 1; r < rows; r++)
        {
            double y = baseY + r * RowHeight;
            lines.Add((new Vector2d(left, y), new Vector2d(right, y), SheetLayers.TitleBlock));
        }
        foreach (double x in new[] { left + numberW, left + numberW + quantityW, right - materialW })
            lines.Add((new Vector2d(x, baseY), new Vector2d(x, top), SheetLayers.TitleBlock));

        for (int i = 0; i < Bom.Lines.Count; i++)
        {
            var line = Bom.Lines[i];
            int slot = Order == PartsListOrder.BottomUp ? i : Bom.Lines.Count - 1 - i;
            double y = baseY + slot * RowHeight + (RowHeight - TextHeight) / 2;
            Cell(left + pad, y, ItemNumber(i));
            Cell(left + numberW + pad, y, line.Quantity.ToString(CultureInfo.InvariantCulture));
            Cell(left + numberW + quantityW + pad, y, line.Item);
            if (line.Material is { } material)
                Cell(right - materialW + pad, y, material.Name);
        }

        if (Header)
        {
            double y = baseY + Bom.Lines.Count * RowHeight + (RowHeight - TextHeight) / 2;
            Cell(left + pad, y, "NO");
            Cell(left + numberW + pad, y, "QTY");
            Cell(left + numberW + quantityW + pad, y, "ITEM");
            Cell(right - materialW + pad, y, "MATERIAL");
        }

        void Cell(double x, double y, string text) =>
            texts.Add(new SheetText(
                new Vector2d(x, y), text, TextHeight, SheetTextAnchor.Left, SheetLayers.TitleBlock));
    }

    private static void FrameRectangle(
        List<(Vector2d A, Vector2d B, string Layer)> lines,
        double left, double bottom, double right, double top)
    {
        var bl = new Vector2d(left, bottom);
        var br = new Vector2d(right, bottom);
        var tr = new Vector2d(right, top);
        var tl = new Vector2d(left, top);
        lines.Add((bl, br, SheetLayers.TitleBlock));
        lines.Add((br, tr, SheetLayers.TitleBlock));
        lines.Add((tr, tl, SheetLayers.TitleBlock));
        lines.Add((tl, bl, SheetLayers.TitleBlock));
    }
}

/// <summary>One balloon a <see cref="BomBalloons"/> pass placed, and what it labels.</summary>
/// <param name="Item">The item number, the same one the parts list prints.</param>
/// <param name="Instance">The occurrence path whose line work the leader touches.</param>
/// <param name="Anchor">The anchor in the view's projected MODEL coordinates.</param>
/// <param name="Balloon">The annotation added to the view.</param>
public sealed record PlacedBalloon(
    string Item, string Instance, Vector2d Anchor, SheetBalloon Balloon);

/// <summary>
/// Attaches one <see cref="SheetBalloon"/> per BOM line to a view, numbered by the list and
/// anchored on the line work of the occurrence it names.
///
/// <para><b>The anchor is a VISIBLE point of that instance's OWN runs.</b> A hidden-line
/// projection knows which instance produced each run (<see cref="HiddenLineRun.Instance"/>),
/// so the balloon points at the part it labels rather than at whatever is nearest; a
/// balloon on a dashed edge would be pointing through the material at something the reader
/// cannot see, which is why hidden runs are never chosen.</para>
///
/// <para>Which point is a DECISION rather than a search: the extreme point along the
/// leader's own direction, so the leader always runs outward and away from the part, with
/// ties broken by run order — deterministic, and the same answer every time the sheet is
/// computed.</para>
/// </summary>
public static class BomBalloons
{
    /// <summary>Default leader, sheet mm: up and to the right at 45 degrees.</summary>
    public static Vector2d DefaultLeader { get; } = new Vector2d(14, 14);

    /// <summary>
    /// Balloons every line of <paramref name="list"/> that has visible line work in
    /// <paramref name="view"/>, adds them to the view, and returns what it placed. A line
    /// with no visible run in this view gets no balloon (there is nothing to point at) and
    /// is simply absent from the result.
    /// </summary>
    public static IReadOnlyList<PlacedBalloon> Attach(
        DrawingView view, SheetPartsList list, Vector2d? leader = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(list);
        var direction = leader ?? DefaultLeader;
        if (!direction.TryNormalize(Tolerance.Default, out var outward))
            throw new ArgumentOutOfRangeException(nameof(leader), "A balloon's leader must be non-zero.");

        var placed = new List<PlacedBalloon>();
        var runs = view.Content.Runs;
        for (int i = 0; i < list.Bom.Lines.Count; i++)
        {
            var line = list.Bom.Lines[i];
            if (!TryAnchor(runs, line.Paths, outward, out var anchor, out string? path))
                continue;
            string item = SheetPartsList.ItemNumber(i);
            var balloon = view.Annotate(new SheetBalloon(anchor, direction, item));
            placed.Add(new PlacedBalloon(item, path!, anchor, balloon));
        }
        return placed;
    }

    /// <summary>
    /// The anchor for one BOM line: the point of the line's own VISIBLE runs that reaches
    /// furthest along <paramref name="outward"/>. Returns false when the line has no visible
    /// line work in this view.
    /// </summary>
    public static bool TryAnchor(
        IReadOnlyList<HiddenLineRun> runs, IReadOnlyList<string> paths, in Vector2d outward,
        out Vector2d anchor, out string? instance)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(paths);
        anchor = Vector2d.Zero;
        instance = null;
        double best = double.NegativeInfinity;
        foreach (var run in runs)
        {
            if (run.Visibility != EdgeVisibility.Visible || run.Instance is not { } path)
                continue;
            if (!paths.Contains(path, StringComparer.Ordinal))
                continue;
            foreach (var p in run.Points)
            {
                double reach = p.Dot(outward);
                // Strictly greater: the FIRST run in discovery order wins a tie, so the
                // answer never depends on how the runs happen to be ordered downstream.
                if (reach > best)
                {
                    best = reach;
                    anchor = p;
                    instance = path;
                }
            }
        }
        return instance is not null;
    }
}
