using System.Globalization;
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
///
/// <para>For a STEP (multi-level) stencil — a foil milled to different thicknesses in different zones, with
/// its own aperture expansion per level — see <see cref="PasteStencil"/>, a separate step declaration
/// passed to <see cref="PcbPaste.For(PcbCopperModel, PasteStencil)"/> and the Gerber export. This flat
/// settings type is the single-stencil case.</para>
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

/// <summary>
/// Decides which SMD pads a <see cref="PasteStep"/> (foil-thickness level) of a step stencil covers. It is
/// a predicate over a pad's <c>Source</c> and its copper <c>Region</c> (the pad BEFORE the level's
/// expansion is applied), so a level's selection is a property of the PAD, not of the grown aperture.
///
/// <para>The two required kinds are an explicit ZONE (<see cref="InZone"/> / <see cref="InRectangle"/>: a
/// pad whose CENTRE lies in the zone) and an explicit PAD SET (<see cref="Pads"/> / <see cref="Component"/>);
/// the fine-pitch <see cref="FinePitch"/> HEURISTIC is opt-in with no silent default (its threshold is an
/// engineering input — the minimum-member-size rule).</para>
/// </summary>
public sealed class PasteLevelSelector
{
    private readonly Func<string, CurvedRegion2d, bool> _covers;

    private PasteLevelSelector(string description, Func<string, CurvedRegion2d, bool> covers)
    {
        Description = description;
        _covers = covers;
    }

    /// <summary>A short human-readable description of the rule (for reports).</summary>
    public string Description { get; }

    /// <summary>Does this selector cover the pad with the given <paramref name="source"/> and copper
    /// <paramref name="pad"/> region?</summary>
    public bool Covers(string source, CurvedRegion2d pad)
    {
        ArgumentNullException.ThrowIfNull(pad);
        return _covers(source, pad);
    }

    private static Vector2d Centre(CurvedRegion2d pad)
    {
        var c = pad.Bounds.Center;
        return new Vector2d(c.X, c.Y);
    }

    /// <summary>A pad whose CENTRE lies inside <paramref name="zone"/> (a curved region in board-local mm)
    /// takes this level. Zones are ordered by their step's position; first match wins (see
    /// <see cref="PasteStencil"/>).</summary>
    public static PasteLevelSelector InZone(CurvedRegion2d zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return new PasteLevelSelector("in zone", (_, pad) => zone.Contains(Centre(pad)));
    }

    /// <summary>A pad whose CENTRE lies in the axis-aligned rectangle <paramref name="min"/>..<paramref
    /// name="max"/> (board-local mm) takes this level — the common rectangular zone, tested directly as an
    /// AABB so no region is built.</summary>
    public static PasteLevelSelector InRectangle(Vector2d min, Vector2d max) =>
        new($"in rectangle [{min.X:g6},{min.Y:g6}]-[{max.X:g6},{max.Y:g6}]", (_, pad) =>
        {
            var c = Centre(pad);
            return c.X >= min.X && c.X <= max.X && c.Y >= min.Y && c.Y <= max.Y;
        });

    /// <summary>A pad whose <c>Source</c> (e.g. <c>"U1.1"</c>) is one of <paramref name="sources"/> takes
    /// this level — an explicit pad selection.</summary>
    public static PasteLevelSelector Pads(IEnumerable<string> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var set = new HashSet<string>(sources, StringComparer.Ordinal);
        return new PasteLevelSelector($"pads {{{string.Join(", ", set.OrderBy(s => s, StringComparer.Ordinal))}}}",
            (source, _) => set.Contains(source));
    }

    /// <summary>A pad whose <c>Source</c> (e.g. <c>"U1.1"</c>) is one of <paramref name="sources"/>.</summary>
    public static PasteLevelSelector Pads(params string[] sources) => Pads((IEnumerable<string>)sources);

