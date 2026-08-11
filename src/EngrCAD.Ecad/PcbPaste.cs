using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Ecad;

/// <summary>
/// How a <see cref="PcbPaste"/> is derived: the solder-paste EXPANSION — how far each SMD pad's stencil
/// aperture is grown past the pad copper. It is a value (equal settings compare equal), so it rides in
/// the layout file as LAYOUT TRUTH the same way a mask's or a pour's settings do — write-only-when-stated,
/// so a layout that states none saves byte-identically.
///
/// <para><b>The default is slightly NEGATIVE.</b> A stencil aperture is typically a few percent — a few
/// mils — SMALLER than the pad it deposits paste onto, to control the paste VOLUME (a paste brick as wide
/// as the pad tends to bridge and slump; pulling the aperture in a hair leaves the same shape with less
/// volume). So the default <see cref="Expansion"/> is <c>-0.05 mm</c> (~2 mil in, ⚠ verify against your
/// stencil house's process). A positive expansion (a larger aperture) is allowed — it is unusual but has
/// uses (a proud thermal pad) — and a zero expansion makes the aperture the exact pad.</para>
/// </summary>
/// <param name="Expansion">The paste expansion (mm) — how far the aperture is grown past the pad copper.
/// Negative (the default) SHRINKS the aperture, 0 makes it the exact pad, positive grows it. A negative
/// value large enough to consume the pad simply leaves no aperture there.</param>
public sealed record PcbPasteSettings(double Expansion = PcbPasteSettings.DefaultExpansion)
{
    /// <summary>The default paste expansion (mm) — <c>-0.05 mm</c> (~2 mil in), a common house value
    /// that pulls the aperture in a hair to control paste volume (⚠ verify against your process).</summary>
    public const double DefaultExpansion = -0.05;

    /// <summary>The default settings — a -0.05 mm expansion.</summary>
    public static PcbPasteSettings Default { get; } = new();

    internal void Validate()
    {
        if (!double.IsFinite(Expansion))
            throw new ArgumentException($"A paste expansion must be finite (got {Expansion:g6}).");
    }
}

/// <summary>One stencil aperture — the opening through which paste is deposited onto ONE SMD pad: its
/// source pad (<c>"R1.1"</c>) and the exact region (the pad grown by the paste expansion) the stencil is
/// cut to.</summary>
/// <param name="Source">The SMD pad this aperture prints paste onto.</param>
/// <param name="Region">The aperture region in board-local 2D coordinates (mm) — the pad grown by the
/// expansion.</param>
public readonly record struct PasteAperture(string Source, CurvedRegion2d Region);

/// <summary>One side's solder-paste (stencil) layer — the outer copper <see cref="Layer"/> it prints
/// over, the <see cref="Side"/>, and the apertures cut in the stencil. The Gerber images the APERTURES
/// (the openings), so it decodes back to the apertures (see <see cref="GerberWriter.PasteLayer"/>).</summary>
/// <param name="Layer">The outer copper layer name (<c>"Top"</c>, <c>"Bottom"</c>).</param>
/// <param name="Side">The board face.</param>
/// <param name="Apertures">The stencil apertures (one per SMD pad on this side), in copper-feature
/// declaration order.</param>
public sealed record PasteLayerContent(
    string Layer, CopperSide Side, IReadOnlyList<PasteAperture> Apertures);

/// <summary>
/// The solder-paste (stencil) model, derived from a <see cref="PcbCopperModel"/>: a stencil aperture over
/// every SMD pad — and ONLY over SMD pads — each aperture the pad grown by the
/// <see cref="PcbPasteSettings.Expansion"/>. It is the reflow-assembly layer: paste is printed through
/// the stencil onto the SMD lands, the parts are placed, and reflow wets the joints. Like the solder mask
/// it exists on the two OUTER copper layers only (<see cref="PcbStackup.Top"/> / <see cref="PcbStackup.Bottom"/>).
///
/// <para><b>SMD pads ONLY — the SMD-only rule.</b> A through-hole / plated pad gets NO paste (through-hole
/// parts are wave- or hand-soldered, so a stencil aperture over one would only foul the barrel), which is
/// the classic bug this layer must not have. Which pads are SMD is read from the copper model itself: a
/// pad is a COMPONENT pad (not a trace, pour or via — the mask's own distinction) that carries NO DRILL
/// (its source is not among the model's <see cref="PcbCopperModel.Drills"/>). A via gets no paste EVER —
/// unlike the mask there is no via policy here, because paste on a via wicks solder down the barrel.</para>
///
/// <para><b>The aperture is EXACT — the pad grown by the expansion</b> (<see cref="CurvedRegion2dOffset"/>,
/// round joins), so a round pad's aperture is a disc of radius r + expansion (area π(r+e)²) — with the
/// default negative expansion a disc SMALLER than the pad — and a rectangular pad's is a rounded rectangle.
/// That exactness is the oracle: the decoded Gerber aperture equals the pad grown by the expansion to the
/// region grade.</para>
///
/// <para><b>The Gerber convention</b>: a paste Gerber images the stencil APERTURES (the openings) as dark
/// — the same positive-openings form the mask uses — so a paste Gerber decodes back to the apertures, and
/// the stencil is cut where the Gerber is dark.</para>
/// </summary>
public sealed class PcbPaste
{
    private PcbPaste(PasteLayerContent top, PasteLayerContent bottom, PcbPasteSettings settings)
    {
        Top = top;
        Bottom = bottom;
        Settings = settings;
    }

