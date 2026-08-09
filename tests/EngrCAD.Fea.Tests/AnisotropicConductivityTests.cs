using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Verification for directional (tensor) thermal conductivity — the conduction twin of
/// <see cref="OrthotropicMaterialTests"/>, and checked the same way and for the same reason.
///
/// <para><b>The whole family is exact rather than convergent.</b> A uniform temperature
/// gradient prescribed on a bar's whole boundary produces a CONSTANT gradient, hence a LINEAR
/// temperature field, which is in the linear-tetrahedron space — so the solve reproduces the
/// closed form to round-off at any density, leaving the conductivity tensor as the only thing
/// under test.</para>
///
/// <para><b>The oracle rotates the GRADIENT into the material frame, applies the diagonal K
/// there, and rotates the FLUX back</b> — three-by-three matrices with no shared line of code
/// with the production <c>R·K·Rᵀ</c> congruence. Two derivations that share nothing but the
/// physics is what makes agreement evidence. The classical <c>k(θ) = kx·cos²θ + ky·sin²θ</c> is
/// a third reading, and the CROSS-CONDUCTION — the flux is not parallel to the gradient — is the
/// one behaviour no isotropic law can produce and the one a transposed rotation would lose.</para>
/// </summary>
public class AnisotropicConductivityTests(ITestOutputHelper output)
{
    // A directional conductor, nominal. Only the ratios matter to these tests. A laminate
    // conducts well along the fibres (kx) and poorly across them (ky); kz differs again so a
    // 3D rotation has something to move.
    private const double Kx = 40.0;    // mW/(mm·K), along the fibre
    private const double Ky = 5.0;     // across it
    private const double Kz = 12.0;

    // The carrier material states NO conductivity of its own — the region's conductivity comes
    // from the LAW, exactly as an orthotropic elastic region's stiffness comes from ElasticLaw
    // and the carrier supplies only density and name.
    private static readonly Material Carrier = new("carrier", 200_000, 0.3, 8e-9);

    private static Frame3d RotatedAboutZ(double degrees)
    {
        double a = degrees * Math.PI / 180.0;
        return Frame3d.FromOrthonormal(
            Vector3d.Zero,
            new Vector3d(Math.Cos(a), Math.Sin(a), 0),
            new Vector3d(-Math.Sin(a), Math.Cos(a), 0));
    }

    /// <summary>The heat flux <c>q = -K·grad T</c> for an orthotropic material at a frame,
    /// computed by rotating the gradient into the material frame, applying the DIAGONAL K there,
    /// and rotating the flux back — the independent oracle, sharing nothing with
    /// <see cref="ConductivityLaw"/>'s congruence.</summary>
    private static Vector3d ExpectedFlux(
        Frame3d frame, double kx, double ky, double kz, Vector3d gradient)
    {
        // R's columns are the material axes in global; g_material = Rᵀ·g_global is the gradient
        // dotted with each axis.
        double gmx = frame.X.Dot(gradient);
        double gmy = frame.Y.Dot(gradient);
        double gmz = frame.Z.Dot(gradient);
        // q_material = -K_material·g_material, K diagonal.
        double qmx = -kx * gmx, qmy = -ky * gmy, qmz = -kz * gmz;
        // q_global = R·q_material.
        return frame.X * qmx + frame.Y * qmy + frame.Z * qmz;
    }

