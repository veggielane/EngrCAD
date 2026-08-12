using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Ecad;

/// <summary>One layer's fabrication Gerber — its layer name and the RS-274X text, plus (for a
/// step-stencil paste level) the foil-thickness token that disambiguates its filename.</summary>
/// <param name="Layer">The copper / side layer name (<c>"Top"</c>, <c>"Bottom"</c>).</param>
/// <param name="Gerber">The RS-274X Gerber for that layer.</param>
/// <param name="PasteLevelToken">For a step-stencil paste level, the foil-thickness token
/// (e.g. <c>"100um"</c>) appended to the file name so two levels of one side do not collide; <c>null</c>
/// for every other layer (copper / mask / silk / a flat single-stencil paste).</param>
public readonly record struct GerberLayerFile(string Layer, string Gerber, string? PasteLevelToken = null);

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
/// <param name="MaskLayers">One solder-mask Gerber per outer copper side (top, bottom) — the pad
/// windows imaged dark; additive, so the copper Gerbers are byte-identical whether or not the mask is
/// present.</param>
/// <param name="SilkLayers">One silkscreen Gerber per outer copper side (top, bottom) — reference /
/// value / outline line-work; empty on the raw-copper-model path (silk needs placements).</param>
/// <param name="PasteLayers">One solder-paste (stencil) Gerber per outer copper side (top, bottom) — the
/// SMD-pad apertures imaged dark; additive, so the copper Gerbers are byte-identical whether or not the
/// paste is present.</param>
public sealed record FabricationOutput(
    string Name,
    IReadOnlyList<GerberLayerFile> CopperLayers,
    string OutlineGerber,
    string Drill,
    int DrillHitCount,
    GerberFormat Format,
    IReadOnlyList<GerberLayerFile> MaskLayers,
    IReadOnlyList<GerberLayerFile> SilkLayers,
    IReadOnlyList<GerberLayerFile> PasteLayers);

/// <summary>What <see cref="PcbGerberExport.Write"/> wrote to disk.</summary>
/// <param name="Directory">The directory the files were written to.</param>
/// <param name="Files">The full paths written, in order (copper Gerbers, outline, drill).</param>
/// <param name="CopperLayerCount">How many copper-layer Gerbers were written.</param>
/// <param name="DrillHitCount">How many holes the drill program carries.</param>
/// <param name="MaskLayerCount">How many solder-mask Gerbers were written.</param>
/// <param name="SilkLayerCount">How many silkscreen Gerbers were written.</param>
/// <param name="PasteLayerCount">How many solder-paste (stencil) Gerbers were written.</param>
public sealed record GerberExportResult(
    string Directory,
    IReadOnlyList<string> Files,
    int CopperLayerCount,
    int DrillHitCount,
    int MaskLayerCount = 0,
    int SilkLayerCount = 0,
    int PasteLayerCount = 0)
{
    /// <summary>Whether an IPC-D-356A netlist (<c>&lt;name&gt;.ipc</c>) was written beside the Gerber
    /// set (only when the layout <c>Write</c> overload was asked for it). Default false — a fab package
    /// without a netlist is byte-identical to before.</summary>
    public bool NetlistWritten { get; init; }

    /// <summary>Whether a Gerber job file (<c>&lt;name&gt;.gbrjob</c>) was written beside the Gerber set.
    /// Default false.</summary>
    public bool JobFileWritten { get; init; }

    /// <summary>A human-readable summary.</summary>
    public override string ToString() =>
        $"wrote {CopperLayerCount} copper + {MaskLayerCount} mask + {SilkLayerCount} silk + "
        + $"{PasteLayerCount} paste Gerber(s) + outline + {DrillHitCount} drill hits"
        + (NetlistWritten ? " + IPC-356 netlist" : "")
        + (JobFileWritten ? " + job file" : "")
        + $" ({Files.Count} files) to {Directory}";
}

