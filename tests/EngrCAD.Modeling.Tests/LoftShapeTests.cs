using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// <c>Shape.Loft</c> — the Shape-graph wiring of <see cref="SolidFactory.Loft"/>. The
/// kernel's own behaviour (alignment, cardinal-basis blending, prismatoid volumes) is
/// locked by <c>EngrCAD.BRep.Tests.LoftTests</c>; these tests pin the WIRING: honest
/// Explain classification, transform baking, the mesh and implicit routes, and the
/// construction-time validation.
/// </summary>
public class LoftShapeTests
{
    private const double H = 10;

    private static SketchPlane PlaneAt(double z) =>
        SketchPlane.At((0, 0, z), Vector3d.UnitX, Vector3d.UnitY);

    /// <summary>Rect(a0×b0) at z=0 lofted straight to rect(a1×b1) at z=h: the section at
    /// normalized t is the lerped rectangle, so the volume is the exact integral of a
    /// quadratic — h·(a0·b0/3 + (a0·b1 + a1·b0)/6 + a1·b1/3).</summary>
    private static double RuledRectVolume(double a0, double b0, double a1, double b1, double h) =>
        h * (a0 * b0 / 3 + (a0 * b1 + a1 * b0) / 6 + a1 * b1 / 3);

    private static Shape RectLoft(LoftStyle style = LoftStyle.Ruled) =>
        Shape.Loft([(Sketch.Rectangle(8, 6), PlaneAt(0)), (Sketch.Rectangle(4, 2), PlaneAt(H))], style);

    [Fact]
    public void Loft_TwoRectangles_HasTheExactPrismatoidVolume()
    {
        double exact = RuledRectVolume(8, 6, 4, 2, H);

        var brep = RectLoft().ToBrep();
        brep.Validate();
        Assert.True(brep.SatisfiesEulerFormula(genus: 0));
        var tessellated = BRepTessellator.Tessellate(brep);
        Assert.True(tessellated.IsClosed);
        Assert.Equal(exact, tessellated.Volume(), 9);

        var mesh = RectLoft().ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(exact, mesh.Volume(), 9);
    }

    [Fact]
    public void Loft_ExplainIsHonest_NativeBrepBridgedImplicit()
    {
        var loft = RectLoft();

        var brep = loft.Explain(TargetRep.Brep);
        Assert.True(brep.IsConvertible);
        var entry = Assert.Single(brep.Entries);
        Assert.Equal(NodeSupport.Native, entry.Support);
        Assert.StartsWith("Loft(2 sections", entry.Node, StringComparison.Ordinal);

        var implicitReport = loft.Explain(TargetRep.Implicit);
        Assert.True(implicitReport.IsConvertible);
        Assert.Equal(NodeSupport.Bridged, Assert.Single(implicitReport.Entries).Support);

        Assert.True(loft.CanConvertTo(TargetRep.Mesh));
    }

    [Fact]
    public void Loft_TransformsBakeIntoTheSections()
    {
        double exact = RuledRectVolume(8, 6, 4, 2, H);
        var moved = RectLoft().RotateX(0.4).RotateZ(0.9).Translate(3, -2, 5);

        Assert.True(moved.CanConvertTo(TargetRep.Brep));
        var mesh = BRepTessellator.Tessellate(moved.ToBrep());
        Assert.True(mesh.IsClosed);
        Assert.Equal(exact, mesh.Volume(), 9);

        // Uniform scale multiplies lengths by 2, volume by 8 — and the loft stays Native.
        var scaled = RectLoft().Scale(2);
        Assert.Equal(8 * exact, BRepTessellator.Tessellate(scaled.ToBrep()).Volume(), 8);
    }

    [Fact]
    public void Loft_Sheared_IsBrepImpossibleButStillMeshes()
    {
        var sheared = RectLoft().Transform(Matrix4d.CreateScale(new Vector3d(2, 1, 1)));

        var report = sheared.Explain(TargetRep.Brep);
        Assert.False(report.IsConvertible);
        Assert.Contains(report.Entries, e => e.Support == NodeSupport.Impossible);

        // The mesh route bakes the exact identity-placed B-Rep and transforms the
        // tessellation, so the volume scales exactly.
        var mesh = sheared.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(2 * RuledRectVolume(8, 6, 4, 2, H), mesh.Volume(), 9);
    }

    [Fact]
    public void Loft_SmoothThroughThreeSections_IsAValidClosedSolid()
    {
        var loft = Shape.Loft(
        [
            (Sketch.Rectangle(8, 6), PlaneAt(0)),
            (Sketch.Rectangle(10, 8), PlaneAt(4)),
            (Sketch.Rectangle(4, 3), PlaneAt(9)),
        ]);

        var brep = loft.ToBrep();
        brep.Validate();
        var mesh = BRepTessellator.Tessellate(brep);
        Assert.True(mesh.IsClosed);
        Assert.True(mesh.Volume() > 0);
    }

