using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

public class GearTests
{
    private const double PressureAngle20 = 20 * Math.PI / 180;

    // ---- base-circle identities as arithmetic ----

    [Fact]
    public void SpecIdentities_HoldAsArithmetic()
    {
        var spec = new GearSpec(module: 2, teeth: 24, pressureAngleDegrees: 20, profileShift: 0.15);
        double cos = Math.Cos(PressureAngle20);

        // Base circle and base pitch, each reached by TWO independent arithmetic routes.
        Assert.True(Math.Abs(spec.BaseDiameter - spec.PitchDiameter * cos) < 1e-12);
        Assert.True(Math.Abs(spec.BasePitch - spec.CircularPitch * cos) < 1e-12);
        Assert.True(Math.Abs(spec.BasePitch - Math.PI * spec.BaseDiameter / spec.Teeth) < 1e-12);

        // Whole depth is shift-independent: d_a − d_f = 2m(h_a* + h_f*).
        Assert.True(Math.Abs(spec.TipDiameter - spec.RootDiameter - 2 * 2 * (1.0 + 1.25)) < 1e-12);

        // Tooth thickness: a shifted pair with x and −x shares the circular pitch,
        // which is why x₁ + x₂ = 0 keeps the standard centre distance backlash-free.
        var opposite = new GearSpec(2, 24, 20, -0.15);
        Assert.True(Math.Abs(
            spec.ToothThicknessAtPitch + opposite.ToothThicknessAtPitch - spec.CircularPitch) < 1e-12);

        // The classical undercut limit at 20°, x = 0: 2/sin²α = 17.097…
        var standard = new GearSpec(1, 18);
        double sin = Math.Sin(PressureAngle20);
        Assert.True(Math.Abs(standard.MinimumTeethWithoutUndercut - 2 / (sin * sin)) < 1e-12);
        Assert.True(Math.Abs(standard.MinimumTeethWithoutUndercut - 17.097) < 1e-3);
        Assert.True(Math.Abs(standard.MinimumProfileShift - (1 - 18 * sin * sin / 2)) < 1e-12);
    }

    // ---- the fit contract: deviation from the closed-form involute ----

    [Fact]
    public void Flank_MatchesClosedFormInvolute_WithinStatedTolerance()
    {
        var profile = Gears.Spur(new GearSpec(module: 2, teeth: 20));
        Assert.True(profile.MaxFitDeviation > 0);
        Assert.True(profile.MaxFitDeviation <= profile.FitTolerance,
            $"reported deviation {profile.MaxFitDeviation:0.###e0} exceeds tolerance {profile.FitTolerance:0.###e0}");
        Assert.InRange(profile.CurvesPerFlank, 2, 120);

        // Independent, denser measurement: 2000 closed-form involute samples against the
        // whole outline (the nearest outline feature to an on-flank point is the flank).
        var g = profile.Geometry;
        var curves = profile.Sketch.ToCurves();
        double worst = 0;
        for (int i = 0; i <= 2000; i++)
        {
            double t = g.RollAtRoot + (g.RollAtTip - g.RollAtRoot) * i / 2000;
            var p = Involute(g.BaseRadius, g.InvoluteOrigin, t);
            double best = double.PositiveInfinity;
            foreach (var curve in curves)
                best = Math.Min(best, curve.DistanceTo(p));
            worst = Math.Max(worst, best);
        }
        Assert.True(worst <= profile.FitTolerance,
            $"independent deviation {worst:0.###e0} exceeds tolerance {profile.FitTolerance:0.###e0}");
    }

    // ---- tooth thickness at the pitch circle, measured off the sketch ----

