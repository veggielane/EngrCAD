using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// A meshed pair DRIVEN, and checked at every pose of the sweep.
///
/// <para><b>A still only has to be right at one instant; a running pair has to be right
/// at every one.</b> A wrong phase or a wrong ratio is invisible in a static render that
/// was phased by hand — the picture was approved at the one angle it was drawn at — and
/// becomes teeth passing through each other the moment the pair turns. So the animation
/// on the docs page is a by-product of this, rather than a picture somebody looked at.</para>
///
/// <para><b>What this does and does not verify.</b> The mechanism carries a
/// <c>Coupling.Gear</c>, which ENFORCES the ratio, so nothing here is evidence about
/// conjugate action — <c>GearTests.ConjugateAction_RatioConstantThroughToothHandover</c>
/// owns that, measured from contact with no solver in the room. What a coupling says
/// nothing whatever about is the PHASE, which is exactly what is under test: the pair is
/// posed by <see cref="GearMeshing"/> and then rolled, and a phase error the coupling
/// cannot see becomes interpenetration the instrument can.</para>
///
/// <para>The instrument is the pair's own exact 2D signed distance evaluated through the
/// sweep's world matrices — no tessellation, no tolerance beyond the flank fit's own
/// deviation, and an answer in millimetres of penetration.
/// <see cref="MotionStudy.CheckInterference"/> is cross-checked against it below.</para>
/// </summary>
public class GearTrainMotionTests
{
    private const double Module = 2;
    private const int PinionTeeth = 18, WheelTeeth = 27;
    private const double Backlash = 0.4;   // extra centre distance; an involute pair is invariant to it

    /// <summary>2.5 pinion tooth pitches — long enough that contact is handed from one
    /// tooth pair to the next twice, which is where a phase error shows.</summary>
    private const double SweepRange = 2.5 * 2 * Math.PI / PinionTeeth;

    [Fact]
    public void ADrivenPair_NeverInterpenetrates_AtAnyPoseOfTheSweep()
    {
        var rig = GearRig.Build();
        var study = rig.Sweep(frames: 41);
        Assert.True(study.Completed, study.ToString());
        Assert.Equal(41, study.Frames.Count);

        var (worst, at) = rig.WorstClearance(study);
        // The nominal flank gap at 0.4 mm of extra centre distance, measured statically as
        // 0.1413; rolling holds it (an involute pair's backlash is constant through the
        // mesh cycle), so the sweep minimum sits just under it wherever a flank is between
        // samples of the outline.
        Assert.True(worst > 0.05,
            $"the pair bites at driver value {at:0.####}: clearance {worst:0.#####}");
        Assert.True(worst < 0.30,
            $"the pair never comes into mesh at all: clearance {worst:0.#####} — the instrument "
            + "must be measuring the flanks, not the space behind them");
    }

    /// <summary>
    /// <b>The mutation check.</b> Without it a passing sweep only shows the instrument
    /// found nothing, which is also what a blind instrument reports. Half a tooth pitch of
    /// phase error is the classic wrong answer — the ratio is still exactly right, so the
    /// coupling is perfectly satisfied and only the geometry objects.
    /// </summary>
    [Fact]
    public void AHalfToothPhaseError_BitesAcrossTheWholeSweep()
    {
        var rig = GearRig.Build(phaseError: Math.PI / WheelTeeth);
        var study = rig.Sweep(frames: 41);
        Assert.True(study.Completed, study.ToString());

        var (worst, _) = rig.WorstClearance(study);
        Assert.True(worst < -1.0,
            $"a half-pitch phase error must bite deep at some pose; worst clearance {worst:0.#####}");

        // Every frame, not merely some: a half-pitch error is a standing offset, so a
        // sweep that only bit occasionally would mean the measurement was drifting.
        foreach (var frame in study.Frames)
            Assert.True(rig.ClearanceAt(frame) < -1.0, $"frame at {frame.Value:0.####} did not bite");
    }