    // ---- the isotropic path is untouched -----------------------------------------------

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void AnIsotropicLawAssemblesTheIdenticalElement_BitForBit(ElementOrder order)
    {
        // The neutrality rule every optional feature here carries: adding the law type must not
        // move a single bit of an isotropic model. The tensor overload branches on IsIsotropic
        // to the scalar one, so this is a statement about the plumbing rather than the
        // arithmetic — exactly what a later refactor would quietly reroute.
        var material = new Material("metal", 200_000, 0.3, 8e-9, thermalConductivity: 40.0);
        var nodes = SkewNodes(order);
        var rule = TetQuadrature.For(order);
        int n = nodes.Length;

        var viaScalar = new double[n * n];
        var viaLaw = new double[n * n];
        ThermalElement.Conductivity(order, nodes, material.ThermalConductivity, rule, viaScalar);
        ThermalElement.Conductivity(order, nodes, ConductivityLaw.FromMaterial(material), rule, viaLaw);

        for (int i = 0; i < viaScalar.Length; i++)
        {
            Assert.True(
                BitConverter.DoubleToInt64Bits(viaScalar[i])
                    == BitConverter.DoubleToInt64Bits(viaLaw[i]),
                $"entry {i}: {viaScalar[i]:R} against {viaLaw[i]:R}");
        }
    }

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void TheTensorPathAgreesWithTheScalarFormOnAnIsotropicTensor(ElementOrder order)
    {
        // The other half of the split: the two paths must stay the SAME OPERATOR even though
        // they are deliberately different arithmetic. An anisotropic law whose tensor is k·I
        // goes through the full grad_i·K·grad_j contraction, so this compares it against the
        // scalar k·(grad_i·grad_j) form on skew geometry with no symmetry to hide behind.
        const double k = 40.0;
        var isotropicTensor = ConductivityLaw.Anisotropic(
            Frame3d.WorldXY, [k, 0, 0, 0, k, 0, 0, 0, k], "isotropic-as-anisotropic");
        Assert.False(isotropicTensor.IsIsotropic);

        var nodes = SkewNodes(order);
        var rule = TetQuadrature.For(order);
        int n = nodes.Length;
        var expected = new double[n * n];
        var actual = new double[n * n];
        ThermalElement.Conductivity(order, nodes, k, rule, expected);
        ThermalElement.Conductivity(order, nodes, isotropicTensor, rule, actual);

        double scale = 0;
        foreach (double v in expected)
            scale = Math.Max(scale, Math.Abs(v));
        double worst = 0;
        for (int i = 0; i < expected.Length; i++)
            worst = Math.Max(worst, Math.Abs(expected[i] - actual[i]));
        output.WriteLine($"{order}: tensor within {worst:E3} of scalar {scale:E3}");
        Assert.True(worst <= 1e-11 * scale, $"tensor path differs by {worst:E3} of {scale:E3}");
    }

    private static Vector3d[] SkewNodes(ElementOrder order)
    {
        Vector3d[] corners =
        [
            new(0.13, -0.07, 0.21),
            new(1.37, 0.19, -0.11),
            new(0.29, 1.61, 0.07),
            new(-0.17, 0.23, 1.43),
        ];
        if (order == ElementOrder.Linear)
            return corners;
        return
        [
            corners[0], corners[1], corners[2], corners[3],
            0.5 * (corners[0] + corners[1]),
            0.5 * (corners[1] + corners[2]),
            0.5 * (corners[0] + corners[2]),
            0.5 * (corners[0] + corners[3]),
            0.5 * (corners[1] + corners[3]),
            0.5 * (corners[2] + corners[3]),
        ];
    }

    [Fact]
    public void SettingTheIsotropicLawExplicitlyLeavesTheSolveBitIdentical()
    {
        // The pipeline-level neutrality claim: a model that states FromMaterial explicitly must
        // give the SAME temperature field, bit for bit, as one that states no law at all — the
        // path being ConductivityLawOf's default. This pins assembly AND both flux passes at
        // once, where the element test above pins only assembly.
        var metal = new Material("metal", 200_000, 0.3, 8e-9, thermalConductivity: 37.0);
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(2, 1.5, 1), 4, 3, 2);

        double[] Solve(bool stateLaw)
        {
            var mesh = AnalysisMesh.Of(tets);
            var model = new ThermalModel(mesh, metal);
            if (stateLaw)
                model.SetConductivity(0, ConductivityLaw.FromMaterial(metal));
            model.Temperature(StructuredTetMesh.XMin, 100.0)
                 .Temperature(StructuredTetMesh.XMax, 20.0)
                 .Generation(1.5);
            var results = ThermalSolver.Solve(model);
            var t = new double[mesh.NodeCount];
            for (int v = 0; v < mesh.NodeCount; v++)
                t[v] = results.TemperatureAt(v);
            return t;
        }

