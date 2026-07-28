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

    /// <summary>
    /// A bore centred on a rounded rectangle's corner arc centre, wide enough to swallow the
    /// fillet and break out through both adjacent straight walls. That is a cut chain crossing
    /// a face boundary part-way, which the v1 face splitter does not support (todo.md), so the
    /// boolean cannot close its result.
    /// <para>It replaced the coplanar cross as this file's unclosable fixture when the boolean
    /// learned to fuse coincident planar faces — see <see cref="CoplanarCross_FusesExactly"/>.</para>
    /// </summary>
    public static Shape UnclosableBreakout() =>
        Shape.Extrude(Sketch.RoundedRectangle(60, 40, 6), 10, SketchPlane.XY)
        - Shape.Cylinder(7, 20).Translate(new Vector3d(24, 14, 5));

    [Fact]
    public void UnclosableBoolean_ThrowsInsteadOfReturningAnOpenMesh()
    {
        var error = Assert.Throws<InvalidOperationException>(() => UnclosableBreakout().ToMesh());

        // Names the operation, quantifies the damage, and points at a crack.
        Assert.Contains("Difference", error.Message);
        Assert.Contains("unclosed solid", error.Message);
        Assert.Contains("edges are used by", error.Message);
        Assert.IsType<BrepBooleanException>(error.InnerException);
    }

    [Fact]
    public void UnclosableBoolean_NamesTheRouteThatWorks()
    {
        var error = Assert.Throws<InvalidOperationException>(() => UnclosableBreakout().ToBrep());
        Assert.Contains("Shape.From(shape.ToImplicit()).ToMesh(quality)", error.Message);
    }

    [Fact]
    public void TheSuggestedImplicitRouteActuallyProducesTheSolid()
    {
        // A workaround in an error message is only honest if it works. Same geometry, via the
        // field. The exact answer is the plate less the part of the bore disc lying inside it
        // — the disc, minus the quadrant annulus outside the fillet, minus the two segments
        // beyond the straight walls — and Surface Nets lands 0.44 % under it.
        double plate = (60.0 * 40 - (4 - Math.PI) * 36) * 10;
        double removedArea = Math.PI * 49
            - Math.PI / 4 * (49 - 36)
            - (49 * Math.Acos(6.0 / 7) - 6 * Math.Sqrt(13));
        double exact = plate - removedArea * 10;

        var mesh = Shape.From(UnclosableBreakout().ToImplicit()).ToMesh();

        Assert.True(mesh.IsClosed);
        Assert.InRange(mesh.Volume(), exact * 0.98, exact * 1.02);
    }

    [Fact]
    public void CoplanarCross_FusesExactly()
    {
        // Two crossing extrusions with IDENTICAL z-extents: their caps are coincident planar
        // pairs, which the boolean's coplanar tier now handles — ONE shell, exact volume. This
        // was the canonical unclosable input, which is why the fixture above had to move to a
        // genuinely unsupported configuration.
        var fused = Shape.Extrude(Rect(10, 10), 4, At(-2)) | Shape.Extrude(Rect(4, 20), 4, At(-2));

        var solid = fused.ToBrep();
        solid.Validate();
        Assert.Single(solid.Shells);
        var mesh = fused.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(100 * 4 + 80 * 4 - 40 * 4, mesh.Volume(), 9);
    }

    [Fact]
    public void TransversalVersionOfTheSameGeometryStaysExact()
    {
        // Drop the bar's height so no caps coincide: the plain transversal path, unchanged.
        var fused = Shape.Extrude(Rect(10, 10), 4, At(-2)) | Shape.Extrude(Rect(4, 20), 2, At(-1));

        fused.ToBrep().Validate();
        var mesh = fused.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(100 * 4 + 80 * 2 - 40 * 2, mesh.Volume(), 9);
    }
}
