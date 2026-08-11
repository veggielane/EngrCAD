using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Ecad;

/// <summary>One copper layer's fabrication Gerber — its layer name and the RS-274X text.</summary>
/// <param name="Layer">The copper layer name (<c>"Top"</c>, <c>"Bottom"</c>).</param>
/// <param name="Gerber">The RS-274X Gerber for that layer.</param>
public readonly record struct GerberLayerFile(string Layer, string Gerber);

/// <summary>
/// The complete fabrication output for a board, as text (no disk touched) — one Gerber per copper
/// layer, the board-outline Gerber, and the Excellon drill program, all sharing one coordinate
/// <see cref="Format"/>. <see cref="PcbGerberExport.Write"/> writes these to files; tests round-trip
/// the strings directly.
/// </summary>
/// <param name="Name">The board/design name used to name the files.</param>
/// <param name="CopperLayers">One Gerber per copper layer, in stackup order.</param>
/// <param name="OutlineGerber">The board-outline (edge-cuts) Gerber.</param>
/// <param name="Drill">The Excellon NC-drill program (all holes).</param>
/// <param name="DrillHitCount">How many holes the drill program carries.</param>
/// <param name="Format">The shared coordinate format.</param>
public sealed record FabricationOutput(
    string Name,
    IReadOnlyList<GerberLayerFile> CopperLayers,
    string OutlineGerber,
    string Drill,
    int DrillHitCount,
    GerberFormat Format);

/// <summary>What <see cref="PcbGerberExport.Write"/> wrote to disk.</summary>
/// <param name="Directory">The directory the files were written to.</param>
/// <param name="Files">The full paths written, in order (copper Gerbers, outline, drill).</param>
/// <param name="CopperLayerCount">How many copper-layer Gerbers were written.</param>
/// <param name="DrillHitCount">How many holes the drill program carries.</param>
public sealed record GerberExportResult(
    string Directory,
    IReadOnlyList<string> Files,
    int CopperLayerCount,
    int DrillHitCount)
{
    /// <summary>A human-readable summary.</summary>
    public override string ToString() =>
        $"wrote {CopperLayerCount} copper Gerber(s) + outline + {DrillHitCount} drill hits "
        + $"({Files.Count} files) to {Directory}";
}

/// <summary>
/// Gerber (RS-274X) + Excellon fabrication export — the fab output that makes a routed board
/// manufacturable, the immediate follow-on to the autorouter. It reads a routed <see cref="PcbLayout"/>
/// (or a raw <see cref="PcbCopperModel"/>) and produces one copper Gerber per layer, a board-outline
/// Gerber, and an Excellon drill program.
///
/// <para><b>The oracle is the twin-decoder round trip</b> (the repo's rule — the geometry must survive
/// the round trip, not merely a structural validator pass): the copper written can be
/// <see cref="GerberReader">parsed back</see> and the recovered copper equals the copper model's on
/// each layer to the region-area grade, and the <see cref="ExcellonReader">decoded drill hits</see>
/// equal the board's holes exactly. See the ECAD fabrication tests.</para>
///
/// <para><b>What it does not do (each filed):</b> solder-mask, silkscreen and paste/stencil layers
/// (the board carries no mask/silk model yet), Gerber X2 attributes and the job file, and a Gerber
/// IMPORT of a foreign board (this is EXPORT — the reader is the round-trip oracle scoped to what the
/// writer emits). Plated and non-plated holes are not split (the copper model does not distinguish
/// them at the drill), so all holes ride in one drill program.</para>
/// </summary>
public static class PcbGerberExport
{
    /// <summary>Generates the fabrication set (as text) for a routed layout — copper as pad flashes,
    /// trace draws and via annuli, plus the outline and drill.</summary>
    public static FabricationOutput Generate(PcbLayout layout, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var model = PcbCopperModel.FromLayout(layout);
        var format = FormatFor(model, layout.Traces);
        string design = ResolveName(name, layout.Schematic.Name);

        var viaSources = new HashSet<string>(model.Vias.Select(v => v.Source), StringComparer.Ordinal);
        var traceSources = new HashSet<string>(model.TraceSources, StringComparer.Ordinal);

        var layers = new List<GerberLayerFile>();
        foreach (var name2 in model.Layers)
        {
            var features = model.Copper.Where(f =>
                f.Layer == name2 && !viaSources.Contains(f.Source) && !traceSources.Contains(f.Source));
            var vias = model.Vias.Where(v => v.Layers.Contains(name2));
            var traces = layout.Traces
                .Where(t => t.Layer == name2)
                .Select(t => ((IReadOnlyList<Vector2d>)t.Points, t.Width));
            layers.Add(new GerberLayerFile(
                name2, GerberWriter.CopperLayer(name2, features, vias, traces, LayerHoles(model, name2), format)));
        }

        return Build(design, model, layers, format);
    }

    /// <summary>Generates the fabrication set (as text) for a raw copper model — every copper feature
    /// is classified into a flash or a region fill (a copper pour and a trace stroke both region-fill,
    /// since a model carries no trace centre-lines to draw), plus the outline and drill. This is the
    /// path for a hand-built model with copper pours the layout does not yet carry.</summary>
    public static FabricationOutput Generate(PcbCopperModel model, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        var format = FormatFor(model, []);
        string design = ResolveName(name, "board");
        var viaSources = new HashSet<string>(model.Vias.Select(v => v.Source), StringComparer.Ordinal);

        var layers = new List<GerberLayerFile>();
        foreach (var name2 in model.Layers)
        {
            var features = model.Copper.Where(f => f.Layer == name2 && !viaSources.Contains(f.Source));
            var vias = model.Vias.Where(v => v.Layers.Contains(name2));
            layers.Add(new GerberLayerFile(
                name2, GerberWriter.CopperLayer(name2, features, vias, [], LayerHoles(model, name2), format)));
        }

        return Build(design, model, layers, format);
    }