    /// <summary>
    /// The second mutation, and a different failure entirely: the phase is right at the
    /// start and the RATIO is wrong by one tooth, so the pair begins in mesh and walks out
    /// of it. That is the error a single still cannot show at all.
    /// </summary>
    [Fact]
    public void AOneToothRatioError_StartsInMeshAndWalksIntoIt()
    {
        var rig = GearRig.Build(couplingWheelTeeth: WheelTeeth + 1);
        var study = rig.Sweep(frames: 41);
        Assert.True(study.Completed, study.ToString());

        // Frame 0 is the posed configuration, so it is as clear as the correct build.
        Assert.True(rig.ClearanceAt(study.Frames[0]) > 0.05,
            "the ratio error must be invisible at the pose the pair was built in");
        var (worst, at) = rig.WorstClearance(study);
        Assert.True(worst < -0.2,
            $"one tooth of ratio error must bite before the sweep ends; worst {worst:0.#####} at {at:0.####}");
    }

    /// <summary>
    /// The two instruments weighed against each other on the same rig.
    /// <see cref="MotionStudy.CheckInterference"/> can see this — the pair is not JOINTED
    /// to each other (each gear hangs off the housing), so it is not one of the pairs that
    /// check skips — but it answers a BOOLEAN off the display tessellation where the 2D
    /// probe answers a depth off the exact profile. Both are asserted, and the boolean one
    /// is the reason the depth one is the primary: it costs real gear solids and a mesh
    /// crossing test per frame, so it runs at a tenth of the frame count.
    /// </summary>
    [Fact]
    public void CheckInterference_AgreesWithTheExactProbe_OnBothTheGoodAndTheBadBuild()
    {
        var clean = GearRig.Build(solids: true);
        Assert.True(clean.Sweep(frames: 5).CheckInterference().Clear,
            "a correctly phased pair must not clash");

        var broken = GearRig.Build(phaseError: Math.PI / WheelTeeth, solids: true);
        var report = broken.Sweep(frames: 5).CheckInterference();
        Assert.False(report.Clear, "a half-pitch phase error must clash");
        Assert.Contains(report.Pairs, p => p.PathA.Contains("pinion") && p.PathB.Contains("wheel"));
    }

    /// <summary>
    /// The set the docs page animates, checked the same way: every planet against the
    /// sun AND against the ring, at every recorded pose of the sweep. Two meshes per
    /// planet is what makes it worth running separately from the pair above — a phase
    /// that satisfies one and not the other is exactly the failure the planetary
    /// assembly condition exists to rule out, and the ring mesh runs the OTHER way with
    /// the azimuth.
    /// </summary>
    [Fact]
    public void ADrivenPlanetarySet_KeepsBothMeshes_AtEveryPose()
    {
        var rig = PlanetaryRig.Build();
        var study = rig.Sweep(frames: 25);
        Assert.True(study.Completed, study.ToString());

        // A nominal set is zero-backlash, so the conjugate flanks TOUCH; what is
        // asserted is that nothing bites. Both meshes, every planet, every frame.
        var (worstSun, worstRing) = rig.WorstClearances(study);
        Assert.True(worstSun > -0.01, $"a planet bites the sun: {worstSun:0.#####}");
        Assert.True(worstRing > -0.01, $"a planet bites the ring: {worstRing:0.#####}");

        // ...and the picture genuinely turns: over 30 degrees of carrier the sun makes
        // 1 + z_ring/z_sun = 3.5 times as much angle, which is the reduction the figure
        // is about. Read off the joints rather than off the formula.
        Assert.Equal(
            rig.Set.RingHeldRatio,
            (rig.Planetary.SunPin.Angle + rig.Planetary.CarrierPin.Angle) / rig.Planetary.CarrierPin.Angle,
            9);
    }

    [Fact]
    public void AHalfToothPhaseErrorOnOnePlanet_IsSeenAcrossThePlanetarySweep()
    {
        var rig = PlanetaryRig.Build(phaseErrorOnFirstPlanet: Math.PI / 18);
        var study = rig.Sweep(frames: 25);
        Assert.True(study.Completed, study.ToString());

        var (worstSun, worstRing) = rig.WorstClearances(study);
        Assert.True(worstSun < -1.0, $"the sun mesh must bite; measured {worstSun:0.#####}");
        Assert.True(worstRing < -1.0, $"the ring mesh must bite; measured {worstRing:0.#####}");
    }

    [Fact]
    public void TheSweepIsDeterministic()
    {
        var a = GearRig.Build().Sweep(frames: 13);
        var b = GearRig.Build().Sweep(frames: 13);
        for (int i = 0; i < a.Frames.Count; i++)
        {
            for (int k = 0; k < a.Frames[i].Instances.Count; k++)
                Assert.Equal(a.Frames[i].Instances[k].World, b.Frames[i].Instances[k].World);
        }
    }