    /// <summary>The top-side paste (apertures on the outer top copper).</summary>
    public PasteLayerContent Top { get; }

    /// <summary>The bottom-side paste (apertures on the outer bottom copper).</summary>
    public PasteLayerContent Bottom { get; }

    /// <summary>The settings the paste was derived with.</summary>
    public PcbPasteSettings Settings { get; }

    /// <summary>The two paste layers (top then bottom).</summary>
    public IReadOnlyList<PasteLayerContent> Layers => [Top, Bottom];

    /// <summary>
    /// Derives the solder-paste stencil from a copper model: an aperture (the pad grown by
    /// <see cref="PcbPasteSettings.Expansion"/>) over every SMD pad on each outer copper layer.
    /// Through-hole pads and vias get no aperture (the SMD-only rule).
    /// </summary>
    /// <exception cref="ArgumentException">A settings value is invalid, or an aperture lies entirely off
    /// the board (a pad placed off the board) — refused by name.</exception>
    public static PcbPaste For(PcbCopperModel model, PcbPasteSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        settings ??= PcbPasteSettings.Default;
        settings.Validate();

        var boardBounds = BoardBounds(model.Board);
        var top = AperturesOn(model, model.Board.Stackup.Top, CopperSide.Top, settings, boardBounds);
        var bottom = AperturesOn(model, model.Board.Stackup.Bottom, CopperSide.Bottom, settings, boardBounds);
        return new PcbPaste(top, bottom, settings);
    }

    /// <summary>The paste layer for an OUTER copper layer name, refused by name for any other layer
    /// (an inner layer, or a layer the board does not have) — the stencil exists on the outer copper
    /// only.</summary>
    /// <exception cref="ArgumentException"><paramref name="layer"/> is not an outer copper layer.</exception>
    public PasteLayerContent LayerFor(string layer)
    {
        if (layer == Top.Layer)
            return Top;
        if (layer == Bottom.Layer)
            return Bottom;
        throw new ArgumentException(
            $"'{layer}' is not an outer copper layer; the solder-paste stencil exists on the outer copper "
            + $"layers only ('{Top.Layer}', '{Bottom.Layer}').", nameof(layer));
    }

    private static PasteLayerContent AperturesOn(
        PcbCopperModel model, CopperLayerSpec spec, CopperSide side, PcbPasteSettings settings,
        Aabb boardBounds)
    {
        // The mask's own distinction of a COMPONENT pad — not a trace, a pour or a via land.
        var traceSources = new HashSet<string>(model.TraceSources, StringComparer.Ordinal);
        var pourSources = new HashSet<string>(model.PourSources, StringComparer.Ordinal);
        var viaSources = new HashSet<string>(model.Vias.Select(v => v.Source), StringComparer.Ordinal);
        // The SMD-only rule: a through-hole pad's source is a drilled hole (as are vias and board holes),
        // so it carries a drill and is excluded. A pad with no drill is SMD.
        var drilledSources = new HashSet<string>(model.Drills.Select(d => d.Source), StringComparer.Ordinal);

        var apertures = new List<PasteAperture>();
        foreach (var feature in model.Copper)
        {
            if (feature.Layer != spec.Name)
                continue;
            if (traceSources.Contains(feature.Source) || pourSources.Contains(feature.Source)
                || viaSources.Contains(feature.Source))
                continue;
            if (drilledSources.Contains(feature.Source))
                continue;   // a through-hole / plated pad is wave- or hand-soldered — no paste
            AddAperture(feature.Source, feature.Region, settings.Expansion, boardBounds, apertures);
        }

        // Deliberately no via policy here (unlike the mask): a via never gets paste, so vias are simply
        // excluded above and never reached.
        return new PasteLayerContent(spec.Name, side, apertures);
    }

    private static void AddAperture(
        string source, CurvedRegion2d pad, double expansion, Aabb boardBounds, List<PasteAperture> into)
    {
        // The aperture is the pad grown by the expansion (round joins) — exact for any pad shape. An
        // exact-zero expansion returns the pad verbatim (the Offset contract); a negative one shrinks it,
        // and a negative one large enough to consume the pad leaves no aperture (an empty offset result).
        foreach (var region in CurvedRegion2dOffset.Offset(pad, expansion, OffsetJoin.Round))
        {
            var b = region.Bounds;
            bool offBoard =
                b.Max.X < boardBounds.Min.X || b.Min.X > boardBounds.Max.X ||
                b.Max.Y < boardBounds.Min.Y || b.Min.Y > boardBounds.Max.Y;
            if (offBoard)
                throw new ArgumentException(
                    $"Solder-paste aperture '{source}' lies entirely off the board (its bounds "
                    + $"[{b.Min.X:g6}, {b.Min.Y:g6}]-[{b.Max.X:g6}, {b.Max.Y:g6}] do not touch the "
                    + $"board's [{boardBounds.Min.X:g6}, {boardBounds.Min.Y:g6}]-"
                    + $"[{boardBounds.Max.X:g6}, {boardBounds.Max.Y:g6}]). A pad off the board is a "
                    + "placement error.");
            into.Add(new PasteAperture(source, region));
        }
    }

    private static Aabb BoardBounds(PcbBoard board)
    {
        var b = Aabb.Empty;
        foreach (var p in board.OutlinePoints)
            b = b.Union(new Vector3d(p.X, p.Y, 0));
        return b;
    }
}
