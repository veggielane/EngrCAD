using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// <see cref="Draft"/> — the moulding taper. Angles and corner positions are exact here;
/// volumes live in Interop.Tests.
/// </summary>
public class DraftTests
{
    private const double Ten = Math.PI / 18; // 10 degrees

    private static BrepSolid Block() => SolidFactory.MakeBox(new Aabb((-10, -10, 0), (10, 10, 10)));

    private static BrepFace BottomOf(BrepSolid solid) =>
        solid.PlanarFacesWithNormal(-Vector3d.UnitZ).Single();

    private static BrepFace TopOf(BrepSolid solid) =>
        solid.PlanarFacesWithNormal(Vector3d.UnitZ).Single();

    private static IEnumerable<BrepFace> SidesOf(BrepSolid solid) =>
        solid.Faces.Where(f => f.IsPlanar(out _, out var n) && Math.Abs(n.Z) < 0.5);

    [Fact]
    public void Draft_AllSidesOfABlock_MakesAFrustumWithExactAngles()
    {
        var block = Block();
        var drafted = Draft.Apply(block, BottomOf(block), Ten);
        drafted.Validate();
        Assert.True(drafted.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(8, drafted.Vertices.Count());
        Assert.Equal(12, drafted.Edges.Count());
        Assert.Equal(6, drafted.Faces.Count());
        // Every face stays an exact plane, so the result is selectable and exportable.
        Assert.All(drafted.Faces, f => Assert.IsType<PlaneSurface>(f.Surface));

        foreach (var side in SidesOf(drafted))
        {
            side.IsPlanar(out _, out var normal);
            // Rotated about the neutral line toward the pull direction by exactly 10 deg.
            Assert.Equal(Math.Sin(Ten), normal.Dot(Vector3d.UnitZ), 12);
        }

        double inset = 10 * Math.Tan(Ten);
        foreach (var vertex in drafted.Vertices)
        {
            var p = vertex.Position;
            double expected = p.Z < 5 ? 10 : 10 - inset;
            Assert.Equal(expected, Math.Abs(p.X), 12);
            Assert.Equal(expected, Math.Abs(p.Y), 12);
        }
    }

    [Fact]
    public void Draft_NeutralPlaneGeometryDoesNotMove()
    {
        // The defining property of a neutral plane: it is the parting line.
        var block = Block();
        var drafted = Draft.Apply(block, BottomOf(block), Ten);
        var baseCorners = drafted.Vertices.Where(v => Math.Abs(v.Position.Z) < 1e-12).ToList();
        Assert.Equal(4, baseCorners.Count);
        foreach (var corner in baseCorners)
        {
            Assert.Equal(10.0, Math.Abs(corner.Position.X), 12);
            Assert.Equal(10.0, Math.Abs(corner.Position.Y), 12);
        }
    }

    [Fact]
    public void Draft_SelectedFaceOnly_LeavesTheOthersExactlyInPlace()
    {
        var block = Block();
        var drafted = Draft.Apply(
            block, BottomOf(block), Ten,
            f => f.IsPlanar(out _, out var n) && n.Dot(Vector3d.UnitX) > 0.99);
        drafted.Validate();
        Assert.True(drafted.SatisfiesEulerFormula(genus: 0));

        var sides = SidesOf(drafted).ToList();
        Assert.Equal(4, sides.Count);
        int tilted = 0;
        foreach (var side in sides)
        {
            side.IsPlanar(out _, out var normal);
            if (normal.Dot(Vector3d.UnitX) > 0.5)
            {
                Assert.Equal(Math.Sin(Ten), normal.Dot(Vector3d.UnitZ), 12);
                tilted++;
            }
            else
            {
                Assert.Equal(0.0, normal.Dot(Vector3d.UnitZ), 12);
            }
        }
        Assert.Equal(1, tilted);

        // The +X corners came in; the -X ones did not move at all.
        foreach (var vertex in drafted.Vertices)
        {
            var p = vertex.Position;
            double expected = p.X > 0 ? 10 - p.Z * Math.Tan(Ten) : -10;
            Assert.Equal(expected, p.X, 12);
            Assert.Equal(10.0, Math.Abs(p.Y), 12);
        }
    }

    [Fact]
    public void Draft_NegativeAngle_Widens()
    {
        var block = Block();
        var drafted = Draft.Apply(block, BottomOf(block), -Ten);
        drafted.Validate();
        double outset = 10 * Math.Tan(Ten);
        var top = drafted.Vertices.Where(v => v.Position.Z > 5).ToList();
        Assert.Equal(4, top.Count);
        Assert.All(top, v => Assert.Equal(10 + outset, Math.Abs(v.Position.X), 12));
    }

    [Fact]
    public void Draft_AboutTheTopFace_TapersDownwards()
    {
        // Pull is into the solid, so drafting about the top narrows it going DOWN.
        var block = Block();
        var drafted = Draft.Apply(block, TopOf(block), Ten);
        drafted.Validate();
        double inset = 10 * Math.Tan(Ten);
        foreach (var vertex in drafted.Vertices)
        {
            double expected = vertex.Position.Z > 5 ? 10 : 10 - inset;
            Assert.Equal(expected, Math.Abs(vertex.Position.X), 12);
        }
    }

    [Fact]
    public void Draft_ExplicitPullDirection_OverridesTheFaceSense()
    {
        // Same neutral plane (z = 0) but pulling downward: now the block flares upward.
        var block = Block();
        var drafted = Draft.Apply(block, Vector3d.Zero, -Vector3d.UnitZ, Ten);
        drafted.Validate();
        double outset = 10 * Math.Tan(Ten);
        var top = drafted.Vertices.Where(v => v.Position.Z > 5).ToList();
        Assert.All(top, v => Assert.Equal(10 + outset, Math.Abs(v.Position.X), 12));
    }

    [Fact]
    public void Draft_TwiceByHalf_EqualsOnceByTheWhole()
    {
        // Rotation about a fixed neutral line composes: the neutral plane is unchanged and
        // the first rotation left the neutral line where it was.
        var once = Draft.Apply(Block(), BottomOf(Block()), Ten);
        var half = Draft.Apply(Block(), BottomOf(Block()), Ten / 2);
        var twice = Draft.Apply(half, BottomOf(half), Ten / 2);
        twice.Validate();

        var a = once.Vertices.Select(v => v.Position).OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ToList();
        var b = twice.Vertices.Select(v => v.Position).OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ToList();
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
            Assert.True(a[i].AreEqual(b[i], new Tolerance(1e-12, 1e-12)), $"{a[i]} vs {b[i]}");
    }

    [Fact]
    public void Draft_ZeroAngle_ReproducesTheSolid()
    {
        var block = Block();
        var drafted = Draft.Apply(block, BottomOf(block), 0);
        drafted.Validate();
        foreach (var vertex in drafted.Vertices)
        {
            Assert.Equal(10.0, Math.Abs(vertex.Position.X), 12);
            Assert.Equal(10.0, Math.Abs(vertex.Position.Y), 12);
        }
    }

    [Fact]
    public void Draft_NonRectangularPrism_TapersEveryFaceAboutItsOwnNeutralLine()
    {
        // A hexagonal prism: each side has a different neutral line, and all six must end
        // at the same angle to the pull direction.
        var corners = new Vector3d[6];
        for (int i = 0; i < 6; i++)
            corners[i] = (5 * Math.Cos(i * Math.PI / 3), 5 * Math.Sin(i * Math.PI / 3), 0);
        var prism = SolidFactory.Extrude(Profile.FromPoints(corners), (0, 0, 4));
        var drafted = Draft.Apply(prism, BottomOf(prism), Ten);
        drafted.Validate();
        Assert.True(drafted.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(12, drafted.Vertices.Count());
        Assert.Equal(8, drafted.Faces.Count());

        foreach (var side in SidesOf(drafted))
        {
            side.IsPlanar(out _, out var normal);
            Assert.Equal(Math.Sin(Ten), normal.Dot(Vector3d.UnitZ), 12);
        }
        // Apothem shrinks by height x tan(angle) on every face.
        double apothem = 5 * Math.Cos(Math.PI / 6);
        foreach (var vertex in drafted.Vertices.Where(v => v.Position.Z > 2))
        {
            double radius = Math.Sqrt(vertex.Position.X * vertex.Position.X + vertex.Position.Y * vertex.Position.Y);
            Assert.Equal((apothem - 4 * Math.Tan(Ten)) / Math.Cos(Math.PI / 6), radius, 10);
        }
    }

    [Fact]
    public void Draft_RejectsWhatItCannotDoExactly()
    {
        var block = Block();

        // Curved faces.
        var cylinder = SolidFactory.MakeCylinder(3, 5);
        Assert.Throws<NotSupportedException>(() =>
            Draft.Apply(cylinder, Vector3d.Zero, Vector3d.UnitZ, Ten));

        // Caps with holes.
        var plate = Profile.FromPoints([(-5, -5, 0), (5, -5, 0), (5, 5, 0), (-5, 5, 0)]);
        var hole = Profile.FromPoints([(-2, -2, 0), (2, -2, 0), (2, 2, 0), (-2, 2, 0)]);
        var drilled = SolidFactory.Extrude(plate, (0, 0, 3), holes: [hole]);
        Assert.Throws<NotSupportedException>(() =>
            Draft.Apply(drilled, Vector3d.Zero, Vector3d.UnitZ, Ten));

        // A taper the solid is not tall enough for: 10 tall, 20 wide, so 45 degrees on all
        // four sides collapses the top face to a point and beyond.
        Assert.Throws<ArgumentException>(() =>
            Draft.Apply(Block(), BottomOf(Block()), 0.9 * Math.PI / 2));

        // Selecting a cap — alone, or mixed in with valid side faces (never a silent no-op).
        Assert.Throws<ArgumentException>(() =>
            Draft.Apply(Block(), BottomOf(Block()), Ten, f => f.IsPlanar(out _, out var n) && n.Z < -0.9));
        Assert.Throws<ArgumentException>(() => Draft.Apply(Block(), BottomOf(Block()), Ten, _ => true));

        // Selecting nothing.
        Assert.Throws<ArgumentException>(() => Draft.Apply(Block(), BottomOf(Block()), Ten, _ => false));

        // 90 degrees or more.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Draft.Apply(Block(), BottomOf(Block()), Math.PI / 2));
        Assert.Throws<ArgumentException>(() =>
            Draft.Apply(Block(), Vector3d.Zero, Vector3d.Zero, Ten));
    }
}
