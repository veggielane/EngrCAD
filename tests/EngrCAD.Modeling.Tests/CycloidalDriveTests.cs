using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

public class CycloidalDriveTests
{
    private static CycloidalDiscSpec Standard() =>
        new(pins: 11, pinCircleRadius: 50, pinRadius: 3, eccentricity: 1.5);

    // ---- the identities, as arithmetic ----

    [Fact]
    public void SpecIdentities_HoldAsArithmetic()
    {
        var spec = Standard();
        Assert.Equal(10, spec.Lobes);

        // The two classic arrangements give different numbers off the same geometry, so both
        // are named rather than one being called "the" ratio.
        Assert.Equal(10, spec.ReductionRatio, 12);            // pins fixed, disc output
        Assert.Equal(11, spec.RingOutputRatio, 12);           // disc held, ring output
        Assert.Equal((double)spec.Lobes / (spec.Pins - spec.Lobes), spec.ReductionRatio, 12);
        Assert.Equal((double)spec.Pins / (spec.Pins - spec.Lobes), spec.RingOutputRatio, 12);

        // The SIGN is the trap: the disc counter-rotates.
        Assert.Equal(-0.1, spec.DiscTurnsPerInputTurn, 12);
        Assert.True(spec.DiscRotation(2 * Math.PI) < 0);
        Assert.Equal(-2 * Math.PI / 10, spec.DiscRotation(2 * Math.PI), 12);
        Assert.Equal(1.0 / spec.ReductionRatio, Math.Abs(spec.DiscTurnsPerInputTurn), 12);

        // Eccentricity identity, and how the roller offset moves the extremes.
        Assert.Equal(3.0, spec.LobeDepth, 12);
        Assert.Equal(50 + 1.5 - 3, spec.MaximumRadius, 12);
        Assert.Equal(50 - 1.5 - 3, spec.MinimumRadius, 12);
        Assert.Equal(spec.LobeDepth, spec.MaximumRadius - spec.MinimumRadius, 12);

        // Green's closed form for the roller-centre curve.
        Assert.Equal(Math.PI * (50 * 50 + 1.5 * 1.5 * 11), spec.RollerCentreCurveArea, 9);

        // The pose convention: at input angle 0 the disc sits at (e, 0) unrotated.
        Assert.Equal(new Vector2d(1.5, 0), spec.DiscCentre(0));
        Assert.Equal(0, spec.DiscRotation(0), 12);
        Assert.Equal(new Vector2d(50, 0), spec.PinCentre(0));
    }

