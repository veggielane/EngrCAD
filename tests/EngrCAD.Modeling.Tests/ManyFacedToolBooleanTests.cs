using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Booleans whose TOOL has many faces — the shape of every engraved label, every
/// spline pocket, every sketch outline with a lot of segments, since each profile
/// segment becomes its own extruded wall. This used to be the pipeline's worst case
/// (the cost grew superlinearly with tool faces: a 7-glyph engraving took over a
/// minute), which made it the case most likely to regress silently.
///
/// The tools here are straight-sided regular polygons, so the answer is an EXACT
/// analytic volume rather than a tolerance band — the only assertion that catches
/// "closed, manifold, and wrong", which is this pipeline's real failure mode.
/// </summary>
public class ManyFacedToolBooleanTests
{
    /// <summary>Regular n-gon of circumradius r centred at (cx, cy), first vertex on +x.</summary>
    private static Sketch Ngon(int sides, double radius, double cx = 0, double cy = 0)
    {
        var corners = new Vector2d[sides];
        for (int i = 0; i < sides; i++)
        {
            double angle = 2 * Math.PI * i / sides;
            corners[i] = new Vector2d(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle));
        }
        return Sketch.Polygon(corners);
    }

    /// <summary>Exact area of a regular n-gon of circumradius r.</summary>
    private static double NgonArea(int sides, double radius) =>
        0.5 * sides * radius * radius * Math.Sin(2 * Math.PI / sides);

    [Fact]
    public void PocketFromA28SidedTool_IsExactAndClosed()
    {
        const int sides = 28;
        const double radius = 5;
        const double depth = 1.5;

        // Box(40, 20, 4) is centred: top face at z = 2. The tool starts below the top
        // face and overshoots it, so the boolean never meets a coplanar pair.
        var plane = SketchPlane.At((0, 0, 2 - depth), Vector3d.UnitX, Vector3d.UnitY);
        var tool = Shape.Extrude(Ngon(sides, radius), depth * 2, plane);
        var engraved = Shape.Box(40, 20, 4) - tool;

        // The tool really is many-faced (n walls + two caps) — if this ever collapses to
        // a handful of faces the test has stopped covering what it was written for.
        Assert.Equal(sides + 2, tool.ToBrep().Faces.Count());

        var solid = engraved.ToBrep();
        solid.Validate();
        Assert.Single(solid.Shells);                 // a pocket, not a buried cavity
        Assert.True(solid.SatisfiesEulerFormula());

        var mesh = engraved.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(40 * 20 * 4 - NgonArea(sides, radius) * depth, mesh.Volume(), 9);
    }

    [Fact]
    public void SeveralManyFacedToolsInOneBody_StayExact()
    {
        // Three separate pockets, each cut by its own many-faced tool: 3 x (16 + 2)
        // tool faces meeting one top face, which is where the arrangement tracing and
        // the seam sealing have to keep the fragments apart.
        const int sides = 16;
        const double radius = 3;
        const double depth = 1.0;

        var plane = SketchPlane.At((0, 0, 2 - depth), Vector3d.UnitX, Vector3d.UnitY);
        var body = Shape.Box(40, 20, 4);
        foreach (double x in new[] { -12.0, 0.0, 12.0 })
            body -= Shape.Extrude(Ngon(sides, radius, x, 0), depth * 2, plane);

        var solid = body.ToBrep();
        solid.Validate();
        var mesh = body.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(40 * 20 * 4 - 3 * NgonArea(sides, radius) * depth, mesh.Volume(), 9);
    }

    [Fact]
    public void ManyFacedToolCuttingRightThrough_MakesAGenusOneSolid()
    {
        // Through-cut rather than a pocket: the tool crosses BOTH caps, so both of the
        // box's horizontal faces get the full n-segment arrangement, and the result is
        // a genuine hole (genus 1) instead of a pocket.
        const int sides = 24;
        const double radius = 4;

        var plane = SketchPlane.At((0, 0, -3), Vector3d.UnitX, Vector3d.UnitY);
        var body = Shape.Box(40, 20, 4) - Shape.Extrude(Ngon(sides, radius), 6, plane);

        var solid = body.ToBrep();
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 1));

        var mesh = body.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(40 * 20 * 4 - NgonArea(sides, radius) * 4, mesh.Volume(), 9);
    }
}
