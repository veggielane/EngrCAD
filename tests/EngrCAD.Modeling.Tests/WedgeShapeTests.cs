using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The wedge primitive (OCCT's <c>BRepPrimAPI_MakeWedge</c>). Every face is planar, so
/// unlike the cone every assertion here is EXACT: the volume is the trapezoidal
/// cross-section times the depth, with no discretization anywhere.
/// </summary>
public class WedgeShapeTests
{
    private const double X = 6, Y = 4, Z = 3;

    /// <summary>Trapezoid area times depth — exact for any wedge.</summary>
    private static double ExactVolume(double sizeX, double sizeY, double sizeZ, double topX) =>
        (sizeX + topX) / 2 * sizeZ * sizeY;

    [Fact]
    public void Wedge_NativeInAllThreeRepresentations()
    {
        var wedge = Shape.Wedge(X, Y, Z, topX: 2).RotateX(0.4).Translate(1, 2, 3);
        foreach (var target in new[] { TargetRep.Brep, TargetRep.Implicit, TargetRep.Mesh })
        {
            var report = wedge.Explain(target);
            Assert.True(report.IsConvertible);
            Assert.All(report.Entries, e => Assert.Equal(NodeSupport.Native, e.Support));
        }
    }

    [Fact]
    public void SharpWedge_HasTheExactTriangularPrismVolumeInEveryRepresentation()
    {
        var wedge = Shape.Wedge(X, Y, Z);         // topX = 0: a symmetric chisel
        double exact = ExactVolume(X, Y, Z, 0);   // = 36

        var brep = wedge.ToBrep();
        brep.Validate();
        Assert.True(brep.SatisfiesEulerFormula(genus: 0));
        var tessellated = BRepTessellator.Tessellate(brep);
        Assert.True(tessellated.IsClosed);
        Assert.Equal(exact, tessellated.Volume(), 9);

        var mesh = wedge.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(exact, mesh.Volume(), 9);

        // The sketch's exact 2D field, extruded: the sign is exact everywhere.
        var sdf = wedge.ToImplicit();
        Assert.True(sdf.Evaluate((0, 0, 0)) < 0, "the centre is inside");
        Assert.True(sdf.Evaluate((0, 0, Z / 2 + 0.01)) > 0, "just above the top edge is outside");
        // Just inside the base corner: the slant runs from (X/2, -Z/2) to (0, Z/2), so at
        // z = -Z/2 + 0.01 the material reaches x = X/2 - 0.01 exactly. Stay clear of it.
        Assert.True(sdf.Evaluate((X / 2 - 0.05, 0, -Z / 2 + 0.01)) < 0, "the base corner is inside");
        Assert.True(sdf.Evaluate((X / 4, 0, Z / 2 - 0.01)) > 0, "the taper has cut this corner away");
    }

    [Fact]
    public void TruncatedWedge_HasTheExactTrapezoidalVolume()
    {
        const double topX = 2.5;
        var wedge = Shape.Wedge(X, Y, Z, topX);

        var tessellated = BRepTessellator.Tessellate(wedge.ToBrep());
        Assert.True(tessellated.IsClosed);
        Assert.Equal(ExactVolume(X, Y, Z, topX), tessellated.Volume(), 9);
        Assert.Equal(ExactVolume(X, Y, Z, topX), wedge.ToMesh().Volume(), 9);
    }

    [Fact]
    public void ARamp_IsTheTopEdgeMovedOverOneSide()
    {
        // topOffsetX = -sizeX/2 puts the sharp edge above x = -sizeX/2, so the wedge is a
        // right triangular prism with a vertical back face.
        var ramp = Shape.Wedge(X, Y, Z, topX: 0, topOffsetX: -X / 2);

        var brep = ramp.ToBrep();
        brep.Validate();
        var mesh = BRepTessellator.Tessellate(brep);
        Assert.Equal(ExactVolume(X, Y, Z, 0), mesh.Volume(), 9);

        var sdf = ramp.ToImplicit();
        // The back face is vertical at x = -X/2: just inside it is material at any height.
        Assert.True(sdf.Evaluate((-X / 2 + 0.01, 0, Z / 2 - 0.01)) < 0);
        // ...and the far side has been sloped away entirely.
        Assert.True(sdf.Evaluate((X / 2 - 0.01, 0, 0)) > 0);
    }

    [Fact]
    public void ASquareTopWedge_IsExactlyABox()
    {
        var wedge = Shape.Wedge(X, Y, Z, topX: X);

        var mesh = BRepTessellator.Tessellate(wedge.ToBrep());
        Assert.Equal(X * Y * Z, mesh.Volume(), 9);
        var bounds = mesh.ComputeBounds();
        Assert.Equal(-X / 2, bounds.Min.X, 9);
        Assert.Equal(Z / 2, bounds.Max.Z, 9);
    }

    [Fact]
    public void ASquareTopWedgeWithAnOffset_IsAShearedBox()
    {
        // The classic parallelepiped: the top slides along x without changing width, so the
        // volume is unchanged (Cavalieri) but the bounds grow.
        const double slide = 2;
        var sheared = Shape.Wedge(X, Y, Z, topX: X, topOffsetX: slide);

        var mesh = BRepTessellator.Tessellate(sheared.ToBrep());
        Assert.Equal(X * Y * Z, mesh.Volume(), 9);
        Assert.Equal(X / 2 + slide, mesh.ComputeBounds().Max.X, 9);
    }

    [Fact]
    public void WedgeIsCentredLikeEveryOtherPrimitive()
    {
        var mesh = BRepTessellator.Tessellate(Shape.Wedge(X, Y, Z, topX: 2).ToBrep());
        var bounds = mesh.ComputeBounds();

        Assert.Equal(-X / 2, bounds.Min.X, 9);
        Assert.Equal(X / 2, bounds.Max.X, 9);
        Assert.Equal(-Y / 2, bounds.Min.Y, 9);
        Assert.Equal(Y / 2, bounds.Max.Y, 9);
        Assert.Equal(-Z / 2, bounds.Min.Z, 9);
        Assert.Equal(Z / 2, bounds.Max.Z, 9);
    }

    [Fact]
    public void WedgeComposesWithTransformsAndBooleans()
    {
        // A ramp cutting a sloped relief into one edge of a 10 x 10 x 4 block. The tool
        // overshoots in y and z so the boolean never sees a coplanar pair.
        var body = Shape.Box(10, 10, 4)
            .Subtract(Shape.Wedge(4, 12, 8, topX: 0, topOffsetX: 2).Translate(-5, 0, 0));

        var brep = body.ToBrep();
        brep.Validate();
        var mesh = BRepTessellator.Tessellate(brep);
        Assert.True(mesh.IsClosed);
        // In wedge-local (x, z) the tool is {-2 <= x <= 2, -4 <= z <= 2x}. Clipped to the
        // block (local x in [0, 2], z in [-2, 2]) that is (2x + 2) tall up to x = 1 and 4
        // tall after it: area = 3 + 4 = 7, over the block's 10 units of depth.
        Assert.Equal(10 * 10 * 4 - 7 * 10, mesh.Volume(), 6);
    }

    [Fact]
    public void WedgeAppearsAsAPrimitiveInTheConstructionTree()
    {
        var tree = ConstructionTree.FromShape(Shape.Wedge(X, Y, Z, topX: 1));

        Assert.Equal(ConstructionNodeKind.Primitive, tree.Kind);
        Assert.StartsWith("Wedge(", tree.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidWedges_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.Wedge(0, Y, Z));
        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.Wedge(X, -1, Z));
        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.Wedge(X, Y, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.Wedge(X, Y, Z, topX: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.Wedge(X, Y, Z, topOffsetX: double.NaN));
    }
}
