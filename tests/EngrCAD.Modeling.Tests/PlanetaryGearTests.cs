using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Planetary (epicyclic) sets: the assembly conditions, the internal ring geometry, the
/// mesh phasing measured from CONTACT, and the train ratios read back off the mechanism
/// solver.
/// <para>The ratio tests are deliberately NOT circular. <c>Coupling.Gear</c> does enforce
/// a ratio — but only for one mesh at a time, sun-to-planet and planet-to-ring. Nothing
/// anywhere states the Willis relation or the held-ring ratio; those emerge from the two
/// couplings composing through the carrier, so asserting them against the solver tests the
/// arrangement (which body each joint connects, and which angles are therefore relative)
/// rather than restating a constraint.</para>
/// </summary>
public class PlanetaryGearTests
{
    private static PlanetarySet Standard => new(module: 2, sunTeeth: 24, planetTeeth: 18, planetCount: 3);

    // ---------------------------------------------------------------- assembly conditions

    [Fact]
    public void Set_DerivesTheRingCountFromCoaxiality()
    {
        var set = Standard;
        Assert.Equal(60, set.RingTeeth);
        Assert.Equal(set.SunTeeth + 2 * set.PlanetTeeth, set.RingTeeth);

        // The same statement in RADII, which is where it comes from: the ring's pitch
        // radius is the sun's plus two planet diameters... i.e. r_r = r_s + 2 r_p.
        Assert.Equal(
            set.Sun.PitchDiameter / 2 + set.Planet.PitchDiameter,
            set.Ring.PitchDiameter / 2, 12);
        Assert.Equal(set.CentreDistance, set.Sun.PitchDiameter / 2 + set.Planet.PitchDiameter / 2, 12);
    }

    [Theory]
    [InlineData(24, 18, 3)]   // 24 + 60 = 84 = 3*28
    [InlineData(24, 18, 4)]   // 84 is not divisible by 4
    [InlineData(20, 20, 3)]   // 20 + 60 = 80 is not divisible by 3
    [InlineData(30, 15, 4)]   // 30 + 60 = 90 is not divisible by 4
    public void Set_EnforcesTheEqualSpacingAssemblyCondition(int sun, int planet, int count)
    {
        int ring = sun + 2 * planet;
        bool assembles = (sun + ring) % count == 0;
        if (assembles)
        {
            var set = new PlanetarySet(2, sun, planet, count);
            Assert.Equal(ring, set.RingTeeth);
        }
        else
        {
            var ex = Assert.Throws<ArgumentException>(() => new PlanetarySet(2, sun, planet, count));
            Assert.Contains("divisible", ex.Message);
            Assert.Contains($"{sun + ring}", ex.Message);
        }
    }

    [Fact]
    public void Set_RefusesPlanetsThatWouldTouchTheirNeighbours()
    {
        // Big planets around a small sun: the tip circles overlap along the centre chord.
        // 12 + 84 = 96 is divisible by 6, so the ASSEMBLY condition passes and only the
        // clearance one can catch it - which is the point of having both.
        var ex = Assert.Throws<ArgumentException>(() => new PlanetarySet(2, 12, 36, 6));
        Assert.Contains("do not fit", ex.Message);
        Assert.Contains("tip circles", ex.Message);
    }

    [Fact]
    public void Set_ReportsTheNeighbourClearanceItChecked()
    {
        var set = Standard;
        double chord = 2 * set.CentreDistance * Math.Sin(Math.PI / set.PlanetCount);
        double tips = set.Module * (set.PlanetTeeth + 2);
        Assert.Equal(chord - tips, set.NeighbourClearance, 12);
        Assert.True(set.NeighbourClearance > 0);

        // A single planet has no neighbour, so the condition is vacuous rather than tight.
        Assert.True(double.IsPositiveInfinity(new PlanetarySet(2, 24, 18, 1).NeighbourClearance));
    }

    // ---------------------------------------------------------------- the internal ring

