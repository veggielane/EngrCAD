using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Adaptive swept-volume sampling. Unioning a body at the study's own frames inherits
/// whatever frame count the SWEEP happened to use, so the scallop between placements is
/// whatever it is — a 20-long arm sampled at 9 frames over a full turn leaves 40°
/// scallops off the swept disk. <c>maxTravel</c> replaces that with a bound in MODEL
/// UNITS, by rigidly interpolating extra placements between the recorded frames.
/// <para>Travel is measured exactly, as the largest displacement of the part's own
/// bounding-box corners between two poses — no rotation angle times an assumed radius —
/// so the refinement is proportional to how far the body actually moves.</para>
/// </summary>
public class AdaptiveSweptVolumeTests
{
    /// <summary>A 20-long arm spinning about the origin: the tip is 10 out, so a coarse
    /// sweep's scallops are large and easy to measure against the analytic disk.</summary>
    private static (Mechanism Mechanism, RevoluteJoint Pin) Spinner()
    {
        var rig = new Assembly("rig");
        var ground = rig.Add(new Part("ground", MeshPrimitives.Box(2, 2, 1)));
        var arm = rig.Add(new Part("arm", MeshPrimitives.Box(20, 2, 2)));
        var pin = Joint.Revolute(
            MateGeometry.Axis(ground, (0, 0, 0), Vector3d.UnitZ),
            MateGeometry.Axis(arm, (0, 0, 0), Vector3d.UnitZ), "pin");
        return (new Mechanism(rig).Ground(ground).Add(pin), pin);
    }

    /// <summary>The disk the arm sweeps: radius 10 (the arm's half length), thickness 2.
    /// A coarse union falls short of it by its scallops; a refined one converges onto it.</summary>
    private const double Disk = Math.PI * 100 * 2;

    [Fact]
    public void RefiningACoarseSweepClosesTheGapToTheAnalyticDisk()
    {
        var (mechanism, pin) = Spinner();
        // Nine frames over a full turn: 45° between placements, which leaves a visible
        // scallop at radius 10 (sagitta 10·(1 − cos 22.5°) = 0.76).
        var study = mechanism.Sweep(MechanismDriver.Angle(pin), 0, Math.Tau, frames: 9);

        double coarse = study.SweptVolume("arm").ToMesh().Volume();
        double refined = study.SweptVolume("arm", maxTravel: 0.5).ToMesh().Volume();

        // Both under-fill the disk (a union of chords is inscribed), and the refined one
        // is strictly closer — the property that makes the bound worth having.
        Assert.True(coarse < Disk, $"coarse {coarse:g6} should not exceed the disk {Disk:g6}");
        Assert.True(refined > coarse, $"refined {refined:g6} should beat coarse {coarse:g6}");
        Assert.True(refined > 0.97 * Disk, $"refined {refined:g6} should approach the disk {Disk:g6}");
    }

    [Fact]
    public void TheRecordedFramesAreKeptExactly_RefinementOnlyFillsBetweenThem()
    {
        var (mechanism, pin) = Spinner();
        var study = mechanism.Sweep(MechanismDriver.Angle(pin), 0, Math.PI / 2, frames: 3);

        // Refining a sweep can only ADD material (every original placement is still in
        // the union), so the volume is monotone in the bound.
        double loose = study.SweptVolume("arm", maxTravel: 8).ToMesh().Volume();
        double tight = study.SweptVolume("arm", maxTravel: 1).ToMesh().Volume();
        double none = study.SweptVolume("arm").ToMesh().Volume();
        Assert.True(none <= loose + 1e-6);
        Assert.True(loose <= tight + 1e-6);
    }

    [Fact]
    public void NoBoundLeavesTheStudysOwnFramesUntouched()
    {
        var (mechanism, pin) = Spinner();
        var study = mechanism.Sweep(MechanismDriver.Angle(pin), 0, Math.PI / 2, frames: 5);

        // A bound so loose that no interval needs subdividing must give bit-identical
        // geometry to no bound at all — the "opt-in changes nothing until it bites" rule.
        double plain = study.SweptVolume("arm").ToMesh().Volume();
        double loose = study.SweptVolume("arm", maxTravel: 1e6).ToMesh().Volume();
        Assert.Equal(plain, loose, 12);
    }

    [Fact]
    public void ANonPositiveBoundIsRefused()
    {
        var (mechanism, pin) = Spinner();
        var study = mechanism.Sweep(MechanismDriver.Angle(pin), 0, 1, frames: 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => study.SweptVolume("arm", maxTravel: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => study.SweptVolume("arm", maxTravel: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => study.SweptVolume("arm", maxTravel: double.NaN));
    }

    [Fact]
    public void InterpolatePoseIsExactAtBothEndsAndRigidBetween()
    {
        // The shared seam MechanismTrack plays a study back with and SweptVolume
        // subdivides one: a half-turn's midpoint must be the quarter turn, not the
        // straight-line average (which would leave the arm short of its own radius).
        var a = Matrix4d.Identity;
        var b = Matrix4d.CreateRotationZ(Math.PI / 2);

        Assert.Equal(a, MotionStudy.InterpolatePose(a, b, 0));
        var half = MotionStudy.InterpolatePose(a, b, 0.5);
        var tip = half.TransformPoint(new Vector3d(10, 0, 0));
        Assert.Equal(10 * Math.Cos(Math.PI / 4), tip.X, 9);
        Assert.Equal(10 * Math.Sin(Math.PI / 4), tip.Y, 9);
        Assert.Equal(10, tip.Length, 9);   // rigid: the radius is preserved

        var end = MotionStudy.InterpolatePose(a, b, 1).TransformPoint(new Vector3d(10, 0, 0));
        Assert.Equal(0, end.X, 9);
        Assert.Equal(10, end.Y, 9);

        // A body that did not move returns its pose by reference-equal value, no solve.
        Assert.Equal(a, MotionStudy.InterpolatePose(a, a, 0.37));
    }
}