    [Fact]
    public void Loft_MixedStraightAndCurvedSections_Welds()
    {
        // Rectangle (4 lines) to slot (2 lines + 2 arcs): counts match, and the kernel's
        // sampling unification keeps the skin welded where a straight strip meets an arc.
        var loft = Shape.Loft(
            [(Sketch.Rectangle(10, 4), PlaneAt(0)), (Sketch.Slot(8, 3), PlaneAt(6))]);

        var mesh = loft.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.True(mesh.Volume() > 0);
    }

    [Fact]
    public void Loft_ProfileOverload_TakesRawProfiles()
    {
        var bottom = Profile.FromPoints([new(-4, -3, 0), new(4, -3, 0), new(4, 3, 0), new(-4, 3, 0)]);
        var top = Profile.FromPoints([new(-2, -1, H), new(2, -1, H), new(2, 1, H), new(-2, 1, H)]);

        var mesh = Shape.Loft([bottom, top], LoftStyle.Ruled).ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(RuledRectVolume(8, 6, 4, 2, H), mesh.Volume(), 9);
    }

    [Fact]
    public void Loft_ComposesWithBooleans()
    {
        double exact = RuledRectVolume(8, 6, 4, 2, H);
        const double radius = 0.6; // stays clear of the small top section's y = ±1 edges
        var drilled = RectLoft().Subtract(Shape.Cylinder(radius, 3 * H).Translate(0, 0, H / 2));

        var mesh = BRepTessellator.Tessellate(drilled.ToBrep());
        Assert.True(mesh.IsClosed);
        // A straight bore tessellates to exactly the inscribed n-gon prism.
        double ngon = 0.5 * 32 * radius * radius * Math.Sin(2 * Math.PI / 32);
        Assert.Equal(exact - ngon * H, mesh.Volume(), 6);
    }

    [Fact]
    public void Loft_AppearsInTheConstructionTree()
    {
        var tree = ConstructionTree.FromShape(RectLoft());
        Assert.Equal(ConstructionNodeKind.Operation, tree.Kind);
        Assert.StartsWith("Loft(2 sections", tree.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void Loft_InvalidSections_FailAtConstruction()
    {
        // Fewer than two sections.
        Assert.Throws<ArgumentException>(() =>
            Shape.Loft([(Sketch.Rectangle(4, 4), PlaneAt(0))]));

        // Mismatched segment counts (rectangle = 4, rounded rectangle = 8) fail at the
        // CALL, not deep inside a lowering.
        Assert.Throws<ArgumentException>(() => Shape.Loft(
            [(Sketch.Rectangle(6, 4), PlaneAt(0)), (Sketch.RoundedRectangle(6, 4, 1), PlaneAt(5))]));

        // Holes are a documented follow-up, refused by name.
        var holed = Sketch.Rectangle(8, 8).WithHole(Sketch.Circle(2));
        Assert.Throws<NotSupportedException>(() =>
            Shape.Loft([(holed, PlaneAt(0)), (holed, PlaneAt(5))]));
    }

    // ---- LoftAlong: the evolution-law loft (pipe shell with a law) ----

    [Fact]
    public void LoftAlong_StraightSpineNoLaw_IsThePrism()
    {
        var spine = new Line3d(new Vector3d(0, 0, 0), new Vector3d(0, 0, H));
        var prism = Shape.LoftAlong(Sketch.Rectangle(6, 4), spine, sectionCount: 4, style: LoftStyle.Ruled);

        var mesh = prism.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(6 * 4 * H, mesh.Volume(), 9);
    }

    [Fact]
    public void LoftAlong_LinearScaleLaw_IsTheExactFrustum()
    {
        // Two ruled stations with scale 1 → 0.5: corners interpolate linearly, so the
        // volume is the pyramidal frustum integral over the lerped rectangle.
        var spine = new Line3d(new Vector3d(0, 0, 0), new Vector3d(0, 0, H));
        var frustum = Shape.LoftAlong(
            Sketch.Rectangle(8, 6), spine, sectionCount: 2,
            scale: s => 1 - 0.5 * s, style: LoftStyle.Ruled);

        var mesh = frustum.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(RuledRectVolume(8, 6, 4, 3, H), mesh.Volume(), 9);
    }

    [Fact]
    public void LoftAlong_TwistLaw_ProducesAValidTwistedColumn()
    {
        var spine = new Line3d(new Vector3d(0, 0, 0), new Vector3d(0, 0, H));
        var column = Shape.LoftAlong(
            Sketch.Rectangle(5, 5), spine, sectionCount: 9,
            twist: s => s * Math.PI / 4);

        var brep = column.ToBrep();
        brep.Validate();
        var mesh = BRepTessellator.Tessellate(brep);
        Assert.True(mesh.IsClosed);
        // The ruled interpolation of rotated corners lies INSIDE the untwisted prism.
        Assert.True(mesh.Volume() > 0 && mesh.Volume() < 5 * 5 * H);
    }

    [Fact]
    public void LoftAlong_InvalidLaws_AreRejected()
    {
        var spine = new Line3d(new Vector3d(0, 0, 0), new Vector3d(0, 0, H));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Shape.LoftAlong(Sketch.Rectangle(4, 4), spine, sectionCount: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Shape.LoftAlong(Sketch.Rectangle(4, 4), spine, sectionCount: 3, scale: _ => 0));
    }
}