    /// <summary>The rig: two gears on parallel axes, posed by <see cref="GearMeshing"/>,
    /// pinned to a grounded housing and tied by one gear coupling.</summary>
    private sealed class GearRig
    {
        private IPlanarRegion _pinionRegion = null!;
        private List<Vector2d> _wheelOutline = null!;
        private double _reachSquared;

        public required Mechanism Mechanism { get; init; }
        public required RevoluteJoint PinionPin { get; init; }

        public static GearRig Build(
            double phaseError = 0, int? couplingWheelTeeth = null, bool solids = false)
        {
            var pinionSpec = new GearSpec(Module, PinionTeeth);
            var wheelSpec = new GearSpec(Module, WheelTeeth);
            double a = GearMeshing.ExternalCentreDistance(pinionSpec, wheelSpec) + Backlash;
            var mesh = GearMeshing
                .External(pinionSpec.Teeth, wheelSpec.Teeth, a)
                .RolledBy(phaseError);

            // A mechanism poses BODIES; only the interference cross-check needs them to be
            // the real gears, so everything else runs on cheap placeholders and measures
            // the profiles directly.
            var rig = new Assembly("gear train");
            var housing = rig.Add(new Part("housing", Shape.Box(2, 2, 1)));
            var pinion = rig.Add(new Part("pinion", solids
                ? Gears.SpurGear(pinionSpec, faceWidth: 8, boreDiameter: 8)
                : Shape.Box(2, 2, 1)));
            var wheel = rig.Add(new Part("wheel", solids
                ? Gears.SpurGear(wheelSpec, faceWidth: 8, boreDiameter: 12)
                : Shape.Box(2, 2, 1)), mesh.Frame);

            var z = Vector3d.UnitZ;
            var pinionPin = Joint.Revolute(
                MateGeometry.Axis(housing, Vector3d.Zero, z),
                MateGeometry.Axis(pinion, Vector3d.Zero, z), "pinion");
            var wheelPin = Joint.Revolute(
                MateGeometry.Axis(housing, mesh.Centre, z),
                MateGeometry.Axis(wheel, Vector3d.Zero, z), "wheel");

            var mechanism = new Mechanism(rig)
                .Ground(housing)
                .Add(pinionPin)
                .Add(wheelPin)
                .Add(Coupling.Gear(
                    pinionPin, wheelPin, PinionTeeth, couplingWheelTeeth ?? WheelTeeth));
            mechanism.Assemble();

            return new GearRig { Mechanism = mechanism, PinionPin = pinionPin }
                .WithProfiles(pinionSpec, wheelSpec);
        }

        private GearRig WithProfiles(GearSpec pinionSpec, GearSpec wheelSpec)
        {
            _pinionRegion = Gears.Spur(pinionSpec).Sketch.ToRegion();
            _wheelOutline = GearContact.Outline(Gears.Spur(wheelSpec).Sketch);
            double reach = pinionSpec.TipDiameter / 2 + 0.05;
            _reachSquared = reach * reach;
            return this;
        }

        public MotionStudy Sweep(int frames) =>
            Mechanism.Sweep(MechanismDriver.Angle(PinionPin), 0, SweepRange, frames);

        public (double Worst, double At) WorstClearance(MotionStudy study)
        {
            double worst = double.PositiveInfinity, at = 0;
            foreach (var frame in study.Frames)
            {
                double clearance = ClearanceAt(frame);
                if (clearance < worst)
                {
                    worst = clearance;
                    at = frame.Value;
                }
            }
            return (worst, at);
        }

        /// <summary>
        /// The minimum signed distance of the wheel's outline in the pinion's material, at
        /// this frame's poses. The frame's world matrices carry the whole pose chain, so
        /// the map wheel-local → world → pinion-local is read off the sweep rather than
        /// re-derived from the driver angle — which is what makes this a check on the
        /// MOTION and not on the arithmetic that produced it.
        /// </summary>
        public double ClearanceAt(MotionFrame frame)
        {
            var pinion = frame.Instances.First(i => i.Path.EndsWith("pinion", StringComparison.Ordinal));
            var wheel = frame.Instances.First(i => i.Path.EndsWith("wheel", StringComparison.Ordinal));
            var toPinion = pinion.World.Inverse() * wheel.World;

            double min = double.PositiveInfinity;
            foreach (var p in _wheelOutline)
            {
                var q = toPinion.TransformPoint(new Vector3d(p.X, p.Y, 0));
                if (q.X * q.X + q.Y * q.Y > _reachSquared)
                    continue;
                min = Math.Min(min, _pinionRegion.SignedDistance(new Vector2d(q.X, q.Y)));
            }
            return min;
        }
    }

