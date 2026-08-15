using EngrCAD.Core;

namespace EngrCAD.Cam;

/// <summary>
/// HSM (high-speed machining) toolpaths — CAM stage 4, whose defining invariant is the
/// ENGAGEMENT ANGLE: the arc of tool circumference in material at each instant. A
/// conventional slot cut buries the tool half-in (180° plus the sides), which is why
/// slotting is where cutters die; a TROCHOIDAL slot keeps the engagement bounded by
/// construction — the tool rides circular loops that advance a small step per
/// revolution, so each loop shaves a thin crescent off material the previous loops
/// already opened.
///
/// <para><b>The advance per revolution is DERIVED from the stated engagement bound</b>,
/// not tuned: a tool of radius r cutting a radial width a engages
/// <c>φ = acos((r − a)/r)</c> of its circumference, so a stated maximum φ gives
/// <c>a = r·(1 − cos φ)</c> — and the entry is an Archimedean SPIRAL-OUT at the same
/// pitch (radius grows one advance per turn), so the bound holds from the first loop
/// rather than from wherever a plunged first circle stops being buried. The campaign's
/// own bar is that this is MEASURED, never inferred from "it looks smooth": the tests
/// compute the engagement from the evolving stock — the tool-circle arc not yet covered
/// by the path's own swept prefix — and hold it under the stated maximum, with a
/// straight-line slot cut as the ~180° control.</para>
///
/// <para>Filed with the campaign: general adaptive (constant-engagement) POCKET
/// clearing — the medial-axis-guided spiral over the evolving stock region, of which
/// this closed-form cycloid family is the honest first step — plus helical (ramped)
/// z entry, and trochoidal linking of necks found by <c>Region2dThickness</c>.</para>
/// </summary>
public static class CncHsm
{
    /// <summary>
    /// A trochoidal slot from <paramref name="start"/> to <paramref name="end"/> of
    /// <paramref name="slotWidth"/> (the finished slot's width, tool included): an
    /// Archimedean spiral-out at the derived pitch, then circular loops advancing that
    /// pitch per revolution, one finishing loop at the far end, repeated per
    /// <c>StepDown</c> depth level with the last clamped to the stated depth. The z
    /// entry at each level is a plunge (helical entry is filed).
    /// </summary>
    public static MillOperation TrochoidalSlot(
        Vector2d start, Vector2d end, double slotWidth, MillTool tool, double depth,
        double maxEngagementDegrees = 60, int samplesPerLoop = 36,
        string name = "trochoidal slot")
    {
        ArgumentNullException.ThrowIfNull(tool);
        tool.Validate();
        if (!(depth > 0) || !double.IsFinite(depth))
            throw new ArgumentException($"'{name}': depth must be finite and positive; got {depth:0.###}.");
        if (!(slotWidth > tool.Diameter))
            throw new ArgumentException(
                $"'{name}': the slot width ({slotWidth:0.###}) must exceed the tool diameter "
                + $"({tool.Diameter:0.###}) — a slot at tool width has no room to trochoid and "
                + "is a plain profile cut.");
        if (!(maxEngagementDegrees > 0) || maxEngagementDegrees > 180)
            throw new ArgumentException(
                $"'{name}': maxEngagementDegrees must lie in (0, 180]; got {maxEngagementDegrees:0.###}.");
        if (samplesPerLoop < 8)
            throw new ArgumentException(
                $"'{name}': samplesPerLoop must be at least 8; got {samplesPerLoop}.");
        double length = (end - start).Length;
        if (!(length > 0))
            throw new ArgumentException($"'{name}': the slot has no length.");

        double loopRadius = (slotWidth - tool.Diameter) / 2;
        double advance = AdvanceFor(
            tool.Radius, loopRadius, maxEngagementDegrees, samplesPerLoop);

        var direction = (end - start) / length;
        double step = 2 * Math.PI / samplesPerLoop;
        double spiralEnd = 2 * Math.PI * loopRadius / advance;   // theta where the spiral reaches full radius
        double totalTheta = spiralEnd + 2 * Math.PI * length / advance + 2 * Math.PI;

        // Sampled by INDEX, never by accumulating theta: totalTheta can be an exact
        // multiple of the step (it is, for round fixtures), and an accumulated loop then
        // emits a final segment a few ulps long — too short for the stroke's normalize,
        // too long for exact-duplicate compaction (the epsilon-guard-Ceiling lesson).
        int steps = (int)Math.Ceiling(totalTheta / step - 1e-9);
        var loop = new List<Vector2d>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            double t = Math.Min(i * step, totalTheta);
            double radius = Math.Min(advance * t / (2 * Math.PI), loopRadius);
            double along = Math.Clamp(advance * (t - spiralEnd) / (2 * Math.PI), 0, length);
            loop.Add(start + direction * along
                + new Vector2d(radius * Math.Cos(t), radius * Math.Sin(t)));
        }

