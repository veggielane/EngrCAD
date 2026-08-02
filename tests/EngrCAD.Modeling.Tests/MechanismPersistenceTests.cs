using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Mechanism persistence (MechanismPersistence.cs): the joint layer a MateSet file
/// loses — joints with their saved perpendicular references and unwrap state,
/// couplings with their factory arguments, cam laws by the Feature.SaveInputs
/// precedent. The oracle throughout is save→load→save being a BYTE-IDENTICAL fixed
/// point, which catches a field written but never read, a default that reloads
/// differently, and an ordering that is not a function of the model — none of which a
/// pose comparison can see.
/// </summary>
public class MechanismPersistenceTests
{
    private static Frame3d At(double x, double y, double z) =>
        Frame3d.FromXY((x, y, z), Vector3d.UnitX, Vector3d.UnitY);

    private static Part BoxPart(string name) => new(name, MeshPrimitives.Box(4, 2, 1));

    /// <summary>The HigherPairTests gear rig plus a cam-driven follower and a raw
    /// (redundant-consistent) mate — one of everything the envelope carries.</summary>
    private static (Assembly Rig, Mechanism Mechanism, RevoluteJoint PinA, RevoluteJoint PinB,
        PrismaticJoint Slide) FullRig()
    {
        var rig = new Assembly("gearbox");
        var housing = rig.Add(BoxPart("housing"));
        var gearA = rig.Add(BoxPart("gearA"), At(0, 0, 0));
        var gearB = rig.Add(BoxPart("gearB"), At(30, 0, 0));
        var follower = rig.Add(BoxPart("follower"), At(30, 20, 0));
        var z = Vector3d.UnitZ;
        var pinA = Joint.Revolute(
            MateGeometry.Axis(housing, (0, 0, 0), z), MateGeometry.Axis(gearA, (0, 0, 0), z), "pin A")
            .WithLimits(-720, 1440);
        var pinB = Joint.Revolute(
            MateGeometry.Axis(housing, (30, 0, 0), z), MateGeometry.Axis(gearB, (0, 0, 0), z), "pin B");
        var slide = Joint.Prismatic(
            MateGeometry.Axis(housing, (30, 20, 0), Vector3d.UnitY),
            MateGeometry.Axis(follower, (0, 0, 0), Vector3d.UnitY), "follower");
        var mechanism = new Mechanism(rig)
            .Ground(housing)
            .Add(pinA).Add(pinB).Add(slide)
            .Add(Mate.Parallel(
                MateGeometry.Axis(housing, (0, 0, 0), z), MateGeometry.Axis(gearA, (0, 0, 0), z),
                "spindle alignment"))
            .Add(Coupling.Gear(pinA, pinB, teethA: 20, teethB: 40))
            .Add(Coupling.Cam(pinB, slide, CamLaw.Segments(
                (Math.PI, CamLaw.Dwell()),
                (Math.PI, CamLaw.Cycloidal(6, Math.PI)))));
        return (rig, mechanism, pinA, pinB, slide);
    }

    [Fact]
    public void SaveLoadSave_IsAByteIdenticalFixedPoint()
    {
        var (rig, mechanism, _, _, _) = FullRig();
        string first = mechanism.SaveMechanism();

        var loaded = new Mechanism(rig);
        var warnings = loaded.LoadMechanism(first);
        Assert.Empty(warnings);
        Assert.Equal(mechanism.Joints.Count, loaded.Joints.Count);
        Assert.Equal(mechanism.Couplings.Count, loaded.Couplings.Count);

        Assert.Equal(first, loaded.SaveMechanism());
    }

    [Fact]
    public void LoadedMechanism_DrivesLikeTheOriginal()
    {
        var (rig, mechanism, _, _, _) = FullRig();
        string saved = mechanism.SaveMechanism();

        var loaded = new Mechanism(rig);
        Assert.Empty(loaded.LoadMechanism(saved));
        var pinA = (RevoluteJoint)loaded.Joints[0];
        var pinB = (RevoluteJoint)loaded.Joints[1];
        var slide = (PrismaticJoint)loaded.Joints[2];

        loaded.SolveAt(MechanismDriver.Angle(pinA), Math.PI / 2);
        Assert.Equal(Math.PI / 2, pinA.Angle, 9);
        Assert.Equal(-Math.PI / 4, pinB.Angle, 9);   // gear 20:40 counter-rotates
        // The follower rides the reloaded cam law of pin B's unwrapped angle.
        var camLaw = CamLaw.Segments(
            (Math.PI, CamLaw.Dwell()),
            (Math.PI, CamLaw.Cycloidal(6, Math.PI)));
        camLaw.Evaluate(pinB.Angle, out double lift, out _, out _);
        camLaw.Evaluate(0, out double zeroLift, out _, out _);
        Assert.Equal(lift - zeroLift, slide.Displacement, 6);

        // And the limits reloaded with the joint: pin A saved with [-720°, 1440°].
        Assert.NotNull(pinA.AngleLimits);
        Assert.Equal(-720 * Math.PI / 180, pinA.AngleLimits!.Value.Min, 12);
        Assert.Equal(1440 * Math.PI / 180, pinA.AngleLimits!.Value.Max, 12);
    }

