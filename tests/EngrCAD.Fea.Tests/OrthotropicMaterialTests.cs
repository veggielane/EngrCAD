using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Verification for orthotropic and anisotropic elasticity.
///
/// <para><b>The whole family is checked EXACTLY rather than by convergence</b>, and the
/// reason is the one the multi-material bar already records: a uniform stress state in a
/// prismatic bar produces a CONSTANT strain, hence a LINEAR displacement field, which is in
/// the linear-tetrahedron space — so the solve reproduces the closed form to round-off at
/// any mesh density rather than converging onto it. That leaves the constitutive law as the
/// only thing under test, which is the point.</para>
///
/// <para><b>The oracle is built by 3x3 TENSOR rotation, not by the production code's Voigt
/// machinery.</b> The expected strain is computed by rotating the stress tensor into the
/// material frame, applying the orthotropic compliance there (straight from the engineering
/// constants), and rotating the strain tensor back — three-by-three matrices throughout,
/// with no Voigt vector, no engineering-shear convention and no Bond transformation
/// anywhere. The production path rotates the STIFFNESS by the Voigt STRESS transformation.
/// Two derivations that share nothing but the physics is what makes agreement evidence; a
/// test that re-ran the same rotation would agree with a broken one just as happily. The
/// classical off-axis modulus formula is then a THIRD reading of the same number.</para>
/// </summary>
public class OrthotropicMaterialTests(ITestOutputHelper output)
{
    // A unidirectional carbon/epoxy lamina, nominal and flagged verify-against-datasheet
    // like every other transcribed table here. Only the RATIOS matter to these tests.
    private const double E1 = 135_000.0;   // MPa, along the fibre
    private const double E2 = 9_000.0;     // MPa, across it
    private const double E3 = 9_000.0;
    private const double Nu12 = 0.30;
    private const double Nu13 = 0.30;
    private const double Nu23 = 0.45;
    private const double G12 = 4_800.0;    // MPa
    private const double G13 = 4_800.0;
    private const double G23 = E2 / (2.0 * (1.0 + Nu23));

    private static readonly Material Carrier =
        new("lamina carrier", E1, 0.3, 1.6e-9);

    private static ElasticLaw Lamina(Frame3d frame) =>
        ElasticLaw.Orthotropic(frame, E1, E2, E3, Nu12, Nu23, Nu13, G12, G23, G13, "lamina");

    private static Frame3d RotatedAboutZ(double degrees)
    {
        double a = degrees * Math.PI / 180.0;
        return Frame3d.FromOrthonormal(
            Vector3d.Zero,
            new Vector3d(Math.Cos(a), Math.Sin(a), 0),
            new Vector3d(-Math.Sin(a), Math.Cos(a), 0));
    }

    // ---- the independent oracle -------------------------------------------------------

