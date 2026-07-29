using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The existing verification fixtures, re-run on a mesh with an anisotropic boundary layer in
/// it. <b>A boundary-layer mesh that breaks the solvers that already work is not progress</b>,
/// so the bar is the incumbent tests' own: the patch tests exact to round-off, the cantilever
/// converging on Euler-Bernoulli, the slab exact, equilibrium exact.
///
/// <para><b>The patch tests are the ones that earn their keep here</b>, and it is worth
/// saying why. A layer's prisms are split into tetrahedra, and if two prisms sharing a
/// quadrilateral face picked different diagonals the mesh would be non-conforming — the two
/// halves of that face would not match and every solver would integrate over a gap it cannot
/// see. Nothing about the assembled matrix looks wrong when that happens; the solve converges
/// and returns a plausible answer. But a linear displacement or temperature field can only be
/// reproduced EXACTLY if the elements genuinely tile the body, so a patch test on a layered
/// mesh is a direct measurement of the diagonal rule.</para>
/// </summary>
public class BoundaryLayerSolverTests(ITestOutputHelper output)
{
    private static readonly Material Steel = new(
        "layered steel", 210_000, 0.3, 7.85e-9, thermalConductivity: 45.0, specificHeat: 4.6e8);

    /// <summary>
    /// A box whose faces are already divided into a grid.
    ///
    /// <para><b>Why the fixtures cannot use the plain two-triangles-per-face box.</b> The
    /// layer's interface is FROZEN — the fill may not insert a vertex into a surface the stack
    /// has already built elements against — and Ruppert's encroachment rule then blocks every
    /// interior refinement point inside those triangles' diametral balls. On a plain box each
    /// of those balls is half the box, so nothing refines at all and a "convergence study"
    /// returns the same 42 elements three times. That is not a defect of the fixture, it is
    /// the standing rule of boundary-layer meshing showing through: <b>the surface mesh sets
    /// the layer's in-plane element size</b>, so the wall has to be at the size you want
    /// before the layer is grown.</para>
    /// </summary>
    private static HalfEdgeMesh GriddedBox(Vector3d origin, Vector3d size, int nx, int ny, int nz)
    {
        var index = new Dictionary<(int, int, int), int>();
        var positions = new List<Vector3d>();

        int At(int i, int j, int k)
        {
            if (index.TryGetValue((i, j, k), out int existing))
                return existing;
            int id = positions.Count;
            index[(i, j, k)] = id;
            positions.Add(origin + new Vector3d(
                size.X * i / nx, size.Y * j / ny, size.Z * k / nz));
            return id;
        }

        var faces = new List<int[]>();

        // Each call below lists its quad in the order that is counter-clockwise seen from the
        // POSITIVE side of its axis; emitting them reversed makes every face wind outward,
        // which one Volume() > 0 check at the end confirms rather than assumes.
        void Quad(int a, int b, int c, int d)
        {
            faces.Add([a, d, c]);
            faces.Add([a, c, b]);
        }

        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                Quad(At(i, j, 0), At(i + 1, j, 0), At(i + 1, j + 1, 0), At(i, j + 1, 0));            // -z, inward
                Quad(At(i, j, nz), At(i, j + 1, nz), At(i + 1, j + 1, nz), At(i + 1, j, nz));        // +z
            }
        for (int i = 0; i < nx; i++)
            for (int k = 0; k < nz; k++)
            {
                Quad(At(i, 0, k), At(i, 0, k + 1), At(i + 1, 0, k + 1), At(i + 1, 0, k));            // -y
                Quad(At(i, ny, k), At(i + 1, ny, k), At(i + 1, ny, k + 1), At(i, ny, k + 1));        // +y
            }
        for (int j = 0; j < ny; j++)
            for (int k = 0; k < nz; k++)
            {
                Quad(At(0, j, k), At(0, j + 1, k), At(0, j + 1, k + 1), At(0, j, k + 1));            // -x
                Quad(At(nx, j, k), At(nx, j, k + 1), At(nx, j + 1, k + 1), At(nx, j + 1, k));        // +x
            }

