using System.Numerics;
using EngrCAD.Core;
using EngrCAD.Core.Solvers;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The direct per-frequency harmonic solve, verified three independent ways:
///
/// <para><b>(1) Against the modal route on a proportionally damped model</b>, where the two
/// methods answer the same question — exactly on the 1x1-reduced scalar fixture (a single
/// mode IS the whole basis, so there is no truncation at all), and to the truncation
/// correction's own error on a real cantilever mesh with the mode-acceleration correction
/// on.</para>
///
/// <para><b>(2) Against a hand-built complex oracle on a NON-proportional model</b> — a
/// dashpot coupling two far-apart nodes of one mesh, whose 2x2 reduced system is solved by
/// Cramer's rule in explicit complex arithmetic sharing nothing with SparseLdlt. The two
/// free nodes share no element, so the dashpot's coupling entry sits where the stiffness
/// pattern has NOTHING — the union-pattern case the factorization exists for — and the
/// per-region damping twin uses two DISJOINT bodies so each region's coefficient answers as
/// its own closed-form oscillator, which is what catches a per-region map applied to the
/// wrong region (the interface-value lesson: a total agrees just as happily with the
/// regions swapped).</para>
///
/// <para><b>(3) In the house style</b>: resonant amplification 1/(2·zeta) and the 90-degree
/// phase at resonance, exact on the scalar fixture because nothing is truncated.</para>
/// </summary>
public class DirectHarmonicTests(ITestOutputHelper output)
{
    // ---- (3) the scalar closed forms, exact ------------------------------------------

    [Fact]
    public void ResonantAmplificationIsExactlyOneOverTwiceZeta()
    {
        // Stiffness-proportional damping tuned to zeta at the fixture's own natural
        // frequency: beta = 2·zeta/omega. At omega = omega_n the 1-DOF response is
        // f/(i·omega·beta·k), so |u|/u_static = 1/(2·zeta) EXACTLY — no sweep sampling in
        // the way, unlike the modal test's 25.006, because the drive frequency is the
        // measured natural frequency itself.
        const double zeta = 0.02;
        var model = TransientFixtures.SingleDof(out int free);
        var (k, _, omega) = TransientFixtures.Properties(model, free);
        const double force = 10.0;
        model.NodalForce(free, new Vector3d(force, 0, 0));
        model.SetDamping(new RayleighDamping(0.0, 2.0 * zeta / omega));

        var response = DirectHarmonicSolver.Solve(model, new DirectHarmonicOptions
        {
            Frequencies = [omega / (2.0 * Math.PI)],
        });

        double amplitude = response.ResponseAt(free, 0)[0].Magnitude;
        double amplification = amplitude / (force / k);
        output.WriteLine($"amplification {amplification:G10} against {1.0 / (2.0 * zeta)}");
        output.WriteLine(response.ToText());

        // The only error sources are the factorization's round-off and the ulp gap
        // between the driven omega and the modal solve's own — both far below 1e-6.
        AssertRelative(1.0 / (2.0 * zeta), amplification, 1e-6);

        // Phase at resonance: the response is -i·|u| relative to the drive, i.e. -90 deg.
        double phase = response.ResponseAt(free, 0)[0].Phase * 180.0 / Math.PI;
        output.WriteLine($"phase {phase:G6} deg");
        Assert.Equal(-90.0, phase, 1e-4);
    }

