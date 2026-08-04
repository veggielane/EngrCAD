using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Heat flux across a BONDED MATERIAL INTERFACE, where <c>q = -k·grad T</c> is genuinely
/// discontinuous and one value per node is therefore one too few — the thermal twin of
/// <see cref="MaterialInterfaceRecoveryTests"/>, over the same
/// <c>AnalysisMesh</c> (node, region) slot table.
///
/// <para><b>The job is smaller than the structural one for a reason worth stating.</b> There
/// is no recovery on the thermal path at all — <c>ThermalResults</c> averages element values
/// at each node and nothing else — so nothing here has REACH, which was the whole argument
/// for the structural fix (one cross-interface patch corrupts nodes a full element layer
/// inside each material). Averaging is local: only the interface node itself blends. So the
/// defect is confined to those nodes, and the fix is the accessor that reports the honest
/// value there.</para>
///
/// <para><b>Which arrangement can SEE it is the fixture question, and the series bar cannot.</b>
/// At an interface the temperature is continuous, so the TANGENTIAL gradient is continuous and
/// the tangential flux jumps with <c>k</c>, while the NORMAL flux is continuous by
/// conservation. A SERIES stack (the interface perpendicular to the flow) has a purely normal
/// flux, so both halves carry the same vector — this project's recorded two-material thermal
/// fixture measures exactly 200 mW/mm^2 through both — and any averaging reproduces it.
/// <see cref="ASeriesBarCannotSeeTheDefect"/> asserts that, so the choice is documented rather
/// than merely made.</para>
///
/// <para><b>The arrangement with a jump is PARALLEL</b> — two materials side by side, the
/// interface CONTAINING the flow direction. Both see the same end temperatures over the same
/// length, so both carry the same gradient and the flux jumps with the conductivity. The exact
/// solution is <c>T = T_hot·(1 - x/L)</c>: it is linear, so every element order reproduces it
/// exactly; its divergence vanishes in each material; and the interface normal is perpendicular
/// to the flow, so <c>q·n = 0</c> on both sides and the interface condition holds identically.
/// The per-material flux is therefore exactly <c>k_i·dT/L</c> and the bar is an EQUALITY rather
/// than a rate.</para>
///
/// <para><b>And the recorded fixture trap recurs here verbatim</b>: prescribing that linear
/// field on the whole boundary of a SERIES bar imposes the same gradient on both halves, which
/// is the parallel condition wearing the series arrangement's geometry — not a solution of the
/// series problem at all. <see cref="ASeriesBarCannotSeeTheDefect"/> measures both loadings, so
/// the distinction is a number rather than a claim in prose.</para>
///
/// <para><b>Reachability, as for the structural twin.</b> A connected multi-region mesh is not
/// constructible from the public API today (<c>TetMesher</c> refuses MATING bodies, disjoint
/// ones share no node, <c>TetMesh</c>'s region-carrying constructor is internal), which
/// <see cref="MaterialInterfaceRecoveryTests.DisjointBodiesShareNoNodeSoThereIsNoInterface"/>
/// pins on the mesh; <see cref="DisjointBodiesReadTheSameFluxThroughBothAccessors"/> pins the
/// thermal half of it, and these fixtures are structured meshes with regions assigned by
/// position exactly as <see cref="MultiMaterialTests"/> builds its bars.</para>
/// </summary>
public class ThermalInterfaceFluxTests(ITestOutputHelper output)
{
    // Only the conductivity matters to a steady conduction solve; the elastic and density
    // columns are here because the catalogue-style constructor takes them.
    private static readonly Material Conductive = new("conductive", 200_000, 0.3, 7.85e-9, 200.0, 4.6e8);
    private static readonly Material Insulating = new("insulating", 70_000, 0.3, 2.70e-9, 50.0, 9.0e8);

    private const double Side = 4.0;
    private const double Half = Side / 2;
    private const int Divisions = 4;
    private const double HotEnd = 100.0;

    /// <summary>The exact flux in a region: k·dT/L along +x, since T falls with x.</summary>
    private static double ExactFlux(int region) =>
        (region == 0 ? Conductive : Insulating).ThermalConductivity * (HotEnd / Side);

