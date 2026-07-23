using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

public class SketchTests
{
    // ---- construction & measures ----

    [Fact]
    public void Builder_ClosesAutomatically_DegenerateThrows()
    {
        var triangle = Sketch.Start(0, 0).LineTo(2, 0).LineTo(0, 1).Close(); // closing line added
        Assert.True(Math.Abs(triangle.Area() - 1.0) < 1e-12);

        Assert.Throws<ArgumentException>(() => Sketch.Start(0, 0).LineTo(1, 0).Close()); // zero area
    }

    [Fact]
    public void Area_AnalyticShapes()
    {
        Assert.True(Math.Abs(Sketch.Rectangle(3, 2).Area() - 6) < 1e-12);
        Assert.True(Math.Abs(Sketch.Circle(1.5).Area() - Math.PI * 2.25) < 1e-9);

        // Rounded rectangle: w·h − (4 − π)·r².
        double expected = 4 * 3 - (4 - Math.PI) * 0.5 * 0.5;
        Assert.True(Math.Abs(Sketch.RoundedRectangle(4, 3, 0.5).Area() - expected) < 1e-9);

        // Slot: straight middle + full circle of ends.
        double slot = (5 - 1) * 1 + Math.PI * 0.5 * 0.5;
        Assert.True(Math.Abs(Sketch.Slot(5, 1).Area() - slot) < 1e-9);

        // Bézier: quadratic y = bulge parabola over a unit chord has area 2/3·bulge.
        var parabolic = Sketch.Start(0, 0).QuadraticTo(new(0.5, 2), new(1, 0)).Close();
        Assert.True(Math.Abs(parabolic.Area() - 2.0 / 3.0) < 1e-12, $"area {parabolic.Area()}");

        // Holes subtract.
        var washer = Sketch.Circle(2).WithHole(Sketch.Circle(1));
        Assert.True(Math.Abs(washer.Area() - Math.PI * 3) < 1e-9);
    }

    [Fact]
    public void Winding_IsNormalized()
    {
        // Clockwise input produces the same positive area.
        var cw = Sketch.Polygon([new(0, 0), new(0, 1), new(2, 1), new(2, 0)]);
        Assert.True(Math.Abs(cw.Area() - 2) < 1e-12);
    }

    // ---- the region SDF ----

    [Fact]
    public void SketchRegion_ExactDistances()
    {
        IPlanarRegion region = new SketchRegion(Sketch.Rectangle(4, 2).WithHole(Sketch.Circle(new(0, 0), 0.5)));

        Assert.True(Math.Abs(region.SignedDistance(new(0, 0)) - 0.5) < 1e-12);      // hole center: outside
        Assert.True(Math.Abs(region.SignedDistance(new(1.5, 0)) - (-0.5)) < 1e-12); // between hole and wall
        Assert.True(Math.Abs(region.SignedDistance(new(3, 0)) - 1) < 1e-12);        // outside right wall
        Assert.True(Math.Abs(region.SignedDistance(new(0, 3)) - 2) < 1e-12);        // outside top
        Assert.True(Math.Abs(region.SignedDistance(new(3, 2)) - Math.Sqrt(2)) < 1e-12); // outside corner

        IPlanarRegion slot = new SketchRegion(Sketch.Slot(4, 2));  // spans x ∈ [−2, 2]
        Assert.True(Math.Abs(slot.SignedDistance(new(0, 0)) - (-1)) < 1e-12);
        Assert.True(Math.Abs(slot.SignedDistance(new(2, 0)) - 0) < 1e-12);          // on the end arc
        Assert.True(Math.Abs(slot.SignedDistance(new(3, 0)) - 1) < 1e-12);          // beyond the end arc
    }

    [Fact]
    public void SketchRegion_BezierBoundaryIsAccurate()
    {
        var region = new SketchRegion(
            Sketch.Start(0, 0).LineTo(2, 0).QuadraticTo(new(1, 2), new(0, 0)).Close());
        // The parabola's apex is at (1, 1): just inside vs just outside.
        Assert.True(region.SignedDistance(new(1, 0.99)) < 0);
        Assert.True(region.SignedDistance(new(1, 1.01)) > 0);
        Assert.True(Math.Abs(region.SignedDistance(new(1, 0.5)) + 0.5) < 1e-3); // ~0.5 inside
    }

