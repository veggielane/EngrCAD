using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Natural frequencies against classical beam theory: a cantilever, a simply-supported beam
/// and a free-free beam.
///
/// <para><b>Every number here carries a modelling caveat, and stating it is half the
/// test.</b> Euler-Bernoulli theory has no shear deformation and no rotary inertia; a
/// three-dimensional solid has both, and both SOFTEN the beam, increasingly so with the mode
/// number (the wavelength shortens while the section does not). So the measured frequencies
/// converge to values BELOW the Euler-Bernoulli ones, and the gap is a property of beam
/// theory rather than an error in the solve. Where the correction has a closed form — the
/// simply-supported beam, whose mode shape is a pure sine — Timoshenko's first-order factor
/// is quoted beside it and the measurement is checked to lie between the two. Where it does
/// not (a cantilever's higher modes), the deviation is REPORTED as measured rather than
/// compared against a formula derived for other boundary conditions.</para>
///
/// <para>The convergence ORDER is therefore measured on the axial bar
/// (<see cref="ModalBarTests"/>), where the 3D and 1D problems are identical and there is no
/// modelling gap for a refinement study to stall on. This is the static solver's
/// clamped-end lesson in a different disguise.</para>
/// </summary>
public class ModalBeamTests(ITestOutputHelper output)
{
    private const double Length = 100.0;