    [Fact]
    public void AgreesWithTheModalRouteExactlyWhereNothingIsTruncated()
    {
        // The 1x1-reduced fixture: one mode is the COMPLETE basis, so modal superposition
        // is exact and the two methods must agree to round-off, not to a truncation bound.
        // Two identical models, because each route's damping statement excludes the
        // other's: the direct one reads the model, the modal one reads per-mode ratios.
        const double zeta = 0.03;
        var directModel = TransientFixtures.SingleDof(out int free);
        var modalModel = TransientFixtures.SingleDof(out _);
        var (_, _, omega) = TransientFixtures.Properties(directModel, free);
        const double force = 5.0;
        directModel.NodalForce(free, new Vector3d(force, 0, 0));
        modalModel.NodalForce(free, new Vector3d(force, 0, 0));

        double beta = 2.0 * zeta / omega;
        directModel.SetDamping(new RayleighDamping(0.0, beta));

        double f1 = omega / (2.0 * Math.PI);
        double[] sweep = [0.5 * f1, 0.9 * f1, f1, 1.1 * f1, 2.0 * f1];

        var direct = DirectHarmonicSolver.Solve(directModel, new DirectHarmonicOptions
        {
            Frequencies = sweep,
        });

        var modes = ModalSolver.Solve(modalModel, new ModalSolveOptions { ModeCount = 1 });
        var modal = HarmonicSolver.Solve(modes, new HarmonicSolveOptions
        {
            Frequencies = sweep,
            Damping = ModalDamping.Rayleigh(new RayleighDamping(0.0, beta)),
        });

        var directProbe = direct.ResponseAt(free, 0);
        var modalProbe = modal.ResponseAt(free, 0);
        for (int i = 0; i < sweep.Length; i++)
        {
            output.WriteLine(
                $"{sweep[i],12:N2} Hz  direct {directProbe[i].Magnitude:E10}  "
                + $"modal {modalProbe[i].Magnitude:E10}");
            AssertRelative(modalProbe[i].Magnitude, directProbe[i].Magnitude, 1e-9);
            Assert.Equal(modalProbe[i].Phase, directProbe[i].Phase, 1e-9);
        }
    }

    [Fact]
    public void AgreesWithTheCorrectedModalRouteOnARealMesh()
    {
        // A quadratic cantilever under a tip force, Rayleigh damping, six modes with the
        // mode-acceleration correction on. The two methods differ by exactly what modal
        // truncation cannot carry at non-zero frequency, so the tolerance is stated from
        // the measurement rather than wished: worst 3.5e-6 relative over this sweep
        // (measured — the six corrected modes carry this response almost completely),
        // asserted at 1e-4.
        const double length = 100.0, width = 12.0, depth = 8.0;
        var alpha = 40.0;
        var beta = 6e-6;

        StructuralModel Build()
        {
            var mesh = ModalFixtures.Beam(length, width, depth, 12, 2, 2, ElementOrder.Quadratic);
            var m = new StructuralModel(mesh, ModalFixtures.Steel);
            m.Fix(Facets.Tag(StructuredTetMesh.XMin));
            m.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(0, 0, -50.0));
            return m;
        }

        var directModel = Build();
        directModel.SetDamping(new RayleighDamping(alpha, beta));
        var modalModel = Build();

        var modes = ModalSolver.Solve(modalModel, new ModalSolveOptions { ModeCount = 6 });
        double f1 = modes.Mode(1).Frequency;
        double[] sweep = [0.25 * f1, 0.5 * f1, 0.95 * f1, f1, 1.05 * f1, 1.5 * f1];

        var statics = StructuralSolver.Solve(modalModel);
        var modal = HarmonicSolver.Solve(modes, new HarmonicSolveOptions
        {
            Frequencies = sweep,
            Damping = ModalDamping.Rayleigh(new RayleighDamping(alpha, beta)),
            StaticCorrection = statics,
        });

        var direct = DirectHarmonicSolver.Solve(directModel, new DirectHarmonicOptions
        {
            Frequencies = sweep,
        });

        double worst = 0;
        for (int i = 0; i < sweep.Length; i++)
        {
            double d = direct.PeakAmplitudeAt(i);
            double m = modal.PeakAmplitudeAt(i);
            double relative = Math.Abs(d - m) / d;
            worst = Math.Max(worst, relative);
            output.WriteLine($"{sweep[i],10:N2} Hz  direct {d:E8}  modal {m:E8}  rel {relative:E2}");
        }
        output.WriteLine($"worst relative gap {worst:E2}");
        output.WriteLine($"direct residual {direct.Report.WorstRelativeResidual:E2}");

