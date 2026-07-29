using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// <see cref="StructuralSolver.SolveAll"/>: several load cases over one assembly and one
/// factorization.
///
/// <para>The claim that has to be held exactly is that sharing changes nothing about the
/// answers — so the headline test is a BIT comparison against the same cases solved one at a
/// time, not a tolerance. The claim that has to be held loudly is that a case list which
/// does not share one stiffness matrix is refused, because substituting the wrong
/// right-hand side through a factorization returns a field that converges, passes its own
/// residual check, and answers a question nobody asked.</para>
/// </summary>
public class MultipleLoadCaseTests
{
    private static readonly Material Steel = new("multi-case steel", 210_000, 0.3);

    private static AnalysisMesh Mesh(ElementOrder order = ElementOrder.Linear, int n = 2)
    {
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(40, 20, 10), 4 * n, 2 * n, n);
        return order == ElementOrder.Linear ? AnalysisMesh.Of(tets) : AnalysisMesh.Quadratic(tets);
    }

    /// <summary>A cantilever fixed at x = 0 and pushed at x = L in the given direction.</summary>
    private static StructuralModel Case(AnalysisMesh mesh, Vector3d tipForce)
    {
        var model = new StructuralModel(mesh, Steel);
        model.Fix(StructuredTetMesh.XMin);
        model.Force(Facets.Tag(StructuredTetMesh.XMax), tipForce);
        return model;
    }

    private static void AssertBitIdentical(StructuralResults expected, StructuralResults actual)
    {
        Assert.Equal(expected.Displacement.Count, actual.Displacement.Count);
        for (int i = 0; i < expected.Displacement.Count; i++)
        {
            var a = expected.Displacement[i];
            var b = actual.Displacement[i];
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.X), BitConverter.DoubleToInt64Bits(b.X));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.Y), BitConverter.DoubleToInt64Bits(b.Y));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.Z), BitConverter.DoubleToInt64Bits(b.Z));
        }
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(expected.Report.StrainEnergy),
            BitConverter.DoubleToInt64Bits(actual.Report.StrainEnergy));
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(expected.Report.EquilibriumResidual),
            BitConverter.DoubleToInt64Bits(actual.Report.EquilibriumResidual));
    }

    // ---------- the answers must not move ----------

    /// <summary>
    /// The oracle for the whole feature. A shared factorization is only legitimate if it
    /// produces the SAME numbers, and "the same to twelve digits" would pass a run that had
    /// quietly reordered an assembly — this asserts the bits.
    /// </summary>
    [Theory]
    [InlineData(ElementOrder.Linear, FeaSolveMethod.Direct)]
    [InlineData(ElementOrder.Quadratic, FeaSolveMethod.Direct)]
    [InlineData(ElementOrder.Linear, FeaSolveMethod.ConjugateGradient)]
    public void SolveAll_MatchesSeparateSolvesBitForBit(ElementOrder order, FeaSolveMethod method)
    {
        var options = new StructuralSolveOptions { Method = method };
        var loads = new[]
        {
            new Vector3d(0, 0, -4000),
            new Vector3d(0, 2500, 0),
            new Vector3d(1500, 0, 900),
        };

        var separate = loads.Select(f => StructuralSolver.Solve(Case(Mesh(order), f), options)).ToList();

        var shared = Mesh(order);
        var together = StructuralSolver.SolveAll(loads.Select(f => Case(shared, f)).ToList(), options);

        Assert.Equal(loads.Length, together.Count);
        for (int c = 0; c < loads.Length; c++)
            AssertBitIdentical(separate[c], together[c]);
    }

    /// <summary>
    /// A single-case <see cref="StructuralSolver.Solve"/> now runs through
    /// <see cref="StructuralSolver.SolveAll"/>, so this pins that the refactor is invisible:
    /// the N = 1 member of the general path is the incumbent path. Verified against a
    /// physical answer as well as against itself, so it cannot be satisfied by two equally
    /// broken routes.
    /// </summary>
    [Fact]
    public void SingleCase_IsTheGeneralPathsNEqualsOne()
    {
        var mesh = Mesh(ElementOrder.Quadratic, 3);
        var model = Case(mesh, new Vector3d(0, 0, -4000));
        var one = StructuralSolver.Solve(model);
        var viaAll = StructuralSolver.SolveAll([model])[0];
        AssertBitIdentical(one, viaAll);
        Assert.Equal(1, one.Report.LoadCases);
        // Free-end deflection of a 40 x 20 x 10 cantilever: a real number, so the two
        // agreeing is not two ways of returning zero.
        Assert.True(one.Displacement.Max(d => d.Length) > 1e-4);
    }

    /// <summary>
    /// Prescribed displacement VALUES are what the cases are allowed to differ in besides
    /// loads — they move to the right-hand side as <c>f -= K_fc·u_c</c>, which is per case
    /// by construction. Only the restraint MASK has to match.
    /// </summary>
    [Fact]
    public void PrescribedValuesMayDifferBetweenCases()
    {
        var mesh = Mesh();

        StructuralModel Pull(double dz)
        {
            var model = new StructuralModel(mesh, Steel);
            model.Fix(StructuredTetMesh.XMin);
            model.Prescribe(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(0, 0, dz), Dof.All);
            return model;
        }

        var separate = new[] { 0.0, 0.05, -0.02 }.Select(d => StructuralSolver.Solve(Pull(d))).ToList();
        var together = StructuralSolver.SolveAll(new[] { 0.0, 0.05, -0.02 }.Select(Pull).ToList());

        for (int c = 0; c < separate.Count; c++)
            AssertBitIdentical(separate[c], together[c]);
        // The three cases really are different: a linear solve scales with the prescribed
        // value, so the strain energies must be in ratio 0 : 0.05^2 : 0.02^2.
        Assert.Equal(0.0, together[0].Report.StrainEnergy, 12);
        Assert.Equal(
            together[1].Report.StrainEnergy * (0.02 * 0.02) / (0.05 * 0.05),
            together[2].Report.StrainEnergy,
            6);
    }

    // ---------- the report ----------

    [Fact]
    public void ReportNamesTheSharedCostAndTheCaseCount()
    {
        var mesh = Mesh();
        var results = StructuralSolver.SolveAll(
        [
            Case(mesh, new Vector3d(0, 0, -4000)),
            Case(mesh, new Vector3d(0, 3000, 0)),
        ]);

        Assert.All(results, r => Assert.Equal(2, r.Report.LoadCases));
        // One assembly and one factorization, so those numbers are shared verbatim rather
        // than divided into a per-case share nobody measured.
        Assert.Equal(results[0].Report.AssembleMs, results[1].Report.AssembleMs);
        Assert.Equal(results[0].Report.FactorMs, results[1].Report.FactorMs);
        Assert.Equal(results[0].Report.FactorNonZeros, results[1].Report.FactorNonZeros);
        Assert.Contains("shared by 2 load cases", results[0].Report.ToText());
    }

    /// <summary>
    /// The advisory's cited ratio is one factorization against one CG run, so N cases divide
    /// it — and once the amortisation has already won there is nothing left to advise.
    /// </summary>
    [Fact]
    public void AdvisoryAccountsForTheAmortisation()
    {
        // 10 s factor dominating a 10.5 s run: fires for one case.
        string? single = StructuralSolver.AdvisoryFor(FeaSolveMethod.Direct, 20_000, 10_000, 10_500);
        Assert.NotNull(single);
        Assert.DoesNotContain("load cases", single);

        string? four = StructuralSolver.AdvisoryFor(FeaSolveMethod.Direct, 20_000, 10_000, 10_500, 4);
        Assert.NotNull(four);
        Assert.Contains("4 load cases", four);
        Assert.Contains("12.2x", four);

        // 48.6 / 60 < 1: the shared factorization has already beaten the alternative on the
        // benchmark's own numbers.
        Assert.Null(StructuralSolver.AdvisoryFor(FeaSolveMethod.Direct, 20_000, 10_000, 10_500, 60));
    }

    // ---------- refusals ----------

    [Fact]
    public void RefusesCasesOnDifferentMeshInstances()
    {
        var ex = Assert.Throws<FeaException>(() => StructuralSolver.SolveAll(
        [
            Case(Mesh(), new Vector3d(0, 0, -4000)),
            Case(Mesh(), new Vector3d(0, 3000, 0)),
        ]));
        Assert.Contains("different AnalysisMesh instance", ex.Message);
    }

    [Fact]
    public void RefusesCasesWithDifferentRestraintMasks()
    {
        var mesh = Mesh();
        var loose = new StructuralModel(mesh, Steel);
        loose.Fix(Facets.Tag(StructuredTetMesh.XMin), Dof.X);
        loose.Fix(Facets.Tag(StructuredTetMesh.YMin), Dof.Y | Dof.Z);
        loose.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(0, 0, -4000));

        var ex = Assert.Throws<FeaException>(() => StructuralSolver.SolveAll(
            [Case(mesh, new Vector3d(0, 0, -4000)), loose]));
        Assert.Contains("restrains node", ex.Message);
        Assert.Contains("Prescribed VALUES may differ freely", ex.Message);
    }

    [Fact]
    public void RefusesCasesWithDifferentMaterials()
    {
        var mesh = Mesh();
        var aluminium = new StructuralModel(mesh, Materials.Aluminium6061);
        aluminium.Fix(StructuredTetMesh.XMin);
        aluminium.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(0, 0, -4000));

        var ex = Assert.Throws<FeaException>(() => StructuralSolver.SolveAll(
            [Case(mesh, new Vector3d(0, 0, -4000)), aluminium]));
        Assert.Contains("different material", ex.Message);
    }

    [Fact]
    public void RefusesAnEmptyCaseList()
    {
        Assert.Throws<ArgumentException>(() => StructuralSolver.SolveAll([]));
    }
}