    // ---- lowerings ----

    [Fact]
    public void ExtrudedPolygonSketch_ExactBrepVolume()
    {
        var shape = Shape.Extrude(Sketch.Polygon([new(0, 0), new(3, 0), new(3, 2), new(0, 2)]), 0.5);
        var solid = shape.ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid);
        Assert.True(mesh.IsClosed);
        Assert.True(Math.Abs(mesh.Volume() - 3.0) < 1e-9);
    }

    [Fact]
    public void ExtrudedRoundedSketch_AllThreeRepresentationsAgree()
    {
        var sketch = Sketch.RoundedRectangle(4, 3, 0.5).WithHole(Sketch.Circle(new(1, 0.5), 0.6));
        var shape = Shape.Extrude(sketch, 1).Translate(0, 0, -0.5).RotateZ(0.3);
        double exact = sketch.Area() * 1;

        var mesh = shape.ToMesh(new MeshQuality { SegmentsPerCircle = 256, CurveSamples = 96 });
        Assert.True(mesh.IsClosed);
        Assert.True(Math.Abs(mesh.Volume() - exact) / exact < 0.001, $"mesh {mesh.Volume()} vs {exact}");

        var report = shape.Explain(TargetRep.Implicit);
        Assert.True(report.IsConvertible);
        Assert.All(report.Entries, e => Assert.Equal(NodeSupport.Native, e.Support));

        double sdfVolume = SurfaceNets.Polygonize(shape.ToImplicit(), 96).Volume();
        Assert.True(Math.Abs(sdfVolume - exact) / exact < 0.05, $"sdf {sdfVolume} vs {exact}");
    }

    [Fact]
    public void ExtrudedSketchSdf_IsExactDistanceField()
    {
        var sdf = Shape.Extrude(Sketch.Rectangle(2, 2), 2).ToImplicit();
        Assert.True(Math.Abs(sdf.Evaluate((0, 0, 1)) - (-1)) < 1e-12);      // center of the cube
        Assert.True(Math.Abs(sdf.Evaluate((0, 0, 3)) - 1) < 1e-12);        // above the top
        Assert.True(Math.Abs(sdf.Evaluate((2, 0, 1)) - 1) < 1e-12);        // beside a wall
        Assert.True(Math.Abs(sdf.Evaluate((2, 0, 3)) - Math.Sqrt(2)) < 1e-12); // edge diagonal
    }

    [Fact]
    public void RevolvedSketch_PappusVolume_BrepAndImplicit()
    {
        // Rounded-square tube profile centered at radius 3, revolved about Z.
        var sketch = Sketch.RoundedRectangle(1, 1, 0.2);
        var moved = Sketch.Polygon([new(2.5, -0.5), new(3.5, -0.5), new(3.5, 0.5), new(2.5, 0.5)]);
        double area = moved.Area();
        double exact = 2 * Math.PI * 3 * area; // Pappus: centroid at r = 3

        var shape = Shape.Revolve(moved);
        var solid = shape.ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid, 512, 24);
        Assert.True(mesh.IsClosed);
        Assert.True(Math.Abs(mesh.Volume() - exact) / exact < 0.001, $"brep {mesh.Volume()} vs {exact}");

        Assert.Contains(shape.Explain(TargetRep.Implicit).Entries,
            e => e.Support == NodeSupport.Native);
        double sdfVolume = SurfaceNets.Polygonize(shape.ToImplicit(), 96).Volume();
        Assert.True(Math.Abs(sdfVolume - exact) / exact < 0.05, $"sdf {sdfVolume} vs {exact}");
        _ = sketch;
    }

    [Fact]
    public void AxisTouchingRevolve_WorksInAllRepresentations()
    {
        // A vase profile touching the axis: the on-axis closing line revolves to
        // nothing and its endpoints become B-Rep poles.
        var vase = Sketch.Start(0, 0).LineTo(1, 0)
            .BezierTo(new(1.6, 0.8), new(0.3, 1.6), new(0.8, 2.4))
            .LineTo(0, 2.4)
            .Close();
        var shape = Shape.Revolve(vase);

        var solid = shape.ToBrep();
        solid.Validate();
        var brepMesh = BRepTessellator.Tessellate(solid, 128, 48);
        Assert.True(brepMesh.IsClosed);
        Assert.Equal(2, brepMesh.EulerCharacteristic);

        Assert.True(shape.Explain(TargetRep.Implicit).Entries.All(e => e.Support == NodeSupport.Native));
        double sdfVolume = SurfaceNets.Polygonize(shape.ToImplicit(), 128).Volume();
        Assert.True(Math.Abs(brepMesh.Volume() - sdfVolume) / sdfVolume < 0.03,
            $"brep {brepMesh.Volume()} vs sdf {sdfVolume}");

        // Partial axis-touching revolve is still rejected up front.
        Assert.Throws<NotSupportedException>(() => Shape.Revolve(vase, Math.PI));
    }

    [Fact]
    public void AxisTouchingRevolve_CylinderAndSphereAreExact()
    {
        // Rectangle touching the axis revolves to a cylinder: n-gon prism volume.
        var cylinder = Shape.Revolve(Sketch.Polygon([new(0, 0), new(1, 0), new(1, 2), new(0, 2)]));
        var solid = cylinder.ToBrep();
        solid.Validate();
        int n = 256;
        var mesh = BRepTessellator.Tessellate(solid, n, 24);
        Assert.True(mesh.IsClosed);
        double exact = 0.5 * n * Math.Sin(2 * Math.PI / n) * 2; // inscribed n-gon × height
        Assert.True(Math.Abs(mesh.Volume() - exact) < 1e-9, $"volume {mesh.Volume()} vs {exact}");

        // Pole-to-pole arc (half-disc) revolves to a sphere via the midpoint split.
        var ball = Shape.Revolve(Sketch.Start(0, -1).ArcThrough(new(1, 0), new(0, 1)).Close());
        var ballSolid = ball.ToBrep();
        ballSolid.Validate();
        var ballMesh = BRepTessellator.Tessellate(ballSolid, 128, 64);
        Assert.True(ballMesh.IsClosed);
        double sphere = 4.0 / 3.0 * Math.PI;
        Assert.True(Math.Abs(ballMesh.Volume() - sphere) / sphere < 0.005,
            $"volume {ballMesh.Volume()} vs {sphere}");
    }

    [Fact]
    public void ProfileBasedExtrude_StillBridged_SketchIsNative()
    {
        var profileShape = Shape.Extrude(
            BRep.Profile.FromPoints([(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)]), (0, 0, 1));
        Assert.Contains(profileShape.Explain(TargetRep.Implicit).Entries,
            e => e.Support == NodeSupport.Bridged);

        var sketchShape = Shape.Extrude(Sketch.Rectangle(1, 1), 1);
        Assert.All(sketchShape.Explain(TargetRep.Implicit).Entries,
            e => Assert.Equal(NodeSupport.Native, e.Support));
    }

    [Fact]
    public void SketchOnPlane_OrientsCorrectly()
    {
        // Extruding on the YZ plane pushes along +X (its normal).
        var shape = Shape.Extrude(Sketch.Rectangle(2, 1), 0.5, SketchPlane.YZ);
        var mesh = shape.ToMesh();
        var bounds = mesh.ComputeBounds();
        Assert.True(Math.Abs(bounds.Size.X - 0.5) < 1e-9, $"X {bounds.Size.X}");
        Assert.True(Math.Abs(bounds.Size.Y - 2) < 1e-9, $"Y {bounds.Size.Y}");
        Assert.True(Math.Abs(bounds.Size.Z - 1) < 1e-9, $"Z {bounds.Size.Z}");
    }

    [Fact]
    public void ArcThrough_MatchesCircleGeometry()
    {
        // Half-disc drawn with a 3-point arc: area = πr²/2.
        var halfDisc = Sketch.Start(-1, 0).ArcThrough(new(0, 1), new(1, 0)).Close();
        Assert.True(Math.Abs(halfDisc.Area() - Math.PI / 2) < 1e-9, $"area {halfDisc.Area()}");
    }
}
