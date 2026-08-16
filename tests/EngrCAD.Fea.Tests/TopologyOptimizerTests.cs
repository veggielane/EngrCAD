using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Verification for SIMP compliance minimisation.
///
/// <para><b>Nothing here asserts that the answer looks like a truss</b>, which is this
/// feature's actual failure mode: a plausible picture is persuasive, and no other output in
/// this repository flatters itself so readily. Every claim below is a closed form, an
/// identity, or a mutation that must fail.</para>
///
/// <para><b>The published 99-line-paper compliances are NOT reproducible here, and pretending
/// otherwise would be the worst of the available options.</b> Those figures are for 2D
/// plane-stress problems on structured 4-node quadrilaterals; this solves 3D elasticity on
/// tetrahedra. The dimensionality, the element and the discretisation all differ, so a number
/// agreeing would be a coincidence and a number disagreeing would prove nothing. What IS
/// available is better: two problems whose SIMP optimum has a closed form (a uniform bar, and
/// a stepped bar whose optimum is the classical fully-stressed design), a finite-difference
/// check of the sensitivity against the production evaluation, the volume constraint as an
/// identity, and the two defects a filter exists to prevent measured with and without
/// one.</para>
/// </summary>
public sealed class TopologyOptimizerTests(ITestOutputHelper output)
{
    /// <summary>
    /// <b>The closed form for a uniform bar: <c>c = c0 / f^p</c>, exactly.</b>
    ///
    /// <para>Under a uniform uniaxial stress every element's strain energy is its own volume
    /// times one energy density, so the uniform field is a stationary point of the optimality
    /// criteria at every penalty — which is why this converges in one iteration and why it is
    /// a measurement of the SIMP interpolation and the volume constraint rather than of the
    /// search. The search has its own closed form below.</para>
    ///
    /// <para><b>It caught a real defect</b>, which is the reason it earns its place: with the
    /// volume constraint stated on the DESIGN variables rather than on the physical density,
    /// the run returned <c>1.79·c0</c> at <c>p = 1</c> against this closed form's
    /// <c>2.00·c0</c> — a compliance BELOW the convex optimum, reachable only by holding more
    /// material than the constraint allowed, because a row-normalised filter is not
    /// volume-preserving.</para>
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(3.0)]
    public void UniformBar_MatchesTheClosedFormCompliance(double penalty)
    {
        const double fraction = 0.5;
        var model = TopologyFixtures.Bar();
        double solid = 2 * StructuralSolver.Solve(model).Report.StrainEnergy;
        // The full-density compliance is itself a closed form: F·(FL/EA).
        Assert.Equal(
            1000 * 1000 * TopologyFixtures.BarLength
                / (TopologyFixtures.BarModulus * TopologyFixtures.BarSide * TopologyFixtures.BarSide),
            solid, 9);

        var result = TopologyOptimizer.Minimize(model, new TopologyOptions
        {
            VolumeFraction = fraction,
            FilterRadius = 6.0,
            Penalty = penalty,
        });

        foreach (double density in result.Density)
            Assert.Equal(fraction, density, 12);
        double expected = solid / Math.Pow(fraction, penalty);
        output.WriteLine(
            $"p={penalty}: c = {result.Compliance:G10}, closed form {expected:G10}, "
            + $"ratio {result.Compliance / expected:G10}");
        Assert.Equal(expected, result.Compliance, 9);
    }

    /// <summary>
    /// <b>The closed form for the SEARCH: a two-step series chain reaches the fully-stressed
    /// design.</b>
    ///
    /// <para>A bar carrying <c>N1</c> over its first half and <c>N2</c> over its second has
    /// compliance <c>(L/2)(N1²/rho1 + N2²/rho2)/(EA)</c>; minimising that subject to
    /// <c>rho1 + rho2 = 2f</c> gives <c>rho_i = 2f·N_i/(N1 + N2)</c> and
    /// <c>c = (L/2)(N1 + N2)²/(2f·E·A)</c>. The uniform start is NOT that answer, so the
    /// optimality-criteria iteration has to find it.</para>
    ///
    /// <para>Run with NO filter deliberately: the filter would smear the step at mid-length
    /// into the comparison, and it is verified separately by the two defects it exists to
    /// prevent. Compared as an ACCURACY rather than an identity because the mid-length load is
    /// spread equally over the plane's nodes, which is self-equilibrated but is not the
    /// consistent distribution of a traction (an interior plane has no facets to state one
    /// on).</para>
    /// </summary>
    [Theory]
    [InlineData(0.4)]
    [InlineData(0.5)]
    public void SteppedBar_ReachesTheFullyStressedDesign(double fraction)
    {
        const double midForce = 800, endForce = 400;
        double n1 = midForce + endForce, n2 = endForce;
        var model = TopologyFixtures.SteppedBar(midForce, endForce);

        var result = TopologyOptimizer.Minimize(model, new TopologyOptions
        {
            VolumeFraction = fraction,
            FilterRadius = 6.0,
            Filter = TopologyFilter.None,
            Penalty = 1.0,
            MaxIterations = 400,
            ChangeTolerance = 1e-5,
        });

        double expected = TopologyFixtures.BarLength / 2 * (n1 + n2) * (n1 + n2)
            / (2 * fraction * TopologyFixtures.BarModulus
               * TopologyFixtures.BarSide * TopologyFixtures.BarSide);
        var (first, second) = TopologyFixtures.HalfMeans(model.Mesh, result.Density);
        output.WriteLine(
            $"f={fraction}: c = {result.Compliance:G8} against {expected:G8} "
            + $"({result.Compliance / expected:G8}); rho = {first:F5}, {second:F5} against "
            + $"{2 * fraction * n1 / (n1 + n2):F5}, {2 * fraction * n2 / (n1 + n2):F5}");

        Assert.InRange(result.Compliance / expected, 1.0, 1.005);
        Assert.Equal(2 * fraction * n1 / (n1 + n2), first, 2);
        Assert.Equal(2 * fraction * n2 / (n1 + n2), second, 2);
    }

    /// <summary>
    /// <b>The sensitivity against a central finite difference, through the PRODUCTION
    /// evaluation.</b>
    ///
    /// <para>This is the assertion with the most teeth in the file. It catches a wrong
    /// penalty exponent, a wrong sign, a missing <c>rho^(p-1)</c>, an energy taken at the
    /// wrong quadrature rule, and — with the density filter on — a transposed filter matrix,
    /// all of which leave a plausible smooth field behind. It compares the gradient against
    /// the compliance the SAME object computes, so the two cannot agree by sharing a
    /// mistake.</para>
    ///
    /// <para><see cref="TopologyFilter.Sensitivity"/> is deliberately absent: its filtered
    /// sensitivity is not the gradient of anything, so it would fail this by construction —
    /// which is exactly why it is documented as a heuristic and why the density filter is the
    /// default.</para>
    /// </summary>
    [Theory]
    [InlineData(TopologyFilter.None)]
    [InlineData(TopologyFilter.Density)]
    public void Sensitivity_MatchesAFiniteDifference(TopologyFilter filter)
    {
        var model = TopologyFixtures.Bar(nx: 6);
        var options = new TopologyOptions
        {
            VolumeFraction = 0.5,
            FilterRadius = 8.0,
            Penalty = 3.0,
            Filter = filter,
        };
        var (evaluator, _) = TopologyOptimizer.BuildEvaluator(model, options);
        int count = model.Mesh.ElementCount;
        var design = new double[count];
        for (int e = 0; e < count; e++)
            design[e] = 0.3 + 0.5 * ((e * 29) % 13) / 12.0;

        evaluator.Evaluate(design);
        var analytic = (double[])evaluator.DesignSensitivity.Clone();

        double worst = 0;
        for (int probe = 0; probe < count; probe += Math.Max(1, count / 7))
        {
            const double delta = 1e-6;
            double keep = design[probe];
            design[probe] = keep + delta;
            double up = evaluator.Evaluate(design);
            design[probe] = keep - delta;
            double down = evaluator.Evaluate(design);
            design[probe] = keep;
            double numeric = (up - down) / (2 * delta);
            worst = Math.Max(worst, Math.Abs(numeric - analytic[probe]) / Math.Abs(numeric));
        }
        output.WriteLine($"{filter}: worst relative difference {worst:G4}");
        // A central difference at delta = 1e-6 carries O(delta^2) truncation plus round-off in
        // the difference of two O(1) compliances, so ~1e-6 is the floor a CORRECT gradient can
        // reach; anything structurally wrong misses by a factor, not by a part per million.
        Assert.True(worst < 1e-5, $"worst relative difference {worst:G4}");
    }

    /// <summary>
    /// <b>The compliance by two independent routes.</b> <c>f'u</c> is the definition;
    /// <c>sum_e rho_e^p·(u_e' k0 u_e)</c> is the sum the sensitivity is built from. They share
    /// only the displacement, so agreeing is two constructions checking each other.
    /// </summary>
    [Fact]
    public void Compliance_AgreesWithTheEnergySumTheSensitivityUses()
    {
        var model = TopologyFixtures.Bar();
        var options = new TopologyOptions
        {
            VolumeFraction = 0.5,
            FilterRadius = 6.0,
            Filter = TopologyFilter.None,
        };
        var (evaluator, _) = TopologyOptimizer.BuildEvaluator(model, options);
        var design = new double[model.Mesh.ElementCount];
        for (int e = 0; e < design.Length; e++)
            design[e] = 0.2 + 0.6 * ((e * 37) % 11) / 10.0;

        double byLoad = evaluator.Evaluate(design);
        double byEnergy = evaluator.ComplianceFromEnergies();
        double relative = Math.Abs(byLoad - byEnergy) / byLoad;
        output.WriteLine($"f'u = {byLoad:G17}, sum rho^p E = {byEnergy:G17} ({relative:G4})");
        Assert.True(relative < 1e-12, $"{relative:G4}");
    }

    /// <summary>
    /// <b>The volume constraint is an IDENTITY at every iteration, not a tolerance.</b> The
    /// optimality-criteria bisection places the multiplier so the constrained volume is met
    /// exactly; measured, the worst relative miss over a whole run is at round-off.
    /// <para>And compliance decreases monotonically under the update — measured rather than
    /// promised, since OC is a heuristic: the run REPORTS its history, and this asserts what
    /// that history contains on this fixture.</para>
    /// </summary>
    [Theory]
    [InlineData(TopologyFilter.Density)]
    [InlineData(TopologyFilter.Sensitivity)]
    [InlineData(TopologyFilter.None)]
    public void VolumeIsMetExactlyAndComplianceFalls(TopologyFilter filter)
    {
        const double fraction = 0.4;
        var model = TopologyFixtures.Cantilever(0, out _);
        var result = TopologyOptimizer.Minimize(model, new TopologyOptions
        {
            VolumeFraction = fraction,
            FilterRadius = 8.0,
            Filter = filter,
            MaxIterations = 60,
        });

        double worst = 0;
        int rises = 0;
        for (int i = 0; i < result.History.Count; i++)
        {
            var row = result.History[i];
            Assert.True(row.VolumeConstraintMet, $"iteration {row.Number} capped by move limits");
            worst = Math.Max(worst, Math.Abs(row.VolumeFraction - fraction) / fraction);
            if (i > 0 && row.Compliance > result.History[i - 1].Compliance)
                rises++;
        }
        output.WriteLine(
            $"{filter}: worst volume miss {worst:G4} over {result.History.Count} iterations, "
            + $"{rises} rises in compliance");
        Assert.True(worst < 1e-12, $"worst relative volume miss {worst:G4}");
        Assert.Equal(0, rises);
        Assert.Equal(fraction, result.VolumeFraction, 12);
    }

    /// <summary>
    /// <b>Refining the mesh at FIXED <c>r_min</c> converges on the same structure — and
    /// without a filter it does not.</b> This is the measurement that proves the filter is
    /// doing its job, and it fails loudly without one.
    ///
    /// <para>The structures are compared in a fixed grid of bins, the one representation two
    /// different meshes share. Measured on 288 / 648 / 1152 elements at <c>r_min = 8</c>: the
    /// filtered fields move 0.014 and 0.014 (density filter) or 0.015 and 0.012 (sensitivity
    /// filter) between levels, against <b>0.137 and 0.068</b> unfiltered — an order of
    /// magnitude, which is what mesh dependence looks like when it is measured rather than
    /// looked at.</para>
    /// </summary>
    [Fact]
    public void RefiningAtFixedRadius_KeepsTheStructure_AndWithoutAFilterDoesNot()
    {
        double[] Run(TopologyFilter filter, int level)
        {
            var model = TopologyFixtures.Cantilever(level, out var mesh);
            var result = TopologyOptimizer.Minimize(model, new TopologyOptions
            {
                VolumeFraction = 0.4,
                FilterRadius = 8.0,
                Filter = filter,
                MaxIterations = 250,
            });
            return TopologyFixtures.Binned(mesh, result.Density);
        }

        var unfiltered = new[] { Run(TopologyFilter.None, 0), Run(TopologyFilter.None, 1),
            Run(TopologyFilter.None, 2) };
        double coarseGap = TopologyFixtures.MeanAbsoluteDifference(unfiltered[0], unfiltered[1]);
        double fineGap = TopologyFixtures.MeanAbsoluteDifference(unfiltered[1], unfiltered[2]);
        output.WriteLine($"None: {coarseGap:F5} then {fineGap:F5}");

        foreach (var filter in new[] { TopologyFilter.Density, TopologyFilter.Sensitivity })
        {
            var levels = new[] { Run(filter, 0), Run(filter, 1), Run(filter, 2) };
            double first = TopologyFixtures.MeanAbsoluteDifference(levels[0], levels[1]);
            double second = TopologyFixtures.MeanAbsoluteDifference(levels[1], levels[2]);
            output.WriteLine($"{filter}: {first:F5} then {second:F5}");
            Assert.True(first < 0.03, $"{filter} coarse-to-mid {first:F5}");
            Assert.True(second < 0.03, $"{filter} mid-to-fine {second:F5}");
            // The mutation: the SAME comparison with the filter removed must be far worse, or
            // the test is measuring a fixture that would pass either way.
            Assert.True(coarseGap > 4 * first, $"{filter}: {coarseGap:F5} against {first:F5}");
        }
    }

    /// <summary>
    /// <b>Checkerboarding, measured.</b> An unfiltered run leaves elements alternating with
    /// all of their face neighbours — the artefact of that pattern overestimating its own
    /// stiffness — and the artificially stiff result shows up as a compliance far BELOW the
    /// filtered one for the same volume. Both halves are asserted: a lower compliance that is
    /// not a better structure is exactly the failure a picture cannot show.
    /// </summary>
    [Fact]
    public void WithoutAFilter_TheAnswerCheckerboardsAndReportsAFalselyLowCompliance()
    {
        var model = TopologyFixtures.Cantilever(0, out var mesh);
        TopologyResult Run(TopologyFilter filter) => TopologyOptimizer.Minimize(
            model,
            new TopologyOptions
            {
                VolumeFraction = 0.4,
                FilterRadius = 8.0,
                Filter = filter,
                MaxIterations = 400,
            });

        var filtered = Run(TopologyFilter.Density);
        var plain = Run(TopologyFilter.None);
        double filteredVariation = TopologyFixtures.NeighbourVariation(mesh, filtered.Density);
        double plainVariation = TopologyFixtures.NeighbourVariation(mesh, plain.Density);
        output.WriteLine(
            $"neighbour variation {filteredVariation:F4} filtered against {plainVariation:F4} "
            + $"unfiltered; compliance {filtered.Compliance:G7} against {plain.Compliance:G7}");

        Assert.True(plainVariation > 10 * filteredVariation,
            $"{plainVariation:F4} against {filteredVariation:F4}");
        Assert.True(plain.Compliance < 0.8 * filtered.Compliance,
            $"{plain.Compliance:G7} against {filtered.Compliance:G7}");
    }

    /// <summary>
    /// <b>Extraction is exact for the field it is given.</b> A density above the threshold
    /// everywhere means the level set is the body's own boundary, so the extracted solid must
    /// have exactly the mesh's volume — an identity, since marching tetrahedra emits the
    /// boundary facets verbatim there.
    /// </summary>
    [Fact]
    public void ExtractingAUniformField_ReturnsTheBodyExactly()
    {
        var result = Uniform(0.9);
        var surface = result.ExtractSurface(0.5);
        Assert.True(surface.IsClosed);
        Assert.Equal(result.TotalVolume, MeshMassProperties.Compute(surface).Volume, 9);
        Assert.Equal(1.0, result.ExtractedVolumeFraction(0.5), 9);
    }

    /// <summary>
    /// <b>Extraction is exact for a LINEAR field too</b>, which is the general statement: the
    /// nodal field is piecewise linear over the tetrahedra, so its level set is planar inside
    /// each one and marching tetrahedra reproduces it with no discretisation of its own. A
    /// field ramping along x from 0 to 1 thresholded at t bounds exactly the slab
    /// <c>x &gt;= t·L</c>.
    /// </summary>
    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    public void ExtractingALinearField_BoundsTheExactSlab(double threshold)
    {
        var model = TopologyFixtures.Bar();
        var mesh = model.Mesh;
        var options = new TopologyOptions { VolumeFraction = 0.5, FilterRadius = 8.0 };
        var (_, volumes) = TopologyOptimizer.BuildEvaluator(model, options);
        var density = new double[mesh.ElementCount];
        for (int e = 0; e < density.Length; e++)
        {
            var nodes = mesh.Element(e);
            double x = 0.25 * (mesh.Position(nodes[0]).X + mesh.Position(nodes[1]).X
                + mesh.Position(nodes[2]).X + mesh.Position(nodes[3]).X);
            density[e] = x / TopologyFixtures.BarLength;
        }
        var result = new TopologyResult(
            model.Mesh, model, null, options, density, density, volumes, [], TopologyStop.Converged, 1, 1);

        var surface = result.ExtractSurface(threshold);
        Assert.True(surface.IsClosed);

        // The volume-weighted nodal average of an AFFINE element field is that field evaluated
        // at the volume-weighted centroid of the node's element star, and on this uniform Kuhn
        // grid that centroid is the node — so the nodal field is exactly `x / L` and the level
        // set is exactly the plane `x = threshold·L`. Hence a plain box, with no allowance for
        // the averaging at all.
        double expected = TopologyFixtures.BarLength * (1 - threshold)
            * TopologyFixtures.BarSide * TopologyFixtures.BarSide;
        double measured = MeshMassProperties.Compute(surface).Volume;
        output.WriteLine($"t={threshold}: extracted {measured:G10}, exact slab {expected:G10}");
        Assert.Equal(expected, measured, 8);
    }

    /// <summary>A run over the standard cantilever produces a field that plots with no new
    /// rendering code, and reports how grey it still is.</summary>
    [Fact]
    public void AResultPublishesADensityFieldAndItsDiscreteness()
    {
        var model = TopologyFixtures.Cantilever(0, out var mesh);
        var result = TopologyOptimizer.Minimize(model, new TopologyOptions
        {
            VolumeFraction = 0.4,
            FilterRadius = 8.0,
        });
        var fields = result.Fields();
        var field = Assert.Single(fields);
        Assert.Equal(TopologyResult.FieldNames.Density, field.Name);
        Assert.Equal(mesh.NodeCount, field.Count);
        Assert.InRange(result.Discreteness, 0, 1);
        Assert.Contains("iterations", result.ToText());
        output.WriteLine(result.ToString());
    }

    private static TopologyResult Uniform(double density)
    {
        var model = TopologyFixtures.Bar();
        var options = new TopologyOptions { VolumeFraction = 0.5, FilterRadius = 8.0 };
        var (_, volumes) = TopologyOptimizer.BuildEvaluator(model, options);
        var field = new double[model.Mesh.ElementCount];
        Array.Fill(field, density);
        return new TopologyResult(
            model.Mesh, model, null, options, field, field, volumes, [], TopologyStop.Converged, 1, 1);
    }
}
