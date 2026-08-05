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
    public void CubicBezier_CrossesVerbatimAndRoundTrips()
    {
        var bezier = new BezierCurve2d((0, 0), (1, 3), (4, 3), (5, 0));
        Assert.True(bezier.TryToCurvedEdge(out var edge));
        Assert.True(edge.IsBezier);
        var (p0, p1, p2, p3) = edge.ControlPoints;
        Assert.Equal(bezier.ControlPoints[0], p0);
        Assert.Equal(bezier.ControlPoints[1], p1);
        Assert.Equal(bezier.ControlPoints[2], p2);
        Assert.Equal(bezier.ControlPoints[3], p3);

        var back = Assert.IsType<BezierCurve2d>(Curve2d.FromCurvedEdge(edge));
        for (int i = 0; i <= 10; i++)
        {
            double t = i / 10.0;
            Assert.Equal(bezier.PointAt(t), back.PointAt(t));
        }
    }

    [Fact]
    public void QuadraticBezier_ElevatesToTheCubicItExactlyIs()
    {
        var quadratic = new BezierCurve2d((0, 0), (3, 6), (6, 0));
        Assert.True(quadratic.TryToCurvedEdge(out var edge));
        Assert.True(edge.IsBezier);
        // Degree elevation is a re-expression, not a fit: the two agree at every parameter.
        for (int i = 0; i <= 20; i++)
        {
            double t = i / 20.0;
            Assert.Equal(quadratic.PointAt(t).X, edge.PointAt(t).X, 12);
            Assert.Equal(quadratic.PointAt(t).Y, edge.PointAt(t).Y, 12);
        }
    }

    [Fact]
    public void AStraightCubic_DemotesToALine()
    {
        // Same point set as a segment, so leaving it a cubic would put two edges of
        // different KINDS on one carrier and the arrangement's dedupe could not see it.
        var straight = new BezierCurve2d((0, 0), (1, 0), (2, 0), (3, 0));
        Assert.True(straight.TryToCurvedEdge(out var edge));
        Assert.False(edge.IsBezier);
        Assert.False(edge.IsArc);
        Assert.Equal(new Vector2d(0, 0), edge.Start);
        Assert.Equal(new Vector2d(3, 0), edge.End);
    }

    [Fact]
    public void RationalNurbsAndQuarticBeziers_RefuseRatherThanSample()
    {
        // A rational curve is not a polynomial, so Hermite data does not determine it and
        // its implicit degree is outside the arrangement's Bezout argument. The one rational
        // curve the tier carries is a circle, and that arrives as an Arc2d.
        var rational = NurbsCurve2d.Arc((0, 0), 2, 0, Math.PI / 2);
        Assert.False(rational.TryToCurvedEdge(out _));
        Assert.False(rational.TryToCurvedEdges([]));

        var quartic = new BezierCurve2d([(0, 0), (1, 3), (3, 4), (5, 3), (6, 0)]);
        Assert.False(quartic.TryToCurvedEdge(out _));
    }

    [Fact]
    public void ANonRationalCubicSpline_DecomposesIntoExactBezierPieces()
    {
        // Two spans: knots 0,0,0,0, 0.5, 1,1,1,1.
        var spline = new NurbsCurve2d(
            3,
            [(0, 0), (1, 4), (3, -2), (5, 3), (7, 0)],
            null,
            [0, 0, 0, 0, 0.5, 1, 1, 1, 1]);
        var edges = new List<CurvedEdge2d>();
        Assert.True(spline.TryToCurvedEdges(edges));
        Assert.Equal(2, edges.Count);

        // The pieces reproduce the spline at every parameter: a span IS a polynomial of
        // degree 3 and a cubic is determined by its Hermite data.
        for (int i = 0; i <= 40; i++)
        {
            double t = i / 40.0;
            var expected = spline.PointAt(t);
            var actual = t <= 0.5 ? edges[0].PointAt(t / 0.5) : edges[1].PointAt((t - 0.5) / 0.5);
            Assert.Equal(expected.X, actual.X, 10);
            Assert.Equal(expected.Y, actual.Y, 10);
        }
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
