using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// The variable offset in the EXACT curved tier: straight edges keep exact tangent slabs and
/// vertex joins are exact sectors, while an ARC's varying-offset boundary is a SPIRAL and is
/// fitted with the departure REPORTED.
///
/// <para>The oracle is the same swept-set membership the polygonal twin uses, extended to
/// arcs: a point is within reach iff some edge carries a parameter t with
/// |p − e(t)| ≤ r(t). For a straight edge that minimisation is the closed-form quadratic;
/// for an arc it is a dense scan refined by golden section, whose own error is made
/// negligible against the fit tolerance being measured.</para>
/// </summary>
public class CurvedVariableOffsetTests
{
    private static CurvedRegion2d Square(double side) => new([
        CurvedEdge2d.Line((0, 0), (side, 0)),
        CurvedEdge2d.Line((side, 0), (side, side)),
        CurvedEdge2d.Line((side, side), (0, side)),
        CurvedEdge2d.Line((0, side), (0, 0))]);

    /// <summary>A 20 x 12 slot: two straight flanks and two semicircular ends, so a variable
    /// law has both exact and fitted primitives in one loop.</summary>
    private static CurvedRegion2d Slot() => new([
        CurvedEdge2d.Line((0, -6), (20, -6)),
        CurvedEdge2d.Arc((20, 0), 6, -Math.PI / 2, Math.PI),
        CurvedEdge2d.Line((20, 6), (0, 6)),
        CurvedEdge2d.Arc((0, 0), 6, Math.PI / 2, Math.PI)]);

    private static CurvedRegion2d PlateWithBore() => new(
        Square(20).Outer,
        [[CurvedEdge2d.Arc((10, 10), 4, 0, -2 * Math.PI)]]);

    /// <summary>
    /// Signed reach of the swept collar at <paramref name="p"/>: negative where some edge's
    /// varying disc reaches it. Straight edges use the exact quadratic; arcs a dense scan
    /// plus golden-section refinement, so the oracle's own error is far under the fit
    /// tolerances asserted against it.
    /// </summary>
    private static double SweptReach(
        CurvedRegion2d region, IReadOnlyList<IReadOnlyList<double>> distances, Vector2d p)
    {
        double best = double.PositiveInfinity;
        int l = 0;
        foreach (var loop in region.AllLoops())
        {
            var radii = distances[l++];
            for (int i = 0; i < loop.Count; i++)
            {
                var edge = loop[i];
                double r0 = Math.Abs(radii[i]), r1 = Math.Abs(radii[(i + 1) % loop.Count]);
                if (!edge.IsArc)
                {
                    var a = edge.Start;
                    var d = edge.End - a;
                    double lengthSquared = d.LengthSquared;
                    if (!(lengthSquared > 0))
                        continue;
                    var w = p - a;
                    double dr = r1 - r0;
                    double A = lengthSquared - dr * dr;
                    double B = -2 * w.Dot(d) - 2 * r0 * dr;
                    double C = w.LengthSquared - r0 * r0;
                    double t = A > 0 ? Math.Clamp(-B / (2 * A), 0, 1) : (B <= 0 ? 1 : 0);
                    foreach (double candidate in new[] { 0.0, t, 1.0 })
                        best = Math.Min(best, A * candidate * candidate + B * candidate + C);
                    continue;
                }

                double F(double t)
                {
                    var q = edge.PointAt(t);
                    double r = r0 + (r1 - r0) * t;
                    return (p - q).LengthSquared - r * r;
                }
                const int scan = 512;
                int bestIndex = 0;
                double bestValue = double.PositiveInfinity;
                for (int k = 0; k <= scan; k++)
                {
                    double v = F((double)k / scan);
                    if (v < bestValue)
                        (bestValue, bestIndex) = (v, k);
                }
                double lo = Math.Max(bestIndex - 1, 0) / (double)scan;
                double hi = Math.Min(bestIndex + 1, scan) / (double)scan;
                const double phi = 0.6180339887498949;
                for (int k = 0; k < 80; k++)
                {
                    double m1 = hi - (hi - lo) * phi, m2 = lo + (hi - lo) * phi;
                    if (F(m1) < F(m2))
                        hi = m2;
                    else
                        lo = m1;
                }
                best = Math.Min(best, Math.Min(bestValue, F((lo + hi) / 2)));
            }
        }
        return best;
    }

    private static bool Covered(IReadOnlyList<CurvedRegion2d> regions, in Vector2d p)
    {
        foreach (var region in regions)
        {
            if (region.Contains(p))
                return true;
        }
        return false;
    }

    private static double TotalArea(IReadOnlyList<CurvedRegion2d> regions)
    {
        double area = 0;
        foreach (var region in regions)
            area += region.Area;
        return area;
    }

