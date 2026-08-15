namespace EngrCAD.Cam;

/// <summary>The sparse-infill pattern family.</summary>
public enum InfillPattern
{
    /// <summary>Parallel lines alternating ±45° per layer (the stage-1 default).</summary>
    Rectilinear,

    /// <summary>Both ±45° directions on EVERY layer, each at twice the spacing so the
    /// stated density holds.</summary>
    Grid,

    /// <summary>Three directions (0°/60°/120°) per layer at three times the spacing.</summary>
    Triangles,

    /// <summary>Inward offsets of the infill core, one spacing apart — closed loops.</summary>
    Concentric,

    /// <summary>The gyroid TPMS's own level set sectioned at each layer's z — the implicit
    /// engine's surface, so the pattern is genuinely three-dimensional.</summary>
    Gyroid,

    /// <summary>The tiled Hilbert curve (`SpaceFillingInfill`) — one connected path, no
    /// preferred direction.</summary>
    Hilbert,
}

/// <summary>Where a closed wall loop starts — the visible seam.</summary>
public enum SeamPosition
{
    /// <summary>The offset output's own first vertex (the stated stage-1 convention).</summary>
    Free,

    /// <summary>The rearmost vertex (max Y, ties to max X) — seams collect at the back.</summary>
    Rear,

    /// <summary>The vertex nearest a fixed anchor (the part bounds' +X midline), so seams
    /// line up vertically layer over layer.</summary>
    Aligned,
}

