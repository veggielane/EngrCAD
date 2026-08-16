using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// The variable outline offset: per-vertex distances interpolated linearly in arc
/// length, built as external TANGENT slabs plus per-vertex round joins. The oracle with
/// teeth is EXACT membership: a point is in the dilation iff the region contains it or
/// some edge's swept disc reaches it, and the per-edge test — minimise
/// |p − e(t)|² − r(t)² over t ∈ [0, 1] — is QUADRATIC in t (both terms are), so the
/// predicate is closed form and carries no sampling error of its own. What the built
/// region adds on top of the predicate is only the round joins' inscribed chords, so
/// probes are asserted wherever the predicate's margin exceeds the arc tolerance.
/// </summary>
public class VariableOffsetTests
{
    private static Region2d Square(double side) => new([
        new Vector2d(0, 0), new Vector2d(side, 0),
        new Vector2d(side, side), new Vector2d(0, side)]);

    /// <summary>Signed reach of the variable offset at <paramref name="p"/>: negative
    /// inside the swept set, positive outside, via the closed-form per-edge quadratic
    /// (plus region membership for the material itself).</summary>
    private static double SignedReach(
        Region2d region, IReadOnlyList<double> distances, in Vector2d p)
    {
        if (region.Contains(p))
            return -1;   // deep in the material — any negative stands for "inside"
        var loop = region.Outer;
        double best = double.PositiveInfinity;
        for (int i = 0; i < loop.Count; i++)
        {
            int next = (i + 1) % loop.Count;
            var a = loop[i];
            var edge = loop[next] - a;
            double lengthSquared = edge.LengthSquared;
            if (!(lengthSquared > 0))
                continue;
            double ra = distances[i], rb = distances[next];
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
        return best;
    }

    [Fact]
    public void EqualDistances_DelegateToTheConstantOffset_BitForBit()
    {
        var region = Square(10);
        var constant = Region2dOffset.Offset(region, 1.5, OffsetJoin.Round);
        var variable = Region2dOffset.Offset(region, [1.5, 1.5, 1.5, 1.5]);
        Assert.Equal(constant.Count, variable.Count);
        for (int r = 0; r < constant.Count; r++)
        {
            Assert.Equal(constant[r].Outer.Count, variable[r].Outer.Count);
            for (int i = 0; i < constant[r].Outer.Count; i++)
            {
                Assert.Equal(constant[r].Outer[i].X, variable[r].Outer[i].X);
                Assert.Equal(constant[r].Outer[i].Y, variable[r].Outer[i].Y);
            }
        }
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
                double reach = SignedReach(region, distances, p);
                if (Math.Abs(reach) < 20 * arcTolerance)
                    continue;   // the chord band (reach is a squared quantity near the
                                // boundary; the factor is generous, the probes plentiful)
                bool expected = reach < 0;
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

    [Fact]
    public void TheVariableOffset_SitsBetweenTheConstantMinAndMax()
    {
        var region = Square(10);
        double[] distances = [0.8, 2.5, 1.2, 3.0];
        var variable = Assert.Single(Region2dOffset.Offset(region, distances));
        var atMin = Assert.Single(Region2dOffset.Offset(region, 0.8, OffsetJoin.Round));
        var atMax = Assert.Single(Region2dOffset.Offset(region, 3.0, OffsetJoin.Round));

        double vMin = RegionArea(atMin), v = RegionArea(variable), vMax = RegionArea(atMax);
        Assert.True(vMin < v, $"min {vMin} !< variable {v}");
        Assert.True(v < vMax, $"variable {v} !< max {vMax}");
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

        // Above-plane y = -r(t) along the bottom edge: at x = 1, r = 1.2. The trapezoid
        // boundary through (0,-1)→(10,-3) sits at y = -1.2 there TOO (same secant) — so
        // probe between the SECANT and the TANGENT: tangent tilt sin φ = 0.2, the
        // tangent line through (0,-1)·foot… numerically, reach < 0 at (0.55, -1.18)
        // while the trapezoid's edge would exclude comparable points near x = 0. The
        // closed form itself picks the witness: assert the predicate and the region
        // agree that it is INSIDE, and that the point lies BELOW the secant line
        // through the offset endpoints (outside the filed trapezoid).
        // Derived from the quadratic itself: at x = 0.4 the secant through the offset
        // endpoints sits at y = −1.08 while the tangent boundary (feet (−0.2, −0.98) →
        // (9.4, −2.939)) sits at y ≈ −1.102, so y = −1.09 lies between them — reach
        // f(t*) = −0.027 at t* = 0.0625, inside the swept set, outside the trapezoid.
        var witness = new Vector2d(0.4, -1.09);
        double reach = SignedReach(region, distances, witness);
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
        // Non-positive distance.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Region2dOffset.Offset(region, [1.0, 0.0, 1.0, 1.0]));
        // A step larger than its edge: the larger disc swallows the sweep.
        Assert.Contains("swallows", Assert.Throws<ArgumentException>(
            () => Region2dOffset.Offset(region, [1.0, 12.0, 1.0, 1.0])).Message);
        // Holes wait for the composition story.
        var holed = new Region2d(Square(10).Outer,
            [[new Vector2d(4, 4), new Vector2d(4, 6), new Vector2d(6, 6), new Vector2d(6, 4)]]);
        Assert.Contains("hole", Assert.Throws<ArgumentException>(
            () => Region2dOffset.Offset(holed, [1.0, 1.0, 1.0, 1.0])).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static double RegionArea(Region2d region)
    {
        double area = Math.Abs(Region2d.SignedArea(region.Outer));
        foreach (var hole in region.Holes)
            area -= Math.Abs(Region2d.SignedArea(hole));
        return area;
    }
}
