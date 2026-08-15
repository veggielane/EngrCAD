using System.Text;
using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Cam;

/// <summary>The machine facts sequential printing is a statement about: the EXTRUDER
/// clearance radius (how close the nozzle assembly may pass a completed part) and the
/// GANTRY height (the clearance under the X beam — a completed part taller than it is hit
/// by the beam while later parts print, wherever it stands). ⚠ Both are per-printer numbers
/// with common defaults; verify against the machine.</summary>
public sealed record FdmSequentialOptions(
    double ClearanceRadius = 25, double GantryHeight = 25);

/// <summary>The print plan: the part indices in print order (ascending height, so the
/// gantry always passes over shorter completed parts), each part's height, and the smallest
/// measured pair clearance — the number the plate was accepted on.</summary>
public sealed record SequentialPlan(
    IReadOnlyList<int> Order, IReadOnlyList<double> Heights, double MinPairClearance);

/// <summary>A planned sequential print: one <see cref="SlicedPart"/> per part, in print
/// order, plus the plan that ordered them.</summary>
public sealed record SequentialPrint(
    IReadOnlyList<SlicedPart> Parts, SequentialPlan Plan);

/// <summary>
/// Sequential (complete-one-object) printing: each part is printed WHOLE before the next
/// starts — the failure-isolation mode, since a mid-print failure costs one part rather than
/// the plate. What makes it legal is CLEARANCE, checked rather than hoped: every pair of
/// parts must stand at least the extruder's clearance radius apart (measured on the XY
/// bounds — a bounds gap UNDER-estimates the true footprint gap, so the check refuses some
/// legal plates and never accepts an illegal one), parts print in ASCENDING height order so
/// the gantry always passes over shorter completed work, and at most ONE part may exceed the
/// gantry height — it prints last, and a second one has nowhere legal to go (refused naming
/// both). The combined G-code is the per-part programs with the middle headers and tails
/// stripped, each handover a clearance HOP above everything completed plus an XY move to the
/// next part's own start BEFORE descending (descending first would drop the nozzle to first-
/// layer height over the completed neighbour), and a <c>G92 E0</c> so each part's absolute E
/// starts clean — which the twin decoder already understands, so the combined program's
/// filament total is the sum of the parts' own.
/// </summary>
public static class FdmSequential
{
    /// <summary>Plans the print order for ALREADY-PLACED parts (use
    /// <see cref="FdmPlating.Arrange"/> to place them with the clearance as the gap).
    /// Refuses a pair closer than the clearance radius or a second over-gantry part,
    /// each named.</summary>
    public static SequentialPlan Plan(
        IReadOnlyList<Shape> placed, FdmSequentialOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(placed);
        if (placed.Count == 0)
            throw new ArgumentException("A sequential print needs at least one part.", nameof(placed));
        var o = options ?? new FdmSequentialOptions();

        var bounds = placed.Select(s => s.Bounds()).ToList();
        double minGap = double.PositiveInfinity;
        for (int i = 0; i < placed.Count; i++)
            for (int j = i + 1; j < placed.Count; j++)
            {
                double gap = RectGap(bounds[i], bounds[j]);
                minGap = Math.Min(minGap, gap);
                if (gap < o.ClearanceRadius)
                    throw new ArgumentException(
                        $"Parts {i} and {j} stand {gap:0.###} apart where the extruder needs "
                        + $"{o.ClearanceRadius:0.###} — the nozzle assembly would strike the "
                        + "completed part. Arrange with the clearance as the gap.");
            }

        var heights = placed.Select((s, i) => bounds[i].Max.Z).ToList();
        var over = Enumerable.Range(0, placed.Count)
            .Where(i => heights[i] > o.GantryHeight).ToList();
        if (over.Count > 1)
            throw new ArgumentException(
                $"Parts {string.Join(", ", over)} all exceed the gantry height "
                + $"({o.GantryHeight:0.###}) — only the LAST printed part may, because the "
                + "gantry beam crosses the whole bed and would strike any taller completed "
                + "part while the next prints. Print them on separate plates.");

        var order = Enumerable.Range(0, placed.Count)
            .OrderBy(i => heights[i]).ThenBy(i => i).ToList();
        return new SequentialPlan(order, heights, minGap);
    }

    /// <summary>Plans and slices: one full <see cref="SlicedPart"/> per part, in print
    /// order (the parts keep their bed positions — they were sliced where they stand).</summary>
    public static SequentialPrint Slice(
        IReadOnlyList<Shape> placed, PrinterProfile? profile = null,
        FdmSequentialOptions? options = null)
    {
        var plan = Plan(placed, options);
        var parts = plan.Order
            .Select(i => FdmSlicer.Slice(placed[i], profile)).ToList();
        return new SequentialPrint(parts, plan);
    }

    /// <summary>The combined program: part programs in print order, middle headers/tails
    /// stripped, each handover a hop above everything completed + the XY move to the next
    /// part's own start + <c>G92 E0</c>.</summary>
    public static string WriteGcode(SequentialPrint print)
    {
        ArgumentNullException.ThrowIfNull(print);
        var parts = print.Parts;
        var b = new StringBuilder();
        double completedTop = 0;
        for (int k = 0; k < parts.Count; k++)
        {
            var part = parts[k];
            if (k > 0)
            {
                double hop = completedTop + 2;
                int travelFeed = (int)Math.Round(part.Profile.TravelSpeed * 60);
                var start = part.Layers[0].Paths[0].Start;
                b.Append($"; sequential part {k + 1}\n");
                b.Append($"G0 Z{hop.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} F{travelFeed}\n");
                b.Append($"G0 X{start.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} "
                    + $"Y{start.Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} F{travelFeed}\n");
                b.Append("G92 E0\n");
            }
            b.Append(GcodeWriter.Write(part, header: k == 0, tail: k == parts.Count - 1));
            completedTop = Math.Max(completedTop, part.Layers[^1].Z);
        }
        return b.ToString();
    }

    /// <summary>The XY gap between two bounds rectangles (0 when they overlap) — an
    /// UNDER-estimate of the true footprint gap, i.e. conservative in the accept
    /// direction.</summary>
    private static double RectGap(in Aabb a, in Aabb b)
    {
        double dx = Math.Max(0, Math.Max(a.Min.X - b.Max.X, b.Min.X - a.Max.X));
        double dy = Math.Max(0, Math.Max(a.Min.Y - b.Max.Y, b.Min.Y - a.Max.Y));
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
