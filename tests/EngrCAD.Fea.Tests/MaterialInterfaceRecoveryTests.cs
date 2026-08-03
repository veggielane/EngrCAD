using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Stress recovery across a BONDED MATERIAL INTERFACE, where the stress tensor is genuinely
/// discontinuous and a smooth recovered field is therefore wrong on both sides.
///
/// <para><b>The recorded oracle does not work here, and that is the first finding.</b>
/// The two-material bar this project already verifies is in SERIES — the interface is
/// perpendicular to the load — and force equilibrium then makes the axial stress
/// <c>F/A</c> in <i>both</i> halves: the STRAIN jumps and the stress does not. A stress
/// recovery reproduces a uniform field exactly whatever it does at the interface, so that
/// fixture structurally cannot see this defect (<see cref="ASeriesBarCannotSeeTheDefect"/>
/// asserts as much, so the choice is documented rather than merely made — and it must be
/// driven by a FORCE: prescribing a uniform stretch on a series bar's boundary imposes the
/// same strain on both halves, which is the parallel condition wearing the series
/// arrangement's geometry, and it measured a 25.4% difference between the two recoveries
/// before that was corrected).</para>
///
/// <para><b>The arrangement with a stress jump is PARALLEL.</b> Two materials side by side
/// under a uniform axial stretch share the strain, so the stress jumps with the modulus. With
/// <b>Poisson's ratio zero in both halves</b> — the same device the series fixture uses, and
/// for the same reason — <c>u = (eps·x, 0, 0)</c> is the EXACT solution with no body force:
/// the strain is <c>(eps, 0, 0)</c> everywhere, the stress is <c>(E_i·eps, 0, 0)</c>, its
/// divergence vanishes, and the traction across the interface (<c>sigma_xy</c>,
/// <c>sigma_yy</c>, <c>sigma_yz</c>) is zero on both sides. A nonzero nu would make
/// <c>sigma_yy</c> jump, which is a traction discontinuity, i.e. not a solution at all.</para>
///
/// <para>That field is linear, so a linear tetrahedron reproduces it exactly and every
/// element's stress is exactly <c>E_i·eps</c>. The recovered value in each material must
/// therefore be exact — the strongest possible bar, an EQUALITY rather than a rate.</para>
///
/// <para><b>What a cross-interface patch costs is REACH, not merely a blurred node.</b>
/// Averaging is local: only the shared node is blended. A patch is not — it is fitted over
/// every element at its node and then written to every node of every one of them — so a
/// single patch centred on the interface writes a ramp into nodes ONE element layer inside
/// each material, nodes that touch a single material and have an unambiguous right answer.
/// Measured on this fixture: <b>34 such nodes wrong, the worst by 92.9%</b>, all of them in
/// that one layer and none beyond it, where the region rule leaves every one exact to 4.5e-15.
/// <see cref="TheCrossRegionFitCorruptsNodesInsideBothMaterials"/> measures exactly that
/// through the <c>respectRegions</c> seam, so the rule is shown to fire rather than merely
/// to leave the right answer standing.</para>
///
/// <para><b>Reachability, stated so it cannot rot.</b> A connected multi-region mesh is not
/// constructible from the public API today: <c>TetMesher</c> refuses MATING bodies, disjoint
/// ones share no node, and <c>TetMesh</c>'s region-carrying constructor is internal. So these
/// fixtures are built the way <see cref="MultiMaterialTests"/> builds its bars — one
/// structured mesh with regions assigned by position — and
/// <see cref="DisjointBodiesShareNoNodeSoThereIsNoInterface"/> pins the other half.</para>
/// </summary>
public class MaterialInterfaceRecoveryTests(ITestOutputHelper output)
{
    // nu = 0 is what makes the exact solution a pure axial field; see the class remarks.
    private static readonly Material Stiff = new("stiff", 200_000, 0.0, 7.85e-9);
    private static readonly Material Soft = new("soft", 70_000, 0.0, 2.70e-9);

    private const double Side = 4.0;
    private const double Half = Side / 2;
    private const int Divisions = 4;
    private const double Strain = 1e-3;

    /// <summary>The exact axial stress in a region: E·eps, uniform within the material.</summary>
    private static double ExactStress(int region) =>
        (region == 0 ? Stiff : Soft).YoungsModulus * Strain;