    /// <summary>
    /// All-equal distances delegate to the CONSTANT curved offset, which is exact — so the
    /// regions come back bit-identical and the reported deviation is exactly zero.
    /// </summary>
    [Fact]
    public void EqualDistances_DelegateToTheConstantOffset_BitForBit()
    {
        var region = Slot();
        foreach (double signed in new[] { 1.5, -1.5 })
        {
            var constant = CurvedRegion2dOffset.Offset(region, signed, OffsetJoin.Round);
            double[] law = [signed, signed, signed, signed];
            var variable = CurvedRegion2dOffset.Offset(region, law);

            Assert.Equal(0.0, variable.MaxDeviation);
            Assert.Equal(constant.Count, variable.Regions.Count);
            for (int r = 0; r < constant.Count; r++)
            {
                Assert.Equal(constant[r].Outer.Count, variable.Regions[r].Outer.Count);
                for (int i = 0; i < constant[r].Outer.Count; i++)
                {
                    Assert.Equal(constant[r].Outer[i].Start.X, variable.Regions[r].Outer[i].Start.X);
                    Assert.Equal(constant[r].Outer[i].Start.Y, variable.Regions[r].Outer[i].Start.Y);
                    Assert.Equal(constant[r].Outer[i].Kind, variable.Regions[r].Outer[i].Kind);
                }
            }
        }
    }

    /// <summary>
    /// A POLYGONAL outline is EXACT in this tier — a straight edge's varying-offset boundary
    /// is still a straight tangent line and a vertex join is a true sector — so the reported
    /// deviation is exactly zero and membership matches the closed form to a tolerance the
    /// polygonal twin cannot reach (it inscribes its joins).
    /// </summary>
    [Fact]
    public void APolygonalOutline_IsExact_AndBeatsThePolygonalTiersInscribedJoins()
    {
        var region = Square(10);
        double[] law = [0.8, 2.5, 1.2, 3.0];
        var offset = CurvedRegion2dOffset.Offset(region, law);
        Assert.Equal(0.0, offset.MaxDeviation);
        var single = Assert.Single(offset.Regions);

        int checked_ = 0, inside = 0;
        for (double x = -4; x <= 14; x += 0.23)
        {
            for (double y = -4; y <= 14; y += 0.23)
            {
                var p = new Vector2d(x, y);
                double reach = SweptReach(region, [law], p);
                // The band is the boundary tolerance alone: nothing here is inscribed.
                if (Math.Abs(reach) < 1e-6)
                    continue;
                Assert.Equal(reach < 0 || region.Contains(p), single.Contains(p));
                checked_++;
                if (reach < 0)
                    inside++;
            }
        }
        Assert.True(checked_ > 3000, $"only {checked_} probes cleared the band");
        Assert.InRange(inside, 500, checked_ - 500);

        // The polygonal tier's answer is strictly SMALLER: its round joins are inscribed
        // chords of the same sectors, so the exact tier gains exactly what they cut off.
        var polygonal = Region2dOffset.Offset(region.ToRegion(1e-3), law, 1e-3);
        double polygonalArea = 0;
        foreach (var piece in polygonal)
            polygonalArea += Math.Abs(Region2d.SignedArea(piece.Outer));
        Assert.True(polygonalArea < single.Area,
            $"the inscribed tier ({polygonalArea}) is not under the exact one ({single.Area})");
    }

    /// <summary>
    /// THE curved-tier statement: an ARC's varying-offset boundary is a spiral, so it is
    /// FITTED, the deviation is reported, and it lands under the tolerance asked for. The
    /// membership check runs outside a band of that same size, so what is asserted is that
    /// the fit is where it says it is.
    /// </summary>
    [Fact]
    public void AnArcsVaryingOffset_IsFittedWithinItsStatedTolerance()
    {
        var region = Slot();
        double[] law = [1.0, 3.0, 1.4, 2.2];
        const double tolerance = 1e-3;
        var offset = CurvedRegion2dOffset.Offset(region, law, tolerance);

        Assert.True(offset.MaxDeviation > 0, "an arc-carrying region reported an exact fit");
        Assert.True(offset.MaxDeviation <= tolerance,
            $"the fit missed its tolerance: {offset.MaxDeviation} > {tolerance}");
        var single = Assert.Single(offset.Regions);

        int checked_ = 0, inside = 0;
        for (double x = -6; x <= 26; x += 0.29)
        {
            for (double y = -12; y <= 12; y += 0.29)
            {
                var p = new Vector2d(x, y);
                double reach = SweptReach(region, [law], p);
                // reach is a SQUARED quantity, so a distance band of `tolerance` shows up
                // scaled by the local gradient; the factor is generous and the probes many.
                if (Math.Abs(reach) < 200 * tolerance)
                    continue;
                Assert.Equal(reach < 0 || region.Contains(p), single.Contains(p));
                checked_++;
                if (reach < 0)
                    inside++;
            }
        }
        Assert.True(checked_ > 5000, $"only {checked_} probes cleared the band");
        Assert.InRange(inside, 800, checked_ - 800);
    }

