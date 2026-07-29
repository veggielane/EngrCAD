using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The selection vocabulary's second wave: construction planes derived from a resolved
/// plane (<c>Offset</c>/<c>Rotated</c>), area ranking, point and adjacency queries, and
/// radius RANGES — plus the <c>Shape</c> overloads that let a design outside a feature
/// history speak the same vocabulary.
/// <para>Every reference here is asserted three ways, because a reference has three
/// jobs: it must RESOLVE to the right geometry, its descriptor must be a round-trip
/// FIXED POINT (the descriptor is also the cache key and the serialized form), and it
/// must fail by NAME rather than by returning something plausible.</para>
/// </summary>
public class GeometryRefVocabularyTests
{
    /// <summary>A 40×30×10 block with a Ø6 and a Ø14 bore — enough distinct faces to
    /// rank, to be adjacent to, and to filter by radius.</summary>
    private static BrepSolid Block()
    {
        var top = SketchPlane.At((0, 0, 10), Vector3d.UnitX, Vector3d.UnitY);
        return (Shape.Box(40, 30, 10).Translate(0, 0, 5)
                - Shape.Cylinder(3, 20).Translate(-12, 0, 5)
                - Shape.Cylinder(7, 20).Translate(12, 0, 5))
            .ToBrep();
    }

    private static T Round<T>(T reference) where T : GeometryRef =>
        (T)GeometryRef.Parse(reference.Descriptor, typeof(T));

    private static void AssertDescriptorIsAFixedPoint<T>(T reference) where T : GeometryRef =>
        Assert.Equal(reference.Descriptor, Round(reference).Descriptor);

    // ---- construction planes ----

    [Fact]
    public void OffsetMovesAPlaneAlongItsNormalAndKeepsItsInPlaneAxes()
    {
        var solid = Block();
        var top = PlaneRef.TopPlane.Resolve(solid, "plane");
        var raised = PlaneRef.TopPlane.Offset(30).Resolve(solid, "plane");

        Assert.Equal(top.Origin + top.Normal * 30, raised.Origin);
        // Verbatim axes: a sketch coordinate on the offset plane must mean what it meant
        // on the base, or every hole on it moves.
        Assert.Equal(top.XAxis, raised.XAxis);
        Assert.Equal(top.YAxis, raised.YAxis);
        AssertDescriptorIsAFixedPoint(PlaneRef.TopPlane.Offset(30));
    }

    [Fact]
    public void OffsetRefindsItsBaseOnEveryResolution()
    {
        // The whole point of a reference: the same object resolves differently against a
        // different body, so a thickness change re-seats what is built on it.
        var thin = Shape.Box(20, 20, 4).Translate(0, 0, 2).ToBrep();
        var thick = Shape.Box(20, 20, 9).Translate(0, 0, 4.5).ToBrep();
        var reference = PlaneRef.TopPlane.Offset(5);

        Assert.Equal(9, reference.Resolve(thin, "plane").Origin.Z, 9);
        Assert.Equal(14, reference.Resolve(thick, "plane").Origin.Z, 9);
    }

    [Fact]
    public void AZeroOffsetOrRotationIsTheBaseItself()
    {
        // Exact-zero semantic test: a no-op wrapper would show up in the descriptor and
        // break the fixed point for a value that means nothing.
        Assert.Same(PlaneRef.TopPlane, PlaneRef.TopPlane.Offset(0));
        Assert.Same(PlaneRef.TopPlane, PlaneRef.TopPlane.Rotated(0, (1, 0)));
    }

    [Fact]
    public void RotatedTiltsAboutAnAxisStatedInTheBasePlanesOwnCoordinates()
    {
        var solid = Block();
        var tilted = PlaneRef.TopPlane.Rotated(30, (1, 0)).Resolve(solid, "plane");
        var top = PlaneRef.TopPlane.Resolve(solid, "plane");

        // Rotating about the base's x axis leaves x alone and tips the normal 30° over.
        Assert.Equal(top.XAxis, tilted.XAxis);
        Assert.Equal(top.Origin, tilted.Origin);
        Assert.Equal(Math.Cos(30 * Math.PI / 180), tilted.Normal.Dot(top.Normal), 9);
        AssertDescriptorIsAFixedPoint(PlaneRef.TopPlane.Rotated(30, (1, 0)));
    }

    [Fact]
    public void OffsetAndRotationCompose()
    {
        var solid = Block();
        var reference = PlaneRef.TopPlane.Offset(5).Rotated(45, (0, 1));
        var plane = reference.Resolve(solid, "plane");
        Assert.Equal(15, plane.Origin.Z, 9);
        Assert.Equal(Math.Cos(Math.PI / 4), plane.Normal.Z, 9);
        AssertDescriptorIsAFixedPoint(reference);
        Assert.Contains("offset", reference.Descriptor);
        Assert.Contains("rotated", reference.Descriptor);
    }

    [Fact]
    public void ARotationNeedsADirection() =>
        Assert.Throws<ArgumentException>(() => PlaneRef.TopPlane.Rotated(30, Vector2d.Zero));

    // ---- area ranking ----