/// <summary>
/// Gerber (RS-274X) + Excellon fabrication export — the fab output that makes a routed board
/// manufacturable, the immediate follow-on to the autorouter. It reads a routed <see cref="PcbLayout"/>
/// (or a raw <see cref="PcbCopperModel"/>) and produces the full fabrication set: one copper Gerber per
/// layer, a solder-mask, a silkscreen and a solder-paste (stencil) Gerber per outer side, a board-outline
/// Gerber, and an Excellon drill program — so a routed board can be fully manufactured AND reflow-assembled.
///
/// <para><b>The oracle is the twin-decoder round trip</b> (the repo's rule — the geometry must survive
/// the round trip, not merely a structural validator pass): the copper written can be
/// <see cref="GerberReader">parsed back</see> and the recovered copper equals the copper model's on
/// each layer to the region-area grade, and the <see cref="ExcellonReader">decoded drill hits</see>
/// equal the board's holes exactly. See the ECAD fabrication tests.</para>
///
/// <para><b>The solder mask, silkscreen and solder paste are ADDITIVE</b> — they are derived from the
/// copper model (mask windows and paste apertures from the pads via <see cref="PcbMask"/> /
/// <see cref="PcbPaste"/>) and the placements (silk text / outlines via <see cref="PcbSilkscreen"/>)
/// without touching the copper path, so the copper Gerbers, outline and drill are byte-identical whether
/// or not those layers are requested. Their settings ride on the layout as LAYOUT TRUTH
/// (<see cref="PcbLayout.MaskSettings"/> / <see cref="PcbLayout.SilkscreenSettings"/> /
/// <see cref="PcbLayout.PasteSettings"/>, write-only-when-stated). Paste covers SMD pads ONLY — a
/// through-hole pad is wave/hand-soldered, so it gets no aperture (the SMD-only rule).</para>
///
/// <para><b>Gerber X2 (opt-in):</b> <c>Generate(..., includeX2: true)</c> / <c>Write(..., includeX2:
/// true)</c> adds the X2 <c>%TO.N,&lt;net&gt;*%</c> object attribute to each copper object (a board
/// house's net-compare datum), a <c>%TF.GenerationSoftware%</c> file attribute to EVERY Gerber, and each
/// Gerber's <c>%TF.FileFunction%</c> role — <c>Copper,L&lt;n&gt;,&lt;side&gt;</c> for a copper layer
/// (stackup position and side), <c>Soldermask,&lt;side&gt;</c> / <c>Legend,&lt;side&gt;</c> /
/// <c>SolderPaste,&lt;side&gt;</c> for the mask / silk / paste, and <c>Profile,NP</c> for the
/// (non-plated) board outline — so every file in the package is self-describing and matches the
/// <c>.gbrjob</c> manifest. Each COMPONENT PAD flash on the copper layer additionally carries the X2
/// <c>%TO.C,&lt;refdes&gt;*%</c> and <c>%TO.P,&lt;refdes&gt;,&lt;pad&gt;*%</c> assembly attributes (the
/// pad tied back to its component pin, looked up by feature source — a via / trace / pour carries none).
/// Each copper APERTURE also declares its <c>%TA.AperFunction%</c> role (<c>SMDPad,CuDef</c> /
/// <c>ComponentPad</c> for a component pad, <c>ViaPad</c> for a via, <c>Conductor</c> for a trace,
/// <c>Profile</c> for the outline), so apertures dedupe by (shape, function) under X2 — a via pad and a
/// trace of the same diameter but different role split into two D-codes. Opt-in, so with it off every
/// Gerber is byte-identical (the function collapses so dedup is by shape alone); the reader ignores X2
/// attributes (they carry metadata, not geometry), so an X2 file round-trips its copper exactly. Filed:
/// the <c>.C</c>/<c>.P</c> and <c>%TA</c> on the mask / silk / paste layers.</para>
///
/// <para><b>What it does not do (each filed):</b> step / multi-level stencils, paste-volume optimisation,
/// window-paning of large apertures, the assembly pick-and-place file (a different output), fine mask
/// tenting control beyond the tented/opened via policy, and a
/// Gerber IMPORT of a foreign board (this is EXPORT — the reader is the round-trip oracle scoped to what
/// the writer emits). Plated and non-plated holes are not split (the copper model does not distinguish
/// them at the drill), so all holes ride in one drill program.</para>
/// </summary>
public static class PcbGerberExport
{
    /// <summary>Generates the fabrication set (as text) for a routed layout — copper as pad flashes,
    /// trace draws and via annuli, plus the outline and drill. Pass a <paramref name="stencil"/> for a STEP
    /// (multi-level) solder-paste stencil (one paste Gerber per foil-thickness level); with none the paste
    /// is the SINGLE (flat) stencil the layout's <see cref="PcbLayout.PasteSettings"/> describe, and the
    /// output is byte-identical to before.</summary>
    public static FabricationOutput Generate(
        PcbLayout layout, string? name = null, PasteStencil? stencil = null, bool includeX2 = false)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var model = PcbCopperModel.FromLayout(layout);
        var format = FormatFor(model, layout.Traces);
        string design = ResolveName(name, layout.Schematic.Name);

