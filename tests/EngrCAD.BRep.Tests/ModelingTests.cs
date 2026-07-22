using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

public class ModelingTests
{
    private static Profile UnitSquare() => Profile.FromPoints(
        [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)]);

    [Fact]
    public void Profile_ValidatesChainAndPlanarity()
    {
        // Disconnected chain.
        Assert.Throws<ArgumentException>(() => new Profile(
        [
            new Line3d((0, 0, 0), (1, 0, 0)),
            new Line3d((2, 0, 0), (0, 1, 0)),
            new Line3d((0, 1, 0), (0, 0, 0)),
        ]));
        // Non-planar loop.
        Assert.Throws<ArgumentException>(() => Profile.FromPoints(
            [(0, 0, 0), (1, 0, 0), (1, 1, 1), (0, 1, 0)]));
        // Degenerate (collinear) loop.
        Assert.Throws<ArgumentException>(() => Profile.FromPoints(
            [(0, 0, 0), (1, 0, 0), (2, 0, 0)]));

        var square = UnitSquare();
        Assert.True(square.Normal.AreEqual(Vector3d.UnitZ, Tolerance.Default));
        Assert.True(square.Reversed().Normal.AreEqual(-Vector3d.UnitZ, Tolerance.Default));
    }

    [Fact]
    public void Extrude_Square_HasBoxTopology()
    {
        var solid = SolidFactory.Extrude(UnitSquare(), (0, 0, 2));
        solid.Validate();
        Assert.Equal(8, solid.Vertices.Count());
        Assert.Equal(12, solid.Edges.Count());
        Assert.Equal(6, solid.Faces.Count());
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(4, solid.Faces.Count(f => f.Surface is ExtrudedSurface));
        Assert.Equal(2, solid.Faces.Count(f => f.Surface is PlaneSurface));
    }

    [Fact]
    public void Extrude_ClockwiseProfile_IsAutoReversed()
    {
        var clockwise = Profile.FromPoints([(0, 0, 0), (0, 1, 0), (1, 1, 0), (1, 0, 0)]);
        Assert.True(clockwise.Normal.AreEqual(-Vector3d.UnitZ, Tolerance.Default));
        var solid = SolidFactory.Extrude(clockwise, (0, 0, 2));
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
    }

    [Fact]
    public void Extrude_Circle_HasCylinderTopology()
    {
        var solid = SolidFactory.Extrude(Profile.Circle((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 1), (0, 0, 3));
        solid.Validate();
        Assert.Equal(2, solid.Vertices.Count());
        Assert.Equal(2, solid.Edges.Count());
        Assert.Equal(3, solid.Faces.Count());
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
    }

    [Fact]
    public void Extrude_InPlaneDirection_Throws()
    {
        Assert.Throws<ArgumentException>(() => SolidFactory.Extrude(UnitSquare(), (1, 0, 0)));
        Assert.Throws<ArgumentException>(() => SolidFactory.Extrude(UnitSquare(), Vector3d.Zero));
    }

    [Fact]
    public void Revolve_Square_HasTorusTopology()
    {
        // Square r ∈ [1, 2], z ∈ [0, 1] in the XZ plane, revolved about Z.
        var profile = Profile.FromPoints([(1, 0, 0), (2, 0, 0), (2, 0, 1), (1, 0, 1)]);
        var solid = SolidFactory.Revolve(profile, Vector3d.Zero, Vector3d.UnitZ);
        solid.Validate();

        Assert.Equal(4, solid.Vertices.Count());
        Assert.Equal(4, solid.Edges.Count());
        Assert.Equal(4, solid.Faces.Count());
        Assert.Equal(8, solid.Loops.Count());
        Assert.True(solid.SatisfiesEulerFormula(genus: 1));
        Assert.All(solid.Faces, f => Assert.IsType<RevolvedSurface>(f.Surface));
        Assert.All(solid.Edges, e => Assert.True(e.IsClosedEdge));
    }

    [Fact]
    public void Revolve_Validations()
    {
        var profile = Profile.FromPoints([(1, 0, 0), (2, 0, 0), (2, 0, 1), (1, 0, 1)]);
        // Axis not in the profile plane.
        Assert.Throws<ArgumentException>(() => SolidFactory.Revolve(profile, Vector3d.Zero, Vector3d.UnitY));
        // Axis offset out of the plane.
        Assert.Throws<ArgumentException>(() => SolidFactory.Revolve(profile, (0, 5, 0), Vector3d.UnitZ));
        // Profile crossing the axis.
        var crossing = Profile.FromPoints([(-1, 0, 0), (1, 0, 0), (1, 0, 1), (-1, 0, 1)]);
        Assert.Throws<ArgumentException>(() => SolidFactory.Revolve(crossing, Vector3d.Zero, Vector3d.UnitZ));
        // Single closed curve.
        var circle = Profile.Circle((2, 0, 0), Vector3d.UnitX, Vector3d.UnitZ, 0.5);
        Assert.Throws<NotSupportedException>(() => SolidFactory.Revolve(circle, Vector3d.Zero, Vector3d.UnitZ));
    }

    [Fact]
    public void Extrude_WithCircularHole_HasGenusOneTopology()
    {
        var plate = Profile.FromPoints([(0, 0, 0), (4, 0, 0), (4, 3, 0), (0, 3, 0)]);
        var hole = Profile.Circle((2, 1.5, 0), Vector3d.UnitX, Vector3d.UnitY, 0.8);
        var solid = SolidFactory.Extrude(plate, (0, 0, 0.5), holes: [hole]);
        solid.Validate();

        Assert.Equal(10, solid.Vertices.Count()); // 8 corners + 2 hole seams
        Assert.Equal(14, solid.Edges.Count());    // 12 + 2 hole circles
        Assert.Equal(7, solid.Faces.Count());     // 6 + hole band
        Assert.Equal(10, solid.Loops.Count());    // 6 + 2 cap hole loops + 2 band loops
        Assert.True(solid.SatisfiesEulerFormula(genus: 1));
        Assert.Equal(2, solid.Faces.Max(f => f.Loops.Count)); // caps carry the hole loop
    }

    [Fact]
    public void Extrude_WithTwoHoles_HasGenusTwoTopology()
    {
        var plate = Profile.FromPoints([(0, 0, 0), (6, 0, 0), (6, 3, 0), (0, 3, 0)]);
        var holes = new[]
        {
            Profile.Circle((1.5, 1.5, 0), Vector3d.UnitX, Vector3d.UnitY, 0.7),
            Profile.Circle((4.5, 1.5, 0), Vector3d.UnitX, Vector3d.UnitY, 0.7),
        };
        var solid = SolidFactory.Extrude(plate, (0, 0, 0.5), holes);
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 2));
    }

    [Fact]
    public void Extrude_NonCoplanarHole_Throws()
    {
        var plate = Profile.FromPoints([(0, 0, 0), (4, 0, 0), (4, 3, 0), (0, 3, 0)]);
        var lifted = Profile.Circle((2, 1.5, 0.2), Vector3d.UnitX, Vector3d.UnitY, 0.5);
        Assert.Throws<ArgumentException>(() => SolidFactory.Extrude(plate, (0, 0, 1), holes: [lifted]));
    }

    [Fact]
    public void PartialRevolve_Square_MatchesExtrudeTopology()
    {
        var profile = Profile.FromPoints([(1, 0, 0), (2, 0, 0), (2, 0, 1), (1, 0, 1)]);
        var solid = SolidFactory.Revolve(profile, Vector3d.Zero, Vector3d.UnitZ, angle: Math.PI / 2);
        solid.Validate();

        Assert.Equal(8, solid.Vertices.Count());
        Assert.Equal(12, solid.Edges.Count());
        Assert.Equal(6, solid.Faces.Count());
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(4, solid.Faces.Count(f => f.Surface is RevolvedSurface));
        Assert.Equal(2, solid.Faces.Count(f => f.Surface is PlaneSurface));
    }

    [Fact]
    public void PartialRevolve_Circle_MakesElbowTopology()
    {
        // A pipe elbow: circular profile revolved a quarter turn.
        var profile = Profile.Circle((1.5, 0, 0), Vector3d.UnitX, Vector3d.UnitZ, 0.4);
        var solid = SolidFactory.Revolve(profile, Vector3d.Zero, Vector3d.UnitZ, angle: Math.PI / 2);
        solid.Validate();
        Assert.Equal(2, solid.Vertices.Count());
        Assert.Equal(2, solid.Edges.Count());
        Assert.Equal(3, solid.Faces.Count());
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
    }

    [Fact]
    public void Revolve_FullTurnWithHoles_IsRejected()
    {
        var profile = Profile.FromPoints([(1, 0, 0), (3, 0, 0), (3, 0, 2), (1, 0, 2)]);
        var hole = Profile.FromPoints([(1.5, 0, 0.5), (2.5, 0, 0.5), (2.5, 0, 1.5), (1.5, 0, 1.5)]);
        Assert.Throws<NotSupportedException>(() =>
            SolidFactory.Revolve(profile, Vector3d.Zero, Vector3d.UnitZ, holes: [hole]));
        // But a partial revolve with the same hole is fine.
        var solid = SolidFactory.Revolve(profile, Vector3d.Zero, Vector3d.UnitZ, angle: Math.PI, holes: [hole]);
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 1));
    }

    [Fact]
    public void Sweep_AlongStraightPath_MatchesExtrudeTopology()
    {
        var path = new Line3d((0, 0, 0), (0, 0, 3));
        var solid = SolidFactory.Sweep(UnitSquare(), path);
        solid.Validate();
        Assert.Equal(8, solid.Vertices.Count());
        Assert.Equal(12, solid.Edges.Count());
        Assert.Equal(6, solid.Faces.Count());
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(4, solid.Faces.Count(f => f.Surface is SweptSurface));
    }

    [Fact]
    public void Sweep_Validations()
    {
        // Profile plane not perpendicular to the start tangent.
        var path = new Line3d((0, 0, 0), (1, 0, 0));
        Assert.Throws<ArgumentException>(() => SolidFactory.Sweep(UnitSquare(), path));

        // Profile plane offset from the path start.
        var offsetProfile = Profile.FromPoints([(0, 0, 1), (1, 0, 1), (1, 1, 1), (0, 1, 1)]);
        var zPath = new Line3d((0, 0, 0), (0, 0, 3));
        Assert.Throws<ArgumentException>(() => SolidFactory.Sweep(offsetProfile, zPath));
    }

    [Fact]
    public void SweptSurface_FramesAreOrthonormalAndMinimallyRotating()
    {
        // A quarter-turn arc path: frames must stay orthonormal and never flip.
        var path = new NurbsCurve(2,
            [(0, 0, 0), (0, 0, 2), (0, 2, 2)],
            [1, Math.Sqrt(2) / 2, 1],
            [0, 0, 0, 1, 1, 1]);
        var surface = new SweptSurface(new Circle3d((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 0.3), path, Vector3d.UnitX);

        Vector3d? previousX = null;
        for (int i = 0; i <= 50; i++)
        {
            double v = path.Domain.ParameterAt(i / 50.0);
            var (_, x, y) = surface.FrameAt(v);
            var tangent = path.TangentAt(v);
            Assert.Equal(1.0, x.Length, 9);
            Assert.Equal(0.0, x.Dot(tangent), 9);
            Assert.Equal(0.0, x.Dot(y), 9);
            if (previousX is { } px)
                Assert.True(x.Dot(px) > 0.9, "frame rotated abruptly between samples");
            previousX = x;
        }
    }
}
