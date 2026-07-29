using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Per-region materials: one <see cref="AnalysisBody"/> list drives both the mesh and the
/// model, so a region id is never restated and the materials cannot drift from the
/// geometry.
///
/// <para><b>Two exactly-analytic verifications carry the correctness half</b>, and both are
/// exact rather than convergent because the true field is <i>in</i> the element space.
/// A bar of two materials in series under axial load has a piecewise-linear axial
/// displacement, and — with <b>Poisson's ratio zero in both halves</b> — no lateral
/// coupling at all, so the exact solution is a linear-tet field and the solve reproduces it
/// to round-off at any mesh density. The thermal twin is the same statement about a
/// piecewise-linear temperature through two conductivities. A nonzero nu would put a
/// boundary layer at the interface (the two halves want to contract laterally by different
/// amounts) and there would be no closed form left to check against.</para>
///
/// <para><b>The assertion that catches a region-order bug is the INTERFACE value, not the
/// total.</b> Swapping the two materials leaves the end deflection (and the through-flux)
/// unchanged — series resistances commute — so a test asserting only the total agrees just
/// as happily with the materials the wrong way round.</para>
///
/// <para>The meshes here are <see cref="StructuredTetMesh"/>' Kuhn grids, for the reason
/// that fixture already states and one more: <see cref="TetMesher"/> v1 meshes DISJOINT
/// bodies, and two halves of one bar mate along a face
/// (<see cref="MatingBodies_AreRefusedByName"/> pins the refusal). The plumbing through the
/// real mesher is verified separately, on bodies that are genuinely apart.</para>
/// </summary>
public class MultiMaterialTests
{
    // nu = 0 makes the exact solution a pure axial field; see the class remarks.
    private static readonly Material Stiff = new("stiff", 200_000, 0.0, 7.85e-9, 200.0, 4.6e8);
    private static readonly Material Soft = new("soft", 70_000, 0.0, 2.70e-9, 50.0, 9.0e8);

    private const double Length = 40, Half = 20, Side = 10, Area = Side * Side;

    /// <summary>A bar along X in [0, 40] x [-5, 5]^2, split at x = 20: region 0 is the
    /// near half, region 1 the far half. An even cell count in X puts the interface exactly
    /// on a grid plane, so no element straddles it.</summary>
    private static TetMesh Bar(int nx = 8, int ny = 2, int nz = 2) =>
        StructuredTetMesh.Box(
            new Vector3d(0, -Side / 2, -Side / 2), new Vector3d(Length, Side, Side),
            nx, ny, nz, centroid => centroid.X < Half ? 0 : 1);

    private static IReadOnlyList<AnalysisBody> Halves(Material near, Material far) =>
    [
        new AnalysisBody(MeshPrimitives.Box(
            new Aabb(new Vector3d(0, -5, -5), new Vector3d(Half, 5, 5))), near, "near half"),
        new AnalysisBody(MeshPrimitives.Box(
            new Aabb(new Vector3d(Half, -5, -5), new Vector3d(Length, 5, 5))), far, "far half"),
    ];

    // ---- structural: series stiffness ----

    /// <summary>
    /// delta = (F/A)(L1/E1 + L2/E2). With F = 1000 N over 100 mm^2 and 20 mm of each
    /// material, that is 10 x (20/200000 + 20/70000) = 3.857142857e-3 mm — and the SOLVE
    /// reproduces it to round-off, because the exact field is piecewise linear.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    public void SeriesStiffness_IsExactAtEveryDensity(int nx)
    {
        var bodies = Halves(Stiff, Soft);
        var model = StructuralModel.For(Bar(nx), bodies);
        model.Fix(Facets.Tag(StructuredTetMesh.XMin));
        model.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(1000, 0, 0));

        var results = StructuralSolver.Solve(model);

