using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Implicit.Tests;

/// <summary>
/// What a TPMS sheet's wall actually measures, and why the excess over the nominal thickness is
/// <b>inherent</b> rather than a defect waiting to be tuned away.
/// <para>
/// A sheet is the band <c>|F| ≤ L</c>, whose perpendicular width at a surface point is
/// <c>2L/|grad F|</c> — so the wall varies wherever the gradient does, which is everywhere for
/// every one of these polynomials, and no choice of DIVISOR fixes it. Dividing by a constant
/// (the global maximum, as the field does, or the surface maximum, which the backlog proposed)
/// only rescales the whole distribution; dividing by the LOCAL gradient would make the wall
/// first-order uniform and would cost the 1-Lipschitz contract the field exists to keep.
/// </para>
/// <para>
/// So the wall is REPORTED. <see cref="Tpms.WallThickness"/> is the first-order relation
/// <c>bound/|grad F|</c> measured on the level set; the test that earns its keep is the one
/// below that checks it against a DIRECT bisection of the sheet's own field along the normal,
/// which shares no arithmetic with it.
/// </para>
/// </summary>
public class TpmsWallThicknessTests(ITestOutputHelper output)
{
    public static TheoryData<TpmsKind> Kinds
    {
        get
        {
            var data = new TheoryData<TpmsKind>();
            foreach (TpmsKind kind in Enum.GetValues<TpmsKind>())
                data.Add(kind);
            return data;
        }
    }

    /// <summary>
    /// The first-order relation against a direct measurement, POINT BY POINT rather than
    /// distribution against distribution — which is the version with teeth, because two medians
    /// can disagree merely by being taken over different measures of the surface and that would
    /// say nothing about the relation itself.
    /// <para>
    /// At a surface point the local wall is predicted as <c>t / |grad(solid field)|</c> — the
    /// same <c>bound/|grad F|</c> the reported figures are built from, read off the public solid
    /// field — and measured by marching out along the normal until the SHEET field turns
    /// positive, both ways. The two share nothing but the surface point; the residual is the
    /// curvature term the first-order relation drops, and it shrinks with the thickness.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void TheFirstOrderWall_MatchesADirectMarchOfTheField(TpmsKind kind)
    {
        const double Cell = 6, Thickness = 0.12;
        var reported = Tpms.WallThickness(kind, Cell, Thickness);
        var sheet = Sdf.TpmsSheet(kind, Cell, Thickness);
        var surface = Sdf.TpmsSolid(kind, Cell);

        int points = 0, thick = 0;
        double worstRelative = 0, minMarched = double.PositiveInfinity, maxMarched = 0;
        var rng = new Random(515);
        for (int i = 0; i < 40000 && points < 400; i++)
        {
            var p = new Vector3d(
                rng.NextDouble() * Cell, rng.NextDouble() * Cell, rng.NextDouble() * Cell);
            // Land on the mid-surface: the solid field is 1-Lipschitz and signed, so a few
            // Newton steps along its own normal converge from anywhere near it.
            for (int step = 0; step < 40; step++)
            {
                double d = surface.Evaluate(p);
                if (Math.Abs(d) < 1e-12)
                    break;
                p -= surface.Normal(p) * d;
            }
            if (Math.Abs(surface.Evaluate(p)) > 1e-9)
                continue;
            var n = surface.Normal(p);
            double outward = March(sheet, p, n, Cell);
            double inward = March(sheet, p, -n, Cell);
            if (double.IsNaN(outward) || double.IsNaN(inward))
                continue;

            // The solid field IS |F| / (bound·omega) up to sign, so its own slope is
            // |grad F| / bound and the predicted wall is t divided by it.
            const double H = 1e-6;
            double gx = surface.Evaluate(p + new Vector3d(H, 0, 0)) - surface.Evaluate(p - new Vector3d(H, 0, 0));
            double gy = surface.Evaluate(p + new Vector3d(0, H, 0)) - surface.Evaluate(p - new Vector3d(0, H, 0));
            double gz = surface.Evaluate(p + new Vector3d(0, 0, H)) - surface.Evaluate(p - new Vector3d(0, 0, H));
            double slope = new Vector3d(gx, gy, gz).Length / (2 * H);
            if (slope <= 0)
                continue;

            double predicted = Thickness / slope;
            double marched = outward + inward;
            minMarched = Math.Min(minMarched, marched);
            maxMarched = Math.Max(maxMarched, marched);
            points++;

            // The relation is FIRST ORDER, so it claims the regime where the sheet's two sides
            // are locally parallel — and the EXCESS FACTOR is the measure of that, since it is
            // how fast the gradient is changing across the band. Past a factor of two the band
            // is no longer thin against the surface's own curvature (at Lidinoid's near-critical
            // point the level set is nearly pinching, so a march along the normal reaches a
            // different sheet entirely). The gate is that condition rather than a list of
            // surfaces, and the excluded points are COUNTED so the exemption cannot go quiet.
            if (predicted > 2 * Thickness)
            {
                thick++;
                continue;
            }
            worstRelative = Math.Max(worstRelative, Math.Abs(marched - predicted) / predicted);
        }

        Assert.True(points > 100, $"{kind}: only {points} usable surface points");
        output.WriteLine(
            $"{kind,-14} worst point-by-point |marched − first order| = {worstRelative:P1} over " +
            $"{points - thick} parallel-band points ({thick} past the regime);   marched span " +
            $"[{minMarched:0.####}, {maxMarched:0.####}], reported [{reported.Minimum:0.####}, " +
            $"{reported.Median:0.####}, {reported.Maximum:0.####}]");

        Assert.True(worstRelative < 0.03,
            $"{kind}: the first-order wall is off by {worstRelative:P1} at some thin-wall point");
        // The reported MINIMUM is the claim "the nominal thickness is a floor", so it is
        // two-sided; the reported MAXIMUM is the first-order figure and over-states wherever
        // the gradient nearly vanishes, so what is asserted of it is that it is an upper bound.
        Assert.True(minMarched >= reported.Minimum * 0.97,
            $"{kind}: marched {minMarched:R} below the reported minimum {reported.Minimum:R}");
        Assert.True(maxMarched <= reported.Maximum,
            $"{kind}: marched {maxMarched:R} above the reported maximum {reported.Maximum:R}");
    }

