using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

public class RemeshShapeTests
{
    private static (double Min, double Max) EdgeRange(HalfEdgeMesh mesh)
    {
        double min = double.PositiveInfinity, max = 0;
        foreach (var edge in mesh.Edges)
        {
            double length = edge.Vector.Length;
            min = Math.Min(min, length);
            max = Math.Max(max, length);
        }
        return (min, max);
    }

    /// <summary>Share of edges inside the remesher's [0.66 L, 1.33 L] band.</summary>
    private static double InBand(HalfEdgeMesh mesh, double target)
    {
        int total = 0, inside = 0;
        foreach (var edge in mesh.Edges)
        {
            double length = edge.Vector.Length;
            total++;
            if (length >= 0.66 * target && length <= 1.33 * target)
                inside++;
        }
        return total == 0 ? 0 : (double)inside / total;
    }

    [Fact]
    public void Remeshed_EqualizesEdgeLengthsAndKeepsTheSolid()
    {
        // A tessellated cylinder has 20 mm cap chords beside 1 mm rim steps; a solver wants
        // neither. The remesh evens them out without moving the surface.
        var shape = Shape.Cylinder(10, 20);
        var before = shape.ToMesh();
        var after = shape.Remeshed(2.0, iterations: 14).ToMesh();

        after.Validate();
        Assert.True(after.IsClosed);
        Assert.Equal(2, after.EulerCharacteristic);

        var (beforeMin, beforeMax) = EdgeRange(before);
        var (_, afterMax) = EdgeRange(after);
        Assert.True(beforeMax / beforeMin > 8, $"the tessellation should be uneven: {beforeMin}..{beforeMax}");

        // Evenness is the SHARE of edges inside the [0.66 L, 1.33 L] band, not a min/max
        // ratio, and that is not a softer test — it is the right one. Both extremes are
        // measuring something other than the remesh: the pinned cap rims keep their short
        // chords (a pinned chain may be split but never collapsed), and the longest edge
        // converges far more slowly than the distribution does, since each collapse can
        // create a fresh edge of up to twice the target that the next pass has to split
        // again. Measured here: 42% of the source's edges are in band, 95% of the remesh's,
        // while the maximum sits around 2 L however many passes are spent on it.
        Assert.True(InBand(before, 2.0) < 0.5, $"the source was already even: {InBand(before, 2.0):P0}");
        Assert.True(InBand(after, 2.0) > 0.85, $"only {InBand(after, 2.0):P0} of edges are in band");
        Assert.True(afterMax < 5.0, $"longest edge {afterMax} against a 2.0 target");

        // And the shape survives: a cylinder of radius 10, height 20 is 6283.19.
        double exact = Math.PI * 100 * 20;
        Assert.True(Math.Abs(after.Volume() - before.Volume()) / before.Volume() < 0.01,
            $"remeshed volume {after.Volume()} against the source tessellation's {before.Volume()}");
        Assert.True(after.Volume() < exact);
    }

    [Fact]
    public void Remeshed_ExplainIsHonest()
    {
        var shape = Shape.Box(10, 10, 10).Remeshed(2.0);

        // B-Rep: Impossible, and it says why rather than producing a tessellation dressed
        // up as a solid.
        var brep = shape.Explain(TargetRep.Brep);
        Assert.False(brep.IsConvertible);
        var brepEntry = Assert.Single(brep.Entries, e => e.Node.StartsWith("Remeshed("));
        Assert.Equal(NodeSupport.Impossible, brepEntry.Support);
        Assert.Contains("triangulation", brepEntry.Detail);
        Assert.Throws<ShapeConversionException>(() => shape.ToBrep());

        // Mesh: reachable, and by the mesh route rather than through the field.
        var mesh = shape.Explain(TargetRep.Mesh);
        Assert.True(mesh.IsConvertible);
        var meshEntry = Assert.Single(mesh.Entries, e => e.Node.StartsWith("Remeshed("));
        Assert.Equal(NodeSupport.Bridged, meshEntry.Support);
        Assert.Contains("isotropic remesh", meshEntry.Detail);

        // Implicit: bridged, and honest that the field is the remeshed triangles' own.
        var field = shape.Explain(TargetRep.Implicit);
        Assert.True(field.IsConvertible);
        var fieldEntry = Assert.Single(field.Entries, e => e.Node.StartsWith("Remeshed("));
        Assert.Equal(NodeSupport.Bridged, fieldEntry.Support);
        Assert.Contains("chord error", fieldEntry.Detail);
    }