    /// <summary>Every pad of the component with reference designator <paramref name="refdes"/> (a source of
    /// <c>"U1"</c> itself, or <c>"U1.&lt;pin&gt;"</c>) takes this level — the "all pads of a footprint"
    /// case.</summary>
    public static PasteLevelSelector Component(string refdes)
    {
        ArgumentNullException.ThrowIfNull(refdes);
        string prefix = refdes + ".";
        return new PasteLevelSelector($"component {refdes}",
            (source, _) => source == refdes || source.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// The FINE-PITCH heuristic: a pad whose bounding-box MAXIMUM dimension is at or below
    /// <paramref name="maxPadSizeMm"/> takes this level — the thin-foil / reduced-aperture level a
    /// fine-pitch part (a 0.4 mm QFN, an 0201) wants. Opt-in, and the threshold is a REQUIRED engineering
    /// input (there is no silent default; a default here would be a process decision made by a library —
    /// the minimum-member-size rule).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxPadSizeMm"/> is not positive
    /// finite.</exception>
    public static PasteLevelSelector FinePitch(double maxPadSizeMm)
    {
        if (!(maxPadSizeMm > 0) || !double.IsFinite(maxPadSizeMm))
            throw new ArgumentOutOfRangeException(nameof(maxPadSizeMm),
                $"The fine-pitch pad-size threshold must be positive finite (got {maxPadSizeMm:g6}).");
        return new PasteLevelSelector($"fine-pitch (pad ≤ {maxPadSizeMm:g6} mm)", (_, pad) =>
        {
            var b = pad.Bounds;
            double w = b.Max.X - b.Min.X, h = b.Max.Y - b.Min.Y;
            return Math.Max(w, h) <= maxPadSizeMm;
        });
    }
}

/// <summary>
/// One foil-thickness LEVEL of a step stencil (see <see cref="PasteStencil"/>): the milled foil thickness
/// there, the paste aperture <see cref="Expansion"/> for pads on that level, and the
/// <see cref="Selector"/> that decides which SMD pads it covers.
///
/// <para>A step with a <c>null</c> <see cref="Selector"/> is the DEFAULT level — it covers every pad no
/// earlier step claimed. Its foil thickness is only the level's IDENTITY (it names the level's Gerber
/// file, e.g. <c>_100um</c>); the level's GEOMETRY is the pad grown by its own expansion, so the foil
/// thickness never touches an aperture — that separation is exactly why the aperture geometry per level is
/// still the pad ± expansion, the same oracle the single stencil has.</para>
/// </summary>
/// <param name="FoilThickness">The milled foil thickness at this level, in mm (e.g. <c>0.1</c> for a
/// 100 µm foil). Positive, finite, and DISTINCT across a stencil's levels (a level's thickness names its
/// Gerber file, so two levels of one thickness would collide).</param>
/// <param name="Expansion">The paste aperture expansion (mm) for pads on this level — negative shrinks the
/// aperture (the usual thin-foil choice), 0 is the exact pad, positive grows it (the usual thick-foil
/// choice for a large thermal pad).</param>
/// <param name="Selector">The rule deciding which SMD pads this level covers, or <c>null</c> for the
/// DEFAULT level (every pad no earlier step claimed).</param>
public sealed record PasteStep(double FoilThickness, double Expansion, PasteLevelSelector? Selector)
{
    /// <summary>The default (catch-all) level of a stencil: a level with no selector.</summary>
    public static PasteStep Default(double foilThickness, double expansion) =>
        new(foilThickness, expansion, null);

    /// <summary>A level covering the pads the <paramref name="selector"/> picks.</summary>
    public static PasteStep For(double foilThickness, double expansion, PasteLevelSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return new PasteStep(foilThickness, expansion, selector);
    }

    /// <summary><c>true</c> if this is the DEFAULT (catch-all) level (no selector).</summary>
    public bool IsDefault => Selector is null;

    /// <summary>The filename token for this level's foil thickness (e.g. <c>"100um"</c> for 0.1 mm) — the
    /// level's identity in its Gerber filename. Micrometres when the thickness is a whole number of them
    /// (the common case), with a fractional micrometre spelled with a <c>p</c> (never a dot, so the token
    /// is filename-safe) as a deterministic fallback.</summary>
    public string ThicknessToken => FormatToken(FoilThickness);

    internal static string FormatToken(double foilThicknessMm)
    {
        double um = foilThicknessMm * 1000.0;
        double rounded = Math.Round(um);
        if (Math.Abs(um - rounded) <= 1e-6 * Math.Max(1.0, Math.Abs(um)))
            return ((long)rounded).ToString(CultureInfo.InvariantCulture) + "um";
        // A fractional-micrometre thickness: a deterministic, filename-safe spelling (dot -> 'p').
        return um.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', 'p') + "um";
    }
}

/// <summary>
/// A STEP (multi-level) solder-paste stencil declaration: an ordered list of foil-thickness
/// <see cref="Steps">levels</see>, each with its own foil thickness, its own paste expansion, and a
/// <see cref="PasteLevelSelector">selector</see> for the pads it covers. A step stencil is milled to
/// DIFFERENT foil thicknesses in different zones — a fine-pitch part wants a thin foil / reduced aperture
/// to control paste volume, a large thermal pad or connector wants a thick foil / more paste — and because
/// each thickness is a separate milling depth, the fab consumes ONE PASTE GERBER PER LEVEL.
///
/// <para><b>Every SMD pad is on EXACTLY ONE level (a partition).</b> A pad is assigned to the FIRST step
/// whose selector covers it (in list order — so overlapping zones resolve by first-match, a STATED rule
/// rather than an error), and a pad no step claims falls to the DEFAULT level (a step with no selector,
/// which every valid stencil must declare). So no pad is printed twice, and none is dropped.</para>
///
/// <para><b>The aperture geometry per level is the pad grown by THAT LEVEL's expansion</b> — the same exact
/// <see cref="CurvedRegion2dOffset"/> machinery the single stencil uses. A level only changes the expansion
/// and the foil, never how an aperture is computed, so a level's round-pad aperture is still a disc of area
/// π(r+e)². The SMD-only rule survives too: a through-hole pad and a via still get no aperture on any
/// level.</para>
///
/// <para>A step stencil is a FABRICATION-PROCESS parameter (which pads get thick/thin foil) — like a
/// <see cref="DrcRuleSet"/> it is passed to the export (<see cref="PcbGerberExport.Generate(PcbLayout,
/// string?, PasteStencil?)"/>), not baked into the layout file, so a layout that declares no stencil saves
/// byte-identically. (Persisting a step declaration is filed.)</para>
/// </summary>
public sealed class PasteStencil
{
    /// <summary>Builds a step stencil from its ordered levels.</summary>
    /// <exception cref="ArgumentException">No levels; a level with a non-positive or non-finite foil
    /// thickness; a level with a non-finite expansion; no DEFAULT level (a step with no selector); or two
    /// levels of the same foil thickness (their Gerber files would collide) — each refused BY NAME.</exception>
    public PasteStencil(IEnumerable<PasteStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var list = steps.ToList();
        if (list.Count == 0)
            throw new ArgumentException("A step stencil must declare at least one level.", nameof(steps));

        var thicknesses = new HashSet<double>();
        foreach (var step in list)
        {
            if (!(step.FoilThickness > 0) || !double.IsFinite(step.FoilThickness))
                throw new ArgumentException(
                    $"A paste level's foil thickness must be positive finite (got {step.FoilThickness:g6} mm "
                    + $"for the {(step.IsDefault ? "default" : step.Selector!.Description)} level).", nameof(steps));
            if (!double.IsFinite(step.Expansion))
                throw new ArgumentException(
                    $"A paste level's expansion must be finite (got {step.Expansion:g6}).", nameof(steps));
            if (!thicknesses.Add(step.FoilThickness))
                throw new ArgumentException(
                    $"Two paste levels have the same foil thickness ({step.FoilThickness:g6} mm); each "
                    + "level names its own Gerber file by thickness, so a step stencil's foil thicknesses "
                    + "must be distinct.", nameof(steps));
        }

        if (!list.Any(s => s.IsDefault))
            throw new ArgumentException(
                "A step stencil must declare a DEFAULT level (a step with no selector) so every SMD pad "
                + "a zone or pad-set does not claim has a home — otherwise a pad could be printed on no "
                + "level (the partition would be broken).", nameof(steps));

        Steps = list;
    }

    /// <summary>Builds a step stencil from its ordered levels.</summary>
    public PasteStencil(params PasteStep[] steps) : this((IEnumerable<PasteStep>)steps) { }

    /// <summary>The ordered levels, first-match-wins for selection.</summary>
    public IReadOnlyList<PasteStep> Steps { get; }

    /// <summary>The DEFAULT (catch-all) level — the FIRST step with no selector (there is always one).</summary>
    public PasteStep DefaultLevel => Steps.First(s => s.IsDefault);

    /// <summary>The level a pad is assigned to: the FIRST step whose selector covers it (a step with no
    /// selector covers everything), so this always returns a level (the default at worst).</summary>
    internal PasteStep LevelFor(string source, CurvedRegion2d pad)
    {
        foreach (var step in Steps)
            if (step.Selector is null || step.Selector.Covers(source, pad))
                return step;
        return DefaultLevel;   // unreachable (there is always a default), but keeps the method total.
    }
}

/// <summary>One stencil aperture — the opening through which paste is deposited onto ONE SMD pad: its
/// source pad (<c>"R1.1"</c>) and the exact region (the pad grown by the paste expansion) the stencil is
/// cut to.</summary>
/// <param name="Source">The SMD pad this aperture prints paste onto.</param>
/// <param name="Region">The aperture region in board-local 2D coordinates (mm) — the pad grown by the
/// expansion.</param>
public readonly record struct PasteAperture(string Source, CurvedRegion2d Region);

/// <summary>One paste (stencil) Gerber's content — the outer copper <see cref="Layer"/> it prints over, the
/// <see cref="Side"/>, the apertures cut in it, and (for a step stencil) the <see cref="Level"/> that names
/// its foil thickness. The Gerber images the APERTURES (the openings), so it decodes back to the apertures
/// (see <see cref="GerberWriter.PasteLayer"/>). For a single (flat) stencil there is one content per side
/// with <see cref="Level"/> <c>null</c>; for a step stencil there is one content per (side, level).</summary>
/// <param name="Layer">The outer copper layer name (<c>"Top"</c>, <c>"Bottom"</c>).</param>
/// <param name="Side">The board face.</param>
/// <param name="Apertures">The stencil apertures for this (side, level), in copper-feature
/// declaration order.</param>
/// <param name="Level">The foil-thickness level this content is for (a step stencil), or <c>null</c> for a
/// single (flat) stencil. Names the Gerber file's thickness token.</param>
public sealed record PasteLayerContent(
    string Layer, CopperSide Side, IReadOnlyList<PasteAperture> Apertures, PasteStep? Level = null);

/// <summary>
/// The solder-paste (stencil) model, derived from a <see cref="PcbCopperModel"/>: a stencil aperture over
/// every SMD pad — and ONLY over SMD pads — each aperture the pad grown by an expansion. It is the
/// reflow-assembly layer: paste is printed through the stencil onto the SMD lands, the parts are placed,
/// and reflow wets the joints. Like the solder mask it exists on the two OUTER copper layers only
/// (<see cref="PcbStackup.Top"/> / <see cref="PcbStackup.Bottom"/>).
///
/// <para><b>Two modes.</b> A SINGLE (flat) stencil (<see cref="For(PcbCopperModel, PcbPasteSettings)"/>)
/// cuts every aperture from ONE foil at ONE expansion — <see cref="Layers"/> is one content per side. A
/// STEP (multi-level) stencil (<see cref="For(PcbCopperModel, PasteStencil)"/>) assigns each pad to a
/// foil-thickness LEVEL and cuts its aperture at that level's expansion — <see cref="Layers"/> is one
/// content per (side, level), the fab milling a separate depth (and consuming a separate Gerber) per level.
/// A single-level step at the default expansion produces the same apertures as a flat stencil.</para>
///
/// <para><b>SMD pads ONLY — the SMD-only rule.</b> A through-hole / plated pad gets NO paste (through-hole
/// parts are wave- or hand-soldered, so a stencil aperture over one would only foul the barrel), which is
/// the classic bug this layer must not have — and a step stencil must not start pasting through-hole pads
/// either. Which pads are SMD is read from the copper model itself: a pad is a COMPONENT pad (not a trace,
/// pour or via — the mask's own distinction) that carries NO DRILL (its source is not among the model's
/// <see cref="PcbCopperModel.Drills"/>). A via gets no paste EVER — unlike the mask there is no via policy
/// here, because paste on a via wicks solder down the barrel.</para>
///
/// <para><b>The aperture is EXACT — the pad grown by the expansion</b> (<see cref="CurvedRegion2dOffset"/>,
/// round joins), so a round pad's aperture is a disc of radius r + expansion (area π(r+e)²) — with the
/// default negative expansion a disc SMALLER than the pad — and a rectangular pad's is a rounded rectangle.
/// A step level only changes WHICH expansion, never how the aperture is computed, so that exactness is the
/// oracle in both modes: the decoded Gerber aperture equals the pad grown by that level's expansion to the
/// region grade.</para>
///
/// <para><b>The Gerber convention</b>: a paste Gerber images the stencil APERTURES (the openings) as dark
/// — the same positive-openings form the mask uses — so a paste Gerber decodes back to the apertures, and
/// the stencil is cut where the Gerber is dark.</para>
/// </summary>
public sealed class PcbPaste
{
    private PcbPaste(
        IReadOnlyList<PasteLayerContent> layers, PasteLayerContent top, PasteLayerContent bottom,
        PcbPasteSettings settings, PasteStencil? stencil)
    {
        Layers = layers;
        Top = top;
        Bottom = bottom;
        Settings = settings;
        Stencil = stencil;
    }

    /// <summary>The paste layers to write — one content per side for a flat stencil (<c>[Top, Bottom]</c>),
    /// one per (side, level) for a step stencil (only levels that carry at least one aperture, so an empty
    /// level emits no Gerber). The export writes ONE Gerber per entry.</summary>
    public IReadOnlyList<PasteLayerContent> Layers { get; }

    /// <summary>The top-side apertures, all foil levels combined (<see cref="PasteLayerContent.Level"/>
    /// <c>null</c>) — the whole top stencil as a single content. For a flat stencil this IS the top layer;
    /// for a step stencil it is the union of every top-side level's apertures.</summary>
    public PasteLayerContent Top { get; }

    /// <summary>The bottom-side apertures, all foil levels combined — see <see cref="Top"/>.</summary>
    public PasteLayerContent Bottom { get; }

    /// <summary>The flat settings the paste was derived with (the defaults for a step stencil, where the
    /// per-level expansions live on <see cref="Stencil"/> instead).</summary>
    public PcbPasteSettings Settings { get; }

    /// <summary>The step declaration if this is a step (multi-level) stencil, else <c>null</c>.</summary>
    public PasteStencil? Stencil { get; }

    /// <summary><c>true</c> for a step (multi-level) stencil (<see cref="Stencil"/> is set).</summary>
    public bool IsStepped => Stencil is not null;

    /// <summary>
    /// Derives a SINGLE (flat) solder-paste stencil from a copper model: an aperture (the pad grown by
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
        var top = FlatAperturesOn(model, model.Board.Stackup.Top, CopperSide.Top, settings.Expansion, boardBounds);
        var bottom = FlatAperturesOn(model, model.Board.Stackup.Bottom, CopperSide.Bottom, settings.Expansion, boardBounds);
        return new PcbPaste([top, bottom], top, bottom, settings, stencil: null);
    }

    /// <summary>
    /// Derives a STEP (multi-level) solder-paste stencil from a copper model: each SMD pad is assigned to
    /// the FIRST level whose selector covers it (else the default level), and its aperture is the pad grown
    /// by THAT level's expansion. <see cref="Layers"/> is one content per (side, level) that carries an
    /// aperture; the export writes one Gerber per level. Through-hole pads and vias get no aperture (the
    /// SMD-only rule) — a step stencil must not paste a through-hole pad.
    /// </summary>
    /// <exception cref="ArgumentException">An aperture lies entirely off the board (a pad placed off the
    /// board) — refused by name. (The stencil itself is validated at construction.)</exception>
    public static PcbPaste For(PcbCopperModel model, PasteStencil stencil)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(stencil);

        var boardBounds = BoardBounds(model.Board);
        var layers = new List<PasteLayerContent>();
        var top = SteppedAperturesOn(model, model.Board.Stackup.Top, CopperSide.Top, stencil, boardBounds, layers);
        var bottom = SteppedAperturesOn(model, model.Board.Stackup.Bottom, CopperSide.Bottom, stencil, boardBounds, layers);
        return new PcbPaste(layers, top, bottom, PcbPasteSettings.Default, stencil);
    }

