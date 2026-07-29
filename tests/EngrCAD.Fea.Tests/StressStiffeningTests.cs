using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Stress stiffening: the natural frequencies of a PRELOADED structure, from the same
/// geometric stiffness the buckling solver uses.
///
/// <para><b>The verification is the identity that ties the two features together.</b> For a
/// pinned-pinned beam under an axial load P, Euler-Bernoulli theory gives
/// <c>omega_n²(P) / omega_n²(0) = 1 + P/P_cr,n</c> with P positive in TENSION — the two
/// operators share the same sine eigenfunctions, so the ratio is exact rather than
/// asymptotic. Two limits fall out of it and both are checked: tension raises every
/// frequency without bound, and at <c>P = -P_cr</c> the first frequency reaches exactly zero,
/// which is buckling. A geometric stiffness with a sign error, a factor of two, or a
/// quadrature that is not exact fails this comparison at the first non-zero preload.</para>
/// </summary>
public class StressStiffeningTests(ITestOutputHelper output)
{
    private const double Length = 120.0;
    private const double Side = 6.0;

    /// <summary>A pinned-pinned column with its reference COMPRESSION, plus the critical load
    /// factor of that reference case — the quantity the frequency law is stated against.</summary>
    private static (StructuralModel Model, double Reference, double CriticalFactor, StructuralResults Statics)
        PreloadedColumn(int nx, int across)
    {
        var (model, reference) = BucklingFixtures.Column(
            ColumnEnds.PinnedPinned, Length, Side, nx, across, ElementOrder.Quadratic);
        var statics = StructuralSolver.Solve(model);
        var buckling = BucklingSolver.Solve(statics, new BucklingSolveOptions { ModeCount = 1 });
        return (model, reference, buckling.CriticalLoadFactor, statics);
    }

    [Fact]
    public void FrequencySquaredIsLinearInThePreload_AndVanishesAtTheCriticalLoad()
    {
        var (model, _, critical, statics) = PreloadedColumn(16, 2);

        // The reference case is a COMPRESSION, so a prestress scale of s corresponds to
        // P = -s (tension positive) and the law reads omega²(s)/omega²(0) = 1 - s/lambda_cr.
        double baseline = 0;
        var measured = new List<(double Scale, double Ratio, double Predicted)>();
        foreach (double scale in new[] { 0.0, -critical, -0.5 * critical, 0.25 * critical, 0.5 * critical, 0.9 * critical })
        {
            var results = ModalSolver.Solve(model, new ModalSolveOptions
            {
                ModeCount = 1,
                Prestress = statics,
                PrestressScale = scale,
                // The buckling solver's tolerance rather than the modal default, and for the
                // same measured reason: near the critical load K + s·Kg is nearly singular BY
                // CONSTRUCTION, so the residual's kappa(K)-proportional floor rises with the
                // preload. At 0.9·P_cr this model stalls at 2.79e-9 against the modal
                // default's 1e-9 — the mode is right (its frequency lands on the law below to
                // eight digits), only the acceptance test is unreachable.
                Tolerance = 1e-7,
            });
            double lambda = results.Mode(1).Eigenvalue;
            if (scale == 0)
            {
                baseline = lambda;
                Assert.Equal(0.0, results.Report.PrestressScale);
            }
            double ratio = lambda / baseline;
            double predicted = 1.0 - scale / critical;
            measured.Add((scale / critical, ratio, predicted));
            output.WriteLine(
                $"P/P_cr = {-scale / critical,6:F3} ({(scale < 0 ? "tension " : "compress")}): "
                + $"f = {results.Mode(1).Frequency,10:N2} Hz, "
                + $"omega²/omega²(0) = {ratio:F9} against a predicted {predicted:F9}, "
                + $"relative {(predicted == 0 ? 0 : (ratio - predicted) / predicted):E2}, "
                + $"residual {results.Mode(1).Residual:E2}");
        }

        // Every point on the line, tension and compression alike, and the band is TIGHT: the
        // law is exact for a Euler-Bernoulli beam, and it turns out to be very nearly exact
        // for the discrete three-dimensional system too, because the buckling shape and the
        // first vibration shape of this column are the same half sine — so the ratio of two
        // Rayleigh quotients over one vector is the ratio of their numerators. A loose band
        // here would let a geometric stiffness that is right to a percent through.
        foreach (var (scale, ratio, predicted) in measured)
        {
            Assert.True(
                Math.Abs(ratio - predicted) < 1e-6 * Math.Max(1.0, Math.Abs(predicted)),
                $"at P/P_cr = {-scale:F3} the ratio was {ratio:F9}, predicted {predicted:F9}");
        }

        // The 0.9·P_cr point is the one with teeth: 90% of the way to buckling the frequency
        // has fallen to about a third of its unloaded value, which nothing but a correct
        // geometric stiffness produces.
        var nearCritical = measured[^1];
        output.WriteLine(
            $"at 90% of the critical load the frequency ratio is "
            + $"{Math.Sqrt(nearCritical.Ratio):F4} of the unloaded value "
            + $"(predicted {Math.Sqrt(0.1):F4})");
        Assert.Equal(Math.Sqrt(0.1), Math.Sqrt(nearCritical.Ratio), 0.02);
    }

