using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The loudness contract for B-Rep booleans. Silent wrong geometry is this project's
/// worst failure mode — an unclosed boolean tessellates into an open mesh, exports an
/// unprintable STL and only surfaces when someone happens to call
/// <c>Validate()</c> — so a boolean the exact kernel cannot close throws, and the
/// <c>Shape</c> layer adds the route that WILL work.
/// </summary>
public class BooleanFailureTests
{
    private static Sketch Rect(double width, double height) => Sketch.Polygon(
    [
        new(-width / 2, -height / 2), new(width / 2, -height / 2),
        new(width / 2, height / 2), new(-width / 2, height / 2),
    ]);

    private static SketchPlane At(double z) => SketchPlane.At((0, 0, z), Vector3d.UnitX, Vector3d.UnitY);

    /// <summary>Two crossing extrusions with IDENTICAL z-extents: their caps are coplanar
    /// pairs, which the v1 transversality contract does not admit.</summary>
    private static Shape CoplanarCross() =>
        Shape.Extrude(Rect(10, 10), 4, At(-2)) | Shape.Extrude(Rect(4, 20), 4, At(-2));

    [Fact]
    public void UnclosableBoolean_ThrowsInsteadOfReturningAnOpenMesh()
    {
        var error = Assert.Throws<InvalidOperationException>(() => CoplanarCross().ToMesh());

        // Names the operation, quantifies the damage, and points at a crack.
        Assert.Contains("Union", error.Message);
        Assert.Contains("unclosed solid", error.Message);
        Assert.Contains("edges are used by", error.Message);
        Assert.IsType<BrepBooleanException>(error.InnerException);
    }

    [Fact]
    public void UnclosableBoolean_NamesTheRouteThatWorks()
    {
        var error = Assert.Throws<InvalidOperationException>(() => CoplanarCross().ToBrep());
        Assert.Contains("Shape.From(shape.ToImplicit()).ToMesh(quality)", error.Message);
    }

    [Fact]
    public void TheSuggestedImplicitRouteActuallyProducesTheSolid()
    {
        // A workaround in an error message is only honest if it works. Same geometry,
        // via the field: closed, and within Surface Nets' discretization of the exact
        // union volume (10×10×4 + 4×20×4 − 4×10×4 = 560).
        var mesh = Shape.From(CoplanarCross().ToImplicit()).ToMesh();

        Assert.True(mesh.IsClosed);
        Assert.InRange(mesh.Volume(), 560 * 0.98, 560 * 1.02);
    }

    [Fact]
    public void TransversalVersionOfTheSameGeometryStaysExact()
    {
        // The failure really is coplanarity, not the crossing: drop the bar's height so
        // no caps coincide and the same boolean is exact.
        var fused = Shape.Extrude(Rect(10, 10), 4, At(-2)) | Shape.Extrude(Rect(4, 20), 2, At(-1));

        fused.ToBrep().Validate();
        var mesh = fused.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(100 * 4 + 80 * 2 - 40 * 2, mesh.Volume(), 9);
    }
}
