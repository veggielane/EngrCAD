using EngrCAD.Core;

namespace EngrCAD.Modeling;

// Writing a sheet out. Both writers consume DrawingSheet.Compute()'s SheetContent and
// nothing else, so they cannot disagree about what a drawing looks like — they differ
// only in how a polyline, a dash pattern and a piece of text are spelled.

/// <summary>
/// SVG and DXF output for a <see cref="DrawingSheet"/>.
///
/// <para><b>Line CLASS drives everything.</b> A drawing is only usable if visible edges
/// are solid and wide, hidden detail is narrow and dashed, cut boundaries are marked and
/// furniture is narrow and continuous — so each kind of line goes onto its own named
/// layer with its own pen (SVG) or line type (DXF), and a downstream editor can toggle
/// whole classes. That is the build123d lesson the 2D interchange layer already
/// records, applied to a whole sheet rather than to a loose profile.</para>
/// </summary>
public static class SheetWriter
{
    /// <summary>Layer-to-line-type mapping for DXF, so hidden detail arrives dashed and
    /// a cut boundary chain-dashed in any editor that reads the file.</summary>
    public static IReadOnlyDictionary<string, string> DxfLineTypeByLayer { get; } =
        new Dictionary<string, string>
        {
            [SheetLayers.Hidden] = DxfLineTypes.Hidden.Name,
            [SheetLayers.Section] = DxfLineTypes.Center.Name,
        };

    /// <summary>The sheet as an SVG document, sized to its paper.</summary>
    public static string ToSvg(this DrawingSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        var content = sheet.Compute();
        var svg = new SvgDrawing { Sheet = (content.Format.Width, content.Format.Height) };

        foreach (var run in content.Runs)
        {
            var (lineClass, layer) = ClassOf(run);
            svg.AddPolyline(run.Points, closed: false, lineClass, layer);
        }
        svg.AddSegments(content.Hatch, SvgLineClass.Thin, SheetLayers.Hatch);
        foreach (var group in content.Lines.GroupBy(l => l.Layer))
            svg.AddSegments(group.Select(l => (l.A, l.B)), SvgLineClass.Thin, group.Key);
        foreach (var text in content.Texts)
            svg.AddText(text.Position, text.Text, text.Height, text.Anchor, text.Layer);
        return svg.ToSvg();
    }

    /// <summary>Writes the sheet's SVG to a file.</summary>
    public static void SaveSvg(this DrawingSheet sheet, string path) =>
        File.WriteAllText(path, sheet.ToSvg());

    /// <summary>
    /// The sheet as a DXF document (millimetres, y up — the same coordinates the SVG
    /// carries), with a layer per line class and an LTYPE table defining every pattern
    /// those layers name.
    /// </summary>
    public static DxfDocument ToDxf(this DrawingSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        var content = sheet.Compute();
        var dxf = new DxfDocument();
        foreach (var (layer, lineType) in DxfLineTypeByLayer)
            dxf.LayerLineTypes[layer] = lineType;

        foreach (var run in content.Runs)
        {
            var (_, layer) = ClassOf(run);
            dxf.Add(new DxfPolyline([.. run.Points], closed: false, layer));
        }
        foreach (var (a, b) in content.Hatch)
            dxf.Add(new DxfLine(a, b, SheetLayers.Hatch));
        foreach (var (a, b, layer) in content.Lines)
            dxf.Add(new DxfLine(a, b, layer));
        foreach (var text in content.Texts)
            dxf.Add(new DxfText(text.Position, text.Text, text.Height, text.Anchor, text.Layer));
        return dxf;
    }

    /// <summary>Writes the sheet's DXF to a file.</summary>
    public static void SaveDxf(this DrawingSheet sheet, string path)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        sheet.ToDxf().SaveFile(path);
    }

    /// <summary>One rule mapping a classified run onto a line class and a layer, read by
    /// both writers so they cannot drift.</summary>
    private static (SvgLineClass Class, string Layer) ClassOf(HiddenLineRun run) =>
        run.Visibility == EdgeVisibility.Hidden
            ? (SvgLineClass.Hidden, SheetLayers.Hidden)
            : run.Source == EdgeSource.Cut
                ? (SvgLineClass.Visible, SheetLayers.Section)
                : (SvgLineClass.Visible, SheetLayers.Visible);
}