    /// <summary>The PARALLEL arrangement: a cube split at y = 2, so the interface plane
    /// CONTAINS the flow direction. An even division count in y puts it exactly on a grid
    /// plane, so no element straddles it.</summary>
    private static TetMesh ParallelCube() => StructuredTetMesh.Box(
        Vector3d.Zero, new Vector3d(Side, Side, Side), Divisions, Divisions, Divisions,
        centroid => centroid.Y < Half ? 0 : 1);

    /// <summary>The SERIES arrangement: split along the flow direction instead.</summary>
    private static TetMesh SeriesCube() => StructuredTetMesh.Box(
        Vector3d.Zero, new Vector3d(Side, Side, Side), Divisions, Divisions, Divisions,
        centroid => centroid.X < Half ? 0 : 1);

    /// <summary>Conduction along x driven by the two END faces only, with the other four
    /// adiabatic — which is what "the same dT over the same L" means, and the loading the
    /// parallel arrangement is defined by.</summary>
    private static ThermalResults ThroughFlow(
        TetMesh tets, ElementOrder order, out ThermalModel model)
    {
        var mesh = order == ElementOrder.Linear
            ? AnalysisMesh.Of(tets)
            : AnalysisMesh.Quadratic(tets);
        model = new ThermalModel(mesh, Conductive).SetMaterial(1, Insulating);
        model.Temperature(Facets.Tag(StructuredTetMesh.XMin), HotEnd);
        model.Temperature(Facets.Tag(StructuredTetMesh.XMax), 0);
        return ThermalSolver.Solve(model);
    }

    // ---- the fix: every node is exact in every material it touches ----

    /// <summary>
    /// The strongest form the claim takes: the exact flux field is piecewise CONSTANT, so the
    /// per-region value must come back EXACTLY — in both materials, at every node, including
    /// the interface nodes themselves, where the answer is per-region and there are two of them.
    /// </summary>
    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void EveryNodeIsExactInEveryMaterialItTouches(ElementOrder order)
    {
        var results = ThroughFlow(ParallelCube(), order, out var model);
        var mesh = model.Mesh;

        // The configuration is CARRIED: without interface nodes this test measures nothing.
        Assert.True(mesh.InterfaceNodeCount > 0, "the fixture has no material interface");

        double worst = 0;
        int checkedSlots = 0;
        for (int node = 0; node < mesh.NodeCount; node++)
        {
            foreach (int region in mesh.RegionsAt(node))
            {
                double expected = ExactFlux(region);
                var got = results.NodalFluxIn(region, node);
                worst = Math.Max(worst, Math.Abs(got.X - expected) / expected);
                // The transverse components are exactly zero in the exact field, so they are
                // measured against the SAME scale rather than against themselves.
                worst = Math.Max(worst, Math.Abs(got.Y) / expected);
                worst = Math.Max(worst, Math.Abs(got.Z) / expected);
                checkedSlots++;
            }
        }

        output.WriteLine(
            $"{order}: {mesh.NodeCount} nodes, {checkedSlots} (node, region) slots, "
            + $"{mesh.InterfaceNodeCount} on the interface; exact flux "
            + $"{ExactFlux(0):F0} / {ExactFlux(1):F0} mW/mm^2; worst relative error {worst:E3}");
        Assert.True(worst < 1e-9, $"per-region flux is not exact: {worst:E3}");
        Assert.Equal(mesh.NodeCount + mesh.InterfaceNodeCount, checkedSlots);
    }

    // ---- the decision: what the node-indexed field reports ----

