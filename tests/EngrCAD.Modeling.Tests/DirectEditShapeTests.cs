using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// <see cref="Shape.OffsetFaces(double, FaceSetRef)"/> and friends — the direct-editing
/// vocabulary reached through the graph, where a placement has to commute with the edit.
/// </summary>
public class DirectEditShapeTests
{
    private static readonly FaceSetRef Top = FaceSetRef.PlanarWithNormal(Vector3d.UnitZ);

    private static Aabb Bounds(Shape shape)
    {
        var bounds = Aabb.Empty;
        foreach (var vertex in shape.ToBrep().Vertices)
            bounds = bounds.Union(vertex.Position);
        return bounds;
    }

    [Fact]
    public void OffsetFaces_ThroughAFaceSetRef_PushesTheNamedFace()
    {
        var bounds = Bounds(Shape.Box(20, 30, 10).OffsetFaces(4, Top));
        Assert.True(bounds.Min.AreEqual((-10, -15, -5), new Tolerance(1e-9, 1e-9)));
        Assert.True(bounds.Max.AreEqual((10, 15, 9), new Tolerance(1e-9, 1e-9)));
    }

    [Fact]
    public void OffsetFaces_ScalesItsDistanceWithAUniformPlacement()
    {
        // A distance is a LENGTH, so it rides the accumulated scale — the same rule a wall
        // thickness and a fillet radius follow. Doubling the shape must double the push.
        var edited = Shape.Box(20, 30, 10).OffsetFaces(4, Top).Scale(2);
        var bounds = Bounds(edited);
        Assert.Equal(-10, bounds.Min.Z, 9);
        Assert.Equal(18, bounds.Max.Z, 9);
    }

    [Fact]
    public void MoveFaces_UnderAMirror_KeepsTheProjectedDistance()
    {
        // The claim the Native classification rests on: the operation reduces to v.n, and a
        // reflection preserves dot products, so a mirrored move pushes by the same amount.
        // Mirroring across the x = 0 plane leaves a +Z face's projected distance alone.
        var moved = Shape.Box(20, 30, 10).MoveFaces(new Vector3d(3, -2, 4), Top);
        var mirrored = moved.Mirror(Vector3d.Zero, Vector3d.UnitX);

        var plain = Bounds(moved);
        var flipped = Bounds(mirrored);
        Assert.Equal(9, plain.Max.Z, 9);
        Assert.Equal(9, flipped.Max.Z, 9);
        Assert.Equal(-5, flipped.Min.Z, 9);
    }

    [Fact]
    public void MoveFaces_ParallelToItself_ChangesNothing()
    {
        var bounds = Bounds(Shape.Box(20, 30, 10).MoveFaces(new Vector3d(7, -3, 0), Top));
        Assert.True(bounds.Min.AreEqual((-10, -15, -5), new Tolerance(1e-9, 1e-9)));
        Assert.True(bounds.Max.AreEqual((10, 15, 5), new Tolerance(1e-9, 1e-9)));
    }

    [Fact]
    public void DeleteFaces_TakesABossOffAnImportedStyleBody()
    {
        // The graph-level version of the Interop fixture: the union has no history to edit, so
        // the boss comes off by naming its faces.
        var withBoss = Shape.Box(40, 30, 8) | Shape.Cylinder(6, 5).Translate(0, 0, 4);
        var restored = withBoss.DeleteFaces(
            FaceSetRef.Where("boss", f => f.Bounds().Max.Z > 4 + 1e-9));

        var solid = restored.ToBrep();
        solid.Validate();
        Assert.Equal(6, solid.Faces.Count());
        var bounds = Bounds(restored);
        Assert.True(bounds.Max.AreEqual((20, 15, 4), new Tolerance(1e-9, 1e-9)));
    }

    [Fact]
    public void EveryDirectEdit_ExplainsAsBRepNative()
    {
        foreach (var shape in (Shape[])
                 [
                     Shape.Box(20, 30, 10).OffsetFaces(4, Top),
                     Shape.Box(20, 30, 10).MoveFaces(new Vector3d(0, 0, 4), Top),
                 ])
        {
            var report = shape.Explain(TargetRep.Brep);
            Assert.All(report.Entries, e => Assert.NotEqual(NodeSupport.Impossible, e.Support));
            Assert.Contains(report.Entries, e => e.Node.Contains("Faces"));
        }
    }

    [Fact]
    public void ADirectEditUnderAShear_IsImpossibleByName()
    {
        var sheared = Shape.Box(20, 30, 10).OffsetFaces(4, Top).Scale(2, 1, 1);
        var report = sheared.Explain(TargetRep.Brep);
        Assert.Contains(report.Entries, e =>
            e.Support == NodeSupport.Impossible && e.Detail!.Contains("does not commute with a face edit"));
    }

    [Fact]
    public void ADirectEditWhoseTypedReferenceMatchesNothing_NamesTheINPUT()
    {
        // A FaceSetRef carries cardinality, so it refuses before the compiler's own check
        // and names the PARAMETER — which is the better message and the reason the typed
        // overloads pass the parameter's own name down.
        var shape = Shape.Box(20, 30, 10).OffsetFaces(4, FaceSetRef.Cylindrical());
        var error = Assert.Throws<GeometryInputException>(() => shape.ToBrep());
        Assert.Contains("faces:", error.Message);
        Assert.Contains("cylindrical face", error.Message);
    }

    [Fact]
    public void ADirectEditWhoseRawSelectorMatchesNothing_FailsAtLoweringByName()
    {
        // The lambda overload has no cardinality to declare, so the compiler's own gate is
        // what fires — and it must still say which node.
        var shape = Shape.Box(20, 30, 10).OffsetFaces(4, _ => []);
        var error = Assert.Throws<InvalidOperationException>(() => shape.ToBrep());
        Assert.Contains("matched nothing on the lowered solid", error.Message);
        Assert.Contains("OffsetFaces", error.Message);
    }
}
