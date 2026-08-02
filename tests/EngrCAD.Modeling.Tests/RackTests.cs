using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The rack is the involute's straight-line limit, so everything about it is EXACT and
/// the assertions say so: the pitch-line thickness and the flank angle are measured off
/// the sketch's own region and held to 1e-9, and the area is an equality against the
/// closed form rather than a bound (where <c>GearTests</c> must allow the involute's
/// biarc fit deviation). The load-bearing test is the last one — conjugate action
/// against a generated pinion, measured from CONTACT.
/// </summary>
public class RackTests
{
    // ---- identities as arithmetic ----

    [Fact]
    public void RackSpec_Identities_HoldAsArithmetic()
    {
        var spec = new RackSpec(module: 2, pressureAngleDegrees: 20);
        double alpha = 20 * Math.PI / 180;

        // The pitch-line thickness is exactly HALF the circular pitch — the whole reason
        // a standard rack meshes a standard gear backlash-free.
        Assert.True(Math.Abs(spec.ToothThicknessAtPitch - spec.CircularPitch / 2) < 1e-12);
        Assert.True(Math.Abs(spec.CircularPitch - Math.PI * 2) < 1e-12);
        Assert.True(Math.Abs(spec.WholeDepth - 2 * (1.0 + 1.25)) < 1e-12);
        Assert.True(Math.Abs(spec.TipLandWidth
            - (spec.ToothThicknessAtPitch - 2 * spec.Addendum * Math.Tan(alpha))) < 1e-12);

        // ISO 53's maximum root fillet, reached by two routes: the standard's closed form
        // and the geometric condition it encodes (the fillet's tangent point on the root
        // line lands exactly on the space centre).
        double sin = Math.Sin(alpha), cos = Math.Cos(alpha), tan = Math.Tan(alpha);
        double rhoMax = (Math.PI * 2 / 4 - spec.Dedendum * tan) * cos / (1 - sin);
        Assert.True(Math.Abs(spec.MaximumRootFilletRadius - rhoMax) < 1e-12);
        double halfRootWidth = spec.ToothThicknessAtPitch / 2 + spec.Dedendum * tan;
        Assert.True(Math.Abs(
            halfRootWidth + rhoMax * (1 - sin) / cos - spec.CircularPitch / 2) < 1e-12);
        // The classical 0.4719·m, which is why ISO 53's 0.38·m fits.
        Assert.True(Math.Abs(spec.MaximumRootFilletRadius / spec.Module - 0.4719) < 1e-4);
        Assert.True(spec.RootFilletRadius < spec.MaximumRootFilletRadius);
    }

    [Fact]
    public void RackAndGear_ShareOneToothSystem()
    {
        var rack = new RackSpec(2.5, 25) { RootFilletCoefficient = 0.3, DedendumCoefficient = 1.3 };
        var gear = rack.MatingGear(31, profileShift: 0.2);
        Assert.Equal(rack.Module, gear.Module);
        Assert.Equal(rack.PressureAngleDegrees, gear.PressureAngleDegrees);
        Assert.Equal(rack.AddendumCoefficient, gear.AddendumCoefficient);
        Assert.Equal(rack.DedendumCoefficient, gear.DedendumCoefficient);
        Assert.Equal(rack.RootFilletCoefficient, gear.RootFilletCoefficient);

        // The round trip drops the profile shift and nothing else: a shift says where a
        // GEAR sits against this rack, so it is not a property of the rack.
        var back = RackSpec.For(gear);
        Assert.Equal(rack, back);

        // Meshing arithmetic: the gear's pitch-circle thickness is the rack's pitch-line
        // thickness at zero shift, so the pair is backlash-free by construction.
        Assert.True(Math.Abs(rack.MatingGear(31).ToothThicknessAtPitch
            - rack.ToothThicknessAtPitch) < 1e-12);
        Assert.True(Math.Abs(gear.CircularPitch - rack.CircularPitch) < 1e-12);
    }

    // ---- measured off the sketch ----

