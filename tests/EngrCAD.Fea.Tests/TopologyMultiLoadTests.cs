using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Several load cases with a stated weighting, verified by SYMMETRY rather than by a picture.
///
/// <para><b>The oracle is a mutation that a wrong weighting cannot survive.</b> Two load cases
/// that are mirror images of each other — a downward load at the left of a symmetric span, and
/// its reflection at the right — each optimise into an ASYMMETRIC structure leaning toward their
/// own load. Their equally-weighted SUM optimises into a mirror-symmetric one, because the
/// weighted-sum compliance is itself mirror-symmetric. A bug that dropped a case, mis-weighted
/// one, or accumulated the per-case energies wrong would break that symmetry, and the
/// single-case runs prove the symmetry is genuinely due to combining the two rather than a
/// property either case has alone.</para>
/// </summary>
public sealed class TopologyMultiLoadTests(ITestOutputHelper output)
{
    /// <summary>The MBB mesh and symmetric supports, with a downward load placed at a stated
    /// fraction of the span — so two models can share one operator and differ only in load.</summary>
    private static StructuralModel MbbWithLoadAt(AnalysisMesh mesh, double spanFraction)
    {
        double span = TopologyFixtures.MbbSpan, th = TopologyFixtures.MbbThickness, depth = TopologyFixtures.MbbDepth;
        var model = new StructuralModel(mesh, Materials.Steel);
        double support = 0.12 * span, load = 0.08 * span, yHi = th + 1;
        model.Fix(Facets.And(Facets.Tag(StructuredTetMesh.ZMin),
            Facets.InBox(new Aabb((-1, -1, -1), (support, yHi, 1)))), Dof.Y | Dof.Z);
        model.Fix(Facets.And(Facets.Tag(StructuredTetMesh.ZMin),
            Facets.InBox(new Aabb((span - support, -1, -1), (span + 1, yHi, 1)))), Dof.Y | Dof.Z);
        model.Fix(Facets.And(Facets.Tag(StructuredTetMesh.ZMin),
            Facets.InBox(new Aabb((span / 2 - load, -1, -1), (span / 2 + load, yHi, 1)))), Dof.X);
        double xc = spanFraction * span;
        model.Force(Facets.And(Facets.Tag(StructuredTetMesh.ZMax),
            Facets.InBox(new Aabb((xc - load, -1, depth - 1), (xc + load, yHi, depth + 1)))),
            new Vector3d(0, 0, -3000));
        return model;
    }

