using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Twisted/tapered extrusion (OpenSCAD's <c>linear_extrude(twist, scale, slices)</c>):
/// tapers against exact frustum volumes (B-Rep-Native via the ruled loft — every
/// straight side sweeps a plane through the scaling centre), twists against Cavalieri
/// (every section is a rotated copy, so the volume is area x height regardless of
/// twist), and honest <c>Explain</c> verdicts for the cases with no exact B-Rep.
/// </summary>
public class TwistExtrudeTests
{
    private static Sketch Square(double size) => Sketch.Rectangle(size, size);

    // ---- pure taper: exact, B-Rep-Native --------------------------------------

    [Fact]
    public void UniformTaper_IsTheExactPyramidFrustum()
    {
        var shape = Shape.Extrude(Square(20), height: 10, twist: 0, scale: 0.5);

        var report = shape.Explain(TargetRep.Brep);
        Assert.All(report.Entries, e => Assert.Equal(NodeSupport.Native, e.Support));
        Assert.Contains("ruled loft", report.Entries[0].Detail);

        // Frustum: h/3 (A0 + A1 + sqrt(A0 A1)) = 10/3 (400 + 100 + 200).
        var mesh = shape.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(10.0 / 3 * 700, mesh.Volume(), 6);
    }

    [Fact]
    public void PerAxisTaper_IntegratesTheLinearAreaLaw()
    {
        var shape = Shape.Extrude(Square(20), height: 10, twist: 0, scale: new Vector2d(0.5, 1));

        // A(t) = 400 (1 - t/2), so V = 10 * 400 * 3/4.
        var mesh = shape.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(3000, mesh.Volume(), 6);
    }

    [Fact]
    public void Taper_StaysExactUnderRigidPlacement()
    {
        var shape = Shape.Extrude(Square(20), height: 10, twist: 0, scale: 0.5)
            .RotateZ(0.7).Translate((5, -3, 2));

        Assert.All(shape.Explain(TargetRep.Brep).Entries, e => Assert.Equal(NodeSupport.Native, e.Support));
        Assert.Equal(10.0 / 3 * 700, shape.ToMesh().Volume(), 6);
    }

    [Fact]
    public void TaperOfHoledSketch_IsBrepNative_WithTheExactHollowVolume()
    {
        // A pure taper of a holed sketch lowers as a two-section ruled loft with a hole
        // family: the hole takes the SAME two placements as the outer, so its skin is
        // the taper of the hole about the same scaling centre. With a RECT hole every
        // wall is planar and the volume identity is exact.
        var holed = Square(20).WithHole(Sketch.Rectangle(6, 4));
        var shape = Shape.Extrude(holed, height: 10, twist: 0, scale: 0.5);

        Assert.All(shape.Explain(TargetRep.Brep).Entries, e => Assert.Equal(NodeSupport.Native, e.Support));
        var brep = shape.ToBrep();
        brep.Validate();
        Assert.True(brep.SatisfiesEulerFormula(genus: 1));

        double exact = 10.0 / 3 * 700 - RuledRectVolume(6, 4, 3, 2, 10);
        var mesh = BRepTessellator.Tessellate(brep);
        Assert.True(mesh.IsClosed);
        Assert.Equal(exact, mesh.Volume(), 9);

        // The mesh route (TwistedExtrusion's section sweep) is a different construction
        // of the same solid: with straight sides both are exact, so they agree.
        Assert.Equal(exact, shape.ToMesh().Volume(), 6);
    }

    /// <summary>Rect(a0×b0) tapering linearly to rect(a1×b1) over h — the prismatoid
    /// integral of the lerped rectangle.</summary>
    private static double RuledRectVolume(double a0, double b0, double a1, double b1, double h) =>
        h * (a0 * b0 / 3 + (a0 * b1 + a1 * b0) / 6 + a1 * b1 / 3);

    // ---- twist ---------------------------------------------------------------

    [Fact]
    public void Twist_PreservesTheSectionArea_Cavalieri()
    {
        var shape = Shape.Extrude(Square(20), height: 10, twist: Math.PI / 2);

        var mesh = shape.ToMesh();
        Assert.True(mesh.IsClosed);
        // Every section is a rotated copy of the square: V = 400 * 10, up to the
        // sweep's chordal discretization (side panels cut inside the true twisted
        // surface; measured 1.45% at default quality, converging with slices — see
        // Slices_ControlTheSweepResolution).
        Assert.Equal(4000, mesh.Volume(), tolerance: 4000 * 0.02);

        // The swept corners reach radius 10*sqrt(2) somewhere in the stack.
        double maxRadius = mesh.ToIndexed().Positions.Max(p => Math.Sqrt(p.X * p.X + p.Y * p.Y));
        Assert.Equal(10 * Math.Sqrt(2), maxRadius, tolerance: 0.2);
    }

    [Fact]
    public void Twist_CarriesHolesThroughTheSweep()
    {
        var washer = Square(20).WithHole(Sketch.Circle(5));
        var shape = Shape.Extrude(washer, height: 10, twist: Math.PI / 2);

        var mesh = shape.ToMesh();
        Assert.True(mesh.IsClosed);
        // (400 - pi 25) * 10; the inscribed polygonal bore makes the mesh's hole
        // slightly smaller, i.e. the volume slightly larger.
        Assert.Equal((400 - Math.PI * 25) * 10, mesh.Volume(), tolerance: 4000 * 0.02);
    }

    [Fact]
    public void Twist_RotatesTheTopSection()
    {
        // A 20x10 rectangle twisted a quarter turn: near the top the section is
        // rotated ~90 deg, so a point that sits inside the BASE cross-section's long
        // axis is far outside the TOP one.
        var shape = Shape.Extrude(Sketch.Rectangle(20, 10), height: 10, twist: Math.PI / 2);
        var sdf = shape.ToImplicit();

        Assert.True(sdf.Evaluate((8, 0, 0.5)) < 0, "inside near the base");
        Assert.True(sdf.Evaluate((8, 0, 9.5)) > 0, "the top section has rotated away");
        Assert.True(sdf.Evaluate((0, 8, 9.5)) < 0, "and its long axis now runs along y");
    }

    [Fact]
    public void Slices_NoLongerReachTheMeshOnceTheTwistIsExact()
    {
        // `slices` sizes the direct SECTION SWEEP, and ShapeCompiler.ToMesh takes the
        // highest-fidelity route available — which, now that a twist has an exact surface,
        // is one tessellation of the B-Rep. So the sweep is not reached for a plain twisted
        // extrude at all and the two meshes are the SAME mesh, asserted as bit equality
        // rather than as a tolerance: that is the statement that one declaration now has one
        // geometry. (The sweep still serves a tree the B-Rep cannot lower — a twisted body
        // welded into a mesh, which is how the herringbone's apex is built.)
        var coarse = Shape.Extrude(Square(20), height: 10, twist: Math.PI / 2, scale: 1,
            plane: null, slices: 2).ToMesh();
        var fine = Shape.Extrude(Square(20), height: 10, twist: Math.PI / 2, scale: 1,
            plane: null, slices: 64).ToMesh();

        Assert.True(coarse.IsClosed);
        Assert.True(fine.IsClosed);
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(coarse.Volume()),
            BitConverter.DoubleToInt64Bits(fine.Volume()));
        // And the identity the exact surface carries: a rotation preserves area, so the
        // twisted prism encloses exactly the untwisted volume.
        Assert.Equal(4000, fine.Volume(), tolerance: 4000 * 0.005);
    }

    [Fact]
    public void TwistWithTaper_CombinesBothLaws()
    {
        var shape = Shape.Extrude(Square(20), height: 10, twist: Math.PI / 3, scale: 0.5);

        var mesh = shape.ToMesh();
        Assert.True(mesh.IsClosed);
        // A(t) = 400 (1 - t/2)^2 (rotation preserves area): V = 400 h * 7/12.
        Assert.Equal(400 * 10 * 7.0 / 12, mesh.Volume(), tolerance: 4000 * 0.01);
    }

    // ---- Explain honesty -----------------------------------------------------

    [Fact]
    public void Explain_ReportsTheTwistAsBrepNative()
    {
        var shape = Shape.Extrude(Square(20), height: 10, twist: Math.PI / 2);

        // A twist has an exact side surface (TwistedSurface), so it is Native rather than
        // Impossible, and the detail names the factory that builds it.
        var brep = shape.Explain(TargetRep.Brep).Entries[0];
        Assert.Equal(NodeSupport.Native, brep.Support);
        Assert.Contains("twisted", brep.Detail);
        Assert.NotNull(shape.ToBrep());

        var mesh = shape.Explain(TargetRep.Mesh).Entries[0];
        Assert.Equal(NodeSupport.Native, mesh.Support);

        var implicitEntry = shape.Explain(TargetRep.Implicit).Entries[0];
        Assert.Equal(NodeSupport.Bridged, implicitEntry.Support);
        Assert.Contains("mesh SDF", implicitEntry.Detail);
    }

    [Fact]
    public void NoOpParameters_CollapseToThePlainExtrusion()
    {
        var shape = Shape.Extrude(Square(20), height: 10, twist: 0, scale: 1);

        // The plain node: Native everywhere, including implicit (exact 2D sketch SDF).
        Assert.All(shape.Explain(TargetRep.Brep).Entries, e => Assert.Equal(NodeSupport.Native, e.Support));
        Assert.All(shape.Explain(TargetRep.Implicit).Entries, e => Assert.Equal(NodeSupport.Native, e.Support));
        Assert.Equal(4000, shape.ToMesh().Volume(), 9);
    }

    // ---- validation ----------------------------------------------------------

    [Fact]
    public void Construction_ValidatesItsArguments()
    {
        var sketch = Square(20);

        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.Extrude(sketch, height: 0, twist: 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.Extrude(sketch, 10, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.Extrude(sketch, 10, 1.0, scale: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.Extrude(sketch, 10, 1.0, scale: -0.5));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Shape.Extrude(sketch, 10, 1.0, new Vector2d(1, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Shape.Extrude(sketch, 10, 1.0, scale: 1, plane: null, slices: 0));
    }
}
