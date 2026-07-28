using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Interference over the sweep (MotionInterference.cs) and swept volumes as Shapes:
/// per-step clash detection on the meshes that already exist, exact volumes opt-in,
/// and the swept volume implicit-native / B-Rep-impossible.
/// </summary>
public class MotionInterferenceTests
{
    private static Frame3d At(double x, double y, double z) =>
        Frame3d.FromXY((x, y, z), Vector3d.UnitX, Vector3d.UnitY);

    /// <summary>A 20-long arm spinning about the origin, and a stationary post the
    /// arm's tip sweeps through twice per turn (when <paramref name="postX"/> is
    /// inside the arm's reach).</summary>
    private static (Mechanism Mechanism, RevoluteJoint Pin) SpinnerRig(double postX)
    {
        var rig = new Assembly("rig");
        var ground = rig.Add(new Part("ground", MeshPrimitives.Box(2, 2, 1)));
        var arm = rig.Add(new Part("arm", MeshPrimitives.Box(20, 2, 2)));
        rig.Add(new Part("post", MeshPrimitives.Box(2, 2, 6)), At(postX, 0, 0));
        var pin = Joint.Revolute(
            MateGeometry.Axis(ground, (0, 0, 0), Vector3d.UnitZ),
            MateGeometry.Axis(arm, (0, 0, 0), Vector3d.UnitZ), "pin");
        return (new Mechanism(rig).Ground(ground).Add(pin), pin);
    }

    [Fact]
    public void ASweptClash_ReportsThePairAndTheParameterRanges()
    {
        var (mechanism, pin) = SpinnerRig(postX: 8);
        var study = mechanism.Sweep(MechanismDriver.Angle(pin), 0, 2 * Math.PI, frames: 73);
        Assert.True(study.Completed, study.ToString());

        var report = study.CheckInterference();

        var pair = Assert.Single(report.Pairs);
        Assert.Equal("rig/arm", pair.PathA);
        Assert.Equal("rig/post", pair.PathB);
        // The arm passes through the post near 0, π and 2π (the 0-crossing splits
        // across the sweep's two ends).
        Assert.InRange(pair.Ranges.Count, 2, 3);
        foreach (var range in pair.Ranges)
        {
            double nearest = new[] { 0, Math.PI, 2 * Math.PI }
                .Min(c => Math.Min(Math.Abs(range.Start - c), Math.Abs(range.End - c)));
            Assert.True(nearest < 0.3, $"range [{range.Start:g4}, {range.End:g4}] far from any crossing");
        }
    }

    [Fact]
    public void OutOfReach_IsClear()
    {
        var (mechanism, pin) = SpinnerRig(postX: 14);
        var study = mechanism.Sweep(MechanismDriver.Angle(pin), 0, 2 * Math.PI, frames: 37);
        Assert.True(study.CheckInterference().Clear);
    }

    [Fact]
    public void JointedPairs_AreSkippedByDefault_AndIncludedOnRequest()
    {
        // The arm permanently interpenetrates the ground block it is pinned to —
        // exactly the false positive the default exists to silence.
        var (mechanism, pin) = SpinnerRig(postX: 14);
        var study = mechanism.Sweep(MechanismDriver.Angle(pin), 0, Math.PI / 2, frames: 7);

        Assert.True(study.CheckInterference().Clear);

        var including = study.CheckInterference(new InterferenceOptions { IncludeJointedPairs = true });
        var pair = Assert.Single(including.Pairs);
        Assert.Equal(("rig/arm", "rig/ground"), (pair.PathA, pair.PathB));
    }

    [Fact]
    public void ExactVolumes_AreComputedForConfirmedRangesOnly()
    {
        var (mechanism, pin) = SpinnerRig(postX: 8);
        var study = mechanism.Sweep(MechanismDriver.Angle(pin), 0, 2 * Math.PI, frames: 73);

        var report = study.CheckInterference(new InterferenceOptions { ExactVolumes = true });

        var pair = Assert.Single(report.Pairs);
        Assert.All(pair.Ranges, r =>
        {
            Assert.NotNull(r.Volume);
            Assert.True(r.Volume > 0, $"volume {r.Volume} should be positive");
            // Bounded by the post's own volume (2 × 2 × 6).
            Assert.True(r.Volume < 24, $"volume {r.Volume} exceeds the post");
        });
    }

    // ---- swept volume ----

    [Fact]
    public void SweptVolume_OfAFullTurn_IsTheSweptDisk()
    {
        var (mechanism, pin) = SpinnerRig(postX: 14);
        var study = mechanism.Sweep(MechanismDriver.Angle(pin), 0, 2 * Math.PI, frames: 73);

        var swept = study.SweptVolume("arm");

        // Implicit-native, B-Rep honestly impossible.
        Assert.Contains(swept.Explain(TargetRep.Implicit).Entries,
            e => e.Support == NodeSupport.Native && e.Node.StartsWith("SweptOver"));
        Assert.Contains(swept.Explain(TargetRep.Brep).Entries,
            e => e.Support == NodeSupport.Impossible);

        // The union of 73 rotated 20×2×2 bars is (nearly) a disk of radius ~10,
        // thickness 2: volume just under π·10²·2 plus the corner overhang.
        var mesh = swept.ToMesh();
        Assert.True(mesh.IsClosed, "swept volume should polygonize closed");
        double volume = mesh.Volume();
        Assert.InRange(volume, 0.9 * Math.PI * 100 * 2, 1.05 * Math.PI * 101 * 2);
    }

    [Fact]
    public void SweptVolume_OfAnUnknownPath_RefusesListingWhatExists()
    {
        var (mechanism, pin) = SpinnerRig(postX: 14);
        var study = mechanism.Sweep(MechanismDriver.Angle(pin), 0, 1, frames: 5);
        var exception = Assert.Throws<ArgumentException>(() => study.SweptVolume("no-such"));
        Assert.Contains("rig/arm", exception.Message);
    }
}
