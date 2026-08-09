using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

public class SmoothedShapeTests
{
    private static double MaxDistanceFromOrigin(HalfEdgeMesh mesh)
    {
        double worst = 0;
        foreach (var v in mesh.Vertices)
            worst = Math.Max(worst, v.Position.Length);
        return worst;
    }

    [Fact]
    public void Smoothed_FairsTheSurfaceAndKeepsAClosedSolid()
    {
        // A cube is centred at the origin, so its corners sit at distance sqrt(75) = 8.66.
        // Laplacian fairing has no boundary to pin on a closed solid, so the whole surface
        // fairs: the corners are pulled toward their neighbours (inward), the volume drops,
        // and the result is still a valid closed manifold.
        var box = Shape.Box(10, 10, 10);
        var before = box.ToMesh();
        var after = box.Smoothed(1.0, iterations: 2).ToMesh();

        after.Validate();
        Assert.True(after.IsClosed);
        Assert.Equal(2, after.EulerCharacteristic);

        Assert.True(after.Volume() > 0, $"the solid collapsed to volume {after.Volume()}");
        Assert.True(after.Volume() < before.Volume(),
            $"fairing should shrink a convex-cornered solid: {before.Volume()} -> {after.Volume()}");
        Assert.True(MaxDistanceFromOrigin(after) < MaxDistanceFromOrigin(before),
            "the corners should be pulled inward by fairing");
    }

    [Fact]
    public void Smoothed_ExplainIsHonest()
    {
        var shape = Shape.Box(10, 10, 10).Smoothed(1.0);

        // B-Rep: Impossible, and it says why rather than producing a tessellation dressed
        // up as a solid.
        var brep = shape.Explain(TargetRep.Brep);
        Assert.False(brep.IsConvertible);
        var brepEntry = Assert.Single(brep.Entries, e => e.Node.StartsWith("Smoothed("));
        Assert.Equal(NodeSupport.Impossible, brepEntry.Support);
        Assert.Contains("triangulation", brepEntry.Detail);
        Assert.Throws<ShapeConversionException>(() => shape.ToBrep());

        // Mesh: reachable, and by the mesh route (fairing) rather than through the field.
        var mesh = shape.Explain(TargetRep.Mesh);
        Assert.True(mesh.IsConvertible);
        var meshEntry = Assert.Single(mesh.Entries, e => e.Node.StartsWith("Smoothed("));
        Assert.Equal(NodeSupport.Bridged, meshEntry.Support);
        Assert.Contains("Laplacian fairing", meshEntry.Detail);

        // Implicit: bridged, and honest that the field is the faired triangles' own.
        var field = shape.Explain(TargetRep.Implicit);
        Assert.True(field.IsConvertible);
        var fieldEntry = Assert.Single(field.Entries, e => e.Node.StartsWith("Smoothed("));
        Assert.Equal(NodeSupport.Bridged, fieldEntry.Support);
        Assert.Contains("chord error", fieldEntry.Detail);
    }

    [Fact]
    public void Smoothed_ToImplicitBridgesThroughTheFairedMesh()
    {
        // A sphere fairs inward under curvature flow, so the field is one of a smaller shape
        // than the child's own — which is exactly the honesty the Bridged label promises.
        var shape = Shape.Sphere(10).Smoothed(1.0, iterations: 2);
        var field = shape.ToImplicit();

        double centre = field.Evaluate(Vector3d.Zero);
        Assert.True(centre < 0, "the origin is inside the faired sphere");
        double faired = SurfaceNets.Polygonize(field, 48).Volume();
        double sphere = 4.0 / 3.0 * Math.PI * 1000;
        Assert.True(faired < sphere, $"faired volume {faired} against the sphere's {sphere}");
        Assert.True(faired > 0.6 * sphere, $"fairing overshot: {faired}");
    }

    [Fact]
    public void Smoothed_BakesTheTransformSoItMeansTheSameWhereverItSits()
    {
        // The step is dimensionless (lambda = step * hbar^2), so fairing is scale-free — and
        // the node bakes the accumulated transform into the child before fairing, so
        // "fair then scale" and "scale then fair" are the SAME geometry, not merely similar.
        var faidThenScaled = Shape.Box(10, 10, 10).Smoothed(1.0, iterations: 2).Scale(3).ToMesh();
        var scaledThenFaired = Shape.Box(10, 10, 10).Scale(3).Smoothed(1.0, iterations: 2).ToMesh();

        Assert.Equal(scaledThenFaired.Volume(), faidThenScaled.Volume(), 6);
    }

    [Fact]
    public void Smoothed_ShowsUpInTheConstructionTree()
    {
        var shape = Shape.Box(10, 10, 10).Smoothed(1.0);
        var part = new Part("blob", shape);
        var tree = part.ConstructionTree();

        Assert.NotNull(tree);
        Assert.StartsWith("Smoothed(", tree.Label);
        Assert.Single(tree.Children);
        Assert.Contains("Box", tree.Children[0].Label);
    }

    [Fact]
    public void Smoothed_RejectsNonsense()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.Box(1, 1, 1).Smoothed(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.Box(1, 1, 1).Smoothed(1.0, iterations: 0));
        Assert.Throws<ArgumentNullException>(() => Shape.Box(1, 1, 1).Smoothed(null!));
    }
}