        var viaSources = new HashSet<string>(model.Vias.Select(v => v.Source), StringComparer.Ordinal);
        var traceSources = new HashSet<string>(model.TraceSources, StringComparer.Ordinal);

        // The map from a pad feature's SOURCE ("R1.1") to its (refdes, pad, aperture-function) for the X2
        // %TO.C% / %TO.P% assembly attributes and its %TA.AperFunction% (SMDPad / ComponentPad). Keyed on
        // PlacedPad.Name, which IS the source the copper model builds, so it needs no string parsing.
        // Only built (and only consulted) when X2 is on.
        IReadOnlyDictionary<string, (string, string, string)>? padIdentity =
            includeX2 ? PadIdentity(layout) : null;

        var layers = new List<GerberLayerFile>();
        foreach (var name2 in model.Layers)
        {
            var features = model.Copper.Where(f =>
                f.Layer == name2 && !viaSources.Contains(f.Source) && !traceSources.Contains(f.Source));
            var vias = model.Vias.Where(v => v.Layers.Contains(name2));
            var traces = layout.Traces
                .Where(t => t.Layer == name2)
                .Select(t => ((IReadOnlyList<Vector2d>)t.Points, t.Width, (string?)t.Net));
            layers.Add(new GerberLayerFile(
                name2, GerberWriter.CopperLayer(name2, features, vias, traces, LayerHoles(model, name2), format,
                    includeX2, includeX2 ? CopperFileFunction(model, name2) : null, padIdentity)));
        }