    /// <summary>A cube split at y = 2 into region 0 (below, stiff) and region 1 (above, soft).
    /// An even division count in y puts the interface exactly on a grid plane, so no element
    /// straddles it.</summary>
    private static TetMesh SplitCube() => StructuredTetMesh.Box(
        Vector3d.Zero, new Vector3d(Side, Side, Side), Divisions, Divisions, Divisions,
        centroid => centroid.Y < Half ? 0 : 1);

    /// <summary>The series arrangement: split along the LOAD direction instead.</summary>
    private static TetMesh SeriesCube() => StructuredTetMesh.Box(
        Vector3d.Zero, new Vector3d(Side, Side, Side), Divisions, Divisions, Divisions,
        centroid => centroid.X < Half ? 0 : 1);

    /// <summary>The uniform stretch u = (eps·x, 0, 0), prescribed on the whole boundary. With
    /// nu = 0 it is the exact solution of both arrangements with no body force.</summary>
    private static StructuralResults Stretch(TetMesh tets, out StructuralModel model)
    {
        var mesh = AnalysisMesh.Of(tets);
        model = new StructuralModel(mesh, Stiff).SetMaterial(1, Soft);
        foreach (int node in model.NodesOn(Facets.All))
        {
            var p = mesh.Position(node);
            model.PrescribeNode(node, new Vector3d(Strain * p.X, 0, 0));
        }
        return StructuralSolver.Solve(model);
    }

    // ---- the fix: every node is exact in every material it touches ----

    /// <summary>
    /// The strongest form the claim takes: with a piecewise-CONSTANT exact stress field, a
    /// recovery that never fits across the jump must return it EXACTLY — in both materials,
    /// at every node, including the interface nodes themselves, where the answer is
    /// per-region and there are two of them.
    /// </summary>
    [Theory]
    [InlineData(StressRecovery.Direct)]
    [InlineData(StressRecovery.Superconvergent)]
    public void EveryNodeIsExactInEveryMaterialItTouches(StressRecovery recovery)
    {
        var results = Stretch(SplitCube(), out var model);
        results.Recovery = recovery;
        var mesh = model.Mesh;

        // The configuration is CARRIED: without interface nodes this test measures nothing.
        Assert.True(mesh.InterfaceNodeCount > 0, "the fixture has no material interface");

        double worst = 0;
        int checkedSlots = 0;
        for (int node = 0; node < mesh.NodeCount; node++)
        {
            foreach (int region in mesh.RegionsAt(node))
            {
                double expected = ExactStress(region);
                var got = results.NodalStressIn(region, node);
                worst = Math.Max(worst, Math.Abs(got.Xx - expected) / expected);
                // The other five components are exactly zero in the exact field, so they are
                // measured against the SAME scale rather than against themselves.
                worst = Math.Max(worst, Math.Abs(got.Yy) / expected);
                worst = Math.Max(worst, Math.Abs(got.Zz) / expected);
                worst = Math.Max(worst, Math.Abs(got.Xy) / expected);
                worst = Math.Max(worst, Math.Abs(got.Yz) / expected);
                worst = Math.Max(worst, Math.Abs(got.Xz) / expected);
                checkedSlots++;
            }
        }

        output.WriteLine(
            $"{recovery}: {mesh.NodeCount} nodes, {checkedSlots} (node, region) slots, "
            + $"{mesh.InterfaceNodeCount} on the interface; worst relative error {worst:E3}");
        Assert.True(worst < 1e-9, $"per-region stress is not exact: {worst:E3}");
        Assert.Equal(mesh.NodeCount + mesh.InterfaceNodeCount, checkedSlots);
    }

