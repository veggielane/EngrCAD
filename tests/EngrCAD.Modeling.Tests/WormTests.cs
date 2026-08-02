using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The worm is a thread, so it is exact and its geometry is measured from the FIELD (the
/// axial section, the lead and the handedness, all bisected out of the shape's own
/// implicit lowering). The wheel is honestly an approximation — a crossed-helical gear
/// at the worm's lead angle — so what is asserted of it is the PAIRING arithmetic and
/// the handedness convention it shares with the worm, which is the part that can be
/// silently wrong.
/// </summary>
public class WormTests
{
    // ---- identities as arithmetic, each by two routes ----

    [Fact]
    public void WormSpec_Identities_HoldAsArithmetic()
    {
        var spec = new WormSpec(axialModule: 2, starts: 2, pitchDiameter: 20,
            axialPressureAngleDegrees: 20);

        Assert.True(Math.Abs(spec.AxialPitch - Math.PI * 2) < 1e-12);
        Assert.True(Math.Abs(spec.Lead - 2 * spec.AxialPitch) < 1e-12);
        Assert.True(Math.Abs(spec.DiameterFactor - 10) < 1e-12);

        // Lead angle, three routes: the definition, the diameter-factor form z1/q, and
        // the trigonometric identity that makes the helix angle its complement.
        double gamma = spec.LeadAngleRadians;
        Assert.True(Math.Abs(Math.Tan(gamma) - spec.Lead / (Math.PI * spec.PitchDiameter)) < 1e-12);
        Assert.True(Math.Abs(Math.Tan(gamma) - spec.Starts / spec.DiameterFactor) < 1e-12);
        Assert.True(Math.Abs(spec.HelixAngleDegrees + spec.LeadAngleDegrees - 90) < 1e-12);
        // z1/q = 2/10, so gamma = atan(0.2) = 11.3099 degrees.
        Assert.True(Math.Abs(spec.LeadAngleDegrees - 11.309932) < 1e-6);

        // Normal quantities: both are the axial ones projected through the lead angle,
        // and the normal pressure angle is measurably FLATTER than the axial 20 degrees.
        Assert.True(Math.Abs(spec.NormalModule - spec.AxialModule * Math.Cos(gamma)) < 1e-12);
        Assert.True(Math.Abs(
            Math.Tan(spec.NormalPressureAngleDegrees * Math.PI / 180)
            - Math.Tan(spec.AxialPressureAngleRadiansForTest()) * Math.Cos(gamma)) < 1e-12);
        Assert.True(spec.NormalPressureAngleDegrees < 19.7);

        Assert.True(Math.Abs(spec.TipDiameter - 24) < 1e-12);
        Assert.True(Math.Abs(spec.RootDiameter - 15) < 1e-12);
        Assert.True(Math.Abs(spec.AxialToothThicknessAtPitch - spec.AxialPitch / 2) < 1e-12);

        // The diameter-factor spelling is the same worm.
        Assert.Equal(spec, WormSpec.FromDiameterFactor(2, 2, 10, 20));
    }

    [Fact]
    public void WormPair_MeshingIdentities_AndTheStartsTrap()
    {
        var worm = new WormSpec(axialModule: 2, starts: 2, pitchDiameter: 20);
        var pair = Gears.WormPair(worm, wheelTeeth: 40);

        // THE meshing condition: the worm's AXIAL pitch is the wheel's TRANSVERSE
        // circular pitch, equivalently the wheel's transverse module is the worm's axial
        // module. Everything else about the pairing follows from it.
        Assert.True(Math.Abs(worm.AxialPitch - pair.Wheel.CircularPitch) < 1e-12);
        Assert.True(Math.Abs(worm.AxialModule - pair.Wheel.Module) < 1e-12);
        // At a 90-degree shaft angle the worm's axial plane IS the wheel's transverse
        // plane at the central point, so there is nothing to convert between the two
        // pressure angles.
        Assert.True(Math.Abs(worm.AxialPressureAngleDegrees - pair.Wheel.PressureAngleDegrees) < 1e-12);

        // Shaft angle sum, the reason the wheel's helix angle is the worm's LEAD angle.
        Assert.True(Math.Abs(worm.HelixAngleDegrees + pair.WheelHelixAngleDegrees
            - pair.ShaftAngleDegrees) < 1e-12);
        Assert.True(Math.Abs(pair.WheelHelixAngleDegrees - worm.LeadAngleDegrees) < 1e-12);

        // Centre distance, two routes: from the two pitch diameters, and from the
        // dimensionless (q + z2)·m/2.
        Assert.True(Math.Abs(pair.WheelPitchDiameter - 80) < 1e-12);
        Assert.True(Math.Abs(pair.CentreDistance - 50) < 1e-12);
        Assert.True(Math.Abs(pair.CentreDistance
            - (worm.DiameterFactor + pair.WheelTeeth) * worm.AxialModule / 2) < 1e-12);

        // THE TRAP: a worm's "one tooth" is one START. Two starts on forty teeth is
        // 20:1, not 40:1 — and a one-start worm of the same lead angle would need a
        // different diameter, so the two are genuinely different machines.
        Assert.Equal(20.0, pair.GearRatio, 12);
        Assert.Equal(40.0, Gears.WormPair(worm with { }, 40).WheelTeeth / 1.0, 12);
        var single = new WormSpec(2, 1, 20);
        Assert.Equal(40.0, Gears.WormPair(single, 40).GearRatio, 12);
        Assert.True(single.LeadAngleDegrees < worm.LeadAngleDegrees);
    }

    [Fact]
    public void WormPair_LeftHandWorm_TakesALeftHandWheel()
    {
        var right = new WormSpec(2, 2, 20);
        var left = right with { };
        var leftSpec = new WormSpec(2, 2, 20, 20, leftHand: true);
        Assert.Equal(right, left);
        Assert.True(Gears.WormPair(right, 40).WheelHelixAngleDegrees > 0);
        Assert.True(Gears.WormPair(leftSpec, 40).WheelHelixAngleDegrees < 0);
        // Same magnitude — handedness is not a different worm, it is the same one wound
        // the other way (the ThreadSpec.LeftHanded rule).
        Assert.Equal(Gears.WormPair(right, 40).WheelHelixAngleDegrees,
            -Gears.WormPair(leftSpec, 40).WheelHelixAngleDegrees, 12);
        Assert.Equal(right.LeadAngleDegrees, leftSpec.LeadAngleDegrees, 12);
    }

    // ---- the body, measured from its own field ----

    [Fact]
    public void Worm_AxialSection_IsTheStraightZaProfile()
    {
        var spec = new WormSpec(axialModule: 2, starts: 2, pitchDiameter: 20);
        double length = 3 * spec.Lead;
        var field = Gears.Worm(spec, length).ToImplicit();

        double px = spec.AxialPitch;
        double tan = Math.Tan(spec.AxialPressureAngleRadiansForTest());
        double zc = 2 * px;   // a tooth centre well inside the capped length
        double rPitch = spec.PitchDiameter / 2;

        // Axial tooth thickness at three radii spanning the whole depth. The bias is
        // DERIVED, not allowed for: the tessellation chords the helical bands in PHASE
        // only (the generator is straight, so a v-chord is exactly on the surface), which
        // pulls the surface inward by r(1 - cos(pi/n)) and so narrows a measured
        // thickness by twice that times tan(alpha) - 0.0267 mm at r = 7.6 and the default
        // n = 32, against a measured 0.0275. So the measurement must land one-sided,
        // between the exact value and 1.5x the predicted bias below it.
        int n = new MeshQuality().SegmentsPerCircle;
        foreach (double r in (double[])[spec.RootDiameter / 2 + 0.1, rPitch, spec.TipDiameter / 2 - 0.1])
        {
            double right = BisectZ(field, r, 0, zc, zc + px / 2);
            double left = BisectZ(field, r, 0, zc, zc - px / 2);
            double expected = spec.AxialToothThicknessAtPitch - 2 * (r - rPitch) * tan;
            double bias = 2 * r * (1 - Math.Cos(Math.PI / n)) * tan;
            double measuredThickness = right - left;
            Assert.True(measuredThickness < expected && measuredThickness > expected - 1.5 * bias,
                $"axial thickness {measuredThickness} at r = {r}, expected {expected} "
                + $"less a chord bias of {bias:0.####}");
            // The tooth CENTRE carries no bias at all - both flanks are pulled the same
            // way, so it cancels exactly, which is why the lead below reads to 1e-14.
            Assert.True(Math.Abs((right + left) / 2 - zc) < 1e-9,
                $"tooth centre {(right + left) / 2} vs {zc}");
        }

        // The flank angle follows from how fast the thickness closes with radius, which
        // is the ZA property itself: a STRAIGHT axial flank.
        double rLow = spec.RootDiameter / 2 + 0.1, rHigh = spec.TipDiameter / 2 - 0.1;
        double tLow = BisectZ(field, rLow, 0, zc, zc + px / 2) - BisectZ(field, rLow, 0, zc, zc - px / 2);
        double tHigh = BisectZ(field, rHigh, 0, zc, zc + px / 2) - BisectZ(field, rHigh, 0, zc, zc - px / 2);
        double measured = Math.Atan((tLow - tHigh) / (2 * (rHigh - rLow))) * 180 / Math.PI;
        // Measured 20.10 degrees: the chord bias grows with radius, so it does not
        // cancel between the two readings and leaves about 0.1 degree.
        Assert.True(Math.Abs(measured - spec.AxialPressureAngleDegrees) < 0.2,
            $"axial flank angle {measured} deg, expected {spec.AxialPressureAngleDegrees}");
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(2, true)]
    public void Worm_HelicalSymmetry_MeasuresTheLeadAndTheHand(int starts, bool leftHand)
    {
        var spec = new WormSpec(2, starts, 24, 20, leftHand);
        double length = 3 * spec.Lead;
        var field = Gears.Worm(spec, length).ToImplicit();
        double px = spec.AxialPitch;
        double r = spec.PitchDiameter / 2;
        double zc = px * (int)Math.Round(1.5 * starts);   // a tooth centre away from both caps

        // The rate the sweep advances per radian: measure the tooth centre at two
        // azimuths a quarter turn apart, so the shift is lead/4 and its SIGN is the
        // handedness. (A quarter turn keeps the shift inside one tooth pitch for a
        // single start and stays unambiguous for several, since the search window is
        // recentred on the predicted position.)
        double ToothCentre(double theta, double about)
        {
            double right = BisectZ(field, r, theta, about, about + px / 2);
            double left = BisectZ(field, r, theta, about, about - px / 2);
            return (right + left) / 2;
        }

        double sign = leftHand ? -1 : 1;
        double at0 = ToothCentre(0, zc);
        double atQuarter = ToothCentre(Math.PI / 2, zc + sign * spec.Lead / 4);
        double shift = atQuarter - at0;
        // Measured to 1e-14: the tooth centre is the mean of two flank crossings, so the
        // tessellation's chord bias cancels exactly and this reads the LEAD, not an
        // approximation of it.
        Assert.True(Math.Abs(shift - sign * spec.Lead / 4) < 1e-9,
            $"a quarter turn advanced the thread by {shift}, expected {sign * spec.Lead / 4}");

        // A half turn, from the same reading, must be twice it — which is what makes this
        // a measurement of the LEAD rather than of one lucky sample.
        double atHalf = ToothCentre(Math.PI, zc + sign * spec.Lead / 2);
        Assert.True(Math.Abs((atHalf - at0) - sign * spec.Lead / 2) < 1e-9,
            $"a half turn advanced the thread by {atHalf - at0}, expected {sign * spec.Lead / 2}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void Worm_Volume_MatchesPappusOverTheSweep(int starts)
    {
        var spec = new WormSpec(2, starts, 24);
        // Deliberately NOT a whole number of leads: the Pappus argument averages the
        // axial section over a full turn, so any length works and the phase washes out.
        double length = 2.7 * spec.Lead;
        double closedForm = spec.VolumeOfLength(length);

        // Independent route: integrate pi*R(z)^2 numerically over one AXIAL PITCH — a
        // different decomposition, since the radius has period p_x while the sweep has
        // period lead — from the profile's stated dimensions rather than its corner list.
        double px = spec.AxialPitch;
        double tan = Math.Tan(spec.AxialPressureAngleRadiansForTest());
        double ra = spec.TipDiameter / 2, rf = spec.RootDiameter / 2;
        double a = spec.CrestLandHalfWidth;
        double Radius(double z)
        {
            double w = z - Math.Floor(z / px + 0.5) * px;   // into (-p_x/2, p_x/2]
            double d = Math.Abs(w);
            if (d <= a)
                return ra;
            double r = ra - (d - a) / tan;
            return Math.Max(r, rf);
        }
        int n = 400_000;
        double integral = 0;
        for (int i = 0; i < n; i++)
        {
            double z0 = px * i / n, z1 = px * (i + 1) / n;
            integral += (Radius(z0) * Radius(z0) + Radius(z1) * Radius(z1)) / 2 * (z1 - z0);
        }
        double numeric = length * Math.PI * integral / px;
        Assert.True(Math.Abs(numeric - closedForm) / closedForm < 1e-6,
            $"numeric {numeric} vs closed form {closedForm}");

        // And the mesh against the closed form. The helical bands are chorded in PHASE
        // only, so the deficit is one-sided AND predictable: it is essentially the
        // inscribed n-gon's area deficit 1 - (n/2pi)sin(2pi/n), 6.40e-3 at the default
        // 32 segments per circle. Measured 6.15e-3 / 5.91e-3 / 5.86e-3 at 1/2/4 starts,
        // so the bound is the PREDICTION rather than a number that happened to pass.
        var mesh = Gears.Worm(spec, length).ToMesh();
        Assert.True(mesh.IsClosed);
        double relative = (closedForm - mesh.Volume()) / closedForm;
        Assert.True(relative > 0, $"an inscribed tessellation cannot exceed the exact volume ({relative:0.###e0})");
        double predicted = NgonDeficit(new MeshQuality().SegmentsPerCircle);
        Assert.True(relative > 0.8 * predicted && relative < 1.2 * predicted,
            $"volume {mesh.Volume()} vs {closedForm} ({relative:0.###e0} relative, n-gon prediction {predicted:0.###e0})");
    }

    [Fact]
    public void Worm_Volume_ConvergesQuadraticallyWithTheSegmentCount()
    {
        var spec = new WormSpec(2, 2, 24);
        double length = 2.7 * spec.Lead;
        double closedForm = spec.VolumeOfLength(length);
        var worm = Gears.Worm(spec, length);

        double previous = 0;
        foreach (int segments in (int[])[32, 64, 128])
        {
            var mesh = worm.ToMesh(new MeshQuality { SegmentsPerCircle = segments, CurveSamples = segments });
            double deficit = (closedForm - mesh.Volume()) / closedForm;
            double predicted = NgonDeficit(segments);
            Assert.True(deficit > 0);
            Assert.True(deficit > 0.8 * predicted && deficit < 1.2 * predicted,
                $"at {segments} segments the deficit is {deficit:0.###e0}, n-gon prediction {predicted:0.###e0}");
            if (previous > 0)
            {
                double ratio = previous / deficit;
                Assert.True(ratio > 3.5 && ratio < 4.5,
                    $"halving the step took {previous:0.###e0} -> {deficit:0.###e0} (ratio {ratio:0.##})");
            }
            previous = deficit;
        }
    }

    /// <summary>The area a regular inscribed n-gon loses against its circle, relative:
    /// 1 − (n/2π)·sin(2π/n).</summary>
    private static double NgonDeficit(int n) => 1 - n / (2 * Math.PI) * Math.Sin(2 * Math.PI / n);

    // ---- the handedness convention is SHARED, and that is the trap ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WormWheel_TakesTheSameHandAsTheWorm_MeasuredFromBothFields(bool leftHand)
    {
        // The worm is a helical SWEEP in the B-Rep kernel and the wheel is a twisted
        // EXTRUSION in the modelling layer: two independent constructions that must agree
        // about what "right-handed" means, or a pair specified correctly would be built
        // meshing the wrong way. Both are read off the geometry.
        var spec = new WormSpec(2, 2, 20, 20, leftHand);
        var pair = Gears.WormPair(spec, wheelTeeth: 30);
        double sign = leftHand ? -1 : 1;

        // Worm: a quarter turn advances the thread by +lead/4 when right-handed.
        var wormField = Gears.Worm(spec, 3 * spec.Lead).ToImplicit();
        double px = spec.AxialPitch, r1 = spec.PitchDiameter / 2, zc = 3 * px;
        double at0 = (BisectZ(wormField, r1, 0, zc, zc + px / 2)
            + BisectZ(wormField, r1, 0, zc, zc - px / 2)) / 2;
        double atQuarter = (BisectZ(wormField, r1, Math.PI / 2, zc + sign * spec.Lead / 4,
                zc + sign * spec.Lead / 4 + px / 2)
            + BisectZ(wormField, r1, Math.PI / 2, zc + sign * spec.Lead / 4,
                zc + sign * spec.Lead / 4 - px / 2)) / 2;
        Assert.True(Math.Sign(atQuarter - at0) == Math.Sign(sign),
            $"worm advanced {atQuarter - at0} over a quarter turn (hand sign {sign})");

        // Wheel: ONE point settles it. At the top of the face the tooth that started on
        // +X has rotated by the twist w*tan(beta)/r; probing the pitch cylinder at
        // +twist is INSIDE for a right-hand wheel and outside for a left-hand one,
        // because 2*twist (0.16 rad) exceeds the tooth half-angle (0.105 rad).
        double faceWidth = 12;
        var wheelField = Gears.WormWheel(pair, faceWidth, boreDiameter: 8).ToImplicit();
        double r2 = pair.WheelPitchDiameter / 2;
        double twist = faceWidth * Math.Tan(Math.Abs(pair.WheelHelixAngleDegrees) * Math.PI / 180) / r2;
        Assert.True(2 * twist > Math.PI / pair.WheelTeeth,
            "the fixture must twist further than a tooth half-angle or the probe cannot tell the hands apart");
        double zTop = 0.95 * faceWidth;
        double withHand = wheelField.Evaluate(Cyl(r2, sign * twist * 0.95, zTop));
        double against = wheelField.Evaluate(Cyl(r2, -sign * twist * 0.95, zTop));
        Assert.True(withHand < 0, $"the wheel's own hand probe read {withHand} (expected inside)");
        Assert.True(against > 0, $"the opposite-hand probe read {against} (expected outside)");
    }

    // ---- honest representation support ----

    [Fact]
    public void Worm_IsExact_AndTheWheelSaysItIsNot()
    {
        var spec = new WormSpec(2, 2, 20);
        var pair = Gears.WormPair(spec, 30);

        // The worm IS a thread: one helical sweep, so the B-Rep is the solid.
        var worm = Gears.Worm(spec, 20);
        Assert.True(worm.Explain(TargetRep.Brep).IsConvertible);
        Assert.NotNull(worm.ToBrep());

        // The wheel is a twisted extrusion and says so rather than pretending.
        var wheel = Gears.WormWheel(pair, faceWidth: 10);
        Assert.False(wheel.Explain(TargetRep.Brep).IsConvertible);
        var wheelMesh = wheel.ToMesh();
        Assert.True(wheelMesh.IsClosed);
    }

    // ---- refusals, by name ----

    [Fact]
    public void NonPositiveRootDiameter_Refused()
    {
        // q = 2 puts the dedendum through the axis.
        var ex = Assert.Throws<ArgumentException>(() =>
            Gears.Worm(WormSpec.FromDiameterFactor(2, 1, 2), 20));
        Assert.Contains("Root diameter", ex.Message);
        Assert.Contains("diameter factor", ex.Message);
    }

    [Fact]
    public void PointedThread_Refused()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Gears.Worm(new WormSpec(2, 1, 20) { AddendumCoefficient = 2.5 }, 20));
        Assert.Contains("point", ex.Message);
    }

    [Fact]
    public void OverlappingStartsAtTheRoot_Refused()
    {
        // A deep dedendum at a steep flank angle closes the root land.
        var ex = Assert.Throws<ArgumentException>(() =>
            Gears.Worm(new WormSpec(2, 1, 30, 35) { DedendumCoefficient = 2.5 }, 20));
        Assert.Contains("root cylinder", ex.Message);
    }

    [Fact]
    public void DegenerateSpecs_RefusedAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WormSpec(2, 0, 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WormSpec(0, 1, 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WormSpec(2, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WormSpec(2, 1, 20, 50));
        Assert.Throws<ArgumentOutOfRangeException>(() => Gears.WormPair(new WormSpec(2, 1, 20), 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WormSpec(2, 1, 20).VolumeOfLength(0));
    }

    // ------------------------------------------------------------------ helpers

    private static Vector3d Cyl(double r, double theta, double z) =>
        new(r * Math.Cos(theta), r * Math.Sin(theta), z);

    /// <summary>Bisects the field's boundary along the axis at a fixed radius and
    /// azimuth, between an inside z and an outside z.</summary>
    private static double BisectZ(Sdf field, double r, double theta, double inside, double outside)
    {
        double At(double z) => field.Evaluate(Cyl(r, theta, z));
        Assert.True(At(inside) < 0, $"inside probe (r={r}, th={theta}, z={inside}) read {At(inside)}");
        Assert.True(At(outside) > 0, $"outside probe (r={r}, th={theta}, z={outside}) read {At(outside)}");
        for (int i = 0; i < 50; i++)
        {
            double mid = (inside + outside) / 2;
            if (At(mid) < 0)
                inside = mid;
            else
                outside = mid;
        }
        return (inside + outside) / 2;
    }
}

internal static class WormTestHelpers
{
    /// <summary>The spec's axial pressure angle in radians (the property is internal to
    /// the modelling assembly).</summary>
    public static double AxialPressureAngleRadiansForTest(this WormSpec spec) =>
        spec.AxialPressureAngleDegrees * Math.PI / 180;
}