    /// <summary>A tighter tolerance buys a closer fit — the property that makes the reported
    /// number a control rather than a label.</summary>
    [Fact]
    public void TheFitConverges_AsTheToleranceTightens()
    {
        var region = Slot();
        double[] law = [1.0, 3.0, 1.4, 2.2];
        double coarse = CurvedRegion2dOffset.Offset(region, law, 1e-2).MaxDeviation;
        double fine = CurvedRegion2dOffset.Offset(region, law, 1e-5).MaxDeviation;
        Assert.True(coarse > fine, $"tightening did not help ({coarse} then {fine})");
        Assert.True(fine <= 1e-5, $"the tight fit missed its tolerance ({fine})");
    }

    /// <summary>
    /// THE arc derivation, asserted the way the polygonal tier asserts its tangent slab: the
    /// swept boundary of a varying disc along an ARC is NOT the radial offset `R + r(θ)`. The
    /// tangency foot tilts off the radial by `sin φ = dr/ds`, so the set reaches further out
    /// along a ray than the radial construction claims — the maximum of
    /// `s(θ) = R cos θ + √(r² − R² sin²θ)` sits at `θ* = r′/(R + R²/r)` rather than at 0, and
    /// the excess is `r′²/(2(R + R²/r))`.
    ///
    /// <para>The fixture is a TIGHT cap under a steep law so the excess is a measurable 0.07
    /// rather than a few thousandths, and the witness is picked by the closed-form oracle
    /// itself: scan outward, find where the swept set really ends, and assert both that it
    /// passes the radial boundary and that the built region reaches there.</para>
    /// </summary>
    [Fact]
    public void AnArcsSweptBoundary_ReachesPastTheRadialOffset()
    {
        // A 20 x 4 capsule: R = 2 caps, so the tilt is worth ~0.07 rather than ~0.006.
        var region = new CurvedRegion2d([
            CurvedEdge2d.Line((0, -2), (20, -2)),
            CurvedEdge2d.Arc((20, 0), 2, -Math.PI / 2, Math.PI),
            CurvedEdge2d.Line((20, 2), (0, 2)),
            CurvedEdge2d.Arc((0, 0), 2, Math.PI / 2, Math.PI)]);
        double[] law = [3.0, 3.0, 0.5, 0.5];   // the right cap's law falls 3.0 -> 0.5
        var offset = Assert.Single(CurvedRegion2dOffset.Offset(region, law, 1e-4).Regions);

        // Mid-cap along +x: r there is the mean of the cap's two ends.
        const double radial = 2 + 1.75;
        double reachEnd = radial;
        for (double s = radial; s <= radial + 0.3; s += 1e-4)
        {
            if (SweptReach(region, [law], new Vector2d(20 + s, 0)) < 0)
                reachEnd = s;
        }
        Assert.True(reachEnd > radial + 0.03,
            $"the swept set stops at {reachEnd}, barely past the radial boundary {radial} — "
            + "the fixture cannot separate the two constructions");

        // A witness strictly between the two: inside the true swept set, outside the radial
        // offset the naive construction would draw, and the built region must have it.
        var witness = new Vector2d(20 + (radial + reachEnd) / 2, 0);
        Assert.True(SweptReach(region, [law], witness) < 0, "the witness is not in the swept set");
        Assert.True((witness - new Vector2d(20, 0)).Length > radial,
            "the witness does not separate the tangency foot from the radial offset");
        Assert.True(offset.Contains(witness), "the built region misses the tangency excess");
    }

    [Fact]
    public void VariableErosion_MatchesTheSweptSetPredicate()
    {
        var region = Slot();
        double[] law = [-0.8, -2.0, -1.1, -1.6];
        const double tolerance = 1e-3;
        var eroded = CurvedRegion2dOffset.Offset(region, law, tolerance);

        int checked_ = 0, inside = 0;
        for (double x = -2; x <= 22; x += 0.23)
        {
            for (double y = -8; y <= 8; y += 0.23)
            {
                var p = new Vector2d(x, y);
                double reach = SweptReach(region, [law], p);
                if (Math.Abs(reach) < 200 * tolerance)
                    continue;
                Assert.Equal(region.Contains(p) && reach > 0, Covered(eroded.Regions, p));
                checked_++;
                if (region.Contains(p) && reach > 0)
                    inside++;
            }
        }
        Assert.True(checked_ > 3000, $"only {checked_} probes cleared the band");
        Assert.InRange(inside, 300, checked_ - 300);
    }