        var withoutLaw = Solve(false);
        var withLaw = Solve(true);
        for (int v = 0; v < withoutLaw.Length; v++)
        {
            Assert.True(
                BitConverter.DoubleToInt64Bits(withoutLaw[v])
                    == BitConverter.DoubleToInt64Bits(withLaw[v]),
                $"node {v}: {withoutLaw[v]:R} against {withLaw[v]:R}");
        }
    }

    // ---- the whole solve, against the closed form --------------------------------------

    [Theory]
    [InlineData(0.0)]
    [InlineData(30.0)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    public void AnOffAxisConductivityBarReproducesTheClosedFormFluxExactly(double degrees)
    {
        // A prismatic bar with a uniform temperature gradient imposed on its WHOLE boundary,
        // the material's principal axis at `degrees` to the bar's x-axis. The exact field is
        // T = G·x everywhere (a constant gradient), so it is linear and exactly representable —
        // and the flux q = -K·grad T is a constant vector the solve must reproduce to round-off.
        const double length = 2.0, width = 1.5, height = 1.0;
        const double gradient = 2.5;   // K/mm along x

        var frame = RotatedAboutZ(degrees);
        var law = ConductivityLaw.Orthotropic(frame, Kx, Ky, Kz);
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, new Vector3d(length, width, height), 4, 3, 2);
        var mesh = AnalysisMesh.Of(tets);
        var model = new ThermalModel(mesh, Carrier);
        model.SetConductivity(0, law);
        foreach (int node in model.NodesOn(Facets.All))
            model.TemperatureNode(node, gradient * mesh.Position(node).X);

        var results = ThermalSolver.Solve(model);

        var g = new Vector3d(gradient, 0, 0);
        var expectedFlux = ExpectedFlux(frame, Kx, Ky, Kz, g);
        double fluxScale = expectedFlux.Length;

        double worstTemp = 0, worstFlux = 0;
        for (int v = 0; v < mesh.NodeCount; v++)
            worstTemp = Math.Max(worstTemp, Math.Abs(
                results.TemperatureAt(v) - gradient * mesh.Position(v).X));
        for (int e = 0; e < mesh.ElementCount; e++)
            worstFlux = Math.Max(worstFlux, (results.ElementFlux(e) - expectedFlux).Length);

        // The effective axial conductivity, as a third reading of the same number:
        //   q_x = -G·(kx·cos²θ + ky·sin²θ), so -q_x/G is that effective conductivity.
        double measured = -results.ElementFlux(0).X / gradient;
        double a = degrees * Math.PI / 180.0;
        double c2 = Math.Cos(a) * Math.Cos(a), s2 = Math.Sin(a) * Math.Sin(a);
        double classical = Kx * c2 + Ky * s2;
        double solvedCross = results.ElementFlux(0).Y;   // the flux component across the gradient

        output.WriteLine(
            $"{degrees:F0} deg: k_axial measured {measured:F3}, classical {classical:F3}, "
            + $"worst temp error {worstTemp:E3}, worst flux error {worstFlux:E3} of {fluxScale:E3}, "
            + $"cross flux q_y = {solvedCross:E3}");

        Assert.Equal(classical, measured, classical * 1e-11);
        Assert.True(worstTemp <= 1e-11 * (gradient * length), $"temperature error {worstTemp:E3}");
        Assert.True(worstFlux <= 1e-9 * fluxScale, $"flux error {worstFlux:E3} of {fluxScale:E3}");

        // The assertion with teeth. Off axis the heat flux is NOT parallel to the imposed
        // (x-only) temperature gradient — a solid steel bar cannot do this, an orthotropic
        // laminate can, and a transposed rotation would lose it while leaving the axial
        // conductivity intact.
        if (degrees is > 0.5 and < 89.5)
        {
            Assert.True(Math.Abs(solvedCross) > 1e-3 * fluxScale,
                "an off-axis conductor must carry heat across the gradient (cross-conduction)");
        }
        else
        {
            Assert.True(Math.Abs(solvedCross) <= 1e-10 * fluxScale,
                "an on-axis conductor must carry no heat across the gradient");
        }
    }

    [Fact]
    public void RotatingTheFrameByAQuarterTurnSwapsTheTwoConductivities()
    {
        // The cheapest check that the frame is applied in the direction the documentation
        // claims, and the one whose failure mode — a transposed congruence — is invisible at 0
        // and 90 degrees to any symmetric measure. K_xx of the rotated global tensor is the
        // axial conductivity, and it swaps kx <-> ky across a quarter turn.
        double at0 = ConductivityLaw.Orthotropic(RotatedAboutZ(0), Kx, Ky, Kz).ConductivityMatrix[0];
        double at90 = ConductivityLaw.Orthotropic(RotatedAboutZ(90), Kx, Ky, Kz).ConductivityMatrix[0];
        output.WriteLine($"K_xx at 0 deg = {at0:F3}, at 90 deg = {at90:F3}");
        Assert.Equal(Kx, at0, Kx * 1e-12);
        Assert.Equal(Ky, at90, Ky * 1e-12);
    }

    [Fact]
    public void TheAnisotropicFactoryReproducesTheOrthotropicOne()
    {
        // Rotating an orthotropic law by a frame and stating the SAME rotated tensor to the
        // general factory in world coordinates must give one law — a cross-check on the
        // congruence from the other side.
        var frame = RotatedAboutZ(37.0);
        var rotated = ConductivityLaw.Orthotropic(frame, Kx, Ky, Kz);
        var restated = ConductivityLaw.Anisotropic(Frame3d.WorldXY, rotated.ConductivityMatrix, "restated");

        double scale = 0;
        foreach (double v in rotated.ConductivityMatrix)
            scale = Math.Max(scale, Math.Abs(v));
        double worst = 0;
        for (int i = 0; i < 9; i++)
            worst = Math.Max(worst, Math.Abs(rotated.ConductivityMatrix[i] - restated.ConductivityMatrix[i]));
        Assert.True(worst <= 1e-12 * scale, $"differs by {worst:E3}");

        // A rotated law is genuinely off-diagonal: K_xy couples an x-gradient to a y-flux.
        Assert.True(Math.Abs(rotated.ConductivityMatrix[1]) > 1e-6 * scale,
            "a rotated orthotropic law must have off-diagonal conductivity");
    }

    // ---- manufactured-order confirmation with a rotated constant tensor ----------------

    private static readonly Vector3d Size = new(2.0, 1.5, 1.0);
    private const double Amplitude = 5.0;

    // The same quartic field ThermalConvergenceTests uses, and for the same reasons: cross
    // terms so no direction is independent, and a non-zero fourth derivative so the nodal
    // values are not accidentally exact.
    private static double Exact(Vector3d p) => Amplitude * (
        p.X * p.X * p.X + p.Y * p.Y * p.Y + p.Z * p.Z * p.Z
        + p.X * p.X * p.Y + p.Y * p.Y * p.Z + p.Z * p.Z * p.X
        + p.X * p.X * p.Y * p.Y);

    private static Vector3d ExactGradient(Vector3d p) => new(
        Amplitude * (3 * p.X * p.X + 2 * p.X * p.Y + p.Z * p.Z + 2 * p.X * p.Y * p.Y),
        Amplitude * (3 * p.Y * p.Y + p.X * p.X + 2 * p.Y * p.Z + 2 * p.X * p.X * p.Y),
        Amplitude * (3 * p.Z * p.Z + p.Y * p.Y + 2 * p.Z * p.X));

    // The Hessian of the quartic field, as (xx, yy, zz, xy, xz, yz).
    private static (double Xx, double Yy, double Zz, double Xy, double Xz, double Yz) Hessian(Vector3d p) => (
        Amplitude * (6 * p.X + 2 * p.Y + 2 * p.Y * p.Y),
        Amplitude * (6 * p.Y + 2 * p.Z + 2 * p.X * p.X),
        Amplitude * (6 * p.Z + 2 * p.X),
        Amplitude * (2 * p.X + 4 * p.X * p.Y),
        Amplitude * 2 * p.Z,
        Amplitude * 2 * p.Y);

    /// <summary>The generation that makes <see cref="Exact"/> exact for a CONSTANT tensor K:
    /// <c>q = -div(K·grad T) = -K:Hessian(T)</c>.</summary>
    private static double Generation(ReadOnlySpan<double> k, Vector3d p)
    {
        var h = Hessian(p);
        // K:H = k00·Txx + k11·Tyy + k22·Tzz + 2(k01·Txy + k02·Txz + k12·Tyz).
        double contract = k[0] * h.Xx + k[4] * h.Yy + k[8] * h.Zz
            + 2.0 * (k[1] * h.Xy + k[2] * h.Xz + k[5] * h.Yz);
        return -contract;
    }

    private static double QuadForm(ReadOnlySpan<double> k, Vector3d v)
    {
        double kx = k[0] * v.X + k[1] * v.Y + k[2] * v.Z;
        double ky = k[3] * v.X + k[4] * v.Y + k[5] * v.Z;
        double kz = k[6] * v.X + k[7] * v.Y + k[8] * v.Z;
        return v.X * kx + v.Y * ky + v.Z * kz;
    }

    private readonly record struct Run(double H, int Elements, double L2, double Energy);

    private static Run Solve(ConductivityLaw law, ElementOrder order, int divisions)
    {
        var globalK = law.ConductivityMatrix.ToArray();
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, Size, 2 * divisions, 2 * divisions, divisions);
        var mesh = order == ElementOrder.Linear
            ? AnalysisMesh.Of(tets)
            : AnalysisMesh.Quadratic(tets);

        var model = new ThermalModel(mesh, Carrier);
        model.SetConductivity(0, law);
        foreach (int node in model.NodesOn(Facets.All))
            model.TemperatureNode(node, Exact(mesh.Position(node)));
        model.Generation(p => Generation(globalK, p));

        var results = ThermalSolver.Solve(model);
        Assert.True(results.Report.RelativeResidual < 1e-8,
            $"solve residual {results.Report.RelativeResidual:E3}");

        double errorL2 = 0, exactL2 = 0, errorEnergy = 0, exactEnergy = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            double volume = mesh.ElementVolume(e);
            var nodes = mesh.Element(e);
            var centroid = Vector3d.Zero;
            for (int i = 0; i < 4; i++)
                centroid += mesh.Position(nodes[i]);
            centroid *= 0.25;

            double interpolated = results.TemperatureIn(e, 0.25, 0.25, 0.25);
            double exact = Exact(centroid);
            errorL2 += volume * (interpolated - exact) * (interpolated - exact);
            exactL2 += volume * exact * exact;

            // The energy norm of a directional conduction problem is grad·K·grad — the exact
            // analogue of the isotropic k·|grad|², with the tensor doing the weighting.
            var gradientError = results.ElementGradient(e) - ExactGradient(centroid);
            errorEnergy += volume * QuadForm(globalK, gradientError);
            exactEnergy += volume * QuadForm(globalK, ExactGradient(centroid));
        }

        return new Run(
            Size.Z / divisions, mesh.ElementCount,
            Math.Sqrt(errorL2 / exactL2), Math.Sqrt(errorEnergy / exactEnergy));
    }

    [Fact]
    public void ManufacturedSolution_WithARotatedTensor_ConvergesAtTheTheoreticalOrder()
    {
        // A general 3D rotation, so every off-diagonal of the global tensor is non-zero and the
        // K:Hessian contraction and the grad·K·grad energy norm both exercise them. A CONSTANT
        // directional conductivity changes the FIELD, not the element order — so the measured
        // orders must be the same theory (2/1 linear, 3/2 quadratic) the isotropic study reports.
        var frame = Frame3d.FromXY(
            Vector3d.Zero, new Vector3d(2, 1, 0.5), new Vector3d(-1, 1, 1));
        var law = ConductivityLaw.Orthotropic(frame, Kx, Ky, Kz);
        var k = law.ConductivityMatrix;
        Assert.True(Math.Abs(k[1]) > 1e-6 && Math.Abs(k[2]) > 1e-6 && Math.Abs(k[5]) > 1e-6,
            "the rotated tensor must be fully off-diagonal, or the test does not exercise it");

        var linear = new[] { 2, 4, 8 }.Select(d => Solve(law, ElementOrder.Linear, d)).ToArray();
        var quadratic = new[] { 1, 2, 4 }.Select(d => Solve(law, ElementOrder.Quadratic, d)).ToArray();

        Report("linear", linear);
        Report("quadratic", quadratic);

        double linearL2 = Order(linear, r => r.L2);
        double linearEnergy = Order(linear, r => r.Energy);
        double quadraticL2 = Order(quadratic, r => r.L2);
        double quadraticEnergy = Order(quadratic, r => r.Energy);

        output.WriteLine(
            $"rotated-tensor orders: linear L2 {linearL2:F2} (theory 2), energy {linearEnergy:F2} "
            + $"(theory 1); quadratic L2 {quadraticL2:F2} (theory 3), energy {quadraticEnergy:F2} "
            + "(theory 2)");

        Assert.InRange(linearL2, 1.7, 2.4);
        Assert.InRange(linearEnergy, 0.8, 1.4);
        Assert.InRange(quadraticL2, 2.6, 3.6);
        Assert.InRange(quadraticEnergy, 1.7, 2.4);
        Assert.True(quadraticL2 > linearL2 + 0.5,
            $"quadratic L2 order {quadraticL2:F2} does not beat linear {linearL2:F2}");
    }

    private static double Order(Run[] runs, Func<Run, double> measure)
    {
        double coarse = measure(runs[^2]), fine = measure(runs[^1]);
        return Math.Log(coarse / fine) / Math.Log(runs[^2].H / runs[^1].H);
    }

    private void Report(string label, Run[] runs)
    {
        output.WriteLine($"{label}:");
        for (int i = 0; i < runs.Length; i++)
        {
            string l2 = i == 0 ? "-" : (Math.Log(runs[i - 1].L2 / runs[i].L2)
                / Math.Log(runs[i - 1].H / runs[i].H)).ToString("F2");
            string en = i == 0 ? "-" : (Math.Log(runs[i - 1].Energy / runs[i].Energy)
                / Math.Log(runs[i - 1].H / runs[i].H)).ToString("F2");
            output.WriteLine(
                $"  h={runs[i].H:F4} elems={runs[i].Elements,7:N0} "
                + $"L2={runs[i].L2:E3} ({l2}) energy={runs[i].Energy:E3} ({en})");
        }
    }

    // ---- refusals ----------------------------------------------------------------------

    [Fact]
    public void ANonPositiveDefiniteTensorIsRefusedByItsMinor()
    {
        // Symmetric but indefinite: the leading 2x2 minor is 1·1 - 2·2 = -3, so minor 2 fails.
        var ex = Assert.Throws<FeaException>(() => ConductivityLaw.Anisotropic(
            Frame3d.WorldXY, [1, 2, 0, 2, 1, 0, 0, 0, 1], "indefinite"));
        output.WriteLine(ex.Message);
        Assert.Contains("positive definite", ex.Message);
        Assert.Contains("minor 2", ex.Message);
    }

    [Fact]
    public void AnAsymmetricTensorIsRefusedAsATranscriptionError()
    {
        var ex = Assert.Throws<FeaException>(() => ConductivityLaw.Anisotropic(
            Frame3d.WorldXY, [10, 3, 0, 1, 5, 0, 0, 0, 4], "broken"));
        output.WriteLine(ex.Message);
        Assert.Contains("not symmetric", ex.Message);
        Assert.Contains("K[0,1]", ex.Message);
    }

    [Fact]
    public void AZeroOrthotropicConductivityIsRefusedBeforeTheFactorization()
    {
        var ex = Assert.Throws<FeaException>(
            () => ConductivityLaw.Orthotropic(Frame3d.WorldXY, Kx, 0.0, Kz));
        Assert.Contains("ky", ex.Message);
        Assert.Contains("positive", ex.Message);
    }

    [Fact]
    public void AWronglySizedTensorIsRefused()
    {
        var ex = Assert.Throws<FeaException>(
            () => ConductivityLaw.Anisotropic(Frame3d.WorldXY, new double[6]));
        Assert.Contains("9", ex.Message);
    }

    [Fact]
    public void AZeroConductivityMaterialWithNoLawIsRefusedAtTheSolve()
    {
        // FromMaterial does not refuse a zero conductivity (it is a legal document material);
        // the refusal is at the point of use, where it can name the property. A region with no
        // set law and a zero-conductivity material must still be caught.
        var material = new Material("no-k", 200_000, 0.3, 8e-9);   // ThermalConductivity = 0
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(2, 1.5, 1), 2, 2, 1);
        var mesh = AnalysisMesh.Of(tets);
        var model = new ThermalModel(mesh, material);
        model.Temperature(StructuredTetMesh.XMin, 100.0)
             .Temperature(StructuredTetMesh.XMax, 20.0);
        var ex = Assert.Throws<FeaException>(() => ThermalSolver.Solve(model));
        Assert.Contains("thermal conductivity", ex.Message);
    }

    [Fact]
    public void ADirectionalLawSurvivesAZeroConductivityMaterial()
    {
        // The complement: the carrier states no conductivity, but the region's directional law
        // is positive definite by construction, so the solve is fine. The refusal above must
        // not fire here.
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(2, 1.5, 1), 3, 2, 2);
        var mesh = AnalysisMesh.Of(tets);
        var model = new ThermalModel(mesh, Carrier);   // Carrier.ThermalConductivity == 0
        model.SetConductivity(0, ConductivityLaw.Orthotropic(RotatedAboutZ(30), Kx, Ky, Kz));
        model.Temperature(StructuredTetMesh.XMin, 100.0)
             .Temperature(StructuredTetMesh.XMax, 20.0);
        var results = ThermalSolver.Solve(model);
        Assert.True(results.Report.RelativeResidual < 1e-8);
        Assert.True(results.Report.EnergyBalanceResidual < 1e-10);
    }
}