    // Note the fillet column: rho_fP,max falls with the pressure angle (0.472*m at 20
    // degrees, 0.318*m at 25), so the ISO 53 default does not fit a 25-degree rack and
    // the factory says so by name. These rows are legal specs, not tuned ones.
    [Theory]
    [InlineData(2.0, 20.0, 0.38)]
    [InlineData(1.5, 14.5, 0.38)]
    [InlineData(3.0, 25.0, 0.30)]
    public void ToothThickness_AtPitchLine_IsExactlyHalfThePitch(double module, double angle, double fillet)
    {
        var spec = new RackSpec(module, angle) { RootFilletCoefficient = fillet };
        var profile = Gears.Rack(spec, teeth: 4);
        var region = profile.Sketch.ToRegion();
        double p = spec.CircularPitch;
        double xc = 1.5 * p;                 // the second tooth's centre

        double right = BisectAlongY(region, 0, xc, xc + p / 2);
        double left = BisectAlongY(region, 0, xc, xc - p / 2);
        double measured = right - left;
        // A straight flank and an exact parity test: the only error is bisection, so the
        // band is 1e-9 (the weld tier) rather than the involute fit's 3*delta.
        Assert.True(Math.Abs(measured - spec.ToothThicknessAtPitch) < 1e-9,
            $"thickness {measured} vs {spec.ToothThicknessAtPitch}");
        Assert.True(Math.Abs(right - (xc + p / 4)) < 1e-9);
        Assert.True(Math.Abs(left - (xc - p / 4)) < 1e-9);
    }

    [Theory]
    [InlineData(2.0, 20.0, 0.38)]
    [InlineData(1.5, 14.5, 0.38)]
    [InlineData(3.0, 25.0, 0.30)]
    public void FlankAngle_IsExactlyThePressureAngle(double module, double angle, double fillet)
    {
        var spec = new RackSpec(module, angle) { RootFilletCoefficient = fillet };
        var profile = Gears.Rack(spec, teeth: 4);
        var region = profile.Sketch.ToRegion();
        double p = spec.CircularPitch;
        double xc = 1.5 * p;
        // Two heights strictly between the fillet's flank tangency and the tip line.
        double y0 = -spec.Addendum / 2, y1 = spec.Addendum / 2;

        double x0 = BisectAlongY(region, y0, xc, xc + p / 2);
        double x1 = BisectAlongY(region, y1, xc, xc + p / 2);
        double measured = Math.Atan2(x0 - x1, y1 - y0);
        Assert.True(Math.Abs(measured - spec.PressureAngleRadiansForTest()) < 1e-9,
            $"flank at {measured * 180 / Math.PI} deg, expected {angle}");

        // The left flank is the mirror, so the tooth is symmetric to the same bar.
        double m0 = BisectAlongY(region, y0, xc, xc - p / 2);
        double m1 = BisectAlongY(region, y1, xc, xc - p / 2);
        Assert.True(Math.Abs(Math.Atan2(m1 - m0, y1 - y0) - measured) < 1e-9);
    }

    [Theory]
    [InlineData(2.0, 20.0, 3, 0.38)]
    [InlineData(1.0, 20.0, 5, 0.0)]      // no fillet: the corner fills vanish exactly
    [InlineData(2.5, 25.0, 2, 0.30)]
    [InlineData(4.0, 14.5, 4, 0.2)]
    public void Area_SketchAgreesWithTheClosedForm(double module, double angle, int teeth, double fillet)
    {
        var spec = new RackSpec(module, angle) { RootFilletCoefficient = fillet };
        var profile = Gears.Rack(spec, teeth, backHeight: 1.5 * module);
        double area = profile.Sketch.Area();
        double relative = Math.Abs(area - profile.ClosedFormArea) / profile.ClosedFormArea;
        // Lines and exact arcs on both sides: an EQUALITY, not a bound.
        Assert.True(relative < 1e-12,
            $"sketch area {area} vs closed form {profile.ClosedFormArea} ({relative:0.###e0} relative)");
        Assert.Equal(teeth * spec.CircularPitch, profile.Length, 12);
        Assert.Equal(-(spec.Dedendum + 1.5 * module), profile.BackFaceOffset, 12);
    }

    [Fact]
    public void Profile_IsPeriodicAtItsOwnPitch_SoBarsTile()
    {
        var spec = new RackSpec(2);
        var profile = Gears.Rack(spec, teeth: 4);
        var region = profile.Sketch.ToRegion();
        double p = spec.CircularPitch;
        // Both windows sit a full pitch from either end, so the signed distance in the
        // tooth band is governed entirely by the local geometry.
        for (int i = 0; i <= 40; i++)
        {
            double x = p + p * i / 40.0;
            for (int j = 0; j <= 8; j++)
            {
                double y = -spec.Dedendum + (spec.WholeDepth) * j / 8.0;
                double here = region.SignedDistance(new Vector2d(x, y));
                double next = region.SignedDistance(new Vector2d(x + p, y));
                Assert.True(Math.Abs(here - next) < 1e-12,
                    $"at ({x}, {y}): {here} vs {next} one pitch along");
            }
        }
    }

    // ---- refusals, by name ----

    [Fact]
    public void PointedTooth_Refused()
    {
        // Tip land vanishes at h_a* = pi/(4 tan a) = 2.158 for 20 degrees.
        var ex = Assert.Throws<ArgumentException>(() =>
            Gears.Rack(new RackSpec(1) { AddendumCoefficient = 2.5 }, teeth: 2));
        Assert.Contains("point", ex.Message);
        // 2.0 clears it.
        _ = Gears.Rack(new RackSpec(1) { AddendumCoefficient = 2.0 }, teeth: 2);
    }

