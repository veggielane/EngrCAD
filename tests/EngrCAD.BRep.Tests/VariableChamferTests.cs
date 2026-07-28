using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// Variable-setback chamfers: the law is evaluated at rim corners and interpolates
/// linearly along each edge. The inset boundary of a straight edge is then still a
/// straight line, so miters stay exact line–line intersections and every strip is an
/// exact plane. These are pure-geometry checks; volumes are asserted in
/// EngrCAD.Modeling.Tests (against an independently built convex hull) and tessellation
/// quality in the Interop corpus gate.
/// </summary>
public class VariableChamferTests
{
    private static (BrepSolid Solid, BrepFace Top) Box(double w, double d, double h)
    {
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (w, d, h)));
        return (box, box.PlanarFacesWithNormal(Vector3d.UnitZ).Single());
    }

    [Fact]
    public void BoxTopRim_VariableChamfer_HasExactMiterCornersAndValidTopology()
    {
        // Law 1 + 0.05·x on a 30 × 20 × 6 box: corners at x = 0 get setback 1, corners
        // at x = 30 get 2.5. Everything below is closed-form.
        var (box, top) = Box(30, 20, 6);
        var solid = Filleting.ChamferRim(box, top, p => 1 + 0.05 * p.X);
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
        // bottom + 4 sides + shrunk top + 4 strips.
        Assert.Equal(10, solid.Faces.Count());

        // Every strip is an exact PLANE whose loop lies ON it (the weld-critical
        // property): strips are the four new faces.
        var strips = solid.Faces.Where(f =>
            f.Surface is PlaneSurface plane &&
            Math.Abs(plane.Normal.Normalized().Dot(Vector3d.UnitZ)) is > 1e-12 and < 1 - 1e-12).ToList();
        Assert.Equal(4, strips.Count);
        foreach (var strip in strips)
        {
            var plane = (PlaneSurface)strip.Surface;
            var normal = plane.Normal.Normalized();
            foreach (var coedge in strip.OuterLoop.Coedges)
            {
                var edge = coedge.Edge;
                for (int i = 0; i <= 8; i++)
                {
                    var point = edge.Curve.PointAt(edge.Domain.ParameterAt(i / 8.0));
                    Assert.True(Math.Abs((point - plane.Origin).Dot(normal)) < 1e-9,
                        $"strip loop point {point} is off its own plane");
                }
            }
        }

        // The miter corner nearest the origin: left inset line x = 1 against the
        // bottom edge's tilted inset line y = 1 + 0.05·x, meeting at (1, 1.05, 6);
        // the dropped corner sits the corner's own setback below the corner.
        var positions = solid.Vertices.Select(v => v.Position).ToList();
        Assert.Contains(positions, p => p.DistanceTo((1, 1.05, 6)) < 1e-9);
        Assert.Contains(positions, p => p.DistanceTo((0, 0, 5)) < 1e-9);
        // And the deep end: inset line x = 27.5 against y = 1 + 0.05·x → (27.5, 2.375, 6),
        // drop 2.5 at (30, 0, 3.5).
        Assert.Contains(positions, p => p.DistanceTo((27.5, 2.375, 6)) < 1e-9);
        Assert.Contains(positions, p => p.DistanceTo((30, 0, 3.5)) < 1e-9);
    }

    [Fact]
    public void UniformLaw_AgreesWithConstantChamfer()
    {
        var (boxA, topA) = Box(30, 20, 6);
        var byLaw = Filleting.ChamferRim(boxA, topA, _ => 1.5);
        var (boxB, topB) = Box(30, 20, 6);
        var byConstant = Filleting.ChamferRim(boxB, topB, 1.5, 1.5);

        var lawPositions = byLaw.Vertices.Select(v => v.Position).ToList();
        foreach (var vertex in byConstant.Vertices)
            Assert.Contains(lawPositions, p => p.DistanceTo(vertex.Position) < 1e-12);
        Assert.Equal(byConstant.Faces.Count(), byLaw.Faces.Count());
    }

    [Fact]
    public void AngleVariant_DropsByTanAngleTimesSetback()
    {
        var (box, top) = Box(30, 20, 6);
        var solid = Filleting.ChamferRimAtAngle(box, top, p => 1 + 0.05 * p.X, 60);
        solid.Validate();
        double drop = 1 * Math.Tan(60 * Math.PI / 180);
        Assert.Contains(solid.Vertices.Select(v => v.Position),
            p => p.DistanceTo((0, 0, 6 - drop)) < 1e-9);
        double deepDrop = 2.5 * Math.Tan(60 * Math.PI / 180);
        Assert.Contains(solid.Vertices.Select(v => v.Position),
            p => p.DistanceTo((30, 0, 6 - deepDrop)) < 1e-9);
    }

    [Fact]
    public void NonPositiveLaw_IsRefusedNamingTheCorner()
    {
        var (box, top) = Box(30, 20, 6);
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => Filleting.ChamferRim(box, top, p => p.X - 1));
        Assert.Contains("positive and finite at every rim corner", error.Message);
    }

    [Fact]
    public void VariableLawOnFullCircularRim_IsRefusedAsSpiral()
    {
        var cylinder = SolidFactory.MakeCylinder(10, 8);
        var top = cylinder.PlanarFacesWithNormal(Vector3d.UnitZ).Single();
        var error = Assert.Throws<NotSupportedException>(
            () => Filleting.ChamferRim(cylinder, top, p => 1 + 0.05 * p.X));
        Assert.Contains("spiral", error.Message);
    }

    [Fact]
    public void ConstantLawOnFullCircularRim_ReducesToTheExactConeBand()
    {
        var cylinder = SolidFactory.MakeCylinder(10, 8);
        var top = cylinder.PlanarFacesWithNormal(Vector3d.UnitZ).Single();
        var solid = Filleting.ChamferRim(cylinder, top, _ => 1.5);
        solid.Validate();
        Assert.Contains(solid.Faces, f => f.Surface is RevolvedSurface);
    }

    [Fact]
    public void ChamferEdges_VariableLaw_ResolvesRimsLikeTheConstantForm()
    {
        var (box, top) = Box(30, 20, 6);
        var rim = top.OuterLoop.Coedges.Select(c => c.Edge).ToList();
        var solid = Filleting.ChamferEdges(box, rim, p => 1 + 0.05 * p.X);
        solid.Validate();
        Assert.Equal(10, solid.Faces.Count());
    }
}
