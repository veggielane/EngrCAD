using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>The location/workplane algebra: constructors, composition, descriptor
/// round-trips, and the one-idea contract — Drill, Pattern and component placement all
/// consuming the same value.</summary>
public class LocationSetTests
{
    // ---- constructors ----

    [Fact]
    public void Grid_IsCentredAndXFastest()
    {
        var grid = LocationSet.Grid(2, 2, 10, 8);
        Assert.Equal(4, grid.Count);
        Assert.Equal(new Vector2d(-5, -4), grid[0].Point);
        Assert.Equal(new Vector2d(5, -4), grid[1].Point);
        Assert.Equal(new Vector2d(-5, 4), grid[2].Point);
        Assert.Equal(new Vector2d(5, 4), grid[3].Point);
        Assert.All(grid, l => Assert.Equal(0, l.Angle));
    }

    [Fact]
    public void Linear_StepsFromTheOrigin()
    {
        var line = LocationSet.Linear(3, new Vector2d(10, 0));
        Assert.Equal(new Vector2d(0, 0), line[0].Point);
        Assert.Equal(new Vector2d(20, 0), line[2].Point);
    }

    [Fact]
    public void Polar_FullTurnDoesNotRepeatTheSeam()
    {
        var polar = LocationSet.Polar(4, 10);
        Assert.Equal(4, polar.Count);
        Assert.Equal(10, polar[0].Point.X, 12);
        Assert.Equal(0, polar[0].Point.Y, 12);
        Assert.Equal(0, polar[1].Point.X, 12);
        Assert.Equal(10, polar[1].Point.Y, 12);
        // Rotations follow the polar angle by default...
        Assert.Equal(Math.PI / 2, polar[1].Angle, 12);
        Assert.Equal(Math.PI, polar[2].Angle, 12);
        // ...and stay upright when asked.
        var upright = LocationSet.Polar(4, 10, rotate: false);
        Assert.All(upright, l => Assert.Equal(0, l.Angle));
    }

    [Fact]
    public void PolarArc_IncludesBothEnds()
    {
        var arc = LocationSet.PolarArc(3, 10, 0, Math.PI / 2);
        Assert.Equal(3, arc.Count);
        Assert.Equal(10, arc[0].Point.X, 12);
        Assert.Equal(0, arc[2].Point.X, 12);
        Assert.Equal(10, arc[2].Point.Y, 12);
    }

    [Fact]
    public void Hex_NeighboursSitOnePitchApart()
    {
        var hex = LocationSet.Hex(3, 2, 6);
        Assert.Equal(6, hex.Count);

        // Adjacent in a row, and nearest across rows, are both exactly one pitch apart
        // (the close-packing property).
        Assert.Equal(6, hex[1].Point.DistanceTo(hex[0].Point), 12);
        Assert.Equal(6, hex[3].Point.DistanceTo(hex[0].Point), 12);
        Assert.Equal(6, hex[3].Point.DistanceTo(hex[1].Point), 12);

        // Centred by extents.
        double minX = hex.Min(l => l.Point.X), maxX = hex.Max(l => l.Point.X);
        double minY = hex.Min(l => l.Point.Y), maxY = hex.Max(l => l.Point.Y);
        Assert.Equal(0, (minX + maxX) / 2, 12);
        Assert.Equal(0, (minY + maxY) / 2, 12);
    }

    // ---- composition ----

    [Fact]
    public void TranslateRotateConcat_Compose()
    {
        var moved = LocationSet.At(new Vector2d(1, 0)).Translate(new Vector2d(0, 2));
        Assert.Equal(new Vector2d(1, 2), moved[0].Point);

        var turned = LocationSet.At(new Vector2d(1, 0)).Rotate(Math.PI / 2);
        Assert.Equal(0, turned[0].Point.X, 12);
        Assert.Equal(1, turned[0].Point.Y, 12);
        Assert.Equal(Math.PI / 2, turned[0].Angle, 12);

        var both = LocationSet.At(new Vector2d(1, 0)) + LocationSet.At(new Vector2d(2, 0));
        Assert.Equal(2, both.Count);
        Assert.Equal(new Vector2d(2, 0), both[1].Point);
    }

    [Fact]
    public void Rotate_OfPolar_StaysOnTheCircle()
    {
        var rotated = LocationSet.Polar(6, 20).Rotate(0.3);
        Assert.All(rotated, l => Assert.Equal(20, l.Point.Length, 12));
        // Point and carried rotation advance together.
        Assert.Equal(0.3, rotated[0].Angle, 12);
        Assert.Equal(Math.Atan2(rotated[0].Point.Y, rotated[0].Point.X), rotated[0].Angle, 12);
    }

    // ---- descriptors ----