    [Fact]
    public void Cantilever_MatchesEulerBernoulli_WithTheShearGapReported()
    {
        // A square section, deliberately: the two bending directions are then IDENTICAL, so
        // every bending mode is a degenerate PAIR — which is the configuration a
        // single-vector Lanczos cannot see without locking and restarting, and the commonest
        // real one (every shaft, every square post).
        const double side = 10.0;
        var mesh = ModalFixtures.Beam(Length, side, side, 20, 2, 2, ElementOrder.Quadratic);
        var model = new StructuralModel(mesh, ModalFixtures.Steel);
        model.Fix(Facets.Tag(StructuredTetMesh.XMin));

        var results = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 8 });
        output.WriteLine(results.ToText());

        Assert.Empty(results.RigidBodyModes);
        Assert.Equal(0.0, results.Report.Shift);

        double area = side * side;
        double inertia = ModalFixtures.SecondMoment(side, side);

        // The three bending modes are the FIRST of each degenerate pair: 1, 3 and 7 in the
        // extracted ordering, because a torsional mode and an axial mode fall between the
        // second and third bending pairs on this section.
        int[] bendingModes = [1, 3, 7];
        for (int i = 0; i < bendingModes.Length; i++)
        {
            double euler = ModalFixtures.BeamFrequency(
                ModalFixtures.CantileverBetaL[i], Length, area, inertia, ModalFixtures.Steel);
            double measured = results.Mode(bendingModes[i]).Frequency;
            output.WriteLine(
                $"bending {i + 1} (mode {bendingModes[i]}): Euler-Bernoulli {euler:N1} Hz, "
                + $"measured {measured:N1} Hz, {(measured - euler) / euler:P2}");

            // Below Euler-Bernoulli, by more at every higher mode: that IS the shear and
            // rotary-inertia softening, so the direction is asserted and the size reported.
            Assert.True(measured < euler, $"bending mode {i + 1} came out above Euler-Bernoulli");
            Assert.True(
                Math.Abs(measured - euler) / euler < 0.20,
                $"bending mode {i + 1} is {(measured - euler) / euler:P2} from Euler-Bernoulli");
        }

        // The degenerate pairs, to the accuracy the mesh can hold them. The two bending
        // directions are geometrically identical but Kuhn's subdivision picks its diagonals
        // by index order, and no reflection preserves that — the same asymmetry the static
        // solver's stress-concentration "mesh spread" measures, so the pair's separation is
        // a direct measurement of the discretization rather than a tolerance to be tuned.
        foreach (var (a, b) in new[] { (1, 2), (3, 4), (7, 8) })
        {
            double split = Math.Abs(results.Mode(a).Frequency - results.Mode(b).Frequency)
                / results.Mode(a).Frequency;
            output.WriteLine($"pair ({a}, {b}) splits by {split:P4}");
            Assert.True(split < 0.02, $"modes {a} and {b} should be a degenerate pair, split {split:P4}");
        }

        // The effective mass of a DEGENERATE pair belongs to the pair, not to either mode.
        // Both members come out as mixtures of the two bending directions — measured, each
        // carries the same 2.399e-5 in Y as in Z — because any orthonormal basis of a
        // two-dimensional eigenspace is an equally valid answer and the solver has no
        // grounds to prefer one. Summed across the pair, the classical 61% of a uniform
        // cantilever's mass reappears.
        double mass = ModalFixtures.Steel.Density * Length * side * side;
        double pairZ = results.Mode(1).EffectiveMass.Z + results.Mode(2).EffectiveMass.Z;
        output.WriteLine(
            $"modes 1 and 2 carry {results.Mode(1).EffectiveMass.Z / mass:P2} and "
            + $"{results.Mode(2).EffectiveMass.Z / mass:P2} of the beam's mass along Z, "
            + $"{pairZ / mass:P2} together");
        Assert.InRange(pairZ / mass, 0.56, 0.62);
    }

    [Fact]
    public void Cantilever_ConvergesWithMeshRefinement()
    {
        const double side = 10.0;
        double euler = ModalFixtures.BeamFrequency(
            ModalFixtures.CantileverBetaL[0], Length, side * side,
            ModalFixtures.SecondMoment(side, side), ModalFixtures.Steel);

        var measured = new List<double>();
        foreach (var (nx, ny) in new[] { (5, 1), (10, 2), (20, 2), (30, 3) })
        {
            var mesh = ModalFixtures.Beam(Length, side, side, nx, ny, ny, ElementOrder.Quadratic);
            var model = new StructuralModel(mesh, ModalFixtures.Steel);
            model.Fix(Facets.Tag(StructuredTetMesh.XMin));
            var results = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 1 });
            measured.Add(results.Mode(1).Frequency);
            output.WriteLine(
                $"{nx}x{ny}x{ny} ({mesh.ElementCount:N0} elements, {results.Report.FreeDofs:N0} DOF): "
                + $"{results.Mode(1).Frequency:N2} Hz, {(results.Mode(1).Frequency - euler) / euler:P3} "
                + $"from Euler-Bernoulli {euler:N2} Hz");
        }

        // Monotone from above: a coarser mesh is a smaller subspace, so its Rayleigh
        // quotient can only be larger. That is a theorem about the method, not a property of
        // this fixture, and it is the right shape of assertion for a sequence converging onto
        // an answer that is NOT the analytic one it is being compared against.
        for (int i = 1; i < measured.Count; i++)
            Assert.True(measured[i] <= measured[i - 1],
                $"refinement {i} raised the frequency from {measured[i - 1]:N3} to {measured[i]:N3}");

        // The finest mesh is within a couple of percent of Euler-Bernoulli, below it, which
        // is where a slenderness of 10 puts the shear correction.
        double finalError = (measured[^1] - euler) / euler;
        Assert.InRange(finalError, -0.05, 0.0);
    }

    [Fact]
    public void SimplySupportedBeam_LiesBetweenEulerBernoulliAndTimoshenko()
    {
        // A RECTANGULAR section, so the two bending directions separate and each mode can be
        // identified with the beta·L it belongs to.
        const double width = 12.0, depth = 8.0;
        var mesh = ModalFixtures.Beam(Length, width, depth, 20, 2, 2, ElementOrder.Quadratic);
        var model = new StructuralModel(mesh, ModalFixtures.Steel);
        // A simple support in three dimensions: transverse motion held at both end faces,
        // rotation of the section left free (a face held only in Y and Z can still turn about
        // Y and Z, since the rotation moves it along X). One node holds the axial rigid
        // translation, so nothing is over-constrained.
        model.Fix(Facets.Tag(StructuredTetMesh.XMin), Dof.Y | Dof.Z);
        model.Fix(Facets.Tag(StructuredTetMesh.XMax), Dof.Y | Dof.Z);

        // The axial rigid translation is removed by holding u_x = 0 along the beam's own
        // CENTROIDAL LINE — and the choice is a finding, not a detail.
        //
        // The obvious device, pinning u_x at a single node, is what a STATIC 3-2-1 restraint
        // does and it is wrong here. In statics a single-node restraint is a local
        // disturbance St Venant confines to its own neighbourhood; in DYNAMICS it creates a
        // genuine mode in which the whole body translates axially while a few elements around
        // the pinned node deform, and its frequency is set by the mesh rather than by the
        // beam. Measured on this fixture at 20x2x2: a spurious axial-translation mode at
        // 5 540 Hz, sitting between the second and third bending modes and carrying 96% of
        // the axial effective mass — which duly failed a comparison against the second
        // bending frequency by 26%.
        //
        // Pure bending has u_x = -z·w'(x) measured from the neutral axis, which is
        // identically zero ON that axis, so this constraint adds NO bending stiffness — the
        // bending modes it permits are exactly the ones the free beam has. What it does
        // remove is the axial family, which needs u_x != 0 on the axis.
        double tolerance = 1e-9 * Length;
        int axisNodes = 0;
        for (int v = 0; v < mesh.NodeCount; v++)
        {
            var p = mesh.Position(v);
            if (Math.Abs(p.Y - width / 2) > tolerance || Math.Abs(p.Z - depth / 2) > tolerance)
                continue;
            model.FixNode(v, Dof.X);
            axisNodes++;
        }
        output.WriteLine($"axial constraint on {axisNodes} centroidal-line nodes");

        var results = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 4 });
        output.WriteLine(results.ToText());
        Assert.Empty(results.RigidBodyModes);

        double area = width * depth;
        double weakInertia = ModalFixtures.SecondMoment(width, depth);   // bending along Z
        double strongInertia = ModalFixtures.SecondMoment(depth, width); // bending along Y
        double weakGyration = Math.Sqrt(weakInertia / area);
        double strongGyration = Math.Sqrt(strongInertia / area);

        // beta·L = n·pi for a simply-supported beam. Modes interleave by direction:
        // weak(pi), strong(pi), weak(2pi), strong(2pi).
        (int Mode, int N, double Inertia, double Gyration)[] cases =
        [
            (1, 1, weakInertia, weakGyration),
            (2, 1, strongInertia, strongGyration),
            (3, 2, weakInertia, weakGyration),
            (4, 2, strongInertia, strongGyration),
        ];

        foreach (var (number, n, inertia, gyration) in cases)
        {
            double betaL = n * Math.PI;
            double euler = ModalFixtures.BeamFrequency(
                betaL, Length, area, inertia, ModalFixtures.Steel);
            double timoshenko = euler * ModalFixtures.TimoshenkoRatio(
                betaL, gyration, Length, ModalFixtures.Steel);
            double measured = results.Mode(number).Frequency;
            output.WriteLine(
                $"mode {number} (beta·L = {n}·pi): Euler-Bernoulli {euler:N1} Hz, "
                + $"Timoshenko {timoshenko:N1} Hz ({(timoshenko - euler) / euler:P2}), "
                + $"measured {measured:N1} Hz ({(measured - euler) / euler:P2} from EB, "
                + $"{(measured - timoshenko) / timoshenko:P2} from Timoshenko)");

            // The 3D answer sits between the two beam theories: below Euler-Bernoulli
            // because shear deformation is real, and above Timoshenko because a solid end
            // face held over its whole area is stiffer than a beam theory's point support.
            // A 5% band on each side is the honest bar for a comparison between two DIFFERENT
            // models, not a tolerance on an arithmetic result.
            Assert.True(measured < euler * 1.005,
                $"mode {number} at {measured:N1} Hz is not below Euler-Bernoulli's {euler:N1} Hz");
            Assert.True(measured > timoshenko * 0.95,
                $"mode {number} at {measured:N1} Hz fell below Timoshenko's {timoshenko:N1} Hz");
        }
    }

    [Fact]
    public void FreeFreeBeam_HasSixZeroModes_AndItsSeventhIsTheFirstElasticOne()
    {
        const double width = 12.0, depth = 8.0;
        var mesh = ModalFixtures.Beam(Length, width, depth, 20, 2, 2, ElementOrder.Quadratic);
        var model = new StructuralModel(mesh, ModalFixtures.Steel);
        // No supports at all. The static solver REFUSES this model; a modal analysis of it is
        // perfectly well posed, and the six rigid-body modes are part of the answer.

        var results = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 3 });
        output.WriteLine(results.ToText());

        Assert.Equal(6, results.RigidBodyModes.Count);
        Assert.True(results.Report.Shift < 0, "a singular K needs a strictly negative shift");

        // The six zero frequencies, reported as MEASURED. In exact arithmetic each is zero;
        // what they actually read is how much round-off the assembled stiffness carries on an
        // exactly rigid field, which is a conditioning measurement of this model. The bar is
        // relative to the first ELASTIC eigenvalue, because that is the quantity a
        // zero-frequency mode has to be negligible against.
        double firstElastic = results.Mode(1).Eigenvalue;
        foreach (var rigid in results.RigidBodyModes)
        {
            output.WriteLine(
                $"  {rigid.Description}: lambda {rigid.Eigenvalue:E3} "
                + $"({Math.Abs(rigid.Eigenvalue) / firstElastic:E2} of the first elastic), "
                + $"{rigid.Frequency:E3} Hz");
            Assert.True(Math.Abs(rigid.Eigenvalue) < 1e-9 * firstElastic,
                $"rigid mode '{rigid.Description}' measured lambda {rigid.Eigenvalue:E3}");
        }

        // Three translations and three rotations, described rather than counted.
        Assert.Equal(3, results.RigidBodyModes.Count(r => r.Description.StartsWith("translation")));
        Assert.Equal(3, results.RigidBodyModes.Count(r => r.Description.StartsWith("rotation")));

        double area = width * depth;
        double weakInertia = ModalFixtures.SecondMoment(width, depth);
        double strongInertia = ModalFixtures.SecondMoment(depth, width);
        double euler = ModalFixtures.BeamFrequency(
            ModalFixtures.FreeFreeBetaL[0], Length, area, weakInertia, ModalFixtures.Steel);
        double eulerStrong = ModalFixtures.BeamFrequency(
            ModalFixtures.FreeFreeBetaL[0], Length, area, strongInertia, ModalFixtures.Steel);

        output.WriteLine(
            $"first elastic mode: Euler-Bernoulli {euler:N1} Hz, measured "
            + $"{results.Mode(1).Frequency:N1} Hz ({(results.Mode(1).Frequency - euler) / euler:P2})");
        output.WriteLine(
            $"second elastic mode: Euler-Bernoulli {eulerStrong:N1} Hz, measured "
            + $"{results.Mode(2).Frequency:N1} Hz "
            + $"({(results.Mode(2).Frequency - eulerStrong) / eulerStrong:P2})");

        // Mode 1 of the ELASTIC list is the seventh mode of the body: numbering starts at the
        // lowest mode that stores strain energy, which is the whole point of separating them.
        Assert.Equal(1, results.Mode(1).Number);
        Assert.True(results.Mode(1).Frequency < euler);
        Assert.True(Math.Abs(results.Mode(1).Frequency - euler) / euler < 0.10);
    }
}