    [Fact]
    public void ToothThickness_AtPitchCircle_MeasuredOffTheSketch()
    {
        var spec = new GearSpec(module: 2, teeth: 20, pressureAngleDegrees: 20, profileShift: 0.2);
        var profile = Gears.Spur(spec);
        var region = profile.Sketch.ToRegion();
        double r = spec.PitchDiameter / 2;
        double psi = profile.Geometry.HalfToothAngle;
        double gapHalf = Math.PI / spec.Teeth - psi;

        // Bisect the boundary crossing along the pitch circle on both flanks of the
        // +X tooth. The region's parity is exact, so the crossing is found to bisection
        // precision; the flank itself sits within the fit tolerance of the true
        // involute, and a normal deviation δ shifts the crossing along the circle by at
        // most δ/cos α — hence the 3δ band.
        double left = BisectCrossing(region, r, 0, psi + 0.8 * gapHalf);
        double right = BisectCrossing(region, r, 0, -(psi + 0.8 * gapHalf));
        double measured = r * (left - right);
        double band = 3 * profile.FitTolerance;
        Assert.True(Math.Abs(left - psi) * r <= band, $"left flank at {left}, expected {psi}");
        Assert.True(Math.Abs(right + psi) * r <= band, $"right flank at {right}, expected {-psi}");
        Assert.True(Math.Abs(measured - spec.ToothThicknessAtPitch) <= 2 * band,
            $"thickness {measured} vs {spec.ToothThicknessAtPitch}");
    }

    // ---- backlash + the measurement identities ----

    /// <summary>The allowance thins the drawn tooth by exactly j — the same
    /// pitch-circle bisection instrument as the nominal thickness test, on a
    /// backlashed spec, whose <c>ToothThicknessAtPitch</c> already carries the
    /// subtraction (one source of truth, so the drawn flank and the arithmetic
    /// cannot disagree about what the allowance did).</summary>
    [Fact]
    public void Backlash_ThinsTheMeasuredToothThicknessByExactlyJ()
    {
        var nominal = new GearSpec(module: 2, teeth: 20, pressureAngleDegrees: 20);
        var thinned = nominal with { Backlash = 0.12 };
        Assert.Equal(nominal.ToothThicknessAtPitch - 0.12, thinned.ToothThicknessAtPitch, 12);

        var profile = Gears.Spur(thinned);
        var region = profile.Sketch.ToRegion();
        double r = thinned.PitchDiameter / 2;
        double psi = profile.Geometry.HalfToothAngle;
        double gapHalf = Math.PI / thinned.Teeth - psi;
        double left = BisectCrossing(region, r, 0, psi + 0.8 * gapHalf);
        double right = BisectCrossing(region, r, 0, -(psi + 0.8 * gapHalf));
        double measured = r * (left - right);
        double band = 3 * profile.FitTolerance;
        Assert.True(Math.Abs(measured - thinned.ToothThicknessAtPitch) <= 2 * band,
            $"thinned thickness {measured} vs {thinned.ToothThicknessAtPitch}");
    }

    [Fact]
    public void Backlash_ZeroIsBitIdentical_AndEatingTheToothIsRefused()
    {
        var spec = new GearSpec(module: 2, teeth: 20);
        var a = Gears.Spur(spec).Sketch.ToCurves();
        var b = Gears.Spur(spec with { Backlash = 0 }).Sketch.ToCurves();
        Assert.Equal(a.Count, b.Count);   // the exact-zero branch: same construction

        var thrown = Assert.Throws<ArgumentOutOfRangeException>(
            () => Gears.Spur(spec with { Backlash = spec.ToothThicknessAtPitch + 1 }));
        Assert.Contains("eats the whole", thrown.Message);
    }

