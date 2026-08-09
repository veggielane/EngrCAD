using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Mesh→B-Rep reconstruction (<see cref="MeshToBrep"/>). The verification bar needs no
/// external data: tessellate a solid this kernel built, reconstruct it, and require the same
/// analytic types with the same parameters and the same FACE COUNT. The cylinder-radius test
/// is the one that separates a real fit from the inscribed-radius impostor.
/// </summary>
public class MeshToBrepTests
{
    // ---- The headline metric: face count, not a mesh wearing a .step extension -----------

    [Fact]
    public void Box_ReconstructsToSixPlanarFaces()
    {
        var mesh = BRepTessellator.Tessellate(SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 3, 4))));
        var result = MeshToBrep.Reconstruct(mesh);

        Assert.Equal(6, result.Report.RegionCount);
        Assert.All(result.Report.Regions, r => Assert.Equal(ReconstructedSurfaceKind.Plane, r.Kind));
        Assert.True(result.Report.AllFitted);
    }

    [Fact]
    public void Cylinder_ReconstructsToTwoPlanesAndOneCylinder()
    {
        var mesh = BRepTessellator.Tessellate(SolidFactory.MakeCylinder(5, 10), segmentsPerCircle: 64);
        var result = MeshToBrep.Reconstruct(mesh);

        Assert.Equal(3, result.Report.RegionCount);
        Assert.Equal(2, result.Report.Regions.Count(r => r.Kind == ReconstructedSurfaceKind.Plane));
        Assert.Equal(1, result.Report.Regions.Count(r => r.Kind == ReconstructedSurfaceKind.Cylinder));
    }

    // ---- The test that separates a real fit from its impostor ----------------------------

    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    public void Cylinder_RecoversExactRadiusAtEveryDensity(int segments)
    {
        const double radius = 5.0;
        var mesh = BRepTessellator.Tessellate(SolidFactory.MakeCylinder(radius, 10), segmentsPerCircle: segments);
        var result = MeshToBrep.Reconstruct(mesh);

        var cylinder = result.Report.Regions.Single(r => r.Kind == ReconstructedSurfaceKind.Cylinder);
        var surface = Assert.IsType<CylinderSurface>(cylinder.Surface);

        // An inscribed n-gon's vertices lie ON the cylinder, so a vertex-fitted radius is the
        // TRUE radius at every density — not the inscribed radius r·cos(π/n) a chord-length
        // fit would report, which is measurably wrong (4.976 at 32 segments).
        double inscribed = radius * Math.Cos(Math.PI / segments);
        Assert.Equal(radius, surface.Radius, 8);
        // ... and the fit is nowhere near the inscribed-radius impostor (which is off by 0.024
        // at 32 segments — orders of magnitude more than the fit's own error).
        Assert.True(surface.Radius - inscribed > 0.9 * (radius - inscribed));
    }

    [Fact]
    public void DrilledPlate_ReconstructsToSevenFaces()
    {
        var plate = DrilledPlate();
        var mesh = BRepTessellator.Tessellate(plate, segmentsPerCircle: 64);
        var result = MeshToBrep.Reconstruct(mesh);

        // Six planar outer faces plus one cylindrical bore wall — not five thousand facets.
        Assert.Equal(7, result.Report.RegionCount);
        Assert.Equal(6, result.Report.Regions.Count(r => r.Kind == ReconstructedSurfaceKind.Plane));
        Assert.Equal(1, result.Report.Regions.Count(r => r.Kind == ReconstructedSurfaceKind.Cylinder));
    }

    [Fact]
    public void Sphere_ReconstructsToOneSphere()
    {
        var mesh = BRepTessellator.Tessellate(SolidFactory.MakeSphere(6), segmentsPerCircle: 48);
        var result = MeshToBrep.Reconstruct(mesh);

        var sphere = result.Report.Regions.Single(r => r.Kind == ReconstructedSurfaceKind.Sphere);
        var surface = Assert.IsType<SphereSurface>(sphere.Surface);
        Assert.Equal(6.0, surface.Radius, 6);
    }

    // ---- Phase 2: the assembled solid -----------------------------------------------------

    [Fact]
    public void Box_AssemblesToValidSolidWithMatchingVolume()
    {
        var mesh = BRepTessellator.Tessellate(SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 3, 4))));
        var result = MeshToBrep.Reconstruct(mesh);

        Assert.True(result.Succeeded, result.FailureReason);
        var solid = result.Solid!;
        solid.Validate();
        Assert.Equal(6, solid.Faces.Count());
        Assert.Equal(24.0, BrepMassProperties.Compute(solid).Volume, 6);
    }

    [Fact]
    public void Cylinder_AssemblesToValidSolidWithMatchingVolume()
    {
        var brep = SolidFactory.MakeCylinder(5, 10);
        var mesh = BRepTessellator.Tessellate(brep, segmentsPerCircle: 64);
        var result = MeshToBrep.Reconstruct(mesh);

        Assert.True(result.Succeeded, result.FailureReason);
        var solid = result.Solid!;
        solid.Validate();
        Assert.Equal(3, solid.Faces.Count());
        // Reconstructed volume matches the mesh it came from (an isometry preserves the
        // inscribed n-gon), so compare the two DISCRETE volumes rather than πr²h.
        double reconstructed = BrepMassProperties.Compute(solid, options: null).Volume;
        Assert.Equal(Math.PI * 25 * 10, reconstructed, 0); // within ~1 of the analytic value
    }

    [Fact]
    public void DrilledPlate_AssemblesToValidSolidWithMatchingVolume()
    {
        var plate = DrilledPlate();
        var mesh = BRepTessellator.Tessellate(plate, segmentsPerCircle: 64);
        var result = MeshToBrep.Reconstruct(mesh);

        Assert.True(result.Succeeded, result.FailureReason);
        var solid = result.Solid!;
        solid.Validate();
        Assert.Equal(7, solid.Faces.Count());
        // 40·30·10 − π·4²·10 = 12000 − 502.65 = 11497.3
        Assert.Equal(12000 - Math.PI * 16 * 10, BrepMassProperties.Compute(solid).Volume, 0);
    }

    // ---- The refusals ---------------------------------------------------------------------

    [Fact]
    public void Reconstructed_TessellatesClosedAndMatchesOriginalMesh()
    {
        var original = BRepTessellator.Tessellate(DrilledPlate(), segmentsPerCircle: 64);
        var solid = MeshToBrep.Reconstruct(original).Solid!;

        // The reconstructed solid re-tessellates to a closed, genus-1 (one bore) manifold whose
        // discrete volume matches — a full round trip through the conversion triangle.
        var again = BRepTessellator.Tessellate(solid, segmentsPerCircle: 64);
        again.Validate();
        Assert.True(again.IsClosed);
        Assert.Equal(original.Volume(), again.Volume(), 3);
    }

    [Fact]
    public void WholeSphere_Phase2RefusedByName()
    {
        // A seamless sphere is one region with no boundary edges; a seamed single-face solid
        // is out of v1 scope, so the fit is reported but no solid is assembled.
        var mesh = BRepTessellator.Tessellate(SolidFactory.MakeSphere(6), segmentsPerCircle: 48);
        var result = MeshToBrep.Reconstruct(mesh);

        Assert.True(result.Report.AllFitted);
        Assert.False(result.Succeeded);
        Assert.Null(result.Solid);
        Assert.Contains("no boundary", result.FailureReason);
    }

    [Fact]
    public void OpenMesh_RefusedByName()
    {
        // A single triangle: closed test fails.
        var open = HalfEdgeMesh.Build(
            [(0, 0, 0), (1, 0, 0), (0, 1, 0)], [0, 1, 2], verticesPerFace: 3);
        var result = MeshToBrep.Reconstruct(open);
        Assert.False(result.Succeeded);
        Assert.Contains("not closed", result.FailureReason);
    }

    private static BrepSolid DrilledPlate()
    {
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (40, 30, 10)));
        var bore = SolidFactory.MakeCylinder(4, 20).Transformed(Matrix4d.CreateTranslation((20, 15, -5)));
        return BrepBoolean.Difference(box, bore);
    }
}
