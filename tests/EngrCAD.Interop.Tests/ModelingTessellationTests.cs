using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Interop.Tests;

public class ModelingTessellationTests
{
    [Fact]
    public void ExtrudedLProfile_ExactVolume()
    {
        // L-shaped (concave) profile, area 3, extruded to height 2 → volume exactly 6.
        var profile = Profile.FromPoints(
            [(0, 0, 0), (2, 0, 0), (2, 1, 0), (1, 1, 0), (1, 2, 0), (0, 2, 0)]);
        var mesh = BRepTessellator.Tessellate(SolidFactory.Extrude(profile, (0, 0, 2)));
        mesh.Validate();

        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);
        Assert.Equal(6.0, mesh.Volume(), 9);
    }

    [Fact]
    public void ShearExtrusion_VolumeIsBaseTimesNormalHeight()
    {
        // Oblique prism: volume = base area × (direction · normal), independent of shear.
        var square = Profile.FromPoints([(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)]);
        var mesh = BRepTessellator.Tessellate(SolidFactory.Extrude(square, (0.7, 0.3, 2)));
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(2.0, mesh.Volume(), 9);
    }

    [Fact]
    public void ExtrudedCircle_MatchesMakeCylinder()
    {
        var extruded = BRepTessellator.Tessellate(
            SolidFactory.Extrude(Profile.Circle((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 1.5), (0, 0, 4)),
            segmentsPerCircle: 48);
        var reference = BRepTessellator.Tessellate(SolidFactory.MakeCylinder(1.5, 4), segmentsPerCircle: 48);

        extruded.Validate();
        Assert.True(extruded.IsClosed);
        Assert.Equal(reference.Volume(), extruded.Volume(), 9);
        Assert.Equal(reference.SurfaceArea(), extruded.SurfaceArea(), 9);
    }

    [Fact]
    public void RevolvedSquare_IsAnExactPolygonalRing()
    {
        int n = 48;
        var profile = Profile.FromPoints([(1, 0, 0), (2, 0, 0), (2, 0, 1), (1, 0, 1)]);
        var mesh = BRepTessellator.Tessellate(
            SolidFactory.Revolve(profile, Vector3d.Zero, Vector3d.UnitZ), segmentsPerCircle: n);
        mesh.Validate();

        Assert.True(mesh.IsClosed);
        Assert.Equal(0, mesh.EulerCharacteristic); // torus topology

        // The tessellation is an annular n-gonal prism: exact volume, and within 1% of Pappus.
        double ringArea = 0.5 * n * Math.Sin(2 * Math.PI / n) * (4 - 1);
        Assert.Equal(ringArea * 1.0, mesh.Volume(), 9);
        double pappus = 2 * Math.PI * 1.5 * 1.0;
        Assert.True(Math.Abs(mesh.Volume() - pappus) / pappus < 0.01);
    }

    [Fact]
    public void SweepAlongStraightPath_ExactPrism()
    {
        var square = Profile.FromPoints([(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)]);
        var mesh = BRepTessellator.Tessellate(
            SolidFactory.Sweep(square, new Line3d((0, 0, 0), (0, 0, 3))));
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);
        Assert.Equal(3.0, mesh.Volume(), 6);
    }

    [Fact]
    public void SweepCircleAlongCurvedPath_TubeVolume()
    {
        // Gentle curved Bézier path starting straight up.
        var path = new NurbsCurve(2,
            [(0, 0, 0), (0, 0, 1.5), (0, 1, 3)],
            null,
            [0, 0, 0, 1, 1, 1]);
        double radius = 0.4;
        var profile = Profile.Circle((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, radius);

        var solid = SolidFactory.Sweep(profile, path);
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid, segmentsPerCircle: 48, curveSamples: 48);
        mesh.Validate();

        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);

        double pathLength = 0;
        var previous = path.PointAt(path.Domain.Start);
        for (int i = 1; i <= 1000; i++)
        {
            var p = path.PointAt(path.Domain.ParameterAt(i / 1000.0));
            pathLength += previous.DistanceTo(p);
            previous = p;
        }
        double expected = Math.PI * radius * radius * pathLength;
        Assert.True(Math.Abs(mesh.Volume() - expected) / expected < 0.05,
            $"tube volume {mesh.Volume()} vs π·r²·L = {expected}");
    }

    [Fact]
    public void SweepSquareAlongCurvedPath_ClosedAndPlausible()
    {
        var path = new NurbsCurve(2,
            [(0, 0, 0), (0, 0, 2), (0, 1.2, 4)],
            null,
            [0, 0, 0, 1, 1, 1]);
        var square = Profile.FromPoints(
            [(-0.3, -0.3, 0), (0.3, -0.3, 0), (0.3, 0.3, 0), (-0.3, 0.3, 0)]);

        var mesh = BRepTessellator.Tessellate(SolidFactory.Sweep(square, path), curveSamples: 48);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);
        Assert.True(mesh.Volume() > 0);
    }
}