        double stress = 1000.0 / Area;
        double expected = stress * (Half / Stiff.YoungsModulus + Half / Soft.YoungsModulus);
        Assert.Equal(3.857142857142857e-3, expected, 1e-18);
        Assert.Equal(expected, TipDisplacement(model, results), expected * 1e-10);
    }

    /// <summary>
    /// The interface displacement is what pins region 0 to the NEAR half: it is
    /// (F/A)(L1/E1), which changes when the two materials swap while the tip deflection
    /// does not (series resistances commute). A test asserting only the total would agree
    /// with the materials the wrong way round.
    /// </summary>
    [Fact]
    public void TheInterfaceValuePinsWhichRegionIsWhichBody()
    {
        double stress = 1000.0 / Area;
        double stiffFirst = stress * Half / Stiff.YoungsModulus;   // 1.0e-3
        double softFirst = stress * Half / Soft.YoungsModulus;    // 2.857e-3
        Assert.Equal(1.0e-3, stiffFirst, 1e-15);

        foreach (var (bodies, expected) in new[]
        {
            (Halves(Stiff, Soft), stiffFirst),
            (Halves(Soft, Stiff), softFirst),
        })
        {
            var model = StructuralModel.For(Bar(), bodies);
            model.Fix(Facets.Tag(StructuredTetMesh.XMin));
            model.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(1000, 0, 0));
            var results = StructuralSolver.Solve(model);

            Assert.Equal(expected, InterfaceDisplacement(model, results), expected * 1e-10);
            // ...while the TOTAL is the same either way, which is the point.
            Assert.Equal(
                stress * (Half / Stiff.YoungsModulus + Half / Soft.YoungsModulus),
                TipDisplacement(model, results),
                1e-12);
        }
    }

    private static double TipDisplacement(StructuralModel model, StructuralResults results) =>
        AverageUx(model, results, Length);

    private static double InterfaceDisplacement(StructuralModel model, StructuralResults results) =>
        AverageUx(model, results, Half);

    private static double AverageUx(StructuralModel model, StructuralResults results, double x)
    {
        double sum = 0;
        int count = 0;
        for (int node = 0; node < model.Mesh.NodeCount; node++)
        {
            if (Math.Abs(model.Mesh.Position(node).X - x) > 1e-9)
                continue;
            sum += results.DisplacementAt(node).X;
            count++;
        }
        Assert.True(count > 0, $"no nodes at x = {x}");
        return sum / count;
    }

    // ---- thermal: series resistance ----

    /// <summary>
    /// q = dT / (L1/k1 + L2/k2) = 100 / (20/200 + 20/50) = 200 mW/mm^2, and the interface
    /// sits at 100 - q(L1/k1) = 80 K. Exact for the same reason the bar is: the true
    /// temperature is piecewise linear. The interface temperature again carries the region
    /// order — swapping the conductivities moves it to 20 K while the flux is unchanged.
    /// </summary>
    [Fact]
    public void SeriesThermalResistance_IsExactAndTheInterfacePinsTheOrder()
    {
        double resistance = Half / Stiff.ThermalConductivity + Half / Soft.ThermalConductivity;
        double flux = 100.0 / resistance;
        Assert.Equal(200.0, flux, 1e-12);

        foreach (var (bodies, expected) in new[]
        {
            (Halves(Stiff, Soft), 100.0 - flux * Half / Stiff.ThermalConductivity),   // 80 K
            (Halves(Soft, Stiff), 100.0 - flux * Half / Soft.ThermalConductivity),    // 20 K
        })
        {
            var model = ThermalModel.For(Bar(), bodies);
            model.Temperature(Facets.Tag(StructuredTetMesh.XMin), 100);
            model.Temperature(Facets.Tag(StructuredTetMesh.XMax), 0);
            var results = ThermalSolver.Solve(model);

            double interface20 = AverageTemperature(model, results, Half);
            Assert.Equal(expected, interface20, 1e-9);
        }

        Assert.Equal(80.0, 100.0 - flux * Half / Stiff.ThermalConductivity, 1e-12);
        Assert.Equal(20.0, 100.0 - flux * Half / Soft.ThermalConductivity, 1e-12);
    }

    private static double AverageTemperature(ThermalModel model, ThermalResults results, double x)
    {
        double sum = 0;
        int count = 0;
        for (int node = 0; node < model.Mesh.NodeCount; node++)
        {
            if (Math.Abs(model.Mesh.Position(node).X - x) > 1e-9)
                continue;
            sum += results.TemperatureAt(node);
            count++;
        }
        Assert.True(count > 0, $"no nodes at x = {x}");
        return sum / count;
    }

    // ---- the seam itself: one list drives the mesh AND the model ----

    /// <summary>
    /// The plumbing, through the real mesher: two bodies genuinely apart, meshed from the
    /// list and then modelled from the SAME list. Every element's material is the one its
    /// centroid's body states — which is the property that used to depend on two
    /// separately written lists staying in step.
    /// </summary>
    [Fact]
    public void TheMesherAndTheModelReadOneBodyList()
    {
        IReadOnlyList<AnalysisBody> bodies =
        [
            new AnalysisBody(MeshPrimitives.Box(
                new Aabb(new Vector3d(0, 0, 0), new Vector3d(10, 10, 10))), Stiff, "block"),
            new AnalysisBody(MeshPrimitives.Box(
                new Aabb(new Vector3d(30, 0, 0), new Vector3d(40, 10, 10))), Soft, "plate"),
        ];

        var tets = TetMesher.Mesh(bodies);
        var model = StructuralModel.For(tets, bodies);

        Assert.Equal(new[] { 0, 1 }, tets.Regions);
        for (int e = 0; e < model.Mesh.ElementCount; e++)
        {
            var centroid = Centroid(model.Mesh, e);
            var expected = centroid.X < 20 ? Stiff : Soft;
            Assert.Same(expected, model.MaterialOf(e));
        }

        // The thermal twin reads the same list to the same answer.
        var thermal = ThermalModel.For(tets, bodies);
        for (int e = 0; e < thermal.Mesh.ElementCount; e++)
            Assert.Same(model.MaterialOf(e), thermal.MaterialOf(e));

        // And every assignment is reported, so a model's conditions say what it is made of.
        Assert.Contains(model.Conditions, c => c.Contains("'stiff'") && c.Contains("region 0"));
        Assert.Contains(model.Conditions, c => c.Contains("'soft'") && c.Contains("region 1"));
    }

    private static Vector3d Centroid(AnalysisMesh mesh, int element)
    {
        var nodes = mesh.Element(element);
        var sum = Vector3d.Zero;
        for (int i = 0; i < 4; i++)
            sum += mesh.Position(nodes[i]);
        return sum / 4.0;
    }

    /// <summary>The quadratic spelling reads the same list, so a second-order run needs no
    /// second statement of what anything is made of.</summary>
    [Fact]
    public void TheQuadraticSpellingReadsTheSameList()
    {
        var bodies = Halves(Stiff, Soft);
        var quadratic = QuadraticTetMesh.From(Bar(4));
        var model = StructuralModel.For(quadratic, bodies);

        Assert.Equal(ElementOrder.Quadratic, model.Mesh.Order);
        for (int e = 0; e < model.Mesh.ElementCount; e++)
            Assert.Same(model.Mesh.RegionOf(e) == 0 ? Stiff : Soft, model.MaterialOf(e));
    }

    // ---- refusals, each naming the body ----

    [Fact]
    public void ABodyWithNoMaterial_IsRefusedByName()
    {
        IReadOnlyList<AnalysisBody> bodies =
        [
            new AnalysisBody(MeshPrimitives.Box(Aabb.FromPoints([Vector3d.Zero, new(1, 1, 1)])), Stiff, "near"),
            new AnalysisBody(MeshPrimitives.Box(Aabb.FromPoints([Vector3d.Zero, new(1, 1, 1)])), null, "far half"),
        ];
        var error = Assert.Throws<FeaException>(() => StructuralModel.For(Bar(), bodies));
        Assert.Contains("'far half'", error.Message);
        Assert.Contains("states no material", error.Message);
    }

    [Fact]
    public void AMeshRegionNoBodyDeclares_IsRefusedByName()
    {
        IReadOnlyList<AnalysisBody> bodies =
        [
            new AnalysisBody(MeshPrimitives.Box(Aabb.FromPoints([Vector3d.Zero, new(1, 1, 1)])), Stiff, "only"),
        ];
        var error = Assert.Throws<FeaException>(() => StructuralModel.For(Bar(), bodies));
        Assert.Contains("region 1", error.Message);
        Assert.Contains("body indices", error.Message);
    }

    [Fact]
    public void ADeclaredBodyWithNoElements_IsRefusedByName()
    {
        IReadOnlyList<AnalysisBody> bodies =
        [
            .. Halves(Stiff, Soft),
            new AnalysisBody(MeshPrimitives.Box(Aabb.FromPoints([Vector3d.Zero, new(1, 1, 1)])), Soft, "ghost"),
        ];
        var error = Assert.Throws<FeaException>(() => ThermalModel.For(Bar(), bodies));
        Assert.Contains("'ghost'", error.Message);
        Assert.Contains("contributed no elements", error.Message);
    }

    /// <summary>
    /// The v1 limitation, pinned so it cannot become a silent wrong answer. Two bodies
    /// MATING along a face is the natural way to draw a bi-material part, and it is
    /// refused: welding the shared vertices would mesh, but an inter-body face is never
    /// recovered onto the input plane, so the material boundary would be a jagged surface
    /// of the mesher's choosing rather than the plane that was drawn.
    /// </summary>
    [Fact]
    public void MatingBodies_AreRefusedByName()
    {
        var error = Assert.Throws<TetMeshException>(
            () => TetMesher.Mesh(Halves(Stiff, Soft)));
        Assert.Contains("MATE", error.Message);
        Assert.Contains("disjoint bodies only", error.Message);
        // ...and it names the vertex, so the user can see WHERE the two surfaces meet.
        Assert.Contains("(20,", error.Message);
    }
}
