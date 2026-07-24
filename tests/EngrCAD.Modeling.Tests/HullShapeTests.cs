using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

public class HullShapeTests
{
    [Fact]
    public void HullOfTwoBoxes_IsTheirBoundingPrismExactly()
    {
        // Two unit cubes offset along X share Y/Z extents: the hull is the 3×1×1 box.
        var hull = Shape.Hull(Shape.Box(1, 1, 1), Shape.Box(1, 1, 1).Translate(2, 0, 0));
        var mesh = hull.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(3.0, mesh.Volume(), 12);
    }

    [Fact]
    public void HullOfTwoSpheres_ApproximatesTheCapsule()
    {
        var hull = Shape.Hull(Shape.Sphere(1), Shape.Sphere(1).Translate(0, 0, 3));
        var mesh = hull.ToMesh(new MeshQuality { SegmentsPerCircle = 64 });
        Assert.True(mesh.IsClosed);

        double capsule = 4.0 / 3.0 * Math.PI + Math.PI * 3;
        Assert.True(mesh.Volume() < capsule);
        Assert.True(mesh.Volume() > 0.95 * capsule, $"hull volume {mesh.Volume()} vs capsule {capsule}");
    }

    [Fact]
    public void Hull_TransformsCommute_ForPolyhedralOperands()
    {
        // Exact commutation holds when the operands' vertices transform exactly (boxes).
        // Curved operands only commute to tessellation accuracy: their sample layout is
        // axis-aligned regardless of the accumulated rotation.
        var hull = Shape.Hull(Shape.Box(1, 1, 1), Shape.Box(0.5, 2, 1).Translate(2, 0, 0));
        var moved = hull.RotateZ(0.6).RotateX(0.3).Translate(1, -2, 3);
        double baseVolume = hull.ToMesh().Volume();
        double movedVolume = moved.ToMesh().Volume();
        Assert.True(Math.Abs(baseVolume - movedVolume) < 1e-9,
            $"moved hull volume {movedVolume} vs {baseVolume}");
    }

    [Fact]
    public void Hull_ExplainIsHonest()
    {
        var hull = Shape.Hull(Shape.Box(1, 1, 1), Shape.Sphere(1).Translate(2, 0, 0));

        Assert.False(hull.Explain(TargetRep.Brep).IsConvertible);
        Assert.Throws<ShapeConversionException>(() => hull.ToBrep());

        var meshReport = hull.Explain(TargetRep.Mesh);
        Assert.True(meshReport.IsConvertible);
        var entry = Assert.Single(meshReport.Entries, e => e.Node.StartsWith("Hull("));
        Assert.Equal(NodeSupport.Bridged, entry.Support);
        Assert.Contains("quickhull", entry.Detail);

        var implicitReport = hull.Explain(TargetRep.Implicit);
        Assert.True(implicitReport.IsConvertible);
        Assert.Contains(implicitReport.Entries, e => e.Support == NodeSupport.Bridged);
    }

    [Fact]
    public void Hull_ToImplicitBridgesThroughTheHullMesh()
    {
        var hull = Shape.Hull(Shape.Box(1, 1, 1), Shape.Box(1, 1, 1).Translate(2, 0, 0));
        double volume = SurfaceNets.Polygonize(hull.ToImplicit(), 64).Volume();
        Assert.True(Math.Abs(volume - 3.0) / 3.0 < 0.05, $"sdf hull volume {volume}");
    }

    [Fact]
    public void Hull_EmptyOperandsThrow()
    {
        Assert.Throws<ArgumentException>(() => Shape.Hull());
    }
}
