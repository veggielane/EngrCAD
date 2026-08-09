using EngrCAD.Fea;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// <see cref="TetMeshDiagnostics.MinBoundaryFacetSize"/> — the report field that answers
/// "why did a coarse MaxElementSize still give a big mesh". See
/// <see cref="MaxElementSizeMeasurement"/> for the sweep that motivated it: MaxElementSize
/// only SPLITS facets larger than its target and never coarsens a finely tessellated feature,
/// so where the surface is finer than MaxElementSize, the surface — reported here — sets the
/// element-size floor.
/// </summary>
public class MinBoundaryFacetSizeTests
{
    [Fact]
    public void ReportsAPositiveFloorAndNamesItInToString()
    {
        var surface = Shape.Box(20, 20, 10).ToMesh();
        TetMesher.Mesh(surface,
            new TetMeshOptions { RefineQuality = true, MaxElementSize = 8 }, out var diag);

        // The finest boundary facet is refined below the size target, so it is a positive
        // number no larger than the cap.
        Assert.True(diag.MinBoundaryFacetSize > 0, "a mesh with boundary facets has a finest one");
        Assert.True(diag.MinBoundaryFacetSize <= 8, "boundary refinement does not exceed the size target");
        Assert.Contains("Finest boundary facet", diag.ToString());
    }

    [Fact]
    public void AFineBoreFloorsTheSizeFarBelowACoarseMaxElementSize()
    {
        // A Ø8 bore tessellated at 12 seg/circle gives ~1 mm wall facets. Ask for a very
        // COARSE mesh (MaxElementSize = 20): the surface, not the request, sets the floor.
        var surface = Shape.Box(60, 20, 8).Subtract(Shape.Cylinder(4, 40))
            .ToMesh(new MeshQuality { SegmentsPerCircle = 12, CurveSamples = 12 });

        TetMesher.Mesh(surface,
            new TetMeshOptions { RefineQuality = true, MaxElementSize = 20 }, out var diag);

        // The floor is set by the bore facets, an order of magnitude below the request — the
        // measured "MaxElementSize does not bound the element count of a coarse request".
        Assert.True(diag.MinBoundaryFacetSize < 4,
            $"the bore floor should be well below MaxElementSize=20; was {diag.MinBoundaryFacetSize}");
        // ...and its density is carried by BOUNDARY refinement, not interior quality points.
        Assert.True(diag.BoundarySteinerPoints > diag.QualitySteinerPoints);
    }
}
