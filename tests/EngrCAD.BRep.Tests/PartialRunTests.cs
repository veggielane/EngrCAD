using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// Partial rim runs: a band that stops mid-rim with SETBACK terminations — a planar
/// face perpendicular to the terminal edge at the stop vertex, exact because the band's
/// cross-section there is already planar. Volumes are asserted in
/// EngrCAD.Modeling.Tests (closed-form: the removed prism is terminated flush at the
/// end planes) and tessellation quality through the Interop corpus gate.
/// </summary>
public class PartialRunTests
{
    private static BrepSolid Box(double w = 30, double d = 20, double h = 6) =>
        SolidFactory.MakeBox(new Aabb((0, 0, 0), (w, d, h)));

    private static BrepFace Top(BrepSolid solid) =>
        solid.PlanarFacesWithNormal(Vector3d.UnitZ).Single();

    private static BrepEdge TopEdgeAlongX(BrepSolid solid, double y) =>
        Top(solid).OuterLoop.Coedges
            .Select(c => c.Edge)
            .Single(e => e.Curve.Underlying is Line3d
                && Math.Abs(e.Curve.PointAt(e.Domain.Start).Y - y) < 1e-9
                && Math.Abs(e.Curve.PointAt(e.Domain.End).Y - y) < 1e-9);

    [Fact]
    public void SingleEdgeFilletRun_TerminatesWithPlanarSetbackFaces()
    {
        var box = Box();
        var solid = Filleting.FilletEdges(box, [TopEdgeAlongX(box, 0)], 2);
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
        // 6 box faces (top and front rebuilt) + 1 band + 2 terminations.
        Assert.Equal(9, solid.Faces.Count());

        // The terminations are planar faces perpendicular to the run edge (±x), and
        // every loop point lies on its own plane.
        var terminations = solid.Faces
            .Where(f => f.Surface is PlaneSurface plane
                && Math.Abs(Math.Abs(plane.Normal.Normalized().X) - 1) < 1e-12
                && f.OuterLoop.Coedges.Count == 3)
            .ToList();
        Assert.Equal(2, terminations.Count);
        foreach (var termination in terminations)
        {
            var plane = (PlaneSurface)termination.Surface;
            var normal = plane.Normal.Normalized();
            foreach (var coedge in termination.OuterLoop.Coedges)
            {
                for (int i = 0; i <= 8; i++)
                {
                    var point = coedge.Edge.Curve.PointAt(coedge.Edge.Domain.ParameterAt(i / 8.0));
                    Assert.True(Math.Abs((point - plane.Origin).Dot(normal)) < 1e-9,
                        $"termination loop point {point} off its plane");
                }
            }
        }

        // The rest of the rim keeps its sharp corners: the original box vertices at
        // both ends of the run edge survive.
        Assert.Contains(solid.Vertices, v => v.Position.DistanceTo((0, 0, 6)) < 1e-9);
        Assert.Contains(solid.Vertices, v => v.Position.DistanceTo((30, 0, 6)) < 1e-9);
    }

    [Fact]
    public void TwoEdgeLRun_MitersTheInteriorCornerAndTerminatesTheEnds()
    {
        var box = Box();
        var top = Top(box);
        // The edge along y = 0 and the adjacent edge along x = 30: an L-shaped run
        // with one mitered interior corner.
        var alongX = TopEdgeAlongX(box, 0);
        var alongY = top.OuterLoop.Coedges.Select(c => c.Edge)
            .Single(e => e.Curve.Underlying is Line3d
                && Math.Abs(e.Curve.PointAt(e.Domain.Start).X - 30) < 1e-9
                && Math.Abs(e.Curve.PointAt(e.Domain.End).X - 30) < 1e-9);

        var solid = Filleting.FilletEdges(box, [alongX, alongY], 1.5);
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
        // 6 box faces + 2 bands + 2 terminations.
        Assert.Equal(10, solid.Faces.Count());
        // The interior corner mitered on the bicylinder ellipse.
        Assert.Single(solid.Edges, e => e.Curve is Ellipse3d);
    }

    [Fact]
    public void DisjointRuns_ResolveSeparately()
    {
        var box = Box();
        var solid = Filleting.FilletEdges(
            box, [TopEdgeAlongX(box, 0), TopEdgeAlongX(box, 20)], 2);
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
        // 6 box faces + 2 bands + 4 terminations.
        Assert.Equal(12, solid.Faces.Count());
    }

    [Fact]
    public void ChamferRun_BuildsTheSameTopology()
    {
        var box = Box();
        var solid = Filleting.ChamferEdges(box, [TopEdgeAlongX(box, 0)], 1.5);
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(9, solid.Faces.Count());
    }

    [Fact]
    public void ArcTerminalEdge_IsRefusedByName()
    {
        // A stadium prism's top rim: straight edges alternate with semicircle arcs.
        // Selecting an arc puts an arc at a run END, which is refused (its cylindrical
        // neighbor would need periodic re-trimming).
        var profile = Profile.FromLoop(
            [new Vector2d(-8, -4), new Vector2d(8, -4), new Vector2d(8, 4), new Vector2d(-8, 4)],
            Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY));
        _ = profile; // the polygon profile has no arcs; use a hand-built wedge instead

        var box = Box();
        var top = Top(box);
        // Simulate by selecting a run and asserting the straight-terminal rule via the
        // public API on a rim with arcs: an extruded quarter-round wedge.
        var arc = new CurveSegment(
            new Circle3d((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 6), 0, Math.PI / 2);
        var wedgeProfile = new Profile(
        [
            new Line3d((0, 0, 0), (6, 0, 0)),
            arc,
            new Line3d((0, 6, 0), (0, 0, 0)),
        ]);
        var wedge = SolidFactory.Extrude(wedgeProfile, Vector3d.UnitZ * 3);
        var wedgeTop = wedge.PlanarFacesWithNormal(Vector3d.UnitZ).Single();
        var arcEdge = wedgeTop.OuterLoop.Coedges.Select(c => c.Edge)
            .Single(e => e.Curve.Underlying is Circle3d);

        var error = Assert.Throws<NotSupportedException>(
            () => Filleting.ChamferEdges(wedge, [arcEdge], 0.5));
        Assert.Contains("STRAIGHT rim edges", error.Message);
    }

    [Fact]
    public void CompleteRimSelections_StillResolveAsRims()
    {
        var box = Box();
        var rim = Top(box).OuterLoop.Coedges.Select(c => c.Edge).ToList();
        var solid = Filleting.FilletEdges(box, rim, 2);
        solid.Validate();
        // Full-rim surgery: 10 faces (bottom + 4 sides + shrunk top + 4 bands), no
        // termination faces anywhere.
        Assert.Equal(10, solid.Faces.Count());
    }
}