    /// <summary>The set the docs page animates, on placeholder bodies: only the POSES
    /// matter, and the profiles are probed directly.</summary>
    private sealed class PlanetaryRig
    {
        private IPlanarRegion _sunRegion = null!;
        private IPlanarRegion _ringRegion = null!;
        private List<Vector2d> _planetOutline = null!;

        public required PlanetarySet Set { get; init; }
        public required PlanetaryMechanism Planetary { get; init; }

        public static PlanetaryRig Build(double phaseErrorOnFirstPlanet = 0)
        {
            var set = new PlanetarySet(module: 2, sunTeeth: 24, planetTeeth: 18, planetCount: 3);
            var body = new Part("body", Shape.Box(2, 2, 1));

            var rig = new Assembly("planetary");
            var housing = rig.Add(body, name: "housing");
            var carrier = rig.Add(body, name: "carrier");
            var sun = rig.Add(body, name: "sun");
            var ring = rig.Add(body, new GearMesh(Vector3d.Zero, set.RingPhase).Frame, "ring");
            var planets = new List<Occurrence>();
            for (int k = 0; k < set.PlanetCount; k++)
            {
                var mesh = GearMeshing.Internal(
                    set.Ring, set.Planet, set.PlanetAzimuth(k), set.RingPhase);
                // The docs snippet asserts this equality; it is the whole reason
                // PlanetarySet can delegate.
                Assert.Equal(set.PlanetPhase(k), mesh.Phase);
                if (k == 0)
                    mesh = mesh.RolledBy(phaseErrorOnFirstPlanet);
                planets.Add(rig.Add(body, mesh.Frame, $"planet.{k + 1}"));
            }

            var planetary = PlanetaryGears.Mechanism(set, rig, housing, carrier, sun, ring, planets);
            planetary.Mechanism.Ground(ring);
            Assert.Equal(1, planetary.Mechanism.Assemble().RemainingDegreesOfFreedom);

            return new PlanetaryRig { Set = set, Planetary = planetary }.WithProfiles();
        }

        private PlanetaryRig WithProfiles()
        {
            _sunRegion = Gears.Spur(Set.Sun).Sketch.ToRegion();
            _ringRegion = PlanetaryGears
                .RingProfile(Set.Ring, 2 * Set.RingRootRadius + 4 * Set.Module).ToRegion();
            _planetOutline = GearContact.Outline(Gears.Spur(Set.Planet).Sketch);
            return this;
        }

        public MotionStudy Sweep(int frames) => Planetary.Mechanism.Sweep(
            MechanismDriver.Angle(Planetary.CarrierPin), 0, Math.PI / 6, frames);

        public (double Sun, double Ring) WorstClearances(MotionStudy study)
        {
            double sun = double.PositiveInfinity, ring = double.PositiveInfinity;
            foreach (var frame in study.Frames)
            {
                var sunWorld = Named(frame, "sun");
                var ringWorld = Named(frame, "ring");
                for (int k = 0; k < Set.PlanetCount; k++)
                {
                    var planetWorld = Named(frame, $"planet.{k + 1}");
                    sun = Math.Min(sun, Probe(_sunRegion, sunWorld.Inverse() * planetWorld));
                    ring = Math.Min(ring, Probe(_ringRegion, ringWorld.Inverse() * planetWorld));
                }
            }
            return (sun, ring);
        }

        private static Matrix4d Named(MotionFrame frame, string suffix) =>
            frame.Instances.First(i => i.Path.EndsWith("/" + suffix, StringComparison.Ordinal)).World;

        private double Probe(IPlanarRegion region, in Matrix4d toRegion)
        {
            double min = double.PositiveInfinity;
            foreach (var p in _planetOutline)
            {
                var q = toRegion.TransformPoint(new Vector3d(p.X, p.Y, 0));
                min = Math.Min(min, region.SignedDistance(new Vector2d(q.X, q.Y)));
            }
            return min;
        }
    }
}
