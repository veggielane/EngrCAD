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
    /// <summary>Minimum web between two vias' drilled holes (mm), edge to edge — a MANUFACTURING
    /// spacing between drills, so it applies to ALL via pairs regardless of net (electrical clearance
    /// between different-net via PADS is the general <see cref="MinCopperClearance"/>, which via
    /// annular pads ride as ordinary copper). Not on the positional constructor (a value with a
    /// default), so a stage-4 caller building a rule set the six-argument way is unaffected; ≈ 8 mil
    /// at the default. A via annular ring uses <see cref="MinAnnularRing"/> (a via is a drilled pad),
    /// and a via pad / drill to different-net copper uses <see cref="MinCopperClearance"/> /
    /// <see cref="MinDrillToCopper"/> — a via pad is copper and a via drill is a drilled hole.</summary>
    public double MinViaToVia { get; init; } = 0.2;

    /// <summary>
    /// Nominal defaults (⚠ verify against your fabricator's datasheet): 0.15 mm (≈ 6 mil)
    /// clearance, trace width and annular ring; 0.2 mm drill-to-copper; 0.25 mm copper-to-edge;
    /// a 90° acid-trap threshold. These are the same Class-2-ish figures <see cref="ForIpcClass"/>
    /// gives for class 2.
    /// </summary>
    public static DrcRuleSet Default { get; } = new(
        MinCopperClearance: 0.15,
        MinTraceWidth: 0.15,
        MinAnnularRing: 0.15,
        MinDrillToCopper: 0.2,
        MinCopperToEdge: 0.25,
        MinAcuteAngleDegrees: 90);

    /// <summary>
    /// The nominal rule set for an IPC-6012 performance class (1, 2 or 3), following the IPC
    /// producibility levels (Level A ↔ class 1, Level B ↔ class 2, Level C ↔ class 3). A stricter
    /// class REQUIRES more copper — a larger clearance, annular ring and edge keep-out — so **every
    /// minimum GROWS with the class and class 3 is the strictest** (the DRC flags progressively
    /// more), which is the IPC-6012 direction for a minimum annular ring exactly (Level C leaves the
    /// most copper). Class 2 is the Class-2-ish <see cref="Default"/>, so the values spread around it.
    ///
    /// <para>These are the REQUIRED design minimums a board must satisfy to be fabricated to that
    /// class — the reliability lens, not the fabricator-CAPABILITY lens (how fine a process can go,
    /// which tightens the other way). A DRC minimum is a floor the design must clear, so a stricter
    /// class is a HIGHER floor here.</para>
    ///
    /// <para><b>⚠ Nominal transcribed figures — verify against your fabricator's capability sheet</b>
    /// (flagged like <c>StandardHoles</c> / <c>SheetMaterials</c> / <see cref="Default"/>). The
    /// class→level mapping is a nominal convention (the task's "roughly class 1/2/3"), the acid-trap
    /// angle is the same 90° at every class, and every length is in the model's millimetres so the
    /// preset scales with <see cref="Scaled"/>.</para>
    /// </summary>
    /// <param name="producibilityClass">The IPC-6012 class: 1 (general), 2 (dedicated service) or
    /// 3 (high reliability).</param>
    /// <exception cref="ArgumentOutOfRangeException">The class is not 1, 2 or 3.</exception>
    public static DrcRuleSet ForIpcClass(int producibilityClass) => producibilityClass switch
    {
        // Class 1 (Level A) — general electronic products, the loosest floor.
        1 => new(
            MinCopperClearance: 0.10,
            MinTraceWidth: 0.10,
            MinAnnularRing: 0.10,
            MinDrillToCopper: 0.15,
            MinCopperToEdge: 0.20,
            MinAcuteAngleDegrees: 90) { MinViaToVia = 0.15 },
        // Class 2 (Level B) — dedicated-service electronics; the Class-2-ish Default figures
        // (this branch is field-identical to Default, asserted by test).
        2 => new(
            MinCopperClearance: 0.15,
            MinTraceWidth: 0.15,
            MinAnnularRing: 0.15,
            MinDrillToCopper: 0.20,
            MinCopperToEdge: 0.25,
            MinAcuteAngleDegrees: 90) { MinViaToVia = 0.20 },
        // Class 3 (Level C) — high-reliability (aerospace/medical), the strictest floor.
        3 => new(
            MinCopperClearance: 0.20,
            MinTraceWidth: 0.20,
            MinAnnularRing: 0.20,
            MinDrillToCopper: 0.25,
            MinCopperToEdge: 0.30,
            MinAcuteAngleDegrees: 90) { MinViaToVia = 0.25 },
        _ => throw new ArgumentOutOfRangeException(nameof(producibilityClass), producibilityClass,
            "An IPC-6012 producibility class must be 1, 2 or 3."),
    };

    /// <summary>
    /// Cross-checks a <see cref="PcbFabricationSpec"/>'s own stated minimum trace width and clearance
    /// against the IPC-6012 class it CLAIMS (<see cref="PcbFabricationSpec.Ipc6012Class"/>). A spec
    /// claiming a strict class but stating a minimum trace/clearance LOOSER (finer) than that class's
    /// floor (<see cref="ForIpcClass"/>) is inconsistent — it names the strict class yet permits
    /// features below its minimum — so that mismatch is FLAGGED naming the stated value and the class
    /// minimum. A spec whose stated minimums MEET (are at least as tight as) its class conforms.
    ///
    /// <para>A spec that states no class, or a class but no minimum trace or clearance, has nothing
    /// to check: the result is <see cref="IpcClassCheckResult.NotCheckable"/> with a reason — it is
    /// never invented into a pass or a fail.</para>
    /// </summary>
    public static IpcClassCheck CheckSpec(PcbFabricationSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.Ipc6012Class is not { } cls)
            return new IpcClassCheck(IpcClassCheckResult.NotCheckable, null, [],
                "The spec states no IPC-6012 class, so there is nothing to cross-check.");

        if (spec.MinTraceWidthMm is null && spec.MinClearanceMm is null)
            return new IpcClassCheck(IpcClassCheckResult.NotCheckable, cls, [],
                $"The spec claims IPC-6012 class {cls} but states no minimum trace width or "
                + "clearance to check against it.");

        var classRules = ForIpcClass(cls);
        var issues = new List<string>();

        if (spec.MinTraceWidthMm is { } trace && trace < classRules.MinTraceWidth)
            issues.Add(
                $"the spec's minimum trace width {trace:g6} mm is looser than IPC-6012 class {cls}'s "
                + $"minimum of {classRules.MinTraceWidth:g6} mm");

        if (spec.MinClearanceMm is { } clearance && clearance < classRules.MinCopperClearance)
            issues.Add(
                $"the spec's minimum clearance {clearance:g6} mm is looser than IPC-6012 class {cls}'s "
                + $"minimum of {classRules.MinCopperClearance:g6} mm");

        return issues.Count == 0
            ? new IpcClassCheck(IpcClassCheckResult.Conforming, cls, [],
                $"The spec's stated minimums meet IPC-6012 class {cls}.")
            : new IpcClassCheck(IpcClassCheckResult.NonConforming, cls, issues,
                $"The spec claims IPC-6012 class {cls} but {issues.Count} stated minimum(s) do not "
                + "meet it: " + string.Join("; ", issues) + ".");
    }

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
            MinViaToVia = MinViaToVia * factor,
        };
    }
}

