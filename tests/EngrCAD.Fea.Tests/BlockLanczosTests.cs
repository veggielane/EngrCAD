using EngrCAD.Core;
using EngrCAD.Core.Solvers;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Block Lanczos, and the multiplicity boundary it moves. A single-vector Krylov space
/// contains ONE vector from each eigenspace, so a repeated eigenvalue's copies are invisible
/// to one run; a block of size b carries up to b of them. The fixtures here are SYNTHETIC
/// diagonal pencils with exact multiplicities, deliberately: a real mesh of a symmetric part
/// SPLITS its theoretical multiplicities (this project's square-section beam pairs split by
/// 0.04–0.13%), so no mesh fixture can carry the configuration these tests exist for — the
/// same reason the Surface Nets ambiguous-face fixtures assert they still CARRY their
/// configuration rather than trusting a shape to keep it.
/// </summary>
public class BlockLanczosTests(ITestOutputHelper output)
{
    /// <summary>A diagonal stiffness with the given eigenvalues against an identity mass —
    /// the smallest pencil whose spectrum is exactly what the test says it is.</summary>
    private static (PackedSparseMatrix K, PackedSparseMatrix M, SparseCholesky Factor)
        DiagonalPencil(double[] eigenvalues)
    {
        int n = eigenvalues.Length;
        var kb = new SparseMatrixBuilder(n, n);
        var mb = new SparseMatrixBuilder(n, n);
        for (int i = 0; i < n; i++)
        {
            kb.Add(i, i, eigenvalues[i]);
            mb.Add(i, i, 1.0);
        }
        var k = kb.ToSymmetricUpper();
        return (k, mb.ToSymmetricUpper(), SparseCholesky.Factorize(k));
    }

    private static double[] Spectrum(int n, params double[] leading)
    {
        var values = new double[n];
        for (int i = 0; i < n; i++)
            values[i] = i < leading.Length ? leading[i] : leading[^1] + (i - leading.Length + 1);
        return values;
    }

    private static LanczosResult Solve(
        double[] eigenvalues, int wanted, int blockSize, int maxKrylov = 40)
    {
        var (k, m, factor) = DiagonalPencil(eigenvalues);
        return LanczosEigen.Solve(
            k, m, m, factor, 0.0, [], wanted, 1e-9, maxKrylov, maxRestarts: 8,
            blockSize: blockSize);
    }

    [Fact]
    public void SingleVector_RecoversAnExactDouble_TheRecordedClaimNowMeasured()
    {
        // The recorded design claim — "locking and restarting recovers the second member of
        // a degenerate pair" — had never been measured against an EXACT multiplicity,
        // because no mesh fixture can carry one (real meshes split their pairs). It holds:
        // the first run returns one copy, the restart's start vector is purged against it,
        // and the second copy comes back and sorts ahead of the extra.
        var result = Solve(Spectrum(30, 1, 1, 2, 3), wanted: 2, blockSize: 1);
        output.WriteLine(
            $"single-vector on a double: {string.Join(", ", result.Pairs.Select(p => $"{p.Eigenvalue:G6}"))} "
            + $"({result.Iterations} back-substitutions, {result.Restarts} restarts)");

        Assert.True(result.Converged);
        Assert.Equal(1.0, result.Pairs[0].Eigenvalue, 1e-8);
        Assert.Equal(1.0, result.Pairs[1].Eigenvalue, 1e-8);
    }

    [Fact]
    public void SingleVector_MissesTheThirdCopyOfAnExactTriple_TheDocumentedLimitation()
    {
        // The failure the block method exists for, pinned so the docs cannot rot: locking
        // and restarting plus the one-extra targeting recover the SECOND copy of a repeated
        // eigenvalue, and the third is exactly one restart past what the target allows —
        // "the three lowest" comes back {1, 1, 2} for a truth of {1, 1, 1}. Every returned
        // pair carries a tiny measured residual — each IS an eigenpair — which is why
        // nothing inside the iteration can notice a copy is missing.
        var result = Solve(Spectrum(30, 1, 1, 1, 2, 3), wanted: 3, blockSize: 1);
        output.WriteLine(
            $"single-vector on a triple: {string.Join(", ", result.Pairs.Select(p => $"{p.Eigenvalue:G6}"))} "
            + $"({result.Iterations} back-substitutions, {result.Restarts} restarts)");

        Assert.True(result.Converged);
        Assert.Equal(1.0, result.Pairs[0].Eigenvalue, 1e-8);
        Assert.Equal(1.0, result.Pairs[1].Eigenvalue, 1e-8);
        Assert.Equal(2.0, result.Pairs[2].Eigenvalue, 1e-8);
    }

