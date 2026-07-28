using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Mates ACROSS assembly levels: an occurrence path ("carrier/bolt") names a unique
/// placement, the deep occurrence's local frame becomes the solver variable, and the
/// analytic Jacobian takes the chain rule through the frame chain. Sub-assemblies no
/// mate reaches into stay rigid; a deep target inside a multiply-placed sub-assembly is
/// refused by name (its frame is one shared object).
/// </summary>
public class MateCrossLevelTests
{
    private static Frame3d At(double x, double y, double z) =>
        Frame3d.FromXY((x, y, z), Vector3d.UnitX, Vector3d.UnitY);

    private static Part BoxPart(string name, double s = 4) =>
        new(name, MeshPrimitives.Box(s, s, s));

    private static Part BoredPlate(string name, double bore = 3) =>
        new(name, Shape.Box(40, 30, 6).Drill(
            HoleSpec.Simple(bore * 2), [new(0, 0)], 8,
            SketchPlane.At((0, 0, 3), Vector3d.UnitX, Vector3d.UnitY)));

    private static Vector3d WorldOf(Assembly root, string path, in Vector3d local)
    {
        var instance = root.Flatten().Single(i => i.Path == path);
        return instance.World.TransformPoint(local);
    }

    // ---- the core: a deep occurrence moves within its sub-assembly ----

    [Fact]
    public void CrossLevelMate_MovesTheDeepOccurrenceWithinItsSubAssembly()
    {
        var carrier = new Assembly("carrier");
        var bolt = carrier.Add(BoxPart("bolt"), At(3, -2, 7));

        var rig = new Assembly("rig");
        var ground = rig.Add(BoxPart("base"));
        var sub = rig.Add(carrier, At(20, 20, 20));
        var subFrameBefore = sub.Frame;

        var result = new MateSet(rig)
            .Ground(ground)
            .Add(Mate.Coincident(
                MateGeometry.Point(ground, (5, 0, 10)),
                MateGeometry.Point(rig, "carrier/bolt", Vector3d.Zero)))
            .Solve();

        Assert.True(result.Converged, result.ToString());
        // The bolt's composed world origin hit the target...
        var world = WorldOf(rig, "rig/carrier/bolt", Vector3d.Zero);
        Assert.Equal(5, world.X, 8);
        Assert.Equal(0, world.Y, 8);
        Assert.Equal(10, world.Z, 8);
        // ...by moving the bolt WITHIN the carrier: the untouched sub-assembly
        // occurrence is rigid and did not move.
        Assert.Equal(subFrameBefore, sub.Frame);
        Assert.NotEqual(At(3, -2, 7), bolt.Frame);
        // The report names the deep occurrence by path.
        var freedom = Assert.Single(result.OccurrenceFreedoms);
        Assert.Equal("carrier/bolt", freedom.Path);
        Assert.Equal(3, freedom.ConstrainedDegreesOfFreedom);
        Assert.Equal(3, freedom.RemainingDegreesOfFreedom);
    }

    [Fact]
    public void CrossLevelMate_ReachesTwoLevelsDown()
    {
        var clamp = new Assembly("clamp");
        clamp.Add(BoxPart("bolt"), At(1, 1, 1));
        var stack = new Assembly("stack");
        stack.Add(clamp, At(5, 0, 0));

        var rig = new Assembly("rig");
        var ground = rig.Add(BoxPart("base"));
        rig.Add(stack, At(0, 0, 30));

        var result = new MateSet(rig)
            .Ground(ground)
            .Add(Mate.Coincident(
                MateGeometry.Point(ground, (2, 3, 4)),
                MateGeometry.Point(rig, "stack/clamp/bolt", Vector3d.Zero)))
            .Solve();

        Assert.True(result.Converged, result.ToString());
        var world = WorldOf(rig, "rig/stack/clamp/bolt", Vector3d.Zero);
        Assert.Equal(2, world.X, 8);
        Assert.Equal(3, world.Y, 8);
        Assert.Equal(4, world.Z, 8);
        Assert.Equal("stack/clamp/bolt", Assert.Single(result.OccurrenceFreedoms).Path);
    }

    // ---- the chain rule: a free ancestor and a free descendant solve together ----