    /// <summary>The span identity: W = (k−1)·p_b + cos α·(s + m·z·inv α) must equal the
    /// textbook closed form at zero backlash, drop by exactly j·cos α with the
    /// allowance, and agree with the SKETCH through the measured thickness — which is
    /// what holds the drawn flank to the arithmetic the caliper dimension claims.</summary>
    [Fact]
    public void SpanOverTeeth_MatchesTheTextbookFormAndTheMeasuredThickness()
    {
        var spec = new GearSpec(module: 2, teeth: 20, pressureAngleDegrees: 20, profileShift: 0.2);
        double alpha = Math.PI / 9;
        double inv = GearSpec.InvoluteFunction(alpha);
        double textbook = 2 * Math.Cos(alpha) * ((3 - 0.5) * Math.PI + 20 * inv)
            + 2 * 0.2 * 2 * Math.Sin(alpha);
        Assert.Equal(textbook, spec.SpanOverTeeth(3), 10);

        var thinned = spec with { Backlash = 0.1 };
        Assert.Equal(spec.SpanOverTeeth(3) - 0.1 * Math.Cos(alpha), thinned.SpanOverTeeth(3), 12);

        // Off the sketch: rebuild W from the MEASURED pitch thickness.
        var profile = Gears.Spur(thinned);
        var region = profile.Sketch.ToRegion();
        double r = thinned.PitchDiameter / 2;
        double psi = profile.Geometry.HalfToothAngle;
        double gapHalf = Math.PI / thinned.Teeth - psi;
        double measured = r * (BisectCrossing(region, r, 0, psi + 0.8 * gapHalf)
            - BisectCrossing(region, r, 0, -(psi + 0.8 * gapHalf)));
        double rebuilt = 2 * thinned.BasePitch
            + Math.Cos(alpha) * (measured + 2 * 20 * inv);
        Assert.True(Math.Abs(rebuilt - thinned.SpanOverTeeth(3)) <= 6 * profile.FitTolerance,
            $"span {thinned.SpanOverTeeth(3)} vs sketch-rebuilt {rebuilt}");

        var thrown = Assert.Throws<ArgumentOutOfRangeException>(() => spec.SpanOverTeeth(9));
        Assert.Contains("outside the involute flank", thrown.Message);
    }

    /// <summary>The over-pins dimension against an INDEPENDENT sketch oracle: a pin
    /// seated in a tooth space touches both flanks, i.e. the region's own signed
    /// distance at the pin centre equals the pin radius — bisected along the space
    /// centreline, sharing no line with the involute-function arithmetic it checks.</summary>
    [Fact]
    public void MeasurementOverPins_MatchesASketchSeatedPin()
    {
        var spec = new GearSpec(module: 2, teeth: 20, pressureAngleDegrees: 20)
            { Backlash = 0.1 };
        double pin = 3.5;
        double claimed = spec.MeasurementOverPins(pin);

        var region = Gears.Spur(spec).Sketch.ToRegion();
        double spaceAzimuth = Math.PI / spec.Teeth;   // the +X tooth is centred on 0
        double Distance(double radius) => region.SignedDistance(new Vector2d(
            radius * Math.Cos(spaceAzimuth), radius * Math.Sin(spaceAzimuth)));

        // sd grows with radius over the flank-contact range: bisect sd = pin/2.
        double lo = spec.RootDiameter / 2, hi = spec.TipDiameter / 2 + pin;
        Assert.True(Distance(lo) < pin / 2 && Distance(hi) > pin / 2, "bracket");
        for (int i = 0; i < 60; i++)
        {
            double mid = (lo + hi) / 2;
            if (Distance(mid) < pin / 2)
                lo = mid;
            else
                hi = mid;
        }
        double seated = 2 * (lo + hi) / 2 + pin;   // even tooth count: across a diameter
        double band = 6 * Gears.Spur(spec).FitTolerance;
        Assert.True(Math.Abs(seated - claimed) <= band,
            $"over pins {claimed} vs sketch-seated {seated}");

        // Refusals, both directions, by name.
        Assert.Contains("below the base circle",
            Assert.Throws<ArgumentOutOfRangeException>(
                () => spec.MeasurementOverPins(0.05)).Message);
        Assert.Contains("clear of the tooth tips",
            Assert.Throws<ArgumentOutOfRangeException>(
                () => spec.MeasurementOverPins(400)).Message);
    }

    // ---- the keyed bore ----

