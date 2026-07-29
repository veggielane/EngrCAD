using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Shelled and offset solids through the tessellator. Every face stays planar, so these
/// volumes are exact — a shelled box's wall volume is arithmetic, not an approximation.
/// </summary>
public class ShellingTessellationTests
{
    private static BrepSolid Block() => SolidFactory.MakeBox(new Aabb((0, 0, 0), (20, 30, 10)));

    private static BrepFace TopOf(BrepSolid solid) =>
        solid.PlanarFacesWithNormal(Vector3d.UnitZ).Single();

    [Fact]
    public void Shell_BoxWithTopOpen_HasTheExactTrayVolume()
    {
        var block = Block();
        var tray = Shelling.Shell(block, 2, f => ReferenceEquals(f, TopOf(block)));
        var mesh = BRepTessellator.Tessellate(tray);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);

        // Outer 20 x 30 x 10 minus a cavity 16 x 26 x 8 that reaches the open top.
        Assert.Equal(20.0 * 30 * 10 - 16.0 * 26 * 8, mesh.Volume(), 9);
    }

    [Fact]
    public void Shell_SealedVoid_HasTheExactWallVolume()
    {
        var mesh = BRepTessellator.Tessellate(Shelling.Shell(Block(), 2));
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        // Two closed surfaces: outer boundary plus the void.
        Assert.Equal(4, mesh.EulerCharacteristic);
        Assert.Equal(20.0 * 30 * 10 - 16.0 * 26 * 6, mesh.Volume(), 9);
    }

    [Fact]
    public void Shell_OpenBothEnds_IsAnExactRectangularTube()
    {
        var block = Block();
        var top = TopOf(block);
        var bottom = block.PlanarFacesWithNormal(-Vector3d.UnitZ).Single();
        var tube = Shelling.Shell(block, 2, f => ReferenceEquals(f, top) || ReferenceEquals(f, bottom));
        var mesh = BRepTessellator.Tessellate(tube);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(0, mesh.EulerCharacteristic); // genus 1
        Assert.Equal(20.0 * 30 * 10 - 16.0 * 26 * 10, mesh.Volume(), 9);
    }

    [Fact]
    public void Offset_Box_HasTheExactGrownVolume()
    {
        var mesh = BRepTessellator.Tessellate(
            Shelling.Offset(SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 3, 4))), 0.5));
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(3.0 * 4 * 5, mesh.Volume(), 9);
    }

    // ---- curved faces: volumes against closed forms, convergence measured ----

    [Fact]
    public void Shell_CylinderOpenTop_ConvergesOnTheAnalyticCupVolume()
    {
        const double radius = 5, height = 10, wall = 1;
        // A cup: the full cylinder minus the cavity, which is a cylinder of radius 4 from
        // z = 1 to the open top.
        double exact = Math.PI * radius * radius * height
                     - Math.PI * (radius - wall) * (radius - wall) * (height - wall);

        double previous = 0;
        foreach (int segments in (ReadOnlySpan<int>)[32, 64, 128])
        {
            var cylinder = SolidFactory.MakeCylinder(radius, height);
            var top = cylinder.PlanarFacesWithNormal(Vector3d.UnitZ).Single();
            var cup = Shelling.Shell(cylinder, wall, f => ReferenceEquals(f, top));
            var mesh = BRepTessellator.Tessellate(cup, segments, segments / 4);
            mesh.Validate();
            Assert.True(mesh.IsClosed);
            Assert.Equal(2, mesh.EulerCharacteristic);

            // Inscribed n-gons, so the tessellation is always UNDER the analytic volume.
            double deficit = exact - mesh.Volume();
            Assert.True(deficit > 0, $"an inscribed tessellation cannot exceed the analytic volume ({deficit})");
            if (previous > 0)
                Assert.True(previous / deficit > 3.5,
                    $"quadratic convergence expected; ratio was {previous / deficit:0.00}");
            previous = deficit;
        }
    }

    [Fact]
    public void Shell_CylinderSealed_IsTwoShellsWithTheExactWallVolume()
    {
        const double radius = 5, height = 10, wall = 1;
        double exact = Math.PI * radius * radius * height
                     - Math.PI * (radius - wall) * (radius - wall) * (height - 2 * wall);

        var mesh = BRepTessellator.Tessellate(
            Shelling.Shell(SolidFactory.MakeCylinder(radius, height), wall), 128, 32);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(4, mesh.EulerCharacteristic); // outer boundary plus a sealed void
        Assert.Equal(exact, mesh.Volume(), 0.1 * exact / 100);  // within 0.1% (a wall volume is a DIFFERENCE of two inscribed cylinders, so the two deficits add rather than cancel)
    }

    [Fact]
    public void Shell_ConeFrustumOpenTop_HasTheAnalyticWallVolume()
    {
        // A conical cup. The cavity is the inner frustum: its radii and height follow from
        // the perpendicular offset, so this is a closed form and not a fitted number.
        const double bottom = 10, top = 4, height = 12, wall = 1.5;
        // The cavity runs from the raised floor (z = wall) to the open top (z = height), with
        // its radii taken off the inward-offset generator at those two heights.
        double innerHeight = height - wall;
        double innerBottom = OffsetConeRadius(bottom, top, height, wall, wall);
        double innerTop = OffsetConeRadius(bottom, top, height, wall, height);

        double outerVolume = Math.PI * height / 3 * (bottom * bottom + bottom * top + top * top);
        double cavity = Math.PI * innerHeight / 3
            * (innerBottom * innerBottom + innerBottom * innerTop + innerTop * innerTop);

        var cone = SolidFactory.MakeCone(bottom, top, height);
        var cap = cone.PlanarFacesWithNormal(Vector3d.UnitZ).Single();
        var mesh = BRepTessellator.Tessellate(
            Shelling.Shell(cone, wall, f => ReferenceEquals(f, cap)), 192, 48);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);

        double expected = outerVolume - cavity;
        Assert.Equal(expected, mesh.Volume(), 0.1 * expected / 100); // within 0.1%
    }

    /// <summary>Radius of the inward-offset cone's generator at a given height.</summary>
    private static double OffsetConeRadius(double bottom, double top, double height, double wall, double z)
    {
        // Generator (r, z) from (bottom, 0) to (top, height); outward 2D normal is (dz, -dr)
        // normalized, so an inward offset moves the line by -wall along it.
        double dr = top - bottom, dz = height;
        double length = Math.Sqrt(dr * dr + dz * dz);
        double nr = dz / length, nz = -dr / length;
        // Offset line through (bottom, 0) - wall*(nr, nz), same direction: solve for r at z.
        double r0 = bottom - wall * nr, z0 = -wall * nz;
        return r0 + dr * (z - z0) / dz;
    }

    [Fact]
    public void Shell_PipeElbowOpenBothEnds_IsAGenusOneTubeWithThePappusVolume()
    {
        const double major = 20, tube = 5, wall = 1;
        double sweep = Math.PI / 2;
        // Pappus: the swept area times the distance its centroid travels.
        double exact = sweep * major * Math.PI * (tube * tube - (tube - wall) * (tube - wall));

        var elbow = Elbow(major, tube, sweep);
        var caps = elbow.Faces.Where(f => f.Surface is PlaneSurface).ToList();
        var mesh = BRepTessellator.Tessellate(
            Shelling.Shell(elbow, wall, caps.Contains), 128, 32);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(0, mesh.EulerCharacteristic); // genus 1: a tube
        Assert.Equal(exact, mesh.Volume(), 0.5 * exact / 100); // within 0.5% (a thin wall on a coarse torus grid)
    }

    [Fact]
    public void Offset_Sphere_ConvergesOnTheGrownSphereVolume()
    {
        var mesh = BRepTessellator.Tessellate(Shelling.Offset(SolidFactory.MakeSphere(3), 1.5), 192, 96);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        double exact = 4.0 / 3 * Math.PI * 4.5 * 4.5 * 4.5;
        Assert.Equal(exact, mesh.Volume(), 0.05 * exact / 100);
    }

    private static BrepSolid Elbow(double majorRadius, double tubeRadius, double sweep)
    {
        var tubeCentre = new Vector3d(majorRadius, 0, 0);
        var outerArc = NurbsCurve.Arc(
            tubeCentre, Vector3d.UnitX, Vector3d.UnitZ, tubeRadius, -Math.PI / 2, Math.PI / 2);
        var innerArc = NurbsCurve.Arc(
            tubeCentre, Vector3d.UnitX, Vector3d.UnitZ, tubeRadius, Math.PI / 2, 3 * Math.PI / 2);
        return SolidFactory.Revolve(
            new Profile([outerArc, innerArc]), Vector3d.Zero, Vector3d.UnitZ, sweep);
    }

    [Fact]
    public void Offset_PlateWithHole_ShrinksTheOutsideAndGrowsTheHole()
    {
        var plate = Profile.FromPoints([(0, 0, 0), (20, 0, 0), (20, 20, 0), (0, 20, 0)]);
        var hole = Profile.FromPoints([(8, 8, 0), (12, 8, 0), (12, 12, 0), (8, 12, 0)]);
        var solid = SolidFactory.Extrude(plate, (0, 0, 5), holes: [hole]);
        var mesh = BRepTessellator.Tessellate(Shelling.Offset(solid, -1));
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(0, mesh.EulerCharacteristic); // genus 1 survives the offset
        // 18 x 18 outer, 6 x 6 hole, 3 thick.
        Assert.Equal((18.0 * 18 - 6.0 * 6) * 3, mesh.Volume(), 9);
    }
}