        var mask = PcbMask.For(model, layout.MaskSettings);
        var silk = PcbSilkscreen.For(layout, layout.SilkscreenSettings);
        var paste = stencil is null ? PcbPaste.For(model, layout.PasteSettings) : PcbPaste.For(model, stencil);
        return Build(design, model, layers, format,
            MaskGerbers(mask, format, includeX2, model), SilkGerbers(silk, format, includeX2, model),
            PasteGerbers(paste, format, includeX2, model), includeX2);
    }

    /// <summary>Generates the fabrication set (as text) for a raw copper model — every copper feature
    /// is classified into a flash or a region fill (a copper pour and a trace stroke both region-fill,
    /// since a model carries no trace centre-lines to draw), plus the outline and drill. This is the
    /// path for a hand-built model with copper pours the layout does not yet carry. Pass a
    /// <paramref name="stencil"/> for a STEP (multi-level) solder-paste stencil.</summary>
    public static FabricationOutput Generate(
        PcbCopperModel model, string? name = null, PasteStencil? stencil = null, bool includeX2 = false)
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
                name2, GerberWriter.CopperLayer(
                    name2, features, vias,
                    Array.Empty<(IReadOnlyList<Vector2d>, double, string?)>(),
                    LayerHoles(model, name2), format,
                    includeX2, includeX2 ? CopperFileFunction(model, name2) : null)));
        }

        // The raw-model path has no placements, so no silk (an empty, well-formed Gerber per side); the
        // mask and paste are well-defined over the model's pad features and derived with the defaults (or
        // the step stencil, when one is supplied).
        var mask = PcbMask.For(model);
        var paste = stencil is null ? PcbPaste.For(model) : PcbPaste.For(model, stencil);
        return Build(design, model, layers, format,
            MaskGerbers(mask, format, includeX2, model), EmptySilk(model, format, includeX2),
            PasteGerbers(paste, format, includeX2, model), includeX2);
    }

    private static FabricationOutput Build(
        string design, PcbCopperModel model, IReadOnlyList<GerberLayerFile> layers, GerberFormat format,
        IReadOnlyList<GerberLayerFile> maskLayers, IReadOnlyList<GerberLayerFile> silkLayers,
        IReadOnlyList<GerberLayerFile> pasteLayers, bool x2 = false)
    {
        var outline = GerberWriter.Outline(model.Board.OutlinePoints, format, x2);
        var hits = model.Drills.Select(d => new DrillHit(d.Center, d.Diameter)).ToList();
        var drill = ExcellonWriter.Write(hits, format);
        return new FabricationOutput(
            design, layers, outline, drill, hits.Count, format, maskLayers, silkLayers, pasteLayers);
    }

    /// <summary>One solder-mask Gerber per side (the pad windows imaged dark). With <paramref name="x2"/>
    /// on each carries its X2 <c>Soldermask,&lt;side&gt;</c> file function.</summary>
    private static IReadOnlyList<GerberLayerFile> MaskGerbers(
        PcbMask mask, GerberFormat format, bool x2, PcbCopperModel model) =>
        [.. mask.Layers.Select(m => new GerberLayerFile(
            m.Layer, GerberWriter.MaskLayer(m.Layer, m.Openings.Select(o => o.Region), format,
                x2, NonCopperFileFunction(x2, model, m.Layer, "Soldermask"))))];

    /// <summary>One solder-paste (stencil) Gerber per layer (the SMD-pad apertures imaged dark) — one per
    /// side for a flat stencil, one per (side, level) for a step stencil. A step level carries its
    /// foil-thickness token so its file NAME does not collide; the Gerber content itself is named only by
    /// the side, so a one-level step at the default expansion is byte-identical to a flat stencil. With
    /// <paramref name="x2"/> on each carries its X2 <c>SolderPaste,&lt;side&gt;</c> file function.</summary>
    private static IReadOnlyList<GerberLayerFile> PasteGerbers(
        PcbPaste paste, GerberFormat format, bool x2, PcbCopperModel model) =>
        [.. paste.Layers.Select(p => new GerberLayerFile(
            p.Layer,
            GerberWriter.PasteLayer(p.Layer, p.Apertures.Select(a => a.Region), format,
                x2, NonCopperFileFunction(x2, model, p.Layer, "SolderPaste")),
            p.Level?.ThicknessToken))];

    /// <summary>One silkscreen Gerber per side (reference / value / outline line-work). With
    /// <paramref name="x2"/> on each carries its X2 <c>Legend,&lt;side&gt;</c> file function.</summary>
    private static IReadOnlyList<GerberLayerFile> SilkGerbers(
        PcbSilkscreen silk, GerberFormat format, bool x2, PcbCopperModel model) =>
        [.. silk.Layers.Select(s => new GerberLayerFile(
            s.Layer, GerberWriter.Silkscreen(s.Layer, s.Strokes.Select(st => st.Points), s.LineWidth, format,
                x2, NonCopperFileFunction(x2, model, s.Layer, "Legend"))))];

    /// <summary>A well-formed empty silkscreen Gerber per side (the raw-model path, which has no
    /// placements to draw). With <paramref name="x2"/> on each carries its X2 <c>Legend,&lt;side&gt;</c>
    /// file function.</summary>
    private static IReadOnlyList<GerberLayerFile> EmptySilk(PcbCopperModel model, GerberFormat format, bool x2)
    {
        double pen = PcbSilkscreenSettings.Default.LineWidth;
        string top = model.Board.Stackup.Top.Name, bottom = model.Board.Stackup.Bottom.Name;
        return
        [
            new GerberLayerFile(top, GerberWriter.Silkscreen(
                top, [], pen, format, x2, NonCopperFileFunction(x2, model, top, "Legend"))),
            new GerberLayerFile(bottom, GerberWriter.Silkscreen(
                bottom, [], pen, format, x2, NonCopperFileFunction(x2, model, bottom, "Legend"))),
        ];
    }

    /// <summary>The X2 <c>%TF.FileFunction%</c> value for a non-copper outer layer —
    /// <c>&lt;role&gt;,&lt;side&gt;</c> (e.g. <c>Soldermask,Top</c>), the side read off the stackup's top
    /// copper. Returns <c>null</c> when X2 is off, so the Gerber content is byte-identical.</summary>
    private static string? NonCopperFileFunction(bool x2, PcbCopperModel model, string layer, string role) =>
        x2 ? $"{role},{(layer == model.Board.Stackup.Top.Name ? "Top" : "Bot")}" : null;

    /// <summary>Writes the fabrication set for a routed layout to files under <paramref name="directory"/>
    /// (created if needed) — one <c>&lt;name&gt;-&lt;Layer&gt;.gbr</c> per copper layer, a
    /// <c>&lt;name&gt;-Edge_Cuts.gbr</c> outline, and a <c>&lt;name&gt;.drl</c> drill program — and
    /// reports what it wrote. With <paramref name="includeNetlist"/> true, an IPC-D-356A netlist
    /// (<c>&lt;name&gt;.ipc</c>, via <see cref="PcbIpc356.Write(PcbLayout)"/>) is written beside them for
    /// the board house's net-compare; with it false (the default) the Gerber / drill files are exactly
    /// what they were.</summary>
    public static GerberExportResult Write(
        PcbLayout layout, string directory, string? name = null, PasteStencil? stencil = null,
        bool includeNetlist = false, bool includeX2 = false, bool includeJobFile = false)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var output = Generate(layout, name, stencil, includeX2);
        var result = WriteOutput(output, directory);

        if (includeNetlist)
        {
            string ipcPath = Path.Combine(directory, output.Name + ".ipc");
            File.WriteAllText(ipcPath, PcbIpc356.Write(layout));
            result = result with { Files = [.. result.Files, ipcPath], NetlistWritten = true };
        }
        if (includeJobFile)
        {
            string jobPath = Path.Combine(directory, output.Name + ".gbrjob");
            File.WriteAllText(jobPath, BuildJobFile(output, layout.Board, layout.Fabrication));
            result = result with { Files = [.. result.Files, jobPath], JobFileWritten = true };
        }
        return result;
    }

    /// <summary>Builds the <c>.gbrjob</c> JSON for a written fabrication set — the board size and
    /// thickness, the copper-layer count, the surface finish, and every Gerber file with its
    /// <c>FileFunction</c> (copper roles by stackup position/side, mask/silk/paste by side, the outline
    /// as the profile). The file names mirror <see cref="WriteOutput"/>'s exactly.</summary>
    private static string BuildJobFile(FabricationOutput output, PcbBoard board, PcbFabricationSpec? spec)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var p in board.OutlinePoints)
        {
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
        }

        string top = board.Stackup.Top.Name;
        string Side(string layer) => layer == top ? "Top" : "Bot";

        var files = new List<(string, string)>();
        for (int i = 0; i < output.CopperLayers.Count; i++)
        {
            string side = i == 0 ? "Top" : i == output.CopperLayers.Count - 1 ? "Bot" : "Inr";
            files.Add(($"{output.Name}-{Sanitize(output.CopperLayers[i].Layer)}.gbr", $"Copper,L{i + 1},{side}"));
        }
        foreach (var l in output.MaskLayers)
            files.Add(($"{output.Name}-{Sanitize(l.Layer)}_Mask.gbr", $"Soldermask,{Side(l.Layer)}"));
        foreach (var l in output.SilkLayers)
            files.Add(($"{output.Name}-{Sanitize(l.Layer)}_Silkscreen.gbr", $"Legend,{Side(l.Layer)}"));
        foreach (var l in output.PasteLayers)
        {
            string suffix = string.IsNullOrEmpty(l.PasteLevelToken) ? "" : "_" + l.PasteLevelToken;
            files.Add(($"{output.Name}-{Sanitize(l.Layer)}_Paste{suffix}.gbr", $"SolderPaste,{Side(l.Layer)}"));
        }
        files.Add(($"{output.Name}-Edge_Cuts.gbr", "Profile,NP"));

        double thickness = spec?.FinishedThicknessMm ?? board.Thickness;
        return GerberJobFile.Build(
            output.Name, maxX - minX, maxY - minY, output.CopperLayers.Count, thickness, FinishName(spec), files);
    }

    private static string? FinishName(PcbFabricationSpec? spec) => spec?.SurfaceFinish switch
    {
        null => null,
        PcbSurfaceFinish.Enig => "ENIG",
        PcbSurfaceFinish.Hasl => "HAL",
        PcbSurfaceFinish.HaslLeadFree => "HAL lead free",
        PcbSurfaceFinish.Osp => "OSP",
        PcbSurfaceFinish.ImmersionSilver => "Immersion silver",
        PcbSurfaceFinish.ImmersionTin => "Immersion tin",
        PcbSurfaceFinish.Other => string.IsNullOrEmpty(spec.SurfaceFinishOther) ? null : spec.SurfaceFinishOther,
        _ => null,
    };

    /// <summary>Writes the fabrication set for a raw copper model to files (see the layout overload).</summary>
    public static GerberExportResult Write(
        PcbCopperModel model, string directory, string? name = null, PasteStencil? stencil = null) =>
        WriteOutput(Generate(model, name, stencil), directory);

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
        foreach (var layer in output.MaskLayers)
        {
            string path = Path.Combine(directory, $"{output.Name}-{Sanitize(layer.Layer)}_Mask.gbr");
            File.WriteAllText(path, layer.Gerber);
            files.Add(path);
        }
        foreach (var layer in output.SilkLayers)
        {
            string path = Path.Combine(directory, $"{output.Name}-{Sanitize(layer.Layer)}_Silkscreen.gbr");
            File.WriteAllText(path, layer.Gerber);
            files.Add(path);
        }
        foreach (var layer in output.PasteLayers)
        {
            // A step-stencil level appends its foil-thickness token (e.g. `_100um`) so two levels of one
            // side write distinct files; a flat single-stencil paste has none (byte-identical file name).
            string suffix = string.IsNullOrEmpty(layer.PasteLevelToken) ? "" : "_" + layer.PasteLevelToken;
            string path = Path.Combine(directory, $"{output.Name}-{Sanitize(layer.Layer)}_Paste{suffix}.gbr");
            File.WriteAllText(path, layer.Gerber);
            files.Add(path);
        }
        string outlinePath = Path.Combine(directory, $"{output.Name}-Edge_Cuts.gbr");
        File.WriteAllText(outlinePath, output.OutlineGerber);
        files.Add(outlinePath);

        string drillPath = Path.Combine(directory, $"{output.Name}.drl");
        File.WriteAllText(drillPath, output.Drill);
        files.Add(drillPath);

        return new GerberExportResult(
            directory, files, output.CopperLayers.Count, output.DrillHitCount,
            output.MaskLayers.Count, output.SilkLayers.Count, output.PasteLayers.Count);
    }

    /// <summary>The X2 <c>%TF.FileFunction%</c> value for a copper layer — <c>Copper,L&lt;n&gt;,&lt;side&gt;</c>,
    /// where n is the 1-based stackup position and the side is Top (first copper), Bot (last) or Inr
    /// (an inner layer). It tells a fab the copper layer order and side straight from the Gerber.</summary>
    /// <summary>The map from a component pad's copper-feature SOURCE (<c>"R1.1"</c>) to its (refdes,
    /// pad-number, aperture-function), for the X2 <c>%TO.C%</c> / <c>%TO.P%</c> assembly attributes and
    /// the pad's <c>%TA.AperFunction%</c> (<c>SMDPad,CuDef</c> for a surface pad, <c>ComponentPad</c> for
    /// a through-hole one). The key is <see cref="PlacedPad.Name"/>, which is exactly the source
    /// <see cref="PcbCopperModel.FromLayout"/> builds a pad feature with — so a flash is tied back to its
    /// component pin with no string parsing. A repeated name (never, since a refdes is unique and its pad
    /// numbers are) keeps the last.</summary>
    private static Dictionary<string, (string, string, string)> PadIdentity(PcbLayout layout)
    {
        var map = new Dictionary<string, (string, string, string)>(StringComparer.Ordinal);
        foreach (var pad in layout.PlacedPads())
        {
            string aperFunction = pad.Kind == PadKind.ThroughHole ? "ComponentPad" : "SMDPad,CuDef";
            map[pad.Name] = (pad.Reference, pad.PadNumber, aperFunction);
        }
        return map;
    }

    private static string CopperFileFunction(PcbCopperModel model, string layer)
    {
        var coppers = model.Layers;
        int i = 0;
        for (int k = 0; k < coppers.Count; k++)
            if (coppers[k] == layer) { i = k; break; }
        string side = i == 0 ? "Top" : i == coppers.Count - 1 ? "Bot" : "Inr";
        return $"Copper,L{i + 1},{side}";
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
