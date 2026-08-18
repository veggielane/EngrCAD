using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// `Sketch.Offset(law)` / `OffsetExact(law)`: the variable offset stated as a function of
/// POSITION, which is the one spelling independent of flattening, winding and hole nesting.
/// </summary>
public class SketchVariableOffsetTests
{
    /// <summary>
    /// A CONSTANT law is the constant offset — asserted as an area identity rather than by
    /// eye, since the two reach the same primitives by different entry points.
    /// </summary>
    [Fact]
    public void AConstantLaw_MatchesTheConstantOffset()
    {
        var sketch = Sketch.Rectangle(30, 20);
        double variable = Area(sketch.Offset(_ => 2.0));
        double constant = Area(sketch.Offset(2.0));
        Assert.Equal(constant, variable, 9);

        double erodedVariable = Area(sketch.Offset(_ => -2.0));
        double erodedConstant = Area(sketch.Offset(-2.0));
        Assert.Equal(erodedConstant, erodedVariable, 9);
    }

    /// <summary>
    /// THE claim the positional law makes: it is sampled at the boundary vertices and
    /// interpolated linearly between them, so an AFFINE law is reproduced EXACTLY along every
    /// straight edge — and the oracle for "exactly" is that refining the sampling changes
    /// nothing. The same refinement measurably moves a QUADRATIC law, which is what stops the
    /// first assertion being a statement about nothing.
    /// </summary>
    [Fact]
    public void AnAffineLaw_IsInsensitiveToTheSamplingAQuadraticOneIsNot()
    {
        double Affine(Vector2d p) => 2.0 + p.X / 15.0;
        double Quadratic(Vector2d p) => 2.0 + p.X * p.X / 225.0;

        double affineCoarse = Area(Plate(1).Offset(Affine, 1e-4));
        double affineFine = Area(Plate(8).Offset(Affine, 1e-4));
        double quadraticCoarse = Area(Plate(1).Offset(Quadratic, 1e-4));
        double quadraticFine = Area(Plate(8).Offset(Quadratic, 1e-4));

        double affineShift = Math.Abs(affineFine - affineCoarse);
        double quadraticShift = Math.Abs(quadraticFine - quadraticCoarse);
        Assert.True(affineShift < 1e-6, $"an affine law moved by {affineShift} under refinement");
        Assert.True(quadraticShift > 100 * Math.Max(affineShift, 1e-9),
            $"the quadratic control moved only {quadraticShift} — the instrument is blind");

        // A 30 x 20 plate whose four sides carry the ramp: the sides' own contribution is the
        // trapezoid mean, and the answer sits between the two constant offsets it brackets.
        double variable = affineCoarse;
        Assert.True(Area(Plate(1).Offset(1.0)) < variable, "the variable offset is under its minimum");
        Assert.True(variable < Area(Plate(1).Offset(3.0)), "the variable offset is over its maximum");
    }

    /// <summary>A 30 x 20 plate whose sides carry <paramref name="perSide"/> segments each, so
    /// the same law is sampled at more points without changing the geometry.</summary>
    private static Sketch Plate(int perSide)
    {
        var points = new List<Vector2d>();
        Vector2d[] corners = [(-15, -10), (15, -10), (15, 10), (-15, 10)];
        for (int c = 0; c < 4; c++)
        {
            var a = corners[c];
            var b = corners[(c + 1) % 4];
            for (int k = 0; k < perSide; k++)
                points.Add(a + (b - a) * ((double)k / perSide));
        }
        return Sketch.Polygon(points);
    }

    /// <summary>
    /// A hole's law means what the outline's means, so ONE positive law grows the plate and
    /// shrinks the bore — the property a per-vertex list could not express without the caller
    /// knowing the flattening's own vertex order.
    /// </summary>
    [Fact]
    public void APositiveLaw_ShrinksAHole()
    {
        var sketch = Sketch.Rectangle(40, 30).WithHole(Sketch.Circle((0, 0), 6));
        var offset = Assert.Single(sketch.Offset(p => 1.5 + p.X / 40.0, 1e-4));

        Assert.Single(offset.Holes);
        double holeArea = Math.Abs(Region2d.SignedArea(offset.Holes[0]));
        Assert.True(holeArea < Math.PI * 36, $"the bore did not shrink (area {holeArea})");
        Assert.True(Math.Abs(Region2d.SignedArea(offset.Outer)) > 40 * 30,
            "the outline did not grow");
    }

    /// <summary>
    /// The EXACT tier reports what it fitted: a polygonal outline comes back with a deviation
    /// of exactly zero and a larger area than the flattened route, whose joins are inscribed.
    /// </summary>
    [Fact]
    public void TheExactTier_IsExactForAPolygonalOutlineAndBeatsTheFlattenedOne()
    {
        var sketch = Sketch.Rectangle(30, 20);
        double Law(Vector2d p) => 2.0 + p.X / 15.0;

        var exact = sketch.OffsetExact(Law);
        Assert.Equal(0.0, exact.MaxDeviation);
        double exactArea = 0;
        foreach (var region in exact.Regions)
            exactArea += region.Area;

        double flattened = Area(sketch.Offset(Law, 1e-3));
        Assert.True(flattened < exactArea,
            $"the inscribed route ({flattened}) is not under the exact one ({exactArea})");
    }

    /// <summary>
    /// An outline carrying an ARC under a law that genuinely VARIES across that arc is
    /// fitted, and says so. The law must vary along the arc's own ends to reach the fit at
    /// all: a slot's caps both sit at one x, so an x-ramp leaves each cap's two ends equal
    /// and the tier takes its exact concentric branch — which is the honest behaviour and the
    /// reason this fixture ramps with Y.
    /// </summary>
    [Fact]
    public void TheExactTier_ReportsItsFitOnAnArc()
    {
        var sketch = Sketch.Slot(24, 10);
        Assert.Equal(0.0, sketch.OffsetExact(p => 1.5 + p.X / 20.0).MaxDeviation);

        var exact = sketch.OffsetExact(p => 1.5 + p.Y / 20.0, 1e-3);
        Assert.True(exact.MaxDeviation > 0, "a slot's caps reported an exact variable offset");
        Assert.True(exact.MaxDeviation <= 1e-3, $"the fit missed its tolerance ({exact.MaxDeviation})");
    }

    [Fact]
    public void Refusals_AreByName()
    {
        var sketch = Sketch.Rectangle(30, 20);
        // A law that disagrees with itself.
        Assert.Contains("SIGN", Assert.Throws<ArgumentException>(
            () => sketch.Offset(p => p.X)).Message);
        // A zero anywhere.
        Assert.Throws<ArgumentOutOfRangeException>(() => sketch.Offset(_ => 0.0));
        Assert.Throws<ArgumentNullException>(() => sketch.Offset((Func<Vector2d, double>)null!));
    }

    private static double Area(IReadOnlyList<Region2d> regions)
    {
        double total = 0;
        foreach (var region in regions)
        {
            total += Math.Abs(Region2d.SignedArea(region.Outer));
            foreach (var hole in region.Holes)
                total -= Math.Abs(Region2d.SignedArea(hole));
        }
        return total;
    }
}