    /// <summary>The paste apertures for an OUTER copper layer name, all foil levels combined, refused by
    /// name for any other layer (an inner layer, or a layer the board does not have) — the stencil exists
    /// on the outer copper only.</summary>
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

    // ---- the SMD pad set both modes share (so the two modes cover exactly the same pads) ----

    /// <summary>The SMD pad copper features on one outer layer, in copper-feature declaration order — a
    /// COMPONENT pad (not a trace, pour or via) that carries NO DRILL. Both the flat and stepped paths use
    /// this, so their pad sets are identical by construction (the partition's union equals the flat set).</summary>
    private static IEnumerable<CopperFeature> SmdPadsOn(PcbCopperModel model, CopperLayerSpec spec)
    {
        // The mask's own distinction of a COMPONENT pad — not a trace, a pour or a via land.
        var traceSources = new HashSet<string>(model.TraceSources, StringComparer.Ordinal);
        var pourSources = new HashSet<string>(model.PourSources, StringComparer.Ordinal);
        var viaSources = new HashSet<string>(model.Vias.Select(v => v.Source), StringComparer.Ordinal);
        // The SMD-only rule: a through-hole pad's source is a drilled hole (as are vias and board holes),
        // so it carries a drill and is excluded. A pad with no drill is SMD.
        var drilledSources = new HashSet<string>(model.Drills.Select(d => d.Source), StringComparer.Ordinal);

        foreach (var feature in model.Copper)
        {
            if (feature.Layer != spec.Name)
                continue;
            if (traceSources.Contains(feature.Source) || pourSources.Contains(feature.Source)
                || viaSources.Contains(feature.Source))
                continue;
            if (drilledSources.Contains(feature.Source))
                continue;   // a through-hole / plated pad is wave- or hand-soldered — no paste
            yield return feature;
        }
        // Deliberately no via policy here (unlike the mask): a via never gets paste, so vias are simply
        // excluded above and never reached.
    }