    [Fact]
    public void RingProfile_PutsItsTipsInsideThePitchCircleAndItsRootsOutside()
    {
        // An internal gear inverts the radial roles. Both radii are measured OFF the
        // drawn sketch by bisecting along a tooth ray and a space ray.
        var set = Standard;
        var sketch = PlanetaryGears.RingProfile(set.Ring, 2 * set.RingRootRadius + 8);
        var region = sketch.ToRegion();
        int z = set.RingTeeth;

        // RingProfile draws a tooth SPACE centred on +X, so a tooth is centred at pi/z.
        double At(double theta, double r) =>
            region.SignedDistance(new Vector2d(r * Math.Cos(theta), r * Math.Sin(theta)));

        double tip = BisectRadius(r => At(Math.PI / z, r), 0, set.RingRootRadius + 1);
        double root = BisectRadius(r => At(0, r), 0, set.RingRootRadius + 1);
        Assert.Equal(set.RingTipRadius, tip, 3);
        Assert.Equal(set.RingRootRadius, root, 3);
        Assert.True(set.RingTipRadius < set.Ring.PitchDiameter / 2);
        Assert.True(set.RingRootRadius > set.Ring.PitchDiameter / 2);
    }

    [Fact]
    public void RingProfile_HasTheRequestedToothCountAndAHalfPitchSpace()
    {
        var set = Standard;
        var sketch = PlanetaryGears.RingProfile(set.Ring, 2 * set.RingRootRadius + 8);
        var region = sketch.ToRegion();
        double r = set.Ring.PitchDiameter / 2;

        Assert.True(region.SignedDistance(new Vector2d(r, 0)) > 0,
            "+X is a tooth SPACE on the ring, so it must be outside material");

        // Sampled INCLUSIVELY of 2*pi on purpose. That last sample lands ~1.5e-14 below
        // the +X axis (sin(2*pi) is -2.4e-16), which is the seam ordinate of the blank's
        // full outer circle - the band where SketchRegion used to return the wrong SIGN.
        // This test carried a [0, 2*pi) workaround while that defect stood; sampling the
        // seam is now the point, so a regression shows up here as an ODD transition
        // count, which is combinatorially impossible on a closed curve.
        int transitions = 0;
        const int samples = 23999;
        bool previous = Inside(0);
        for (int i = 1; i <= samples; i++)
        {
            bool inside = Inside(2 * Math.PI * i / samples);
            if (inside != previous) transitions++;
            previous = inside;
        }
        Assert.Equal(2 * set.RingTeeth, transitions);

        bool Inside(double theta) =>
            region.SignedDistance(new Vector2d(r * Math.Cos(theta), r * Math.Sin(theta))) < 0;

        // Zero-backlash nominal: at the pitch circle the space is exactly half the pitch.
        double half = Bisect(t => -region.SignedDistance(new Vector2d(r * Math.Cos(t), r * Math.Sin(t))),
            0, Math.PI / set.RingTeeth);
        Assert.Equal(Math.PI * set.Module / 2, 2 * half * r, 2);
    }

    [Fact]
    public void RingGear_IsASolidRimAndExactInEveryRepresentation()
    {
        var set = Standard;
        double od = 2 * set.RingRootRadius + 8;
        var ring = PlanetaryGears.RingGear(set.Ring, faceWidth: 6, outerDiameter: od);

        Assert.True(ring.CanConvertTo(TargetRep.Brep));
        Assert.True(ring.CanConvertTo(TargetRep.Mesh));

        var mesh = ring.ToMesh(new MeshQuality { CurveSamples = 3, SegmentsPerCircle = 24 });
        Assert.True(mesh.IsClosed);
        // The rim's volume lies between the two annuli the ring's tip and root circles cut.
        double outer = Math.PI * od * od / 4;
        double lower = (outer - Math.PI * set.RingRootRadius * set.RingRootRadius) * 6;
        double upper = (outer - Math.PI * set.RingTipRadius * set.RingTipRadius) * 6;
        Assert.InRange(mesh.Volume(), lower * 0.98, upper * 1.02);
    }