    /// <summary>
    /// The guard shown to FIRE. Collapsing the regions is exactly the algorithm before this
    /// change, and it is wrong at nodes that are not on the interface at all — one whole
    /// element layer into each material, where the answer is unambiguous.
    /// </summary>
    [Fact]
    public void TheCrossRegionFitCorruptsNodesInsideBothMaterials()
    {
        var results = Stretch(SplitCube(), out var model);
        results.Recovery = StressRecovery.Superconvergent;
        var mesh = model.Mesh;

        var wrong = results.RecoverStress(respectRegions: false);
        var right = results.RecoverStress(respectRegions: true);

        double worstWrong = 0, worstRight = 0, worstWrongAwayFromInterface = 0;
        int corrupted = 0;
        for (int node = 0; node < mesh.NodeCount; node++)
        {
            var regions = mesh.RegionsAt(node);
            if (regions.Length != 1)
                continue;   // a node ON the interface has two answers; these have one
            double expected = ExactStress(regions[0]);

            double wrongError = Math.Abs(wrong.Stress[node].Xx - expected) / expected;
            double rightError =
                Math.Abs(right.SlotStress[mesh.RegionSlot(node, regions[0])].Xx - expected)
                / expected;
            worstWrong = Math.Max(worstWrong, wrongError);
            worstRight = Math.Max(worstRight, rightError);

            // "Away from the interface" is the sharp form: these nodes are not even adjacent
            // to it, so nothing about them is ambiguous under any convention.
            if (Math.Abs(mesh.Position(node).Y - Half) > Side / Divisions + 1e-9)
                worstWrongAwayFromInterface = Math.Max(worstWrongAwayFromInterface, wrongError);
            if (wrongError > 1e-6)
                corrupted++;
        }

        output.WriteLine(
            $"single-region nodes: cross-region fit worst {worstWrong:P2} "
            + $"({corrupted} nodes past 1e-6), of which {worstWrongAwayFromInterface:P2} is "
            + $"more than one element from the interface; per-region fit worst {worstRight:E3}");

        Assert.True(worstWrong > 0.01,
            $"the cross-region fit measured only {worstWrong:E3} — this fixture proves nothing");
        Assert.True(corrupted > 0, "no node was corrupted, so the comparison is vacuous");
        Assert.True(worstRight < 1e-9, $"the per-region fit is not exact: {worstRight:E3}");
    }

    /// <summary>
    /// The estimator's own stake in it: a cross-interface fit manufactures a stress gap in
    /// every element beside the interface, and the ZZ estimate books that modelling jump as
    /// discretization error. Here the true error is exactly zero, so anything the estimator
    /// reports is spurious.
    /// </summary>
    [Fact]
    public void TheErrorEstimateIsNotPollutedByTheInterface()
    {
        var results = Stretch(SplitCube(), out _);
        results.Recovery = StressRecovery.Superconvergent;

        var honest = results.ErrorEstimate;
        var polluted = results.ErrorEstimateFrom(results.RecoverStress(respectRegions: false));

        output.WriteLine(
            $"exact field: region-aware estimate {honest.RelativeError:P4}, "
            + $"cross-region estimate {polluted.RelativeError:P4}");

        Assert.True(honest.RelativeError < 1e-9,
            $"the exact field must estimate zero error, got {honest.RelativeError:E3}");
        Assert.True(polluted.RelativeError > 0.01,
            $"the cross-region estimate measured only {polluted.RelativeError:E3}");
    }

    // ---- the decision: what the node-indexed field reports ----

    /// <summary>
    /// The decision, asserted. <c>NodalStress</c> is indexed by node, so at an interface node
    /// it holds one value where the physics has two, and it reports the average of them —
    /// strictly between the materials rather than either one. Away from the interface the
    /// blend is a blend of one value, so it is the per-region value BIT for bit, which is
    /// what makes the two accessors safe to mix.
    /// </summary>
    [Theory]
    [InlineData(StressRecovery.Direct)]
    [InlineData(StressRecovery.Superconvergent)]
    public void TheNodeIndexedFieldBlendsOnlyWhereItMust(StressRecovery recovery)
    {
        var results = Stretch(SplitCube(), out var model);
        results.Recovery = recovery;
        var mesh = model.Mesh;
        var nodal = results.NodalStress;

        double stiff = ExactStress(0), soft = ExactStress(1);
        int blended = 0;
        for (int node = 0; node < mesh.NodeCount; node++)
        {
            var regions = mesh.RegionsAt(node);
            if (regions.Length == 1)
            {
                // Bit for bit, not within a tolerance: the blend of one value must BE it.
                var perRegion = results.NodalStressIn(regions[0], node);
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(perRegion.Xx),
                    BitConverter.DoubleToInt64Bits(nodal[node].Xx));
                continue;
            }

            blended++;
            Assert.True(nodal[node].Xx > soft && nodal[node].Xx < stiff,
                $"node {node} reports {nodal[node].Xx:F3}, not between {soft} and {stiff}");
            Assert.Equal(stiff, results.NodalStressIn(0, node).Xx, stiff * 1e-9);
            Assert.Equal(soft, results.NodalStressIn(1, node).Xx, soft * 1e-9);
        }