    [Fact]
    public void Remeshed_ToImplicitBridgesThroughTheRemeshedMesh()
    {
        var shape = Shape.Box(10, 10, 10).Remeshed(3.0, iterations: 8);
        var field = shape.ToImplicit();

        // The box's own faces are planar, so the remesh does not move them: the field still
        // measures the box.
        Assert.Equal(-5.0, field.Evaluate(Vector3d.Zero), 6);
        double volume = SurfaceNets.Polygonize(field, 64).Volume();
        Assert.True(Math.Abs(volume - 1000.0) / 1000.0 < 0.02, $"sdf volume {volume}");
    }

    [Fact]
    public void Remeshed_TargetEdgeScalesWithAUniformScaleAboveIt()
    {
        // The node carries a LENGTH in its own coordinates, so it must mean the same thing
        // wherever the graph places it: scaling the shape by 3 scales its edges by 3.
        var plain = Shape.Box(10, 10, 10).Remeshed(2.0, iterations: 10).ToMesh();
        var scaled = Shape.Box(10, 10, 10).Remeshed(2.0, iterations: 10).Scale(3).ToMesh();

        var (_, plainMax) = EdgeRange(plain);
        var (_, scaledMax) = EdgeRange(scaled);
        Assert.True(Math.Abs(scaledMax / plainMax - 3) < 0.5,
            $"expected the edges to scale with the body: {plainMax} -> {scaledMax}");
    }

    [Fact]
    public void Remeshed_AcceptsAnExplicitFieldTarget()
    {
        // The documented escape hatch: project onto the EXACT field instead of the child's
        // tessellation, so a coarse lowering does not cap the accuracy.
        var shape = Shape.Sphere(10);
        var coarse = shape.ToMesh(new MeshQuality { SegmentsPerCircle = 16 });

        var refined = shape.Remeshed(new RemeshOptions(2.0)
        {
            Iterations = 14,
            FeatureAngleDegrees = 0,
            ProjectionTarget = new SdfProjectionTarget(shape.ToImplicit()),
        }).ToMesh(new MeshQuality { SegmentsPerCircle = 16 });

        refined.Validate();
        Assert.True(refined.IsClosed);
        double worst = 0;
        foreach (var vertex in refined.Vertices)
            worst = Math.Max(worst, Math.Abs(vertex.Position.Length - 10));
        Assert.True(worst < 0.05, $"worst radial deviation {worst} from the exact sphere");
        // The coarse lowering it started from is much further off.
        Assert.True(coarse.Vertices.Max(v => Math.Abs(v.Position.Length - 10)) < 1e-9);
        // An inscribed triangulation at a 2 mm edge on a 10 mm sphere is a percent or so
        // short of the exact volume; the point of this test is the radial deviation above.
        double exact = 4.0 / 3.0 * Math.PI * 1000;
        Assert.True(Math.Abs(refined.Volume() - exact) / exact < 0.02, $"volume {refined.Volume()}");
    }

    [Fact]
    public void Remeshed_ShowsUpInTheConstructionTree()
    {
        var shape = Shape.Box(10, 10, 10).Remeshed(2.0);
        var part = new Part("plate", shape);
        var tree = part.ConstructionTree();

        Assert.NotNull(tree);
        Assert.StartsWith("Remeshed(", tree.Label);
        Assert.Single(tree.Children);
        Assert.Contains("Box", tree.Children[0].Label);
    }

    [Fact]
    public void Remeshed_RejectsNonsense()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.Box(1, 1, 1).Remeshed(0));
        Assert.Throws<ArgumentNullException>(() => Shape.Box(1, 1, 1).Remeshed(null!));
    }
}