    [Fact]
    public void CrossLevelMate_ChainsThroughAFreeAncestor()
    {
        var carrier = new Assembly("carrier");
        carrier.Add(BoxPart("bolt"), At(4, 0, 0));

        var rig = new Assembly("rig");
        var ground = rig.Add(BoxPart("base"));
        var sub = rig.Add(carrier, At(25, -10, 15));

        // Mate 1 targets the carrier occurrence itself; mate 2 targets the bolt inside
        // it — so the bolt's end has TWO Jacobian contributions (its own frame and the
        // carrier's), which is exactly the chain rule the multi-level solver adds.
        var result = new MateSet(rig)
            .Ground(ground)
            .Add(Mate.Coincident(
                MateGeometry.Point(ground, (5, 0, 0)),
                MateGeometry.Point(sub, Vector3d.Zero)))
            .Add(Mate.Coincident(
                MateGeometry.Point(ground, (5, 0, 10)),
                MateGeometry.Point(rig, "carrier/bolt", Vector3d.Zero)))
            .Solve();

        Assert.True(result.Converged, result.ToString());
        Assert.True(result.Residual <= 1e-9, $"residual {result.Residual}");
        var carrierWorld = sub.Frame.ToWorld(Vector3d.Zero);
        Assert.Equal(5, carrierWorld.X, 8);
        Assert.Equal(0, carrierWorld.Y, 8);
        Assert.Equal(0, carrierWorld.Z, 8);
        var boltWorld = WorldOf(rig, "rig/carrier/bolt", Vector3d.Zero);
        Assert.Equal(5, boltWorld.X, 8);
        Assert.Equal(0, boltWorld.Y, 8);
        Assert.Equal(10, boltWorld.Z, 8);
        Assert.Equal(2, result.OccurrenceFreedoms.Count);
        Assert.Contains(result.OccurrenceFreedoms, f => f.Path == "carrier");
        Assert.Contains(result.OccurrenceFreedoms, f => f.Path == "carrier/bolt");
    }

    // ---- selectors and typed references reach deep parts ----

    [Fact]
    public void CrossLevelConcentricAndPlanar_SeatADeepPlateOnTheBase()
    {
        var carrier = new Assembly("carrier");
        carrier.Add(BoredPlate("plate"), At(3, 4, 5));

        var rig = new Assembly("rig");
        var lower = rig.Add(BoredPlate("lower"));
        rig.Add(carrier, At(17, 9, 25));

        var result = new MateSet(rig)
            .Ground(lower)
            .Add(Mate.Concentric(
                MateGeometry.CylindricalFace(lower, FaceRef.One(FaceSetRef.Cylindrical())),
                MateGeometry.CylindricalFace(rig, "carrier/plate", FaceRef.One(FaceSetRef.Cylindrical()))))
            .Add(Mate.Planar(
                MateGeometry.PlanarFace(lower, FaceRef.Top),
                MateGeometry.PlanarFace(rig, "carrier/plate", FaceRef.Bottom)))
            .Solve();

        Assert.True(result.Converged, result.ToString());
        // The deep plate's bottom face seats on the lower plate's top (z = 3) and its
        // bore axis lands on the world Z axis, all through the carrier's frame.
        var bottom = WorldOf(rig, "rig/carrier/plate", (0, 0, -3));
        Assert.Equal(3, bottom.Z, 7);
        Assert.Equal(0, bottom.X, 7);
        Assert.Equal(0, bottom.Y, 7);
        Assert.Equal(5, result.ConstrainedDegreesOfFreedom);   // only the spin is left
    }

    // ---- honesty: shared sub-assembly internals are one frame ----

    [Fact]
    public void DeepTargetInAMultiplyPlacedSubAssembly_IsRefusedByName()
    {
        var clamp = new Assembly("clamp");
        clamp.Add(BoxPart("bolt"));

        var rig = new Assembly("rig");
        var ground = rig.Add(BoxPart("base"));
        rig.Add(clamp);
        rig.Add(clamp, At(50, 0, 0));

        var mates = new MateSet(rig)
            .Ground(ground)
            .Add(Mate.Coincident(
                MateGeometry.Point(ground, Vector3d.Zero),
                MateGeometry.Point(rig, "clamp.2/bolt", Vector3d.Zero)));

        var exception = Assert.Throws<InvalidOperationException>(() => mates.TrySolve());
        Assert.Contains("placed 2 times", exception.Message);
        Assert.Contains("rig/clamp", exception.Message);
        Assert.Contains("rig/clamp.2", exception.Message);
    }

    [Fact]
    public void GroundedDeepOccurrence_IsAnAnchorNotAVariable()
    {
        var clamp = new Assembly("clamp");
        clamp.Add(BoxPart("bolt"), At(2, 0, 0));

        var rig = new Assembly("rig");
        rig.Add(clamp, At(10, 0, 0));
        var arm = rig.Add(BoxPart("arm"), At(-30, 40, 8));

        // The bolt is grounded (by path), so the mate can only move the arm — onto the
        // bolt's COMPOSED world position.
        var result = new MateSet(rig)
            .Ground("clamp/bolt")
            .Add(Mate.Coincident(
                MateGeometry.Point(rig, "clamp/bolt", Vector3d.Zero),
                MateGeometry.Point(arm, Vector3d.Zero)))
            .Solve();

        Assert.True(result.Converged, result.ToString());
        Assert.Equal(12, arm.Frame.Origin.X, 8);   // 10 (clamp) + 2 (bolt)
        Assert.Equal("arm", Assert.Single(result.OccurrenceFreedoms).Path);
    }

