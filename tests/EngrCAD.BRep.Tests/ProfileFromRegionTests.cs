using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.BRep.Tests;

public class ProfileFromRegionTests
{
    private static Region2d Box(double x0, double y0, double x1, double y1) =>
        new([new Vector2d(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)]);

    [Fact]
    public void FromRegion_ProducesOneProfilePerLoopOnTheWorldXyPlane()
    {
        var region = new Region2d(
            [new Vector2d(0, 0), new(10, 0), new(10, 6), new(0, 6)],
            [[new Vector2d(2, 2), new(4, 2), new(4, 4), new(2, 4)]]);

        var (outer, holes) = Profile.FromRegion(region);

        Assert.Equal(4, outer.Segments.Count);
        var hole = Assert.Single(holes);
        Assert.Equal(4, hole.Segments.Count);
        Assert.True(outer.Normal.IsParallelTo(Vector3d.UnitZ, Tolerance.Default));
        // The region is canonical (CCW outer, CW hole), so the two profiles wind opposite.
        Assert.True(outer.Normal.Dot(hole.Normal) < 0);
    }

    [Fact]
    public void FromRegion_HonorsThePlacementFrame()
    {
        var frame = Frame3d.FromXY((1, 2, 3), Vector3d.UnitY, Vector3d.UnitZ);
        var (outer, _) = Profile.FromRegion(Box(0, 0, 4, 2), frame);

        // Sketch-local (4, 2) maps to origin + 4·Y + 2·Z.
        var corners = outer.Segments.Select(s => s.PointAt(s.Domain.Start)).ToList();
        Assert.Contains(corners, p => p.AreEqual((1, 2, 3), Tolerance.Default));
        Assert.Contains(corners, p => p.AreEqual((1, 6, 5), Tolerance.Default));
        Assert.True(outer.Normal.IsParallelTo(Vector3d.UnitX, Tolerance.Default));
    }

    [Fact]
    public void FromRegion_FeedsExtrudeRevolveAndSweep()
    {
        var plate = Profile.FromRegion(Box(0, 0, 10, 4), Frame3d.WorldXY);
        var extruded = SolidFactory.Extrude(plate.Outer, Vector3d.UnitZ * 2, plate.Holes);
        extruded.Validate();
        Assert.True(extruded.SatisfiesEulerFormula(genus: 0));

        // A region off the axis revolves into a ring (torus topology on a full turn).
        var ring = Profile.FromRegion(Box(3, -1, 5, 1), Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ));
        var revolved = SolidFactory.Revolve(ring.Outer, Vector3d.Zero, Vector3d.UnitZ);
        revolved.Validate();
        Assert.True(revolved.SatisfiesEulerFormula(genus: 1));

        var swept = SolidFactory.Sweep(plate.Outer, new Line3d((0, 0, 0), (0, 0, 5)));
        swept.Validate();
        Assert.True(swept.SatisfiesEulerFormula(genus: 0));
    }

    [Fact]
    public void FromLoop_RejectsDegenerateInput()
    {
        Assert.Throws<ArgumentException>(() =>
            Profile.FromLoop([new Vector2d(0, 0), new(1, 1)], Frame3d.WorldXY));
        Assert.Throws<ArgumentNullException>(() => Profile.FromRegion(null!));
    }
}