    [Fact]
    public void AtTheCriticalLoadTheModalProblemIsSingular_AndSaysSo()
    {
        // The exact statement, and the sharpest link between the two solvers: at
        // s = lambda_cr the matrix K + s·Kg is singular BY DEFINITION — its null vector is
        // the buckling shape — so there is no positive definite stiffness left to factor and
        // no vibration problem to solve. A solver that quietly returned a small positive
        // frequency here would be reporting the square root of round-off.
        var (model, _, critical, statics) = PreloadedColumn(12, 1);

        var error = Assert.Throws<FeaException>(() => ModalSolver.Solve(model, new ModalSolveOptions
        {
            ModeCount = 1,
            Prestress = statics,
            PrestressScale = critical * 1.001,
        }));
        output.WriteLine(error.Message);
        Assert.Contains("not positive definite", error.Message);
        Assert.Contains("critical buckling load", error.Message);
    }

    [Fact]
    public void TensionRaisesEveryFrequencyAndCompressionLowersIt()
    {
        var (model, _, critical, statics) = PreloadedColumn(12, 1);

        var unloaded = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 3 });
        var compressed = ModalSolver.Solve(model, new ModalSolveOptions
        {
            ModeCount = 3,
            Prestress = statics,
            PrestressScale = 0.5 * critical,
        });
        var stretched = ModalSolver.Solve(model, new ModalSolveOptions
        {
            ModeCount = 3,
            Prestress = statics,
            PrestressScale = -0.5 * critical,
        });

        for (int n = 1; n <= 3; n++)
        {
            output.WriteLine(
                $"mode {n}: {compressed.Mode(n).Frequency,10:N2} Hz compressed, "
                + $"{unloaded.Mode(n).Frequency,10:N2} Hz free, "
                + $"{stretched.Mode(n).Frequency,10:N2} Hz in tension");
            Assert.True(compressed.Mode(n).Frequency < unloaded.Mode(n).Frequency);
            Assert.True(stretched.Mode(n).Frequency > unloaded.Mode(n).Frequency);
        }
    }

    [Fact]
    public void AZeroScalePrestressIsBitIdenticalToNoPrestress()
    {
        // The neutrality rule every optional feature in this repo carries: asking for a
        // prestress and scaling it to nothing must not change a single bit of the answer,
        // which is only true because the assembly SKIPS the combination rather than adding a
        // zero matrix (adding one would change the summation order).
        var (model, _, _, statics) = PreloadedColumn(8, 1);

        var plain = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 3 });
        var zero = ModalSolver.Solve(model, new ModalSolveOptions
        {
            ModeCount = 3,
            Prestress = statics,
            PrestressScale = 0.0,
        });

        for (int n = 1; n <= 3; n++)
        {
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(plain.Mode(n).Eigenvalue),
                BitConverter.DoubleToInt64Bits(zero.Mode(n).Eigenvalue));
        }
        output.WriteLine(
            $"three eigenvalues bit-identical with and without a zero-scaled prestress "
            + $"(first {plain.Mode(1).Eigenvalue:G17})");
    }

    [Fact]
    public void APrestressFromAnotherMeshIsRefusedByName()
    {
        var (model, _, _, statics) = PreloadedColumn(8, 1);
        var (other, _) = BucklingFixtures.Column(
            ColumnEnds.PinnedPinned, Length, Side, 8, 1, ElementOrder.Quadratic);

        // The SAME dimensions and the same element count, deliberately: a check on counts or
        // positions would pass this, and the node numbering is what actually has to match.
        Assert.Equal(model.Mesh.NodeCount, other.Mesh.NodeCount);
        var error = Assert.Throws<FeaException>(() => ModalSolver.Solve(other, new ModalSolveOptions
        {
            ModeCount = 1,
            Prestress = statics,
        }));
        output.WriteLine(error.Message);
        Assert.Contains("different AnalysisMesh instance", error.Message);
        Assert.Contains("node numbering", error.Message);
    }

    [Fact]
    public void TheReportNamesTheScaleThatWasApplied()
    {
        var (model, _, critical, statics) = PreloadedColumn(8, 1);
        var results = ModalSolver.Solve(model, new ModalSolveOptions
        {
            ModeCount = 1,
            Prestress = statics,
            PrestressScale = 0.5 * critical,
        });
        Assert.Equal(0.5 * critical, results.Report.PrestressScale);
        Assert.Contains("stress-stiffened by", results.Report.ToText());
        output.WriteLine(results.Report.ToText());
    }
}