    [Fact]
    public void UnwrappedAngleHistory_SurvivesTheRoundTrip()
    {
        // The part of a mechanism no pose can recover: how many turns the crank has
        // taken. Sweep two full turns, save mid-history, reload — the loaded joint
        // must read 4π, not the 0 a fresh construction at the same pose would.
        var (rig, mechanism, pinA, pinB, _) = FullRig();
        var study = mechanism.Sweep(MechanismDriver.Angle(pinA), 0, 4 * Math.PI, frames: 33);
        Assert.True(study.Completed, study.ToString());
        Assert.Equal(4 * Math.PI, pinA.Angle, 8);

        string saved = mechanism.SaveMechanism();
        var loaded = new Mechanism(rig);
        Assert.Empty(loaded.LoadMechanism(saved));
        var loadedPinA = (RevoluteJoint)loaded.Joints[0];
        var loadedPinB = (RevoluteJoint)loaded.Joints[1];
        Assert.Equal(pinA.Angle, loadedPinA.Angle, 12);
        Assert.Equal(pinB.Angle, loadedPinB.Angle, 12);

        // And the history keeps counting: driving on from 4π continues the same turn
        // count instead of snapping to the nearest branch of a fresh zero.
        loaded.SolveAt(MechanismDriver.Angle(loadedPinA), 4 * Math.PI + 0.3);
        Assert.Equal(4 * Math.PI + 0.3, loadedPinA.Angle, 8);
    }

    [Fact]
    public void EveryJointKind_RoundTrips()
    {
        // A serial chain carrying all seven kinds. Nothing is ever assembled — Add's
        // per-joint DOF verification is the gate being exercised — and the fixed point
        // covers kind-specific fields (screw pitch, planar gap) plus the axis joints'
        // references and state.
        var rig = new Assembly("chain");
        var bodies = new Occurrence[8];
        bodies[0] = rig.Add(BoxPart("b0"));
        for (int i = 1; i < 8; i++)
            bodies[i] = rig.Add(BoxPart($"b{i}"), At(10 * i, 0, 0));
        var z = Vector3d.UnitZ;
        MateRef A(int k) => MateGeometry.Axis(bodies[k], (10, 0, 0), z);
        MateRef B(int k) => MateGeometry.Axis(bodies[k], (0, 0, 0), z);
        // A planar joint's faces BEAR on each other: normals oppose at construction.
        MateRef Bearing(int k) => MateGeometry.Axis(bodies[k], (0, 0, 0), -z);

        var mechanism = new Mechanism(rig)
            .Ground(bodies[0])
            .Add(Joint.Revolute(A(0), B(1), "hinge"))
            .Add(Joint.Prismatic(A(1), B(2), "slider"))
            .Add(Joint.Cylindrical(A(2), B(3), "pin"))
            .Add(Joint.Spherical(A(3), B(4), "ball"))
            .Add(Joint.Planar(A(4), Bearing(5), gap: 0.5, name: "pad"))
            .Add(Joint.Screw(A(5), B(6), pitch: 2.5, name: "leadscrew"))
            .Add(Joint.Fixed(A(6), B(7), "weld"));

        string first = mechanism.SaveMechanism();
        var loaded = new Mechanism(rig);
        Assert.Empty(loaded.LoadMechanism(first));
        Assert.Equal(first, loaded.SaveMechanism());

        Assert.Equal(2.5, ((ScrewJoint)loaded.Joints[5]).Pitch);
        Assert.Equal(0.5, ((PlanarJoint)loaded.Joints[4]).Gap);
    }

