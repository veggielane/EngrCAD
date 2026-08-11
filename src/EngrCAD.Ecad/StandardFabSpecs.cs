namespace EngrCAD.Ecad;

/// <summary>
/// A small catalogue of named <see cref="PcbFabricationSpec"/> presets — common "house" fabrication
/// specifications — so a caller can pick a standard stack-up rather than typing the material,
/// finish, copper weight, colours, class and minimum trace/clearance by hand. It is exactly the
/// <c>StandardHoles</c> / <c>SheetMaterials</c> / <see cref="DrcRuleSet.ForIpcClass"/> pattern:
/// a catalogue entry is an ORDINARY <see cref="PcbFabricationSpec"/> a caller passes to
/// <see cref="PcbLayout.WithFabrication"/>, so it persists and drives the fabrication drawing
/// through the machinery that already exists — there is no second application path.
///
/// <para><b>⚠ Nominal transcribed figures — verify against your fabricator's capability sheet.</b>
/// A fabrication spec is a property of the board house, the process and the order together, so the
/// authority is the shop that will make the board, not this file. These are the near-universal
/// defaults a typical prototype/small-volume FR-4 or flex fabricator ships when you say nothing:
/// FR-4, 1.6 mm (62 mil) finished, 1 oz (35 µm) outer copper, a green LPI solder mask with a white
/// legend, and a 6 mil (0.15 mm) minimum trace and space — the IPC-6012 class-2 floor. They exist
/// so a design can NAME a house standard instead of restating it.</para>
///
/// <para><b>The spec carries no LAYER COUNT — that lives on the board's stackup</b>
/// (<see cref="PcbBoard"/> / <see cref="LayerStackup"/>). A "2-layer" or "4-layer" name here is
/// descriptive of the intended board; what the spec itself captures is the fabrication REQUIREMENTS
/// such a board is typically built to (finish, class, required-minimum copper). So the two 2-layer
/// entries differ by FINISH (the single most common real distinction, HASL vs ENIG), and the
/// 4-layer entry is a higher-reliability class-3 build.</para>
///
/// <para>Every entry states the same nine core fields (material, finished thickness, copper weight,
/// surface finish, both colours, class, minimum trace and clearance), so none is half-filled, and
/// every entry's stated minimums MEET the IPC-6012 class it claims — <see cref="DrcRuleSet.CheckSpec"/>
/// reports each one <see cref="IpcClassCheckResult.Conforming"/>. To vary a preset, use the record
/// <c>with</c> expression (e.g. <c>StandardFabSpecs.TwoLayerFr4Enig with { SolderMaskColour = "Black" }</c>).</para>
/// </summary>
public static class StandardFabSpecs
{
    /// <summary>
    /// The workhorse economy prototype: <b>2-layer FR-4, 1.6 mm, 1 oz copper, lead-free HASL, green
    /// mask / white legend, IPC-6012 class 2</b>, 0.15 mm (6 mil) minimum trace and clearance. The
    /// near-universal default a low-cost fabricator ships when a RoHS-compliant leaded-free finish is
    /// wanted and pad flatness does not matter (leaded HASL is <see cref="PcbSurfaceFinish.Hasl"/> if
    /// you need it). ⚠ verify against your fabricator's datasheet.
    /// </summary>
    public static PcbFabricationSpec TwoLayerFr4Hasl { get; } = new()
    {
        BaseMaterial = "FR-4",
        FinishedThicknessMm = 1.6,
        CopperWeightOz = 1,
        SurfaceFinish = PcbSurfaceFinish.HaslLeadFree,
        SolderMaskColour = "Green",
        SilkscreenColour = "White",
        Ipc6012Class = 2,
        MinTraceWidthMm = 0.15,
        MinClearanceMm = 0.15,
    };

    /// <summary>
    /// The fine-pitch / RoHS prototype: <b>2-layer FR-4, 1.6 mm, 1 oz copper, ENIG, green mask /
    /// white legend, IPC-6012 class 2</b>, 0.15 mm (6 mil) minimum trace and clearance. Identical to
    /// <see cref="TwoLayerFr4Hasl"/> except for the FINISH — ENIG's flat gold pads suit fine-pitch
    /// and BGA parts, press-fit and long shelf life, where HASL's domed pads do not. ⚠ verify against
    /// your fabricator's datasheet.
    /// </summary>
    public static PcbFabricationSpec TwoLayerFr4Enig { get; } = new()
    {
        BaseMaterial = "FR-4",
        FinishedThicknessMm = 1.6,
        CopperWeightOz = 1,
        SurfaceFinish = PcbSurfaceFinish.Enig,
        SolderMaskColour = "Green",
        SilkscreenColour = "White",
        Ipc6012Class = 2,
        MinTraceWidthMm = 0.15,
        MinClearanceMm = 0.15,
    };

    /// <summary>
    /// The high-reliability multilayer: <b>4-layer FR-4, 1.6 mm, 1 oz outer copper, ENIG, green mask
    /// / white legend, IPC-6012 class 3</b>, 0.20 mm (8 mil) minimum trace and clearance. The layer
    /// count itself is the board's stackup (<see cref="LayerStackup.FourLayer"/>); what this spec
    /// states beyond a 2-layer ENIG board is the tighter class-3 reliability floor (larger annular
    /// rings and required-minimum copper) a serious 4-layer product is usually built to. ⚠ verify
    /// against your fabricator's datasheet.
    /// </summary>
    public static PcbFabricationSpec FourLayerFr4Enig { get; } = new()
    {
        BaseMaterial = "FR-4",
        FinishedThicknessMm = 1.6,
        CopperWeightOz = 1,
        SurfaceFinish = PcbSurfaceFinish.Enig,
        SolderMaskColour = "Green",
        SilkscreenColour = "White",
        Ipc6012Class = 3,
        MinTraceWidthMm = 0.20,
        MinClearanceMm = 0.20,
    };

    /// <summary>
    /// A common 2-layer flex: <b>polyimide, 0.1 mm finished, 0.5 oz copper, ENIG, amber coverlay /
    /// white legend, IPC-6012 class 2</b>, 0.15 mm minimum trace and clearance. Flex differs in the
    /// stuff (polyimide, not FR-4), the thickness (thin), the copper weight (½ oz rolled-annealed),
    /// and the finish (ENIG, since HASL's thermal shock is unfriendly to thin flex). Its "solder
    /// mask" is the amber polyimide COVERLAY the <c>SolderMaskColour</c> field records as
    /// <c>"Yellow"</c>. ⚠ verify against your fabricator's datasheet.
    /// </summary>
    public static PcbFabricationSpec FlexPolyimideEnig { get; } = new()
    {
        BaseMaterial = "Polyimide (flex)",
        FinishedThicknessMm = 0.1,
        CopperWeightOz = 0.5,
        SurfaceFinish = PcbSurfaceFinish.Enig,
        SolderMaskColour = "Yellow",
        SilkscreenColour = "White",
        Ipc6012Class = 2,
        MinTraceWidthMm = 0.15,
        MinClearanceMm = 0.15,
    };

    /// <summary>Every catalogue entry, in declaration order, each paired with its property name — so
    /// a caller (or a coverage test) can enumerate the catalogue.</summary>
    public static IReadOnlyList<(string Name, PcbFabricationSpec Spec)> All { get; } =
    [
        (nameof(TwoLayerFr4Hasl), TwoLayerFr4Hasl),
        (nameof(TwoLayerFr4Enig), TwoLayerFr4Enig),
        (nameof(FourLayerFr4Enig), FourLayerFr4Enig),
        (nameof(FlexPolyimideEnig), FlexPolyimideEnig),
    ];
}