    [Fact]
    public void RingProfile_RefusesABlankThatDoesNotClearItsRoot()
    {
        var set = Standard;
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlanetaryGears.RingProfile(set.Ring, 2 * set.RingTipRadius));
    }

    // ---------------------------------------------------------------- phasing, from contact

    [Fact]
    public void PlanetPhases_MakeEveryPlanetMeshWithBothTheSunAndTheRing()
    {
        // The substance of the arrangement, measured rather than asserted: place each
        // planet at its solved phase and measure its outline against the sun's material
        // and the ring's with the sketches' own exact signed distance. Zero-backlash
        // teeth should TOUCH - so the contact must be within the flank fit's own
        // deviation of zero, in both directions.
        var set = Standard;
        var fit = Gears.Spur(set.Planet).MaxFitDeviation;
        var (toSun, toRing) = MeasureContact(set, phaseOffset: 0);

        double bar = 20 * fit;
        Assert.True(Math.Abs(toSun) < bar, $"sun contact {toSun:E3} exceeds {bar:E3}");
        Assert.True(Math.Abs(toRing) < bar, $"ring contact {toRing:E3} exceeds {bar:E3}");
    }

    [Fact]
    public void PlanetPhases_ContactInstrumentSeesAWrongPhase()
    {
        // The mutation that gives the test above its meaning: a quarter-pitch phase error
        // is not a small perturbation of a meshing set, it is a collision.
        var set = Standard;
        var (goodSun, goodRing) = MeasureContact(set, 0);
        var (badSun, badRing) = MeasureContact(set, Math.PI / (2 * set.PlanetTeeth));

        Assert.True(badSun < -1, $"a quarter-pitch error must bury the planet in the sun, got {badSun:E3}");
        Assert.True(badRing < -1, $"... and in the ring, got {badRing:E3}");
        Assert.True(Math.Abs(badSun) > 1000 * Math.Abs(goodSun));
        Assert.True(Math.Abs(badRing) > 1000 * Math.Abs(goodRing));
    }

    [Fact]
    public void PlanetPhases_AreTheSameGeometryAtEveryPlanet()
    {
        // Equal spacing plus the assembly condition means the set has N-fold symmetry, so
        // each planet's contact figures must agree - a phase formula that happened to work
        // only at planet 0 would show up here.
        var set = Standard;
        var perPlanet = MeasureContactPerPlanet(set, 0);
        for (int k = 1; k < perPlanet.Count; k++)
        {
            Assert.Equal(perPlanet[0].Sun, perPlanet[k].Sun, 9);
            Assert.Equal(perPlanet[0].Ring, perPlanet[k].Ring, 9);
        }
    }

    [Fact]
    public void Layout_PlacesEveryMemberOnItsOwnAxis()
    {
        var set = new PlanetarySet(2, 24, 18, 3);
        var members = PlanetaryGears.Layout(set, faceWidth: 5, sunBore: 10, planetBore: 6);

        Assert.Equal(set.PlanetCount + 2, members.Count);
        Assert.Equal("sun", members[0].Name);
        Assert.Equal("ring", members[^1].Name);
        for (int k = 0; k < set.PlanetCount; k++)
        {
            var bounds = members[k + 1].Shape.Bounds();
            var centre = bounds.Center;
            double azimuth = set.PlanetAzimuth(k);
            Assert.Equal(set.CentreDistance * Math.Cos(azimuth), centre.X, 6);
            Assert.Equal(set.CentreDistance * Math.Sin(azimuth), centre.Y, 6);
        }
    }

    // ---------------------------------------------------------------- kinematics

    [Fact]
    public void Mechanism_ReproducesTheWillisRelationFromComposedMeshes()
    {
        // (w_sun - w_carrier) / (w_ring - w_carrier) = -z_ring / z_sun.
        // Neither coupling states this: one ties the sun to a planet, the other the planet
        // to the ring, and the carrier is a THIRD body whose motion both are written
        // relative to. The relation is what those compose to.
        var set = Standard;
        var (rig, planetary) = BuildRig(set);
        planetary.Mechanism.Ground(rig.Ring);

        planetary.Mechanism.SolveAt(MechanismDriver.Angle(planetary.CarrierPin), 0.4);

        double sunRelative = planetary.SunPin.Angle;    // w_sun - w_carrier
        double ringRelative = planetary.RingPin.Angle;  // w_ring - w_carrier
        Assert.Equal(set.WillisRatio, sunRelative / ringRelative, 9);
        Assert.Equal(-(double)set.RingTeeth / set.SunTeeth, sunRelative / ringRelative, 9);
    }

    [Fact]
    public void Mechanism_GivesTheHeldRingRatioOfOnePlusRingOverSun()
    {
        // The textbook reduction, likewise emergent: with the ring held, the sun turns
        // 1 + z_ring/z_sun times per carrier turn. Note the sun's ABSOLUTE rotation is
        // its joint angle plus the carrier's, because its pin is on the carrier.
        var set = Standard;
        var (rig, planetary) = BuildRig(set);
        planetary.Mechanism.Ground(rig.Ring);

        planetary.Mechanism.SolveAt(MechanismDriver.Angle(planetary.CarrierPin), 0.4);
        double carrier = planetary.CarrierPin.Angle;
        double sunAbsolute = planetary.SunPin.Angle + carrier;

        Assert.Equal(set.RingHeldRatio, sunAbsolute / carrier, 9);
        Assert.Equal(3.5, sunAbsolute / carrier, 9);          // 1 + 60/24
        Assert.Equal(0.0, planetary.RingPin.Angle + carrier, 9);  // the ring really is still
    }

    [Fact]
    public void Mechanism_GivesTheCarrierHeldRatioWhenTheCarrierIsGrounded()
    {
        // The same set, a different member held: sun and ring counter-rotate at -z_s/z_r.
        var set = Standard;
        var (rig, planetary) = BuildRig(set);
        planetary.Mechanism.Ground(rig.Carrier);

        planetary.Mechanism.SolveAt(MechanismDriver.Angle(planetary.SunPin), 1.0);
        Assert.Equal(0.0, planetary.CarrierPin.Angle, 9);
        Assert.Equal(-(double)set.SunTeeth / set.RingTeeth, planetary.RingPin.Angle, 9);
    }

    [Fact]
    public void Mechanism_PlanetSpinsAtItsOwnRatioAndEveryPlanetAgrees()
    {
        var set = Standard;
        var (rig, planetary) = BuildRig(set);
        planetary.Mechanism.Ground(rig.Ring);
        planetary.Mechanism.SolveAt(MechanismDriver.Angle(planetary.CarrierPin), 0.4);

        // An INTERNAL mesh keeps the sign, so (w_p - w_c) = (z_ring/z_planet)(w_r - w_c):
        // with the ring held that is -(z_ring/z_planet) times the carrier angle, i.e. the
        // planet spins BACKWARDS 3.33x as fast as the carrier goes round.
        double expected = (double)set.RingTeeth / set.PlanetTeeth * planetary.RingPin.Angle;
        foreach (var pin in planetary.PlanetPins)
            Assert.Equal(expected, pin.Angle, 9);
    }

    [Fact]
    public void Mechanism_SweepsThroughAWholeCarrierTurnKeepingTheRatio()
    {
        var set = Standard;
        var (rig, planetary) = BuildRig(set);
        planetary.Mechanism.Ground(rig.Ring);

        var study = planetary.Mechanism.Sweep(
            MechanismDriver.Angle(planetary.CarrierPin), 0, 2 * Math.PI, frames: 13);
        Assert.True(study.Completed, study.ToString());
        // Unwrapped coordinates, so a whole turn of the carrier is 2*pi*(ratio - 1) of
        // sun rotation RELATIVE to it - 15.708 rad, i.e. two and a half turns.
        Assert.Equal(2 * Math.PI * (set.RingHeldRatio - 1), planetary.SunPin.Angle, 7);
    }

    [Fact]
    public void Mechanism_ColdSolveIsLimitedByTheFASTESTMemberNotTheDrivenOne()
    {
        // Worth pinning, because the failure looks like a modelling error and is not.
        // A gear coupling is written on the CHANGE in each joint's coordinate, so a cold
        // Levenberg-Marquardt step has to cross the largest of those changes - and on this
        // train the planet turns z_ring/z_planet = 3.33x the carrier. Driving the carrier
        // 0.8 rad moves the planet 2.67 rad and converges; 1.0 rad asks for 3.33 rad,
        // past a half turn, and the cold solve fails. Continuation is the answer, and it
        // is exactly why Sweep seeds from the previous converged pose.
        var set = Standard;

        var (nearRig, near) = BuildRig(set);
        near.Mechanism.Ground(nearRig.Ring);
        near.Mechanism.SolveAt(MechanismDriver.Angle(near.CarrierPin), 0.8);
        Assert.Equal(-(double)set.RingTeeth / set.PlanetTeeth * 0.8, near.PlanetPins[0].Angle, 9);

        var (farRig, far) = BuildRig(set);
        far.Mechanism.Ground(farRig.Ring);
        Assert.Throws<MateSolveException>(
            () => far.Mechanism.SolveAt(MechanismDriver.Angle(far.CarrierPin), 1.0));

        // ... and the same target reached by continuation is fine.
        var (sweptRig, swept) = BuildRig(set);
        swept.Mechanism.Ground(sweptRig.Ring);
        var study = swept.Mechanism.Sweep(MechanismDriver.Angle(swept.CarrierPin), 0, 1.0, frames: 9);
        Assert.True(study.Completed, study.ToString());
        Assert.Equal(2.5, swept.SunPin.Angle, 9);
    }

    [Fact]
    public void Mechanism_IsRedundantByGrueblerAndOneDegreeOfFreedomByRank()
    {
        // Three planets carry the same relation three times, which is exactly how a real
        // planetary set shares load - so the constraint rows are redundant and the
        // Kutzbach count is far more negative than the truth. The measured rank is the
        // source of truth (the four-bar's lesson, with a much larger gap).
        var set = Standard;
        var (rig, planetary) = BuildRig(set);
        planetary.Mechanism.Ground(rig.Ring);

        var result = planetary.Mechanism.Assemble();
        Assert.Equal(1, result.RemainingDegreesOfFreedom);

        var mobility = planetary.Mechanism.Mobility();
        Assert.False(mobility.Agrees);
        Assert.True(mobility.PredictedDegreesOfFreedom < mobility.MeasuredDegreesOfFreedom,
            $"expected Kutzbach ({mobility.PredictedDegreesOfFreedom}) to under-count the measured rank "
            + $"({mobility.MeasuredDegreesOfFreedom}) on a load-sharing planetary");
    }

    [Fact]
    public void Mechanism_RefusesAPlanetOccurrenceCountThatDoesNotMatchTheSet()
    {
        var set = Standard;
        var rig = new Assembly("gearbox");
        var housing = rig.Add(BoxPart("housing"));
        var carrier = rig.Add(BoxPart("carrier"));
        var sun = rig.Add(BoxPart("sun"));
        var ring = rig.Add(BoxPart("ring"));
        var planets = new[] { rig.Add(BoxPart("p1")), rig.Add(BoxPart("p2")) };

        var ex = Assert.Throws<ArgumentException>(
            () => PlanetaryGears.Mechanism(set, rig, housing, carrier, sun, ring, planets));
        Assert.Contains("3 planets", ex.Message);
    }

    // ---------------------------------------------------------------- helpers

    private static Part BoxPart(string name) => new(name, MeshPrimitives.Box(4, 2, 1));

    private sealed record Rig(
        Assembly Assembly, Occurrence Housing, Occurrence Carrier, Occurrence Sun,
        Occurrence Ring, IReadOnlyList<Occurrence> Planets);

    private static (Rig Rig, PlanetaryMechanism Mechanism) BuildRig(PlanetarySet set)
    {
        // Placeholder bodies: the joints are built from explicit local coordinates, so the
        // kinematics do not depend on the gears' geometry (and a test need not mesh them).
        var assembly = new Assembly("planetary");
        var housing = assembly.Add(BoxPart("housing"));
        var carrier = assembly.Add(BoxPart("carrier"));
        var sun = assembly.Add(BoxPart("sun"));
        var ring = assembly.Add(BoxPart("ring"));
        var planets = new List<Occurrence>();
        for (int k = 0; k < set.PlanetCount; k++)
        {
            double azimuth = set.PlanetAzimuth(k);
            planets.Add(assembly.Add(BoxPart($"planet.{k + 1}"), Frame3d.FromXY(
                (set.CentreDistance * Math.Cos(azimuth), set.CentreDistance * Math.Sin(azimuth), 0),
                Vector3d.UnitX, Vector3d.UnitY)));
        }
        var rig = new Rig(assembly, housing, carrier, sun, ring, planets);
        return (rig, PlanetaryGears.Mechanism(set, assembly, housing, carrier, sun, ring, planets));
    }

    /// <summary>Worst signed distance from any planet's outline into the sun's material
    /// and into the ring's (negative = interpenetration).</summary>
    private static (double Sun, double Ring) MeasureContact(PlanetarySet set, double phaseOffset)
    {
        var all = MeasureContactPerPlanet(set, phaseOffset);
        return (all.Min(p => p.Sun), all.Min(p => p.Ring));
    }

    private static IReadOnlyList<(double Sun, double Ring)> MeasureContactPerPlanet(
        PlanetarySet set, double phaseOffset)
    {
        var sunRegion = Gears.Spur(set.Sun).Sketch.ToRegion();
        var ringRegion = PlanetaryGears.RingProfile(set.Ring, 2 * set.RingRootRadius + 8).ToRegion();
        var planetSketch = Gears.Spur(set.Planet).Sketch;

        var points = new List<Vector2d>();
        foreach (var curve in planetSketch.ToCurves())
            for (int i = 0; i <= 4; i++)
                points.Add(curve.PointAt(
                    curve.Domain.Start + (curve.Domain.End - curve.Domain.Start) * i / 4.0));

        var results = new List<(double, double)>(set.PlanetCount);
        double cr = Math.Cos(-set.RingPhase), sr = Math.Sin(-set.RingPhase);
        for (int k = 0; k < set.PlanetCount; k++)
        {
            double azimuth = set.PlanetAzimuth(k), phase = set.PlanetPhase(k) + phaseOffset;
            double cg = Math.Cos(phase), sg = Math.Sin(phase);
            double cx = set.CentreDistance * Math.Cos(azimuth), cy = set.CentreDistance * Math.Sin(azimuth);
            double sun = double.MaxValue, ring = double.MaxValue;
            foreach (var p in points)
            {
                var world = new Vector2d(p.X * cg - p.Y * sg + cx, p.X * sg + p.Y * cg + cy);
                sun = Math.Min(sun, sunRegion.SignedDistance(world));
                ring = Math.Min(ring, ringRegion.SignedDistance(
                    new Vector2d(world.X * cr - world.Y * sr, world.X * sr + world.Y * cr)));
            }
            results.Add((sun, ring));
        }
        return results;
    }

    private static double Bisect(Func<double, double> at, double inside, double outside)
    {
        for (int i = 0; i < 60; i++)
        {
            double mid = (inside + outside) / 2;
            if (at(mid) < 0) inside = mid; else outside = mid;
        }
        return (inside + outside) / 2;
    }

    /// <summary>Radius at which <paramref name="at"/> crosses from outside material
    /// (positive) to inside (negative).</summary>
    private static double BisectRadius(Func<double, double> at, double outside, double inside)
    {
        Assert.True(at(outside) > 0, "the inner probe must be in free space");
        Assert.True(at(inside) < 0, "the outer probe must be in material");
        for (int i = 0; i < 60; i++)
        {
            double mid = (outside + inside) / 2;
            if (at(mid) > 0) outside = mid; else inside = mid;
        }
        return (outside + inside) / 2;
    }
}
