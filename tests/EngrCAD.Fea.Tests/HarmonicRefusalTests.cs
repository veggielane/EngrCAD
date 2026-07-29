using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>What <see cref="HarmonicSolver"/> refuses. Each of these would otherwise return a
/// plausible sweep computed from something other than what was asked for.</summary>
public class HarmonicRefusalTests(ITestOutputHelper output)
{
    private static StructuralModel Cantilever(bool loaded = true)
    {
        var mesh = ModalFixtures.Beam(80, 12, 8, 6, 1, 1, ElementOrder.Quadratic);
        var model = new StructuralModel(mesh, ModalFixtures.Steel);
        model.Fix(Facets.Tag(StructuredTetMesh.XMin));
        if (loaded)
            model.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(0, 0, -50));
        return model;
    }

    [Fact]
    public void AModelWithNoLoadIsRefused_BecauseTheSweepWouldBeIdenticallyZero()
    {
        var model = Cantilever(loaded: false);
        var modes = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 2 });
        var error = Assert.Throws<FeaException>(() => HarmonicSolver.Solve(
            modes, new HarmonicSolveOptions
            {
                Frequencies = [100.0],
                Damping = ModalDamping.Uniform(0.02),
            }));
        output.WriteLine(error.Message);
        Assert.Contains("no applied force", error.Message);
        Assert.Contains("A modal analysis ignores loads", error.Message);
    }

    [Fact]
    public void AThermalLoadIsRefusedRatherThanSilentlyDropped()
    {
        // A thermal strain enters a static solve as an ELEMENT integral, not as a nodal force,
        // so it cannot be projected onto a mode shape — and the load vector this solver reads
        // would simply not contain it. Accepting the model would compute a correct response to
        // the wrong load.
        var model = Cantilever();
        model.UniformThermalLoad(20.0);
        var modes = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 2 });
        var error = Assert.Throws<FeaException>(() => HarmonicSolver.Solve(
            modes, new HarmonicSolveOptions
            {
                Frequencies = [100.0],
                Damping = ModalDamping.Uniform(0.02),
            }));
        output.WriteLine(error.Message);
        Assert.Contains("thermal load", error.Message);
        Assert.Contains("silently drop it", error.Message);
    }

    [Fact]
    public void AFreeFreeModelIsRefused_BecauseARigidModeHasNoSteadyState()
    {
        var mesh = ModalFixtures.Beam(80, 12, 8, 6, 1, 1, ElementOrder.Quadratic);
        var model = new StructuralModel(mesh, ModalFixtures.Steel);
        model.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(0, 0, -50));
        var modes = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 2 });
        Assert.Equal(6, modes.RigidBodyModes.Count);

        var error = Assert.Throws<FeaException>(() => HarmonicSolver.Solve(
            modes, new HarmonicSolveOptions
            {
                Frequencies = [100.0],
                Damping = ModalDamping.Uniform(0.02),
            }));
        output.WriteLine(error.Message);
        Assert.Contains("rigid-body mode", error.Message);
        Assert.Contains("accelerates away", error.Message);
    }

    [Fact]
    public void AStaticCorrectionFromAnotherModelIsRefused()
    {
        var model = Cantilever();
        var other = Cantilever();
        var modes = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 2 });
        var wrongStatics = StructuralSolver.Solve(other);

        var error = Assert.Throws<FeaException>(() => HarmonicSolver.Solve(
            modes, new HarmonicSolveOptions
            {
                Frequencies = [100.0],
                Damping = ModalDamping.Uniform(0.02),
                StaticCorrection = wrongStatics,
            }));
        output.WriteLine(error.Message);
        Assert.Contains("different StructuralModel instance", error.Message);
        Assert.Contains("subtract the wrong thing", error.Message);
    }

    [Fact]
    public void AnEmptyOrNegativeSweepIsRefused()
    {
        var model = Cantilever();
        var modes = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 1 });
        var damping = ModalDamping.Uniform(0.02);

        Assert.Throws<ArgumentException>(() => HarmonicSolver.Solve(
            modes, new HarmonicSolveOptions { Frequencies = [], Damping = damping }));
        Assert.Throws<ArgumentException>(() => HarmonicSolver.Solve(
            modes, new HarmonicSolveOptions { Frequencies = [10, -5], Damping = damping }));
        Assert.Throws<ArgumentException>(() => HarmonicSolver.Solve(
            modes, new HarmonicSolveOptions { Frequencies = [double.NaN], Damping = damping }));
    }

    [Fact]
    public void OutOfRangeProbesAreNamed()
    {
        var model = Cantilever();
        var modes = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 2 });
        var response = HarmonicSolver.Solve(modes, new HarmonicSolveOptions
        {
            Frequencies = [100.0, 200.0],
            Damping = ModalDamping.Uniform(0.02),
        });

        Assert.Throws<ArgumentOutOfRangeException>(() => response.ModalCoordinate(2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => response.ModalCoordinate(0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => response.ResponseAt(0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => response.ResponseAt(model.Mesh.NodeCount, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => response.AmplitudeAt(5));
    }
}
