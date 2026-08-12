using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Ecad;

/// <summary>
/// Teardrop settings — the tapered copper a trace gains where it meets a ROUND pad or a via of its own
/// net, relieving drill-breakout stress (a crack that starts at the sharp trace-to-pad junction). It is
/// LAYOUT TRUTH (part of the design), so a layout carrying it rides in the file, and
/// <see cref="PcbCopperModel.FromLayout"/> DERIVES the teardrop copper — same-net, added to the trace's
/// own copper — so the DRC, connectivity, Gerber export and everything downstream read it like any
/// other copper.
///
/// <para><b>v1 scope.</b> A convex-hull teardrop on ROUND pads and vias only (a rectangular or oval pad
/// is skipped). The teardrop is same-net, so it never shorts its own pad; it is DRC-gated against
/// OTHER-net copper (a teardrop that would come within <see cref="Clearance"/> of another net is
/// dropped, never shipped as a violation). A trace whose end is not inside a same-net round pad / via,
/// or a pad no wider than the trace, simply gets none.</para>
/// </summary>
/// <param name="LengthRatio">How far the teardrop runs back along the trace, as a multiple of the pad
/// radius (clamped to the trace's own first segment). The default 2.0 (one pad diameter) is a
/// substantial teardrop; smaller grabs less of the pad.</param>
/// <param name="Clearance">The gap the teardrop keeps from OTHER-net copper (mm) — a teardrop that would
/// close below it is dropped.</param>
public sealed record TeardropSettings(double LengthRatio = 2.0, double Clearance = 0.2)
{
    /// <summary>The default teardrop settings (one pad diameter long, 0.2 mm clearance).</summary>
    public static TeardropSettings Default { get; } = new();

    internal void Validate()
    {
        if (!(LengthRatio > 0))
            throw new ArgumentException($"A teardrop length ratio must be positive (got {LengthRatio:g6}).");
        if (!(Clearance > 0))
            throw new ArgumentException($"A teardrop clearance must be positive (got {Clearance:g6}).");
    }
}

/// <summary>
/// Builds teardrop copper for a layout — the tapered same-net fill at each trace-to-pad / trace-to-via
/// junction. Each teardrop carries the SOURCE of its trace, so it merges into the trace's copper and the
/// connectivity engine reads it as a connector, not a new terminal (a trace already shares one source
/// across its stroke regions, so this needs no new source kind). Every teardrop is DRC-gated against
/// other-net copper and dropped if it would violate — a teardrop is never shipped as a short or a
/// clearance miss.
///
/// <para><b>The geometry is the convex hull of the pad DISC and the two trace-EDGE points</b> (the trace
/// edges at ±half-width a length back along the trace). That hull is the pad plus a wedge reaching to
/// those points, so unioned with the pad and trace it fills the CONCAVE CORNERS where the trace edge
/// leaves the pad circle — the copper a teardrop adds, OUTSIDE the pad. (A naive straight chamfer from
/// the pad's perpendicular diameter to the trace edges lies entirely INSIDE the pad for a trace ending
/// at the pad centre and adds nothing.) The pad arc is sampled into the hull, so the added copper's
/// pad-side boundary is a fine polygon that lies UNDER the exact pad disc — the visible teardrop
/// boundary, the two flanks, is exact straight copper.</para>
/// </summary>
internal static class TeardropBuilder
{
    // A round junction a trace can teardrop into: a round pad or a via, with the copper layers it is on.
    private readonly record struct Junction(Vector2d Center, double Radius, string Net, IReadOnlyList<string> Layers);

