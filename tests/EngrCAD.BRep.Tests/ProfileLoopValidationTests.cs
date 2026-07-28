using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// A profile is the boundary of a face, so it must be a simple closed curve. A bow-tie
/// outline used to sail through <see cref="Profile.FromLoop"/> and produce a
/// self-overlapping shell that still passed <c>Validate()</c>.
/// </summary>
public class ProfileLoopValidationTests
{
    [Fact]
    public void FromLoop_RefusesASelfIntersectingOutline()
    {
        Vector2d[] bowTie = [new(0, 0), new(4, 6), new(4, 0), new(0, 4)];
        var error = Assert.Throws<ArgumentException>(
            () => Profile.FromLoop(bowTie, Frame3d.WorldXY));
        Assert.Contains("crosses itself", error.Message);
    }

    [Fact]
    public void FromLoop_AcceptsASimpleOutline()
    {
        Vector2d[] square = [new(0, 0), new(4, 0), new(4, 4), new(0, 4)];
        var profile = Profile.FromLoop(square, Frame3d.WorldXY);
        Assert.Equal(4, profile.Segments.Count);
    }

    [Fact]
    public void FromRegion_PlacesAValidatedRegionWithoutRecheckingIt()
    {
        // The region validated its own loops at construction; FromRegion must not pay for
        // that twice, and must still produce the outer/holes pair the factories take.
        var outer = new Vector2d[] { new(0, 0), new(10, 0), new(10, 10), new(0, 10) };
        var hole = new Vector2d[] { new(3, 3), new(7, 3), new(7, 7), new(3, 7) };
        var (outerProfile, holeProfiles) = Profile.FromRegion(new Region2d(outer, [hole]));
        Assert.Equal(4, outerProfile.Segments.Count);
        Assert.Single(holeProfiles);
    }
}