    /// <summary>The keyed bore's area is CLOSED FORM — the notch corners sit exactly on
    /// the bore circle, so the union is πr² plus the rectangle above the chord less the
    /// circular segment it overlaps — held against the sketch's own exact Area(), and
    /// the keyed gear's volume against the plain one less the notch prism.</summary>
    [Fact]
    public void KeyedBore_AreaAndVolume_MatchTheClosedForms()
    {
        var keyway = StandardKeys.For(20);          // 6 x 6, t2 = 2.8
        Assert.Equal(6, keyway.Width);
        Assert.Equal(2.8, keyway.HubDepth);

        double r = 10, half = keyway.Width / 2;
        double chord = Math.Sqrt(r * r - half * half);
        double area = Math.PI * r * r
            + keyway.Width * (r + keyway.HubDepth)
            - keyway.Width * chord / 2
            - r * r * Math.Asin(half / r);
        var bore = Gears.KeyedBore(20, keyway);
        Assert.Equal(area, bore.Area(), 9);

        var spec = new GearSpec(module: 2.5, teeth: 24);
        var plain = new Part("plain", Gears.SpurGear(spec, faceWidth: 8, boreDiameter: 20));
        var keyed = new Part("keyed", Gears.SpurGear(spec, faceWidth: 8, boreDiameter: 20, keyway));
        double notch = area - Math.PI * r * r;
        double expected = plain.MassProperties().Volume - notch * 8;
        double actual = keyed.MassProperties().Volume;
        // Mass-properties grade: both volumes ride tessellate-then-Richardson, and the
        // two bores chord their arcs independently, so the identity holds to ~1e-7
        // relative rather than to the sketch area's 1e-9 absolute.
        Assert.True(Math.Abs(actual - expected) <= 1e-6 * expected,
            $"keyed volume {actual} vs plain-minus-notch {expected}");
    }

    [Fact]
    public void KeyedBore_RefusalsFireByName()
    {
        // A keyway with no bore, a keyway into the root circle, a keyway wider than
        // its bore, and an off-table shaft — each named.
        var spec = new GearSpec(module: 2, teeth: 20);
        Assert.Contains("needs a bore", Assert.Throws<ArgumentOutOfRangeException>(
            () => Gears.SpurGear(spec, 8, keyway: StandardKeys.For(20))).Message);
        Assert.Contains("root circle", Assert.Throws<ArgumentOutOfRangeException>(
            () => Gears.SpurGear(spec, 8, boreDiameter: 30,
                keyway: new KeywaySpec(8, 4))).Message);
        Assert.Contains("does not fit", Assert.Throws<ArgumentOutOfRangeException>(
            () => Gears.KeyedBore(6, new KeywaySpec(8, 3))).Message);
        Assert.Contains("outside that range", Assert.Throws<ArgumentOutOfRangeException>(
            () => StandardKeys.For(100)).Message);
    }

    // ---- the exact area identity ----

    [Theory]
    [InlineData(2.0, 20, 0.0)]    // fillet tangent to the radial stretch (r_f < r_b)
    [InlineData(2.0, 20, 0.3)]    // shifted
    [InlineData(1.0, 40, -0.2)]   // fillet tangent to the involute itself
    public void Area_SketchAgreesWithClosedForm(double module, int teeth, double shift)
    {
        var profile = Gears.Spur(new GearSpec(module, teeth, 20, shift));
        var g = profile.Geometry;

        // Sanity: between the root and tip discs.
        Assert.InRange(profile.ClosedFormArea,
            Math.PI * g.RootRadius * g.RootRadius, Math.PI * g.TipRadius * g.TipRadius);

        // The sketch's own exact per-segment area against the Green's-theorem closed
        // form of the ideal outline. They may differ only where the fit deviates from
        // the involute: bound = flank count × flank arc length × deviation, doubled for
        // safety. Flank arc length is closed-form: r_b(t_tip² − t_root²)/2.
        double flankLength = g.BaseRadius * (g.RollAtTip * g.RollAtTip - g.RollAtRoot * g.RollAtRoot) / 2;
        double bound = 2 * (2 * teeth * flankLength * profile.MaxFitDeviation) + 1e-9;
        double area = profile.Sketch.Area();
        Assert.True(Math.Abs(area - profile.ClosedFormArea) <= bound,
            $"sketch area {area} vs closed form {profile.ClosedFormArea}, bound {bound:0.###e0}");
    }