    private static AnalysisMesh MbbMesh()
    {
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero,
            new Vector3d(TopologyFixtures.MbbSpan, TopologyFixtures.MbbThickness, TopologyFixtures.MbbDepth),
            24, 3, 8);
        return AnalysisMesh.Of(tets);
    }

    [Fact]
    public void TwoMirrorLoadsGiveAMirrorSymmetricStructure()
    {
        var mesh = MbbMesh();
        var left = MbbWithLoadAt(mesh, 0.30);
        var right = MbbWithLoadAt(mesh, 0.70);
        var options = new TopologyOptions
        {
            VolumeFraction = 0.4, FilterRadius = 4.0, MaxIterations = 60,
        };

        // Each single case ALONE is asymmetric (its mirror differs a lot).
        var leftOnly = TopologyOptimizer.Minimize(left, options);
        double leftAsymmetry = TopologyFixtures.MeanAbsoluteDifference(
            TopologyFixtures.MbbBinned(mesh, leftOnly.Density),
            TopologyFixtures.MirrorX(TopologyFixtures.MbbBinned(mesh, leftOnly.Density)));

        // The two cases combined, equally weighted, is mirror-symmetric.
        var both = TopologyOptimizer.Minimize(
            [new TopologyLoadCase(left, 1.0), new TopologyLoadCase(right, 1.0)], options);
        double bothAsymmetry = TopologyFixtures.MeanAbsoluteDifference(
            TopologyFixtures.MbbBinned(mesh, both.Density),
            TopologyFixtures.MirrorX(TopologyFixtures.MbbBinned(mesh, both.Density)));

        output.WriteLine(
            $"single-case asymmetry {leftAsymmetry:G4}, two-case asymmetry {bothAsymmetry:G4} "
            + $"({bothAsymmetry / leftAsymmetry:P1} of it)");
        // The combined structure is dramatically more symmetric than either case alone — a
        // dropped or mis-weighted case would leave it looking like a single case (~5x this).
        // The residual is not zero because the Kuhn tet mesh is not itself mirror-symmetric
        // (its diagonals pick by index order — the recorded "no reflection preserves Kuhn's
        // diagonals" lesson, the same asymmetry the modal beam's degenerate pair measures), so
        // the ratio is the mutation-proof statement and the absolute bound sits above that floor.
        Assert.True(bothAsymmetry < 0.25 * leftAsymmetry,
            $"two-case field not symmetric: {bothAsymmetry:G4} against single-case {leftAsymmetry:G4}");
        Assert.True(bothAsymmetry < 0.10,
            $"two-case mirror difference {bothAsymmetry:G4} above the mesh floor");
    }

    [Fact]
    public void AnUnequalWeightingLeansTowardTheHeavierCase()
    {
        // Weighting case A three times case B makes the structure lean toward A's load — the
        // statement that the weighting is a real design lever and not decoration.
        var mesh = MbbMesh();
        var left = MbbWithLoadAt(mesh, 0.30);
        var right = MbbWithLoadAt(mesh, 0.70);
        var options = new TopologyOptions
        {
            VolumeFraction = 0.4, FilterRadius = 4.0, MaxIterations = 60,
        };

        var leaning = TopologyOptimizer.Minimize(
            [new TopologyLoadCase(left, 3.0), new TopologyLoadCase(right, 1.0)], options);

        // More material on the left half than the right — the heavier case's side.
        double leftMass = 0, rightMass = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var n = mesh.Element(e);
            double x = 0.25 * (mesh.Position(n[0]).X + mesh.Position(n[1]).X
                + mesh.Position(n[2]).X + mesh.Position(n[3]).X);
            double m = mesh.ElementVolume(e) * leaning.Density[e];
            if (x < TopologyFixtures.MbbSpan / 2) leftMass += m; else rightMass += m;
        }
        output.WriteLine($"left mass {leftMass:G6}, right mass {rightMass:G6} "
            + $"(left/right {leftMass / rightMass:G4})");
        Assert.True(leftMass > rightMass * 1.05, "the heavier case's side is not heavier");
    }

    [Fact]
    public void OneCaseEqualsTheSingleModelForm_BitForBit()
    {
        // Minimize(model) IS Minimize([case(model, 1)]) — one implementation, and the weight
        // of exactly 1.0 folds in as itself, so the fields are byte-identical.
        var a = TopologyOptimizer.Minimize(TopologyFixtures.Bar(),
            new TopologyOptions { VolumeFraction = 0.5, FilterRadius = 6.0, MaxIterations = 15 });
        var b = TopologyOptimizer.Minimize(
            [new TopologyLoadCase(TopologyFixtures.Bar(), 1.0)],
            new TopologyOptions { VolumeFraction = 0.5, FilterRadius = 6.0, MaxIterations = 15 });
        for (int e = 0; e < a.Density.Count; e++)
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(a.Density[e]),
                BitConverter.DoubleToInt64Bits(b.Density[e]));
    }

    [Fact]
    public void MismatchedOperatorsAndBadWeightsRefuseByName()
    {
        var options = new TopologyOptions { VolumeFraction = 0.5, FilterRadius = 6.0 };

        // Two cases on DIFFERENT mesh instances cannot share a factorization.
        var m1 = TopologyFixtures.Bar();
        var m2 = TopologyFixtures.Bar();  // a fresh mesh instance
        var differentMesh = Assert.Throws<FeaException>(() => TopologyOptimizer.Minimize(
            [new TopologyLoadCase(m1, 1.0), new TopologyLoadCase(m2, 1.0)], options));
        Assert.Contains("different AnalysisMesh", differentMesh.Message);

        // A non-positive weight is refused, before any operator check.
        var badWeight = Assert.Throws<FeaException>(() => TopologyOptimizer.Minimize(
            [new TopologyLoadCase(m1, 1.0), new TopologyLoadCase(m1, -1.0)], options));
        Assert.Contains("positive", badWeight.Message);
    }
}
