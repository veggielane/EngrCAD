using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// The variable offset: per-vertex distances interpolated linearly in arc length, built as
/// external TANGENT slabs plus per-vertex round joins — outward for a positive law, inward
/// for a negative one, on every loop of a holed region alike.
///
/// <para>The oracle with teeth is EXACT membership: a point is within reach of the boundary
/// iff some edge's swept disc reaches it, and the per-edge test — minimise
/// |p − e(t)|² − r(t)² over t ∈ [0, 1] — is QUADRATIC in t (both terms are), so the
/// predicate is closed form and carries no sampling error of its own. The DILATION is that
/// reach unioned with the region; the EROSION is the region minus it. What the built region
/// adds on top of the predicate is only the round joins' inscribed chords, so probes are
/// asserted wherever the predicate's margin exceeds the arc tolerance.</para>
/// </summary>
public class VariableOffsetTests
{
    private static Region2d Square(double side) => new([
        new Vector2d(0, 0), new Vector2d(side, 0),
        new Vector2d(side, side), new Vector2d(0, side)]);

    /// <summary>An L with one REFLEX corner at (6, 6) — the corner an inward collar must
    /// fill and an outward one must not, so it separates the two join rules.</summary>
    private static Region2d Ell() => new([
        new Vector2d(0, 0), new Vector2d(14, 0), new Vector2d(14, 6),
        new Vector2d(6, 6), new Vector2d(6, 14), new Vector2d(0, 14)]);

    private static Region2d PlateWithSquareHole() => new(
        Square(20).Outer,
        [[new Vector2d(6, 6), new Vector2d(6, 14), new Vector2d(14, 14), new Vector2d(14, 6)]]);

    /// <summary>
    /// Signed reach of the swept collar at <paramref name="p"/>: negative where some edge's
    /// varying disc reaches it, positive outside, via the closed-form per-edge quadratic over
    /// EVERY loop of the region.
    /// </summary>
    private static double SignedReach(
        Region2d region, IReadOnlyList<IReadOnlyList<double>> distances, in Vector2d p)
    {
        double best = double.PositiveInfinity;
        int l = 0;
        foreach (var loop in region.AllLoops())
        {
            var radii = distances[l++];
            for (int i = 0; i < loop.Count; i++)
            {
                int next = (i + 1) % loop.Count;
                var a = loop[i];
                var edge = loop[next] - a;
                double lengthSquared = edge.LengthSquared;
                if (!(lengthSquared > 0))
                    continue;
                double ra = Math.Abs(radii[i]), rb = Math.Abs(radii[next]);
                // f(t) = |p − a − t·edge|² − (ra + t·(rb − ra))², quadratic At² + Bt + C.
                var w = p - a;
                double dr = rb - ra;
                double A = lengthSquared - dr * dr;
                double B = -2 * w.Dot(edge) - 2 * ra * dr;
                double C = w.LengthSquared - ra * ra;
                double t = A > 0 ? Math.Clamp(-B / (2 * A), 0, 1) : (B <= 0 ? 1 : 0);
                foreach (double candidate in new[] { 0.0, t, 1.0 })
                {
                    double f = A * candidate * candidate + B * candidate + C;
                    best = Math.Min(best, f);
                }
            }
        }
        return best;
    }

    private static bool Covered(IReadOnlyList<Region2d> regions, in Vector2d p)
    {
        foreach (var region in regions)
        {
            if (region.Contains(p))
                return true;
        }
        return false;
    }

    private static double TotalArea(IReadOnlyList<Region2d> regions)
    {
        double area = 0;
        foreach (var region in regions)
        {
            area += Math.Abs(Region2d.SignedArea(region.Outer));
            foreach (var hole in region.Holes)
                area -= Math.Abs(Region2d.SignedArea(hole));
        }
        return area;
    }