    /// <summary>
    /// The nominal thickness IS the minimum wall, which is the claim
    /// <see cref="Sdf.TpmsSheet(TpmsKind, double, double)"/> makes and the reason the excess is
    /// in the safe direction. Equivalently the minimum factor is 1, attained where the local
    /// gradient reaches the global maximum — and the surfaces whose maximum is NOT on the
    /// surface (Neovius, Lidinoid) are exactly the ones whose minimum factor exceeds 1, which is
    /// the same fact read the other way.
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void TheNominalThicknessIsAMinimumWall_AndTheExcessIsReported(TpmsKind kind)
    {
        var wall = Tpms.WallThickness(kind, 6, 1.0);
        output.WriteLine(
            $"{kind,-14} nominal 1.0 -> wall [{wall.Minimum:0.###}, {wall.Median:0.###}, " +
            $"{wall.Maximum:0.###}]   median excess {wall.MedianExcess:0.##}, worst {wall.WorstExcess:0.##}");

        Assert.True(wall.Minimum >= wall.Nominal * 0.999,
            $"{kind}: the wall falls BELOW the nominal thickness ({wall.Minimum:R})");
        Assert.True(wall.Median >= wall.Minimum && wall.Maximum >= wall.Median);
        Assert.True(wall.MedianExcess >= 1 && wall.WorstExcess >= wall.MedianExcess);
    }