    private static FabricationOutput Build(
        string design, PcbCopperModel model, IReadOnlyList<GerberLayerFile> layers, GerberFormat format)
    {
        var outline = GerberWriter.Outline(model.Board.OutlinePoints, format);
        var hits = model.Drills.Select(d => new DrillHit(d.Center, d.Diameter)).ToList();
        var drill = ExcellonWriter.Write(hits, format);
        return new FabricationOutput(design, layers, outline, drill, hits.Count, format);
    }

    /// <summary>Writes the fabrication set for a routed layout to files under <paramref name="directory"/>
    /// (created if needed) — one <c>&lt;name&gt;-&lt;Layer&gt;.gbr</c> per copper layer, a
    /// <c>&lt;name&gt;-Edge_Cuts.gbr</c> outline, and a <c>&lt;name&gt;.drl</c> drill program — and
    /// reports what it wrote.</summary>
    public static GerberExportResult Write(PcbLayout layout, string directory, string? name = null) =>
        WriteOutput(Generate(layout, name), directory);

    /// <summary>Writes the fabrication set for a raw copper model to files (see the layout overload).</summary>
    public static GerberExportResult Write(PcbCopperModel model, string directory, string? name = null) =>
        WriteOutput(Generate(model, name), directory);

    private static GerberExportResult WriteOutput(FabricationOutput output, string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        Directory.CreateDirectory(directory);
        var files = new List<string>();

        foreach (var layer in output.CopperLayers)
        {
            string path = Path.Combine(directory, $"{output.Name}-{Sanitize(layer.Layer)}.gbr");
            File.WriteAllText(path, layer.Gerber);
            files.Add(path);
        }
        string outlinePath = Path.Combine(directory, $"{output.Name}-Edge_Cuts.gbr");
        File.WriteAllText(outlinePath, output.OutlineGerber);
        files.Add(outlinePath);

        string drillPath = Path.Combine(directory, $"{output.Name}.drl");
        File.WriteAllText(drillPath, output.Drill);
        files.Add(drillPath);

        return new GerberExportResult(directory, files, output.CopperLayers.Count, output.DrillHitCount);
    }

    /// <summary>The TRUE AIR of a layer's final copper UNION — the pockets the Gerber must CLEAR
    /// (exposed via drills, mounting holes, pour anti-pads). A drill covered by a pad or trace has no
    /// hole in the union, so it is not cleared and stays solid. A pour's clearance hole that contains
    /// an OTHER-net pad is only air AROUND that pad — a RING — so the copper (the pad island) is
    /// subtracted from each hole, and the ring is returned carrying the pad as its own hole (the writer
    /// clears the ring and re-darkens the island). For a board with no pours (nothing nests) this is
    /// exactly the union's holes, so a non-poured board's Gerber is byte-identical.</summary>
    private static IReadOnlyList<CurvedRegion2d> LayerHoles(PcbCopperModel model, string layer)
    {
        var copper = model.Copper.Where(f => f.Layer == layer).Select(f => f.Region).ToList();
        if (copper.Count == 0)
            return [];
        var union = CurvedRegion2dBoolean.UnionAll([.. copper]);
        var holeRegions = new List<CurvedRegion2d>();
        foreach (var region in union)
            foreach (var hole in region.Holes)
                holeRegions.Add(new CurvedRegion2d([.. hole]));
        if (holeRegions.Count == 0)
            return [];
        // Copper islands inside a hole (an other-net pad in a pour's anti-pad) are copper, not air —
        // subtract the copper, leaving the true air (a plain disc for a drill, a ring for an anti-pad).
        return CurvedRegion2dBoolean.Difference(holeRegions, union);
    }

    // ---- the shared coordinate format ----

    private static GerberFormat FormatFor(PcbCopperModel model, IReadOnlyList<PcbTrace> traces) =>
        GerberFormat.For(Magnitudes(model, traces));

    private static IEnumerable<double> Magnitudes(PcbCopperModel model, IReadOnlyList<PcbTrace> traces)
    {
        foreach (var p in model.Board.OutlinePoints)
        {
            yield return p.X;
            yield return p.Y;
        }
        foreach (var f in model.Copper)
        {
            var b = f.Region.Bounds;
            yield return b.Min.X;
            yield return b.Max.X;
            yield return b.Min.Y;
            yield return b.Max.Y;
        }
        foreach (var d in model.Drills)
        {
            yield return d.Center.X;
            yield return d.Center.Y;
            yield return d.Diameter;
        }
        foreach (var v in model.Vias)
        {
            yield return v.Center.X;
            yield return v.Center.Y;
            yield return v.PadDiameter;
        }
        foreach (var t in traces)
        {
            foreach (var p in t.Points)
            {
                yield return p.X;
                yield return p.Y;
            }
            yield return t.Width;
        }
    }

    private static string ResolveName(string? name, string fallback)
    {
        string n = string.IsNullOrWhiteSpace(name) ? fallback : name;
        n = Sanitize(n);
        return n.Length == 0 ? "board" : n;
    }

    private static string Sanitize(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_').ToArray();
        return new string(chars);
    }
}
