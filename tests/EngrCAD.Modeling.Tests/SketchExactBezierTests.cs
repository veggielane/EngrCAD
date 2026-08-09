using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.BRep;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Béziers now cross <see cref="Sketch.ToCurvedRegions(double)"/> UNFLATTENED, so the exact
/// 2D booleans, <c>Profile.FromCurvedRegion</c> and everything built on them stop inheriting
/// a chord error from a sketch that draws a curve.
///
/// <para>The oracle is the ARCH, whose enclosed area is the rational 0.6·w·h — see
/// <c>CurvedBezierTierTests</c> for the derivation. <see cref="Sketch.Area"/> already
/// computed it exactly from the sketch's own Green's terms, so it is an INDEPENDENT reading
/// of the same number: the two agreeing means the curve survived the trip, and the flattened
/// route is measurably short of both.</para>
/// </summary>
public class SketchExactBezierTests
{
    private const double Width = 4;
    private const double Height = 3;
    private const double ArchArea = 0.6 * Width * Height;

    /// <summary>The arch: a cubic up and over, closed by the straight chord.</summary>
    private static Sketch Arch() => Sketch.Start(0, 0)
        .BezierTo((0, Height), (Width, Height), (Width, 0))
        .LineTo((0, 0))
        .Close();

    [Fact]
    public void ASketchesOwnAreaIsTheClosedForm()
    {
        Assert.Equal(ArchArea, Arch().Area(), 12);
    }

    [Fact]
    public void ACurvedRegionKeepsTheBezierAndTheExactArea()
    {
        var region = Assert.Single(Arch().ToCurvedRegions());
        Assert.Contains(region.Outer, e => e.IsBezier);
        // The same number the sketch's own integral gives, through a completely different
        // path (segments -> Curve2d -> CurvedEdge2d -> Green's term).
        Assert.Equal(ArchArea, region.Area, 12);
    }

    [Fact]
    public void TheFlattenedRouteIsAFloorTheExactOneIsNot()
    {
        // The chord tolerance is no longer spent on a Bézier at all, so the SAME call at
        // three tolerances returns the identical exact answer where the polygonal route
        // converges toward it and never arrives.
        foreach (double tolerance in (double[])[1e-1, 1e-2, 1e-3])
        {
            Assert.Equal(ArchArea, Arch().ToCurvedRegions(tolerance).Single().Area, 12);
            double flattened = Arch().ToRegions(tolerance).Single().Area;
            Assert.True(flattened < ArchArea, "an inscribed polygon under-measures");
            Assert.True(ArchArea - flattened > 1e-9, $"flattening at {tolerance} is not exact");
        }
    }

    [Fact]
    public void ABezierSurvivesABooleanAndComesBackAsASketch()
    {
        var window = Sketch.Rectangle(40, 4).Placed((Width / 2, 0), (1, 0));
        var cut = Assert.Single(Arch().IntersectExact(window));
        Assert.Contains(cut.Outer, e => e.IsBezier);

        var back = Sketch.FromCurvedRegion(cut);
        // The sketch's own exact integral over the returned curves agrees with the region's.
        Assert.Equal(cut.Area, back.Area(), 9);
        Assert.Contains(back.ToCurves(), c => c is BezierCurve2d);
    }

    [Fact]
    public void AnUncutBezierComesBackAsOnePiece()
    {
        // The arrangement splits nothing the boolean does not need, and MergeChain fuses back
        // whatever it did have to split — a cubic's two pieces recombine through the closed
        // form for their shared parameter, verified against both control polygons.
        var window = Sketch.Rectangle(60, 60).Placed((Width / 2, 0), (1, 0));
        var result = Assert.Single(Arch().IntersectExact(window));
        Assert.Equal(ArchArea, result.Area, 9);
        Assert.Equal(2, result.Outer.Count);
        Assert.Single(result.Outer, e => e.IsBezier);
    }