    /// <summary>
    /// The excess is a property of the SURFACE, not of the thickness, so the whole distribution
    /// scales linearly — which is what makes <see cref="Tpms.SheetForWallThickness"/> one
    /// division rather than an iteration, and is worth asserting because it is the assumption
    /// the solve rests on.
    /// </summary>
    [Fact]
    public void TheWallScalesLinearlyWithTheNominalThickness()
    {
        foreach (TpmsKind kind in Enum.GetValues<TpmsKind>())
        {
            var one = Tpms.WallThickness(kind, 6, 1.0);
            var three = Tpms.WallThickness(kind, 6, 3.0);
            Assert.Equal(3 * one.Minimum, three.Minimum, 12);
            Assert.Equal(3 * one.Median, three.Median, 12);
            Assert.Equal(3 * one.Maximum, three.Maximum, 12);
        }
    }

    /// <summary>
    /// The engineering call: ask for a wall, get a sheet whose median wall is that. And the
    /// solve must be MEASURABLY different from setting the thickness to the wall, or the
    /// feature is a rename — the gap is the excess factor, largest for Neovius and Lidinoid,
    /// which are the two the backlog named.
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void SheetForWallThickness_LandsOnTheWallItWasAskedFor(TpmsKind kind)
    {
        const double Cell = 6, Wall = 0.8;
        var (field, wall) = Tpms.SheetForWallThickness(kind, Cell, Wall);
        output.WriteLine(
            $"{kind,-14} wall {Wall} -> nominal thickness {wall.Nominal:0.####} " +
            $"(the naive setting would be {Wall})");

        Assert.Equal(Wall, wall.Median, 9);
        Assert.NotNull(field);
        Assert.True(wall.Nominal <= Wall,
            $"{kind}: the nominal thickness must be at or under the wall, not {wall.Nominal:R}");
    }

    [Fact]
    public void SheetForWallThickness_IsMeasurablyNotJustTheThicknessRenamed()
    {
        // Neovius and Lidinoid are the two the excess makes a usability problem for: measured
        // median excesses 2.32 and 1.65, so the solve takes a third and two fifths off.
        foreach (var kind in new[] { TpmsKind.Neovius, TpmsKind.Lidinoid })
        {
            var (_, wall) = Tpms.SheetForWallThickness(kind, 6, 1.0);
            output.WriteLine($"{kind,-14} wall 1.0 -> nominal {wall.Nominal:0.####}");
            Assert.True(wall.Nominal < 0.65,
                $"{kind}: the solve barely moved the thickness ({wall.Nominal:R})");
        }
        // ... and the gyroid, whose gradient is nearly uniform, barely moves — which is what
        // makes the comparison a statement about the surfaces rather than about the solve.
        var (_, gyroid) = Tpms.SheetForWallThickness(TpmsKind.Gyroid, 6, 1.0);
        output.WriteLine($"Gyroid         wall 1.0 -> nominal {gyroid.Nominal:0.####}");
        Assert.True(gyroid.Nominal > 0.8, $"gyroid: unexpectedly large correction ({gyroid.Nominal:R})");
    }

    [Fact]
    public void WallThickness_RefusesNonsense()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Tpms.WallThickness(TpmsKind.Gyroid, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Tpms.WallThickness(TpmsKind.Gyroid, 5, -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Tpms.SheetForWallThickness(TpmsKind.Gyroid, 5, 0));
    }

    /// <summary>How far along <paramref name="direction"/> the sheet field stays negative, by
    /// bisection — NaN when the point is not inside the sheet at all.</summary>
    private static double March(Sdf sheet, in Vector3d from, in Vector3d direction, double limit)
    {
        if (sheet.Evaluate(from) >= 0)
            return double.NaN;
        double lo = 0, hi = 0.02;
        while (hi < limit && sheet.Evaluate(from + direction * hi) < 0)
        {
            lo = hi;
            hi *= 1.5;
        }
        if (hi >= limit)
            return double.NaN;
        for (int i = 0; i < 60; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (sheet.Evaluate(from + direction * mid) < 0)
                lo = mid;
            else
                hi = mid;
        }
        return 0.5 * (lo + hi);
    }
}