    private static PasteLayerContent FlatAperturesOn(
        PcbCopperModel model, CopperLayerSpec spec, CopperSide side, double expansion, Aabb boardBounds)
    {
        var apertures = new List<PasteAperture>();
        foreach (var feature in SmdPadsOn(model, spec))
            AddApertures(feature.Source, feature.Region, expansion, boardBounds, apertures);
        return new PasteLayerContent(spec.Name, side, apertures);
    }

    private static PasteLayerContent SteppedAperturesOn(
        PcbCopperModel model, CopperLayerSpec spec, CopperSide side, PasteStencil stencil,
        Aabb boardBounds, List<PasteLayerContent> layers)
    {
        // Assign each SMD pad to its level (first match wins), then grow it by that level's expansion — a
        // pad is on EXACTLY ONE level, so the apertures partition across levels.
        var byLevel = new Dictionary<PasteStep, List<PasteAperture>>();
        var combined = new List<PasteAperture>();
        foreach (var feature in SmdPadsOn(model, spec))
        {
            var level = stencil.LevelFor(feature.Source, feature.Region);
            if (!byLevel.TryGetValue(level, out var into))
                byLevel[level] = into = new List<PasteAperture>();
            AddApertures(feature.Source, feature.Region, level.Expansion, boardBounds, into);
        }

        // Emit one content per level (in stencil order, so the layer list is deterministic) that carries at
        // least one aperture — an empty level emits no Gerber.
        foreach (var level in stencil.Steps)
        {
            if (!byLevel.TryGetValue(level, out var apertures) || apertures.Count == 0)
                continue;
            layers.Add(new PasteLayerContent(spec.Name, side, apertures, level));
            combined.AddRange(apertures);
        }
        return new PasteLayerContent(spec.Name, side, combined);
    }

    private static void AddApertures(
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
