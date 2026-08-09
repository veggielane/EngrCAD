using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Verification for superconvergent patch recovery and the Zienkiewicz-Zhu error estimator.
///
/// <para><b>The claim under test is a RATE, not a value.</b> Recovery does not make any one
/// answer right; it makes the nodal stress converge one order faster than direct evaluation
/// does. So the deliverable is a convergence table on a manufactured solution — the only
/// fixture with no competing modelling error in the way — measured as an L2 norm INSIDE the
/// elements over a refinement sequence, which is the form the recorded fixture traps
/// (a probe at a stationary point, an error read only at nodes) force.</para>
///
/// <para>The companion claim is the estimator's <b>effectivity index</b>: the estimated
/// error over the true error, which must approach 1 as the mesh refines. That is the one
/// measurement that says the estimator is an estimate of the error rather than merely a
/// number that gets smaller.</para>
/// </summary>
public class StressRecoveryTests(ITestOutputHelper output)
{
    private static readonly Material Steel = new("recovery steel", 210_000, 0.3);
    private static readonly Vector3d Size = new(2.0, 1.5, 1.0);
    private const double Amplitude = 1e-3;

    /// <summary>The same cubic manufactured field the convergence suite uses — cubic so that
    /// neither element order reproduces it exactly and reports an infinite rate.</summary>
    private static Vector3d Exact(Vector3d p) => new(
        Amplitude * (p.X * p.X * p.X + p.Y * p.Y),
        Amplitude * (p.Y * p.Y * p.Y + p.Z * p.Z),
        Amplitude * (p.Z * p.Z * p.Z + p.X * p.X));

    private static SymmetricTensor3 ExactStrain(Vector3d p) => new(
        Amplitude * 3 * p.X * p.X,
        Amplitude * 3 * p.Y * p.Y,
        Amplitude * 3 * p.Z * p.Z,
        Amplitude * 0.5 * (2 * p.Y),
        Amplitude * 0.5 * (2 * p.X),
        Amplitude * 0.5 * (2 * p.Z));

    private static Vector3d Body(Vector3d p)
    {
        double lambda = Steel.Lambda, mu = Steel.Mu;
        var gradDiv = new Vector3d(6 * Amplitude * p.X, 6 * Amplitude * p.Y, 6 * Amplitude * p.Z);
        var laplacian = new Vector3d(
            Amplitude * (6 * p.X + 2), Amplitude * (6 * p.Y + 2), Amplitude * (6 * p.Z + 2));
        return -((lambda + mu) * gradDiv + mu * laplacian);
    }

    /// <summary>The exact Cauchy stress, as a Voigt 6-vector with engineering shear.</summary>
    private static void ExactStress(Vector3d p, Span<double> stress)
    {
        var e = ExactStrain(p);
        // The tensor carries TENSOR shear; the Voigt strain the constitutive law consumes
        // carries ENGINEERING shear, hence the doubling. Getting this wrong would inflate
        // both columns of the table equally and hide itself.
        Span<double> strain = [e.Xx, e.Yy, e.Zz, 2 * e.Xy, 2 * e.Yz, 2 * e.Xz];
        Steel.Stress(strain, stress);
    }

    private readonly record struct Run(
        double H, int Elements, double Direct, double Recovered, double TrueEnergy,
        double EstimatedEnergy, double Effectivity, int Fallbacks);

    private Run Solve(ElementOrder order, int divisions)
    {
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, Size, 2 * divisions, 2 * divisions, divisions);
        var mesh = order == ElementOrder.Linear ? AnalysisMesh.Of(tets) : AnalysisMesh.Quadratic(tets);

        var model = new StructuralModel(mesh, Steel);
        foreach (int node in model.NodesOn(Facets.All))
            model.PrescribeNode(node, Exact(mesh.Position(node)));
        model.BodyForce(Body);

        var results = StructuralSolver.Solve(model);

        results.Recovery = StressRecovery.Direct;
        var direct = results.NodalStress.ToArray();
        results.Recovery = StressRecovery.Superconvergent;
        var recovered = results.NodalStress.ToArray();

