using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Verification for classical lamination theory and the smeared law it produces.
///
/// <para><b>The lamina constants are TRANSCRIBED and flagged; everything derived from them
/// is CHECKED against a closed form.</b> The row is the canonical T300/5208 graphite/epoxy
/// one quoted by R. M. Jones, <i>Mechanics of Composite Materials</i> (2nd ed., 1999) and by
/// Tsai &amp; Hahn, <i>Introduction to Composite Materials</i> — ⚠ verify against a real data
/// sheet before designing anything, the <c>StandardHoles</c> convention. What is NOT
/// transcribed is any ABD entry or equivalent modulus: those are asserted against closed
/// forms DERIVED here from the reduced stiffnesses, because a re-typed number agrees with
/// its own transcription error and a derived one does not.</para>
///
/// <para><b>The Qbar oracle shares nothing with the production path.</b> Production reaches
/// the plane-stress reduced stiffness by asking <see cref="ElasticLaw.TransverselyIsotropic"/>
/// for the ply's rotated 6x6 (the Voigt stress transformation) and statically condensing the
/// out-of-plane rows. The oracle here builds the 3x3 plane-stress <c>Q</c> straight from the
/// engineering constants and rotates it as a FOURTH-ORDER TENSOR by index summation — no
/// Voigt vector, no engineering shear, no condensation. Two derivations sharing only the
/// physics, which is the rule §3h records after the engineering-shear trap.</para>
/// </summary>
public class LaminateTests(ITestOutputHelper output)
{
    // ⚠ Transcribed, verify against a data sheet: T300/5208 graphite/epoxy, the row Jones
    // and Tsai & Hahn both quote. MPa throughout (ModelUnits' mm/N/MPa system).
    private const double E1 = 181_000.0;
    private const double E2 = 10_300.0;
    private const double Nu12 = 0.28;
    private const double G12 = 7_170.0;

    // The standard cured prepreg ply thickness. Dyadic on purpose: the interface
    // coordinates of a symmetric stack are then exact, so "B is zero" is a statement about
    // the layup rather than about the accumulation.
    private const double PlyThickness = 0.125;

    private static LaminaProperties Lamina(double nu23 = 0.4) =>
        new(E1, E2, Nu12, G12, nu23, "T300/5208");

    // ---- the independent oracle --------------------------------------------------------

    /// <summary>The plane-stress reduced stiffness in the MATERIAL frame, straight from the
    /// engineering constants. Voigt order (11, 22, 12) with engineering shear.</summary>
    private static double[] ReducedQ()
    {
        double nu21 = Nu12 * E2 / E1;
        double denominator = 1.0 - Nu12 * nu21;
        return
        [
            E1 / denominator, Nu12 * E2 / denominator, 0,
            Nu12 * E2 / denominator, E2 / denominator, 0,
            0, 0, G12,
        ];
    }