        Assert.True(worst < 1e-4,
            $"Direct and corrected-modal responses differ by {worst:E2}; the gap should be "
            + "the modal truncation error, well under 1e-4 with six modes corrected.");
        // The unpivoted factorization's backward error is a property of each frequency's
        // conditioning, worst near resonance: measured 7.4e-9 on this sweep (the point
        // nearest the first mode dominates). Asserted at 1e-6 — the answer's own accuracy
        // is the residual squared over the gap, far tighter than the response comparison
        // above needs.
        Assert.True(direct.Report.WorstRelativeResidual < 1e-6,
            $"backward residual {direct.Report.WorstRelativeResidual:E2}");
    }

    // ---- (2) non-proportional damping against a hand-built complex oracle -------------

    /// <summary>Cramer's rule on the 2x2 complex system — explicit complex arithmetic
    /// sharing nothing with SparseLdlt's elimination.</summary>
    private static (Complex U0, Complex U1) Cramer2(
        Complex z00, Complex z01, Complex z10, Complex z11, Complex f0, Complex f1)
    {
        var det = z00 * z11 - z01 * z10;
        return ((f0 * z11 - z01 * f1) / det, (z00 * f1 - f0 * z10) / det);
    }

    [Fact]
    public void ADashpotBetweenUnconnectedNodesMatchesTheComplexOracle()
    {
        // One box, two free DOFs at opposite corners (X on each), a dashpot coupling them.
        // The corners share no element, so C's coupling entry sits where K and M have no
        // entry at all — the union-pattern case — and the reduced 2x2 system has diagonal
        // K and M with a full C. The oracle reads K, M via the same assembly (already
        // verified elsewhere) and solves the complex 2x2 by Cramer.
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(20, 20, 20), 2, 2, 2);
        var mesh = AnalysisMesh.Of(tets);
        var model = new StructuralModel(mesh, Materials.Steel);

        int nodeA = NearestNode(mesh, Vector3d.Zero);
        int nodeB = NearestNode(mesh, new Vector3d(20, 20, 20));
        Assert.False(ShareAnElement(mesh, nodeA, nodeB),
            "the fixture needs two nodes with no shared element, or it does not exercise "
            + "the union pattern");

        for (int node = 0; node < mesh.NodeCount; node++)
        {
            model.FixNode(node, node == nodeA || node == nodeB ? Dof.Y | Dof.Z : Dof.All);
        }
        const double c = 3.0e-3;
        model.Dashpot(nodeA, nodeB, new Vector3d(1, 0, 0), c);
        const double force = 7.0;
        model.NodalForce(nodeA, new Vector3d(force, 0, 0));

        // The reduced 2x2 K and M through the same assembly the solver uses (internals):
        // the oracle's independence is in the SOLVE, not the matrices — SparseLdlt's own
        // suite already checks the factorization against dense solves.
        var reduced = FeaAssembly.ReducedIndices(model, out int freeCount);
        Assert.Equal(2, freeCount);
        var k = FeaAssembly.Reduce(
            FeaAssembly.Stiffness(model, TetQuadrature.For(mesh.Order)), reduced, freeCount);
        var (fullMass, _) = FeaAssembly.Mass(model, TetQuadrature.ForMass(mesh.Order));
        var m = FeaAssembly.Reduce(fullMass, reduced, freeCount);

        double k00 = Entry(k, 0, 0), k01 = Entry(k, 0, 1), k11 = Entry(k, 1, 1);
        double m00 = Entry(m, 0, 0), m01 = Entry(m, 0, 1), m11 = Entry(m, 1, 1);
        Assert.Equal(0.0, k01);
        Assert.Equal(0.0, m01);

        int slotA = reduced[3 * nodeA];
        double fA = force;

        double[] sweep = [40.0, 400.0, 4000.0];
        var response = DirectHarmonicSolver.Solve(model, new DirectHarmonicOptions
        {
            Frequencies = sweep,
        });
        Assert.True(response.Report.NonProportional);

        foreach (var (hertz, index) in sweep.Select((f, i) => (f, i)))
        {
            double omega = 2.0 * Math.PI * hertz;
            // Z = K - omega²·M + i·omega·C with C = [[c, -c], [-c, c]] on the X pair.
            var z00 = new Complex(k00 - omega * omega * m00, omega * c);
            var z11 = new Complex(k11 - omega * omega * m11, omega * c);
            var z01 = new Complex(0, -omega * c);
            var (u0, u1) = slotA == 0
                ? Cramer2(z00, z01, z01, z11, fA, 0)
                : Cramer2(z00, z01, z01, z11, 0, fA);

            var uA = response.ResponseAt(nodeA, 0)[index];
            var uB = response.ResponseAt(nodeB, 0)[index];
            var (expectA, expectB) = slotA == 0 ? (u0, u1) : (u1, u0);

            output.WriteLine(
                $"{hertz,8:N1} Hz  A {uA.Real:E6}+{uA.Imaginary:E6}i  "
                + $"oracle {expectA.Real:E6}+{expectA.Imaginary:E6}i");
            AssertRelative(expectA.Real, uA.Real, 1e-10);
            AssertRelative(expectA.Imaginary, uA.Imaginary, 1e-10);
            AssertRelative(expectB.Real, uB.Real, 1e-10);
            AssertRelative(expectB.Imaginary, uB.Imaginary, 1e-10);

            // The dashpot genuinely couples the pair: the unloaded node responds.
            Assert.True(uB.Magnitude > 0);
        }
    }

    [Fact]
    public void PerRegionDampingLandsOnItsOwnRegion()
    {
        // A two-region bar (split at x = 20 on a grid plane), one free X DOF DEEP in each
        // region, different stiffness-proportional beta per region. Every element touching
        // a free node lies in that node's own region, so its damping entry is exactly
        // beta_region·K_nodal and each free DOF is its own closed-form oscillator
        // (k - omega²·m + i·omega·beta_r·k)·u = f. The oracle is that closed form with
        // DISTINCT betas — a per-region map applied to the wrong region fails it at the
        // right magnitude, where any total-only assertion would agree with the regions
        // swapped (the interface-value lesson).
        var tets = StructuredTetMesh.Box(
            new Vector3d(0, -5, -5), new Vector3d(40, 10, 10), 8, 2, 2,
            centroid => centroid.X < 20 ? 0 : 1);
        var mesh = AnalysisMesh.Of(tets);

        int nodeA = NearestNode(mesh, new Vector3d(5, 0, 0));
        int nodeB = NearestNode(mesh, new Vector3d(35, 0, 0));
        Assert.False(ShareAnElement(mesh, nodeA, nodeB));

        var model = new StructuralModel(mesh, Materials.Steel);
        for (int node = 0; node < mesh.NodeCount; node++)
            model.FixNode(node, node == nodeA || node == nodeB ? Dof.Y | Dof.Z : Dof.All);

        const double betaA = 4.0e-6, betaB = 9.0e-6, force = 3.0;
        model.SetDamping(0, new RayleighDamping(0.0, betaA));
        model.SetDamping(1, new RayleighDamping(0.0, betaB));
        model.NodalForce(nodeA, new Vector3d(force, 0, 0));
        model.NodalForce(nodeB, new Vector3d(force, 0, 0));

        var reduced = FeaAssembly.ReducedIndices(model, out int freeCount);
        Assert.Equal(2, freeCount);
        var k = FeaAssembly.Reduce(
            FeaAssembly.Stiffness(model, TetQuadrature.For(mesh.Order)), reduced, freeCount);
        var (fullMass, _) = FeaAssembly.Mass(model, TetQuadrature.ForMass(mesh.Order));
        var m = FeaAssembly.Reduce(fullMass, reduced, freeCount);
        int slotA = reduced[3 * nodeA];
        int slotB = reduced[3 * nodeB];
        double kA = Entry(k, slotA, slotA), kB = Entry(k, slotB, slotB);
        double mA = Entry(m, slotA, slotA), mB = Entry(m, slotB, slotB);

        double[] sweep = [800.0, 8000.0];
        var response = DirectHarmonicSolver.Solve(model, new DirectHarmonicOptions
        {
            Frequencies = sweep,
        });
        Assert.True(response.Report.NonProportional);

        for (int i = 0; i < sweep.Length; i++)
        {
            double omega = 2.0 * Math.PI * sweep[i];
            var expectA = force / new Complex(kA - omega * omega * mA, omega * betaA * kA);
            var expectB = force / new Complex(kB - omega * omega * mB, omega * betaB * kB);
            var uA = response.ResponseAt(nodeA, 0)[i];
            var uB = response.ResponseAt(nodeB, 0)[i];
            output.WriteLine(
                $"{sweep[i],8:N0} Hz  A {uA.Real:E8}+{uA.Imaginary:E8}i vs "
                + $"{expectA.Real:E8}+{expectA.Imaginary:E8}i");
            AssertRelative(expectA.Real, uA.Real, 1e-12);
            AssertRelative(expectA.Imaginary, uA.Imaginary, 1e-12);
            AssertRelative(expectB.Real, uB.Real, 1e-12);
            AssertRelative(expectB.Imaginary, uB.Imaginary, 1e-12);

            // The betas genuinely differ, so applying B's to A would miss by the ratio of
            // the imaginary terms — assert the oracle CAN see the difference.
            var wrongA = force / new Complex(kA - omega * omega * mA, omega * betaB * kA);
            Assert.NotEqual(wrongA.Imaginary, uA.Imaginary);
        }
    }

    // ---- acceptance and refusals ------------------------------------------------------

    [Fact]
    public void AFreeBodyAnswersWithItsRigidInertiaResponse()
    {
        // Unrestrained, driven well below the first elastic mode: the body responds as a
        // rigid mass, |u| = F/(omega²·m_total), 180 degrees out of phase. The direct solve
        // legitimately answers this where the modal route refuses (its rigid mode's
        // near-zero eigenvalue makes the term unbounded as the frequency FALLS, which is a
        // statement about plotting, not about a single frequency) — pinned so the static
        // solver's refusal is never copied across.
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(20, 20, 20), 1, 1, 1);
        var mesh = AnalysisMesh.Of(tets);
        var model = new StructuralModel(mesh, Materials.Steel);
        const double force = 2.0;
        model.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(force, 0, 0));

        double totalMass = Materials.Steel.Density * 20.0 * 20.0 * 20.0;
        const double hertz = 20.0; // far below the first elastic mode of a 20 mm steel cube
        var response = DirectHarmonicSolver.Solve(model, new DirectHarmonicOptions
        {
            Frequencies = [hertz],
        });

        double omega = 2.0 * Math.PI * hertz;
        double expected = force / (omega * omega * totalMass);
        var probe = response.ResponseAt(0, 0)[0];
        output.WriteLine($"|u| {probe.Magnitude:E8} against rigid {expected:E8}");
        AssertRelative(expected, probe.Magnitude, 1e-4);
        Assert.True(probe.Real < 0, "inertia-dominated response is out of phase with the drive");
    }

    [Fact]
    public void AnUndampedModelDrivenExactlyAtItsResonanceRefusesByName()
    {
        // The refusal fires on an EXACTLY zero pivot, so the fixture must make the 1x1
        // system's real part compute to exactly 0.0 through the solver's OWN expression
        // chain: the caller passes HERTZ, the solver computes omega = fl(2·pi·hertz) and
        // Combine adds fl(1·k) + fl(fl((-omega)·omega)·m) with k, m the ASSEMBLED matrix
        // entries. Two replication traps cost the first two drafts of this test: the
        // measured stiffness (probe/deflection) is ulps away from the assembled entry,
        // and an omega found exactly does NOT survive the hertz round-trip (one ulp of
        // hertz moves the pivot by ~5-9 ulps of k, so a single fixture can step over the
        // zero). So the scan is over HERTZ, through the verbatim chain, across a family
        // of fixture sizes — each size a fresh (k, m) pair, so one of them puts an exact
        // zero on the achievable lattice. Deterministic: the same build always finds the
        // same hit.
        StructuralModel? model = null;
        double resonantHertz = double.NaN;
        for (int size = 0; size < 16 && double.IsNaN(resonantHertz); size++)
        {
            double side = 20.0 + 0.5 * size;
            var tets = StructuredTetMesh.Box(
                Vector3d.Zero, new Vector3d(side, side, side), 1, 1, 1);
            var mesh = AnalysisMesh.Of(tets);
            var candidateModel = new StructuralModel(mesh, Materials.Steel);
            int freeNode = NearestNode(mesh, new Vector3d(side, side, side));
            for (int node = 0; node < mesh.NodeCount; node++)
                candidateModel.FixNode(node, node == freeNode ? Dof.Y | Dof.Z : Dof.All);
            candidateModel.NodalForce(freeNode, new Vector3d(1, 0, 0));

            var reduced = FeaAssembly.ReducedIndices(candidateModel, out int freeCount);
            Assert.Equal(1, freeCount);
            var kMatrix = FeaAssembly.Reduce(
                FeaAssembly.Stiffness(candidateModel, TetQuadrature.For(mesh.Order)), reduced, 1);
            var (fullMass, _) = FeaAssembly.Mass(
                candidateModel, TetQuadrature.ForMass(mesh.Order));
            var mMatrix = FeaAssembly.Reduce(fullMass, reduced, 1);
            double k = Entry(kMatrix, 0, 0);
            double m = Entry(mMatrix, 0, 0);

            double hertz = Math.Sqrt(k / m) / (2.0 * Math.PI);
            for (int i = 0; i < 1 << 10; i++)
                hertz = Math.BitDecrement(hertz);
            for (int i = 0; i < 1 << 11; i++)
            {
                double omega = 2.0 * Math.PI * hertz;
                if (1.0 * k + -omega * omega * m == 0.0)
                {
                    resonantHertz = hertz;
                    model = candidateModel;
                    break;
                }
                hertz = Math.BitIncrement(hertz);
            }
        }
        Assert.False(double.IsNaN(resonantHertz),
            "no fixture in the family put an exactly zero pivot on the achievable "
            + "frequency lattice; widen the scan or the family");

        var ex = Assert.Throws<FeaException>(() => DirectHarmonicSolver.Solve(
            model!, new DirectHarmonicOptions { Frequencies = [resonantHertz] }));
        output.WriteLine(ex.Message);
        Assert.Contains("no steady state", ex.Message);
        Assert.Contains("damping", ex.Message);
    }

    [Fact]
    public void ThePrescribedOffsetAndTheModalAndTransientRoutesRefuseByName()
    {
        // A prescribed non-zero support offset is refused by the direct solve.
        var offsetModel = TransientFixtures.SingleDof(out int free);
        offsetModel.NodalForce(free, new Vector3d(1, 0, 0));
        offsetModel.PrescribeNode(0, new Vector3d(0.1, 0, 0), Dof.X);
        var ex1 = Assert.Throws<FeaException>(() => DirectHarmonicSolver.Solve(
            offsetModel, new DirectHarmonicOptions { Frequencies = [100.0] }));
        Assert.Contains("base excitation", ex1.Message);

        // A model carrying its own damping is refused by the MODAL harmonic route (it
        // cannot integrate a model-carried C, and two damping statements would conflict)…
        var damped = TransientFixtures.SingleDof(out free);
        damped.NodalForce(free, new Vector3d(1, 0, 0));
        damped.Dashpot(free, new Vector3d(1, 0, 0), 0.01);
        var modes = ModalSolver.Solve(damped, new ModalSolveOptions { ModeCount = 1 });
        var ex2 = Assert.Throws<FeaException>(() => HarmonicSolver.Solve(modes,
            new HarmonicSolveOptions
            {
                Frequencies = [100.0],
                Damping = ModalDamping.Uniform(0.02),
            }));
        Assert.Contains("DirectHarmonicSolver", ex2.Message);

        // …and by the transient, which would silently ignore it.
        var ex3 = Assert.Throws<FeaException>(() => TransientSolver.Solve(
            damped, new TransientSolveOptions(1e-6, 2)));
        Assert.Contains("DirectHarmonicSolver", ex3.Message);
    }

    [Fact]
    public void DashpotVocabularyRefusesTheMeaninglessSpellings()
    {
        var model = TransientFixtures.SingleDof(out int free);

        // Negative and zero coefficients add energy / say nothing.
        Assert.Throws<FeaException>(() => model.Dashpot(free, new Vector3d(1, 0, 0), -1.0));
        Assert.Throws<FeaException>(() => model.Dashpot(free, new Vector3d(1, 0, 0), 0.0));
        // A zero axis has no direction to resist along.
        Assert.Throws<FeaException>(() => model.Dashpot(free, Vector3d.Zero, 1.0));
        // A dashpot from a node to itself resists nothing.
        Assert.Throws<FeaException>(() => model.Dashpot(free, free, new Vector3d(1, 0, 0), 1.0));

        // An untouched model reports no damping; each vocabulary item flips the flags it
        // should and no other.
        Assert.False(model.HasDamping);
        Assert.False(model.HasNonProportionalDamping);
        model.SetDamping(new RayleighDamping(1.0, 1e-6));
        Assert.True(model.HasDamping);
        Assert.False(model.HasNonProportionalDamping);
        model.Dashpot(free, new Vector3d(1, 0, 0), 0.5);
        Assert.True(model.HasNonProportionalDamping);
    }

    [Fact]
    public void EqualPerRegionValuesAreBitIdenticalToTheUniformStatement()
    {
        // One assembly path serves both spellings, so "every region states the same
        // value" and "the default states it once" must be the same C to the BIT — which
        // is what makes the per-region feature safe to reach for.
        var model1 = TransientFixtures.SingleDof(out int free);
        model1.NodalForce(free, new Vector3d(1, 0, 0));
        model1.SetDamping(new RayleighDamping(2.0, 3e-6));

        var model2 = TransientFixtures.SingleDof(out _);
        model2.NodalForce(free, new Vector3d(1, 0, 0));
        model2.SetDamping(0, new RayleighDamping(2.0, 3e-6));

        double[] sweep = [500.0, 5000.0];
        var r1 = DirectHarmonicSolver.Solve(model1, new DirectHarmonicOptions { Frequencies = sweep });
        var r2 = DirectHarmonicSolver.Solve(model2, new DirectHarmonicOptions { Frequencies = sweep });

        for (int i = 0; i < sweep.Length; i++)
        {
            var a = r1.ResponseAt(free, 0)[i];
            var b = r2.ResponseAt(free, 0)[i];
            Assert.Equal(a.Real, b.Real);           // bit-exact
            Assert.Equal(a.Imaginary, b.Imaginary); // bit-exact
        }
    }

    // ---- helpers ---------------------------------------------------------------------

    private static void AssertRelative(double expected, double actual, double tolerance)
    {
        double scale = Math.Max(Math.Abs(expected), Math.Abs(actual));
        if (scale == 0)
        {
            Assert.Equal(expected, actual);
            return;
        }
        double relative = Math.Abs(expected - actual) / scale;
        Assert.True(relative <= tolerance,
            $"expected {expected:G17}, got {actual:G17} (relative {relative:E2} > {tolerance:E1})");
    }

    private static int NearestNode(AnalysisMesh mesh, Vector3d target)
    {
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int v = 0; v < mesh.NodeCount; v++)
        {
            double d = mesh.Position(v).DistanceTo(target);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = v;
            }
        }
        return best;
    }

    private static bool ShareAnElement(AnalysisMesh mesh, int a, int b)
    {
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            bool hasA = false, hasB = false;
            foreach (int node in mesh.Element(e))
            {
                hasA |= node == a;
                hasB |= node == b;
            }
            if (hasA && hasB)
                return true;
        }
        return false;
    }

    private static double Entry(PackedSparseMatrix matrix, int row, int column)
    {
        // Symmetric-upper storage: ask the upper triangle.
        if (row > column)
            (row, column) = (column, row);
        var columns = matrix.RowColumns(row);
        var values = matrix.RowValues(row);
        for (int i = 0; i < columns.Length; i++)
        {
            if (columns[i] == column)
                return values[i];
        }
        return 0.0;
    }
}