        var mesh = HalfEdgeMesh.Build(positions, faces);
        return mesh.Volume() > 0 ? mesh : throw new InvalidOperationException("gridded box is inverted");
    }

    /// <summary>
    /// A block with a graded layer on its top and bottom faces — the shape every fixture here
    /// uses, because it puts genuinely anisotropic elements on the loaded and restrained
    /// surfaces where they would do the most damage if they were wrong.
    /// </summary>
    private static TetMesh LayeredBlock(
        Vector3d origin, Vector3d size, double first, int layers, double ratio,
        double? elementSize = null, (int X, int Y, int Z)? divisions = null)
    {
        var (dx, dy, dz) = divisions ?? (1, 1, 1);
        var box = GriddedBox(origin, size, dx, dy, dz);
        return TetMesher.Mesh(box, new TetMeshOptions
        {
            RefineQuality = elementSize is not null,
            MaxElementSize = elementSize,
            BoundaryLayer = new BoundaryLayerSpec
            {
                Wall = Facets.Or(
                    Facets.FacingAlong(new Vector3d(0, 0, 1), 10),
                    Facets.FacingAlong(new Vector3d(0, 0, -1), 10)),
                FirstLayerThickness = first,
                LayerCount = layers,
                GrowthRatio = ratio,
            },
        });
    }

    private static AnalysisMesh Wrap(TetMesh tets, ElementOrder order) =>
        order == ElementOrder.Linear ? AnalysisMesh.Of(tets) : AnalysisMesh.Quadratic(tets);

    // ---- structural patch test ----

    private static Vector3d LinearField(Vector3d p) => new(
        1.0e-3 + 2.0e-3 * p.X + 0.7e-3 * p.Y - 0.4e-3 * p.Z,
        -0.5e-3 + 0.3e-3 * p.X - 1.1e-3 * p.Y + 0.9e-3 * p.Z,
        0.2e-3 - 0.6e-3 * p.X + 0.5e-3 * p.Y + 1.4e-3 * p.Z);

    private static SymmetricTensor3 ExpectedStrain() => new(
        2.0e-3, -1.1e-3, 1.4e-3,
        0.5 * (0.7e-3 + 0.3e-3),
        0.5 * (-0.4e-3 - 0.6e-3),
        0.5 * (0.9e-3 + 0.5e-3));

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void StructuralDisplacementPatchTest_IsStillExactOnALayeredMesh(ElementOrder order)
    {
        var tets = LayeredBlock(
            new Vector3d(0, 0, 0), new Vector3d(2.0, 1.5, 1.0),
            first: 0.04, layers: 3, ratio: 1.4, elementSize: 0.45, divisions: (5, 4, 3));
        var mesh = Wrap(tets, order);
        var model = new StructuralModel(mesh, Steel);

        var boundary = model.NodesOn(Facets.All);
        var isBoundary = new bool[mesh.NodeCount];
        foreach (int node in boundary)
        {
            isBoundary[node] = true;
            model.PrescribeNode(node, LinearField(mesh.Position(node)));
        }

        int interior = mesh.NodeCount - boundary.Count;
        Assert.True(interior > 20, $"the patch needs interior nodes to solve for; found {interior}");

        var results = StructuralSolver.Solve(model);

        double reference = 0;
        for (int v = 0; v < mesh.NodeCount; v++)
            reference = Math.Max(reference, LinearField(mesh.Position(v)).Length);

        double worstDisplacement = 0;
        for (int v = 0; v < mesh.NodeCount; v++)
        {
            if (isBoundary[v])
                continue;
            worstDisplacement = Math.Max(worstDisplacement,
                (results.DisplacementAt(v) - LinearField(mesh.Position(v))).Length);
        }

        var expected = ExpectedStrain();
        double worstStrain = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var strain = results.ElementStrain(e);
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    worstStrain = Math.Max(worstStrain, Math.Abs(strain[i, j] - expected[i, j]));
        }

        var quality = TetQuality.Analyze(tets);
        output.WriteLine(
            $"{order}: {mesh.ElementCount:N0} elements ({quality.AnisotropicCount:N0} anisotropic, " +
            $"max stretch {quality.MaxStretch:F1}x), {interior:N0} interior nodes");
        output.WriteLine($"  worst displacement error {worstDisplacement:E3} "
            + $"(relative {worstDisplacement / reference:E3})");
        output.WriteLine($"  worst strain error {worstStrain:E3} (relative {worstStrain / 2.0e-3:E3})");

        Assert.True(quality.AnisotropicCount > 0, "the fixture must actually contain layer elements");
        Assert.True(worstDisplacement <= 1e-9 * reference,
            $"displacement error {worstDisplacement:E3} against field scale {reference:E3}");
        Assert.True(worstStrain <= 1e-9 * 2.0e-3, $"strain error {worstStrain:E3}");
        Assert.True(results.Report.RelativeResidual < 1e-9,
            $"solve residual {results.Report.RelativeResidual:E3}");
    }

    // ---- thermal patch test ----

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void ThermalPatchTest_IsStillExactOnALayeredMesh(ElementOrder order)
    {
        static double Exact(Vector3d p) => 17.5 + 3.25 * p.X - 1.75 * p.Y + 0.875 * p.Z;

        var tets = LayeredBlock(
            new Vector3d(-1, 2, -0.5), new Vector3d(3, 2, 1.5),
            first: 0.05, layers: 3, ratio: 1.4, elementSize: 0.6, divisions: (5, 4, 3));
        var mesh = Wrap(tets, order);
        var model = new ThermalModel(mesh, Steel);
        foreach (int node in model.NodesOn(Facets.All))
            model.TemperatureNode(node, Exact(mesh.Position(node)));

        var results = ThermalSolver.Solve(model);

        double span = 3.25 * 3 + 1.75 * 2 + 0.875 * 1.5;
        double worst = 0;
        for (int v = 0; v < mesh.NodeCount; v++)
            worst = Math.Max(worst, Math.Abs(results.TemperatureAt(v) - Exact(mesh.Position(v))));

        var expectedFlux = new Vector3d(3.25, -1.75, 0.875) * -Steel.ThermalConductivity;
        double worstFlux = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
            worstFlux = Math.Max(worstFlux, (results.ElementFlux(e) - expectedFlux).Length);

        var quality = TetQuality.Analyze(tets);
        output.WriteLine(
            $"{order}: {mesh.ElementCount:N0} elements ({quality.AnisotropicCount:N0} anisotropic, " +
            $"max stretch {quality.MaxStretch:F1}x), {results.Report.FreeDofs:N0} free DOF");
        output.WriteLine($"  worst temperature error {worst:E3} on a span of {span:G6} "
            + $"-> {worst / span:E3} relative");
        output.WriteLine($"  worst element flux error {worstFlux / expectedFlux.Length:E3} relative");
        output.WriteLine($"  energy balance residual {results.Report.EnergyBalanceResidual:E3}");

        Assert.True(quality.AnisotropicCount > 0, "the fixture must actually contain layer elements");
        Assert.True(worst / span < 1e-13, $"temperature error {worst / span:E3}");
        Assert.True(worstFlux / expectedFlux.Length < 1e-12, $"flux error {worstFlux:E3}");
        Assert.True(results.Report.EnergyBalanceResidual < 1e-12);
    }

    // ---- the 1D slab, exact ----

    /// <summary>
    /// Conduction across a slab held at two temperatures: the exact profile is linear, so it
    /// lies in both element spaces and a correct solve reproduces it to round-off. The layer
    /// sits on the two HELD faces, which is where an anisotropic element could do the most
    /// harm to a flux calculation.
    /// </summary>
    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void ThermalSlab_IsStillExactThroughTheLayer(ElementOrder order)
    {
        const double length = 10.0, cold = 20.0, hot = 120.0;
        var tets = LayeredBlock(
            new Vector3d(0, 0, 0), new Vector3d(4, 4, length),
            first: 0.2, layers: 4, ratio: 1.3, elementSize: 1.5, divisions: (3, 3, 7));
        var mesh = Wrap(tets, order);
        var model = new ThermalModel(mesh, Steel);
        model.Temperature(Facets.OnPlane(new Vector3d(0, 0, 0), new Vector3d(0, 0, -1)), cold);
        model.Temperature(Facets.OnPlane(new Vector3d(0, 0, length), new Vector3d(0, 0, 1)), hot);

        var results = ThermalSolver.Solve(model);

        double worst = 0;
        for (int v = 0; v < mesh.NodeCount; v++)
        {
            double exact = cold + (hot - cold) * mesh.Position(v).Z / length;
            worst = Math.Max(worst, Math.Abs(results.TemperatureAt(v) - exact));
        }

        // The exact flux is uniform: -k * dT/dz.
        var expectedFlux = new Vector3d(0, 0, -Steel.ThermalConductivity * (hot - cold) / length);
        double worstFlux = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
            worstFlux = Math.Max(worstFlux, (results.ElementFlux(e) - expectedFlux).Length);

        output.WriteLine($"{order}: {mesh.ElementCount:N0} elements; worst temperature error "
            + $"{worst:E3} on a {hot - cold:G4} K drop -> {worst / (hot - cold):E3} relative, "
            + $"worst flux error {worstFlux / expectedFlux.Length:E3} relative");

        Assert.True(worst / (hot - cold) < 1e-12, $"temperature error {worst:E3}");
        Assert.True(worstFlux / expectedFlux.Length < 1e-10, $"flux error {worstFlux:E3}");
    }

    // ---- the cantilever, converging ----

    /// <summary>
    /// The incumbent cantilever fixture, meshed with a boundary layer on its top and bottom
    /// faces — where a beam's bending stress is largest, so a layer there is the useful place
    /// to put one. The claim is not a fixed number (a layered mesh is a different
    /// discretization, so it lands somewhere else) but that it still CONVERGES on
    /// Euler-Bernoulli from the stiff side as the elements shrink, which is what a
    /// displacement formulation must do.
    /// </summary>
    [Fact]
    public void Cantilever_StillConvergesOnEulerBernoulliThroughALayeredMesh()
    {
        const double length = 100, width = 10, depth = 10, load = 1000;
        double inertia = width * depth * depth * depth / 12.0;
        double analytic = load * length * length * length / (3.0 * Steel.YoungsModulus * inertia);

        var deflections = new List<(int Elements, double Tip)>();
        foreach (int n in new[] { 1, 2, 3 })
        {
            // Refinement is driven by the WALL GRID, not by a sizing field, and that is not a
            // shortcut: the layer's interface is frozen, so a finer sizing field cannot make
            // the elements beside the wall any smaller. Refining the surface is what refines
            // a layered mesh.
            var tets = LayeredBlock(
                new Vector3d(0, 0, 0), new Vector3d(length, width, depth),
                first: 0.3, layers: 3, ratio: 1.5, divisions: (10 * n, n, n));
            var mesh = AnalysisMesh.Quadratic(tets);
            var model = new StructuralModel(mesh, Steel);
            model.Fix(Facets.OnPlane(new Vector3d(0, 0, 0), new Vector3d(-1, 0, 0)));
            model.Force(Facets.OnPlane(new Vector3d(length, 0, 0), new Vector3d(1, 0, 0)),
                        new Vector3d(0, 0, -load));

            var results = StructuralSolver.Solve(model);
            double tip = 0;
            foreach (int node in model.NodesOn(Facets.OnPlane(new Vector3d(length, 0, 0), new Vector3d(1, 0, 0))))
                tip = Math.Min(tip, results.DisplacementAt(node).Z);

            deflections.Add((mesh.ElementCount, -tip));
            var quality = TetQuality.Analyze(tets);
            output.WriteLine(
                $"n={n}: {mesh.ElementCount:N0} elements " +
                $"({quality.AnisotropicCount:N0} anisotropic), tip {-tip:F5} mm, " +
                $"{(-tip / analytic - 1) * 100:+0.00;-0.00}% of Euler-Bernoulli {analytic:F5}");
        }

        // Monotone from below (a displacement formulation is stiff) and approaching.
        for (int i = 1; i < deflections.Count; i++)
        {
            Assert.True(deflections[i].Tip > deflections[i - 1].Tip,
                $"tip deflection went {deflections[i - 1].Tip:F5} -> {deflections[i].Tip:F5}; a refined " +
                "displacement mesh must be softer, not stiffer");
            Assert.True(deflections[i].Tip < analytic * 1.02,
                $"tip {deflections[i].Tip:F5} exceeds Euler-Bernoulli {analytic:F5} by more than 2%");
        }
        Assert.True(deflections[^1].Tip > analytic * 0.99,
            $"finest tip {deflections[^1].Tip:F5} is more than 1% below Euler-Bernoulli {analytic:F5}");
    }

    // ---- equilibrium ----

    /// <summary>
    /// Whatever the elements look like, the reactions must equal the applied load exactly:
    /// that is a property of the assembled system, not of the discretization, and it fails
    /// immediately if the layer's consistent load vectors are wrong.
    /// </summary>
    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void AppliedLoadAndReactionsStillBalanceExactly(ElementOrder order)
    {
        const double pressure = 2.5;
        var tets = LayeredBlock(
            new Vector3d(0, 0, 0), new Vector3d(20, 12, 8),
            first: 0.25, layers: 3, ratio: 1.3, elementSize: 3.0, divisions: (5, 3, 2));
        var mesh = Wrap(tets, order);
        var model = new StructuralModel(mesh, Steel);
        model.Fix(Facets.OnPlane(new Vector3d(0, 0, 0), new Vector3d(-1, 0, 0)));
        model.Pressure(Facets.OnPlane(new Vector3d(0, 0, 8), new Vector3d(0, 0, 1)), pressure);
        model.Gravity(new Vector3d(0, 0, -9810));

        var results = StructuralSolver.Solve(model);
        var applied = model.AppliedForce;
        var reaction = results.Report.ReactionForce;
        double scale = Math.Max(applied.Length, 1e-30);
        double residual = (applied + reaction).Length / scale;

        output.WriteLine($"{order}: applied {applied}, reaction {reaction}, residual {residual:E3}");
        Assert.True(residual < 1e-9, $"equilibrium residual {residual:E3}");
    }

    // ---- the quality report does not cry wolf on correct output ----

    /// <summary>
    /// The report's partition is measured from geometry, with no knowledge of how the mesh was
    /// built — so the strong check is that it agrees with what the mesher actually did. On a
    /// box whose core is a handful of well-shaped elements the two numbers must match
    /// EXACTLY, and the layer must contribute no slivers at all, because the sliver rule is
    /// not applied to a deliberately stretched element.
    /// </summary>
    [Fact]
    public void TheQualityReportSeparatesTheLayerFromTheCore_AndCountsNoSlivers()
    {
        var box = MeshPrimitives.Box(new Aabb(new Vector3d(0, 0, 0), new Vector3d(20, 20, 20)));
        var tets = TetMesher.Mesh(box, new TetMeshOptions
        {
            BoundaryLayer = new BoundaryLayerSpec
            { Wall = Facets.All, FirstLayerThickness = 0.5, LayerCount = 3, GrowthRatio = 1.2 },
        }, out var report);

        var quality = TetQuality.Analyze(tets);
        output.WriteLine(quality.ToText());

        int layerElements = report.BoundaryLayer!.Value.ElementCount;
        Assert.Equal(layerElements, quality.AnisotropicCount);
        Assert.Equal(tets.TetCount - layerElements, quality.IsotropicCount);
        Assert.Equal(0, quality.SliverCount);
        Assert.True(quality.MinStretchedDihedralDegrees > 30,
            $"the layer's un-stretched min dihedral is {quality.MinStretchedDihedralDegrees:F2} deg");
        Assert.True(quality.MaxRadiusEdgeRatio < 2.0,
            $"the CORE's radius-edge is {quality.MaxRadiusEdgeRatio:F3}; the layer's own " +
            $"{quality.MaxAnisotropicRadiusEdgeRatio:F1} must not have leaked into it");
        Assert.True(quality.MaxStretch > 20, $"max stretch {quality.MaxStretch:F1}");
    }

    /// <summary>
    /// And the other half of the same claim: a mesh with NO layer reports exactly what it
    /// always did. The partition can only change numbers where there is something stretched
    /// to partition off.
    /// </summary>
    [Fact]
    public void AnIsotropicMeshReportsTheSameNumbersItAlwaysDid()
    {
        var tets = TetMesher.Mesh(
            MeshPrimitives.Box(new Aabb(new Vector3d(0, 0, 0), new Vector3d(4, 3, 2))),
            new TetMeshOptions { RefineQuality = true, MaxElementSize = 0.9 });

        var partitioned = TetQuality.Analyze(tets);
        var everythingIsotropic = TetQuality.Analyze(
            tets, new TetQualityOptions { AnisotropyThreshold = double.PositiveInfinity });

        output.WriteLine($"{tets.TetCount} elements, max stretch {partitioned.MaxStretch:F2}, " +
                         $"{partitioned.AnisotropicCount} classified anisotropic");
        Assert.Equal(0, partitioned.AnisotropicCount);
        Assert.Equal(everythingIsotropic.SliverCount, partitioned.SliverCount);
        Assert.Equal(everythingIsotropic.MaxRadiusEdgeRatio, partitioned.MaxRadiusEdgeRatio);
        Assert.Equal(everythingIsotropic.MeanRadiusEdgeRatio, partitioned.MeanRadiusEdgeRatio);
        Assert.Equal(everythingIsotropic.WorstElement, partitioned.WorstElement);
    }
}
