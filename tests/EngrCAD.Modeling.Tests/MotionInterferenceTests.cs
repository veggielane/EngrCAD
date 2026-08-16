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

    [Fact]
    public void ExactVolumes_AreBrepExactForBrepBackedParts()
    {
        // The same spinner built from Shape boxes: both parts lower to B-Reps, so the
        // volume takes the exact boolean of the POSED solids. The oracle is a closed
        // form: at the crossing's middle frame the drive angle is exactly π (73 frames
        // over [0, 2π] put a frame value AT π), where the 20-long arm covers the post's
        // x ∈ [7, 9] and the overlap is the box [7,9] × [−1,1] × [−1,1] = 8 exactly —
        // a claim the mesh route can only approach at its chord grade. The post is
        // WIDER than the arm in y on purpose, so every face pair is transversal.
        var rig = new Assembly("rig");
        var ground = rig.Add(new Part("ground", Shape.Box(2, 2, 1).Translate(0, 0, -4)));
        var arm = rig.Add(new Part("arm", Shape.Box(20, 2, 2)));
        rig.Add(new Part("post", Shape.Box(2, 4, 6)), At(8, 0, 0));
        var pin = Joint.Revolute(
            MateGeometry.Axis(ground, (0, 0, 0), Vector3d.UnitZ),
            MateGeometry.Axis(arm, (0, 0, 0), Vector3d.UnitZ), "pin");
        var mechanism = new Mechanism(rig).Ground(ground).Add(pin);
        var study = mechanism.Sweep(MechanismDriver.Angle(pin), 0, 2 * Math.PI, frames: 73);

        var report = study.CheckInterference(new InterferenceOptions { ExactVolumes = true });
        var pair = Assert.Single(report.Pairs);
        var middle = pair.Ranges.Single(r => r.Start < Math.PI && Math.PI < r.End);
        Assert.Equal(InterferenceVolumeSource.BrepBoolean, middle.VolumeSource);
        Assert.NotNull(middle.Volume);
        Assert.InRange(Math.Abs(middle.Volume!.Value - 8), 0, 1e-9);
    }

    [Fact]
    public void ExactVolumes_NameTheMeshGradeWhereTheyFallBack()
    {
        // Mesh-backed parts have no solid to intersect, so the grade is the mesh
        // boolean's and the SOURCE says so — the incumbent behaviour, now nameable.
        var (mechanism, pin) = SpinnerRig(postX: 8);
        var study = mechanism.Sweep(MechanismDriver.Angle(pin), 0, 2 * Math.PI, frames: 73);
        var report = study.CheckInterference(new InterferenceOptions { ExactVolumes = true });
        Assert.All(Assert.Single(report.Pairs).Ranges,
            r => Assert.Equal(InterferenceVolumeSource.MeshBoolean, r.VolumeSource));

        // And a placement the exact tier REFUSES — a part carrying a SCALED
        // transform, which BrepSolid.Transformed rejects as non-rigid — falls back to
        // the mesh grade rather than failing the whole report. The post is scaled in z
        // only, so the overlap is still the arm-bounded [7,9] x [-1,1] x [-1,1] box and
        // the MESH answer is exact too (a mesh boolean of boxes is exact for
        // polyhedra): the same 8, now carrying the mesh grade's NAME.
        var rig = new Assembly("rig");
        var ground = rig.Add(new Part("ground", Shape.Box(2, 2, 1).Translate(0, 0, -4)));
        var arm = rig.Add(new Part("arm", Shape.Box(20, 2, 2)));
        rig.Add(new Part("post", Shape.Box(2, 4, 6)) { Transform = Matrix4d.CreateScale((1, 1, 1.5)) },
            At(8, 0, 0));
        var pin2 = Joint.Revolute(
            MateGeometry.Axis(ground, (0, 0, 0), Vector3d.UnitZ),
            MateGeometry.Axis(arm, (0, 0, 0), Vector3d.UnitZ), "pin");
        var scaled = new Mechanism(rig).Ground(ground).Add(pin2);
        var scaledStudy = scaled.Sweep(MechanismDriver.Angle(pin2), 0, 2 * Math.PI, frames: 73);
        var scaledReport = scaledStudy.CheckInterference(new InterferenceOptions { ExactVolumes = true });
        var middle2 = Assert.Single(scaledReport.Pairs).Ranges
            .Single(r => r.Start < Math.PI && Math.PI < r.End);
        Assert.Equal(InterferenceVolumeSource.MeshBoolean, middle2.VolumeSource);
        Assert.NotNull(middle2.Volume);
        Assert.InRange(Math.Abs(middle2.Volume!.Value - 8), 0, 1e-9);
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
