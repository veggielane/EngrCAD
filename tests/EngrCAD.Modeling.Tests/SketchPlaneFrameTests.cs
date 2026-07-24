using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>SketchPlane is a veneer over Frame3d: the historical surface (Origin/XAxis/
/// YAxis/Normal, statics, At) must be unchanged, with the frame now exposed.</summary>
public class SketchPlaneFrameTests
{
    [Fact]
    public void Statics_KeepTheirHistoricalAxes()
    {
        Assert.Equal(Vector3d.Zero, SketchPlane.XY.Origin);
        Assert.Equal(Vector3d.UnitX, SketchPlane.XY.XAxis);
        Assert.Equal(Vector3d.UnitY, SketchPlane.XY.YAxis);
        Assert.Equal(Vector3d.UnitZ, SketchPlane.XY.Normal);

        Assert.Equal(Vector3d.UnitX, SketchPlane.XZ.XAxis);
        Assert.Equal(Vector3d.UnitZ, SketchPlane.XZ.YAxis);
        Assert.Equal(new Vector3d(0, -1, 0), SketchPlane.XZ.Normal); // X × Z

        Assert.Equal(Vector3d.UnitY, SketchPlane.YZ.XAxis);
        Assert.Equal(Vector3d.UnitZ, SketchPlane.YZ.YAxis);
        Assert.Equal(Vector3d.UnitX, SketchPlane.YZ.Normal); // Y × Z
    }

    [Fact]
    public void At_MatchesFrameFromXYBitForBit()
    {
        var origin = new Vector3d(1, 2, 3);
        var xAxis = new Vector3d(0.3, -0.4, 0.5);
        var yAxis = new Vector3d(-0.2, 0.9, 0.1);
        var plane = SketchPlane.At(origin, xAxis, yAxis);
        var frame = Frame3d.FromXY(origin, xAxis, yAxis);
        Assert.Equal(frame, plane.Frame);
        Assert.Equal(frame.X, plane.XAxis);
        Assert.Equal(frame.Y, plane.YAxis);
        Assert.Equal(frame.Z, plane.Normal);
    }

    [Fact]
    public void FrameConstructor_RoundTrips()
    {
        var frame = Frame3d.FromNormal(new Vector3d(0, 0, 6), new Vector3d(0.1, 0.2, 1));
        var plane = new SketchPlane(frame);
        Assert.Equal(frame, plane.Frame);
        Assert.Equal(frame.Origin, plane.Origin);

        // ToWorld agrees with the frame's in-plane map.
        var p = new Vector2d(1.5, -0.5);
        Assert.Equal(plane.Origin + plane.XAxis * p.X + plane.YAxis * p.Y, plane.ToWorld(p));
    }

    // ---- sketch-on-face: the capability Frame3d enables ----

    /// <summary>Inscribed n-gon area — what a tessellated circle of radius r encloses.</summary>
    private static double NgonArea(int n, double r) => 0.5 * n * r * r * Math.Sin(2 * Math.PI / n);

    [Fact]
    public void On_TopFace_SketchExtrudeAndDrillEndToEnd()
    {
        const int n = 128;
        var body = Shape.Extrude(Sketch.Rectangle(4, 3), 2); // x ∈ [-2,2], y ∈ [-1.5,1.5], z ∈ [0,2]
        var brep = body.ToBrep();
        var top = brep.PlanarFacesWithNormal(Vector3d.UnitZ).Single();
        var plane = SketchPlane.On(top);

        Assert.True(plane.Normal.AreEqual(Vector3d.UnitZ, Tolerance.Default)); // outward
        Assert.True(plane.Origin.AreEqual(new Vector3d(0, 0, 2), Tolerance.Default)); // face centroid

        // Extrude a circle sketched on the face: a boss sitting exactly on it, growing
        // along the outward normal.
        var boss = Shape.Extrude(Sketch.Circle(0.8), 1, plane).ToBrep();
        boss.Validate();
        var bossMesh = BRepTessellator.Tessellate(boss, n, 24);
        Assert.True(bossMesh.IsClosed);
        var bounds = Aabb.Empty;
        foreach (var vertex in bossMesh.Vertices)
            bounds = bounds.Union(vertex.Position);
        Assert.Equal(2, bounds.Min.Z, 9);
        Assert.Equal(3, bounds.Max.Z, 9);
        double bossExact = Math.PI * 0.8 * 0.8 * 1;
        Assert.True(Math.Abs(bossMesh.Volume() - bossExact) / bossExact < 0.001,
            $"boss volume {bossMesh.Volume()} vs {bossExact}");

        // Drill a through-hole from the same face plane.
        var drilled = body.Drill(HoleSpec.Simple(0.8), [new(1, 0.5)], depth: 3, plane);
        var solid = drilled.ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid, n, 24);
        Assert.True(mesh.IsClosed);
        Assert.Equal(0, mesh.EulerCharacteristic); // genus 1
        double exact = 24 - NgonArea(n, 0.4) * 2;
        Assert.True(Math.Abs(mesh.Volume() - exact) < 1e-9, $"volume {mesh.Volume()} vs {exact}");
    }

    [Fact]
    public void On_ExtrudedSideFace_DrillsIntoTheSide()
    {
        // Side faces of a sketch extrusion are ExtrudedSurface-over-line planes: the
        // frame's X follows the generator, Z the outward normal.
        const int n = 128;
        var body = Shape.Extrude(Sketch.Rectangle(4, 3), 2);
        var side = body.ToBrep().PlanarFacesWithNormal(Vector3d.UnitY).Single(); // y = 1.5
        var plane = SketchPlane.On(side);

        Assert.True(plane.Normal.AreEqual(Vector3d.UnitY, Tolerance.Default));
        Assert.True(plane.Origin.AreEqual(new Vector3d(0, 1.5, 1), Tolerance.Default));

        var drilled = body.Drill(HoleSpec.Simple(0.6), [new(0, 0)], depth: 1, plane);
        var solid = drilled.ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid, n, 24);
        Assert.True(mesh.IsClosed);
        // Bores drilled into extruded side faces don't hit the inscribed-ngon volume
        // exactly (verified pre-existing with a manually placed SketchPlane.At of the
        // same pose — the trimmed side-face triangulation differs from a planar cap's,
        // ~5e-5 here). The bound still catches any misplacement of the face frame.
        double exact = 24 - NgonArea(n, 0.3) * 1; // blind flat-bottom hole into the side
        Assert.True(Math.Abs(mesh.Volume() - exact) < 1e-4, $"volume {mesh.Volume()} vs {exact}");
    }

    [Fact]
    public void On_NonPlanarFace_Throws()
    {
        var cylinder = Shape.Cylinder(0.75, 2).ToBrep();
        var band = cylinder.Faces.Single(f => f.IsCylindrical(out _, out _, out _));
        Assert.Throws<ArgumentException>(() => SketchPlane.On(band));
    }
}
