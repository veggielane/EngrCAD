using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Ecad;

/// <summary>Whether the solder mask covers a via (tented) or leaves a window over it (opened).</summary>
public enum ViaMaskPolicy
{
    /// <summary>The mask covers the via — no opening (the default; a signal / stitching via has no
    /// need to be soldered, and tenting keeps flux and probes off it).</summary>
    Tented,

    /// <summary>The mask leaves a window over the via land (via + expansion), so it can be soldered
    /// or probed.</summary>
    Opened,
}

/// <summary>
/// How a <see cref="PcbMask"/> is derived: how far each solderable pad's mask window is grown past the
/// copper (the mask EXPANSION — the annular sliver of bare laminate the mask pulls back to, so a small
/// registration error never lets mask creep onto the land), and whether vias are tented or opened.
/// It is a value (equal settings compare equal), so it rides in the layout file as LAYOUT TRUTH the
/// same way a pour's settings do — write-only-when-stated, so a layout that states none saves
/// byte-identically.
/// </summary>
/// <param name="Expansion">The mask expansion (mm) — how far the opening is grown past the pad copper.
/// 0 makes the window the exact pad. Negative (a mask-defined pad, where the mask overlaps the copper)
/// is refused by name in v1.</param>
/// <param name="Vias">Whether placed vias are tented (no opening) or opened.</param>
public sealed record PcbMaskSettings(
    double Expansion = PcbMaskSettings.DefaultExpansion,
    ViaMaskPolicy Vias = ViaMaskPolicy.Tented)
{
    /// <summary>The default mask expansion (mm) — 0.05 mm (~2 mil), a common IPC-ish house value
    /// (⚠ verify against your fabricator's process).</summary>
    public const double DefaultExpansion = 0.05;

    /// <summary>The default settings — a 0.05 mm expansion with vias tented.</summary>
    public static PcbMaskSettings Default { get; } = new();

    internal void Validate()
    {
        if (!double.IsFinite(Expansion))
            throw new ArgumentException($"A mask expansion must be finite (got {Expansion:g6}).");
        if (Expansion < 0)
            throw new ArgumentException(
                $"A mask expansion cannot be negative (got {Expansion:g6}); a mask-defined pad (mask "
                + "overlapping the copper) is not modelled in v1.");
    }
}

/// <summary>One window in the solder mask — the exposed copper of a pad or opened via: its source pad
/// (<c>"R1.1"</c>, a via id) and the exact region (the pad grown by the mask expansion) the mask is
/// pulled back from.</summary>
/// <param name="Source">The pad / via the window exposes.</param>
/// <param name="Region">The opening region in board-local 2D coordinates (mm) — the pad grown by the
/// expansion.</param>
public readonly record struct MaskOpening(string Source, CurvedRegion2d Region);

/// <summary>One side's solder mask — the outer copper <see cref="Layer"/> it masks, the
/// <see cref="Side"/>, and the windows the mask leaves. The mask itself is the whole board MINUS these
/// windows; the Gerber images the windows (see <see cref="GerberWriter.MaskLayer"/>).</summary>
/// <param name="Layer">The outer copper layer name (<c>"Top"</c>, <c>"Bottom"</c>).</param>
/// <param name="Side">The board face.</param>
/// <param name="Openings">The mask windows (the exposed pads), in copper-feature declaration order.</param>
public sealed record MaskLayerContent(
    string Layer, CopperSide Side, IReadOnlyList<MaskOpening> Openings);

/// <summary>
/// The solder-mask model, derived from a <see cref="PcbCopperModel"/>: the mask covers the whole board
/// EXCEPT windows over the pads that must be soldered, each window the pad grown by the
/// <see cref="PcbMaskSettings.Expansion"/>. The mask exists on the two OUTER copper layers only
/// (<see cref="PcbStackup.Top"/> / <see cref="PcbStackup.Bottom"/>) — inner copper is buried and never
/// masked — so a through-hole pad (which is on every layer, solderable from both faces) gets a window
/// on both.
///
/// <para><b>What is a solderable pad.</b> Every component pad is one; a trace and a copper pour are NOT
/// (they stay covered by mask), and a via is tented or opened by <see cref="PcbMaskSettings.Vias"/>.
/// The distinction is read from the copper model's own tags (<see cref="PcbCopperModel.TraceSources"/>,
/// <see cref="PcbCopperModel.PourSources"/>, <see cref="PcbCopperModel.Vias"/>), so the mask cannot
/// disagree with the copper about what a pad is.</para>
///
/// <para><b>The window is EXACT — the pad grown by the expansion</b> (<see cref="CurvedRegion2dOffset"/>,
/// round joins), so a round pad's window is a disc of radius r + expansion (area π(r+e)²) and a
/// rectangular pad's is a rounded rectangle. That exactness is the oracle: the decoded Gerber window
/// equals the pad grown by the expansion to the region grade.</para>
///
/// <para><b>The Gerber convention</b> (the standard positive-openings form, as KiCad / Eagle): the mask
/// Gerber images the WINDOWS as dark, and the fabricator clears mask where the Gerber is dark and leaves
/// mask elsewhere — so a mask Gerber decodes back to the openings, not to the mask coverage.</para>
/// </summary>
public sealed class PcbMask
{
    private PcbMask(MaskLayerContent top, MaskLayerContent bottom, PcbMaskSettings settings)
    {
        Top = top;
        Bottom = bottom;
        Settings = settings;
    }