        var passes = new List<MillPass>();
        foreach (double level in CncMill.DepthLevels(depth, tool.StepDown))
            passes.Add(new MillPass(
                [.. loop.Select(p => new Vector3d(p.X, p.Y, level))], IsClosed: false));
        return new MillOperation(name, tool, passes);
    }

    /// <summary>
    /// The advance per revolution that MEETS the stated engagement bound, solved by
    /// bisection against a steady-state model rather than taken from the straight-cut
    /// relation <c>a = r·(1 − cos φ)</c> — which is measurably WRONG here (it reads 60°
    /// where the evolving stock measures 90°), because a trochoid cuts against the
    /// previous loop's CONVEX swept boundary, and a convex opposing surface engages more
    /// of the circumference than a straight wall at the same radial width. The model is
    /// the same rule the tests measure with — several loops at the candidate advance,
    /// the last loop's tool-circle arc not covered by the earlier sweep — so the bound
    /// is met by the construction the verification independently re-measures.
    /// </summary>
    private static double AdvanceFor(
        double toolRadius, double loopRadius, double maxEngagementDegrees, int samplesPerLoop)
    {
        double bound = maxEngagementDegrees * Math.PI / 180;
        double lo = 0, hi = 2 * toolRadius;
        for (int iteration = 0; iteration < 32; iteration++)
        {
            double f = 0.5 * (lo + hi);
            if (SteadyEngagement(toolRadius, loopRadius, f, samplesPerLoop) > bound)
                hi = f;
            else
                lo = f;
        }
        return lo > 0 ? lo : hi * 1e-6; // the safe (under-bound) side of the bracket
    }

    /// <summary>Steady-state engagement (radians) of a trochoid at advance
    /// <paramref name="f"/>: six loops built, the LAST loop's worst tool-circle arc not
    /// yet covered by the swept prefix (a circle sample is cut when it lies within the
    /// tool radius of an earlier path segment).</summary>
    private static double SteadyEngagement(
        double toolRadius, double loopRadius, double f, int samplesPerLoop)
    {
        const int loops = 6;
        const int circleSamples = 90;
        int total = loops * samplesPerLoop;
        var path = new Vector2d[total + 1];
        for (int i = 0; i <= total; i++)
        {
            double theta = 2 * Math.PI * i / samplesPerLoop;
            path[i] = new Vector2d(
                f * theta / (2 * Math.PI) + loopRadius * Math.Cos(theta),
                loopRadius * Math.Sin(theta));
        }

        double worst = 0;
        for (int i = (loops - 1) * samplesPerLoop; i <= total; i++)
        {
            int inMaterial = 0;
            for (int k = 0; k < circleSamples; k++)
            {
                double a = 2 * Math.PI * k / circleSamples;
                var q = path[i] + new Vector2d(
                    toolRadius * Math.Cos(a), toolRadius * Math.Sin(a));
                bool cut = false;
                for (int j = 1; j < i && !cut; j++)
                {
                    var s0 = path[j - 1];
                    var d = path[j] - s0;
                    double len2 = d.Dot(d);
                    double t = len2 > 0 ? Math.Clamp((q - s0).Dot(d) / len2, 0, 1) : 0;
                    cut = (q - (s0 + d * t)).Length < toolRadius - 1e-9;
                }
                if (!cut)
                    inMaterial++;
            }
            worst = Math.Max(worst, 2 * Math.PI * inMaterial / circleSamples);
        }
        return worst;
    }
}
