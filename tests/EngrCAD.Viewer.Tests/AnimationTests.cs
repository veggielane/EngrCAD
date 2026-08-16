using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The animation timeline (Animation.cs / PoseTracks.cs / CameraTracks.cs in
/// Viewer.Core) — all headless, no GL: <c>Animation.At</c> is a pure function of t, so
/// every behavior here is a value assertion. The load-bearing contracts: instance
/// count/order independent of t, recorded mechanism frames returned VERBATIM at their
/// sample points, exact-zero explode factors leaving frames bit-identical, and yaw
/// interpolation taking the shortest path.
/// </summary>
public class AnimationTests
{
    private static Part BoxPart(string name) => new(name, MeshPrimitives.Box(4, 2, 1));

    // ---- the timeline ----

    [Fact]
    public void SecondPoseOrCameraTrackIsRefused()
    {
        var scene = RigScene(out _, out _);
        var animation = new Animation()
            .With(new ExplodeTrack(scene, deriveOffsets: false))
            .With(new TurntableTrack(new CameraState(0, 0.4, 10, Vector3d.Zero)));
        Assert.Throws<InvalidOperationException>(
            () => animation.With(new ExplodeTrack(scene, deriveOffsets: false)));
        Assert.Throws<InvalidOperationException>(
            () => animation.With(new TurntableTrack(new CameraState(0, 0.4, 10, Vector3d.Zero))));
    }

    [Fact]
    public void AtIsPureAndClamped()
    {
        var turntable = new TurntableTrack(new CameraState(0.7, 0.45, 12, (1, 2, 3)));
        var animation = new Animation(durationSeconds: 4).With(turntable);

        var a = animation.At(0.3);
        var b = animation.At(0.3);
        Assert.Equal(a.Camera, b.Camera);           // same t, same sample
        Assert.Null(a.Instances);                    // no pose track: scene's own poses stand
        Assert.Equal(animation.At(0).Camera, animation.At(-2).Camera);
        Assert.Equal(animation.At(1).Camera, animation.At(7).Camera);
        Assert.Equal(animation.At(0.5).Camera, animation.AtTime(2).Camera);
    }

    [Fact]
    public void SmoothstepEasingShapesTheTimeline()
    {
        var baseCamera = new CameraState(0, 0.45, 12, Vector3d.Zero);
        var linear = new Animation().With(new TurntableTrack(baseCamera));
        var eased = new Animation(easing: AnimationEasing.Smoothstep)
            .With(new TurntableTrack(baseCamera));

        // Smoothstep fixes 0, 1/2 and 1 and slows the start: at t = 0.25 the eased
        // timeline sits at 0.15625.
        Assert.Equal(linear.At(0.5).Camera!.Yaw, eased.At(0.5).Camera!.Yaw, 12);
        Assert.Equal(
            baseCamera.Yaw + 0.15625 * 2 * Math.PI, eased.At(0.25).Camera!.Yaw, 12);
    }

    [Fact]
    public void TrackWindowsClampOutsideAndRunInside()
    {
        var track = new TurntableTrack(new CameraState(0, 0.45, 12, Vector3d.Zero));
        track.Window(0.25, 0.75);
        Assert.Equal(0, track.LocalT(0.1));      // holds the start value before the window
        Assert.Equal(0, track.LocalT(0.25));
        Assert.Equal(0.5, track.LocalT(0.5), 12);
        Assert.Equal(1, track.LocalT(0.9));      // holds the end value after it
        Assert.Throws<ArgumentOutOfRangeException>(() => track.Window(0.5, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => track.Window(-0.1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => track.Window(0, 1.1));
    }

    // ---- camera tracks ----

    [Fact]
    public void TurntableOrbitsAboutZAtFixedPitch()
    {
        var baseCamera = new CameraState(0.7, 0.45, 15, (1, -2, 3));
        var track = new TurntableTrack(baseCamera, turns: 1);

        Assert.Equal(baseCamera, track.CameraAt(0));
        var half = track.CameraAt(0.5);
        Assert.Equal(0.7 + Math.PI, half.Yaw, 12);
        Assert.Equal(0.45, half.Pitch);
        Assert.Equal(15, half.Distance);
        Assert.Equal(baseCamera.Target, half.Target);
        // A whole turn returns to the same view direction: the loop is seamless.
        var full = track.CameraAt(1);
        Assert.Equal(Math.Cos(baseCamera.Yaw), Math.Cos(full.Yaw), 12);
        Assert.Equal(Math.Sin(baseCamera.Yaw), Math.Sin(full.Yaw), 12);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TurntableTrack(baseCamera, turns: 0));
    }

    [Fact]
    public void KeyframedCameraTakesTheShortestYawPath()
    {
        // Yaw 0.1 to 2π − 0.1: the short way is BACKWARD through 0, not forward
        // through π. Naive lerp would swing the camera almost a full turn.
        var track = new KeyframedCameraTrack(
        [
            new CameraKeyframe(0, new CameraState(0.1, 0.4, 10, Vector3d.Zero)),
            new CameraKeyframe(1, new CameraState(2 * Math.PI - 0.1, 0.4, 10, Vector3d.Zero)),
        ]);

        var mid = track.CameraAt(0.5);
        Assert.Equal(0, mid.Yaw, 12);            // halfway between +0.1 and −0.1
        Assert.Equal(track.Keyframes[0].Camera, track.CameraAt(0));
        Assert.Equal(track.Keyframes[1].Camera, track.CameraAt(1));
    }

    [Fact]
    public void KeyframedCameraEasesPerSegmentAndHoldsOutsideItsKeys()
    {
        var track = new KeyframedCameraTrack(
        [
            new CameraKeyframe(0.2, new CameraState(0, 0.2, 10, Vector3d.Zero)),
            new CameraKeyframe(0.8, new CameraState(1, 0.6, 20, (4, 0, 0))),
        ]);

        Assert.Equal(track.Keyframes[0].Camera, track.CameraAt(0));     // holds before
        Assert.Equal(track.Keyframes[1].Camera, track.CameraAt(0.95));  // holds after
        var mid = track.CameraAt(0.5);   // segment midpoint; smoothstep(0.5) = 0.5
        Assert.Equal(0.5, mid.Yaw, 12);
        Assert.Equal(0.4, mid.Pitch, 12);
        Assert.Equal(15, mid.Distance, 12);
        Assert.Equal(2, mid.Target.X, 12);
        // Quarter of the segment: smoothstep(0.25) = 0.15625, not 0.25.
        Assert.Equal(0.15625, track.CameraAt(0.35).Yaw, 12);

        Assert.Throws<ArgumentException>(() => new KeyframedCameraTrack(
            [new CameraKeyframe(0, new CameraState(0, 0, 10, Vector3d.Zero))]));
        Assert.Throws<ArgumentException>(() => new KeyframedCameraTrack(
        [
            new CameraKeyframe(0.5, new CameraState(0, 0, 10, Vector3d.Zero)),
            new CameraKeyframe(0.5, new CameraState(1, 0, 10, Vector3d.Zero)),
        ]));
    }

    [Fact]
    public void FlyThroughFollowsTheCurveLookingAlongTheTangent()
    {
        var path = new Line3d((0, 0, 5), (10, 0, 5));
        var track = new FlyThroughTrack(path, lookAhead: 2);

        var mid = track.CameraAt(0.5);
        // Eye = target − distance·(view direction); reconstruct and compare to the path.
        var eye = CameraMath.Eye(mid.Yaw, mid.Pitch, mid.Distance, mid.Target);
        Assert.Equal(5, eye.X, 9);
        Assert.Equal(0, eye.Y, 9);
        Assert.Equal(5, eye.Z, 9);
        Assert.Equal(7, mid.Target.X, 9);        // looking 2 units ahead along +X
        Assert.Equal(2, mid.Distance, 12);
    }

    [Fact]
    public void FlyThroughLookAtKeepsWatchingTheFixedPoint()
    {
        var path = new Line3d((10, 0, 0), (0, 10, 0));
        var focus = new Vector3d(0, 0, 0);
        var track = new FlyThroughTrack(path, lookAhead: 3, lookAt: focus);

        foreach (double t in new[] { 0.0, 0.3, 0.7, 1.0 })
        {
            var camera = track.CameraAt(t);
            var eye = CameraMath.Eye(camera.Yaw, camera.Pitch, camera.Distance, camera.Target);
            var toFocus = (focus - eye).Normalized();
            var view = (camera.Target - eye).Normalized();
            Assert.Equal(1.0, toFocus.Dot(view), 9);   // still looking at the focus
        }
    }

    [Fact]
    public void FlyThroughClampsNearVerticalTangentsToTheOrbitLimit()
    {
        var path = new Line3d((0, 0, 0), (0, 0, 10));   // straight up
        var track = new FlyThroughTrack(path, lookAhead: 1);
        var camera = track.CameraAt(0.5);
        Assert.Equal(-CameraMath.PitchLimit, camera.Pitch, 12);
    }

    // ---- the explode track ----

    private static Scene RigScene(out Occurrence pinLeft, out Occurrence pinRight)
    {
        var scene = new Scene();
        var plate = BoxPart("plate");
        var pin = BoxPart("pin");
        var rig = new Assembly("rig");
        rig.Add(plate);
        pinLeft = rig.Add(pin, Frame3d.FromXY((-5, 0, 1.5), Vector3d.UnitX, Vector3d.UnitY));
        pinRight = rig.Add(pin, Frame3d.FromXY((5, 0, 1.5), Vector3d.UnitX, Vector3d.UnitY));
        pinLeft.ExplodeOffset = new Vector3d(0, 0, 10);
        pinRight.ExplodeOffset = new Vector3d(0, 0, 10);
        scene.AddTab("rig").Add(rig);
        return scene;
    }

    [Fact]
    public void ExplodeTrackEndpointsMatchTheScalarFlatten()
    {
        var scene = RigScene(out _, out _);
        var track = new ExplodeTrack(scene, deriveOffsets: false);

        var assembled = scene.Instances(0.0).ToList();
        var exploded = scene.Instances(1.0).ToList();
        var at0 = track.PosesAt(0);
        var at1 = track.PosesAt(1);

        Assert.Equal(assembled.Count, at0.Count);
        for (int i = 0; i < assembled.Count; i++)
        {
            Assert.Equal(assembled[i].Path, at0[i].Path);
            Assert.Equal(assembled[i].World, at0[i].World);   // bit-identical at factor 0
            Assert.Equal(exploded[i].World, at1[i].World);
        }
    }

    [Fact]
    public void StaggeredExplodeSequencesOccurrences()
    {
        var scene = RigScene(out var pinLeft, out var pinRight);
        var track = new ExplodeTrack(scene, deriveOffsets: false)
            .Stagger(pinLeft, 0, 0.5)
            .Stagger(pinRight, 0.5, 1);

        // Halfway: the left pin has fully backed out, the right has not moved.
        var mid = track.PosesAt(0.5);
        var assembled = track.PosesAt(0);
        int left = IndexOf(mid, "rig/pin");
        int right = IndexOf(mid, "rig/pin.2");
        Assert.Equal(10, mid[left].World.M34 - assembled[left].World.M34, 12);
        Assert.Equal(assembled[right].World, mid[right].World);   // exact 0 before its window

        // Count and order never change — the SetInstancePoses contract.
        foreach (double t in new[] { 0.0, 0.2, 0.5, 0.8, 1.0 })
        {
            var poses = track.PosesAt(t);
            Assert.Equal(assembled.Count, poses.Count);
            for (int i = 0; i < poses.Count; i++)
                Assert.Equal(assembled[i].Path, poses[i].Path);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => track.Stagger(pinLeft, 0.5, 0.4));
    }

    private static int IndexOf(IReadOnlyList<PartInstance> instances, string path)
    {
        for (int i = 0; i < instances.Count; i++)
        {
            if (instances[i].Path == path)
                return i;
        }
        throw new InvalidOperationException($"no instance at path '{path}'");
    }

    // ---- the mechanism track ----

    private static (Mechanism Mechanism, MotionStudy Study, Assembly Rig) SweptCrank(
        double from, double to, int frames)
    {
        var rig = new Assembly("rig");
        var fixedOne = rig.Add(BoxPart("base"));
        var moving = rig.Add(BoxPart("crank"));
        var joint = Joint.Revolute(
            MateGeometry.Axis(fixedOne, (0, 0, 0), Vector3d.UnitZ),
            MateGeometry.Axis(moving, (0, 0, 0), Vector3d.UnitZ));
        var mechanism = new Mechanism(rig).Ground(fixedOne).Add(joint);
        var study = mechanism.Sweep(MechanismDriver.Angle(joint), from, to, frames);
        Assert.True(study.Completed, study.ToString());
        return (mechanism, study, rig);
    }

    [Fact]
    public void MechanismTrackReturnsRecordedFramesVerbatim()
    {
        var (_, study, _) = SweptCrank(0, Math.PI / 2, frames: 5);
        var track = new MechanismTrack(study);

        for (int i = 0; i < study.Frames.Count; i++)
        {
            double t = i / (double)(study.Frames.Count - 1);
            var poses = track.PosesAt(t);
            var recorded = study.Frames[i].Instances;
            Assert.Equal(recorded.Count, poses.Count);
            for (int k = 0; k < poses.Count; k++)
            {
                Assert.Equal(recorded[k].Path, poses[k].Path);
                Assert.Equal(recorded[k].World, poses[k].World);   // bit-exact: frames are the truth
            }
        }
    }

    [Fact]
    public void MechanismTrackInterpolatesRigidlyBetweenFrames()
    {
        // A crank about a fixed Z axis: the inter-frame delta is a pure rotation about
        // Z, so the chordal interpolation's slerp is EXACT at the half step — the
        // half-angle rotation, to solver precision.
        var (_, study, _) = SweptCrank(0, Math.PI / 2, frames: 5);
        var track = new MechanismTrack(study);

        double midT = 0.5 / (study.Frames.Count - 1);   // halfway through the first step
        var poses = track.PosesAt(midT);
        int crank = IndexOf(poses, "rig/crank");
        double expected = Math.PI / 2 / 4 / 2;   // half of one step
        var x = poses[crank].World.TransformVector(Vector3d.UnitX);
        Assert.Equal(Math.Cos(expected), x.X, 8);
        Assert.Equal(Math.Sin(expected), x.Y, 8);
        // The grounded base does not move between frames — and takes the bit-exact
        // short-circuit rather than a matrix inverse.
        int ground = IndexOf(poses, "rig/base");
        Assert.Equal(study.Frames[0].Instances[IndexOf(study.Frames[0].Instances, "rig/base")].World,
            poses[ground].World);
    }

    [Fact]
    public void MechanismTrackGraftsOntoASceneKeepingBystandersStill()
    {
        var (_, study, rig) = SweptCrank(0, Math.PI / 2, frames: 5);
        var scene = new Scene();
        var tab = scene.AddTab("rig");
        var fixture = tab.Add(BoxPart("fixture"));   // a loose part outside the mechanism
        tab.Add(rig);

        var track = new MechanismTrack(study, scene);
        var template = scene.Instances().ToList();
        foreach (double t in new[] { 0.0, 0.37, 1.0 })
        {
            var poses = track.PosesAt(t);
            Assert.Equal(template.Count, poses.Count);
            for (int i = 0; i < poses.Count; i++)
                Assert.Equal(template[i].Path, poses[i].Path);
            int still = IndexOf(poses, "fixture");
            Assert.Equal(fixture.Transform, poses[still].World);   // bystander never moves
        }
        // The crank DOES move through the graft.
        int crank = IndexOf(track.PosesAt(1), "rig/crank");
        Assert.NotEqual(track.PosesAt(0)[crank].World, track.PosesAt(1)[crank].World);
    }

    [Fact]
    public void MechanismTrackRefusesAForeignScene()
    {
        var (_, study, _) = SweptCrank(0, Math.PI / 2, frames: 3);
        var other = new Scene();
        other.Add(BoxPart("unrelated"));
        Assert.Throws<ArgumentException>(() => new MechanismTrack(study, other));
    }

    [Fact]
    public void RigidInterpolationIsExactAtItsEndpoints()
    {
        // A general rigid delta (rotation about an arbitrary axis + translation),
        // composed onto a base pose that includes a part transform.
        var a = Matrix4d.CreateTranslation((1, 2, 3))
            * Quaterniond.FromAxisAngle(new Vector3d(1, 1, 0).Normalized(), 0.4).ToMatrix();
        var delta = Matrix4d.CreateTranslation((-2, 5, 1))
            * Quaterniond.FromAxisAngle(new Vector3d(0, 1, 3).Normalized(), 1.1).ToMatrix();
        var b = delta * a;

        AssertMatricesEqual(a, MechanismTrack.InterpolateRigid(a, b, 0), 12);
        AssertMatricesEqual(b, MechanismTrack.InterpolateRigid(a, b, 1), 9);
        // Identical poses short-circuit bit-exactly (no inverse, no round-off).
        Assert.Equal(a, MechanismTrack.InterpolateRigid(a, a, 0.37));
    }

    private static void AssertMatricesEqual(in Matrix4d expected, in Matrix4d actual, int digits)
    {
        Assert.Equal(expected.M11, actual.M11, digits);
        Assert.Equal(expected.M12, actual.M12, digits);
        Assert.Equal(expected.M13, actual.M13, digits);
        Assert.Equal(expected.M14, actual.M14, digits);
        Assert.Equal(expected.M21, actual.M21, digits);
        Assert.Equal(expected.M22, actual.M22, digits);
        Assert.Equal(expected.M23, actual.M23, digits);
        Assert.Equal(expected.M24, actual.M24, digits);
        Assert.Equal(expected.M31, actual.M31, digits);
        Assert.Equal(expected.M32, actual.M32, digits);
        Assert.Equal(expected.M33, actual.M33, digits);
        Assert.Equal(expected.M34, actual.M34, digits);
    }

    [Fact]
    public void MechanismTrackRefusesAStudyWithTooFewFrames()
    {
        // A sweep that cannot even start (the first target is beyond a joint stop)
        // records zero frames — nothing to animate, and the track says so instead of
        // rendering a motionless clip.
        var rig = new Assembly("rig");
        var fixedOne = rig.Add(BoxPart("base"));
        var moving = rig.Add(BoxPart("crank"));
        var joint = Joint.Revolute(
                MateGeometry.Axis(fixedOne, (0, 0, 0), Vector3d.UnitZ),
                MateGeometry.Axis(moving, (0, 0, 0), Vector3d.UnitZ))
            .WithLimits(0, 30);
        var mechanism = new Mechanism(rig).Ground(fixedOne).Add(joint);
        var study = mechanism.Sweep(MechanismDriver.Angle(joint), Math.PI, 2 * Math.PI, frames: 5);
        Assert.False(study.Completed);
        Assert.Empty(study.Frames);
        var refusal = Assert.Throws<ArgumentException>(() => new MechanismTrack(study));
        Assert.Contains("at least two", refusal.Message);
    }

    // ---- the field-sequence track ----

    [Fact]
    public void FieldSequenceTrack_HoldsTheLatestStepAtOrBeforeTheInstant()
    {
        // Steps at their REAL times: t maps linearly over [10, 40] s, and the answer is
        // the latest step at or before that instant — hold-last, never a tween, because
        // a colour between two stored solutions is a state the solver never produced.
        var track = new FieldSequenceTrack([("T@10", 10), ("T@20", 20), ("T@40", 40)]);

        Assert.Equal("T@10", track.FieldAt(0));
        Assert.Equal("T@10", track.FieldAt(0.32));     // 19.6 s: still the 10 s step
        Assert.Equal("T@20", track.FieldAt(0.34));     // 20.2 s
        Assert.Equal("T@20", track.FieldAt(0.99));     // 39.7 s: the 40 s step not yet reached
        Assert.Equal("T@40", track.FieldAt(1));
        // Clamp semantics outside [0,1], like every other track.
        Assert.Equal("T@10", track.FieldAt(-2));
        Assert.Equal("T@40", track.FieldAt(3));
        // The displayed instant, for a caption.
        Assert.Equal(10, track.SecondsAt(0));
        Assert.Equal(40, track.SecondsAt(1));
        Assert.Equal(25, track.SecondsAt(0.5));
    }

    [Fact]
    public void FieldSequenceTrack_RidesTheAnimationAsItsOwnSlot()
    {
        var animation = new Animation(durationSeconds: 2)
            .With(new FieldSequenceTrack([("a", 0), ("b", 1)]));

        Assert.Equal("a", animation.At(0).FieldName);
        Assert.Equal("b", animation.At(1).FieldName);
        // A second track refuses — two selections of one result cannot compose.
        Assert.Throws<InvalidOperationException>(() =>
            animation.With(new FieldSequenceTrack([("c", 0), ("d", 1)])));
        // An animation without one says nothing (the null the consumers key on).
        Assert.Null(new Animation().At(0.5).FieldName);
    }

    [Fact]
    public void FieldSequenceTrack_RefusesMalformedRuns()
    {
        Assert.Throws<ArgumentException>(() => new FieldSequenceTrack([]));
        Assert.Throws<ArgumentException>(
            () => new FieldSequenceTrack([("a", 5), ("b", 5)]));          // not increasing
        Assert.Throws<ArgumentException>(
            () => new FieldSequenceTrack([("", 0), ("b", 1)]));           // nameless step
    }
}
