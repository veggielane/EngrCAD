using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// A real twist drill leaves a conical bottom, and <see cref="HoleSpec.WithTipAngle"/>
/// models it exactly: the tool stays ONE axis-touching revolved sketch, with the cone as
/// the profile run from the bore radius down to the apex on the axis — the same machinery
/// a countersink's cone already uses.
/// </summary>
public class DrillTipAngleTests
{
    private static Shape Plate() => Shape.Box(new Aabb((0, 0, 0), (40, 30, 10)));
    private static SketchPlane Top() => SketchPlane.At((0, 0, 10), Vector3d.UnitX, Vector3d.UnitY);

    /// <summary>The tessellator's inscribed n-gon area — the discrete truth a bore removes.</summary>
    private static double NgonArea(int n, double r) => 0.5 * n * r * r * Math.Sin(2 * Math.PI / n);

    [Fact]
    public void TipLengthIsTheDrillPointsOwnTrigonometry()
    {
        var spec = HoleSpec.Simple(6).WithTipAngle(118);
        Assert.Equal(118, spec.TipAngleDegrees!.Value, 12);
        // Half-angle 59° from the axis, so the point drops r / tan(59°) below the shoulder.
        Assert.Equal(3 / Math.Tan(59 * Math.PI / 180), spec.TipLength, 12);
    }

    [Fact]
    public void TheDefaultStaysFlat()
    {
        // Back-compat is the whole reason the default is a flat bottom: every existing
        // design's tools must keep exactly the reach they had.
        var flat = HoleSpec.Simple(6);
        Assert.Null(flat.TipAngleDegrees);
        Assert.Equal(0, flat.TipLength);
        Assert.Null(StandardHoles.Clearance(5).TipAngleDegrees);
        Assert.Null(StandardHoles.Tapped(5).TipAngleDegrees);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(128)]
    public void ATippedBoreRemovesTheCylinderPlusTheConeExactly(int segments)
    {
        // Exact as an identity at every density, which is the bar the countersink cone
        // already meets: the tessellated tool is an n-gon prism of the FULL depth plus an
        // n-gon pyramid of the tip length — depth is measured to the shoulder, so adding a
        // point removes strictly more material and never shortens the bore.
        var spec = HoleSpec.Simple(6).WithTipAngle(StandardHoles.TwistDrillPoint);
        var solid = Plate().Drill(spec, [new Vector2d(20, 15)], 6, Top()).ToBrep();
        solid.Validate();

        var mesh = BRepTessellator.Tessellate(solid, segments, 24);
        Assert.True(mesh.IsClosed);
        double expected = 40 * 30 * 10 - NgonArea(segments, 3) * (6 + spec.TipLength / 3);
        Assert.Equal(expected, mesh.Volume(), 6);
    }

    [Fact]
    public void DepthIsMeasuredToTheShoulder()
    {
        // The drawing convention, and the observable consequence: a tipped hole and a flat
        // one of the same depth differ by exactly the cone, not by a shifted cylinder.
        const int n = 64;
        var flat = Plate().Drill(HoleSpec.Simple(6), [new Vector2d(20, 15)], 6, Top()).ToBrep();
        var tipped = Plate()
            .Drill(HoleSpec.Simple(6).WithTipAngle(135), [new Vector2d(20, 15)], 6, Top()).ToBrep();

        double difference = BRepTessellator.Tessellate(flat, n, 24).Volume()
            - BRepTessellator.Tessellate(tipped, n, 24).Volume();
        double cone = NgonArea(n, 3) * (3 / Math.Tan(67.5 * Math.PI / 180)) / 3;
        Assert.Equal(cone, difference, 9);
    }

    [Fact]
    public void ACounterboreKeepsItsRecessUnderATip()
    {
        // The tip composes with the other two recipes rather than replacing them: the
        // silhouette gains an apex run and everything above it is untouched.
        var spec = HoleSpec.Counterbore(5, 10, 3).WithTipAngle(118);
        var solid = Plate().Drill(spec, [new Vector2d(20, 15)], 7, Top()).ToBrep();
        solid.Validate();

        const int n = 64;
        var mesh = BRepTessellator.Tessellate(solid, n, 24);
        Assert.True(mesh.IsClosed);
        double removed = NgonArea(n, 5) * 3                       // the recess
            + NgonArea(n, 2.5) * 4                                // bore below it, to the shoulder
            + NgonArea(n, 2.5) * spec.TipLength / 3;              // the point
        Assert.Equal(40 * 30 * 10 - removed, mesh.Volume(), 6);
    }

    [Fact]
    public void ATipCoplanarWithTheFarFaceIsRejected()
    {
        // A flat bottom landing on a face makes the bore wall and the face coincide along
        // a circle; an APEX landing on it touches at a point. Both are degenerate boolean
        // input, and only the shoulder used to be checked.
        var spec = HoleSpec.Simple(6).WithTipAngle(118);
        double depth = 10 - spec.TipLength; // apex exactly on the plate's underside

        var error = Assert.Throws<ArgumentException>(() =>
            Plate().Drill(spec, [new Vector2d(20, 15)], depth, Top()).ToBrep());
        Assert.Contains("drill point", error.Message);
    }

    [Fact]
    public void TheCalloutNamesTheTipAngle()
    {
        Assert.Equal("⌀6 ↧10 ×118° TIP",
            HoleCallout.Text(HoleSpec.Simple(6).WithTipAngle(118), 10));
        // Flat holes read exactly as before.
        Assert.Equal("⌀6 ↧10", HoleCallout.Text(HoleSpec.Simple(6), 10));
        // A countersink keeps its own continuation after the tip note.
        Assert.Contains("⌵", HoleCallout.Text(HoleSpec.Countersink(6, 12).WithTipAngle(118), 10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(180)]
    [InlineData(-30)]
    [InlineData(200)]
    public void AnImpossiblePointAngleIsRejected(double degrees) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => HoleSpec.Simple(6).WithTipAngle(degrees));

    [Fact]
    public void TheStandardAnglesAreTheCatalogueValues()
    {
        Assert.Equal(118, StandardHoles.TwistDrillPoint);
        Assert.Equal(135, StandardHoles.SplitDrillPoint);
    }

    [Fact]
    public void ATippedToolIsStillCheckedAgainstOpposingBores()
    {
        // The point extends the tool's reach, so it can meet a bore from the far face that
        // the shoulder alone would clear. The cross-plane test reads the silhouette, so it
        // sees the cone for free.
        var bottom = SketchPlane.At((0, 0, 0), Vector3d.UnitX, -Vector3d.UnitY);
        var spec = HoleSpec.Simple(6).WithTipAngle(118); // 1.8026 mm of point
        var first = Plate().Drill(spec, [new Vector2d(20, 15)], 5, Top());

        // 5 + 1.8026 from the top and 4 from below leaves the shoulders 1 mm apart but the
        // APEX 0.8 mm inside the lower tool.
        Assert.Throws<ArgumentException>(() =>
            first.Drill(HoleSpec.Simple(6), [new Vector2d(20, -15)], 4, bottom));
    }
}