        output.WriteLine(
            $"{recovery}: {blended} of {mesh.NodeCount} nodes blend two materials "
            + $"({soft} and {stiff} MPa)");
        Assert.Equal(mesh.InterfaceNodeCount, blended);
    }

    /// <summary>The von Mises spelling reads the same values, so a per-material peak needs no
    /// second rule.</summary>
    [Fact]
    public void TheVonMisesSpellingFollowsThePerRegionStress()
    {
        var results = Stretch(SplitCube(), out var model);
        results.Recovery = StressRecovery.Superconvergent;
        var mesh = model.Mesh;

        double peakStiff = 0, peakSoft = 0;
        for (int node = 0; node < mesh.NodeCount; node++)
        {
            foreach (int region in mesh.RegionsAt(node))
            {
                double v = results.NodalVonMisesIn(region, node);
                if (region == 0)
                    peakStiff = Math.Max(peakStiff, v);
                else
                    peakSoft = Math.Max(peakSoft, v);
            }
        }

        // Uniaxial, so von Mises IS the axial stress.
        Assert.Equal(ExactStress(0), peakStiff, ExactStress(0) * 1e-9);
        Assert.Equal(ExactStress(1), peakSoft, ExactStress(1) * 1e-9);

        // The node-indexed peak can never EXCEED the stiff material's, because every value it
        // holds is that material's own or a blend pulled toward the softer one — and it is
        // strictly below wherever the hot spot sits ON an interface, which is the reason the
        // per-region accessor exists. Here the field is uniform, so the peak is reached at
        // interior stiff nodes too and the two coincide; the inequality is what is asserted.
        output.WriteLine(
            $"per-region peaks {peakStiff:F3} / {peakSoft:F3} MPa, "
            + $"node-indexed peak {results.MaxVonMises:F3} MPa");
        Assert.True(results.MaxVonMises <= peakStiff + 1e-9);
    }

    // ---- the recorded oracle, and why it is not this one ----

    /// <summary>
    /// The series bar — the fixture the backlog entry pointed at — has a CONTINUOUS stress
    /// field: equilibrium makes the axial stress F/A in both halves however the moduli
    /// differ. Both algorithms therefore reproduce it exactly and the comparison is empty.
    /// Asserted, so that fixture cannot be reached for again by a later reader.
    /// </summary>
    [Fact]
    public void ASeriesBarCannotSeeTheDefect()
    {
        // Driven by a FORCE, not by a prescribed stretch — and that distinction is the whole
        // point rather than a detail. Prescribing u = (eps·x, 0, 0) on the boundary of a
        // series bar forces the same STRAIN on both halves, which is the parallel condition
        // in disguise: it is not a solution at all (the traction across the interface would
        // jump), and the fixture measured a 25.4% difference between the two recoveries, i.e.
        // it saw the rule. A free end under a load is the series arrangement, and there
        // equilibrium makes sigma_xx = F/A on both sides.
        var mesh = AnalysisMesh.Of(SeriesCube());
        var model = new StructuralModel(mesh, Stiff).SetMaterial(1, Soft);
        model.Fix(Facets.Tag(StructuredTetMesh.XMin));
        model.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(1000, 0, 0));
        var results = StructuralSolver.Solve(model);
        results.Recovery = StressRecovery.Superconvergent;

        // The exact axial stress is uniform: 1000 N over 4 x 4 mm.
        double uniform = 1000.0 / (Side * Side);
        var withRegions = results.RecoverStress(respectRegions: true);
        var without = results.RecoverStress(respectRegions: false);

        double worst = 0, worstAgainstExact = 0;
        for (int node = 0; node < mesh.NodeCount; node++)
        {
            worst = Math.Max(worst, Math.Abs(withRegions.Stress[node].Xx - without.Stress[node].Xx));
            worstAgainstExact =
                Math.Max(worstAgainstExact, Math.Abs(without.Stress[node].Xx - uniform));
        }

        output.WriteLine(
            $"series bar: the exact axial stress is {uniform} MPa in BOTH halves; the "
            + $"cross-region recovery is off by {worstAgainstExact:E3} and the two recoveries "
            + $"differ by {worst:E3} ({worst / uniform:P4}) - the arrangement carries no "
            + "information about the interface rule");
        Assert.True(worstAgainstExact < 1e-6 * uniform,
            $"the series stress is not uniform ({worstAgainstExact:E3} of {uniform})");
        Assert.True(worst < 1e-6 * uniform,
            $"the series bar DOES see the rule ({worst:E3} of {uniform}); the class remarks "
            + "claim it cannot, so one of them is wrong");
    }

    // ---- neutrality, and the reachability boundary ----

    /// <summary>
    /// A single-region mesh takes the same code path either way, bit for bit — which is the
    /// safety argument for touching the recovery at all, and what makes every verification
    /// figure this project quotes provably unmoved.
    /// </summary>
    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void ASingleRegionMeshIsBitIdenticalWithAndWithoutTheRule(ElementOrder order)
    {
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, new Vector3d(4, 3, 2), 4, 3, 2);
        var mesh = order == ElementOrder.Linear
            ? AnalysisMesh.Of(tets)
            : AnalysisMesh.Quadratic(tets);
        var model = new StructuralModel(mesh, Stiff);
        foreach (int node in model.NodesOn(Facets.All))
        {
            var p = mesh.Position(node);
            model.PrescribeNode(node, new Vector3d(1e-3 * p.X * p.X, 0, 1e-4 * p.Z * p.Y));
        }
        var results = StructuralSolver.Solve(model);

        Assert.True(mesh.RegionSlotsAreNodes, "a one-region mesh's slots must BE its nodes");
        Assert.Equal(0, mesh.InterfaceNodeCount);

        var withRule = results.RecoverStress(respectRegions: true);
        var without = results.RecoverStress(respectRegions: false);
        for (int node = 0; node < mesh.NodeCount; node++)
        {
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(without.Stress[node].Xx),
                BitConverter.DoubleToInt64Bits(withRule.Stress[node].Xx));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(without.Stress[node].Xy),
                BitConverter.DoubleToInt64Bits(withRule.Stress[node].Xy));
        }
        Assert.Equal(without.FallbackNodes, withRule.FallbackNodes);
        Assert.Equal(without.RankDeficientPatches, withRule.RankDeficientPatches);
    }

    /// <summary>
    /// The other half of the reachability statement, pinned so the finding cannot rot:
    /// <c>TetMesher</c>'s multi-body meshes are DISJOINT, so no node is shared between two
    /// regions and there is no interface for a patch to span. The defect these tests fix is
    /// therefore not reachable from the public API today — it is reachable the moment
    /// conforming interfaces land, which is what this work is a precondition for.
    /// </summary>
    [Fact]
    public void DisjointBodiesShareNoNodeSoThereIsNoInterface()
    {
        IReadOnlyList<AnalysisBody> bodies =
        [
            new AnalysisBody(MeshPrimitives.Box(
                new Aabb(new Vector3d(0, 0, 0), new Vector3d(10, 10, 10))), Stiff, "block"),
            new AnalysisBody(MeshPrimitives.Box(
                new Aabb(new Vector3d(30, 0, 0), new Vector3d(40, 10, 10))), Soft, "plate"),
        ];

        var mesh = AnalysisMesh.Of(TetMesher.Mesh(bodies));

        Assert.Equal(new[] { 0, 1 }, mesh.Regions);
        Assert.Equal(0, mesh.InterfaceNodeCount);
        Assert.True(mesh.RegionSlotsAreNodes);
        for (int node = 0; node < mesh.NodeCount; node++)
            Assert.Single(mesh.RegionsAt(node).ToArray());
    }

    /// <summary>A region the node does not touch is refused BY NAME, naming the ones it
    /// does — never answered with some other material's value.</summary>
    [Fact]
    public void AskingForARegionTheNodeDoesNotTouchIsRefusedByName()
    {
        var results = Stretch(SplitCube(), out var model);
        var mesh = model.Mesh;

        int inStiffOnly = -1;
        for (int node = 0; node < mesh.NodeCount && inStiffOnly < 0; node++)
        {
            var regions = mesh.RegionsAt(node);
            if (regions.Length == 1 && regions[0] == 0)
                inStiffOnly = node;
        }
        Assert.True(inStiffOnly >= 0);

        var error = Assert.Throws<ArgumentException>(
            () => results.NodalStressIn(1, inStiffOnly));
        Assert.Contains($"Node {inStiffOnly}", error.Message);
        Assert.Contains("region 1", error.Message);
        Assert.Contains("region(s) 0", error.Message);
    }
}