/// <summary>The outcome of <see cref="DrcRuleSet.CheckSpec"/> — whether a fabrication spec's own
/// stated minimums are consistent with the IPC-6012 class it claims.</summary>
public enum IpcClassCheckResult
{
    /// <summary>Nothing to check — the spec states no IPC-6012 class, or a class but no minimum
    /// trace width or clearance to compare against it. Never invented into a pass or a fail.</summary>
    NotCheckable,

    /// <summary>Every stated minimum meets (is at least as tight as) the claimed class's floor.</summary>
    Conforming,

    /// <summary>A stated minimum is looser (finer) than the claimed class's floor — the spec names a
    /// strict class yet permits features below its minimum. <see cref="IpcClassCheck.Issues"/> names
    /// each.</summary>
    NonConforming,
}

/// <summary>
/// The result of cross-checking a <see cref="PcbFabricationSpec"/>'s stated minimum trace width and
/// clearance against the IPC-6012 class it claims (<see cref="DrcRuleSet.CheckSpec"/>). Names the
/// offending stated value and the class minimum in <see cref="Issues"/> (a report that only said
/// "fail" would be useless — the <c>PcbLayoutCheck</c> house style), and reports "not checkable"
/// with a reason rather than inventing a verdict.
/// </summary>
/// <param name="Result">Conforming, non-conforming, or not checkable.</param>
/// <param name="ClaimedClass">The IPC-6012 class the spec claimed, or null when it claimed none.</param>
/// <param name="Issues">One line per stated minimum that does not meet the class (empty when
/// conforming or not checkable), each naming the stated value and the class minimum.</param>
/// <param name="Summary">A one-line human-readable summary (the reason, when not checkable).</param>
public sealed record IpcClassCheck(
    IpcClassCheckResult Result,
    int? ClaimedClass,
    IReadOnlyList<string> Issues,
    string Summary)
{
    /// <summary>Whether the spec carried enough to cross-check (a class AND at least one minimum).</summary>
    public bool IsCheckable => Result != IpcClassCheckResult.NotCheckable;

    /// <summary>Whether every stated minimum meets the claimed class (true only for a checkable,
    /// conforming spec).</summary>
    public bool Conforms => Result == IpcClassCheckResult.Conforming;
}