    /// <summary>
    /// The strain a stated stress produces, computed with 3x3 tensor rotations and the
    /// material-frame compliance only — see the class remarks for why this shares nothing
    /// with the production path. Returns TENSOR shear components.
    /// </summary>
    private static SymmetricTensor3 ExpectedStrain(Frame3d frame, SymmetricTensor3 globalStress)
    {
        // R maps material components to global; R' maps global to material.
        var x = frame.X;
        var y = frame.Y;
        var z = frame.Z;
        Span<double> r = [x.X, y.X, z.X, x.Y, y.Y, z.Y, x.Z, y.Z, z.Z];

        Span<double> sg = [
            globalStress.Xx, globalStress.Xy, globalStress.Xz,
            globalStress.Xy, globalStress.Yy, globalStress.Yz,
            globalStress.Xz, globalStress.Yz, globalStress.Zz];

        // sigma_material = R' sigma_global R.
        Span<double> tmp = stackalloc double[9];
        Span<double> sm = stackalloc double[9];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                double sum = 0;
                for (int k = 0; k < 3; k++)
                    sum += r[k * 3 + i] * sg[k * 3 + j];
                tmp[i * 3 + j] = sum;
            }
        }
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                double sum = 0;
                for (int k = 0; k < 3; k++)
                    sum += tmp[i * 3 + k] * r[k * 3 + j];
                sm[i * 3 + j] = sum;
            }
        }

        // The orthotropic compliance, written straight from the engineering constants. The
        // minor ratios come from the symmetry nu_ji/E_j = nu_ij/E_i, which is the same
        // relation the factory uses and the only one there is.
        double s11 = sm[0], s22 = sm[4], s33 = sm[8];
        double e11 = s11 / E1 - Nu12 * s22 / E1 - Nu13 * s33 / E1;
        double e22 = -Nu12 * s11 / E1 + s22 / E2 - Nu23 * s33 / E2;
        double e33 = -Nu13 * s11 / E1 - Nu23 * s22 / E2 + s33 / E3;
        // Tensor shear: the engineering shear is tau/G, so the tensor component is half it.
        double e12 = sm[1] / (2.0 * G12);
        double e23 = sm[5] / (2.0 * G23);
        double e13 = sm[2] / (2.0 * G13);

        Span<double> em = [e11, e12, e13, e12, e22, e23, e13, e23, e33];

        // eps_global = R eps_material R'.
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                double sum = 0;
                for (int k = 0; k < 3; k++)
                    sum += r[i * 3 + k] * em[k * 3 + j];
                tmp[i * 3 + j] = sum;
            }
        }
        Span<double> eg = stackalloc double[9];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                double sum = 0;
                for (int k = 0; k < 3; k++)
                    sum += tmp[i * 3 + k] * r[j * 3 + k];
                eg[i * 3 + j] = sum;
            }
        }
        return new SymmetricTensor3(eg[0], eg[4], eg[8], eg[1], eg[2], eg[5]);
    }

    // ---- the isotropic law is unchanged ------------------------------------------------

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void AnIsotropicLawAssemblesTheIdenticalElement_BitForBit(ElementOrder order)
    {
        // The neutrality rule every optional feature here carries: adding the law type must
        // not move a single bit of an isotropic model. The branch is on IsIsotropic, so this
        // is a statement about the plumbing rather than about the arithmetic - which is
        // exactly what makes it worth pinning, since the plumbing is what a later refactor
        // would quietly reroute.
        var material = new Material("steel", 210_000, 0.3);
        var nodes = SkewNodes(order);
        var rule = TetQuadrature.For(order);
        int dofs = 3 * nodes.Length;

        var viaMaterial = new double[dofs * dofs];
        var viaLaw = new double[dofs * dofs];
        TetElement.Stiffness(order, nodes, material, rule, viaMaterial);
        TetElement.Stiffness(order, nodes, ElasticLaw.FromMaterial(material), rule, viaLaw);

        for (int i = 0; i < viaMaterial.Length; i++)
        {
            Assert.True(
                BitConverter.DoubleToInt64Bits(viaMaterial[i])
                    == BitConverter.DoubleToInt64Bits(viaLaw[i]),
                $"entry {i}: {viaMaterial[i]:R} against {viaLaw[i]:R}");
        }
    }

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void TheGeneralPathAgreesWithTheIndexFormOnAnIsotropicLaw(ElementOrder order)
    {
        // The other half of the split: the two paths must stay the SAME OPERATOR even
        // though they are deliberately different arithmetic. An orthotropic law whose nine
        // constants happen to be isotropic goes through B'DB, so this compares the general
        // element against the index form on geometry with no symmetry to hide behind.
        const double e = 210_000.0, nu = 0.3;
        double g = e / (2.0 * (1.0 + nu));
        var material = new Material("steel", e, nu);
        var isotropicLaw = ElasticLaw.Orthotropic(
            Frame3d.WorldXY, e, e, e, nu, nu, nu, g, g, g, "isotropic-as-orthotropic");
        Assert.False(isotropicLaw.IsIsotropic);

        // The constitutive matrices must agree first: if they do not, comparing stiffnesses
        // says nothing about the element.
        var reference = material.ConstitutiveMatrix();
        var built = isotropicLaw.StiffnessMatrix;
        double dScale = 0;
        foreach (double v in reference)
            dScale = Math.Max(dScale, Math.Abs(v));
        double worstD = 0;
        for (int i = 0; i < 36; i++)
            worstD = Math.Max(worstD, Math.Abs(reference[i] - built[i]));
        Assert.True(worstD <= 1e-12 * dScale, $"constitutive matrix differs by {worstD:E3}");

        var nodes = SkewNodes(order);
        var rule = TetQuadrature.For(order);
        int dofs = 3 * nodes.Length;
        var expected = new double[dofs * dofs];
        var actual = new double[dofs * dofs];
        TetElement.Stiffness(order, nodes, material, rule, expected);
        TetElement.Stiffness(order, nodes, isotropicLaw, rule, actual);

        double scale = 0;
        foreach (double v in expected)
            scale = Math.Max(scale, Math.Abs(v));
        double worst = 0;
        for (int i = 0; i < expected.Length; i++)
            worst = Math.Max(worst, Math.Abs(expected[i] - actual[i]));
        output.WriteLine(
            $"{order}: D within {worstD:E3} of {dScale:E3}, "
            + $"stiffness within {worst:E3} of {scale:E3}");
        Assert.True(worst <= 1e-11 * scale, $"stiffness differs by {worst:E3} of {scale:E3}");
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

    // ---- the whole solve, against the closed form --------------------------------------

    [Theory]
    [InlineData(0.0)]
    [InlineData(30.0)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    public void AnOffAxisLaminaBarReproducesTheClosedFormStrainExactly(double degrees)
    {
        // A prismatic bar pulled by a uniform traction, restrained statically, with the
        // lamina's fibre direction at `degrees` to the bar's axis. The exact solution is a
        // uniform uniaxial STRESS state whatever the anisotropy - equilibrium and the
        // traction-free sides are satisfied by it - so the strain is constant and the
        // displacement field linear, i.e. exactly representable.
        const double length = 2.0, width = 1.5, height = 1.0;
        const double sigma = 25.0;

        var frame = RotatedAboutZ(degrees);
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, new Vector3d(length, width, height), 4, 3, 2);
        var mesh = AnalysisMesh.Of(tets);
        var model = new StructuralModel(mesh, Carrier);
        model.SetElasticity(0, Lamina(frame));

        model.Fix(Facets.OnPlane(Vector3d.Zero, Vector3d.UnitX), Dof.X);
        int origin = FeaPatchTests.FindNode(mesh, Vector3d.Zero);
        int alongY = FeaPatchTests.FindNode(mesh, new Vector3d(0, width, 0));
        model.FixNode(origin, Dof.Y | Dof.Z);
        model.FixNode(alongY, Dof.Z);
        model.Traction(
            Facets.OnPlane(new Vector3d(length, 0, 0), Vector3d.UnitX),
            new Vector3d(sigma, 0, 0));

        var results = StructuralSolver.Solve(model);

        var expected = ExpectedStrain(frame, new SymmetricTensor3(sigma, 0, 0, 0, 0, 0));
        double strainScale = Math.Abs(expected.Xx);

        double worstStrain = 0, worstStress = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var strain = results.ElementStrain(e);
            worstStrain = Math.Max(worstStrain, Math.Abs(strain.Xx - expected.Xx));
            worstStrain = Math.Max(worstStrain, Math.Abs(strain.Yy - expected.Yy));
            worstStrain = Math.Max(worstStrain, Math.Abs(strain.Zz - expected.Zz));
            worstStrain = Math.Max(worstStrain, Math.Abs(strain.Xy - expected.Xy));
            worstStrain = Math.Max(worstStrain, Math.Abs(strain.Yz - expected.Yz));
            worstStrain = Math.Max(worstStrain, Math.Abs(strain.Xz - expected.Xz));

            var stress = results.ElementStress(e);
            worstStress = Math.Max(worstStress, Math.Abs(stress.Xx - sigma));
            worstStress = Math.Max(worstStress, Math.Abs(stress.Yy));
            worstStress = Math.Max(worstStress, Math.Abs(stress.Zz));
            worstStress = Math.Max(worstStress, Math.Abs(stress.Xy));
            worstStress = Math.Max(worstStress, Math.Abs(stress.Yz));
            worstStress = Math.Max(worstStress, Math.Abs(stress.Xz));
        }

        // The classical off-axis modulus, as a third reading of the same number:
        //   1/Ex = c^4/E1 + s^4/E2 + (1/G12 - 2 nu12/E1) s^2 c^2.
        double a = degrees * Math.PI / 180.0;
        double c2 = Math.Cos(a) * Math.Cos(a), s2 = Math.Sin(a) * Math.Sin(a);
        double classical = 1.0 /
            (c2 * c2 / E1 + s2 * s2 / E2 + (1.0 / G12 - 2.0 * Nu12 / E1) * s2 * c2);
        double measured = sigma / expected.Xx;

        output.WriteLine(
            $"{degrees:F0} deg: Ex measured {measured:F2} MPa, classical {classical:F2}, "
            + $"worst strain error {worstStrain:E3} of {strainScale:E3}, "
            + $"worst stress error {worstStress:E3} of {sigma}, "
            + $"shear coupling eps_xy = {expected.Xy:E3}");

        Assert.Equal(classical, measured, classical * 1e-12);
        Assert.True(worstStrain <= 1e-10 * strainScale, $"strain error {worstStrain:E3}");
        Assert.True(worstStress <= 1e-9 * sigma, $"stress error {worstStress:E3}");

        // The assertion with teeth. On axis the two lateral contractions differ because
        // nu12 and nu13 differ THROUGH E2 and E3; off axis the bar additionally SHEARS,
        // which is the single behaviour no isotropic law can produce and the one a wrong
        // rotation would lose.
        if (degrees is > 0.5 and < 89.5)
        {
            Assert.True(Math.Abs(expected.Xy) > 1e-3 * strainScale,
                "an off-axis lamina must show shear-extension coupling");
        }
        else
        {
            Assert.True(Math.Abs(expected.Xy) <= 1e-14 * strainScale,
                "an on-axis lamina must show none");
        }
    }

    [Fact]
    public void RotatingTheMaterialFrameByAQuarterTurnSwapsTheTwoModuli()
    {
        // The cheapest possible check that the frame is applied in the direction the
        // documentation claims, and the one whose failure mode - a transposed rotation - is
        // invisible at 0 and 90 degrees to any symmetric measure.
        double axial = ApparentAxialModulus(RotatedAboutZ(0));
        double transverse = ApparentAxialModulus(RotatedAboutZ(90));
        output.WriteLine($"Ex at 0 deg = {axial:F1}, at 90 deg = {transverse:F1}");
        Assert.Equal(E1, axial, E1 * 1e-10);
        Assert.Equal(E2, transverse, E2 * 1e-10);
    }

    private static double ApparentAxialModulus(Frame3d frame)
    {
        var strain = ExpectedStrain(frame, new SymmetricTensor3(1.0, 0, 0, 0, 0, 0));
        return 1.0 / strain.Xx;
    }

    [Fact]
    public void TheAnisotropicFactoryReproducesTheOrthotropicOne()
    {
        // Rotating an orthotropic law by a frame and stating the SAME rotated matrix to the
        // general factory in world coordinates must give one law. That is a cross-check on
        // the Voigt rotation from the other side: if the rotation were wrong, the two would
        // still agree - but the off-axis modulus test above pins the rotation, so together
        // they say the general factory is the same law by another route.
        var frame = RotatedAboutZ(37.0);
        var rotated = Lamina(frame);
        var restated = ElasticLaw.Anisotropic(
            Frame3d.WorldXY, rotated.StiffnessMatrix, "restated");

        double scale = 0;
        foreach (double v in rotated.StiffnessMatrix)
            scale = Math.Max(scale, Math.Abs(v));
        double worst = 0;
        for (int i = 0; i < 36; i++)
            worst = Math.Max(worst, Math.Abs(rotated.StiffnessMatrix[i] - restated.StiffnessMatrix[i]));
        Assert.True(worst <= 1e-12 * scale, $"differs by {worst:E3}");

        // And a rotated law is genuinely full: an orthotropic matrix has nine non-zeros
        // outside the shear diagonal, a rotated one has shear-normal coupling.
        Assert.True(Math.Abs(rotated.StiffnessMatrix[3]) > 1e-6 * scale,
            "a rotated law must couple normal stress to shear strain");
    }

    // ---- directional thermal expansion --------------------------------------------------

    [Fact]
    public void AFreeOrthotropicBarExpandsByItsOwnThreeCoefficients()
    {
        // The exact case: a statically determinate bar, heated uniformly, carries NO stress
        // and strains by exactly alpha_i dT along each material axis. It is the orthotropic
        // twin of the free-expansion check the isotropic coupling already passes, and it is
        // exact for the same reason - a constant strain field.
        const double length = 2.0, width = 1.5, height = 1.0;
        const double rise = 60.0;
        double[] alpha = [-0.5e-6, 28.0e-6, 28.0e-6];

        var frame = Frame3d.WorldXY;
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, new Vector3d(length, width, height), 3, 2, 2);
        var mesh = AnalysisMesh.Of(tets);
        var model = new StructuralModel(mesh, Carrier);
        model.SetElasticity(
            0, Lamina(frame).WithThermalExpansion(frame, alpha[0], alpha[1], alpha[2]));

        int origin = FeaPatchTests.FindNode(mesh, Vector3d.Zero);
        int alongX = FeaPatchTests.FindNode(mesh, new Vector3d(length, 0, 0));
        int alongY = FeaPatchTests.FindNode(mesh, new Vector3d(0, width, 0));
        model.FixNode(origin, Dof.All);
        model.FixNode(alongX, Dof.Y | Dof.Z);
        model.FixNode(alongY, Dof.Z);

        var temperature = new double[mesh.NodeCount];
        Array.Fill(temperature, rise);
        model.ThermalLoad(temperature, 0.0);

        var results = StructuralSolver.Solve(model);

        double worstStrain = 0, worstStress = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var strain = results.ElementStrain(e);
            worstStrain = Math.Max(worstStrain, Math.Abs(strain.Xx - alpha[0] * rise));
            worstStrain = Math.Max(worstStrain, Math.Abs(strain.Yy - alpha[1] * rise));
            worstStrain = Math.Max(worstStrain, Math.Abs(strain.Zz - alpha[2] * rise));
            worstStrain = Math.Max(worstStrain, Math.Abs(strain.Xy));
            var stress = results.ElementStress(e);
            worstStress = Math.Max(worstStress, Math.Abs(stress.Xx));
            worstStress = Math.Max(worstStress, Math.Abs(stress.Yy));
            worstStress = Math.Max(worstStress, Math.Abs(stress.Zz));
        }

        double strainScale = alpha[1] * rise;
        // What a fully restrained bar WOULD carry, which is the scale a "zero stress" claim
        // has to be relative to.
        double stressScale = E2 * alpha[1] * rise;
        output.WriteLine(
            $"free expansion: worst strain error {worstStrain:E3} of {strainScale:E3}, "
            + $"worst stress {worstStress:E3} of a restrained {stressScale:F2} MPa");

        Assert.True(worstStrain <= 1e-10 * strainScale, $"strain error {worstStrain:E3}");
        Assert.True(worstStress <= 1e-10 * stressScale, $"residual stress {worstStress:E3}");
    }

    [Fact]
    public void AFullyRestrainedOrthotropicBarCarriesTheDirectionalThermalStress()
    {
        // The other end of the same statement, and the one that reads the LOAD rather than
        // its cancellation: hold every face and heat the bar, and the stress is D·alpha·dT
        // exactly - three DIFFERENT numbers, where an isotropic law would give one.
        const double rise = 60.0;
        double[] alpha = [-0.5e-6, 28.0e-6, 28.0e-6];

        var frame = Frame3d.WorldXY;
        var law = Lamina(frame).WithThermalExpansion(frame, alpha[0], alpha[1], alpha[2]);
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(2.0, 1.5, 1.0), 3, 2, 2);
        var mesh = AnalysisMesh.Of(tets);
        var model = new StructuralModel(mesh, Carrier);
        model.SetElasticity(0, law);
        model.Fix(Facets.All, Dof.All);

        var temperature = new double[mesh.NodeCount];
        Array.Fill(temperature, rise);
        model.ThermalLoad(temperature, 0.0);

        var results = StructuralSolver.Solve(model);

        // sigma = -D alpha dT, taken from the law's own product so the load and the
        // expectation cannot be built from different matrices - which is exactly why the
        // law forms D·alpha once and hands it out.
        var expected = law.ThermalStressPerDegree;
        double worst = 0;
        double scale = 0;
        for (int i = 0; i < 3; i++)
            scale = Math.Max(scale, Math.Abs(expected[i] * rise));
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var stress = results.ElementStress(e);
            worst = Math.Max(worst, Math.Abs(stress.Xx + expected[0] * rise));
            worst = Math.Max(worst, Math.Abs(stress.Yy + expected[1] * rise));
            worst = Math.Max(worst, Math.Abs(stress.Zz + expected[2] * rise));
        }

        output.WriteLine(
            $"restrained: sigma = ({-expected[0] * rise:F3}, {-expected[1] * rise:F3}, "
            + $"{-expected[2] * rise:F3}) MPa, worst error {worst:E3}");

        // The teeth: the three are genuinely different, so a law that quietly used one
        // scalar coefficient would fail here even with the right magnitude.
        Assert.True(Math.Abs(expected[0] - expected[1]) > 0.1 * scale / rise,
            "the fixture must produce directionally different thermal stress");
        Assert.True(worst <= 1e-10 * scale, $"thermal stress error {worst:E3} of {scale:E3}");
    }

    [Fact]
    public void TheGeneralThermalLoadAgreesWithTheScalarOneOnAnIsotropicLaw()
    {
        // The other half of the bit-identity split, stated as an equivalence: an isotropic
        // law routed deliberately through the anisotropic path (by spelling it as an
        // orthotropic law with equal constants) must produce the same solve.
        const double e = 210_000.0, nu = 0.3, alpha = 12e-6, rise = 40.0;
        double g = e / (2.0 * (1.0 + nu));
        var material = new Material("steel", e, nu, 7.85e-9, 0, 0, alpha);
        var asOrthotropic = ElasticLaw
            .Orthotropic(Frame3d.WorldXY, e, e, e, nu, nu, nu, g, g, g, "as-orthotropic")
            .WithThermalExpansion(Frame3d.WorldXY, alpha, alpha, alpha);

        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(2.0, 1.5, 1.0), 3, 2, 2);

        double[] Solve(bool viaLaw)
        {
            var mesh = AnalysisMesh.Of(tets);
            var model = new StructuralModel(mesh, material);
            if (viaLaw)
                model.SetElasticity(0, asOrthotropic);
            model.Fix(Facets.All, Dof.All);
            var temperature = new double[mesh.NodeCount];
            Array.Fill(temperature, rise);
            model.ThermalLoad(temperature, 0.0);
            var results = StructuralSolver.Solve(model);
            var values = new double[mesh.ElementCount];
            for (int el = 0; el < mesh.ElementCount; el++)
                values[el] = results.ElementStress(el).Xx;
            return values;
        }

        var scalar = Solve(false);
        var general = Solve(true);
        double scale = 0, worst = 0;
        for (int i = 0; i < scalar.Length; i++)
        {
            scale = Math.Max(scale, Math.Abs(scalar[i]));
            worst = Math.Max(worst, Math.Abs(scalar[i] - general[i]));
        }
        output.WriteLine($"scalar vs general thermal load: {worst:E3} of {scale:E3}");
        Assert.True(worst <= 1e-11 * scale, $"differs by {worst:E3} of {scale:E3}");
    }

    [Fact]
    public void AnExpansionStatedOnAnISOTROPICLawIsNotSilentlyIgnored()
    {
        // The trap this pins: an elastically isotropic, thermally DIRECTIONAL material is
        // unusual but real, and the thermal path must branch on "did the law state an
        // expansion" rather than on "is the law isotropic" - the latter would take the
        // scalar route, read the Material's coefficient and discard the law's three, which
        // is a stated input with no effect.
        const double rise = 50.0;
        double[] alpha = [2.0e-6, 20.0e-6, 11.0e-6];
        var material = new Material("steel", 210_000, 0.3, 7.85e-9, 0, 0, 12e-6);
        var law = ElasticLaw.FromMaterial(material)
            .WithThermalExpansion(Frame3d.WorldXY, alpha[0], alpha[1], alpha[2]);

        Assert.True(law.IsIsotropic);
        Assert.True(law.StatesOwnThermalExpansion);

        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(2.0, 1.5, 1.0), 3, 2, 2);
        var mesh = AnalysisMesh.Of(tets);
        var model = new StructuralModel(mesh, material);
        model.SetElasticity(0, law);

        int origin = FeaPatchTests.FindNode(mesh, Vector3d.Zero);
        int alongX = FeaPatchTests.FindNode(mesh, new Vector3d(2.0, 0, 0));
        int alongY = FeaPatchTests.FindNode(mesh, new Vector3d(0, 1.5, 0));
        model.FixNode(origin, Dof.All);
        model.FixNode(alongX, Dof.Y | Dof.Z);
        model.FixNode(alongY, Dof.Z);

        var temperature = new double[mesh.NodeCount];
        Array.Fill(temperature, rise);
        model.ThermalLoad(temperature, 0.0);

        var results = StructuralSolver.Solve(model);
        var strain = results.ElementStrain(0);
        output.WriteLine(
            $"free strain ({strain.Xx:E3}, {strain.Yy:E3}, {strain.Zz:E3}) against "
            + $"the law's ({alpha[0] * rise:E3}, {alpha[1] * rise:E3}, {alpha[2] * rise:E3}) "
            + $"and the material's scalar {material.ThermalExpansion * rise:E3}");

        double scale = alpha[1] * rise;
        Assert.Equal(alpha[0] * rise, strain.Xx, scale * 1e-10);
        Assert.Equal(alpha[1] * rise, strain.Yy, scale * 1e-10);
        Assert.Equal(alpha[2] * rise, strain.Zz, scale * 1e-10);
        // And it is genuinely NOT what the material's scalar would have given, in all three.
        Assert.True(Math.Abs(strain.Xx - material.ThermalExpansion * rise) > 0.1 * scale);
    }

    // ---- refusals -----------------------------------------------------------------------

    [Fact]
    public void AnAnisotropicRegionWithOnlyTheMaterialsScalarExpansionIsRefusedByName()
    {
        // The other side of the branch above, and the reason it needs BOTH halves: a
        // directional region whose expansion is stated only as the Material's single scalar
        // has no well-defined thermal load, because the scalar route builds it from the
        // MATERIAL's Lame parameters and those are not this region's stiffness at all. The
        // refusal names where the coefficient belongs.
        var material = new Material("carrier", E1, 0.3, 1.6e-9, 0, 0, 12e-6);
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(2.0, 1.5, 1.0), 2, 2, 1);
        var mesh = AnalysisMesh.Of(tets);
        var model = new StructuralModel(mesh, material);
        model.SetElasticity(0, Lamina(Frame3d.WorldXY));   // states no expansion of its own

        var temperature = new double[mesh.NodeCount];
        Array.Fill(temperature, 40.0);
        var ex = Assert.Throws<FeaException>(() => model.ThermalLoad(temperature, 0.0));
        output.WriteLine(ex.Message);
        Assert.Contains("ElasticLaw.WithThermalExpansion", ex.Message);
        Assert.Contains("lamina", ex.Message);
    }

    [Fact]
    public void APoissonsRatioTooLargeForItsModulusRatioIsRefusedByName()
    {
        // nu12 = 0.9 against E1/E2 = 15 breaks |nu_ij| < sqrt(E_i/E_j)? No - it does not,
        // and that is the point of testing the DETERMINANT condition instead: each pairwise
        // bound can hold while the three together still make the compliance indefinite.
        var ex = Assert.Throws<FeaException>(() => ElasticLaw.Orthotropic(
            Frame3d.WorldXY, 100_000, 100_000, 100_000, 0.7, 0.7, 0.7, 4_000, 4_000, 4_000));
        Assert.Contains("positive definite", ex.Message);
        Assert.Contains("sqrt(E_i / E_j)", ex.Message);
    }

    [Fact]
    public void AZeroModulusIsRefusedBeforeTheFactorization()
    {
        var ex = Assert.Throws<FeaException>(() => ElasticLaw.Orthotropic(
            Frame3d.WorldXY, E1, 0.0, E3, Nu12, Nu23, Nu13, G12, G23, G13));
        Assert.Contains("e2", ex.Message);
        Assert.Contains("singular", ex.Message);
    }

    [Fact]
    public void AnAsymmetricStiffnessMatrixIsRefusedAsATranscriptionError()
    {
        var d = new Material("steel", 210_000, 0.3).ConstitutiveMatrix();
        d[1] *= 1.05;
        var ex = Assert.Throws<FeaException>(
            () => ElasticLaw.Anisotropic(Frame3d.WorldXY, d, "broken"));
        Assert.Contains("not symmetric", ex.Message);
        Assert.Contains("D[0,1]", ex.Message);
    }

    [Fact]
    public void AWronglySizedStiffnessMatrixIsRefused()
    {
        var ex = Assert.Throws<FeaException>(
            () => ElasticLaw.Anisotropic(Frame3d.WorldXY, new double[21]));
        Assert.Contains("36", ex.Message);
    }

    [Fact]
    public void ATransverselyIsotropicLawIsTheOrthotropicOneWithFiveConstants()
    {
        var frame = RotatedAboutZ(23.0);
        double g23 = E2 / (2.0 * (1.0 + Nu23));
        var five = ElasticLaw.TransverselyIsotropic(frame, E1, E2, Nu12, Nu23, G12);
        var nine = ElasticLaw.Orthotropic(
            frame, E1, E2, E2, Nu12, Nu23, Nu12, G12, g23, G12);

        double scale = 0;
        foreach (double v in nine.StiffnessMatrix)
            scale = Math.Max(scale, Math.Abs(v));
        for (int i = 0; i < 36; i++)
        {
            Assert.True(
                Math.Abs(five.StiffnessMatrix[i] - nine.StiffnessMatrix[i]) <= 1e-12 * scale,
                $"entry {i}");
        }
    }
}