    [Fact]
    public void LobeDifferenceOtherThanOne_RefusedWithItsReason()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CycloidalDiscSpec(pins: 11, pinCircleRadius: 50, pinRadius: 3,
                eccentricity: 1.5, lobes: 9));
        // The refusal states WHY it is structural rather than a v1 gap.
        Assert.Contains("one lobe fewer", ex.Message);
        Assert.Contains("d = 1", ex.Message);
        // The stated count is accepted.
        Assert.Equal(10, new CycloidalDiscSpec(11, 50, 3, 1.5, lobes: 10).Lobes);
    }

    [Fact]
    public void LobeTipCurvature_AgreesWithTheGeneralMaximumAndWithTheGeneratedGeometry()
    {
        var spec = Standard();

        // (a) Two closed forms: the specialised tip value (R + eN²)/(R + eN)² and the general
        // maximum over the one-variable family κ(u) = (A − Bu)/(P − Qu)^{3/2}.
        Assert.Equal(spec.LobeTipCurvature, spec.MaximumCurvature, 12);
        Assert.Equal(1 / spec.LobeTipCurvature, spec.MinimumCurvatureRadius, 9);

        // (b) An INDEPENDENT route: measure the curvature off the generated roller-centre
        // sketch by circumscribing a circle through three boundary points around the lobe tip.
        // Nothing of the closed form is reused - the points come from the sketch's own exact
        // signed distance.
        var profile = CycloidalDrives.RollerCentreCurve(spec);
        var region = profile.Sketch.ToRegion();
        double tipAngle = ExtremeAngle(region, spec.LobePeriod, wantMaximum: true);
        double delta = 0.06;   // ~3 mm of chord at radius 51.5 - wide enough that the fit
                               // deviation cannot dominate the circumradius
        var a = BoundaryPoint(region, tipAngle - delta);
        var b = BoundaryPoint(region, tipAngle);
        var c = BoundaryPoint(region, tipAngle + delta);
        double measured = 1 / Circumradius(a, b, c);
        double relative = Math.Abs(measured - spec.LobeTipCurvature) / spec.LobeTipCurvature;
        Assert.True(relative < 0.02,
            $"measured tip curvature {measured:0.######} against the closed form "
            + $"{spec.LobeTipCurvature:0.######} ({relative:0.###e0} relative)");
    }

    // ---- areas, from closed forms ----

    [Fact]
    public void RollerCentreCurve_Area_MatchesItsClosedForm()
    {
        var spec = Standard();
        var profile = CycloidalDrives.RollerCentreCurve(spec);
        Assert.Equal(0, profile.Offset);
        Assert.Equal(spec.RollerCentreCurveArea, profile.ClosedFormArea, 9);
        double measured = profile.Sketch.Area();
        double bound = profile.MaxFitDeviation * profile.RollerCentreCurveLength * 2 + 1e-9;
        Assert.True(Math.Abs(measured - spec.RollerCentreCurveArea) < bound,
            $"sketch area {measured} against pi(R^2 + e^2 N) = {spec.RollerCentreCurveArea} "
            + $"(bound {bound})");
    }

    [Fact]
    public void DiscArea_MatchesTheInwardOffsetIdentity()
    {
        // A(offset) = A(C) − R_r·L + π·R_r² for any simple closed curve, because its total
        // turning is exactly one revolution. L is quadrature, but the integrand is smooth and
        // periodic so the trapezoid rule is spectrally accurate.
        var spec = Standard();
        var profile = CycloidalDrives.Disc(spec);
        double expected = spec.RollerCentreCurveArea
            - spec.PinRadius * profile.RollerCentreCurveLength
            + Math.PI * spec.PinRadius * spec.PinRadius;
        Assert.Equal(expected, profile.ClosedFormArea, 9);

        double measured = profile.Sketch.Area();
        double bound = profile.MaxFitDeviation * profile.RollerCentreCurveLength * 2 + 1e-9;
        Assert.True(Math.Abs(measured - expected) < bound,
            $"sketch area {measured} against {expected} (bound {bound})");

        // The quadrature is checked against a bracket no formula supplies: the curve is longer
        // than the circle it oscillates about is at its valleys and shorter than the peak one's.
        double r = spec.PinCircleRadius, e = spec.Eccentricity;
        Assert.InRange(profile.RollerCentreCurveLength, 2 * Math.PI * (r - e), 2 * Math.PI * (r + e));
    }

    [Fact]
    public void LobeDepth_MeasuresTwiceTheEccentricity_OffTheGeneratedSketch()
    {
        var spec = Standard();
        foreach (var profile in new[]
        {
            CycloidalDrives.RollerCentreCurve(spec),
            CycloidalDrives.Disc(spec),
        })
        {
            var region = profile.Sketch.ToRegion();
            double peak = BoundaryPoint(region, ExtremeAngle(region, spec.LobePeriod, true)).Length;
            double valley = BoundaryPoint(region, ExtremeAngle(region, spec.LobePeriod, false)).Length;
            double tolerance = profile.MaxFitDeviation + 1e-9;
            Assert.True(Math.Abs(peak - valley - spec.LobeDepth) < 2 * tolerance,
                $"lobe depth measured {peak - valley} against 2e = {spec.LobeDepth} "
                + $"(offset {profile.Offset})");
            Assert.True(Math.Abs(peak - (spec.PinCircleRadius + spec.Eccentricity - profile.Offset)) < tolerance);
            Assert.True(Math.Abs(valley - (spec.PinCircleRadius - spec.Eccentricity - profile.Offset)) < tolerance);
        }
    }

    // ---- the kinematics, measured from CONTACT ----
    //
    // The derivation says every pin rides the SAME curve in the disc's frame, so at the correct
    // pose relation the sketch's exact signed distance reads exactly the pin radius at every
    // pin and every input angle. That single identity is simultaneously the clash check (no pin
    // ever reads LESS, so nothing interferes) and the ratio measurement (no other rate holds it).

    [Fact]
    public void EveryPinRidesTheProfile_ThroughAFullInputRotation()
    {
        var spec = Standard();
        var profile = CycloidalDrives.Disc(spec);
        var region = profile.Sketch.ToRegion();

        double worstResidual = 0, leastClearance = double.PositiveInfinity;
        const int steps = 72;
        for (int step = 0; step < steps; step++)
        {
            double phi = 2 * Math.PI * step / steps;
            for (int j = 0; j < spec.Pins; j++)
            {
                double sd = region.SignedDistance(spec.WorldToDisc(spec.PinCentre(j), phi));
                worstResidual = Math.Max(worstResidual, Math.Abs(sd - spec.PinRadius));
                leastClearance = Math.Min(leastClearance, sd);
            }
        }
        // The residual IS the fit deviation and nothing else - the pose relation contributes
        // exactly zero, which is what "derived rather than transcribed" buys. The bound is
        // TWICE the reported deviation because that figure measures one Hausdorff direction
        // (true curve to chain) while a pin centre reads the other; the sharp statement is the
        // RATIO, asserted beside it, which comes out at 1.002 - if the pose relation carried
        // any error of its own this would be orders larger, since nothing else here is
        // approximate.
        Assert.True(worstResidual <= 2 * profile.MaxFitDeviation + 1e-9,
            $"pin contact residual {worstResidual:0.###e0} exceeds twice the fit deviation "
            + $"{profile.MaxFitDeviation:0.###e0}");
        Assert.True(worstResidual / profile.MaxFitDeviation < 1.05,
            $"pin contact residual {worstResidual:0.###e0} is {worstResidual / profile.MaxFitDeviation:0.###}x "
            + "the fit deviation, so something other than the fit is contributing");
        // And no pin ever reads less than its own radius: nothing clashes.
        Assert.True(leastClearance > spec.PinRadius - 2 * profile.MaxFitDeviation - 1e-9,
            $"least pin clearance {leastClearance} is under the pin radius {spec.PinRadius}");
    }

    [Fact]
    public void OnlyTheDerivedRate_KeepsThePinsOnTheProfile()
    {
        // The ratio MEASURED rather than asserted: sweep candidate disc rates -1/k and see
        // which one keeps every pin on the profile. Only k = lobes does, and the sign matters -
        // running the disc forward at the same magnitude buries the pins.
        var spec = Standard();
        var profile = CycloidalDrives.Disc(spec);
        var region = profile.Sketch.ToRegion();

        double Penetration(double rate)
        {
            double worst = 0;
            for (int step = 0; step < 36; step++)
            {
                double phi = 2 * Math.PI * step / 36;
                double xi = rate * phi;
                double cos = Math.Cos(-xi), sin = Math.Sin(-xi);
                var origin = spec.DiscCentre(phi);
                for (int j = 0; j < spec.Pins; j++)
                {
                    var w = spec.PinCentre(j) - origin;
                    var q = new Vector2d(w.X * cos - w.Y * sin, w.X * sin + w.Y * cos);
                    worst = Math.Max(worst, spec.PinRadius - region.SignedDistance(q));
                }
            }
            return worst;
        }

        double floor = 2 * profile.MaxFitDeviation + 1e-9;
        double correct = Penetration(-1.0 / spec.Lobes);
        Assert.True(correct <= floor, $"the derived rate penetrated by {correct:0.###e0}");
        foreach (int k in new[] { 8, 9, 11, 12 })
        {
            double wrong = Penetration(-1.0 / k);
            Assert.True(wrong > 100 * floor,
                $"rate -1/{k} penetrated by only {wrong:0.###e0}; the instrument cannot see a rate");
        }
        // The sign: co-rotating at the right magnitude is just as wrong.
        Assert.True(Penetration(1.0 / spec.Lobes) > 100 * floor);
    }

    // ---- refusals, by name ----

    [Fact]
    public void PinCircleTooSmallForTheEccentricity_Refused()
    {
        // R = eN is where the roller-centre curve's own tangent stalls.
        var spec = new CycloidalDiscSpec(pins: 11, pinCircleRadius: 16, pinRadius: 1, eccentricity: 1.5);
        var ex = Assert.Throws<ArgumentException>(() => CycloidalDrives.RollerCentreCurve(spec));
        Assert.Contains("tangent", ex.Message);
    }

    [Fact]
    public void PinLargerThanTheLobeTipRadiusOfCurvature_Refused()
    {
        var spec = new CycloidalDiscSpec(pins: 11, pinCircleRadius: 50, pinRadius: 25, eccentricity: 1.5);
        Assert.True(spec.PinRadius > spec.MinimumCurvatureRadius);
        var ex = Assert.Throws<ArgumentException>(() => CycloidalDrives.Disc(spec));
        Assert.Contains("cusp", ex.Message);
        // The roller-centre curve itself is fine - only the OFFSET is refused.
        Assert.True(CycloidalDrives.RollerCentreCurve(spec).Sketch.Area() > 0);
    }

    [Fact]
    public void DegenerateInputs_Refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CycloidalDiscSpec(2, 50, 3, 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CycloidalDiscSpec(11, 0, 3, 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CycloidalDiscSpec(11, 50, 0, 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CycloidalDiscSpec(11, 50, 3, 0));
        var spec = Standard();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CycloidalDrives.DiscShape(spec, thickness: 8, boreDiameter: 2 * spec.MinimumRadius));
        Assert.Throws<ArgumentOutOfRangeException>(() => CycloidalDrives.DiscShape(spec, thickness: 0));
    }

    // ---- solids ----

    [Fact]
    public void DiscShape_MeshVolume_MatchesTheClosedFormArea()
    {
        var spec = Standard();
        var profile = CycloidalDrives.Disc(spec);
        var shape = CycloidalDrives.DiscShape(spec, thickness: 8, boreDiameter: 20);
        var mesh = shape.ToMesh();
        Assert.True(mesh.IsClosed);
        double expected = (profile.ClosedFormArea - Math.PI * 100) * 8;
        double relative = Math.Abs(mesh.Volume() - expected) / expected;
        Assert.True(relative < 0.01,
            $"volume {mesh.Volume()} against {expected} ({relative:0.###e0} relative)");
    }

    [Fact]
    public void PinShapes_SitOnThePinCircleInIndexOrder()
    {
        var spec = Standard();
        var pins = CycloidalDrives.PinShapes(spec, length: 10);
        Assert.Equal(spec.Pins, pins.Count);
        for (int j = 0; j < spec.Pins; j++)
        {
            var bounds = pins[j].Bounds();
            var centre = spec.PinCentre(j);
            Assert.True(Math.Abs(bounds.Center.X - centre.X) < 0.05);
            Assert.True(Math.Abs(bounds.Center.Y - centre.Y) < 0.05);
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The boundary point along the ray at <paramref name="theta"/>, found by
    /// bisecting the sketch's own exact signed distance (the region is star-shaped about the
    /// disc axis, so the ray crosses exactly once).</summary>
    private static Vector2d BoundaryPoint(IPlanarRegion region, double theta)
    {
        var direction = new Vector2d(Math.Cos(theta), Math.Sin(theta));
        double inside = 0, outside = 1;
        while (region.SignedDistance(direction * outside) < 0)
        {
            inside = outside;
            outside *= 2;
            if (outside > 1e6)
                throw new InvalidOperationException("the ray never leaves the region");
        }
        for (int i = 0; i < 80; i++)
        {
            double mid = (inside + outside) / 2;
            if (region.SignedDistance(direction * mid) < 0) inside = mid; else outside = mid;
        }
        return direction * ((inside + outside) / 2);
    }

    /// <summary>The polar angle of the boundary's greatest (or least) radius within one lobe,
    /// by a coarse scan refined by golden section — measured, never assumed to sit at a
    /// parameter the construction used.</summary>
    private static double ExtremeAngle(IPlanarRegion region, double lobePeriod, bool wantMaximum)
    {
        double Radius(double t) => BoundaryPoint(region, t).Length;
        double sign = wantMaximum ? 1 : -1;
        double best = double.NegativeInfinity, bestAngle = 0;
        const int scan = 64;
        for (int i = 0; i <= scan; i++)
        {
            double t = lobePeriod * i / scan;
            double value = sign * Radius(t);
            if (value > best) { best = value; bestAngle = t; }
        }
        double lo = bestAngle - lobePeriod / scan, hi = bestAngle + lobePeriod / scan;
        for (int i = 0; i < 60; i++)
        {
            double a = lo + (hi - lo) / 3, b = hi - (hi - lo) / 3;
            if (sign * Radius(a) > sign * Radius(b)) hi = b; else lo = a;
        }
        return (lo + hi) / 2;
    }

    /// <summary>Radius of the circle through three points (exact; no fitting).</summary>
    private static double Circumradius(in Vector2d a, in Vector2d b, in Vector2d c)
    {
        double ab = (b - a).Length, bc = (c - b).Length, ca = (a - c).Length;
        double twiceArea = Math.Abs((b - a).Cross(c - a));
        return ab * bc * ca / (2 * twiceArea);
    }
}