    /// <summary>
    /// <c>Q</c> rotated to the laminate frame by fourth-order TENSOR rotation:
    /// <c>C'_ijkl = R_ip R_jq R_kr R_ls C_pqrs</c> over 2D indices, with R's columns the
    /// material axes in laminate coordinates. Shares nothing with the production route.
    /// </summary>
    private static double[] RotatedQByTensor(double degrees)
    {
        var q = ReducedQ();
        // Voigt (11, 22, 12) -> the 2x2x2x2 tensor. With engineering shear in the STRAIN
        // vector the map carries no factors: sigma_12 = Q31 e11 + Q32 e22 + Q33 gamma and
        // gamma = 2 e_12 = e_12 + e_21, so C_1212 = Q33 directly.
        var c = new double[16];
        int Slot(int i, int j) => i == j ? i : 2;
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                for (int k = 0; k < 2; k++)
                    for (int l = 0; l < 2; l++)
                        c[((i * 2 + j) * 2 + k) * 2 + l] = q[Slot(i, j) * 3 + Slot(k, l)];

        double a = degrees * Math.PI / 180.0;
        double cs = Math.Cos(a), sn = Math.Sin(a);
        double[] r = [cs, -sn, sn, cs];   // columns are the material axes in laminate coords

        var rotated = new double[16];
        for (int i = 0; i < 2; i++)
        {
            for (int j = 0; j < 2; j++)
            {
                for (int k = 0; k < 2; k++)
                {
                    for (int l = 0; l < 2; l++)
                    {
                        double sum = 0;
                        for (int p = 0; p < 2; p++)
                            for (int qq = 0; qq < 2; qq++)
                                for (int rr = 0; rr < 2; rr++)
                                    for (int s = 0; s < 2; s++)
                                        sum += r[i * 2 + p] * r[j * 2 + qq] * r[k * 2 + rr]
                                            * r[l * 2 + s]
                                            * c[((p * 2 + qq) * 2 + rr) * 2 + s];
                        rotated[((i * 2 + j) * 2 + k) * 2 + l] = sum;
                    }
                }
            }
        }

        // Back to Voigt.
        int[] first = [0, 1, 0];
        int[] second = [0, 1, 1];
        var result = new double[9];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                result[i * 3 + j] = rotated[
                    ((first[i] * 2 + second[i]) * 2 + first[j]) * 2 + second[j]];
            }
        }
        return result;
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(15.0)]
    [InlineData(30.0)]
    [InlineData(45.0)]
    [InlineData(60.0)]
    [InlineData(90.0)]
    [InlineData(-45.0)]
    public void TheReducedStiffnessMatchesAnIndependentTensorRotation(double degrees)
    {
        // A one-ply laminate's A matrix IS Qbar times the thickness, so this measures the
        // production condensation-and-rotation through the public surface.
        var laminate = Laminate.Stack(Lamina(), PlyThickness, degrees);
        var expected = RotatedQByTensor(degrees);

        double scale = expected.Max(Math.Abs);
        double worst = 0;
        var a = laminate.A;
        for (int i = 0; i < 9; i++)
            worst = Math.Max(worst, Math.Abs(a[i] / PlyThickness - expected[i]));

        output.WriteLine(
            $"{degrees:F0} deg: Qbar11 {a[0] / PlyThickness:F1} vs {expected[0]:F1}, "
            + $"Qbar16 {a[2] / PlyThickness:F1} vs {expected[2]:F1}, "
            + $"worst {worst:E3} of {scale:E3}");
        Assert.True(worst <= 1e-9 * scale, $"Qbar differs by {worst:E3} of {scale:E3}");
    }

    [Fact]
    public void CondensingTheThreeDimensionalLawGivesTheClassicalPlaneStressReduction()
    {
        // The production route to Qbar is a static condensation of the full 3D transversely
        // isotropic stiffness. That it lands on the textbook Q11 = E1/(1 - nu12 nu21) is a
        // theorem, not a coincidence: condensing sigma_zz, tau_yz and tau_zx to zero IS the
        // plane-stress assumption, which is what a thin ply's free surfaces impose.
        var laminate = Laminate.Stack(Lamina(), PlyThickness, 0.0);
        var expected = ReducedQ();
        var a = laminate.A;
        double scale = expected.Max(Math.Abs);
        for (int i = 0; i < 9; i++)
        {
            Assert.True(
                Math.Abs(a[i] / PlyThickness - expected[i]) <= 1e-10 * scale,
                $"Q[{i / 3},{i % 3}] = {a[i] / PlyThickness:G10} against {expected[i]:G10}");
        }
    }

    // ---- the exact structural identities ------------------------------------------------

    [Fact]
    public void ACrossPlyHasNoShearCouplingAtAll_Exactly()
    {
        // A16/A26 and D16/D26 vanish for a cross-ply because every ply's own Qbar16 does.
        // They read EXACTLY zero rather than at 1e-17 because a quarter turn is taken from a
        // table (cos 90 = 0 exactly), which is the repository's standing "a quarter turn is a
        // sign swap, never a cos" rule reaching CLT.
        var laminate = Laminate.Symmetric(Lamina(), PlyThickness, 0.0, 90.0);
        var a = laminate.A;
        var d = laminate.D;
        Assert.True(a[2] == 0.0, $"A16 = {a[2]:R}");
        Assert.True(a[5] == 0.0, $"A26 = {a[5]:R}");
        Assert.True(d[2] == 0.0, $"D16 = {d[2]:R}");
        Assert.True(d[5] == 0.0, $"D26 = {d[5]:R}");
        Assert.True(laminate.IsBalanced);
    }

    [Fact]
    public void ABalancedAngleFlyCancelsItsShearCouplingExactly_AndKeepsItsBendTwist()
    {
        // The other half of the sine convention: +theta and -theta produce exactly opposite
        // Qbar16 terms because the sine is taken from the MAGNITUDE and negated, so the
        // in-plane cancellation is bit-exact at an angle no table covers.
        var laminate = Laminate.Symmetric(Lamina(), PlyThickness, 45.0, -45.0);
        var a = laminate.A;
        var d = laminate.D;
        Assert.True(a[2] == 0.0, $"A16 = {a[2]:R}");
        Assert.True(a[5] == 0.0, $"A26 = {a[5]:R}");
        Assert.True(laminate.IsBalanced);

        // But D16 and D26 do NOT cancel: the +45 and -45 plies sit at different distances
        // from the midplane, so their contributions are weighted differently. That is the
        // textbook bend-twist coupling a balanced symmetric laminate keeps, and asserting it
        // is what stops "balanced" being read as "uncoupled".
        double bendTwist = (Math.Abs(d[2]) + Math.Abs(d[5])) / d.ToArray().Max(Math.Abs);
        output.WriteLine($"[+-45]s bend-twist D16/max|D| = {bendTwist:F4}");
        Assert.True(bendTwist > 0.1, $"expected real bend-twist coupling, measured {bendTwist:E3}");
    }

    [Fact]
    public void ASymmetricStackHasNoCouplingMatrixAndAnUnsymmetricOneIsRefusedByName()
    {
        var symmetric = Laminate.Symmetric(Lamina(), PlyThickness, 0.0, 90.0);
        output.WriteLine($"[0/90]s coupling ratio {symmetric.CouplingRatio:E3}");
        Assert.True(symmetric.IsSymmetric);
        Assert.True(symmetric.CouplingRatio < 1e-15);
        symmetric.ToElasticLaw();   // does not throw

        var unsymmetric = Laminate.Stack(Lamina(), PlyThickness, 0.0, 90.0);
        output.WriteLine($"[0/90] coupling ratio {unsymmetric.CouplingRatio:F4}");
        Assert.False(unsymmetric.IsSymmetric);
        Assert.True(unsymmetric.CouplingRatio > 0.1);

        var error = Assert.Throws<FeaException>(() => unsymmetric.ToElasticLaw());
        Assert.Contains("UNSYMMETRIC", error.Message, StringComparison.Ordinal);
        Assert.Contains("Laminate.Symmetric", error.Message, StringComparison.Ordinal);
    }

    // ---- the equivalent constants, against closed forms ---------------------------------

    [Fact]
    public void AUnidirectionalLaminateReportsItsOwnLaminaConstants()
    {
        // The strongest single check of the A-inverse machinery: for a stack of identically
        // oriented plies the equivalent single-layer constants must be the LAMINA's own, so
        // every step of the derivation is an identity with a known answer.
        var laminate = Laminate.Stack(Lamina(), PlyThickness, 0.0, 0.0, 0.0, 0.0);
        var c = laminate.InPlane;
        output.WriteLine(
            $"[0]4 Ex {c.Ex:F1} Ey {c.Ey:F1} Gxy {c.Gxy:F1} nu_xy {c.NuXy:F4}");
        Assert.Equal(E1, c.Ex, E1 * 1e-12);
        Assert.Equal(E2, c.Ey, E2 * 1e-12);
        Assert.Equal(G12, c.Gxy, G12 * 1e-12);
        Assert.Equal(Nu12, c.NuXy, 1e-12);
        Assert.Equal(Nu12 * E2 / E1, c.NuYx, 1e-12);

        // And there is nothing to smear through the thickness, so the flexural constants are
        // the same numbers and the discrepancy is zero.
        Assert.Equal(E1, laminate.Flexural.Ex, E1 * 1e-10);
        Assert.True(laminate.FlexuralDiscrepancy < 1e-12,
            $"discrepancy {laminate.FlexuralDiscrepancy:E3}");
    }

    [Fact]
    public void TheCrossPlyExtensionalMatrixMatchesItsClosedForm()
    {
        // [0/90]s with equal plies: half the thickness at each angle, so
        //   A11 = A22 = (h/2)(Q11 + Q22),  A12 = h Q12,  A66 = h Q66.
        // Derived here from the reduced stiffnesses rather than transcribed.
        var laminate = Laminate.Symmetric(Lamina(), PlyThickness, 0.0, 90.0);
        double h = laminate.Thickness;
        var q = ReducedQ();
        double expected11 = 0.5 * h * (q[0] + q[4]);
        double expected12 = h * q[1];
        double expected66 = h * q[8];

        var a = laminate.A;
        output.WriteLine($"[0/90]s A11 {a[0]:F1} A22 {a[4]:F1} A12 {a[1]:F1} A66 {a[8]:F1}");
        Assert.Equal(expected11, a[0], expected11 * 1e-10);
        Assert.Equal(expected11, a[4], expected11 * 1e-10);
        Assert.Equal(expected12, a[1], expected12 * 1e-10);
        Assert.Equal(expected66, a[8], expected66 * 1e-10);

        // Ex = Ey follows, and both sit between the lamina's own two moduli — the sanity
        // check a wrong ply fraction would fail while still satisfying the entries above.
        var c = laminate.InPlane;
        output.WriteLine($"[0/90]s Ex {c.Ex:F1} Ey {c.Ey:F1} Gxy {c.Gxy:F1} nu_xy {c.NuXy:F4}");
        Assert.Equal(c.Ex, c.Ey, c.Ex * 1e-10);
        Assert.InRange(c.Ex, E2, E1);
        // The shear modulus is untouched by cross-plying: Qbar66 is the same at 0 and 90.
        Assert.Equal(G12, c.Gxy, G12 * 1e-10);
    }

    [Fact]
    public void TheAngleFlyExtensionalMatrixMatchesItsClosedForm_AndIsTheShearLayup()
    {
        // [+-45]s with equal plies. At 45 degrees c^2 = s^2 = 1/2, so
        //   A11 = A22 = h(Q11 + Q22 + 2 Q12 + 4 Q66)/4
        //   A12       = h(Q11 + Q22 + 2 Q12 - 4 Q66)/4
        //   A66       = h(Q11 + Q22 - 2 Q12)/4
        var laminate = Laminate.Symmetric(Lamina(), PlyThickness, 45.0, -45.0);
        double h = laminate.Thickness;
        var q = ReducedQ();
        double sum = q[0] + q[4] + 2 * q[1];
        double expected11 = h * (sum + 4 * q[8]) / 4.0;
        double expected12 = h * (sum - 4 * q[8]) / 4.0;
        double expected66 = h * (q[0] + q[4] - 2 * q[1]) / 4.0;

        var a = laminate.A;
        output.WriteLine($"[+-45]s A11 {a[0]:F1} A12 {a[1]:F1} A66 {a[8]:F1}");
        Assert.Equal(expected11, a[0], expected11 * 1e-10);
        Assert.Equal(expected11, a[4], expected11 * 1e-10);
        Assert.Equal(expected12, a[1], expected12 * 1e-10);
        Assert.Equal(expected66, a[8], expected66 * 1e-10);

        // The engineering fact the layup exists for: its in-plane shear modulus is a large
        // multiple of the lamina's own, because a +-45 fibre carries shear as tension.
        var c = laminate.InPlane;
        double ratio = c.Gxy / G12;
        output.WriteLine(
            $"[+-45]s Ex {c.Ex:F1} Gxy {c.Gxy:F1} (x{ratio:F1} the lamina G12), nu_xy {c.NuXy:F3}");
        Assert.True(ratio > 5, $"expected the shear layup to be far stiffer in shear; x{ratio:F2}");
        // And it is soft in extension - well under the cross-ply's Ex.
        Assert.True(c.Ex < 0.5 * Laminate.Symmetric(Lamina(), PlyThickness, 0.0, 90.0).InPlane.Ex);
    }

    // ---- what the smearing costs --------------------------------------------------------

    [Fact]
    public void TheFlexuralDiscrepancyIsMeasuredAndLargeForACrossPly()
    {
        // The honest cost of a smeared solid law, as a number rather than a caveat: the
        // outer plies dominate bending, so a [0/90]s laminate is stiffer in bending about x
        // than its in-plane modulus implies, and softer about y.
        var laminate = Laminate.Symmetric(Lamina(), PlyThickness, 0.0, 90.0);
        var inPlane = laminate.InPlane;
        var flexural = laminate.Flexural;
        output.WriteLine(
            $"[0/90]s in-plane Ex {inPlane.Ex:F1} / flexural Ex {flexural.Ex:F1} "
            + $"(x{flexural.Ex / inPlane.Ex:F3}); Ey {inPlane.Ey:F1} / {flexural.Ey:F1}; "
            + $"discrepancy {laminate.FlexuralDiscrepancy:F4}");

        Assert.True(flexural.Ex > inPlane.Ex * 1.2, "the outer 0 plies must stiffen bending about x");
        Assert.True(flexural.Ey < inPlane.Ey * 0.85, "and soften bending about y");
        Assert.True(laminate.FlexuralDiscrepancy > 0.2,
            $"expected a large smearing cost, measured {laminate.FlexuralDiscrepancy:E3}");
    }

    [Fact]
    public void TheTransversePoissonRatioMovesNothingInPlane()
    {
        // The stated blast radius of LaminaProperties' one defaulted constant: every
        // plane-stress quantity is a function of (E1, E2, nu12, G12) alone, so nu23 can only
        // move the smeared law's through-thickness block.
        //
        // MEASURED AT ROUND-OFF RATHER THAN BITWISE, and the reason is worth keeping: the
        // production route to Qbar is a static CONDENSATION of the full 3D law, whose C_oo
        // and C_io blocks both depend on nu23, and the dependence cancels mathematically
        // rather than structurally. So doubling nu23 moves D by an ulp (10690.704098935392
        // against ...39) - which is exactly the evidence that the cancellation is a theorem
        // and not an accident of which terms were written down. A trig-expansion Qbar would
        // be bit-identical here and would have bought that with a second copy of the Voigt
        // rotation; this is the better trade, priced.
        var a = Laminate.Symmetric(Lamina(0.30), PlyThickness, 0.0, 45.0, -45.0, 90.0);
        var b = Laminate.Symmetric(Lamina(0.49), PlyThickness, 0.0, 45.0, -45.0, 90.0);

        var (aa, ba) = (a.A.ToArray(), b.A.ToArray());
        var (ad, bd) = (a.D.ToArray(), b.D.ToArray());
        double aScale = aa.Max(Math.Abs);
        double dScale = ad.Max(Math.Abs);
        double worstA = 0, worstD = 0;
        for (int i = 0; i < 9; i++)
        {
            worstA = Math.Max(worstA, Math.Abs(aa[i] - ba[i]));
            worstD = Math.Max(worstD, Math.Abs(ad[i] - bd[i]));
        }
        output.WriteLine(
            $"nu23 0.30 -> 0.49 moves A by {worstA:E3} of {aScale:E3}, D by {worstD:E3} of {dScale:E3}");
        Assert.True(worstA <= 1e-15 * aScale, $"A moved by {worstA:E3} of {aScale:E3}");
        Assert.True(worstD <= 1e-15 * dScale, $"D moved by {worstD:E3} of {dScale:E3}");

        // And it DOES move the through-thickness block, or the parameter would be inert.
        var lawA = a.ToElasticLaw().StiffnessMatrix.ToArray();
        var lawB = b.ToElasticLaw().StiffnessMatrix.ToArray();
        output.WriteLine($"C_yzyz at nu23 0.30 = {lawA[4 * 6 + 4]:F1}, at 0.49 = {lawB[4 * 6 + 4]:F1}");
        Assert.True(Math.Abs(lawA[4 * 6 + 4] - lawB[4 * 6 + 4]) > 1.0);
    }

    [Fact]
    public void TheSmearedLawReducesToTheClassicalLaminationTheoryInPlane()
    {
        // The theorem the mixed homogenisation is built on: condensing the smeared 6x6 over
        // its out-of-plane rows and columns - the plane-stress reduction a thin laminate's
        // free surfaces impose - returns exactly A/h. So the 3D law a solid element carries
        // and the CLT the design was done with cannot disagree about in-plane behaviour.
        var laminate = Laminate.Symmetric(Lamina(), PlyThickness, 0.0, 45.0, -45.0, 90.0);
        var c = laminate.ToElasticLaw().StiffnessMatrix.ToArray();

        int[] inPlane = [0, 1, 3];
        int[] outOfPlane = [2, 4, 5];
        var cii = new double[9];
        var cio = new double[9];
        var coo = new double[9];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                cii[i * 3 + j] = c[inPlane[i] * 6 + inPlane[j]];
                cio[i * 3 + j] = c[inPlane[i] * 6 + outOfPlane[j]];
                coo[i * 3 + j] = c[outOfPlane[i] * 6 + outOfPlane[j]];
            }
        }
        var cooInverse = Invert3(coo);

        double h = laminate.Thickness;
        var a = laminate.A;
        double scale = a.ToArray().Max(Math.Abs) / h;
        double worst = 0;
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                double sum = 0;
                for (int k = 0; k < 3; k++)
                    for (int l = 0; l < 3; l++)
                        sum += cio[i * 3 + k] * cooInverse[k * 3 + l] * cio[j * 3 + l];
                worst = Math.Max(worst, Math.Abs(cii[i * 3 + j] - sum - a[i * 3 + j] / h));
            }
        }
        output.WriteLine($"condensed smeared law against A/h: worst {worst:E3} of {scale:E3}");
        Assert.True(worst <= 1e-9 * scale, $"differs by {worst:E3} of {scale:E3}");
    }

    [Fact]
    public void TheSmearedLawIsSymmetricAndPositiveDefinite()
    {
        // Symmetry is by construction (the off-diagonal blocks are exact transposes), and
        // positive definiteness is what ElasticLaw.Anisotropic checks by Cholesky - so
        // reaching the law at all is the assertion. Both are worth pinning because the
        // mixed homogenisation would be plausible and wrong if either failed.
        var laminate = Laminate.Symmetric(Lamina(), PlyThickness, 0.0, 45.0, -45.0, 90.0);
        var c = laminate.ToElasticLaw().StiffnessMatrix.ToArray();
        double scale = c.Max(Math.Abs);
        for (int i = 0; i < 6; i++)
        {
            for (int j = 0; j < 6; j++)
            {
                Assert.True(
                    Math.Abs(c[i * 6 + j] - c[j * 6 + i]) <= 1e-12 * scale,
                    $"C[{i},{j}] = {c[i * 6 + j]:G6} against C[{j},{i}] = {c[j * 6 + i]:G6}");
            }
        }
        // A quasi-isotropic layup is nearly isotropic IN PLANE, which is the whole point of
        // the [0/45/-45/90]s stack - and a good check that the rotation is not scrambling
        // directions.
        var inPlane = laminate.InPlane;
        output.WriteLine($"quasi-isotropic Ex {inPlane.Ex:F1} Ey {inPlane.Ey:F1} Gxy {inPlane.Gxy:F1}");
        Assert.Equal(inPlane.Ex, inPlane.Ey, inPlane.Ex * 1e-9);
        // E = 2G(1+nu) is the isotropic identity; a quasi-isotropic laminate satisfies it.
        Assert.Equal(inPlane.Ex, 2 * inPlane.Gxy * (1 + inPlane.NuXy), inPlane.Ex * 1e-9);
    }

    // ---- end to end ---------------------------------------------------------------------

    [Fact]
    public void ASolvedBarOfTheSmearedLawReproducesTheLaminateModulus()
    {
        // The whole point, measured through the solver: a prismatic bar under a uniform
        // traction is a constant-strain state, exactly representable by a linear tetrahedron,
        // so the solve reproduces the CLT modulus to round-off rather than converging onto
        // it. That leaves the homogenisation as the only thing under test.
        const double length = 2.0, width = 1.5, height = 1.0, sigma = 25.0;

        var laminate = Laminate.Symmetric(Lamina(), PlyThickness, 0.0, 90.0);
        var constants = laminate.InPlane;

        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, new Vector3d(length, width, height), 4, 3, 2);
        var mesh = AnalysisMesh.Of(tets);
        var model = new StructuralModel(mesh, new Material("carrier", E1, 0.3, 1.6e-9));
        model.SetElasticity(0, laminate.ToElasticLaw(Frame3d.WorldXY));

        model.Fix(Facets.OnPlane(Vector3d.Zero, Vector3d.UnitX), Dof.X);
        int origin = FeaPatchTests.FindNode(mesh, Vector3d.Zero);
        int alongY = FeaPatchTests.FindNode(mesh, new Vector3d(0, width, 0));
        model.FixNode(origin, Dof.Y | Dof.Z);
        model.FixNode(alongY, Dof.Z);
        model.Traction(
            Facets.OnPlane(new Vector3d(length, 0, 0), Vector3d.UnitX),
            new Vector3d(sigma, 0, 0));

        var results = StructuralSolver.Solve(model);

        double expectedStrain = sigma / constants.Ex;
        double worst = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var strain = results.ElementStrain(e);
            worst = Math.Max(worst, Math.Abs(strain.Xx - expectedStrain));
            worst = Math.Max(worst, Math.Abs(strain.Yy + constants.NuXy * expectedStrain));
        }
        double measured = sigma / results.ElementStrain(0).Xx;
        output.WriteLine(
            $"solved Ex {measured:F2} MPa against CLT {constants.Ex:F2}; "
            + $"worst strain error {worst:E3} of {expectedStrain:E3}");
        Assert.Equal(constants.Ex, measured, constants.Ex * 1e-9);
        Assert.True(worst <= 1e-9 * Math.Abs(expectedStrain), $"strain error {worst:E3}");
    }

    // ---- refusals ------------------------------------------------------------------------

    [Fact]
    public void MalformedLayupsAreRefusedByName()
    {
        var lamina = Lamina();
        Assert.Contains("at least one ply",
            Assert.Throws<FeaException>(() => Laminate.Of()).Message, StringComparison.Ordinal);
        Assert.Contains("thickness",
            Assert.Throws<FeaException>(
                () => Laminate.Stack(lamina, 0.0, 0.0)).Message, StringComparison.Ordinal);
        Assert.Contains("E1 must be finite and positive",
            Assert.Throws<FeaException>(
                () => new LaminaProperties(0, E2, Nu12, G12)).Message.Replace("e1", "E1"),
            StringComparison.Ordinal);
        Assert.Contains("nu21",
            Assert.Throws<FeaException>(
                () => new LaminaProperties(E1, E2, 0.9, G12)).Message, StringComparison.Ordinal);
    }

    private static double[] Invert3(double[] m)
    {
        double a = m[0], b = m[1], c = m[2];
        double d = m[3], e = m[4], f = m[5];
        double g = m[6], h = m[7], i = m[8];
        double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        double inv = 1.0 / det;
        return
        [
            (e * i - f * h) * inv, (c * h - b * i) * inv, (b * f - c * e) * inv,
            (f * g - d * i) * inv, (a * i - c * g) * inv, (c * d - a * f) * inv,
            (d * h - e * g) * inv, (b * g - a * h) * inv, (a * e - b * d) * inv,
        ];
    }
}