    public static IReadOnlyList<CopperFeature> Generate(
        PcbCopperModel baseModel, PcbLayout layout, TeardropSettings settings)
    {
        settings.Validate();
        var junctions = CollectJunctions(baseModel, layout);
        if (junctions.Count == 0)
            return [];

        var result = new List<CopperFeature>();
        for (int i = 0; i < layout.Traces.Count; i++)
        {
            var trace = layout.Traces[i];
            var pts = trace.Points;
            if (pts.Count < 2)
                continue;
            double h = trace.Width / 2;
            string source = PcbLayout.TraceSource(i);

            foreach (bool atStart in Ends)
            {
                Vector2d e = atStart ? pts[0] : pts[^1];
                Vector2d next = atStart ? pts[1] : pts[^2];
                var seg = next - e;
                double segLen = seg.Length;
                if (segLen <= 0)
                    continue;
                var dir = seg / segLen;

                // The NEAREST same-net round junction on the trace's layer whose disc contains the
                // endpoint and is wider than the trace (else there is no room to taper).
                Junction? best = null;
                double bestDist = double.PositiveInfinity;
                foreach (var j in junctions)
                {
                    if (!string.Equals(j.Net, trace.Net, StringComparison.Ordinal))
                        continue;
                    if (!j.Layers.Contains(trace.Layer))
                        continue;
                    if (j.Radius <= h)
                        continue;
                    double dist = (e - j.Center).Length;
                    if (dist <= j.Radius && dist < bestDist)
                    {
                        best = j;
                        bestDist = dist;
                    }
                }
                if (best is not { } jn)
                    continue;

                double length = Math.Min(settings.LengthRatio * jn.Radius, segLen);
                if (!(length > jn.Radius))
                    continue;   // the trace-edge points must reach past the pad to add copper

                var teardrop = HullTeardrop(jn.Center, jn.Radius, dir, h, length);
                if (teardrop is not { } region)
                    continue;

                if (ViolatesOtherNet(baseModel, trace.Layer, trace.Net, region, settings.Clearance))
                    continue;

                result.Add(new CopperFeature(trace.Layer, trace.Net, source, region));
            }
        }
        return result;
    }

    private static readonly bool[] Ends = [true, false];

    private static IReadOnlyList<Junction> CollectJunctions(PcbCopperModel model, PcbLayout layout)
    {
        var junctions = new List<Junction>();
        IReadOnlyList<string> allCopper = [.. model.Board.Stackup.Coppers.Select(c => c.Name)];

        var netBySource = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in model.Copper)
            if (f.Net is not null)
                netBySource[f.Source] = f.Net;
        var placementByRef = layout.Placements.ToDictionary(p => p.Reference, StringComparer.Ordinal);

        foreach (var pad in layout.PlacedPads())
        {
            if (pad.Shape != PadShape.Round)
                continue;
            if (!netBySource.TryGetValue(pad.Name, out var net))
                continue;   // a netless pad — nothing to teardrop into
            double radius = Math.Max(pad.Width, pad.Height) / 2;
            IReadOnlyList<string> layers = pad.Kind == PadKind.ThroughHole
                ? allCopper
                : placementByRef.TryGetValue(pad.Reference, out var placement)
                    ? [layout.TargetLayerName(placement)]
                    : [model.Board.Stackup.Top.Name];
            junctions.Add(new Junction(pad.World, radius, net, layers));
        }

        foreach (var via in layout.PlacedVias())
            junctions.Add(new Junction(via.Center, via.PadDiameter / 2, via.Net, via.Layers));

        return junctions;
    }

    private const int PadSamples = 24;

    // The convex hull of the pad disc (sampled) and the two trace-edge points at (length·dir ± h·n).
    private static CurvedRegion2d? HullTeardrop(
        in Vector2d centre, double radius, in Vector2d dir, double h, double length)
    {
        var n = new Vector2d(-dir.Y, dir.X);
        var points = new List<Vector2d>(PadSamples + 2);
        for (int i = 0; i < PadSamples; i++)
        {
            double a = 2.0 * Math.PI * i / PadSamples;
            points.Add(new Vector2d(centre.X + radius * Math.Cos(a), centre.Y + radius * Math.Sin(a)));
        }
        points.Add(centre + length * dir + h * n);
        points.Add(centre + length * dir - h * n);

        var hull = ConvexHull2.Compute(points);
        if (hull.Length < 3)
            return null;
        try
        {
            return CurvedRegion2d.FromRegion(new Region2d(hull));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool ViolatesOtherNet(
        PcbCopperModel model, string layer, string net, CurvedRegion2d teardrop, double clearance)
    {
        var grown = CurvedRegion2dOffset.Offset(teardrop, clearance, OffsetJoin.Round);
        foreach (var f in model.Copper)
        {
            if (f.Layer != layer || f.Net is null || string.Equals(f.Net, net, StringComparison.Ordinal))
                continue;
            if (CurvedRegion2dBoolean.Intersection(grown, [f.Region]).Count > 0)
                return true;
        }
        return false;
    }
}