    /// <summary>
    /// The decision, asserted, and the defect MEASURED. <c>NodalFlux</c> is indexed by node, so
    /// at an interface node it holds one value where the physics has two and reports a blend —
    /// strictly between the materials rather than either one, and therefore measurably wrong
    /// for BOTH. Away from the interface the blend is a blend of one value, so it is the
    /// per-region value BIT for bit, which is what makes the two accessors safe to mix.
    /// </summary>
    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void TheNodeIndexedFieldBlendsOnlyWhereItMust(ElementOrder order)
    {
        var results = ThroughFlow(ParallelCube(), order, out var model);
        var mesh = model.Mesh;
        var nodal = results.NodalFlux;

        double conductive = ExactFlux(0), insulating = ExactFlux(1);
        int blended = 0;
        double worstBlendError = 0, gentlestBlendError = double.MaxValue;
        for (int node = 0; node < mesh.NodeCount; node++)
        {
            var regions = mesh.RegionsAt(node);
            if (regions.Length == 1)
            {
                // Bit for bit, not within a tolerance: the blend of one value must BE it.
                var perRegion = results.NodalFluxIn(regions[0], node);
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(perRegion.X),
                    BitConverter.DoubleToInt64Bits(nodal[node].X));
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(perRegion.Y),
                    BitConverter.DoubleToInt64Bits(nodal[node].Y));
                continue;
            }

            blended++;
            Assert.True(nodal[node].X > insulating && nodal[node].X < conductive,
                $"node {node} reports {nodal[node].X:F3}, not between {insulating} and {conductive}");
            Assert.Equal(conductive, results.NodalFluxIn(0, node).X, conductive * 1e-9);
            Assert.Equal(insulating, results.NodalFluxIn(1, node).X, insulating * 1e-9);