        // L2 error of each nodal field against the exact stress, integrated INSIDE the
        // elements at a rule rich enough for the integrand - never sampled at the nodes,
        // which is where a nodal field is by construction closest to whatever produced it.
        var rule = order == ElementOrder.Linear ? TetQuadrature.Degree3 : TetQuadrature.Degree5;
        int perElement = mesh.NodesPerElement;
        double directError = 0, recoveredError = 0, exactNorm = 0;
        double trueEnergy = 0;

        var positions = new Vector3d[10];
        var shape = new double[10];
        var grad = new Vector3d[10];
        var exact = new double[6];
        var ue = new double[30];
        // Heap, not stackalloc: this is read inside the quadrature loop, and a stackalloc
        // there grows the frame every iteration instead of popping - which on the finest
        // mesh is a stack overflow rather than a slow test.
        var fe = new double[6];

        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var nodes = mesh.Element(e);
            for (int i = 0; i < perElement; i++)
                positions[i] = mesh.Position(nodes[i]);
            for (int i = 0; i < perElement; i++)
            {
                var u = results.DisplacementAt(nodes[i]);
                ue[3 * i] = u.X;
                ue[3 * i + 1] = u.Y;
                ue[3 * i + 2] = u.Z;
            }

            for (int q = 0; q < rule.Count; q++)
            {
                var (r, s, t) = rule.Point(q);
                if (!TetElement.ShapeGradients(
                        mesh.Order, positions.AsSpan(0, perElement), r, s, t, grad, out double detJ))
                    continue;
                double weight = rule.Weight(q) * detJ;
                TetElement.ShapeValues(mesh.Order, r, s, t, shape);

                var at = Vector3d.Zero;
                for (int i = 0; i < perElement; i++)
                    at += positions[i] * shape[i];
                ExactStress(at, exact);

                directError += weight * Distance(direct, nodes, shape, perElement, exact);
                recoveredError += weight * Distance(recovered, nodes, shape, perElement, exact);
                for (int c = 0; c < 6; c++)
                    exactNorm += weight * exact[c] * exact[c];

                // The TRUE error, in the same energy norm the estimator uses, so the
                // effectivity index compares like with like.
                results.VoigtStressAt(
                    e, positions.AsSpan(0, perElement), ue.AsSpan(0, 3 * perElement), r, s, t, fe);
                trueEnergy += weight * EnergyNorm(exact, fe);
            }
        }

        var estimate = results.ErrorEstimate;
        double trueNorm = Math.Sqrt(trueEnergy);
        return new Run(
            Size.Z / divisions,
            mesh.ElementCount,
            Math.Sqrt(directError / exactNorm),
            Math.Sqrt(recoveredError / exactNorm),
            trueNorm,
            estimate.ErrorNorm,
            trueNorm > 0 ? estimate.ErrorNorm / trueNorm : double.NaN,
            estimate.FallbackNodes);
    }

    private static double Distance(
        SymmetricTensor3[] nodal, ReadOnlySpan<int> nodes, double[] shape, int count,
        ReadOnlySpan<double> exact)
    {
        double xx = 0, yy = 0, zz = 0, xy = 0, yz = 0, zx = 0;
        for (int i = 0; i < count; i++)
        {
            var v = nodal[nodes[i]];
            double n = shape[i];
            xx += n * v.Xx;
            yy += n * v.Yy;
            zz += n * v.Zz;
            xy += n * v.Xy;
            yz += n * v.Yz;
            zx += n * v.Xz;
        }
        double d0 = xx - exact[0], d1 = yy - exact[1], d2 = zz - exact[2];
        double d3 = xy - exact[3], d4 = yz - exact[4], d5 = zx - exact[5];
        return d0 * d0 + d1 * d1 + d2 * d2 + d3 * d3 + d4 * d4 + d5 * d5;
    }

    /// <summary>The energy norm of a stress difference: <c>d' S d</c> with S the compliance,
    /// written from the isotropic constants so the comparison shares no code with the
    /// estimator's cached matrix inverse.</summary>
    private static double EnergyNorm(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        double e = Steel.YoungsModulus, nu = Steel.PoissonsRatio;
        double g = Steel.ShearModulus;
        Span<double> d = stackalloc double[6];
        for (int i = 0; i < 6; i++)
            d[i] = a[i] - b[i];
        double normal =
            (d[0] * d[0] + d[1] * d[1] + d[2] * d[2]) / e
            - 2 * nu * (d[0] * d[1] + d[1] * d[2] + d[2] * d[0]) / e;
        double shear = (d[3] * d[3] + d[4] * d[4] + d[5] * d[5]) / g;
        return normal + shear;
    }

    [Theory]
    [InlineData(ElementOrder.Linear, 2.0)]
    [InlineData(ElementOrder.Quadratic, 3.0)]
    public void RecoveredStressConvergesAnOrderFasterThanDirectEvaluation(
        ElementOrder order, double theory)
    {
        // The quadratic sequence starts at 2, not 1: a 24-element box has NO interior corner
        // node, so no patch exists, nothing is recovered and the row would measure the
        // fallback rather than the recovery.
        int[] sequence = order == ElementOrder.Linear ? [2, 4, 8] : [2, 3, 4];
        var runs = sequence.Select(n => Solve(order, n)).ToArray();

        output.WriteLine($"{order}: theory {theory - 1:F0} direct, {theory:F0} recovered");
        output.WriteLine("     h    elements       direct    recovered   direct rate  rec. rate");
        for (int i = 0; i < runs.Length; i++)
        {
            var r = runs[i];
            string directRate = "-", recoveredRate = "-";
            if (i > 0)
            {
                double ratio = runs[i - 1].H / r.H;
                directRate = (Math.Log(runs[i - 1].Direct / r.Direct) / Math.Log(ratio)).ToString("F3");
                recoveredRate =
                    (Math.Log(runs[i - 1].Recovered / r.Recovered) / Math.Log(ratio)).ToString("F3");
            }
            output.WriteLine(
                $"{r.H,6:F3} {r.Elements,11:N0} {r.Direct,12:E3} {r.Recovered,12:E3} "
                + $"{directRate,13} {recoveredRate,10}   ({r.Fallbacks} fallback nodes)");
        }

        var finest = runs[^1];
        var previous = runs[^2];
        double h = previous.H / finest.H;
        double directOrder = Math.Log(previous.Direct / finest.Direct) / Math.Log(h);
        double recoveredOrder = Math.Log(previous.Recovered / finest.Recovered) / Math.Log(h);

        // The claim is comparative and is asserted comparatively: the recovered field is
        // BOTH more accurate and converging faster. A bare "the order is about p+1" would
        // pass a recovery that had simply scaled the same field.
        Assert.True(finest.Recovered < finest.Direct,
            $"recovered {finest.Recovered:E3} is not better than direct {finest.Direct:E3}");
        Assert.True(recoveredOrder > directOrder + 0.5,
            $"recovered order {recoveredOrder:F3} is not clearly above direct {directOrder:F3}");
        Assert.True(recoveredOrder > theory - 0.5,
            $"recovered order {recoveredOrder:F3} is below theory {theory:F1}");
        Assert.Equal(0, finest.Fallbacks);
    }

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void TheErrorEstimatorsEffectivityApproachesOne(ElementOrder order)
    {
        // The quadratic sequence starts at 2, not 1: a 24-element box has NO interior corner
        // node, so no patch exists, nothing is recovered and the row would measure the
        // fallback rather than the recovery.
        int[] sequence = order == ElementOrder.Linear ? [2, 4, 8] : [2, 3, 4];
        var runs = sequence.Select(n => Solve(order, n)).ToArray();

        output.WriteLine($"{order}: effectivity = estimated / true error, in the energy norm");
        foreach (var r in runs)
        {
            output.WriteLine(
                $"  h {r.H:F3}, {r.Elements,6:N0} elements: true {r.TrueEnergy:E3}, "
                + $"estimated {r.EstimatedEnergy:E3}, theta = {r.Effectivity:F4}");
        }

        // A ZZ estimator is expected inside roughly [0.8, 1.2] and to tighten with
        // refinement. Both halves are asserted: a constant offset would satisfy the band
        // alone and would mean the estimator was measuring something else that happens to
        // scale the same way.
        double finest = runs[^1].Effectivity;
        Assert.True(finest is > 0.8 and < 1.2, $"effectivity {finest:F4} is outside [0.8, 1.2]");
        Assert.True(
            Math.Abs(finest - 1) <= Math.Abs(runs[0].Effectivity - 1) + 1e-6,
            $"effectivity moved away from 1: {runs[0].Effectivity:F4} then {finest:F4}");
    }

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void ALinearStressFieldIsRecoveredEXACTLY(ElementOrder order)
    {
        // The consistency check every recovery scheme must pass: where the finite-element
        // stress IS the exact field, the recovery must return it unchanged rather than
        // smoothing it. A LINEAR stress field is the strongest form available, because it
        // is in the fitted polynomial's space for both orders while still varying - a
        // constant field would pass any averaging scheme whatever.
        //
        // The field comes from a QUADRATIC displacement, which a 10-node element reproduces
        // exactly; for a 4-node element the stress is constant per element, so the exact
        // linear field is asserted only for the quadratic case and the linear case checks
        // the constant one.
        bool quadratic = order == ElementOrder.Quadratic;
        var tets = StructuredTetMesh.Box(Vector3d.Zero, Size, 4, 3, 2);
        var mesh = quadratic ? AnalysisMesh.Quadratic(tets) : AnalysisMesh.Of(tets);

        Vector3d Field(Vector3d p) => quadratic
            ? new Vector3d(Amplitude * p.X * p.X, Amplitude * p.Y * p.Y, Amplitude * p.Z * p.Z)
            : new Vector3d(Amplitude * p.X, -0.4 * Amplitude * p.Y, 0.7 * Amplitude * p.Z);

        var model = new StructuralModel(mesh, Steel);
        foreach (int node in model.NodesOn(Facets.All))
            model.PrescribeNode(node, Field(mesh.Position(node)));

        // The quadratic field is NOT a solution with zero body force, and prescribing it on
        // the boundary alone would solve a different problem entirely - measured, a 20%
        // "recovery error" that was the fixture's, not the recovery's. div u = 2A(x+y+z) and
        // laplacian u = 2A(1,1,1), so the body force is the constant
        // -2A(lambda + 2mu)(1,1,1). The linear field needs none, its second derivatives
        // being zero, which is why that half passed from the start.
        if (quadratic)
        {
            double f = -2.0 * Amplitude * (Steel.Lambda + 2.0 * Steel.Mu);
            model.BodyForce(_ => new Vector3d(f, f, f));
        }

        var results = StructuralSolver.Solve(model);
        results.Recovery = StressRecovery.Superconvergent;
        var recovered = results.NodalStress;

        // The exact stress at a node, from the field's own analytic strain.
        Span<double> strain = stackalloc double[6];
        Span<double> stress = stackalloc double[6];
        double scale = 0, worst = 0;
        for (int v = 0; v < mesh.NodeCount; v++)
        {
            var p = mesh.Position(v);
            if (quadratic)
            {
                strain[0] = 2 * Amplitude * p.X;
                strain[1] = 2 * Amplitude * p.Y;
                strain[2] = 2 * Amplitude * p.Z;
            }
            else
            {
                strain[0] = Amplitude;
                strain[1] = -0.4 * Amplitude;
                strain[2] = 0.7 * Amplitude;
            }
            strain[3] = strain[4] = strain[5] = 0;
            Steel.Stress(strain, stress);

            var got = recovered[v];
            for (int c = 0; c < 6; c++)
                scale = Math.Max(scale, Math.Abs(stress[c]));
            worst = Math.Max(worst, Math.Abs(got.Xx - stress[0]));
            worst = Math.Max(worst, Math.Abs(got.Yy - stress[1]));
            worst = Math.Max(worst, Math.Abs(got.Zz - stress[2]));
            worst = Math.Max(worst, Math.Abs(got.Xy));
            worst = Math.Max(worst, Math.Abs(got.Yz));
            worst = Math.Max(worst, Math.Abs(got.Xz));
        }

        output.WriteLine(
            $"{order}: worst recovered stress error {worst:E3} of {scale:E3} "
            + $"({results.ErrorEstimate})");
        Assert.True(worst <= 1e-9 * scale, $"recovery is not exact: {worst:E3} of {scale:E3}");

        // And the estimator says so: where the recovery reproduces the finite-element field
        // exactly, the estimated error is zero. That is the identity that makes the
        // estimator meaningful rather than a smoothness measure.
        if (!quadratic)
        {
            Assert.True(results.ErrorEstimate.RelativeError < 1e-10,
                $"estimated error {results.ErrorEstimate.RelativeError:E3} on an exact field");
        }
    }

    [Fact]
    public void AMeshWithNoInteriorNodeReportsAnUNKNOWNErrorRatherThanZero()
    {
        // The trap this exists for: with no interior corner node there is no patch, the
        // "recovered" field IS the finite-element field, and the estimated error is then the
        // distance from something to itself. Measured on this 24-element box: 9.9e-15
        // against a TRUE error of 0.313 - i.e. arithmetic that calls the mesh perfect on
        // exactly the mesh too coarse to assess. NaN is the answer, following the same
        // "not small, unknown" spelling HarmonicResponse.TruncationError uses.
        var tets = StructuredTetMesh.Box(Vector3d.Zero, Size, 2, 2, 1);
        var mesh = AnalysisMesh.Quadratic(tets);
        var model = new StructuralModel(mesh, Steel);
        foreach (int node in model.NodesOn(Facets.All))
            model.PrescribeNode(node, Exact(mesh.Position(node)));
        model.BodyForce(Body);
        var results = StructuralSolver.Solve(model);

        var estimate = results.ErrorEstimate;
        output.WriteLine($"{mesh.ElementCount} elements: {estimate}");
        Assert.Equal(mesh.NodeCount, estimate.FallbackNodes);
        Assert.True(double.IsNaN(estimate.RelativeError), $"got {estimate.RelativeError:E3}");
        Assert.True(double.IsNaN(estimate.ErrorNorm));
        Assert.Contains("UNKNOWN", estimate.ToString());
    }

    [Fact]
    public void TheDefaultIsUnchangedAndSwitchingBackRestoresItBitForBit()
    {
        // The neutrality rule: recovery must be invisible until it is asked for, and asking
        // for it and changing one's mind must not leave a different answer behind.
        var tets = StructuredTetMesh.Box(Vector3d.Zero, Size, 3, 2, 2);
        var mesh = AnalysisMesh.Of(tets);
        var model = new StructuralModel(mesh, Steel);
        foreach (int node in model.NodesOn(Facets.All))
            model.PrescribeNode(node, Exact(mesh.Position(node)));
        model.BodyForce(Body);
        var results = StructuralSolver.Solve(model);

        Assert.Equal(StressRecovery.Direct, results.Recovery);
        var before = results.NodalStress.ToArray();

        results.Recovery = StressRecovery.Superconvergent;
        var recovered = results.NodalStress.ToArray();

        results.Recovery = StressRecovery.Direct;
        var after = results.NodalStress.ToArray();

        bool anyDifferent = false;
        for (int v = 0; v < before.Length; v++)
        {
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(before[v].Xx),
                BitConverter.DoubleToInt64Bits(after[v].Xx));
            if (Math.Abs(recovered[v].Xx - before[v].Xx) > 1e-9 * Math.Abs(before[v].Xx))
                anyDifferent = true;
        }
        // The test would be vacuous if the two agreed; assert that they do not.
        Assert.True(anyDifferent, "recovery changed nothing, so the comparison proves nothing");
    }

    [Fact]
    public void TheEstimatorLocalisesTheErrorWhereTheFieldIsHardest()
    {
        // A coarse mesh on a field whose stress varies fastest at one corner: the estimator
        // must put its worst element there rather than spreading evenly. This is a WEAKER
        // claim than the effectivity index and is asserted as such - it says the per-element
        // map carries information, which is what an adaptive scheme would consume.
        var tets = StructuredTetMesh.Box(Vector3d.Zero, Size, 4, 3, 2);
        var mesh = AnalysisMesh.Of(tets);
        var model = new StructuralModel(mesh, Steel);
        foreach (int node in model.NodesOn(Facets.All))
            model.PrescribeNode(node, Exact(mesh.Position(node)));
        model.BodyForce(Body);
        var results = StructuralSolver.Solve(model);

        var estimate = results.ErrorEstimate;
        Assert.Equal(mesh.ElementCount, estimate.ElementError.Count);

        // The per-element errors compose into the global norm - an identity, not a
        // tolerance, since the global figure is the root of their sum of squares.
        double sum = 0;
        foreach (double e in estimate.ElementError)
            sum += e * e;
        Assert.Equal(estimate.ErrorNorm, Math.Sqrt(sum), estimate.ErrorNorm * 1e-12);

        // The exact stress grows as x^2 + y^2 + z^2, so the far corner is where the field is
        // hardest and where the worst element must sit.
        var worst = mesh.Element(estimate.WorstElement);
        var centroid = Vector3d.Zero;
        for (int i = 0; i < 4; i++)
            centroid += mesh.Position(worst[i]);
        centroid *= 0.25;

        output.WriteLine($"{estimate}; worst element at {centroid}");
        Assert.True(centroid.X > 0.5 * Size.X && centroid.Y > 0.5 * Size.Y,
            $"worst element at {centroid}, expected the high-x, high-y corner");
    }

    /// <summary>
    /// The p+1 cap, MEASURED. The quadratic recovered rate settles near 2.76 against a theory of
    /// 3, and there are two candidate causes: the boundary FILL, which extrapolates a patch
    /// polynomial to nodes outside its own elements (second-order accurate in the extrapolation
    /// distance, and an O(h) volume fraction — so a half-order signature), or the SIMPLEX
    /// superconvergence theory itself, which is weaker on tetrahedra than on the hexahedra SPR
    /// was developed for. This separates them with an interior sub-domain norm: restrict the
    /// recovered-error norm to a fixed central box, away from the boundary fill, and see whether
    /// the rate rises toward 3 there.
    ///
    /// <para><b>The finding: the boundary fill is the dominant cause.</b> On a [3, 4, 6]-division
    /// quadratic sequence the whole-domain rate is <b>2.758</b> (reproducing the recorded 2.76),
    /// and over the central [0.25, 0.75] box it rises to <b>2.883</b> — closing about half the
    /// measured gap to theory 3 (0.242 → 0.117). That agrees in direction with the recorded
    /// experiment that excluding boundary patches ENTIRELY reached 3.08/2.91, essentially theory.
    /// The small residual below 3 in the interior box is within the noise of a single last-pair
    /// rate and of the same size as that recorded excluded-boundary energy rate (2.91), so this
    /// study does NOT establish a separate simplex-theory cap: if the tetrahedral Gauss points
    /// not being tensor-product Barlow points cost anything, it is within measurement noise of
    /// the fill's effect. Recorded in CLAUDE.md and design.md §3i.</para>
    /// </summary>
    [Fact]
    public void TheInteriorSubDomainRateSeparatesTheBoundaryFillFromSimplexTheory()
    {
        int[] divisions = [3, 4, 6];
        const double frac = 0.25;   // the central [0.25, 0.75] box, well inside the fill layer

        var hs = new List<double>();
        var whole = new List<double>();
        var interior = new List<double>();
        output.WriteLine(
            "quadratic recovered stress L2 error, whole domain vs central [0.25, 0.75] box "
            + "(theory 3):");
        foreach (int d in divisions)
        {
            var (w, i, insideElems, totalElems) = RecoveredErrorSplit(d, frac);
            hs.Add(Size.Z / d);
            whole.Add(w);
            interior.Add(i);
            output.WriteLine(
                $"  div {d}, h {Size.Z / d:F4}: whole {w:E3}, interior {i:E3} "
                + $"({insideElems} of {totalElems} interior elements)");
        }

        double wholeRate = LastPairRate(hs, whole);
        double interiorRate = LastPairRate(hs, interior);
        output.WriteLine(
            $"  whole-domain rate {wholeRate:F3}, interior sub-domain rate {interiorRate:F3} "
            + "(theory 3)");

        // The interior box is genuinely interior, or the study measures nothing.
        Assert.True(interior[^1] > 0, "the interior sub-domain is empty");

        // The finding: the interior rate rises materially above the whole-domain rate and toward
        // theory 3, which is what says the BOUNDARY FILL is the dominant cap. Both halves are
        // asserted so a regression in either direction fails — the interior rate must be
        // measurably better than the whole-domain one (the fill contributes) AND near theory
        // (removing its influence recovers most of the missing order).
        Assert.True(interiorRate > wholeRate + 0.05,
            $"interior rate {interiorRate:F3} is not measurably above whole {wholeRate:F3}; the "
            + "boundary fill would then not be the cap this test records");
        Assert.True(interiorRate > 2.8,
            $"interior rate {interiorRate:F3} did not rise toward theory 3");
    }

    /// <summary>The recovered-stress L2 error over the whole domain and over the central
    /// <c>[frac, 1-frac]</c> box, at one quadratic refinement level.</summary>
    private (double Whole, double Interior, int InteriorElements, int TotalElements)
        RecoveredErrorSplit(int divisions, double frac)
    {
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, Size, 2 * divisions, 2 * divisions, divisions);
        var mesh = AnalysisMesh.Quadratic(tets);
        var model = new StructuralModel(mesh, Steel);
        foreach (int node in model.NodesOn(Facets.All))
            model.PrescribeNode(node, Exact(mesh.Position(node)));
        model.BodyForce(Body);
        var results = StructuralSolver.Solve(model);
        results.Recovery = StressRecovery.Superconvergent;
        var recovered = results.NodalStress.ToArray();

        var rule = TetQuadrature.Degree5;
        int perElement = mesh.NodesPerElement;
        double wholeErr = 0, wholeNorm = 0, innerErr = 0, innerNorm = 0;
        int innerElems = 0;

        var positions = new Vector3d[10];
        var shape = new double[10];
        var grad = new Vector3d[10];
        var exact = new double[6];

        var lo = new Vector3d(frac * Size.X, frac * Size.Y, frac * Size.Z);
        var hi = new Vector3d((1 - frac) * Size.X, (1 - frac) * Size.Y, (1 - frac) * Size.Z);

        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var nodes = mesh.Element(e);
            for (int i = 0; i < perElement; i++)
                positions[i] = mesh.Position(nodes[i]);
            var centroid = (positions[0] + positions[1] + positions[2] + positions[3]) * 0.25;
            bool inside =
                centroid.X >= lo.X && centroid.X <= hi.X &&
                centroid.Y >= lo.Y && centroid.Y <= hi.Y &&
                centroid.Z >= lo.Z && centroid.Z <= hi.Z;
            if (inside)
                innerElems++;

            for (int q = 0; q < rule.Count; q++)
            {
                var (r, s, t) = rule.Point(q);
                if (!TetElement.ShapeGradients(
                        mesh.Order, positions.AsSpan(0, perElement), r, s, t, grad, out double detJ))
                    continue;
                double weight = rule.Weight(q) * detJ;
                TetElement.ShapeValues(mesh.Order, r, s, t, shape);
                var at = Vector3d.Zero;
                for (int i = 0; i < perElement; i++)
                    at += positions[i] * shape[i];
                ExactStress(at, exact);

                double d = Distance(recovered, nodes, shape, perElement, exact);
                double n = 0;
                for (int c = 0; c < 6; c++)
                    n += exact[c] * exact[c];

                wholeErr += weight * d;
                wholeNorm += weight * n;
                if (inside)
                {
                    innerErr += weight * d;
                    innerNorm += weight * n;
                }
            }
        }

        return (
            Math.Sqrt(wholeErr / wholeNorm),
            innerNorm > 0 ? Math.Sqrt(innerErr / innerNorm) : double.NaN,
            innerElems, mesh.ElementCount);
    }

    private static double LastPairRate(List<double> h, List<double> e) =>
        Math.Log(e[^2] / e[^1]) / Math.Log(h[^2] / h[^1]);
}
