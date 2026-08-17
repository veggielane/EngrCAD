using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// FLEXIBLE sub-assembly placements: one sub-assembly placed twice, each placement
/// holding its OWN internal poses. The refusal for a rigid placement stays by name; a
/// flexible one carries a per-placement pose overlay keyed by the relative occurrence
/// path, which <see cref="Assembly.Flatten(double)"/> reads through — so instances,
/// paths, exploded views and exporters inherit it.
///
/// <para>The test that matters is not "it moves" but that the two placements hold
/// DIFFERENT internal poses while still sharing one <see cref="Part"/>, one mesh and one
/// BOM line — a deep copy would pass the first half and fail the second.</para>
/// </summary>
public class MateFlexibleTests
{
    private static Frame3d At(double x, double y, double z) =>
        Frame3d.FromXY((x, y, z), Vector3d.UnitX, Vector3d.UnitY);

    private static Part BoxPart(string name, double s = 4) =>
        new(name, MeshPrimitives.Box(s, s, s));

    private static Vector3d WorldOf(Assembly root, string path, in Vector3d local)
    {
        var instance = root.Flatten().Single(i => i.Path == path);
        return instance.World.TransformPoint(local);
    }

    /// <summary>A rig with ONE clamp assembly placed twice, both placements flexible.</summary>
    private static (Assembly Rig, Assembly Clamp, Occurrence Ground, Occurrence First, Occurrence Second, Occurrence Bolt)
        TwinClamps(bool flexible = true)
    {
        var clamp = new Assembly("clamp");
        var bolt = clamp.Add(BoxPart("bolt"), At(1, 1, 1));

        var rig = new Assembly("rig");
        var ground = rig.Add(BoxPart("base"));
        var first = rig.Add(clamp, At(0, 0, 0));
        var second = rig.Add(clamp, At(50, 0, 0));
        first.IsFlexible = flexible;
        second.IsFlexible = flexible;
        return (rig, clamp, ground, first, second, bolt);
    }

    // ---- the headline -----------------------------------------------------

    [Fact]
    public void TwoPlacementsOfOneSubAssembly_SolveToDifferentInternalPoses()
    {
        var (rig, clamp, ground, first, second, bolt) = TwinClamps();
        var boltFrameBefore = bolt.Frame;

        var result = new MateSet(rig)
            .Ground(ground)
            .Add(Mate.Coincident(
                MateGeometry.Point(ground, (5, 0, 10)),
                MateGeometry.Point(rig, "clamp/bolt", Vector3d.Zero)))
            .Add(Mate.Coincident(
                MateGeometry.Point(ground, (60, 7, 20)),
                MateGeometry.Point(rig, "clamp.2/bolt", Vector3d.Zero)))
            .Solve();

        Assert.True(result.Converged, result.ToString());
        Assert.True(result.Residual <= 1e-9, $"residual {result.Residual}");

        // Each placement's bolt hit its OWN target — two independent unknowns.
        var a = WorldOf(rig, "rig/clamp/bolt", Vector3d.Zero);
        var b = WorldOf(rig, "rig/clamp.2/bolt", Vector3d.Zero);
        Assert.Equal(5, a.X, 8);
        Assert.Equal(0, a.Y, 8);
        Assert.Equal(10, a.Z, 8);
        Assert.Equal(60, b.X, 8);
        Assert.Equal(7, b.Y, 8);
        Assert.Equal(20, b.Z, 8);

        // The solve wrote OVERLAYS, not the shared frame: the clamp assembly's own
        // occurrence is untouched, bit for bit.
        AssertBitEqual(boltFrameBefore, bolt.Frame);
        var poseA = Assert.Single(first.FlexiblePoses);
        var poseB = Assert.Single(second.FlexiblePoses);
        Assert.Equal("bolt", poseA.Key);
        Assert.Equal("bolt", poseB.Key);
        Assert.NotEqual(poseA.Value.Origin, poseB.Value.Origin);

        // ...and the two placements still share ONE part and ONE assembly object.
        Assert.Same(clamp, first.SubAssembly);
        Assert.Same(clamp, second.SubAssembly);
        Assert.Equal(2, rig.DistinctParts().Count);              // base + bolt, not three
        Assert.Same(bolt.Part, rig.Flatten().Last().Part);
        var line = Assert.Single(Bom.For(rig).Lines, l => l.Part.Name == "bolt");
        Assert.Equal(2, line.Quantity);                          // one line, two occurrences
    }