    private static void AssertBitIdentical(
        IReadOnlyList<Region2d> expected, IReadOnlyList<Region2d> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int r = 0; r < expected.Count; r++)
        {
            Assert.Equal(expected[r].Outer.Count, actual[r].Outer.Count);
            for (int i = 0; i < expected[r].Outer.Count; i++)
            {
                Assert.Equal(expected[r].Outer[i].X, actual[r].Outer[i].X);
                Assert.Equal(expected[r].Outer[i].Y, actual[r].Outer[i].Y);
            }
            Assert.Equal(expected[r].Holes.Count, actual[r].Holes.Count);
            for (int h = 0; h < expected[r].Holes.Count; h++)
            {
                Assert.Equal(expected[r].Holes[h].Count, actual[r].Holes[h].Count);
                for (int i = 0; i < expected[r].Holes[h].Count; i++)
                {
                    Assert.Equal(expected[r].Holes[h][i].X, actual[r].Holes[h][i].X);
                    Assert.Equal(expected[r].Holes[h][i].Y, actual[r].Holes[h][i].Y);
                }
            }
        }
    }

    [Fact]
    public void EqualDistances_DelegateToTheConstantOffset_BitForBit()
    {
        var region = Square(10);
        AssertBitIdentical(
            Region2dOffset.Offset(region, 1.5, OffsetJoin.Round),
            Region2dOffset.Offset(region, [1.5, 1.5, 1.5, 1.5]));

        // The EROSION delegates too, which is what keeps the frame-free construction from
        // being a second answer to a question the constant path already answers.
        AssertBitIdentical(
            Region2dOffset.Offset(region, -1.5, OffsetJoin.Round),
            Region2dOffset.Offset(region, [-1.5, -1.5, -1.5, -1.5]));

        // ... and on a holed region, through the per-loop overload.
        var plate = PlateWithSquareHole();
        double[] outer = [2, 2, 2, 2];
        double[] hole = [2, 2, 2, 2];
        AssertBitIdentical(
            Region2dOffset.Offset(plate, 2.0, OffsetJoin.Round),
            Region2dOffset.Offset(plate, [outer, hole]));
    }

    [Fact]
    public void MembershipMatchesTheClosedFormPredicate_AwayFromTheChordBand()
    {
        // The exact oracle: probe a grid, and wherever the closed-form signed reach is
        // clear of zero by more than the join arcs' inscribed-chord tolerance, the
        // built region must agree. The tangent slabs are EXACT lines, so the band only
        // exists for the round joins.
        const double arcTolerance = 1e-3;
        var region = Square(10);
        double[] distances = [0.8, 2.5, 1.2, 3.0];
        var offset = Region2dOffset.Offset(region, distances, arcTolerance);
        var single = Assert.Single(offset);

        int checked_ = 0, insidePredicate = 0;
        for (double x = -4; x <= 14; x += 0.23)
        {
            for (double y = -4; y <= 14; y += 0.23)
            {
                var p = new Vector2d(x, y);
                double reach = SignedReach(region, [distances], p);
                if (Math.Abs(reach) < 20 * arcTolerance)
                    continue;   // the chord band (reach is a squared quantity near the
                                // boundary; the factor is generous, the probes plentiful)
                bool expected = reach < 0 || region.Contains(p);
                Assert.Equal(expected, single.Contains(p));
                checked_++;
                if (expected)
                    insidePredicate++;
            }
        }
        // The probe set must carry both populations, or the agreement proves nothing.
        Assert.True(checked_ > 3000, $"only {checked_} probes cleared the band");
        Assert.InRange(insidePredicate, 500, checked_ - 500);
    }

    /// <summary>
    /// THE erosion claim, against the same closed form: the eroded set is the region MINUS
    /// the inward collar. The fixture is an L so both corner kinds are exercised — a convex
    /// corner whose inward wedge is already covered by its two slabs, and the REFLEX corner
    /// at (6, 6) whose inward wedge is covered by nothing else, so a build that offered the
    /// join pair in the outward order would leave a quarter disc of material standing there.
    /// </summary>
    [Fact]
    public void VariableErosion_MatchesTheClosedFormPredicate()
    {
        const double arcTolerance = 1e-3;
        var region = Ell();
        double[] distances = [-0.7, -2.2, -1.1, -2.6, -1.4, -1.9];
        var eroded = Region2dOffset.Offset(region, distances, arcTolerance);

        int checked_ = 0, inside = 0;
        for (double x = -1; x <= 15; x += 0.17)
        {
            for (double y = -1; y <= 15; y += 0.17)
            {
                var p = new Vector2d(x, y);
                double reach = SignedReach(region, [distances], p);
                if (Math.Abs(reach) < 20 * arcTolerance)
                    continue;
                bool expected = region.Contains(p) && reach > 0;
                Assert.Equal(expected, Covered(eroded, p));
                checked_++;
                if (expected)
                    inside++;
            }
        }
        Assert.True(checked_ > 3000, $"only {checked_} probes cleared the band");
        Assert.InRange(inside, 200, checked_ - 200);
    }

    /// <summary>
    /// The reflex corner in isolation, so the failure has one cause. Erode the L by a
    /// CONSTANT-magnitude law spelled as a variable one (a hair off constant so it cannot
    /// delegate): the inward wedge at (6, 6) — the quarter disc between +x and +y — must be
    /// removed. Without the reversed join order it survives as a spur of material.
    /// </summary>
    [Fact]
    public void VariableErosion_RemovesTheReflexCornersInwardWedge()
    {
        var region = Ell();
        double[] distances = [-2, -2, -2, -2.0000001, -2, -2];
        var eroded = Region2dOffset.Offset(region, distances, 1e-4);

        // The inward cone at (6, 6) points DOWN-LEFT, into the material: the incoming edge
        // runs −x so its inward normal is −y, the outgoing runs +y so its inward normal is
        // −x, and the wedge between them is the third quadrant. (Up-right is the quadrant
        // the L does not occupy at all — the easy sign to get backwards, which is why the
        // probes are placed from the derivation rather than by eye.)
        var wedge = new Vector2d(-1, -1).Normalized();
        foreach (double radius in new[] { 0.4, 0.9, 1.5 })
        {
            var p = new Vector2d(6, 6) + wedge * radius;
            Assert.True(region.Contains(p), $"the fixture's probe at {radius} is not inside the L");
            Assert.False(Covered(eroded, p), $"the reflex corner's inward wedge survived at {radius}");
        }
        // And a point just past the reach is kept, so the test is not merely eroding away.
        var beyond = new Vector2d(6, 6) + wedge * 2.6;
        Assert.True(Covered(eroded, beyond), "the erosion reached past its own law");
    }

    [Fact]
    public void TheVariableOffset_SitsBetweenTheConstantMinAndMax()
    {
        var region = Square(10);
        double[] distances = [0.8, 2.5, 1.2, 3.0];
        double v = TotalArea(Region2dOffset.Offset(region, distances));
        double vMin = TotalArea(Region2dOffset.Offset(region, 0.8, OffsetJoin.Round));
        double vMax = TotalArea(Region2dOffset.Offset(region, 3.0, OffsetJoin.Round));
        Assert.True(vMin < v, $"min {vMin} !< variable {v}");
        Assert.True(v < vMax, $"variable {v} !< max {vMax}");

        // The erosion brackets the other way: eroding by the SMALLEST magnitude everywhere
        // leaves the most material.
        double e = TotalArea(Region2dOffset.Offset(region, [-0.8, -2.5, -1.2, -3.0]));
        double eMax = TotalArea(Region2dOffset.Offset(region, -0.8, OffsetJoin.Round));
        double eMin = TotalArea(Region2dOffset.Offset(region, -3.0, OffsetJoin.Round));
        Assert.True(eMin < e, $"the deepest constant erosion {eMin} !< variable {e}");
        Assert.True(e < eMax, $"variable {e} !< the shallowest constant erosion {eMax}");
    }

    /// <summary>
    /// A HOLE's distances mean what the outline's mean — how far the material advances into
    /// the void — so ONE positive law grows the plate and shrinks the bore. Asserted against
    /// the same closed form over both loops, and by the closed-form area of a case where the
    /// law is constant per loop but different BETWEEN loops (which is exactly the case a flat
    /// single list cannot spell).
    /// </summary>
    [Fact]
    public void HoledRegion_GrowsOutwardAndIntoItsHole()
    {
        const double arcTolerance = 1e-3;
        var plate = PlateWithSquareHole();
        double[] outer = [1.0, 2.0, 1.5, 2.5];
        double[] hole = [0.5, 1.5, 0.75, 1.25];
        var grown = Region2dOffset.Offset(plate, [outer, hole], arcTolerance);
        var single = Assert.Single(grown);
        Assert.Single(single.Holes);

        int checked_ = 0, inside = 0;
        for (double x = -4; x <= 24; x += 0.21)
        {
            for (double y = -4; y <= 24; y += 0.21)
            {
                var p = new Vector2d(x, y);
                double reach = SignedReach(plate, [outer, hole], p);
                if (Math.Abs(reach) < 20 * arcTolerance)
                    continue;
                bool expected = plate.Contains(p) || reach < 0;
                Assert.Equal(expected, single.Contains(p));
                checked_++;
                if (expected)
                    inside++;
            }
        }
        Assert.True(checked_ > 6000, $"only {checked_} probes cleared the band");
        Assert.InRange(inside, 1000, checked_ - 1000);

        // The hole really shrank: its area falls, and the outline's grew.
        double holeArea = Math.Abs(Region2d.SignedArea(single.Holes[0]));
        Assert.True(holeArea < 64, $"the bore did not shrink (area {holeArea} of 64)");
        Assert.True(Math.Abs(Region2d.SignedArea(single.Outer)) > 400, "the outline did not grow");
    }

    [Fact]
    public void HoledRegion_ErosionShrinksTheOutlineAndOpensTheHole()
    {
        var plate = PlateWithSquareHole();
        double[] outer = [-1.0, -2.0, -1.5, -2.5];
        double[] hole = [-0.5, -1.5, -0.75, -1.25];
        var eroded = Region2dOffset.Offset(plate, [outer, hole]);
        var single = Assert.Single(eroded);
        Assert.Single(single.Holes);

        Assert.True(Math.Abs(Region2d.SignedArea(single.Outer)) < 400, "the outline did not shrink");
        Assert.True(Math.Abs(Region2d.SignedArea(single.Holes[0])) > 64, "the bore did not open");

        // Probed against the closed form, both loops at once.
        int checked_ = 0;
        for (double x = -1; x <= 21; x += 0.19)
        {
            for (double y = -1; y <= 21; y += 0.19)
            {
                var p = new Vector2d(x, y);
                double reach = SignedReach(plate, [outer, hole], p);
                if (Math.Abs(reach) < 2e-2)
                    continue;
                Assert.Equal(plate.Contains(p) && reach > 0, Covered(eroded, p));
                checked_++;
            }
        }
        Assert.True(checked_ > 6000, $"only {checked_} probes cleared the band");
    }

    /// <summary>
    /// A thin neck erodes to NOTHING rather than to an inverted loop — the property the
    /// union-of-primitives construction gives for free, restated for the variable law.
    /// </summary>
    [Fact]
    public void AnErosionDeeperThanTheRegion_ReturnsNothing()
    {
        var rib = new Region2d([
            new Vector2d(0, 0), new Vector2d(30, 0), new Vector2d(30, 2), new Vector2d(0, 2)]);
        Assert.Empty(Region2dOffset.Offset(rib, [-1.2, -1.5, -1.4, -1.1]));
    }

    [Fact]
    public void TheTangentSlab_CoversWhatTheEndpointTrapezoidMisses()
    {
        // THE derivation correction, asserted: near the smaller end of a rising edge
        // the swept set reaches OUTSIDE the trapezoid through the two offset endpoints
        // (the backlog's filed construction), because the true boundary is the external
        // tangent line, tilted toward the smaller radius. The probe sits over the small
        // end, beyond its perpendicular offset but under the tangent line — inside per
        // the closed form, and the built region must contain it.
        var region = Square(10);
        double[] distances = [1.0, 3.0, 1.0, 1.0];   // the bottom edge rises 1 → 3
        var offset = Assert.Single(Region2dOffset.Offset(region, distances, arcTolerance: 1e-4));

        // Derived from the quadratic itself: at x = 0.4 the secant through the offset
        // endpoints sits at y = −1.08 while the tangent boundary (feet (−0.2, −0.98) →
        // (9.4, −2.939)) sits at y ≈ −1.102, so y = −1.09 lies between them — reach
        // f(t*) = −0.027 at t* = 0.0625, inside the swept set, outside the trapezoid.
        var witness = new Vector2d(0.4, -1.09);
        double reach = SignedReach(region, [distances], witness);
        Assert.True(reach < 0, $"the witness is not inside the swept set (reach {reach})");
        // Below the trapezoid boundary through (0,-1) → (10,-3): y = -1 - 0.2·x.
        Assert.True(witness.Y < -1 - 0.2 * witness.X,
            "the witness does not separate the tangent slab from the trapezoid");
        Assert.True(offset.Contains(witness), "the built region misses the tangency wedge");
    }

    [Fact]
    public void Refusals_AreByName()
    {
        var region = Square(10);
        // Count mismatch.
        Assert.Contains("4 outline vertices", Assert.Throws<ArgumentException>(
            () => Region2dOffset.Offset(region, [1.0, 2.0])).Message);
        // Zero: a zero-radius disc sweeps nothing, so it is neither a direction nor a no-op.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Region2dOffset.Offset(region, [1.0, 0.0, 1.0, 1.0]));
        // MIXED SIGNS: the law would pass through a zero-radius disc.
        Assert.Contains("SIGN", Assert.Throws<ArgumentException>(
            () => Region2dOffset.Offset(region, [1.0, -2.0, 1.0, 1.0])).Message);
        // A step larger than its edge: the larger disc swallows the sweep.
        Assert.Contains("swallows", Assert.Throws<ArgumentException>(
            () => Region2dOffset.Offset(region, [1.0, 12.0, 1.0, 1.0])).Message);
        // A holed region names the per-loop overload rather than refusing the feature.
        var holed = PlateWithSquareHole();
        var message = Assert.Throws<ArgumentException>(
            () => Region2dOffset.Offset(holed, [1.0, 1.0, 1.0, 1.0])).Message;
        Assert.Contains("hole", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("per-loop overload", message);
        // Wrong number of LISTS.
        Assert.Contains("distance lists", Assert.Throws<ArgumentException>(
            () => Region2dOffset.Offset(holed, [new double[] { 1, 1, 1, 1 }])).Message);
    }
}
