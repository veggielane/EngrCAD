namespace EngrCAD.Ecad;

/// <summary>
/// The standard PCB design-rule table the copper DRC (<see cref="PcbDrc"/>) checks against —
/// minimum copper-to-copper clearance, trace width, annular ring, drill-to-copper, copper-to-
/// board-edge, and an acute-angle / acid-trap threshold. Every length is in the model's
/// millimetres, so the rules scale with the board (a rule set and a board that pass still pass
/// after a uniform scale of both — see <see cref="Scaled"/>).
///
/// <para><b>Verify against your fabricator's datasheet.</b> The <see cref="Default"/> values are
/// nominal IPC-2221 Class-2-ish figures (⚠ transcribed, flagged like <c>StandardHoles</c> /
/// <c>SheetMaterials</c>), NOT a substitute for the capability sheet of the shop that will make
/// the board — a cheap 2-layer process runs looser and an HDI process tighter. Set every value
/// you care about.</para>
/// </summary>
/// <param name="MinCopperClearance">Minimum edge-to-edge gap between copper of DIFFERENT nets on
/// one layer (mm). The core rule; ≈ 6 mil at the default.</param>
/// <param name="MinTraceWidth">Minimum width of a copper conductor (mm). A pad is measured as
/// whatever width its copper carries; on a trace it is the trace width. ≈ 6 mil at the
/// default.</param>
/// <param name="MinAnnularRing">Minimum copper ring left around a drilled pad's hole (mm) —
/// <c>(min pad dimension − drill) / 2</c>.</param>
/// <param name="MinDrillToCopper">Minimum gap from a drilled hole's edge to OTHER-net copper on
/// any layer (mm) — a drill goes through the whole stack, so this is a cross-layer rule.</param>
/// <param name="MinCopperToEdge">Minimum gap from copper to the board OUTLINE (mm).</param>
/// <param name="MinAcuteAngleDegrees">The acid-trap threshold: a copper corner whose wedge angle
/// (the angle between the two edges meeting there, measured either side) is BELOW this flags. The
/// default 90° passes a pad's square 90° corners (strict inequality) and flags anything sharper —
/// the classic acute-angle rule; lower it for a board routed with 45° traces.</param>
public sealed record DrcRuleSet(
    double MinCopperClearance,
    double MinTraceWidth,
    double MinAnnularRing,
    double MinDrillToCopper,
    double MinCopperToEdge,
    double MinAcuteAngleDegrees)
{
    /// <summary>
    /// Nominal defaults (⚠ verify against your fabricator's datasheet): 0.15 mm (≈ 6 mil)
    /// clearance, trace width and annular ring; 0.2 mm drill-to-copper; 0.25 mm copper-to-edge;
    /// a 90° acid-trap threshold.
    /// </summary>
    public static DrcRuleSet Default { get; } = new(
        MinCopperClearance: 0.15,
        MinTraceWidth: 0.15,
        MinAnnularRing: 0.15,
        MinDrillToCopper: 0.2,
        MinCopperToEdge: 0.25,
        MinAcuteAngleDegrees: 90);

    /// <summary>
    /// The same rule set with every LENGTH multiplied by <paramref name="factor"/> (the angle is
    /// dimensionless and untouched), so a rule set that passes a board still passes the board
    /// scaled by the same factor — the relative-tolerance / epsilon-ladder property the whole DRC
    /// rests on.
    /// </summary>
    public DrcRuleSet Scaled(double factor)
    {
        if (!(factor > 0))
            throw new ArgumentOutOfRangeException(nameof(factor), "A scale factor must be positive.");
        return this with
        {
            MinCopperClearance = MinCopperClearance * factor,
            MinTraceWidth = MinTraceWidth * factor,
            MinAnnularRing = MinAnnularRing * factor,
            MinDrillToCopper = MinDrillToCopper * factor,
            MinCopperToEdge = MinCopperToEdge * factor,
        };
    }
}
