using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// The bridge between the exact 2D curve family (<see cref="Curve2d"/>, which lives here
/// because it produces <see cref="Curve3d"/>) and the curved arrangement's own boundary
/// vocabulary (<see cref="CurvedEdge2d"/>, which lives in Core because Core cannot
/// reference this project). The mapping must be exact in both directions for the lines and
/// arcs that make up the tier, and must REFUSE — never sample — for anything else.
/// </summary>
public class CurvedEdgeBridgeTests
{
    [Fact]
    public void Line2d_MapsToAStraightEdgeAndBack()
    {
        var line = new Line2d((1, 2), (4, 6));
        Assert.True(line.TryToCurvedEdge(out var edge));
        Assert.False(edge.IsArc);
        Assert.Equal(line.Start, edge.Start);
        Assert.Equal(line.End, edge.End);

        var back = Assert.IsType<Line2d>(Curve2d.FromCurvedEdge(edge));
        Assert.Equal(line.Start, back.Start);
        Assert.Equal(line.End, back.End);
    }

    [Fact]
    public void Arc2d_MapsToAnArcEdgeAndBack_KeepingTheSignedSweep()
    {
        var arc = new Arc2d((3, -1), 5, 0.4, -1.9);
        Assert.True(arc.TryToCurvedEdge(out var edge));
        Assert.True(edge.IsArc);
        Assert.Equal(arc.Center, edge.Center);
        Assert.Equal(arc.Radius, edge.Radius);
        Assert.Equal(arc.StartAngle, edge.StartAngle);
        Assert.Equal(arc.SweepAngle, edge.SweepAngle);
        // The signed sweep IS the orientation: no flag, no reverse-and-hope.
        Assert.True(edge.SignedCurvature < 0);

        var back = Assert.IsType<Arc2d>(Curve2d.FromCurvedEdge(edge));
        Assert.Equal(arc.SweepAngle, back.SweepAngle);
        Assert.Equal(arc.PointAt(0.37), back.PointAt(0.37));
    }

    [Fact]
    public void BeziersAndNurbs_RefuseRatherThanSample()
    {
        var bezier = new BezierCurve2d((0, 0), (1, 3), (4, 3), (5, 0));
        Assert.False(bezier.TryToCurvedEdge(out _));
        var nurbs = NurbsCurve2d.Arc((0, 0), 2, 0, Math.PI / 2);
        Assert.False(nurbs.TryToCurvedEdge(out _));
    }

    [Fact]
    public void ProfileFromCurvedRegion_KeepsAWholeCircleAsOneClosedCurve()
    {
        var (outer, holes) = Profile.FromCurvedRegion(CurvedRegion2d.Disc((0, 0), 4));
        Assert.True(outer.IsSingleClosedCurve);
        Assert.IsType<Circle3d>(outer.Segments[0]);
        Assert.Empty(holes);
    }

    [Fact]
    public void ProfileFromCurvedRegion_ProducesExactArcsForAMixedChain()
    {
        var region = CurvedRegion2dBoolean
            .Intersection(CurvedRegion2d.Disc((0, 0), 10), Square(0, -20, 20, 20))
            .Single();
        var (outer, _) = Profile.FromCurvedRegion(region);
        Assert.Equal(2, outer.Segments.Count);
        // One straight chord and one trimmed circle - nothing sampled.
        Assert.Contains(outer.Segments, s => s is Line3d);
        Assert.Contains(outer.Segments, s => s.Underlying is Circle3d);
    }

    [Fact]
    public void ExtrudedCurvedProfile_HasTheAnalyticVolume()
    {
        var (outer, holes) = Profile.FromCurvedRegion(CurvedRegion2d.Disc((0, 0), 3));
        var solid = SolidFactory.Extrude(outer, (0, 0, 7), holes);
        // Face count is the cylinder's: two caps and one lateral band, so the boundary is
        // analytic rather than a prism of chords.
        Assert.Equal(3, solid.Faces.Count());
        solid.Validate();
    }

    private static CurvedRegion2d Square(double x0, double y0, double x1, double y1) =>
        new([
            CurvedEdge2d.Line((x0, y0), (x1, y0)),
            CurvedEdge2d.Line((x1, y0), (x1, y1)),
            CurvedEdge2d.Line((x1, y1), (x0, y1)),
            CurvedEdge2d.Line((x0, y1), (x0, y0)),
        ]);
}