    public static TheoryData<LocationSet> Sets => new()
    {
        LocationSet.At(new Vector2d(1.5, -2), new Vector2d(0, 0.25)),
        LocationSet.Linear(5, new Vector2d(12, 0)),
        LocationSet.Grid(3, 2, 10, 8),
        LocationSet.Polar(6, 20),
        LocationSet.Polar(6, 20, Math.PI / 6, rotate: false),
        LocationSet.PolarArc(4, 15, 0.1, 1.2),
        LocationSet.Hex(3, 3, 6),
        LocationSet.Grid(2, 2, 10, 10).Translate(new Vector2d(5, 5)),
        LocationSet.Polar(4, 10).Rotate(0.25),
        LocationSet.Grid(2, 1, 8, 8) + LocationSet.Polar(3, 12),
    };

    [Theory]
    [MemberData(nameof(Sets))]
    public void Descriptor_RoundTripsBitForBit(LocationSet set)
    {
        var parsed = LocationSet.Parse(set.Descriptor);
        Assert.Equal(set.Descriptor, parsed.Descriptor);
        Assert.Equal(set.Count, parsed.Count);
        for (int i = 0; i < set.Count; i++)
        {
            // Bit equality, not tolerance: the descriptor is a cache key, so parsing
            // must reproduce the constructor's arithmetic exactly.
            Assert.Equal(set[i].Point.X, parsed[i].Point.X);
            Assert.Equal(set[i].Point.Y, parsed[i].Point.Y);
            Assert.Equal(set[i].Angle, parsed[i].Angle);
        }
    }

    [Fact]
    public void Descriptor_SpellingsAreCanonical()
    {
        Assert.Equal("grid(3,2,10,8)", LocationSet.Grid(3, 2, 10, 8).Descriptor);
        Assert.Equal("polar(6,20,0,1)", LocationSet.Polar(6, 20).Descriptor);
        Assert.Equal("hex(3,3,6)", LocationSet.Hex(3, 3, 6).Descriptor);
        Assert.Equal("translate([5,0],grid(2,2,10,10))",
            LocationSet.Grid(2, 2, 10, 10).Translate(new Vector2d(5, 0)).Descriptor);
        Assert.Equal(LocationSet.Polar(6, 20).Descriptor,
            LocationSet.Polar(6, 20).ToString());
    }

    // ---- one value, three consumers ----

    [Fact]
    public void Drill_LocationSetMatchesThePointList()
    {
        var plate = Shape.Extrude(Sketch.Circle(30), 8);
        var circle = LocationSet.Polar(4, 20);

        var byLocations = plate.Drill(StandardHoles.Clearance(5), circle, 20);
        var byPoints = plate.Drill(StandardHoles.Clearance(5), circle.Points, 20);

        double a = byLocations.ToMesh().Volume();
        double b = byPoints.ToMesh().Volume();
        Assert.Equal(b, a); // same code path — bit identical
        Assert.True(a < plate.ToMesh().Volume());
    }

    [Fact]
    public void Pattern_PolarEqualsPatternCircular()
    {
        // A shape modeled at the plane origin, stamped on a bolt circle, is EXACTLY the
        // classic circular pattern of that shape pre-translated to the radius — the
        // conjugation algebra documented on Shape.Pattern.
        var boss = Shape.Cylinder(2, 5);
        double radius = 15;

        var stamped = boss.Pattern(LocationSet.Polar(3, radius));
        var classic = boss.Transform(Matrix4d.CreateTranslation(new Vector3d(radius, 0, 0)))
            .PatternCircular(3, Vector3d.Zero, Vector3d.UnitZ);

        Assert.Equal(classic.ToMesh().Volume(), stamped.ToMesh().Volume(), 9);
    }

    [Fact]
    public void Pattern_RespectsLocationRotation()
    {
        // An off-centre box stamped with rotate:true vs rotate:false gives different
        // geometry (the copies swing about their own stamp points).
        var tab = Shape.Box(6, 2, 3).Transform(Matrix4d.CreateTranslation(new Vector3d(4, 0, 0)));
        var rotated = tab.Pattern(LocationSet.Polar(4, 20));
        var upright = tab.Pattern(LocationSet.Polar(4, 20, rotate: false));

        var boundsRotated = rotated.Bounds();
        var boundsUpright = upright.Bounds();
        // Same volume (rigid copies both ways)...
        Assert.Equal(upright.ToMesh().Volume(), rotated.ToMesh().Volume(), 6);
        // ...but different footprints: the rotated set is 4-fold symmetric, the upright
        // one keeps every tab pointing +X so its bounds are skewed positive.
        Assert.Equal(-boundsRotated.Min.X, boundsRotated.Max.X, 6);
        Assert.True(boundsUpright.Max.X > -boundsUpright.Min.X + 1);
    }

    [Fact]
    public void Place_AcceptsALocationSet()
    {
        var model = new ComponentAssembly("housing",
            Shape.Extrude(Sketch.Rectangle(60, 60), 10));
        var placement = model.Place(StandardComponents.CapScrew(4, 12),
            LocationSet.Grid(2, 2, 40, 40));
        Assert.Equal(4, placement.Points.Count);
        Assert.Contains(placement, model.Placements);
    }
}
