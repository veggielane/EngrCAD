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
    /// A shaft the same diameter as the bore it sits in: the two cylindrical faces are
    /// COINCIDENT, and deciding the shared region's rim on a curved carrier needs
    /// surface–surface re-intersection the kernel does not have. It is refused by name, before
    /// any splitting.
    /// <para>This file's fixture has moved twice, which is the honest record of two capability
    /// gains. The coplanar cross fuses now (<see cref="CoplanarCross_FusesExactly"/>), and so
    /// does a bore swallowing a rounded corner, once traced curves were snapped onto their
    /// exact boundary landing.</para>
    /// </summary>
    public static Shape CoincidentShaftInItsBore() =>
        Shape.Box(40, 30, 10).Drill(HoleSpec.Simple(10), [new Vector2d(0, 0)], 12,
            SketchPlane.At((0, 0, 5), Vector3d.UnitX, Vector3d.UnitY))
        | Shape.Cylinder(5, 30);

    [Fact]
    public void UnsupportedBoolean_ThrowsInsteadOfReturningAnOpenMesh()
    {
        var error = Assert.Throws<InvalidOperationException>(() => CoincidentShaftInItsBore().ToMesh());

        // Names the operation and the exact geometry it could not take.
        Assert.Contains("Union", error.Message);
        Assert.Contains("coincident CYLINDRICAL faces", error.Message);
        Assert.Contains("radius-5", error.Message);
        Assert.IsType<BrepBooleanException>(error.InnerException);
    }

    [Fact]
    public void UnsupportedBoolean_NamesTheRouteThatWorks()
    {
        var error = Assert.Throws<InvalidOperationException>(() => CoincidentShaftInItsBore().ToBrep());
        Assert.Contains("Shape.From(shape.ToImplicit()).ToMesh(quality)", error.Message);
    }

    [Fact]
    public void TheSuggestedImplicitRouteActuallyProducesTheSolid()
    {
        // A workaround in an error message is only honest if it works. Same geometry, via the
        // field: the shaft fills its own bore, so the answer is the undrilled plate plus the
        // 20 mm of shaft standing outside it — and Surface Nets lands 0.14 % under.
        double exact = 40 * 30 * 10 + Math.PI * 25 * 20;

        var mesh = Shape.From(CoincidentShaftInItsBore().ToImplicit()).ToMesh();

        Assert.True(mesh.IsClosed);
        Assert.InRange(mesh.Volume(), exact * 0.98, exact * 1.02);
    }

    [Fact]
    public void BoreSwallowingARoundedCorner_IsNowExact()
    {
        // A cut chain crossing a face boundary part-way — this file's previous unclosable
        // fixture. Snapping traced curves onto the exact boundary landing closed it, and it
        // converges QUADRATICALLY onto the analytic answer (1.0e-4 / 2.7e-5 / 6.8e-6 / 1.7e-6
        // relative at 32/64/128/256 segments), so the geometry is right, not merely closed.
        var breakout = Shape.Extrude(Sketch.RoundedRectangle(60, 40, 6), 10, SketchPlane.XY)
            - Shape.Cylinder(7, 20).Translate(new Vector3d(24, 14, 5));

        // The plate, less the part of the bore disc lying inside it: the disc, minus the
        // quadrant annulus outside the fillet, minus the two segments beyond the walls.
        double plate = (60.0 * 40 - (4 - Math.PI) * 36) * 10;
        double removedArea = Math.PI * 49
            - Math.PI / 4 * (49 - 36)
            - (49 * Math.Acos(6.0 / 7) - 6 * Math.Sqrt(13));
        double exact = plate - removedArea * 10;

        breakout.ToBrep().Validate();
        var mesh = breakout.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(exact, mesh.Volume(), 1e-3 * exact);
    }

    /// <summary>
    /// The other named curved refusal: two EQUAL-RADIUS perpendicular cylinders through one
    /// axis point. Their intersection is the degenerate Steinmetz — two ellipses meeting at
    /// the two points where the surfaces are TANGENT — and the tracer finds it correctly
    /// (<c>SurfaceIntersectionTests.Marching_CrossingCylinders_TwoClosedBranches</c> asserts
    /// two closed branches lying on both surfaces to 1e-8). What has no answer is the SOLID:
    /// each cut wraps its partner's band and its v-range overlaps the band's own rim rather
    /// than sitting cleanly to one side, so there is no side to send each boundary loop to.
    ///
    /// <para>Refused BY NAME in all three operations, which is the claim worth making: a
    /// tangency is a property of the PAIR, so it cannot be a union-only or a
    /// difference-only limitation, and a refusal measured in one operation would leave that
    /// open. Nudging either radius by a working clearance is the modelling answer and the
    /// implicit route is the other; both are exercised below.</para>
    /// </summary>
    [Fact]
    public void TangentBicylinder_IsRefusedByNameInEveryOperation()
    {
        var alongZ = Shape.Cylinder(10, 40);
        var alongX = Shape.Cylinder(10, 60).RotateY(Math.PI / 2);
        foreach (var pair in (Shape[])[alongZ - alongX, alongZ | alongX, alongZ & alongX,
            Shape.Box(44, 44, 30) - alongZ - alongX])
        {
            var error = Assert.ThrowsAny<Exception>(pair.ToBrep);
            Assert.Contains("wrapping cut overlaps a boundary loop's v-range", error.Message);
            Assert.Contains("tangent or mutually intersecting cuts", error.Message);
        }
    }

    [Fact]
    public void UnequalRadiiOfTheSamePair_AreOrdinaryTransversalInput()
    {
        // The complement that makes the refusal a statement about TANGENCY rather than about
        // crossing cylinders: shrink one radius and the same construction is exact. The
        // Steinmetz volume of unequal cylinders has no elementary closed form, so the oracle
        // is the inclusion-exclusion identity the boolean must satisfy whatever it does
        // internally — and it is a real check, since the intersection is the piece both
        // routes have to agree about.
        var alongZ = Shape.Cylinder(10, 40);
        var alongX = Shape.Cylinder(6, 60).RotateY(Math.PI / 2);

        double union = (alongZ | alongX).ToMesh().Volume();
        double intersection = (alongZ & alongX).ToMesh().Volume();
        double a = alongZ.ToMesh().Volume(), b = alongX.ToMesh().Volume();

        Assert.Equal(a + b - intersection, union, 2e-2 * union);
    }

    [Fact]
    public void TheTangentBicylindersImplicitRouteProducesTheSteinmetzSolid()
    {
        // A workaround named in an error message is only honest if it works — and here the
        // exact answer is the classical bicylinder volume 16 r^3 / 3, which is what makes
        // this a verification rather than a smoke test.
        var pair = Shape.Cylinder(10, 40) & Shape.Cylinder(10, 60).RotateY(Math.PI / 2);

        var mesh = Shape.From(pair.ToImplicit()).ToMesh();

        Assert.True(mesh.IsClosed);
        Assert.Equal(16 * 1000 / 3.0, mesh.Volume(), 2e-2 * (16 * 1000 / 3.0));
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