    [Fact]
    public void FromSketchLaw_RoundTripsAsItsSamples()
    {
        // A sketch cam law persists as its sampled lifts — the law IS the samples, and
        // the spline rebuild is deterministic, so the loaded law evaluates
        // BIT-identically, sketch nowhere in the room.
        var (rig, mechanism, _, pinB, slide) = FullRig();
        var law = CamLaw.FromSketch(Sketch.Circle(new Vector2d(3, 0), 8), samples: 90);
        mechanism.Add(Coupling.Cam(pinB, slide, law, "profile cam"));

        string first = mechanism.SaveMechanism();
        var loaded = new Mechanism(rig);
        Assert.Empty(loaded.LoadMechanism(first));
        Assert.Equal(first, loaded.SaveMechanism());

        var restored = loaded.Couplings.Single(c => c.Name == "profile cam");
        var restoredLaw = restored.SaveData!.Law!;
        for (double theta = 0; theta < 2 * Math.PI; theta += 0.41)
        {
            law.Evaluate(theta, out double a, out double va, out double ka);
            restoredLaw.Evaluate(theta, out double b, out double vb, out double kb);
            Assert.Equal(a, b);
            Assert.Equal(va, vb);
            Assert.Equal(ka, kb);
        }
    }

    [Fact]
    public void OpaqueLambdaLaw_SavesAMarkerAndLoadsViaTheHook()
    {
        var (rig, mechanism, _, pinB, slide) = FullRig();
        var lambda = CamLaw.FromFunction(t => 2 * t, _ => 2, _ => 0);
        mechanism.Add(Coupling.Cam(pinB, slide, lambda, "code cam"));

        string saved = mechanism.SaveMechanism();
        Assert.Contains("opaque", saved);

        // Without the hook: the coupling is skipped with a warning naming it — never
        // silently dropped, never a crash.
        var bare = new Mechanism(rig);
        var warnings = bare.LoadMechanism(saved);
        Assert.Contains(warnings, w => w.Contains("code cam") && w.Contains("FromFunction"));
        Assert.DoesNotContain(bare.Couplings, c => c.Name == "code cam");
        // Everything else still loaded.
        Assert.Equal(mechanism.Joints.Count, bare.Joints.Count);

        // With the hook: the caller supplies the law and the coupling comes back.
        var resolved = new Mechanism(rig);
        var hookWarnings = resolved.LoadMechanism(saved,
            name => name == "code cam" ? lambda : null);
        Assert.Empty(hookWarnings);
        Assert.Contains(resolved.Couplings, c => c.Name == "code cam");

        // The second save of the bare load is the file MINUS exactly the record the
        // warning named, then a fixed point — the FeatureHistory drift rule.
        string second = bare.SaveMechanism();
        var rebare = new Mechanism(rig);
        rebare.LoadMechanism(second);
        Assert.Equal(second, rebare.SaveMechanism());
    }

    [Fact]
    public void MissingOccurrence_SkipsTheJointAndItsCouplingsByName()
    {
        var (_, mechanism, _, _, _) = FullRig();
        string saved = mechanism.SaveMechanism();

        // The same rig minus gearB: pin B cannot resolve, so it is skipped — and so
        // are BOTH couplings, each referencing it by index.
        var smaller = new Assembly("gearbox");
        var housing = smaller.Add(BoxPart("housing"));
        smaller.Add(BoxPart("gearA"), At(0, 0, 0));
        smaller.Add(BoxPart("follower"), At(30, 20, 0));
        _ = housing;

        var loaded = new Mechanism(smaller);
        var warnings = loaded.LoadMechanism(saved);
        Assert.Contains(warnings, w => w.Contains("joint 'pin B'") && w.Contains("gearB"));
        Assert.Contains(warnings, w => w.Contains("gear 20:40") && w.Contains("did not load"));
        Assert.Contains(warnings, w => w.Contains("cam") && w.Contains("did not load"));
        Assert.Equal(2, loaded.Joints.Count);       // pin A and the follower slide
        Assert.Empty(loaded.Couplings);
    }

    [Fact]
    public void UnknownVersion_IsRefusedByName()
    {
        var (rig, mechanism, _, _, _) = FullRig();
        string saved = mechanism.SaveMechanism();
        Assert.Throws<FormatException>(
            () => new Mechanism(rig).LoadMechanism(saved.Replace("\"version\": 1", "\"version\": 99")));
        Assert.Throws<FormatException>(
            () => new Mechanism(rig).LoadMechanism("{}"));
    }
}