            // How wrong the one value is for the material that has to live with it: the
            // measurement that makes the accessor worth having rather than a nicety.
            double worseOfTheTwo = Math.Max(
                Math.Abs(nodal[node].X - conductive) / conductive,
                Math.Abs(nodal[node].X - insulating) / insulating);
            worstBlendError = Math.Max(worstBlendError, worseOfTheTwo);
            gentlestBlendError = Math.Min(gentlestBlendError, worseOfTheTwo);
        }

        output.WriteLine(
            $"{order}: {blended} of {mesh.NodeCount} nodes blend two materials "
            + $"({insulating} and {conductive} mW/mm^2); the single value they report is wrong "
            + $"by {gentlestBlendError:P1} to {worstBlendError:P1} for one of the two");
        Assert.Equal(mesh.InterfaceNodeCount, blended);
        // The blend is a REAL error, not a rounding difference — otherwise the per-region
        // accessor would be answering a question nobody has.
        Assert.True(gentlestBlendError > 0.1,
            $"the blend is only {gentlestBlendError:P2} wrong; this fixture proves nothing");
    }

    // ---- the recorded oracle, and why it is not this one ----

    /// <summary>
    /// The series bar — the arrangement this project's recorded two-material thermal fixture
    /// uses — has a CONTINUOUS flux field: conservation makes the normal flux equal on both
    /// sides, and in series that is the whole vector. Both accessors therefore agree exactly
    /// and the comparison is empty. Asserted, so that fixture cannot be reached for again.
    ///
    /// <para>The second half measures the TRAP. Prescribing the parallel arrangement's linear
    /// temperature field on the whole boundary of a series bar forces the same gradient on both
    /// halves, which is the parallel condition in disguise and not a solution of the series
    /// problem — and it does show a jump, so a fixture loaded that way would look like it was
    /// proving the rule while measuring its own boundary data.</para>
    /// </summary>
    [Fact]
    public void ASeriesBarCannotSeeTheDefect()
    {
        // Series resistance: L1/k1 + L2/k2 over the full temperature drop.
        double resistance = Half / Conductive.ThermalConductivity
                          + Half / Insulating.ThermalConductivity;
        double uniform = HotEnd / resistance;
        Assert.Equal(2000.0, uniform, 1e-9);

        var results = ThroughFlow(SeriesCube(), ElementOrder.Linear, out var model);
        var mesh = model.Mesh;
        var nodal = results.NodalFlux;

        double worstAgainstExact = 0, worstBetweenAccessors = 0;
        for (int node = 0; node < mesh.NodeCount; node++)
        {
            worstAgainstExact = Math.Max(worstAgainstExact, Math.Abs(nodal[node].X - uniform));
            foreach (int region in mesh.RegionsAt(node))
            {
                worstBetweenAccessors = Math.Max(
                    worstBetweenAccessors,
                    Math.Abs(results.NodalFluxIn(region, node).X - nodal[node].X));
            }
        }

        // The trap: the parallel arrangement's loading, on the series arrangement's geometry.
        double trapJump = SeriesBarUnderTheParallelLoading();

        output.WriteLine(
            $"series bar: the exact flux is {uniform} mW/mm^2 in BOTH halves; the node-indexed "
            + $"field is off by {worstAgainstExact:E3} and the two accessors differ by "
            + $"{worstBetweenAccessors:E3} ({worstBetweenAccessors / uniform:P4}) - the "
            + "arrangement carries no information about the interface rule. Driven WRONGLY, by "
            + $"prescribing the linear field on the whole boundary, the same geometry reports a "
            + $"{trapJump:P1} jump.");

        Assert.True(worstAgainstExact < 1e-6 * uniform,
            $"the series flux is not uniform ({worstAgainstExact:E3} of {uniform})");
        Assert.True(worstBetweenAccessors < 1e-6 * uniform,
            $"the series bar DOES see the rule ({worstBetweenAccessors:E3} of {uniform}); the "
            + "class remarks claim it cannot, so one of them is wrong");
        Assert.True(trapJump > 0.1,
            $"the mis-loaded series bar shows only a {trapJump:P2} jump, so the trap the class "
            + "remarks warn about is not reproduced and the warning has gone stale");
    }

    /// <summary>The mis-loaded series bar: the largest relative gap between the two accessors
    /// at any interface node, when the linear field is prescribed on the WHOLE boundary.</summary>
    private static double SeriesBarUnderTheParallelLoading()
    {
        var mesh = AnalysisMesh.Of(SeriesCube());
        var model = new ThermalModel(mesh, Conductive).SetMaterial(1, Insulating);
        foreach (int node in model.NodesOn(Facets.All))
            model.TemperatureNode(node, HotEnd * (1 - mesh.Position(node).X / Side));
        var results = ThermalSolver.Solve(model);

        double worst = 0;
        for (int node = 0; node < mesh.NodeCount; node++)
        {
            var regions = mesh.RegionsAt(node);
            if (regions.Length < 2)
                continue;
            double a = results.NodalFluxIn(regions[0], node).X;
            double b = results.NodalFluxIn(regions[1], node).X;
            worst = Math.Max(worst, Math.Abs(a - b) / Math.Max(Math.Abs(a), Math.Abs(b)));
        }
        return worst;
    }

    // ---- neutrality, and the reachability boundary ----

    /// <summary>
    /// A single-region mesh takes the same code path either way, bit for bit — the safety
    /// argument for touching the flux recovery at all, and what makes every thermal
    /// verification figure this project quotes provably unmoved. The two arrays are computed
    /// INDEPENDENTLY through the <c>AveragedFlux</c> seam rather than compared with the
    /// short-circuit that hands the same object back, so the claim is a measurement.
    /// </summary>
    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void ASingleRegionMeshIsBitIdenticalWithAndWithoutTheRule(ElementOrder order)
    {
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(4, 3, 2), 4, 3, 2);
        var mesh = order == ElementOrder.Linear
            ? AnalysisMesh.Of(tets)
            : AnalysisMesh.Quadratic(tets);
        var model = new ThermalModel(mesh, Conductive);
        // A field with a gradient in every direction, so no component is trivially zero.
        foreach (int node in model.NodesOn(Facets.All))
        {
            var p = mesh.Position(node);
            model.TemperatureNode(node, 20 + 3 * p.X + 7 * p.Y * p.Z);
        }
        var results = ThermalSolver.Solve(model);

        Assert.True(mesh.RegionSlotsAreNodes, "a one-region mesh's slots must BE its nodes");
        Assert.Equal(0, mesh.InterfaceNodeCount);

        var withRule = results.AveragedFlux(perRegion: true);
        var without = results.AveragedFlux(perRegion: false);
        Assert.Equal(without.Length, withRule.Length);
        for (int node = 0; node < mesh.NodeCount; node++)
        {
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(without[node].X),
                BitConverter.DoubleToInt64Bits(withRule[node].X));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(without[node].Y),
                BitConverter.DoubleToInt64Bits(withRule[node].Y));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(without[node].Z),
                BitConverter.DoubleToInt64Bits(withRule[node].Z));
        }
    }

    /// <summary>
    /// The thermal half of the reachability statement: <c>TetMesher</c>'s multi-body meshes are
    /// DISJOINT, so no node is shared and the per-region accessor answers exactly what the
    /// node-indexed field does — bit for bit, at every node of both bodies.
    /// </summary>
    [Fact]
    public void DisjointBodiesReadTheSameFluxThroughBothAccessors()
    {
        IReadOnlyList<AnalysisBody> bodies =
        [
            new AnalysisBody(MeshPrimitives.Box(
                new Aabb(new Vector3d(0, 0, 0), new Vector3d(10, 10, 10))), Conductive, "block"),
            new AnalysisBody(MeshPrimitives.Box(
                new Aabb(new Vector3d(30, 0, 0), new Vector3d(40, 10, 10))), Insulating, "plate"),
        ];

        // Refined, so the bodies have INTERIOR nodes: an unrefined box is eight corners, every
        // one of which the two driven faces would prescribe, and a solve with nothing free is
        // refused by name (which is correct, and would make this fixture measure nothing).
        var tets = TetMesher.Mesh(
            bodies, new TetMeshOptions { RefineQuality = true, MaxElementSize = 4 });
        var mesh = AnalysisMesh.Of(tets);
        var model = ThermalModel.For(mesh, bodies);

        // Both bodies driven, so neither carries a trivially zero flux the comparison could
        // pass on: a hot face and a cold face on each.
        var hot = Facets.Or(
            Facets.OnPlane(Vector3d.Zero, Vector3d.UnitX),
            Facets.OnPlane(new Vector3d(30, 0, 0), Vector3d.UnitX));
        var cold = Facets.Or(
            Facets.OnPlane(new Vector3d(10, 0, 0), Vector3d.UnitX),
            Facets.OnPlane(new Vector3d(40, 0, 0), Vector3d.UnitX));
        model.Temperature(hot, HotEnd);
        model.Temperature(cold, 0);
        var results = ThermalSolver.Solve(model);

        Assert.Equal(new[] { 0, 1 }, mesh.Regions);
        Assert.Equal(0, mesh.InterfaceNodeCount);

        var nodal = results.NodalFlux;
        double peak = 0;
        for (int node = 0; node < mesh.NodeCount; node++)
        {
            var regions = mesh.RegionsAt(node);
            Assert.Single(regions.ToArray());
            var perRegion = results.NodalFluxIn(regions[0], node);
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(perRegion.X),
                BitConverter.DoubleToInt64Bits(nodal[node].X));
            peak = Math.Max(peak, Math.Abs(nodal[node].X));
        }
        output.WriteLine($"{mesh.NodeCount} nodes over two disjoint bodies, peak |qx| {peak:F1}");
        Assert.True(peak > 1, "both bodies must carry flux or the comparison is vacuous");
    }

    /// <summary>A region the node does not touch is refused BY NAME, naming the ones it does —
    /// never answered with some other material's value.</summary>
    [Fact]
    public void AskingForARegionTheNodeDoesNotTouchIsRefusedByName()
    {
        var results = ThroughFlow(ParallelCube(), ElementOrder.Linear, out var model);
        var mesh = model.Mesh;

        int inConductiveOnly = -1;
        for (int node = 0; node < mesh.NodeCount && inConductiveOnly < 0; node++)
        {
            var regions = mesh.RegionsAt(node);
            if (regions.Length == 1 && regions[0] == 0)
                inConductiveOnly = node;
        }
        Assert.True(inConductiveOnly >= 0);

        var error = Assert.Throws<ArgumentException>(
            () => results.NodalFluxIn(1, inConductiveOnly));
        Assert.Contains($"Node {inConductiveOnly}", error.Message);
        Assert.Contains("region 1", error.Message);
        Assert.Contains("region(s) 0", error.Message);
    }
}