    /// <summary>The top-side mask (windows on the outer top copper).</summary>
    public MaskLayerContent Top { get; }

    /// <summary>The bottom-side mask (windows on the outer bottom copper).</summary>
    public MaskLayerContent Bottom { get; }

    /// <summary>The settings the mask was derived with.</summary>
    public PcbMaskSettings Settings { get; }

    /// <summary>The two mask layers (top then bottom).</summary>
    public IReadOnlyList<MaskLayerContent> Layers => [Top, Bottom];

    /// <summary>
    /// Derives the solder mask from a copper model: a window (the pad grown by
    /// <see cref="PcbMaskSettings.Expansion"/>) over every solderable pad on each outer copper layer,
    /// plus a window over each opened via when <see cref="PcbMaskSettings.Vias"/> is
    /// <see cref="ViaMaskPolicy.Opened"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A settings value is invalid, or a window lies entirely off
    /// the board (a pad placed off the board) — refused by name.</exception>
    public static PcbMask For(PcbCopperModel model, PcbMaskSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        settings ??= PcbMaskSettings.Default;
        settings.Validate();

        var boardBounds = BoardBounds(model.Board);
        var top = OpeningsOn(model, model.Board.Stackup.Top, CopperSide.Top, settings, boardBounds);
        var bottom = OpeningsOn(model, model.Board.Stackup.Bottom, CopperSide.Bottom, settings, boardBounds);
        return new PcbMask(top, bottom, settings);
    }

    /// <summary>The mask layer for an OUTER copper layer name, refused by name for any other layer
    /// (an inner layer, or a layer the board does not have) — the mask exists on the outer copper
    /// only.</summary>
    /// <exception cref="ArgumentException"><paramref name="layer"/> is not an outer copper layer.</exception>
    public MaskLayerContent LayerFor(string layer)
    {
        if (layer == Top.Layer)
            return Top;
        if (layer == Bottom.Layer)
            return Bottom;
        throw new ArgumentException(
            $"'{layer}' is not an outer copper layer; the solder mask exists on the outer copper layers "
            + $"only ('{Top.Layer}', '{Bottom.Layer}').", nameof(layer));
    }

    private static MaskLayerContent OpeningsOn(
        PcbCopperModel model, CopperLayerSpec spec, CopperSide side, PcbMaskSettings settings,
        Aabb boardBounds)
    {
        // A pad is any copper feature that is not a trace, a pour or a via land — the copper model's
        // own tags decide, so the mask cannot disagree with the copper about what a pad is.
        var traceSources = new HashSet<string>(model.TraceSources, StringComparer.Ordinal);
        var pourSources = new HashSet<string>(model.PourSources, StringComparer.Ordinal);
        var viaSources = new HashSet<string>(model.Vias.Select(v => v.Source), StringComparer.Ordinal);

        var openings = new List<MaskOpening>();
        foreach (var feature in model.Copper)
        {
            if (feature.Layer != spec.Name)
                continue;
            if (traceSources.Contains(feature.Source) || pourSources.Contains(feature.Source)
                || viaSources.Contains(feature.Source))
                continue;
            AddWindow(feature.Source, feature.Region, settings.Expansion, boardBounds, openings);
        }

        if (settings.Vias == ViaMaskPolicy.Opened)
            foreach (var via in model.Vias)
                if (via.Layers.Contains(spec.Name))
                    AddWindow(via.Source, via.PadRegion, settings.Expansion, boardBounds, openings);

        return new MaskLayerContent(spec.Name, side, openings);
    }

    private static void AddWindow(
        string source, CurvedRegion2d pad, double expansion, Aabb boardBounds, List<MaskOpening> into)
    {
        // The window is the pad grown by the expansion (round joins) — exact for any pad shape. An
        // exact-zero expansion returns the pad verbatim (the Offset contract), so a 0 mask is the pad.
        foreach (var region in CurvedRegion2dOffset.Offset(pad, expansion, OffsetJoin.Round))
        {
            var b = region.Bounds;
            bool offBoard =
                b.Max.X < boardBounds.Min.X || b.Min.X > boardBounds.Max.X ||
                b.Max.Y < boardBounds.Min.Y || b.Min.Y > boardBounds.Max.Y;
            if (offBoard)
                throw new ArgumentException(
                    $"Solder-mask window '{source}' lies entirely off the board (its bounds "
                    + $"[{b.Min.X:g6}, {b.Min.Y:g6}]-[{b.Max.X:g6}, {b.Max.Y:g6}] do not touch the "
                    + $"board's [{boardBounds.Min.X:g6}, {boardBounds.Min.Y:g6}]-"
                    + $"[{boardBounds.Max.X:g6}, {boardBounds.Max.Y:g6}]). A pad off the board is a "
                    + "placement error.");
            into.Add(new MaskOpening(source, region));
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
