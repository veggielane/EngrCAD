using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Straight bevel gears (Tredgold's back-cone construction). The identities here are
/// reached by INDEPENDENT routes wherever one exists: the cone angles are compared
/// against the standard closed forms AND read back off the generated section's own
/// radii; the flank is compared against a projected involute this file computes from
/// scratch rather than against anything the factory returns; and the solid is compared
/// against the exact volume of the cone over its own exact section area.
/// </summary>
public class BevelGearTests
{
    private const double Deg = Math.PI / 180;

    // ---------------------------------------------------------------- the pair

    [Theory]
    [InlineData(20, 40, 90)]
    [InlineData(40, 20, 90)]
    [InlineData(20, 20, 90)]
    [InlineData(18, 45, 60)]
    [InlineData(25, 30, 120)]
    [InlineData(20, 25, 135)]
    public void BevelPair_ConeAnglesSumToTheShaftAngle(int z1, int z2, double sigma)
    {
        var pair = new BevelPair(z1, z2, sigma);
        Assert.Equal(sigma, pair.PinionConeAngleDegrees + pair.WheelConeAngleDegrees, 12);
    }

    [Theory]
    [InlineData(20, 40, 90)]
    [InlineData(18, 45, 60)]
    [InlineData(25, 30, 120)]
    public void BevelPair_ConeAnglesCarryTheToothRatioAsTheirSineRatio(int z1, int z2, double sigma)
    {
        // Both pitch cones share one apex and one cone distance R, so r = R sin(delta):
        // sin(d1)/sin(d2) = r1/r2 = z1/z2. An independent route to the same angles.
        var pair = new BevelPair(z1, z2, sigma);
        double ratio = Math.Sin(pair.PinionConeAngleDegrees * Deg) / Math.Sin(pair.WheelConeAngleDegrees * Deg);
        Assert.Equal((double)z1 / z2, ratio, 12);
    }

    [Fact]
    public void BevelPair_AtARightAngleReducesToTheToothRatio()
    {
        var pair = new BevelPair(20, 40);
        Assert.Equal(20.0 / 40, Math.Tan(pair.PinionConeAngleDegrees * Deg), 12);
        Assert.Equal(40.0 / 20, Math.Tan(pair.WheelConeAngleDegrees * Deg), 12);
    }

    [Fact]
    public void BevelPair_SharesOneOuterConeDistance()
    {
        // The identity that makes it a PAIR: either member's own m*z/(2 sin delta) is
        // the same number.
        var pair = new BevelPair(18, 45, 60);
        double fromPinion = pair.OuterConeDistance(3);
        double fromWheel = 3 * pair.WheelTeeth / (2 * Math.Sin(pair.WheelConeAngleDegrees * Deg));
        Assert.Equal(fromPinion, fromWheel, 10);
    }

    [Fact]
    public void BevelPair_RefusesACrownOrInternalCone()
    {
        var ex = Assert.Throws<ArgumentException>(() => new BevelPair(40, 20, 150));
        Assert.Contains("crown", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- the virtual gear

    [Theory]
    [InlineData(3, 20, 26.565051177077986)]
    [InlineData(3, 40, 63.43494882292201)]
    [InlineData(2, 30, 45)]
    [InlineData(4, 24, 30)]
    public void Straight_CarriesTheVirtualGearIdentities(double m, int z, double delta)
    {
        var profile = BevelGears.Straight(new GearSpec(m, z), delta, faceWidth: m * 3);
        double r = m * z / 2;

        // R_e = r / sin(delta); back cone distance r_v = R_e tan(delta) = r / cos(delta);
        // z_v = z / cos(delta) = 2 r_v / m.
        Assert.Equal(r / Math.Sin(delta * Deg), profile.OuterConeDistance, 10);
        Assert.Equal(profile.OuterConeDistance * Math.Tan(delta * Deg), profile.BackConeDistance, 10);
        Assert.Equal(r / Math.Cos(delta * Deg), profile.BackConeDistance, 10);
        Assert.Equal(z / Math.Cos(delta * Deg), profile.VirtualTeeth, 10);
        Assert.Equal(2 * profile.BackConeDistance / m, profile.VirtualTeeth, 10);
    }

    [Fact]
    public void Straight_VirtualToothCountIsGenerallyNotAnInteger()
    {
        // Worth pinning: it is why the virtual gear is a CONSTRUCTION and cannot be
        // handed to Gears.Spur, which takes an int.
        var profile = BevelGears.Straight(new GearSpec(3, 20), 26.565051177077986, 10);
        Assert.Equal(22.360679, profile.VirtualTeeth, 5);
        Assert.True(Math.Abs(profile.VirtualTeeth - Math.Round(profile.VirtualTeeth)) > 0.3);
    }

    // ---------------------------------------------------------------- the cone angles

    [Theory]
    [InlineData(3, 20, 26.565051177077986)]
    [InlineData(3, 40, 63.43494882292201)]
    [InlineData(2, 30, 45)]
    [InlineData(1, 60, 45)]
    [InlineData(5, 20, 20)]
    [InlineData(3, 20, 65)]
    public void Straight_SectionReproducesTheStandardConeAnglesExactly(double m, int z, double delta)
    {
        // THE identity that says the central projection is the right one: read the face
        // and root cone angles back off the SECTION's own radii (atan(R / z_plane)) and
        // they equal the standard delta +/- atan(h/R_e) to machine precision. An axial
        // projection would miss both.
        var spec = new GearSpec(m, z);
        var profile = BevelGears.Straight(spec, delta, faceWidth: m * 2);
        double Re = profile.OuterConeDistance;

        double faceStandard = (delta * Deg + Math.Atan(m * spec.AddendumCoefficient / Re)) / Deg;
        double rootStandard = (delta * Deg - Math.Atan(m * spec.DedendumCoefficient / Re)) / Deg;
        Assert.Equal(faceStandard, profile.FaceConeAngleDegrees, 12);
        Assert.Equal(rootStandard, profile.RootConeAngleDegrees, 12);

        Assert.Equal(faceStandard, Math.Atan(profile.SectionTipRadius / profile.HeelPlaneZ) / Deg, 12);
        Assert.Equal(rootStandard, Math.Atan(profile.SectionRootRadius / profile.HeelPlaneZ) / Deg, 12);
    }

    [Fact]
    public void Straight_ReportsBothTheSectionTipAndTheStandardBackConeTip()
    {
        // The flat-back truncation, stated as two numbers rather than hidden: the model's
        // heel section reaches further than a real bevel's back-cone tip, and the gap
        // grows with the cone angle.
        var shallow = BevelGears.Straight(new GearSpec(3, 20), 15, 10);
        var steep = BevelGears.Straight(new GearSpec(3, 20), 60, 10);
        Assert.True(shallow.SectionTipRadius > shallow.BackConeTipRadius);
        Assert.True(steep.SectionTipRadius > steep.BackConeTipRadius);
        Assert.True(
            steep.SectionTipRadius / steep.BackConeTipRadius
                > shallow.SectionTipRadius / shallow.BackConeTipRadius,
            "the flat-back overshoot must grow with the cone angle");
    }

    // ---------------------------------------------------------------- the drawn section

    [Fact]
    public void Straight_SectionCarriesThePitchDiameterAndArcToothThickness()
    {
        // Measured OFF the sketch by bisecting the region boundary along the pitch
        // circle - the drawn tooth, not a stored number.
        const double m = 3;
        const int z = 20;
        var profile = BevelGears.Straight(new GearSpec(m, z), 26.565051177077986, 10);
        var region = profile.Sketch.ToRegion();
        double r = m * z / 2;

        double At(double theta) =>
            region.SignedDistance(new Vector2d(r * Math.Cos(theta), r * Math.Sin(theta)));
        Assert.True(At(0) < 0, "the pitch circle on +X must be inside a tooth");

        double half = Bisect(At, 0, Math.PI / z);
        double thickness = 2 * half * r;
        // Arc tooth thickness at the pitch circle is pi*m/2 for an unshifted gear; the
        // drawn flank carries the biarc fit's own deviation, so that is the bar.
        Assert.Equal(Math.PI * m / 2, thickness, profile.MaxFitDeviation * 2);
    }

    [Fact]
    public void Straight_SectionHasExactlyTheRequestedToothCount()
    {
        const int z = 17;
        var profile = BevelGears.Straight(new GearSpec(3, z), 30, 8);
        var region = profile.Sketch.ToRegion();
        double r = 3 * z / 2.0;

        // Sample [0, 2*pi) and close the cycle explicitly, never evaluating at 2*pi:
        // sin(2*pi) is -2.4e-16, which puts that sample a hair below the +X axis where a
        // full circle in the sketch would have its parity seam (see the note in
        // PlanetaryGearTests - SketchRegion returns the wrong sign in that band).
        bool Inside(double theta) =>
            region.SignedDistance(new Vector2d(r * Math.Cos(theta), r * Math.Sin(theta))) < 0;

        int transitions = 0;
        const int samples = 19997;
        bool first = Inside(0), previous = first;
        for (int i = 1; i < samples; i++)
        {
            bool inside = Inside(2 * Math.PI * i / samples);
            if (inside != previous)
                transitions++;
            previous = inside;
        }
        if (previous != first) transitions++;
        Assert.Equal(2 * z, transitions);   // one entry and one exit per tooth
    }

    [Fact]
    public void Straight_FlankMatchesTheProjectedInvoluteComputedIndependently()
    {
        // The strongest check here: this test rebuilds the whole construction from the
        // spec - virtual gear on the back cone, wrapped, projected centrally from the
        // pitch apex - and measures the drawn sketch against it. Nothing the factory
        // returned is reused except the sketch itself.
        const double m = 3, delta = 26.565051177077986;
        const int z = 20;
        var spec = new GearSpec(m, z);
        var profile = BevelGears.Straight(spec, delta, faceWidth: 10);

        double measured = WorstFlankDeviation(profile.Sketch, spec, delta, spec.PressureAngleDegrees);
        Assert.True(measured <= profile.MaxFitDeviation * 1.5,
            $"drawn flank is {measured:E3} from the independently projected involute, but the factory "
            + $"reported a fit deviation of only {profile.MaxFitDeviation:E3}");

        // MUTATION: the same instrument, asked for the wrong pressure angle. If it could
        // not tell them apart the agreement above would mean nothing.
        double mutated = WorstFlankDeviation(profile.Sketch, spec, delta, spec.PressureAngleDegrees + 2);
        Assert.True(mutated > 50 * measured,
            $"the instrument cannot see a 2 deg pressure-angle error: correct {measured:E3}, "
            + $"mutated {mutated:E3}");
    }

    [Fact]
    public void Straight_FlankFitHonoursTheStatedTolerance()
    {
        var spec = new GearSpec(3, 20);
        var tight = BevelGears.Straight(spec, 26.565051177077986, 10, fitTolerance: 1e-5);
        var loose = BevelGears.Straight(spec, 26.565051177077986, 10, fitTolerance: 1e-2);

        Assert.True(tight.MaxFitDeviation <= tight.FitTolerance,
            $"{tight.MaxFitDeviation:E3} > {tight.FitTolerance:E3}");
        Assert.True(loose.MaxFitDeviation <= loose.FitTolerance,
            $"{loose.MaxFitDeviation:E3} > {loose.FitTolerance:E3}");
        Assert.True(tight.CurvesPerFlank > loose.CurvesPerFlank,
            "a tighter tolerance must spend more arcs");
    }

    // ---------------------------------------------------------------- the solid

    [Fact]
    public void Straight_SolidIsExactlyTheConeOverItsHeelSection()
    {
        // A cone from the apex over a planar region of area A at height H has volume
        // A*H/3; truncating it at a scale k leaves A*H*(1 - k^3)/3. The section's area is
        // EXACT (lines and arcs), so this is an analytic oracle for the whole loft:
        // section placement, the toe scale and the ruled skin at once.
        var profile = BevelGears.Straight(new GearSpec(3, 20), 26.565051177077986, 10, fitTolerance: 3e-2);
        var mesh = BevelGears.Solid(profile).ToMesh(Coarse);

        Assert.True(mesh.IsClosed, "a bevel gear solid must be closed");
        double predicted = profile.Sketch.Area() * profile.HeelPlaneZ
            * (1 - Math.Pow(profile.ToeScale, 3)) / 3;
        Assert.Equal(1.0, mesh.Volume() / predicted, 3);
    }

    [Fact]
    public void Straight_ConeVolumeIdentityConvergesWithTessellation()
    {
        // The residual above is the tessellation's chord error, not a modelling error:
        // refining the mesh drives it down. (It is not one-sided - the root arcs and
        // fillets are concave, so their chords ADD material while the tip arc's removes.)
        var profile = BevelGears.Straight(new GearSpec(3, 20), 26.565051177077986, 10, fitTolerance: 3e-2);
        var shape = BevelGears.Solid(profile);
        double predicted = profile.Sketch.Area() * profile.HeelPlaneZ
            * (1 - Math.Pow(profile.ToeScale, 3)) / 3;

        double coarse = Math.Abs(shape.ToMesh(new MeshQuality { CurveSamples = 2, SegmentsPerCircle = 8 })
            .Volume() / predicted - 1);
        double fine = Math.Abs(shape.ToMesh(new MeshQuality { CurveSamples = 6, SegmentsPerCircle = 16 })
            .Volume() / predicted - 1);
        Assert.True(fine < coarse / 2, $"coarse {coarse:E3} did not improve to {fine:E3}");
    }

    [Fact]
    public void Straight_VolumeIdentitySeesAnAxiallyMeasuredFaceWidth()
    {
        // The mutation that matters for the loft: face width is measured along the pitch
        // CONE element, not along the axis. Taking it axially would move the toe plane
        // and change the volume by ~10% - far above the identity's own residual, so the
        // test above is not passing by luck.
        var profile = BevelGears.Straight(new GearSpec(3, 20), 26.565051177077986, 10, fitTolerance: 3e-2);
        double area = profile.Sketch.Area(), h = profile.HeelPlaneZ;
        double correct = area * h * (1 - Math.Pow(profile.ToeScale, 3)) / 3;

        double axialScale = (h - 10) / h;
        double mutated = area * h * (1 - Math.Pow(axialScale, 3)) / 3;
        Assert.True(Math.Abs(mutated / correct - 1) > 0.05,
            "the two conventions must be far enough apart for the volume test to separate them");

        double measured = BevelGears.Solid(profile).ToMesh(Coarse).Volume();
        Assert.True(Math.Abs(measured / correct - 1) < Math.Abs(measured / mutated - 1) / 10,
            "the measured volume must sit far closer to the cone-element convention");
    }

    [Fact]
    public void Straight_SolidLiesBetweenTheRootAndTipConeFrusta()
    {
        // A blank sanity check against two closed forms the teeth must bracket: the
        // gear contains the root-cone frustum and is contained in the face-cone one.
        var profile = BevelGears.Straight(new GearSpec(3, 20), 26.565051177077986, 10, fitTolerance: 3e-2);
        double volume = BevelGears.Solid(profile).ToMesh(Coarse).Volume();

        double Frustum(double coneAngleDegrees)
        {
            double tan = Math.Tan(coneAngleDegrees * Deg);
            double zh = profile.HeelPlaneZ, zt = profile.ToePlaneZ;
            return Math.PI * tan * tan * (zh * zh * zh - zt * zt * zt) / 3;
        }
        double rootFrustum = Frustum(profile.RootConeAngleDegrees);
        double tipFrustum = Frustum(profile.FaceConeAngleDegrees);
        Assert.True(volume > rootFrustum, $"{volume:F3} <= root frustum {rootFrustum:F3}");
        Assert.True(volume < tipFrustum, $"{volume:F3} >= tip frustum {tipFrustum:F3}");
    }

    [Fact]
    public void StraightGear_BoresAndRefusesABoreThroughTheToeRoot()
    {
        var spec = new GearSpec(3, 20);
        const double delta = 26.565051177077986;
        var profile = BevelGears.Straight(spec, delta, 10, fitTolerance: 3e-2);
        double solidVolume = BevelGears.Solid(profile).ToMesh(Coarse).Volume();
        double boredVolume = BevelGears.Solid(profile, boreDiameter: 12).ToMesh(Coarse).Volume();

        // Compare against the DISCRETE truth, not pi*r^2*h: the bore wall tessellates as
        // an inscribed n-gon prism at SegmentsPerCircle, so (n/2)*r^2*sin(2pi/n)*h is
        // what the mesh can actually contain (pi*r^2*h is 4.5% high at n = 12).
        double height = profile.HeelPlaneZ - profile.ToePlaneZ;
        const int n = 12;
        double prism = n / 2.0 * 36 * Math.Sin(2 * Math.PI / n) * height;
        Assert.Equal(1.0, (solidVolume - boredVolume) / prism, 2);

        double toeRoot = profile.SectionRootRadius * profile.ToeScale;
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BevelGears.Solid(profile, boreDiameter: 2 * toeRoot + 1));
    }

    [Fact]
    public void StraightGear_IsBRepNative()
    {
        // Lines and arcs lofted through two sections: nothing is bridged.
        var gear = BevelGears.StraightGear(new GearSpec(3, 20), 26.565051177077986, 10,
            fitTolerance: 3e-2);
        Assert.True(gear.CanConvertTo(TargetRep.Brep));
        Assert.True(gear.CanConvertTo(TargetRep.Mesh));
        Assert.True(gear.CanConvertTo(TargetRep.Implicit));
    }

    // ---------------------------------------------------------------- refusals

    [Fact]
    public void Straight_UndercutIsDecidedByTheVirtualToothCount()
    {
        // A bevel tolerates FEWER real teeth than a spur gear, because z_v = z/cos(delta)
        // is the count the rack sees. 13 teeth is undercut as a spur at 20 deg (the limit
        // is 17.1) and is fine on a 45 deg cone, where z_v = 18.4.
        Assert.Throws<ArgumentException>(() => Gears.Spur(new GearSpec(3, 13)));
        var bevel = BevelGears.Straight(new GearSpec(3, 13), 45, faceWidth: 6);
        Assert.True(bevel.VirtualTeeth > 17.097);

        // ... and the same 13 teeth on a SHALLOW cone is refused, naming z_v.
        var ex = Assert.Throws<ArgumentException>(() => BevelGears.Straight(new GearSpec(3, 13), 10, 6));
        Assert.Contains("z_v", ex.Message);
        Assert.Contains("undercut", ex.Message);
    }

    [Fact]
    public void Straight_SteepConesRefuseOnTheRootFilletAndTheRefusalIsActionable()
    {
        // The flat-back truncation's real limit, pinned so it cannot rot: the heel
        // SECTION's tooth grows deeper than the back-cone tooth as delta rises (x1.0 at
        // 10 deg, x2.4 at 65 deg), so the root gap eventually cannot hold two ISO 53
        // profile-A fillets. It is nearly independent of tooth count, which is what says
        // it is a property of the construction rather than of any one gear.
        var spec = new GearSpec(3, 20);
        var ex = Assert.Throws<ArgumentException>(() => BevelGears.Straight(spec, 72, faceWidth: 6));
        Assert.Contains("root gap", ex.Message);
        Assert.Contains("RootFilletCoefficient", ex.Message);

        // ... and the remedy the message names actually works, so it is a limit rather
        // than a dead end. A 3:1 pair's wheel sits at 71.6 deg.
        var relieved = new GearSpec(3, 20) { RootFilletCoefficient = 0.30 };
        var profile = BevelGears.Straight(relieved, 72, faceWidth: 6);
        Assert.True(profile.SectionTipRadius > profile.SectionRootRadius);

        var pair = new BevelPair(20, 60);
        Assert.Equal(71.565, pair.WheelConeAngleDegrees, 3);
        BevelGears.Straight(relieved with { }, pair.WheelConeAngleDegrees, 6);
    }

    [Fact]
    public void Straight_RefusesAFaceWidthPastTheApex()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => BevelGears.Straight(new GearSpec(3, 20), 26.565051177077986, faceWidth: 100));
        Assert.Contains("apex", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(120)]
    [InlineData(-10)]
    public void Straight_RefusesAConeAngleOutsideTheOpenRange(double delta) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BevelGears.Straight(new GearSpec(3, 20), delta, 8));

    [Fact]
    public void Spiral_And_Hypoid_AreRefusedByName()
    {
        var spiral = Assert.Throws<NotSupportedException>(
            () => BevelGears.Spiral(new GearSpec(3, 20), 45, 10, spiralAngleDegrees: 35));
        Assert.Contains("envelope", spiral.Message);
        Assert.Contains("StraightGear", spiral.Message);

        var hypoid = Assert.Throws<NotSupportedException>(
            () => BevelGears.Hypoid(new GearSpec(3, 20), 45, 10, offset: 20));
        Assert.Contains("hyperboloid", hypoid.Message);
    }

    // ---------------------------------------------------------------- helpers

    private static MeshQuality Coarse => new() { CurveSamples = 4, SegmentsPerCircle = 12 };

    private static double Bisect(Func<double, double> at, double inside, double outside)
    {
        for (int i = 0; i < 60; i++)
        {
            double mid = (inside + outside) / 2;
            if (at(mid) < 0) inside = mid; else outside = mid;
        }
        return (inside + outside) / 2;
    }

    /// <summary>
    /// The independent oracle: the virtual spur gear's involute on the back cone,
    /// projected centrally from the pitch apex onto the heel plane, measured against the
    /// drawn sketch. Written from the spec alone — no factory internals.
    /// </summary>
    private static double WorstFlankDeviation(
        Sketch sketch, GearSpec spec, double deltaDegrees, double pressureAngleDegrees)
    {
        double m = spec.Module;
        int z = spec.Teeth;
        double alpha = pressureAngleDegrees * Deg, delta = deltaDegrees * Deg;
        double cosD = Math.Cos(delta), sin2 = Math.Sin(delta) * Math.Sin(delta);
        double r = m * z / 2, rv = r / cosD, zv = z / cosD;
        double rbv = rv * Math.Cos(alpha);
        double theta0 = -(Math.PI / 2) / zv - (Math.Tan(alpha) - alpha);
        double rav = rv + m * spec.AddendumCoefficient;
        double rfv = rv - m * spec.DedendumCoefficient;

        // The projection's radial map, with its pole at w = 1/sin^2(delta). Sample only
        // BETWEEN the root and tip radii, where the flank actually is.
        double Radius(double w) => r * w * cosD * cosD / (1 - w * sin2);
        double tTip = Math.Sqrt(rav * rav / (rbv * rbv) - 1);
        double tLow = rfv > rbv ? Math.Sqrt(rfv * rfv / (rbv * rbv) - 1) : 0;

        var curves = sketch.ToCurves();
        double worst = 0;
        for (int i = 0; i <= 200; i++)
        {
            double t = tLow + (tTip - tLow) * i / 200;
            double rho = rbv * Math.Sqrt(1 + t * t);
            double theta = (theta0 + t - Math.Atan(t)) / cosD;
            double rad = Radius(rho / rv);
            var p = new Vector2d(rad * Math.Cos(theta), rad * Math.Sin(theta));

            double best = double.PositiveInfinity;
            foreach (var curve in curves)
                best = Math.Min(best, curve.DistanceTo(p));
            worst = Math.Max(worst, best);
        }
        return worst;
    }
}