    [Fact]
    public void EachPlacementReportsItsOwnPath_AndBothAreUnknowns()
    {
        var (rig, _, ground, _, _, _) = TwinClamps();

        var result = new MateSet(rig)
            .Ground(ground)
            .Add(Mate.Coincident(
                MateGeometry.Point(ground, (5, 0, 10)),
                MateGeometry.Point(rig, "clamp/bolt", Vector3d.Zero)))
            .Add(Mate.Coincident(
                MateGeometry.Point(ground, (60, 7, 20)),
                MateGeometry.Point(rig, "clamp.2/bolt", Vector3d.Zero)))
            .Solve();

        Assert.Equal(2, result.OccurrenceFreedoms.Count);
        Assert.Contains(result.OccurrenceFreedoms, f => f.Path == "clamp/bolt");
        Assert.Contains(result.OccurrenceFreedoms, f => f.Path == "clamp.2/bolt");
        Assert.Equal(12, result.FreeDegreesOfFreedom);   // 6 per placement, not 6 shared
    }

    [Fact]
    public void OnlyOnePlacementFlexible_MovesThatOneAndLeavesTheOtherOnTheSharedPose()
    {
        var clamp = new Assembly("clamp");
        var bolt = clamp.Add(BoxPart("bolt"), At(1, 1, 1));
        var rig = new Assembly("rig");
        var ground = rig.Add(BoxPart("base"));
        rig.Add(clamp, At(0, 0, 0));
        var second = rig.Add(clamp, At(50, 0, 0));
        second.IsFlexible = true;

        new MateSet(rig)
            .Ground(ground)
            .Add(Mate.Coincident(
                MateGeometry.Point(ground, (60, 0, 20)),
                MateGeometry.Point(rig, "clamp.2/bolt", Vector3d.Zero)))
            .Solve();

        // The flexible placement moved; the rigid one still reads the shared frame.
        Assert.Equal(20, WorldOf(rig, "rig/clamp.2/bolt", Vector3d.Zero).Z, 8);
        var rigid = WorldOf(rig, "rig/clamp/bolt", Vector3d.Zero);
        Assert.Equal(bolt.Frame.Origin.X, rigid.X, 12);
        Assert.Equal(bolt.Frame.Origin.Z, rigid.Z, 12);
    }

    [Fact]
    public void ADeepFlexibleScope_DistinguishesTwoPlacementsOfAnInnerAssembly()
    {
        // The scope's key is the RELATIVE path, so an assembly placed twice INSIDE a
        // flexible placement needs no second overlay: "jaw/bolt" and "jaw.2/bolt" are
        // different entries of one placement's overlay.
        var jaw = new Assembly("jaw");
        jaw.Add(BoxPart("bolt"), At(1, 0, 0));
        var clamp = new Assembly("clamp");
        clamp.Add(jaw, At(0, 0, 0));
        clamp.Add(jaw, At(10, 0, 0));

        var rig = new Assembly("rig");
        var ground = rig.Add(BoxPart("base"));
        var placement = rig.Add(clamp, At(0, 0, 0));
        placement.IsFlexible = true;

        var result = new MateSet(rig)
            .Ground(ground)
            .Add(Mate.Coincident(
                MateGeometry.Point(ground, (0, 0, 5)),
                MateGeometry.Point(rig, "clamp/jaw/bolt", Vector3d.Zero)))
            .Add(Mate.Coincident(
                MateGeometry.Point(ground, (0, 0, 9)),
                MateGeometry.Point(rig, "clamp/jaw.2/bolt", Vector3d.Zero)))
            .Solve();

        Assert.True(result.Converged, result.ToString());
        Assert.Equal(5, WorldOf(rig, "rig/clamp/jaw/bolt", Vector3d.Zero).Z, 8);
        Assert.Equal(9, WorldOf(rig, "rig/clamp/jaw.2/bolt", Vector3d.Zero).Z, 8);
        Assert.Equal(
            ["jaw.2/bolt", "jaw/bolt"],
            placement.FlexiblePoses.Select(p => p.Key).OrderBy(p => p, StringComparer.Ordinal));
    }

    // ---- the rigid path is untouched --------------------------------------