    // ---- refusals, by name ----

    [Fact]
    public void Undercut_RefusedByName_AtTheExactLimit()
    {
        var ex = Assert.Throws<ArgumentException>(() => Gears.Spur(new GearSpec(1, 14)));
        Assert.Contains("undercut", ex.Message);
        Assert.Contains("17.1", ex.Message);          // names the limit
        Assert.Contains("profile shift", ex.Message); // names the way out

        // z = 17 is BELOW 17.097 — the exact limit, not the practical 14.
        Assert.Throws<ArgumentException>(() => Gears.Spur(new GearSpec(1, 17)));
        // 18 clears it; 14 clears it with enough profile shift (x_min = 0.181…).
        _ = Gears.Spur(new GearSpec(1, 18));
        _ = Gears.Spur(new GearSpec(1, 14, profileShift: 0.25));
    }

    [Fact]
    public void PointedTooth_Refused()
    {
        // z = 14, x = 1.1 clears the undercut limit but the tip thickness goes negative.
        var ex = Assert.Throws<ArgumentException>(() =>
            Gears.Spur(new GearSpec(1, 14, profileShift: 1.1)));
        Assert.Contains("point", ex.Message);
    }

    [Fact]
    public void OversizedRootFillet_Refused()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Gears.Spur(new GearSpec(1, 18) with { RootFilletCoefficient = 1.2 }));
        Assert.Contains("root gap", ex.Message);
    }

    // ---- conjugate action: the law of gearing asked, not assumed ----
    //
    // Two generated gears are placed at an EXTENDED centre distance (a₀ + 0.4, which
    // introduces real backlash so drive-flank contact is a transversal zero of the
    // clearance — at the standard distance a zero-backlash pair touches both flanks at
    // once and there is nothing to bisect). The involute's signature property is that
    // the transmission ratio is exactly z₁/z₂ at ANY centre distance, so the contact
    // angle offset β*(φ₁) = φ₂* + (z₁/z₂)·φ₁ − alignment must be CONSTANT through a
    // sweep long enough to hand contact from tooth pair to tooth pair.
    //
    // The mechanism solver is deliberately NOT the instrument here: Coupling.Gear
    // ENFORCES the ratio it would be asserting. Contact through the sketch's exact
    // signed distance derives the ratio from the drawn teeth.
    //
    // Tolerance derivation: each flank deviates ≤ its fit tolerance (2e-4) from the
    // true involute; boundary sampling at 0.03 mm spacing under a relative contact
    // curvature radius ≈ 4 mm adds ≤ h²/(8R) ≈ 3e-5; bisection to 1e-9 rad adds
    // nothing. Total position uncertainty ≈ 4.6e-4 mm over the wheel base radius
    // 25.37 mm ≈ 1.8e-5 rad. Asserted at 6e-5 rad (3× headroom).

    [Fact]
    public void ConjugateAction_RatioConstantThroughToothHandover()
    {
        var pinionSpec = new GearSpec(module: 2, teeth: 18);
        var wheelSpec = new GearSpec(module: 2, teeth: 27);
        double sweepRange = 1.5 * 2 * Math.PI / 18;   // 1.5 tooth pitches of the pinion
        var offsets = MeasureContactOffsets(pinionSpec, wheelSpec, extraCentreDistance: 0.4,
            sweepRange, samples: 10);
        double spread = Max(offsets) - Min(offsets);
        Assert.True(spread <= 6e-5,
            $"transmission varied by {spread:0.###e0} rad across the sweep (contact offsets: "
            + string.Join(", ", offsets.Select(o => o.ToString("0.######"))) + ")");
    }

    [Fact]
    public void ConjugateAction_ProfileShiftedPair()
    {
        // x₁ + x₂ = 0 keeps the standard centre distance; the same extended-distance
        // contact measurement must see the same exact ratio.
        var pinionSpec = new GearSpec(module: 2, teeth: 18, pressureAngleDegrees: 20, profileShift: 0.3);
        var wheelSpec = new GearSpec(module: 2, teeth: 27, pressureAngleDegrees: 20, profileShift: -0.3);
        double sweepRange = 1.2 * 2 * Math.PI / 18;
        var offsets = MeasureContactOffsets(pinionSpec, wheelSpec, extraCentreDistance: 0.4,
            sweepRange, samples: 6);
        double spread = Max(offsets) - Min(offsets);
        Assert.True(spread <= 6e-5,
            $"transmission varied by {spread:0.###e0} rad across the sweep (contact offsets: "
            + string.Join(", ", offsets.Select(o => o.ToString("0.######"))) + ")");
    }

    // ---- solids ----

    [Fact]
    public void SpurGear_MeshVolume_MatchesAreaTimesWidth()
    {
        var spec = new GearSpec(module: 2, teeth: 18, pressureAngleDegrees: 20, profileShift: 0.1);
        var profile = Gears.Spur(spec);
        var shape = Gears.SpurGear(spec, faceWidth: 8, boreDiameter: 8);
        var mesh = shape.ToMesh();
        Assert.True(mesh.IsClosed);
        double expected = (profile.ClosedFormArea - Math.PI * 16) * 8;
        double relative = Math.Abs(mesh.Volume() - expected) / expected;
        // Inscribed-chord tessellation of the arcs: relative deficit ~ (2π/64)²/6 ≈ 1.6e-3.
        Assert.True(relative < 0.01, $"volume {mesh.Volume()} vs {expected} ({relative:0.###e0} relative)");
    }

    [Fact]
    public void HelicalGear_MeshVolume_MatchesSpurArea()
    {
        var spec = new GearSpec(module: 2, teeth: 18, pressureAngleDegrees: 20, profileShift: 0.1);
        var profile = Gears.Spur(spec);
        var shape = Gears.HelicalGear(spec, faceWidth: 10, helixAngleDegrees: 20);
        var mesh = shape.ToMesh();
        Assert.True(mesh.IsClosed);
        // A twisted extrusion preserves the section area; the mesh sweep converges
        // second order in slices (profile subdivision matched to the angular step).
        double expected = profile.ClosedFormArea * 10;
        double relative = Math.Abs(mesh.Volume() - expected) / expected;
        Assert.True(relative < 0.02, $"volume {mesh.Volume()} vs {expected} ({relative:0.###e0} relative)");
    }

    [Fact]
    public void Bore_ReachingTheRootCircle_Refused()
    {
        var spec = new GearSpec(module: 2, teeth: 18);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Gears.SpurGear(spec, faceWidth: 8, boreDiameter: spec.RootDiameter));
    }

    // ------------------------------------------------------------------ helpers

    private static Vector2d Involute(double rb, double theta0, double t)
    {
        double u = theta0 + t;
        return new Vector2d(
            rb * (Math.Cos(u) + t * Math.Sin(u)),
            rb * (Math.Sin(u) - t * Math.Cos(u)));
    }

    /// <summary>Bisects the region boundary crossing along the circle of radius
    /// <paramref name="r"/> between an inside angle and an outside angle.</summary>
    private static double BisectCrossing(IPlanarRegion region, double r, double inside, double outside)
    {
        double At(double theta) => region.SignedDistance(new Vector2d(r * Math.Cos(theta), r * Math.Sin(theta)));
        Assert.True(At(inside) < 0, "inside probe is not inside");
        Assert.True(At(outside) > 0, "outside probe is not outside");
        for (int i = 0; i < 60; i++)
        {
            double mid = (inside + outside) / 2;
            if (At(mid) < 0)
                inside = mid;
            else
                outside = mid;
        }
        return (inside + outside) / 2;
    }

    private static double Max(IReadOnlyList<double> values) => values.Max();
    private static double Min(IReadOnlyList<double> values) => values.Min();

    /// <summary>
    /// Measures the drive-flank contact offset β* over a sweep of pinion angles: the
    /// wheel is rotated toward contact and the touching angle found by bisection of the
    /// clearance — the minimum of the pinion region's exact signed distance over the
    /// wheel outline's sampled points.
    /// </summary>
    private static List<double> MeasureContactOffsets(GearSpec pinionSpec, GearSpec wheelSpec,
        double extraCentreDistance, double sweepRange, int samples)
    {
        var pinion = Gears.Spur(pinionSpec);
        var wheel = Gears.Spur(wheelSpec);
        var region = pinion.Sketch.ToRegion();
        double a = (pinionSpec.PitchDiameter + wheelSpec.PitchDiameter) / 2 + extraCentreDistance;
        double ratio = (double)pinionSpec.Teeth / wheelSpec.Teeth;
        double wheelPitch = 2 * Math.PI / wheelSpec.Teeth;
        double alignment = Math.PI - wheelPitch / 2;   // a wheel gap centred on the line of centres
        double tipRadius = pinionSpec.TipDiameter / 2 + 0.05;

        // Sample the wheel outline once, in wheel-local coordinates, ~0.03 mm apart.
        var points = new List<Vector2d>();
        foreach (var curve in wheel.Sketch.ToCurves())
        {
            double length = curve switch
            {
                Line2d line => (line.End - line.Start).Length,
                Arc2d arc => arc.Length,
                _ => curve.ArcLength(),
            };
            int n = Math.Max(2, (int)Math.Ceiling(length / 0.03));
            for (int i = 0; i < n; i++)
                points.Add(curve.PointAt((double)i / n));
        }

        double Clearance(double phi1, double phi2)
        {
            double c1 = Math.Cos(-phi1), s1 = Math.Sin(-phi1);
            double c2 = Math.Cos(phi2), s2 = Math.Sin(phi2);
            double min = 1.0;
            foreach (var p in points)
            {
                double wx = a + p.X * c2 - p.Y * s2;
                double wy = p.X * s2 + p.Y * c2;
                if (wx * wx + wy * wy > tipRadius * tipRadius)
                    continue; // cannot reach the pinion
                var q = new Vector2d(wx * c1 - wy * s1, wx * s1 + wy * c1);
                min = Math.Min(min, region.SignedDistance(q));
                if (min < -0.05)
                    break; // deeply penetrating — the sign is settled
            }
            return min;
        }

        var offsets = new List<double>(samples);
        double? previous = null;
        for (int step = 0; step < samples; step++)
        {
            double phi1 = sweepRange * step / (samples - 1);
            double basePhi2 = alignment - ratio * phi1;

            double lo, hi;
            if (previous is double seed
                && Clearance(phi1, basePhi2 + seed - 0.004) > 0
                && Clearance(phi1, basePhi2 + seed + 0.004) < 0)
            {
                lo = seed - 0.004;
                hi = seed + 0.004;
            }
            else
            {
                // Locate the backlash well (clear window) by scanning, then its upper
                // (drive-flank) edge.
                double bestBeta = double.NaN, bestG = double.NegativeInfinity;
                for (double beta = -0.03; beta <= 0.03; beta += 0.0015)
                {
                    double gScan = Clearance(phi1, basePhi2 + beta);
                    if (gScan > bestG)
                    {
                        bestG = gScan;
                        bestBeta = beta;
                    }
                }
                Assert.True(bestG > 0, $"no clearance window found at phi1={phi1}");
                lo = bestBeta;
                hi = bestBeta;
                while (Clearance(phi1, basePhi2 + hi) > 0)
                {
                    lo = hi;
                    hi += 0.002;
                    Assert.True(hi < 0.06, "contact edge not found");
                }
            }

            for (int i = 0; i < 45; i++)
            {
                double mid = (lo + hi) / 2;
                if (Clearance(phi1, basePhi2 + mid) > 0)
                    lo = mid;
                else
                    hi = mid;
            }
            double contact = (lo + hi) / 2;
            offsets.Add(contact);
            previous = contact;
        }
        return offsets;
    }
}
