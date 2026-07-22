using System.Text.RegularExpressions;
using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Interop.Tests;

public class StepWriterTests
{
    private static void AssertWellFormed(string step)
    {
        Assert.StartsWith("ISO-10303-21;", step);
        Assert.EndsWith("END-ISO-10303-21;\n", step);
        Assert.Contains("MANIFOLD_SOLID_BREP", step);
        Assert.Contains("CLOSED_SHELL", step);

        // Every reference resolves and ids are unique.
        var defined = Regex.Matches(step, @"(?m)^#(\d+)=").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.Equal(defined.Count, defined.Distinct().Count());
        var definedSet = defined.ToHashSet();
        foreach (Match reference in Regex.Matches(step, @"#(\d+)[^=\d]"))
            Assert.Contains(int.Parse(reference.Groups[1].Value), definedSet);
    }

    [Fact]
    public void Box_ExportsCompleteTopology()
    {
        var step = StepWriter.Write(SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 3, 4))), "box");
        AssertWellFormed(step);
        Assert.Equal(6, Regex.Matches(step, "ADVANCED_FACE").Count);
        Assert.Equal(6, Regex.Matches(step, "PLANE\\(").Count);
        Assert.Equal(12, Regex.Matches(step, "EDGE_CURVE").Count);
        Assert.Equal(24, Regex.Matches(step, "ORIENTED_EDGE").Count);
        Assert.Equal(8, Regex.Matches(step, "VERTEX_POINT").Count);
    }

    [Fact]
    public void CylinderRevolveAndExtrude_ExportAnalyticSurfaces()
    {
        var cylinder = StepWriter.Write(SolidFactory.MakeCylinder(1.5, 4));
        AssertWellFormed(cylinder);
        Assert.Contains("CYLINDRICAL_SURFACE", cylinder);
        Assert.Contains("CIRCLE", cylinder);

        var pulley = StepWriter.Write(SolidFactory.Revolve(
            Profile.FromPoints([(1, 0, 0), (2, 0, 0), (2, 0, 1), (1, 0, 1)]),
            Vector3d.Zero, Vector3d.UnitZ));
        AssertWellFormed(pulley);
        Assert.Contains("SURFACE_OF_REVOLUTION", pulley);

        var bracket = StepWriter.Write(SolidFactory.Extrude(
            Profile.FromPoints([(0, 0, 0), (2, 0, 0), (2, 1, 0), (1, 1, 0), (1, 2, 0), (0, 2, 0)]),
            (0, 0, 1)));
        AssertWellFormed(bracket);
        Assert.Contains("SURFACE_OF_LINEAR_EXTRUSION", bracket);
    }

    [Fact]
    public void BooleanResultAndFillet_Export()
    {
        var box = SolidFactory.MakeBox(new Aabb((-1, -1, 0), (1, 1, 1)));
        var tool = SolidFactory.Extrude(
            Profile.Circle((0, 0, -1), Vector3d.UnitX, Vector3d.UnitY, 0.4), (0, 0, 3));
        var drilled = BrepBoolean.Difference(box, tool);
        var step = StepWriter.Write(drilled, "drilled");
        AssertWellFormed(step);
        Assert.Equal(7, Regex.Matches(step, "ADVANCED_FACE").Count);
        Assert.Contains(",.F.);", step); // the reversed bore wall exports same-sense = false

        var puck = SolidFactory.MakeCylinder(1.5, 2);
        var filleted = Filleting.FilletEdge(puck, puck.Edges.First(e => e.Uses.Any(u =>
            u.Loop.Face.Surface is PlaneSurface p && p.Normal.Dot(Vector3d.UnitZ) > 0.9)), 0.4);
        var filletStep = StepWriter.Write(filleted, "rounded puck");
        AssertWellFormed(filletStep);
        Assert.Contains("SURFACE_OF_REVOLUTION", filletStep);
    }

    [Fact]
    public void NurbsProfileSolid_ExportsRationalBspline()
    {
        // A profile with a rational quadratic (exact circle) segment chain: half circle + base line.
        double w = Math.Sqrt(2) / 2;
        var halfCircle = new NurbsCurve(2,
            [(1, 0, 0), (1, 1, 0), (0, 1, 0), (-1, 1, 0), (-1, 0, 0)],
            [1, w, 1, w, 1],
            [0, 0, 0, 0.5, 0.5, 1, 1, 1]);
        var profile = new Profile([halfCircle, new Line3d((-1, 0, 0), (1, 0, 0))]);
        var solid = SolidFactory.Extrude(profile, (0, 0, 1));
        var step = StepWriter.Write(solid);
        AssertWellFormed(step);
        Assert.Contains("RATIONAL_B_SPLINE_CURVE", step);
    }
}