    [Fact]
    public void ABooleansOutputExtrudesToTheAnalyticVolume()
    {
        // The newly exact path: a curved boolean's result becomes a Profile with its cubic
        // intact (Profile.FromCurvedRegion -> BezierCurve2d.ToCurve3d, which is an exact
        // degree-3 NURBS re-expression), so the SOLID carries the closed-form area rather
        // than a chord error baked in before any solid existed.
        var window = Sketch.Rectangle(60, 60).Placed((Width / 2, 0), (1, 0));
        var cut = Assert.Single(Arch().IntersectExact(window));
        var (outer, _) = Profile.FromCurvedRegion(cut);
        Assert.Contains(outer.Segments, s => s is NurbsCurve);

        var shape = Shape.Extrude(Sketch.FromCurvedRegion(cut), 5);
        double volume = Interop.BrepMassProperties.Compute(shape.ToBrep()).Volume;
        // The residual is the TESSELLATION's (mass properties tessellate then extrapolate),
        // not the profile's — an order finer than the flattened route's own floor, and it
        // shrinks with density where a flattened profile's does not.
        Assert.Equal(ArchArea * 5, volume, ArchArea * 5 * 1e-6);
    }

    // ---- StrokeExact ----

    [Fact]
    public void StrokeExactOfASquareOutlineIsTheCircuit()
    {
        // The same 10x10 square at width 2 with miter joins the Core tests pin: 80 as a
        // circuit, 79 through a repeated-point open stroke.
        var square = Sketch.Rectangle(10, 10);
        double area = square.StrokeExact(2, OffsetJoin.Miter).Sum(r => r.Area);
        Assert.Equal(80, area, 9);
    }

    [Fact]
    public void StrokeExactIsTheMinkowskiSumOfTheOutlineWithADisc()
    {
        // The oracle that earns its keep: stroking a simple closed loop by w is the SAME SET
        // as growing the region it bounds by w/2 and taking away the region shrunk by w/2 —
        // and Stroke and Offset reach it through different primitives, so agreement is two
        // constructions checking each other.
        var disc = Sketch.Circle(8);
        const double width = 3;
        double stroked = disc.StrokeExact(width).Sum(r => r.Area);
        double grown = disc.OffsetExact(width / 2).Sum(r => r.Area);
        double shrunk = disc.OffsetExact(-width / 2).Sum(r => r.Area);
        Assert.Equal(grown - shrunk, stroked, 1e-9);
        // ...and the closed form, which the polygonal route can only approach.
        Assert.Equal(Math.PI * (9.5 * 9.5 - 6.5 * 6.5), stroked, 9);
    }

    [Fact]
    public void StrokeExactOfAnOpenPathTakesItsCaps()
    {
        Curve2d[] path = [new Line2d((0, 0), (10, 0))];
        double round = Sketch.StrokeExact(path, 2).Sum(r => r.Area);
        double butt = Sketch.StrokeExact(path, 2, StrokeCap.Butt).Sum(r => r.Area);
        Assert.Equal(20, butt, 9);
        Assert.Equal(20 + Math.PI, round, 9);   // two exact half-discs of radius 1
    }

    [Fact]
    public void StrokeExactRefusesACurveTheTierDoesNotCarry()
    {
        Curve2d[] path = [new Ellipse2d((0, 0), 5, 3, 0, Math.PI)];
        var error = Assert.Throws<ArgumentException>(() => Sketch.StrokeExact(path, 2));
        Assert.Contains("Ellipse2d", error.Message);
        Assert.Contains("cubic", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEllipticalArcIsStillFlattenedAndSaysSo()
    {
        // The one segment kind outside the tier: a rational curve, which Bezout's bound on
        // the fan tie-break does not cover. It flattens, and the chord tolerance still moves
        // the answer — which is exactly what "exact except along them" means.
        var sketch = Sketch.Start(5, 0)
            .EllipticalArcTo((-5, 0), 5, 3, 0, largeArc: false, clockwise: false)
            .LineTo((5, 0))
            .Close();
        double coarse = sketch.ToCurvedRegions(1e-1).Single().Area;
        double fine = sketch.ToCurvedRegions(1e-4).Single().Area;
        Assert.NotEqual(coarse, fine, 6);
        Assert.Equal(Math.PI * 5 * 3 / 2, fine, 3);
    }
}