    [Fact]
    public void LargestByAreaPicksTheBigFlatsAndSmallestPicksTheSmall()
    {
        var solid = Block();
        var planar = FaceSetRef.OfKind(SurfaceKind.Planar);

        // The 40×30 top and bottom are the two largest planar faces (1200 each, less the
        // bores); the 30×10 ends are the smallest.
        var biggest = FaceSetRef.LargestByArea(planar, 2).Resolve(solid, "faces");
        Assert.Equal(2, biggest.Count);
        Assert.All(biggest, f => Assert.True(f.Area() > 900, $"area {f.Area():g6} is not a big flat"));

        var smallest = FaceSetRef.SmallestByArea(planar, 2).Resolve(solid, "faces");
        Assert.All(smallest, f => Assert.True(f.Area() < 400, $"area {f.Area():g6} is not an end"));

        AssertDescriptorIsAFixedPoint(FaceSetRef.LargestByArea(planar, 2));
        AssertDescriptorIsAFixedPoint(FaceSetRef.SmallestByArea(planar));
    }

    [Fact]
    public void AskingForMoreFacesThanExistSaysSoRatherThanReturningFewer()
    {
        var solid = Block();
        var error = Assert.Throws<GeometryInputException>(() =>
            FaceSetRef.LargestByArea(FaceSetRef.Cylindrical(3), 5).Resolve(solid, "faces"));
        Assert.Contains("only", error.Message);
        Assert.Contains("faces:", error.Message);
    }

    // ---- point and adjacency ----

    [Fact]
    public void TouchingFindsTheFaceThroughAPointAndSkipsTheHoleOverIt()
    {
        var solid = Block();

        // A point on the top face away from either bore: exactly one face there.
        var onTop = FaceSetRef.Touching((0, 12, 10)).Resolve(solid, "faces");
        var face = Assert.Single(onTop);
        Assert.True(face.IsPlanar(out _, out var normal));
        Assert.Equal(1, normal.Z, 9);

        // The same height over the Ø14 bore is not ON any face — the trim test is what
        // makes that different from a bounds test. (Optional() to ask without the
        // at-least-one contract; the default spelling refuses an empty match by name.)
        Assert.Empty(FaceSetRef.Touching((12, 0, 10)).Optional().Resolve(solid, "faces"));

        // The bore wall itself is found by a point on it.
        var wall = FaceSetRef.Touching((12 + 7, 0, 5)).Resolve(solid, "faces");
        Assert.Contains(wall, f => f.IsCylindrical(out _, out _, out double r) && Math.Abs(r - 7) < 1e-6);

        AssertDescriptorIsAFixedPoint(FaceSetRef.Touching((0, 12, 10)));
    }

    [Fact]
    public void AdjacentToReturnsTheNeighboursWithoutTheNamedFacesThemselves()
    {
        var solid = Block();
        var top = FaceSetRef.PlanarWithNormal(Vector3d.UnitZ);
        var named = top.Resolve(solid, "faces").ToHashSet();
        var neighbours = FaceSetRef.AdjacentTo(top).Resolve(solid, "faces");

        Assert.NotEmpty(neighbours);
        Assert.All(neighbours, f => Assert.DoesNotContain(f, named));
        // The four side walls and both bores touch the top face.
        Assert.Equal(6, neighbours.Count);
        AssertDescriptorIsAFixedPoint(FaceSetRef.AdjacentTo(top));
    }

    // ---- radius ranges ----

    [Fact]
    public void ARadiusRangeFiltersWhereAnExactRadiusCannot()
    {
        var solid = Block();

        // The exact spelling is right for exactly-constructed geometry...
        Assert.Single(FaceSetRef.Cylindrical(3).Resolve(solid, "faces"));
        // ... and useless as "every bore under 5", which is what the range is for.
        var small = FaceSetRef.CylindricalBetween(0, 5).Resolve(solid, "faces");
        Assert.All(small, f => Assert.True(f.IsCylindrical(out _, out _, out double r) && r <= 5));
        Assert.Single(small);

        Assert.Equal(2, FaceSetRef.CylindricalBetween(0, 100).Resolve(solid, "faces").Count);
        AssertDescriptorIsAFixedPoint(FaceSetRef.CylindricalBetween(1.5, 9));
        Assert.Throws<ArgumentException>(() => FaceSetRef.CylindricalBetween(9, 1));
    }

    // ---- the Shape overloads ----

    [Fact]
    public void ShapeTakesTheSameVocabularyAFeatureDoesAndAgreesWithTheLambdaSpelling()
    {
        var box = Shape.Box(30, 20, 6);
        double typed = box.Fillet(2, FaceSetRef.PlanarWithNormal(Vector3d.UnitZ)).ToMesh().Volume();
        double lambda = box.Fillet(2, s => s.PlanarFacesWithNormal(Vector3d.UnitZ)).ToMesh().Volume();
        Assert.Equal(lambda, typed, 12);
    }

    [Fact]
    public void AFailedTypedSelectorNamesTheParameterAndWhatItWanted()
    {
        // No cylindrical face of radius 99 on a plain box: the message must say which
        // input asked and what it was looking for, which is the whole reason the typed
        // vocabulary exists.
        var error = Assert.Throws<GeometryInputException>(() =>
            Shape.Box(10, 10, 10).Fillet(1, FaceSetRef.Cylindrical(99)).ToBrep());
        Assert.Contains("faces:", error.Message);
        Assert.Contains("cylindrical", error.Message);
    }
}
