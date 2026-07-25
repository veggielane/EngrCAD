using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

public class ExplodeTests
{
    private static Frame3d At(double x, double y, double z) =>
        Frame3d.FromXY((x, y, z), Vector3d.UnitX, Vector3d.UnitY);

    private static Part BoxPart(string name, double sx = 1, double sy = 1, double sz = 1) =>
        new(name, MeshPrimitives.Box(sx, sy, sz));

    [Fact]
    public void FactorZeroIsBitForBitTheAssembledPose()
    {
        var assembly = new Assembly("rig");
        var a = assembly.Add(BoxPart("a"));
        var b = assembly.Add(BoxPart("b"), At(3, 0, 0));
        a.ExplodeOffset = (0, 0, 10);
        b.ExplodeOffset = (0, 0, -10);

        var assembled = assembly.Flatten();
        var stillAssembled = assembly.Flatten(0);

        for (int i = 0; i < assembled.Count; i++)
            Assert.Equal(assembled[i].World, stillAssembled[i].World);
    }

    [Fact]
    public void ExplodeIsLinearInTheFactorAndKeepsOrientation()
    {
        var assembly = new Assembly("rig");
        var occurrence = assembly.Add(BoxPart("a"), Frame3d.FromXY((1, 2, 3), Vector3d.UnitY, -Vector3d.UnitX));
        occurrence.ExplodeOffset = (0, 0, 8);

        var half = assembly.Flatten(0.5)[0].World;
        var full = assembly.Flatten(1)[0].World;
        var none = assembly.Flatten(0)[0].World;

        Assert.Equal(3 + 4, half.TransformPoint(Vector3d.Zero).Z, 12);
        Assert.Equal(3 + 8, full.TransformPoint(Vector3d.Zero).Z, 12);
        // Only the origin moves — the rotation is untouched.
        Assert.Equal(none.TransformVector(Vector3d.UnitX), full.TransformVector(Vector3d.UnitX));
    }

    [Fact]
    public void NestedOffsetsCompose()
    {
        var inner = new Assembly("inner");
        var leaf = inner.Add(BoxPart("leaf"));
        leaf.ExplodeOffset = (1, 0, 0);

        var outer = new Assembly("outer");
        var sub = outer.Add(inner);
        sub.ExplodeOffset = (0, 0, 10);

        var exploded = outer.Flatten(1)[0].World.TransformPoint(Vector3d.Zero);

        // The sub-assembly moves as a unit AND its own occurrence moves within it.
        Assert.Equal(1, exploded.X, 12);
        Assert.Equal(10, exploded.Z, 12);
    }

    [Fact]
    public void InstanceCountAndOrderAreIndependentOfTheFactor()
    {
        var assembly = new Assembly("rig");
        var part = BoxPart("p");
        for (int i = 0; i < 4; i++)
            assembly.Add(part, At(i, 0, 0));
        assembly.AutoExplode();

        var assembled = assembly.Flatten(0);
        var exploded = assembly.Flatten(1);

        Assert.Equal(assembled.Count, exploded.Count);
        for (int i = 0; i < assembled.Count; i++)
        {
            Assert.Same(assembled[i].Part, exploded[i].Part);
            Assert.Equal(assembled[i].Path, exploded[i].Path);
        }
    }

    [Fact]
    public void TheLargestBodyIsTheDatumAndDoesNotMove()
    {
        var assembly = new Assembly("rig");
        assembly.Add(BoxPart("base", 20, 20, 4));                 // the biggest body
        assembly.Add(BoxPart("near", 1, 1, 1), At(0, 0, 5));
        assembly.Add(BoxPart("far", 1, 1, 1), At(0, 0, 20));

        assembly.AutoExplode(distance: 10);

        Assert.Null(assembly.Occurrences[0].ExplodeOffset);       // the base stays put
        var near = assembly.Occurrences[1].ExplodeOffset!.Value;
        var far = assembly.Occurrences[2].ExplodeOffset!.Value;

        Assert.Equal(10, far.Length, 9);                          // the outermost travels the spread
        Assert.True(far.Z > 0 && near.Z > 0);                     // both go the way they already sit
        Assert.True(near.Length < far.Length,                     // ...in proportion, so the order holds
            $"the nearer body moved {near.Length}, the far one {far.Length}");
    }

    [Fact]
    public void StackedPlatesExplodeAlongTheStackAndKeepTheirOrder()
    {
        var assembly = new Assembly("stack");
        for (int i = 0; i < 4; i++)
            assembly.Add(BoxPart($"plate{i}", 20, 20, 2), At(0, 0, i * 2));

        assembly.AutoExplode(distance: 30);

        // Plate 0 is the datum (first of four equal bodies); the rest climb in order.
        Assert.Null(assembly.Occurrences[0].ExplodeOffset);
        double previous = 0;
        for (int i = 1; i < 4; i++)
        {
            var offset = assembly.Occurrences[i].ExplodeOffset!.Value;
            Assert.Equal(0, offset.X, 9);
            Assert.Equal(0, offset.Y, 9);
            Assert.True(offset.Z > previous, $"plate {i} at {offset.Z} did not clear plate {i - 1}");
            previous = offset.Z;
        }
        Assert.Equal(30, previous, 9);
    }

