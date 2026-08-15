namespace EngrCAD.Cam;

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
    int BottomSolidLayers = 0)
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

        static void Require(double value, string name)
        {
            if (!(value > 0) || !double.IsFinite(value))
                throw new ArgumentException($"{name} must be finite and positive; got {value:0.###}.");
        }
    }
}