    // ---- structure: paths, chains, and validation ----

    [Fact]
    public void ResolvePath_NamesTheLevelThatFails()
    {
        var clamp = new Assembly("clamp");
        clamp.Add(BoxPart("bolt"));
        var rig = new Assembly("rig");
        rig.Add(clamp);
        rig.Add(BoxPart("base"));

        var chain = rig.ResolvePath("clamp/bolt");
        Assert.Equal(2, chain.Count);
        Assert.Equal("bolt", chain[^1].Name);
        // The Flatten() spelling (leading root name) resolves too.
        Assert.Same(chain[^1], rig.FindOccurrence("rig/clamp/bolt"));

        var missing = Assert.Throws<ArgumentException>(() => rig.ResolvePath("clamp/nut"));
        Assert.Contains("no occurrence named 'nut'", missing.Message);
        Assert.Contains("bolt", missing.Message);   // says what it DOES contain
        var throughPart = Assert.Throws<ArgumentException>(() => rig.ResolvePath("base/bolt"));
        Assert.Contains("places a part", throughPart.Message);
        Assert.Throws<ArgumentException>(() => rig.ResolvePath("rig"));
    }

    [Fact]
    public void AChainFromAnotherAssembly_IsRejectedAtAdd()
    {
        var clamp = new Assembly("clamp");
        clamp.Add(BoxPart("bolt"));
        var other = new Assembly("other");
        other.Add(clamp);

        var rig = new Assembly("rig");
        var ground = rig.Add(BoxPart("base"));

        var mates = new MateSet(rig).Ground(ground);
        var foreign = Mate.Coincident(
            MateGeometry.Point(ground, Vector3d.Zero),
            MateGeometry.Point(other, "clamp/bolt", Vector3d.Zero));
        var exception = Assert.Throws<ArgumentException>(() => mates.Add(foreign));
        Assert.Contains("does not descend", exception.Message);
    }

    [Fact]
    public void CrossLevelSolving_IsScaleFree()
    {
        foreach (double scale in new[] { 0.01, 1.0, 1000.0 })
        {
            var carrier = new Assembly("carrier");
            carrier.Add(BoxPart("bolt", scale), At(3 * scale, 0, 0));
            var rig = new Assembly($"rig{scale}");
            var ground = rig.Add(BoxPart("base", scale));
            rig.Add(carrier, At(20 * scale, -7 * scale, 5 * scale));

            var result = new MateSet(rig)
                .Ground(ground)
                .Add(Mate.Coincident(
                    MateGeometry.Point(ground, (scale, 0, 0)),
                    MateGeometry.Point(rig, "carrier/bolt", Vector3d.Zero)))
                .Add(Mate.Parallel(
                    MateGeometry.Axis(ground, Vector3d.Zero, Vector3d.UnitZ),
                    MateGeometry.Axis(rig, "carrier/bolt", Vector3d.Zero, Vector3d.UnitZ)))
                .Solve();

            Assert.True(result.Converged, $"scale {scale}: {result}");
            var world = WorldOf(rig, $"rig{scale}/carrier/bolt", Vector3d.Zero);
            Assert.Equal(scale, world.X, 8);
        }
    }

    [Fact]
    public void SolvedDeepFrames_FlowThroughFlattenAndExplode()
    {
        var carrier = new Assembly("carrier");
        carrier.Add(BoxPart("bolt"), At(1, 0, 0));
        var rig = new Assembly("rig");
        var ground = rig.Add(BoxPart("base"));
        rig.Add(carrier, At(12, 0, 0));

        new MateSet(rig)
            .Ground(ground)
            .Add(Mate.Coincident(
                MateGeometry.Point(ground, (0, 0, 9)),
                MateGeometry.Point(rig, "carrier/bolt", Vector3d.Zero)))
            .Solve();

        // The solved pose is ordinary occurrence state: flattening composes it, and the
        // explode machinery still sees the same instance count and order.
        var assembled = rig.Flatten();
        var exploded = rig.Flatten(0.5);
        Assert.Equal(assembled.Count, exploded.Count);
        Assert.Equal(assembled.Select(i => i.Path), exploded.Select(i => i.Path));
        Assert.Equal(9, WorldOf(rig, "rig/carrier/bolt", Vector3d.Zero).Z, 8);
    }
}