    [Fact]
    public void AutoExplodeMovesHardwareAlongItsOwnAxis()
    {
        var top = SketchPlane.At((0, 0, 4), Vector3d.UnitX, Vector3d.UnitY);
        var build = new ComponentAssembly("plate", Shape.Box(60, 40, 8));
        build.Place(StandardComponents.CapScrew(4, 16), [new(-20, 0), new(20, 0)], top);
        var assembly = build.ToAssembly();

        assembly.AutoExplode(distance: 25);

        // Occurrence 0 is the host; 1 and 2 are the screws, seated +Z out of the plate.
        foreach (var screw in assembly.Occurrences.Skip(1))
        {
            var offset = screw.ExplodeOffset!.Value;
            Assert.Equal(25, offset.Length, 9);
            Assert.Equal(25, offset.Z, 9);       // straight up the fastener axis
        }
        // The host is the only non-catalogue body, so it is the datum and does not move.
        Assert.Null(assembly.Occurrences[0].ExplodeOffset);
    }

    [Fact]
    public void AutoExplodeKeepsHandAuthoredOffsetsUnlessAskedToOverwrite()
    {
        var assembly = new Assembly("rig");
        assembly.Add(BoxPart("base", 20, 20, 20));      // the datum
        var kept = assembly.Add(BoxPart("b"), At(10, 0, 0));
        kept.ExplodeOffset = (0, 1, 0);

        assembly.AutoExplode(distance: 5);
        Assert.Equal(new Vector3d(0, 1, 0), kept.ExplodeOffset);

        assembly.AutoExplode(distance: 5, overwrite: true);
        Assert.Equal(5, kept.ExplodeOffset!.Value.Length, 9);
        Assert.True(kept.ExplodeOffset!.Value.X > 0);
    }

    [Fact]
    public void AutoExplodeDerivesADistanceFromTheAssemblySize()
    {
        var small = new Assembly("small");
        small.Add(BoxPart("a", 1, 1, 1), At(-1, 0, 0));
        small.Add(BoxPart("b", 1, 1, 1), At(1, 0, 0));
        small.AutoExplode();

        var large = new Assembly("large");
        large.Add(BoxPart("a", 10, 10, 10), At(-10, 0, 0));
        large.Add(BoxPart("b", 10, 10, 10), At(10, 0, 0));
        large.AutoExplode();

        double smallTravel = small.Occurrences[1].ExplodeOffset!.Value.Length;
        double largeTravel = large.Occurrences[1].ExplodeOffset!.Value.Length;

        Assert.True(largeTravel > smallTravel * 5,
            $"a 10x bigger assembly should explode much further ({smallTravel} vs {largeTravel})");
    }

    [Fact]
    public void ASharedSubAssemblyIsExplodedOnce()
    {
        var inner = new Assembly("inner");
        inner.Add(BoxPart("a", 3, 3, 3), At(-1, 0, 0));   // the sub-assembly's own datum
        inner.Add(BoxPart("b"), At(4, 0, 0));

        var outer = new Assembly("outer");
        outer.Add(inner);
        outer.Add(inner, At(0, 0, 10));

        outer.AutoExplode(distance: 4);

        // Both placements of `inner` are the SAME object, so its occurrences carry one
        // offset each — not two competing ones.
        Assert.Null(inner.Occurrences[0].ExplodeOffset);
        Assert.Equal(4, inner.Occurrences[1].ExplodeOffset!.Value.Length, 9);
    }

    [Fact]
    public void TabAndSceneThreadTheFactorThrough()
    {
        var scene = new Scene();
        var tab = scene.AddTab("model");
        tab.Add(BoxPart("loose"));
        var assembly = new Assembly("rig");
        var occurrence = assembly.Add(BoxPart("moved"), At(5, 0, 0));
        tab.Add(assembly);
        occurrence.ExplodeOffset = (0, 0, 7);

        var exploded = tab.Instances(1);

        Assert.Equal(Vector3d.Zero, exploded[0].World.TransformPoint(Vector3d.Zero));   // loose part
        Assert.Equal(new Vector3d(5, 0, 7), exploded[1].World.TransformPoint(Vector3d.Zero));
        Assert.Equal(2, scene.Instances(1).Count());
    }

    [Fact]
    public void NegativeDistanceIsRejected()
    {
        var assembly = new Assembly("rig");
        assembly.Add(BoxPart("a"));
        Assert.Throws<ArgumentOutOfRangeException>(() => assembly.AutoExplode(-1));
    }
}
