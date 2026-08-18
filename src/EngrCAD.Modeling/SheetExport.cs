using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// What a sheet's PDF carries beyond its line work. <b>Every member is off or absent by
/// default</b>, and an all-default value writes the file <see cref="SheetWriter.ToPdf"/>
/// has always written byte for byte — which is the property that makes each of these
/// safe to reach for rather than a decision about all previous output.
/// </summary>
public sealed record PdfSheetOptions
{
    /// <summary>All defaults: the file exactly as it was before any of this existed.</summary>
    public static PdfSheetOptions Default { get; } = new();

    /// <summary>
    /// Put each line class on its own toggleable PDF layer (an optional-content group),
    /// named with the SAME <see cref="SheetLayers"/> names the SVG groups and the DXF
    /// layer table use — so hidden detail, hatch, dimensions and the title block can be
    /// switched off in a reader exactly as they can in a drafting package.
    /// </summary>
    public bool Layers { get; init; }

    /// <summary>
    /// Flate-compress the streams. Off by default because an uncompressed ASCII drawing
    /// is diffable; worth it for a very large sheet. See <see cref="PdfDrawing.Compress"/>
    /// for what it does to the byte fixed point (nothing, at a fixed runtime).
    /// </summary>
    public bool Compress { get; init; }

    /// <summary>
    /// The font to letter with; null (the default) is the built-in Helvetica.
    /// <see cref="PdfFont.Embed"/> carries a real TrueType font as a subset instead,
    /// which is what lets a hole callout's depth, counterbore and countersink symbols —
    /// none of them in WinAnsi — reach the paper.
    /// </summary>
    public PdfFont? Font { get; init; }
}

// Writing a sheet out. All three writers (SVG, DXF, PDF) consume
// DrawingSheet.Compute()'s SheetContent and nothing else, so they cannot disagree about
// what a drawing looks like — they differ only in how a polyline, a dash pattern and a
// piece of text are spelled.

/// <summary>
/// SVG, DXF and PDF output for a <see cref="DrawingSheet"/>.
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
        // A multi-line note travels as ONE MTEXT — its lines share a SheetNoteBlock by
        // reference, and the first line of each block carries the whole note (the SVG
        // and PDF writers keep drawing the stacked lines; the block is a semantic
        // grouping over the same geometry, so the one-Compute invariant holds).
        var emitted = new HashSet<SheetNoteBlock>();
        foreach (var text in content.Texts)
        {
            if (text.Block is { } block)
            {
                if (emitted.Add(block))
                    dxf.Add(new DxfMText(
                        block.Insertion, block.Text, text.Height, block.Attachment, text.Layer));
                continue;
            }
            dxf.Add(new DxfText(text.Position, text.Text, text.Height, text.Anchor, text.Layer));
        }
        return dxf;
    }

    /// <summary>Writes the sheet's DXF to a file.</summary>
    public static void SaveDxf(this DrawingSheet sheet, string path)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        sheet.ToDxf().SaveFile(path);
    }

    /// <summary>
    /// The sheet as a PDF file — the deliverable format, over the SAME
    /// <see cref="DrawingSheet.Compute"/> content the SVG and DXF writers consume, so
    /// the three cannot disagree about what a drawing looks like. Line classes keep
    /// their SVG pens (one pen table), the page is the sheet's paper in points, and the
    /// content stream stays in millimetres behind one mm-to-point transform (see
    /// <see cref="PdfDrawing"/> for the whole design, including why there is no y-flip
    /// here and how text is carried).
    /// <para>Passing no <paramref name="options"/> — or an all-default one — writes
    /// exactly the file this method has always written, byte for byte; every setting on
    /// <see cref="PdfSheetOptions"/> is opt-in for that reason.</para>
    /// </summary>
    public static byte[] ToPdf(this DrawingSheet sheet, PdfSheetOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        options ??= PdfSheetOptions.Default;
        var content = sheet.Compute();
        var pdf = new PdfDrawing
        {
            Sheet = (content.Format.Width, content.Format.Height),
            Compress = options.Compress,
            Font = options.Font ?? PdfFont.Helvetica,
        };

        // A layer reaches the PDF as an optional-content group; with Layers off every
        // one of these is null and the content stream is what it always was. The names
        // are the SAME SheetLayers the SVG groups and the DXF layer table use, so a
        // reader's layer panel says what a drafting package's does.
        string? Layer(string name) => options.Layers ? name : null;

        foreach (var run in content.Runs)
        {
            var (lineClass, layer) = ClassOf(run);
            pdf.AddPolyline(run.Points, closed: false, lineClass, layer: Layer(layer));
        }
        pdf.AddSegments(content.Hatch, layer: Layer(SheetLayers.Hatch));
        // One pass in document order rather than grouped by layer: with Layers off every
        // segment lands in the one null-layer group exactly as it always did, so the
        // byte-identity claim needs no second code path to keep in step.
        foreach (var (a, b, layer) in content.Lines)
            pdf.AddSegments([(a, b)], layer: Layer(layer));
        foreach (var text in content.Texts)
            pdf.AddText(text.Position, text.Text, text.Height, text.Anchor, Layer(text.Layer));
        return pdf.ToPdf();
    }

    /// <summary>Writes the sheet's PDF to a file.</summary>
    public static void SavePdf(this DrawingSheet sheet, string path, PdfSheetOptions? options = null) =>
        File.WriteAllBytes(path, sheet.ToPdf(options));

    /// <summary>One rule mapping a classified run onto a line class and a layer, read by
    /// all three writers so they cannot drift.</summary>
    private static (SvgLineClass Class, string Layer) ClassOf(HiddenLineRun run) =>
        run.Visibility == EdgeVisibility.Hidden
            ? (SvgLineClass.Hidden, SheetLayers.Hidden)
            : run.Source == EdgeSource.Cut
                ? (SvgLineClass.Visible, SheetLayers.Section)
                : (SvgLineClass.Visible, SheetLayers.Visible);
}