    /// <summary>
    /// A hole's law means what the outline's means — how far the material advances into the
    /// void — so ONE positive law grows the plate and shrinks the bore, and the bore's own
    /// varying offset is the fitted spiral case on a full circle.
    /// </summary>
    [Fact]
    public void HoledRegion_GrowsOutwardAndIntoItsBore()
    {
        var plate = PlateWithBore();
        double[] outer = [1.0, 2.0, 1.5, 2.5];
        double[] bore = [0.8];
        var grown = CurvedRegion2dOffset.Offset(plate, [outer, bore], 1e-3);
        var single = Assert.Single(grown.Regions);
        Assert.Single(single.Holes);

        // A CONSTANT law on the single-edge bore leaves it exact; the outline's varying law
        // is straight-edged, so this whole region is exact too.
        Assert.Equal(0.0, grown.MaxDeviation);

        double boreArea = Math.Abs(CurvedRegion2d.SignedArea(single.Holes[0]));
        Assert.True(boreArea < Math.PI * 16, $"the bore did not shrink (area {boreArea})");
        Assert.True(Math.Abs(CurvedRegion2d.SignedArea(single.Outer)) > 400, "the outline did not grow");

        int checked_ = 0;
        for (double x = -4; x <= 24; x += 0.27)
        {
            for (double y = -4; y <= 24; y += 0.27)
            {
                var p = new Vector2d(x, y);
                double reach = SweptReach(plate, [outer, bore], p);
                if (Math.Abs(reach) < 1e-5)
                    continue;
                Assert.Equal(plate.Contains(p) || reach < 0, single.Contains(p));
                checked_++;
            }
        }
        Assert.True(checked_ > 6000, $"only {checked_} probes cleared the band");
    }

    [Fact]
    public void TheVariableOffset_SitsBetweenTheConstantMinAndMax()
    {
        var region = Slot();
        double[] law = [1.0, 3.0, 1.4, 2.2];
        double v = TotalArea(CurvedRegion2dOffset.Offset(region, law).Regions);
        double atMin = TotalArea(CurvedRegion2dOffset.Offset(region, 1.0, OffsetJoin.Round));
        double atMax = TotalArea(CurvedRegion2dOffset.Offset(region, 3.0, OffsetJoin.Round));
        Assert.True(atMin < v, $"min {atMin} !< variable {v}");
        Assert.True(v < atMax, $"variable {v} !< max {atMax}");
    }

    [Fact]
    public void Refusals_AreByName()
    {
        var region = Slot();
        // Mixed signs.
        Assert.Contains("SIGN", Assert.Throws<ArgumentException>(
            () => CurvedRegion2dOffset.Offset(region, [1.0, -1.0, 1.0, 1.0])).Message);
        // Zero.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CurvedRegion2dOffset.Offset(region, [1.0, 0.0, 1.0, 1.0]));
        // Count mismatch.
        Assert.Contains("outline edges", Assert.Throws<ArgumentException>(
            () => CurvedRegion2dOffset.Offset(region, [1.0, 1.0])).Message);
        // A hole needs the per-loop overload.
        Assert.Contains("per-loop overload", Assert.Throws<ArgumentException>(
            () => CurvedRegion2dOffset.Offset(PlateWithBore(), [1.0, 1.0, 1.0, 1.0])).Message);
        // A cubic Bézier — the constant tier's own refusal, unchanged.
        var withBezier = new CurvedRegion2d([
            CurvedEdge2d.Line((0, 0), (10, 0)),
            CurvedEdge2d.Bezier((10, 0), (12, 4), (8, 8), (10, 10)),
            CurvedEdge2d.Line((10, 10), (0, 10)),
            CurvedEdge2d.Line((0, 10), (0, 0))]);
        Assert.Contains("Bézier", Assert.Throws<ArgumentException>(
            () => CurvedRegion2dOffset.Offset(withBezier, [1.0, 1.5, 1.0, 1.0])).Message);
        // A step larger than an edge's own length: no external tangent exists.
        Assert.Contains("swallows", Assert.Throws<ArgumentException>(
            () => CurvedRegion2dOffset.Offset(region, [1.0, 40.0, 1.0, 1.0])).Message);
        // An arc offset inward past its own centre has a cusp rather than a spiral.
        Assert.Contains("centre", Assert.Throws<ArgumentException>(
            () => CurvedRegion2dOffset.Offset(region, [-1.0, -7.0, -1.0, -1.0])).Message);
    }
}
