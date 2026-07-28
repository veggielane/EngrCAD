using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>The GeometryRef spellings of the ordering/grouping selection layer
/// (<see cref="BrepSelection"/>): descriptors round-trip, resolution matches the raw
/// extension methods, and failures name the input.</summary>
public class SelectionRefTests
{
    /// <summary>A 40×30×8 plate with a 6 mm clearance bore and a 3 mm clearance bore —
    /// two distinct hole radii, so radius indexing has something to index.</summary>
    private static BrepSolid TwoHolePlate()
    {
        var top = SketchPlane.At((0, 0, 8), Vector3d.UnitX, Vector3d.UnitY);
        return Shape.Extrude(Sketch.Rectangle(40, 30), 8)
            .Drill(StandardHoles.Clearance(6), [new Vector2d(-10, 0)], 20, top)
            .Drill(StandardHoles.Clearance(3), [new Vector2d(10, 0)], 20, top)
            .ToBrep();
    }

    // ---- descriptors round-trip ----

    public static TheoryData<GeometryRef> SelectionRefs => new()
    {
        FaceSetRef.OfKind(SurfaceKind.Planar),
        FaceSetRef.OfKind(SurfaceKind.Cylindrical),
        FaceSetRef.NthByRadius(0),
        FaceSetRef.NthByRadius(-1),
        FaceSetRef.GroupAlong(FaceSetRef.All, Vector3d.UnitZ, 1),
        FaceRef.LargestByArea(FaceSetRef.OfKind(SurfaceKind.Planar)),
        FaceRef.Largest,
        EdgeSetRef.NthByRadius(0),
    };

    [Theory]
    [MemberData(nameof(SelectionRefs))]
    public void Descriptors_RoundTripThroughParse(GeometryRef reference)
    {
        Assert.True(reference.IsSerializable);
        var parsed = GeometryRef.Parse(reference.Descriptor, reference.GetType());
        Assert.Equal(reference.Descriptor, parsed.Descriptor);
    }

    [Fact]
    public void Descriptors_AreTheDocumentedSpellings()
    {
        Assert.Equal("kind(planar)", FaceSetRef.OfKind(SurfaceKind.Planar).Descriptor);
        Assert.Equal("nthByRadius(0)", FaceSetRef.NthByRadius(0).Descriptor);
        Assert.Equal("nthByRadius(-1)", EdgeSetRef.NthByRadius(-1).Descriptor);
        Assert.Equal("groupAlong(all,[0,0,1],1)",
            FaceSetRef.GroupAlong(FaceSetRef.All, Vector3d.UnitZ, 1).Descriptor);
        Assert.Equal("largest(all)", FaceRef.Largest.Descriptor);
    }

    // ---- resolution ----

    [Fact]
    public void OfKind_MatchesFilterBy()
    {
        var plate = TwoHolePlate();
        var planar = FaceSetRef.OfKind(SurfaceKind.Planar).Resolve(plate, "Faces");
        Assert.Equal(plate.Faces.FilterBy(SurfaceKind.Planar).Count(), planar.Count);

        var bores = FaceSetRef.OfKind(SurfaceKind.Cylindrical).Resolve(plate, "Faces");
        Assert.NotEmpty(bores);
        Assert.All(bores, f => Assert.True(f.IsCylindrical(out _, out _, out _)));
    }

    [Fact]
    public void NthByRadius_IndexesDistinctBoreSizes()
    {
        var plate = TwoHolePlate();

        var smallest = FaceSetRef.NthByRadius(0).Resolve(plate, "Bore");
        var largest = FaceSetRef.NthByRadius(-1).Resolve(plate, "Bore");
        Assert.True(smallest[0].IsCylindrical(out _, out _, out double smallRadius));
        Assert.True(largest[0].IsCylindrical(out _, out _, out double largeRadius));
        Assert.True(smallRadius < largeRadius);

        // ISO 273 normal clearance: M3 → Ø3.4, M6 → Ø6.6.
        Assert.Equal(1.7, smallRadius, 9);
        Assert.Equal(3.3, largeRadius, 9);
    }

    [Fact]
    public void NthByRadius_OutOfRange_NamesInputAndRadii()
    {
        var plate = TwoHolePlate();
        var exception = Assert.Throws<GeometryInputException>(
            () => FaceSetRef.NthByRadius(5).Resolve(plate, "Bore"));
        Assert.StartsWith("Bore:", exception.Message);
        Assert.Contains("distinct", exception.Message);
    }

    [Fact]
    public void GroupAlong_IndexesLevels()
    {
        var plate = TwoHolePlate();
        // Along Z the planar faces group as: bottom (z = 0), then top (z = 8).
        var planar = FaceSetRef.OfKind(SurfaceKind.Planar);
        var bottom = FaceSetRef.GroupAlong(planar, Vector3d.UnitZ, 0).Resolve(plate, "Level");
        var top = FaceSetRef.GroupAlong(planar, Vector3d.UnitZ, -1).Resolve(plate, "Level");

        Assert.True(bottom.Highest().RankAlong(Vector3d.UnitZ) <
                    top.Lowest().RankAlong(Vector3d.UnitZ));

        var exception = Assert.Throws<GeometryInputException>(
            () => FaceSetRef.GroupAlong(planar, Vector3d.UnitZ, 99).Resolve(plate, "Level"));
        Assert.StartsWith("Level:", exception.Message);
        Assert.Contains("group", exception.Message);
    }

    [Fact]
    public void Largest_PicksThePlateTopOrBottom()
    {
        var plate = TwoHolePlate();
        var face = FaceRef.Largest.Resolve(plate, "Main");
        Assert.True(face.IsPlanar(out _, out var normal));
        Assert.Equal(1, Math.Abs(normal.Z), 9);
        Assert.Same(face, plate.Faces.LargestByArea());
    }

    [Fact]
    public void EdgeNthByRadius_FindsTheBoreRims()
    {
        var plate = TwoHolePlate();
        var rims = EdgeSetRef.NthByRadius(0).Resolve(plate, "Rims");
        Assert.NotEmpty(rims);
        Assert.All(rims, e =>
        {
            Assert.True(e.IsCircular(out _, out _, out double radius));
            Assert.Equal(1.7, radius, 9); // ISO 273 normal clearance for M3 is Ø3.4
        });
    }
}