/// <summary>
/// An FDM printer/process profile — the numbers a slice is a function of. Distances are model
/// millimetres, speeds mm/s (converted to G-code's mm/min at the writer), temperatures °C with
/// <b>0 meaning "not written"</b> (the write-only-when-stated convention: a profile stating no
/// temperature produces a G-code file with no temperature commands, never a zero that would cool
/// a live hotend).
///
/// <para><b>The bead model is the STADIUM cross-section</b> — a rectangle of width
/// (w − h) capped by two half-circles of diameter h, area <c>h·(w − h) + π·h²/4</c> — the
/// standard slicer model of a bead squashed under the nozzle, and the ONE place the
/// extrusion arithmetic lives: every E value in the G-code is
/// <c>path length × BeadArea / FilamentArea</c>, so the extrusion bookkeeping is an identity a
/// decoder can re-check rather than a calibration factor.</para>
///
/// <para><b>Supports follow the same 0-means-off convention</b>:
/// <c>SupportOverhangAngle</c> is the overhang threshold in degrees (0 = no supports —
/// the write-only-when-stated default, so a profile stating nothing slices byte-identically),
/// <c>SupportSpacing</c> the distance between support lines, and <c>SupportGap</c> the XY
/// clearance a support path's centreline keeps from the part's own section boundary.</para>
/// </summary>
public sealed record PrinterProfile(
    double NozzleDiameter = 0.4,
    double FilamentDiameter = 1.75,
    double LayerHeight = 0.2,
    double? BeadWidth = null,
    int WallCount = 2,
    double InfillDensity = 0.2,
    double PrintSpeed = 40,
    double TravelSpeed = 120,
    double RetractionLength = 1.0,
    double RetractionSpeed = 35,
    double MinTravelForRetraction = 2.0,
    int HotendTemperature = 205,
    int BedTemperature = 60,
    double BrimWidth = 0,
    int SkirtLoops = 0,
    double SkirtGap = 5,
    double SupportOverhangAngle = 0,
    double SupportSpacing = 2.5,
    double SupportGap = 0.8,
    int TopSolidLayers = 0,
    int BottomSolidLayers = 0,
    double? WallSpeed = null,
    double? InfillSpeed = null,
    double? SolidInfillSpeed = null,
    double? SupportSpeed = null,
    double? FirstLayerSpeed = null,
    SeamPosition Seam = SeamPosition.Free,
    bool ExternalPerimetersFirst = false,
    bool SpiralVase = false,
    double MinLayerTime = 0,
    double MinPrintSpeed = 10,
    double FanSpeed = 0,
    int FanOffLayers = 1,
    double MaxVolumetricFlow = 0,
    double ZHop = 0,
    InfillPattern InfillPattern = InfillPattern.Rectilinear,
    double SupportZGap = 0,
    int SupportInterfaceLayers = 0,
    int RaftLayers = 0,
    double RaftMargin = 3,
    bool MonotonicSkins = false,
    double IroningFlow = 0,
    double IroningSpacing = 0,
    bool DetectBridges = false,
    double? BridgeSpeed = null,
    double ElephantFootCompensation = 0,
    double XYCompensation = 0,
    double HoleCompensation = 0,
    string? StartGcode = null,
    string? EndGcode = null,
    string? LayerChangeGcode = null,
    double FuzzySkinThickness = 0,
    double FuzzySkinSpacing = 0)
{
    /// <summary>The stock profile: 0.4 nozzle, 1.75 filament, 0.2 layers, two walls, 20% infill.</summary>
    public static PrinterProfile Default { get; } = new();

    /// <summary>The extrusion bead width (mm): the stated <see cref="BeadWidth"/>, or the
    /// nozzle diameter when none is stated.</summary>
    public double ResolvedBeadWidth => BeadWidth ?? NozzleDiameter;

    /// <summary>The bead's stadium cross-section area (mm²): <c>h·(w − h) + π·h²/4</c>.</summary>
    public double BeadArea =>
        LayerHeight * (ResolvedBeadWidth - LayerHeight)
        + Math.PI * LayerHeight * LayerHeight / 4;

    /// <summary>The filament's cross-section area (mm²).</summary>
    public double FilamentArea => Math.PI * FilamentDiameter * FilamentDiameter / 4;

    /// <summary>The ONE rule for a deposition path's speed (mm/s): a stated
    /// <see cref="FirstLayerSpeed"/> wins on layer 0 (adhesion wants slow, whatever the
    /// role); otherwise the role's own stated speed, a solid skin falling back through
    /// the infill family, everything else to <see cref="PrintSpeed"/>. Stating nothing
    /// resolves to <see cref="PrintSpeed"/> for every path — the write-only-when-stated
    /// convention, so a plain profile's G-code is byte-identical.</summary>
    public double SpeedFor(SlicePathRole role, int layerIndex)
    {
        if (layerIndex == 0 && FirstLayerSpeed is { } first)
            return first;
        return role switch
        {
            SlicePathRole.Wall => WallSpeed ?? PrintSpeed,
            SlicePathRole.Infill => InfillSpeed ?? PrintSpeed,
            SlicePathRole.SolidInfill => SolidInfillSpeed ?? InfillSpeed ?? PrintSpeed,
            SlicePathRole.Support => SupportSpeed ?? PrintSpeed,
            SlicePathRole.Bridge => BridgeSpeed ?? PrintSpeed,
            SlicePathRole.Ironing => SolidInfillSpeed ?? InfillSpeed ?? PrintSpeed,
            _ => PrintSpeed,
        };
    }

    /// <summary>Refuses an unusable profile BY NAME — a wrong number here prints plausibly and
    /// badly, so the refusal happens before any geometry is sliced.</summary>
    public void Validate()
    {
        Require(NozzleDiameter, nameof(NozzleDiameter));
        Require(FilamentDiameter, nameof(FilamentDiameter));
        Require(LayerHeight, nameof(LayerHeight));
        Require(ResolvedBeadWidth, nameof(BeadWidth));
        if (LayerHeight > ResolvedBeadWidth)
            throw new ArgumentException(
                $"The layer height ({LayerHeight:0.###} mm) exceeds the bead width "
                + $"({ResolvedBeadWidth:0.###} mm) — a bead cannot be taller than it is wide "
                + "(the stadium cross-section degenerates).");
        if (WallCount < 0)
            throw new ArgumentException($"WallCount must be non-negative; got {WallCount}.");
        if (!(InfillDensity >= 0) || InfillDensity > 1)
            throw new ArgumentException(
                $"InfillDensity must lie in [0, 1] (0 = none, 1 = solid); got {InfillDensity:0.###}.");
        Require(PrintSpeed, nameof(PrintSpeed));
        Require(TravelSpeed, nameof(TravelSpeed));
        if (RetractionLength < 0)
            throw new ArgumentException(
                $"RetractionLength must be non-negative (0 = off); got {RetractionLength:0.###}.");
        if (RetractionLength > 0)
            Require(RetractionSpeed, nameof(RetractionSpeed));
        if (MinTravelForRetraction < 0)
            throw new ArgumentException(
                $"MinTravelForRetraction must be non-negative; got {MinTravelForRetraction:0.###}.");
        if (BrimWidth < 0)
            throw new ArgumentException(
                $"BrimWidth must be non-negative (0 = no brim); got {BrimWidth:0.###}.");
        if (SkirtLoops < 0)
            throw new ArgumentException(
                $"SkirtLoops must be non-negative (0 = no skirt); got {SkirtLoops}.");
        if (SkirtLoops > 0)
            Require(SkirtGap, nameof(SkirtGap));
        if (SupportOverhangAngle < 0 || SupportOverhangAngle > 90 || !double.IsFinite(SupportOverhangAngle))
            throw new ArgumentException(
                "SupportOverhangAngle must lie in [0, 90] degrees (0 = no supports, 90 = only "
                + $"true ceilings); got {SupportOverhangAngle:0.###}.");
        if (SupportOverhangAngle > 0)
        {
            Require(SupportSpacing, nameof(SupportSpacing));
            if (SupportGap < 0 || !double.IsFinite(SupportGap))
                throw new ArgumentException(
                    $"SupportGap must be non-negative; got {SupportGap:0.###}.");
        }
        if (TopSolidLayers < 0)
            throw new ArgumentException(
                $"TopSolidLayers must be non-negative (0 = no top skins); got {TopSolidLayers}.");
        if (BottomSolidLayers < 0)
            throw new ArgumentException(
                $"BottomSolidLayers must be non-negative (0 = no bottom skins); got {BottomSolidLayers}.");
        RequireStated(WallSpeed, nameof(WallSpeed));
        RequireStated(InfillSpeed, nameof(InfillSpeed));
        RequireStated(SolidInfillSpeed, nameof(SolidInfillSpeed));
        RequireStated(SupportSpeed, nameof(SupportSpeed));
        RequireStated(FirstLayerSpeed, nameof(FirstLayerSpeed));
        if (MinLayerTime < 0 || !double.IsFinite(MinLayerTime))
            throw new ArgumentException(
                $"MinLayerTime must be non-negative (0 = no cooling slowdown); got {MinLayerTime:0.###}.");
        if (MinLayerTime > 0)
            Require(MinPrintSpeed, nameof(MinPrintSpeed));
        if (FanSpeed < 0 || FanSpeed > 1 || !double.IsFinite(FanSpeed))
            throw new ArgumentException(
                $"FanSpeed must lie in [0, 1] (0 = no fan commands); got {FanSpeed:0.###}.");
        if (FanOffLayers < 0)
            throw new ArgumentException(
                $"FanOffLayers must be non-negative; got {FanOffLayers}.");
        if (MaxVolumetricFlow < 0 || !double.IsFinite(MaxVolumetricFlow))
            throw new ArgumentException(
                $"MaxVolumetricFlow must be non-negative (0 = no cap); got {MaxVolumetricFlow:0.###}.");
        if (ZHop < 0 || !double.IsFinite(ZHop))
            throw new ArgumentException(
                $"ZHop must be non-negative (0 = no hop); got {ZHop:0.###}.");
        if (SupportZGap < 0 || !double.IsFinite(SupportZGap))
            throw new ArgumentException(
                $"SupportZGap must be non-negative (0 = supports touch the underside); got {SupportZGap:0.###}.");
        if (SupportInterfaceLayers < 0)
            throw new ArgumentException(
                $"SupportInterfaceLayers must be non-negative; got {SupportInterfaceLayers}.");
        if (RaftLayers < 0)
            throw new ArgumentException(
                $"RaftLayers must be non-negative (0 = no raft); got {RaftLayers}.");
        if (RaftLayers > 0)
            Require(RaftMargin, nameof(RaftMargin));
        if (IroningFlow < 0 || IroningFlow > 1 || !double.IsFinite(IroningFlow))
            throw new ArgumentException(
                $"IroningFlow must lie in [0, 1] (0 = no ironing); got {IroningFlow:0.###}.");
        if (IroningFlow > 0 && TopSolidLayers < 1)
            throw new ArgumentException(
                "IroningFlow needs TopSolidLayers >= 1 — ironing smooths a top skin, and "
                + "without skins there is nothing to iron.");
        if (IroningSpacing < 0 || !double.IsFinite(IroningSpacing))
            throw new ArgumentException(
                $"IroningSpacing must be non-negative (0 = a third of the bead); got {IroningSpacing:0.###}.");
        RequireStated(BridgeSpeed, nameof(BridgeSpeed));
        if (ElephantFootCompensation < 0 || !double.IsFinite(ElephantFootCompensation))
            throw new ArgumentException(
                $"ElephantFootCompensation must be non-negative; got {ElephantFootCompensation:0.###}.");
        if (!double.IsFinite(XYCompensation))
            throw new ArgumentException("XYCompensation must be finite.");
        if (HoleCompensation < 0 || !double.IsFinite(HoleCompensation))
            throw new ArgumentException(
                $"HoleCompensation must be non-negative; got {HoleCompensation:0.###}.");
        if (FuzzySkinThickness < 0 || !double.IsFinite(FuzzySkinThickness))
            throw new ArgumentException(
                $"FuzzySkinThickness must be non-negative (0 = off); got {FuzzySkinThickness:0.###}.");
        if (FuzzySkinSpacing < 0 || !double.IsFinite(FuzzySkinSpacing))
            throw new ArgumentException(
                $"FuzzySkinSpacing must be non-negative (0 = 0.8 of the bead); got {FuzzySkinSpacing:0.###}.");
        if (SpiralVase)
        {
            if (WallCount != 1)
                throw new ArgumentException(
                    $"SpiralVase needs exactly one wall (a vase IS its single continuous "
                    + $"perimeter); got WallCount {WallCount}.");
            if (InfillDensity > 0)
                throw new ArgumentException(
                    "SpiralVase and infill contradict — a continuous spiral wall never "
                    + "returns to fill an interior; state InfillDensity 0.");
            if (TopSolidLayers > 0)
                throw new ArgumentException(
                    "SpiralVase has no top to close (the spiral simply ends); state "
                    + "TopSolidLayers 0. Bottom layers are legal — the vase's base.");
            if (SupportOverhangAngle > 0)
                throw new ArgumentException(
                    "SpiralVase and supports contradict — a support tower would interrupt "
                    + "the continuous wall; state SupportOverhangAngle 0.");
        }

        static void RequireStated(double? value, string name)
        {
            if (value is { } stated && (!(stated > 0) || !double.IsFinite(stated)))
                throw new ArgumentException(
                    $"{name} must be finite and positive when stated; got {stated:0.###}.");
        }

        static void Require(double value, string name)
        {
            if (!(value > 0) || !double.IsFinite(value))
                throw new ArgumentException($"{name} must be finite and positive; got {value:0.###}.");
        }
    }
}