    [Fact]
    public void ABlockOfTwo_RecoversAnExactDouble()
    {
        var result = Solve(Spectrum(30, 1, 1, 2, 3), wanted: 2, blockSize: 2);
        output.WriteLine(
            $"block 2 on a double: {string.Join(", ", result.Pairs.Select(p => $"{p.Eigenvalue:G6}"))} "
            + $"({result.Iterations} back-substitutions, {result.Restarts} restarts)");

        Assert.True(result.Converged);
        Assert.Equal(1.0, result.Pairs[0].Eigenvalue, 1e-8);
        Assert.Equal(1.0, result.Pairs[1].Eigenvalue, 1e-8);
        Assert.True(result.WorstResidual <= 1e-9);
    }

    [Fact]
    public void ABlockOfThree_RecoversAnExactTriple_AsThreeIndependentVectors()
    {
        var result = Solve(Spectrum(30, 1, 1, 1, 2, 3), wanted: 3, blockSize: 3);
        output.WriteLine(
            $"block 3 on a triple: {string.Join(", ", result.Pairs.Select(p => $"{p.Eigenvalue:G6}"))} "
            + $"({result.Iterations} back-substitutions, {result.Restarts} restarts)");

        Assert.True(result.Converged);
        foreach (var pair in result.Pairs)
        {
            Assert.Equal(1.0, pair.Eigenvalue, 1e-8);
            Assert.True(pair.Residual <= 1e-9);
        }

        // Three EIGENVALUES near 1 could still be one vector reported three times; the
        // claim with teeth is that the pairs span the eigenspace, i.e. they are mutually
        // M-orthonormal (M = I here, so plain dot products).
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                double dot = 0;
                for (int c = 0; c < result.Pairs[i].Vector.Length; c++)
                    dot += result.Pairs[i].Vector[c] * result.Pairs[j].Vector[c];
                Assert.Equal(i == j ? 1.0 : 0.0, dot, 1e-8);
            }
        }
    }

    [Fact]
    public void ABlockOfFour_RecoversAnExactQuadruple()
    {
        // The fixture is 60-dimensional with a 60-vector cap, and the room is load-bearing:
        // a block of size b spends its Krylov budget b columns per step, so on the first
        // 30-dimensional draft a block of four got seven steps per run — not enough to
        // drive a 1e-9 residual — and needed eight restarts for an answer a roomier run
        // reaches directly. The block size multiplies the budget's consumption as well as
        // the eigenspace coverage; both halves belong in the option's documentation.
        var result = Solve(Spectrum(60, 1, 1, 1, 1, 2, 3), wanted: 4, blockSize: 4, maxKrylov: 60);
        output.WriteLine(
            $"block 4 on a quadruple: {string.Join(", ", result.Pairs.Select(p => $"{p.Eigenvalue:G6}"))} "
            + $"({result.Iterations} back-substitutions, {result.Restarts} restarts)");

        Assert.True(result.Converged);
        foreach (var pair in result.Pairs)
            Assert.Equal(1.0, pair.Eigenvalue, 1e-8);
    }

    [Fact]
    public void ABlockBelowTheMultiplicity_GuaranteesOnlyItsOwnWidth()
    {
        // A block of two on a quadruple GUARANTEES two copies (they are in the block's
        // exact-arithmetic span); anything beyond that arrives the way the scalar path's
        // second copy does — reorthogonalization round-off re-seeding the eigenspace, which
        // the scalar triple test shows is not a guarantee. So the assertion here is exactly
        // the guaranteed half, and the rest is reported rather than pinned: deterministic,
        // but an accident of budget and round-off, and a test that pinned it would churn on
        // any arithmetic change while protecting nothing.
        var result = Solve(Spectrum(60, 1, 1, 1, 1, 2, 3), wanted: 4, blockSize: 2, maxKrylov: 60);
        output.WriteLine(
            $"block 2 on a quadruple: {string.Join(", ", result.Pairs.Select(p => $"{p.Eigenvalue:G6}"))} "
            + $"({result.Iterations} back-substitutions, {result.Restarts} restarts)");

        Assert.Equal(1.0, result.Pairs[0].Eigenvalue, 1e-8);
        Assert.Equal(1.0, result.Pairs[1].Eigenvalue, 1e-8);
    }

    [Fact]
    public void ABlockRun_OnADistinctSpectrum_AgreesWithTheScalarPath()
    {
        // No multiplicity anywhere: the block path must return the same answer the scalar
        // path does, to eigen-solver accuracy, or it is a different algorithm rather than
        // a wider one.
        var spectrum = Spectrum(40, 1.0, 1.7, 2.4, 5.0, 9.0);
        var scalar = Solve(spectrum, wanted: 4, blockSize: 1);
        var block = Solve(spectrum, wanted: 4, blockSize: 3);

        Assert.True(scalar.Converged);
        Assert.True(block.Converged);
        for (int i = 0; i < 4; i++)
            Assert.Equal(scalar.Pairs[i].Eigenvalue, block.Pairs[i].Eigenvalue, 1e-9);
    }

    // ---- through the solvers -----------------------------------------------------------

    [Fact]
    public void ModalSolve_WithABlock_MatchesTheScalarPathOnARealModel()
    {
        // A real mesh splits its degeneracies, so the block buys nothing here — which is
        // exactly what makes it the agreement fixture: same frequencies, same shapes'
        // physics, through the public option.
        var mesh = ModalFixtures.Beam(80, 12, 8, 10, 2, 2, ElementOrder.Quadratic);
        var model = new StructuralModel(mesh, ModalFixtures.Steel);
        model.Fix(StructuredTetMesh.XMin);

        var scalar = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 4 });
        var block = ModalSolver.Solve(
            model, new ModalSolveOptions { ModeCount = 4, BlockSize = 2 });

        Assert.Equal(2, block.Report.BlockSize);
        Assert.Equal(1, scalar.Report.BlockSize);
        for (int i = 1; i <= 4; i++)
        {
            double a = scalar.Mode(i).Frequency;
            double b = block.Mode(i).Frequency;
            output.WriteLine($"mode {i}: scalar {a:F4} Hz, block {b:F4} Hz");
            Assert.Equal(a, b, a * 1e-7);
        }
    }

    [Fact]
    public void BucklingSolve_WithABlock_MatchesTheScalarPathOnTheColumn()
    {
        var (model, _) = BucklingFixtures.Column(
            ColumnEnds.FixedFree, 120.0, 6.0, 12, 1, ElementOrder.Quadratic);
        var statics = StructuralSolver.Solve(model);

        var scalar = BucklingSolver.Solve(statics, new BucklingSolveOptions { ModeCount = 2 });
        var block = BucklingSolver.Solve(
            statics, new BucklingSolveOptions { ModeCount = 2, BlockSize = 2 });

        for (int i = 1; i <= 2; i++)
        {
            double a = scalar.Mode(i).LoadFactor;
            double b = block.Mode(i).LoadFactor;
            output.WriteLine($"mode {i}: scalar factor {a:G10}, block {b:G10}");
            Assert.Equal(a, b, Math.Abs(a) * 1e-7);
        }
    }

    [Fact]
    public void BlockSizeOptions_AreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ModalSolveOptions { BlockSize = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ModalSolveOptions { BlockSize = 9 });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BucklingSolveOptions { BlockSize = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BucklingSolveOptions { BlockSize = 9 });
    }
}