    [Fact]
    public void OversizedRootFillet_Refused_NamingTheMaximum()
    {
        var spec = new RackSpec(1) { RootFilletCoefficient = 0.6 };
        var ex = Assert.Throws<ArgumentException>(() => Gears.Rack(spec, teeth: 2));
        Assert.Contains("tooth space", ex.Message);
        Assert.Contains("0.472", ex.Message);   // names ISO 53's rho_fP,max
        // The stated maximum is reachable, which is what makes naming it useful.
        _ = Gears.Rack(new RackSpec(1) { RootFilletCoefficient = 0.471 }, teeth: 2);
    }

    [Fact]
    public void ZeroBackHeight_Refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Gears.Rack(new RackSpec(1), 2, backHeight: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Gears.Rack(new RackSpec(1), 0));
    }

    // ---- solid ----

    [Fact]
    public void RackBar_MeshVolume_MatchesAreaTimesWidth()
    {
        var spec = new RackSpec(2);
        var profile = Gears.Rack(spec, teeth: 6);
        var mesh = Gears.RackBar(spec, teeth: 6, faceWidth: 10).ToMesh();
        Assert.True(mesh.IsClosed);
        double expected = profile.ClosedFormArea * 10;
        double relative = Math.Abs(mesh.Volume() - expected) / expected;
        // Measured 1.10e-5, and note the SIGN: the mesh is fractionally LARGER, because
        // the only curved features are the twelve root fillets and a fillet is CONCAVE
        // from the material's side, so chording it cuts across the space and adds
        // material — the opposite of the usual inscribed deficit.
        Assert.True(mesh.Volume() > expected);
        Assert.True(relative < 1e-4,
            $"volume {mesh.Volume()} vs {expected} ({relative:0.###e0} relative)");
    }

    // ---- the law of gearing, asked of a rack and pinion ----
    //
    // A rack and pinion must transmit a constant ratio: the rack advances exactly
    // r_pitch per radian of pinion rotation. The involute's signature property makes
    // that hold at ANY mounting height, which is what lets the pinion be LIFTED to open
    // real backlash — so drive-flank contact is a transversal zero of the clearance and
    // can be bisected, exactly as GearTests does for a gear pair at an extended centre
    // distance. The mechanism solver is again deliberately not the instrument:
    // Coupling.RackAndPinion ENFORCES the ratio this asserts.
    //
    // Tolerance: the rack flank is an exact line, so the whole error budget is the
    // pinion's biarc fit (<= module*1e-4 = 2e-4 mm normal to the flank, i.e. 2.1e-4 mm
    // along the rack once divided by cos a) plus the 0.04 mm outline sampling against a
    // relative curvature radius of about 6 mm (h^2/8R = 3.3e-5). Asserted at 1e-3 mm.

    [Fact]
    public void RackAndPinion_AdvancesExactlyPitchRadiusPerRadian()
    {
        var offsets = MeasureRackContactOffsets(
            new RackSpec(2), new GearSpec(module: 2, teeth: 18), rackTeeth: 6,
            liftOff: 0.4, sweepPitches: 1.2, samples: 8);
        double spread = offsets.Max() - offsets.Min();
        // Measured 6.94e-5 mm, comfortably under the derived 2.1e-4 budget (the fit
        // deviation is systematic along a flank rather than resampled per contact point).
        Assert.True(spread <= 1e-3,
            $"rack advance varied by {spread:0.###e0} mm across the sweep (contact offsets: "
            + string.Join(", ", offsets.Select(o => o.ToString("0.######"))) + ")");
    }

    [Fact]
    public void RackAndPinion_ContactInstrument_SeesAMismatchedPressureAngle()
    {
        // A 25-degree rack against a 20-degree pinion is not conjugate: the same
        // instrument must SEE it, or the test above would pass on geometry that is wrong.
        var offsets = MeasureRackContactOffsets(
            new RackSpec(2, 25) { RootFilletCoefficient = 0.3 },
            new GearSpec(module: 2, teeth: 18), rackTeeth: 6,
            liftOff: 0.4, sweepPitches: 1.2, samples: 8);
        double spread = offsets.Max() - offsets.Min();
        // Measured 1.21e-1 mm — 1740x the conjugate pair's 6.94e-5, so the instrument
        // reads flank FORM and not merely "some contact happened".
        Assert.True(spread > 2e-2,
            $"the mismatched pair only varied by {spread:0.###e0} mm - the instrument is blind");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Bisects the region boundary crossing along the line y = <paramref name="y"/>
    /// between an inside x and an outside x.</summary>
    private static double BisectAlongY(IPlanarRegion region, double y, double inside, double outside)
    {
        double At(double x) => region.SignedDistance(new Vector2d(x, y));
        Assert.True(At(inside) < 0, $"inside probe ({inside}, {y}) is not inside");
        Assert.True(At(outside) > 0, $"outside probe ({outside}, {y}) is not outside");
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

    /// <summary>
    /// Measures the drive-flank contact offset beta over a sweep of pinion angles: the
    /// rack is pushed toward contact beyond its nominal advance r*phi, and the touching
    /// displacement is found by bisecting the clearance (the minimum of the pinion
    /// region's exact signed distance over sampled rack-outline points).
    /// </summary>
    private static List<double> MeasureRackContactOffsets(
        RackSpec rackSpec, GearSpec pinionSpec, int rackTeeth, double liftOff,
        double sweepPitches, int samples)
    {
        Assert.True(rackTeeth % 2 == 0, "an even tooth count puts a space centre at the bar's midpoint");
        var rack = Gears.Rack(rackSpec, rackTeeth);
        var pinion = Gears.Spur(pinionSpec);
        var region = pinion.Sketch.ToRegion();

        double r = pinionSpec.PitchDiameter / 2;
        double centreX = rack.Length / 2;          // a rack space centre
        double centreY = r + liftOff;              // lifted, so there is backlash to bisect
        double tipRadius = pinionSpec.TipDiameter / 2 + 0.05;
        // A tooth of the pinion is centred on its +X axis, so this alignment points one
        // straight down into the rack space under the pinion centre at phi = 0.
        double align = -Math.PI / 2;

        // Only rack points that a pinion tooth can reach are worth carrying: nothing
        // below the pinion's own tip line can ever be touched.
        double reachY = centreY - tipRadius;
        var points = new List<Vector2d>();
        foreach (var curve in rack.Sketch.ToCurves())
        {
            double length = curve switch
            {
                Line2d line => (line.End - line.Start).Length,
                Arc2d arc => arc.Length,
                _ => curve.ArcLength(),
            };
            int n = Math.Max(2, (int)Math.Ceiling(length / 0.04));
            for (int i = 0; i < n; i++)
            {
                var p = curve.PointAt((double)i / n);
                if (p.Y >= reachY)
                    points.Add(p);
            }
        }
        Assert.NotEmpty(points);

        double Clearance(double phi, double advance)
        {
            double c = Math.Cos(-(align + phi)), s = Math.Sin(-(align + phi));
            double min = 1.0;
            foreach (var p in points)
            {
                double wx = p.X + advance - centreX, wy = p.Y - centreY;
                if (wx * wx + wy * wy > tipRadius * tipRadius)
                    continue;
                var q = new Vector2d(wx * c - wy * s, wx * s + wy * c);
                min = Math.Min(min, region.SignedDistance(q));
                if (min < -0.05)
                    break;   // deeply penetrating - the sign is settled
            }
            return min;
        }

        double sweepRange = sweepPitches * 2 * Math.PI / pinionSpec.Teeth;
        var offsets = new List<double>(samples);
        double? previous = null;
        for (int step = 0; step < samples; step++)
        {
            double phi = sweepRange * step / (samples - 1);
            double nominal = r * phi;

            double lo, hi;
            if (previous is double seed
                && Clearance(phi, nominal + seed - 0.004) > 0
                && Clearance(phi, nominal + seed + 0.004) < 0)
            {
                lo = seed - 0.004;
                hi = seed + 0.004;
            }
            else
            {
                double bestBeta = double.NaN, bestG = double.NegativeInfinity;
                for (double beta = -0.20; beta <= 0.20; beta += 0.01)
                {
                    double g = Clearance(phi, nominal + beta);
                    if (g > bestG)
                    {
                        bestG = g;
                        bestBeta = beta;
                    }
                }
                Assert.True(bestG > 0, $"no clearance window found at phi={phi}");
                lo = bestBeta;
                hi = bestBeta;
                while (Clearance(phi, nominal + hi) > 0)
                {
                    lo = hi;
                    hi += 0.004;
                    Assert.True(hi < 0.6, "contact edge not found");
                }
            }

            for (int i = 0; i < 45; i++)
            {
                double mid = (lo + hi) / 2;
                if (Clearance(phi, nominal + mid) > 0)
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

internal static class RackTestHelpers
{
    /// <summary>The spec's pressure angle in radians (the property itself is internal to
    /// the modelling assembly).</summary>
    public static double PressureAngleRadiansForTest(this RackSpec spec) =>
        spec.PressureAngleDegrees * Math.PI / 180;
}