    [Fact]
    public void AFlexiblePlacementWithNoOverrides_FlattensBitIdenticallyToTheRigidOne()
    {
        // The overlay is a set of OVERRIDES: marking a placement flexible must move
        // nothing at all until something poses it.
        var (rigid, _, _, _, _, _) = TwinClamps(flexible: false);
        var (flexible, _, _, _, _, _) = TwinClamps(flexible: true);

        var a = rigid.Flatten();
        var b = flexible.Flatten();
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Path, b[i].Path);
            AssertBitEqual(a[i].World, b[i].World);
        }

        // ...at every explode factor too, since the offset rides the effective pose.
        foreach (double factor in new[] { 0.0, 0.5, 1.0 })
        {
            var x = rigid.Flatten(factor);
            var y = flexible.Flatten(factor);
            for (int i = 0; i < x.Count; i++)
                AssertBitEqual(x[i].World, y[i].World);
        }
    }

    [Fact]
    public void AModelWithNoFlexiblePlacement_WritesNothingAboutOne()
    {
        var (rig, _, _, _, _, _) = TwinClamps(flexible: false);
        var scene = new Scene();
        scene.AddTab("Model").Add(rig);
        string json = new Document(scene).Save();

        Assert.DoesNotContain("flexible", json);
        Assert.DoesNotContain("flexiblePoses", json);
    }

    // ---- persistence ------------------------------------------------------

    [Fact]
    public void SaveLoadSave_IsAByteFixedPoint()
    {
        var (rig, _, ground, _, _, _) = TwinClamps();
        new MateSet(rig)
            .Ground(ground)
            .Add(Mate.Coincident(
                MateGeometry.Point(ground, (5, 0, 10)),
                MateGeometry.Point(rig, "clamp/bolt", Vector3d.Zero)))
            .Add(Mate.Coincident(
                MateGeometry.Point(ground, (60, 7, 20)),
                MateGeometry.Point(rig, "clamp.2/bolt", Vector3d.Zero)))
            .Solve();

        var scene = new Scene();
        scene.AddTab("Model").Add(rig);
        string first = new Document(scene).Save();
        Assert.Contains("flexiblePoses", first);

        var loaded = Document.Load(first);
        string second = loaded.Document.Save();
        Assert.Equal(first, second);

        // The reloaded placements really carry their own poses (not just their bytes).
        var reloaded = loaded.Document.Scene.Tabs[0].Assemblies[0];
        Assert.Equal(20, ReloadedZ(reloaded, "rig/clamp.2/bolt"), 8);
        Assert.Equal(10, ReloadedZ(reloaded, "rig/clamp/bolt"), 8);
    }

    private static double ReloadedZ(Assembly assembly, string path) =>
        assembly.Flatten().Single(i => i.Path == path).World.TransformPoint(Vector3d.Zero).Z;

    [Fact]
    public void PosesWithoutTheFlag_AreDroppedWithAWarningRatherThanThrowing()
    {
        var (rig, _, _, first, _, _) = TwinClamps();
        first.SetFlexiblePose("bolt", At(3, 3, 3));
        var scene = new Scene();
        scene.AddTab("Model").Add(rig);
        string json = new Document(scene).Save().Replace("\"flexible\": true,", "");

        var loaded = Document.Load(json);
        Assert.Contains(loaded.Warnings, w => w.Contains("not marked flexible"));
    }

    // ---- determinism and undo --------------------------------------------

    [Fact]
    public void TwoSolvesFromTheSameDrawnPoses_AreBitIdentical()
    {
        static string Solve()
        {
            var (rig, _, ground, _, _, _) = TwinClamps();
            new MateSet(rig)
                .Ground(ground)
                .Add(Mate.Coincident(
                    MateGeometry.Point(ground, (5, 0, 10)),
                    MateGeometry.Point(rig, "clamp/bolt", Vector3d.Zero)))
                .Add(Mate.Coincident(
                    MateGeometry.Point(ground, (60, 7, 20)),
                    MateGeometry.Point(rig, "clamp.2/bolt", Vector3d.Zero)))
                .Solve();
            var scene = new Scene();
            scene.AddTab("Model").Add(rig);
            return new Document(scene).Save();
        }

        Assert.Equal(Solve(), Solve());
    }

    /// <summary>
    /// The undo oracle is the document SERIALIZER: after undo, <c>Save()</c> must be
    /// byte-identical to the pre-edit save. That is what catches an overlay a
    /// frames-only capture would have left moved.
    /// </summary>
    [Fact]
    public void UndoingAFlexibleSolve_RestoresAByteIdenticalDocument()
    {
        var (rig, _, ground, first, second, _) = TwinClamps();
        var scene = new Scene();
        scene.AddTab("Model").Add(rig);
        var document = new Document(scene);
        var undo = new UndoStack();

        var set = new MateSet(rig)
            .Ground(ground)
            .Add(Mate.Coincident(
                MateGeometry.Point(ground, (5, 0, 10)),
                MateGeometry.Point(rig, "clamp/bolt", Vector3d.Zero)))
            .Add(Mate.Coincident(
                MateGeometry.Point(ground, (60, 7, 20)),
                MateGeometry.Point(rig, "clamp.2/bolt", Vector3d.Zero)));

        string before = document.Save();
        undo.Do(DocumentEdits.SolveMates(set));
        string after = document.Save();
        Assert.NotEqual(before, after);
        Assert.NotEmpty(first.FlexiblePoses);
        Assert.NotEmpty(second.FlexiblePoses);

        undo.Undo();
        Assert.Equal(before, document.Save());
        Assert.Empty(first.FlexiblePoses);      // the overlay entries went too
        Assert.Empty(second.FlexiblePoses);

        undo.Redo();
        Assert.Equal(after, document.Save());
    }

    // ---- refusals ---------------------------------------------------------

    [Fact]
    public void ARigidPlacement_IsStillRefusedByName_AndNamesTheWayOut()
    {
        var (rig, _, ground, _, _, _) = TwinClamps(flexible: false);
        var mates = new MateSet(rig)
            .Ground(ground)
            .Add(Mate.Coincident(
                MateGeometry.Point(ground, Vector3d.Zero),
                MateGeometry.Point(rig, "clamp.2/bolt", Vector3d.Zero)));

        var exception = Assert.Throws<InvalidOperationException>(() => mates.TrySolve());
        Assert.Contains("placed 2 times", exception.Message);
        Assert.Contains("rig/clamp", exception.Message);
        Assert.Contains("rig/clamp.2", exception.Message);
        Assert.Contains("IsFlexible", exception.Message);
    }

    [Fact]
    public void AFlexibleMarkerBelowAMultiplyPlacedRigidAncestor_IsStillRefused()
    {
        // The overlay object itself would be shared: `jaw.2` is one occurrence of the
        // clamp assembly, and the clamp assembly is placed twice.
        var jaw = new Assembly("jaw");
        jaw.Add(BoxPart("bolt"));
        var clamp = new Assembly("clamp");
        clamp.Add(jaw);
        var inner = clamp.Add(jaw, At(10, 0, 0));
        inner.IsFlexible = true;

        var rig = new Assembly("rig");
        var ground = rig.Add(BoxPart("base"));
        rig.Add(clamp);
        rig.Add(clamp, At(80, 0, 0));

        var mates = new MateSet(rig)
            .Ground(ground)
            .Add(Mate.Coincident(
                MateGeometry.Point(ground, Vector3d.Zero),
                MateGeometry.Point(rig, "clamp/jaw.2/bolt", Vector3d.Zero)));

        var exception = Assert.Throws<InvalidOperationException>(() => mates.TrySolve());
        Assert.Contains("'clamp' is placed 2 times", exception.Message);
    }

    [Fact]
    public void FlexibilityOnAPartPlacement_IsRefusedByName()
    {
        var rig = new Assembly("rig");
        var part = rig.Add(BoxPart("base"));
        var exception = Assert.Throws<InvalidOperationException>(() => part.IsFlexible = true);
        Assert.Contains("places a part", exception.Message);
    }

    [Fact]
    public void PosingThroughARigidPlacement_IsRefusedByName()
    {
        var (_, _, _, first, _, _) = TwinClamps(flexible: false);
        var exception = Assert.Throws<InvalidOperationException>(
            () => first.SetFlexiblePose("bolt", At(1, 2, 3)));
        Assert.Contains("not a flexible placement", exception.Message);
    }

    [Fact]
    public void MakingAPlacementRigidAgain_DiscardsItsPerPlacementPoses()
    {
        var (rig, _, _, first, _, bolt) = TwinClamps();
        first.SetFlexiblePose("bolt", At(9, 9, 9));
        Assert.Equal(9, WorldOf(rig, "rig/clamp/bolt", Vector3d.Zero).Z, 12);

        first.IsFlexible = false;
        Assert.Empty(first.FlexiblePoses);
        Assert.Null(first.FlexiblePose("bolt"));
        // Back to the shared frame — rigid means the shared frames.
        Assert.Equal(bolt.Frame.Origin.Z, WorldOf(rig, "rig/clamp/bolt", Vector3d.Zero).Z, 12);
    }

    // ---- grounding is per placement --------------------------------------

    [Fact]
    public void GroundingOnePlacementsInnerOccurrence_LeavesTheOthersFree()
    {
        var (rig, _, ground, _, _, _) = TwinClamps();

        var result = new MateSet(rig)
            .Ground(ground)
            .Ground("clamp/bolt")
            .Add(Mate.Coincident(
                MateGeometry.Point(rig, "clamp/bolt", Vector3d.Zero),
                MateGeometry.Point(rig, "clamp.2/bolt", Vector3d.Zero)))
            .Solve();

        Assert.True(result.Converged, result.ToString());
        // Only the un-grounded placement is an unknown, and it moved onto the grounded
        // placement's composed world position.
        Assert.Equal("clamp.2/bolt", Assert.Single(result.OccurrenceFreedoms).Path);
        var a = WorldOf(rig, "rig/clamp/bolt", Vector3d.Zero);
        var b = WorldOf(rig, "rig/clamp.2/bolt", Vector3d.Zero);
        Assert.Equal(a.X, b.X, 8);
        Assert.Equal(a.Y, b.Y, 8);
        Assert.Equal(a.Z, b.Z, 8);
    }

    // ---- scale ------------------------------------------------------------

    [Fact]
    public void FlexibleSolving_IsScaleFree()
    {
        foreach (double scale in new[] { 0.01, 1.0, 1000.0 })
        {
            var clamp = new Assembly("clamp");
            clamp.Add(BoxPart("bolt", scale), At(scale, 0, 0));
            var rig = new Assembly($"rig{scale}");
            var ground = rig.Add(BoxPart("base", scale));
            var first = rig.Add(clamp);
            var second = rig.Add(clamp, At(50 * scale, 0, 0));
            first.IsFlexible = true;
            second.IsFlexible = true;

            var result = new MateSet(rig)
                .Ground(ground)
                .Add(Mate.Coincident(
                    MateGeometry.Point(ground, (2 * scale, 0, 0)),
                    MateGeometry.Point(rig, "clamp/bolt", Vector3d.Zero)))
                .Add(Mate.Coincident(
                    MateGeometry.Point(ground, (0, 3 * scale, 0)),
                    MateGeometry.Point(rig, "clamp.2/bolt", Vector3d.Zero)))
                .Solve();

            Assert.True(result.Converged, $"scale {scale}: {result}");
            Assert.Equal(2 * scale, WorldOf(rig, $"rig{scale}/clamp/bolt", Vector3d.Zero).X, 8);
            Assert.Equal(3 * scale, WorldOf(rig, $"rig{scale}/clamp.2/bolt", Vector3d.Zero).Y, 8);
        }
    }

    private static void AssertBitEqual(in Frame3d expected, in Frame3d actual)
    {
        AssertBitEqual(expected.Origin, actual.Origin);
        AssertBitEqual(expected.X, actual.X);
        AssertBitEqual(expected.Y, actual.Y);
    }

    private static void AssertBitEqual(in Vector3d expected, in Vector3d actual)
    {
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.X), BitConverter.DoubleToInt64Bits(actual.X));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Y), BitConverter.DoubleToInt64Bits(actual.Y));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Z), BitConverter.DoubleToInt64Bits(actual.Z));
    }

    private static void AssertBitEqual(in Matrix4d expected, in Matrix4d actual)
    {
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.M11), BitConverter.DoubleToInt64Bits(actual.M11));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.M22), BitConverter.DoubleToInt64Bits(actual.M22));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.M33), BitConverter.DoubleToInt64Bits(actual.M33));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.M14), BitConverter.DoubleToInt64Bits(actual.M14));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.M24), BitConverter.DoubleToInt64Bits(actual.M24));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.M34), BitConverter.DoubleToInt64Bits(actual.M34));
    }
}
