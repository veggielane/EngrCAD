using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// <c>Shape.Resized</c> (OpenSCAD's <c>resize()</c>) and the non-uniform
/// <c>Shape.Scale</c> it rides on: measured bounds drive per-axis factors, zero
/// components keep or auto-follow, and representation support degrades exactly as
/// the existing non-uniform-transform policy says it does.
/// </summary>
public class ResizeShapeTests
{
    [Fact]
    public void Resized_HitsTheTargetBoundsExactly_ForPolyhedra()
    {
        var resized = Shape.Box(10, 20, 40).Resized((5, 5, 5));

        var bounds = resized.Bounds();
        Assert.Equal(5, bounds.Size.X, 9);
        Assert.Equal(5, bounds.Size.Y, 9);
        Assert.Equal(5, bounds.Size.Z, 9);
        Assert.Equal(125, resized.ToMesh().Volume(), 9);

        // A box under any affine map stays B-Rep-Native.
        Assert.All(resized.Explain(TargetRep.Brep).Entries, e => Assert.Equal(NodeSupport.Native, e.Support));
    }

    [Fact]
    public void Resized_ZeroComponents_KeepTheirAxis()
    {
        var resized = Shape.Box(10, 20, 40).Resized((50, 0, 0));

        var size = resized.Bounds().Size;
        Assert.Equal(50, size.X, 9);
        Assert.Equal(20, size.Y, 9);
        Assert.Equal(40, size.Z, 9);
    }

    [Fact]
    public void Resized_AutoAxes_FollowTheFirstSizedFactor()
    {
        var resized = Shape.Box(10, 20, 40).Resized((50, 0, 0), auto: true);

        var size = resized.Bounds().Size;
        Assert.Equal(50, size.X, 9);
        Assert.Equal(100, size.Y, 9);
        Assert.Equal(200, size.Z, 9);
    }

    [Fact]
    public void Resized_PerAxisAutoFlags_MixKeepAndFollow()
    {
        var resized = Shape.Box(10, 20, 40).Resized((50, 0, 0), auto: (false, true, false));

        var size = resized.Bounds().Size;
        Assert.Equal(50, size.X, 9);
        Assert.Equal(100, size.Y, 9);
        Assert.Equal(40, size.Z, 9);        // not auto: kept
    }

    [Fact]
    public void Resized_OffCenterShape_ScalesAboutTheOrigin()
    {
        // OpenSCAD semantics: resize is a scale about the ORIGIN, so an off-origin
        // shape moves proportionally.
        var resized = Shape.Box(10, 10, 10).Translate(10, 0, 0).Resized((20, 0, 0));

        var bounds = resized.Bounds();
        Assert.Equal(20, bounds.Size.X, 9);
        Assert.Equal(10, bounds.Min.X, 9);  // 5 * factor 2
        Assert.Equal(30, bounds.Max.X, 9);
    }

    [Fact]
    public void Resized_NonUniformSphere_IsBrepImpossible_NamingTheEllipsoid()
    {
        var resized = Shape.Sphere(5).Resized((10, 20, 30));

        var brep = resized.Explain(TargetRep.Brep).Entries.Single(e => e.Node.StartsWith("Sphere"));
        Assert.Equal(NodeSupport.Impossible, brep.Support);
        Assert.Contains("ellipsoid", brep.Detail);

        // The mesh route stays available and hits the measured targets exactly (the
        // factors came from the same tessellation the lowering transforms).
        var size = resized.Bounds().Size;
        Assert.Equal(10, size.X, 9);
        Assert.Equal(20, size.Y, 9);
        Assert.Equal(30, size.Z, 9);

        // And the implicit route bridges rather than lying about the metric.
        var sdf = resized.Explain(TargetRep.Implicit).Entries[^1];
        Assert.Equal(NodeSupport.Bridged, sdf.Support);
    }

    [Fact]
    public void Scale_NonUniform_TransformsVolumesExactly()
    {
        var scaled = Shape.Box(2, 2, 2).Scale(2, 3, 4);

        Assert.Equal(8 * 24, scaled.ToMesh().Volume(), 9);
        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.Box(1, 1, 1).Scale(0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.Box(1, 1, 1).Scale(1, -2, 1));
    }

    [Fact]
    public void Resized_ValidatesItsArguments()
    {
        var box = Shape.Box(10, 10, 10);

        Assert.Throws<ArgumentException>(() => box.Resized((0, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => box.Resized((-5, 0, 0)));
        Assert.Throws<ArgumentException>(() => box.Resized((0, 0, 0), auto: true));
    }

    [Fact]
    public void Bounds_MeasuresTheMeshLowering()
    {
        var bounds = Shape.Box(10, 20, 40).Translate(1, 2, 3).Bounds();

        Assert.Equal(new Vector3d(-4, -8, -17), bounds.Min);
        Assert.Equal(new Vector3d(6, 12, 23), bounds.Max);
    }
}
