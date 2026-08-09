using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Several transient load patterns with independent histories — gravity held while a shaker
/// runs, <c>f(t) = sum_i g_i(t)·f_i</c> — verified by SUPERPOSITION.
///
/// <para><b>The oracle is linearity itself.</b> A linear system started from rest responds to a
/// sum of loads with the sum of the responses, exactly. So the two-pattern run must equal, at
/// every stored step, the two single-pattern runs added together — a bug that dropped a pattern,
/// mis-scaled one, or evaluated a law at the wrong instant could not survive it. And the
/// single-pattern list is asserted bit-identical to the single-model form, which is the safety
/// statement for touching the transient's load path at all.</para>
/// </summary>
public sealed class TransientLoadPatternTests(ITestOutputHelper output)
{
    /// <summary>One shared mesh, and three models over it — a clean operator plus two that carry
    /// one load each — so they share one factorization.</summary>
    private static (int Free, StructuralModel Op, StructuralModel A, StructuralModel B, double Omega)
        Shared(Vector3d forceA, Vector3d forceB)
    {
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(20, 20, 20), 1, 1, 1);
        var mesh = AnalysisMesh.Of(tets);
        int free = 0;
        double best = double.MinValue;
        for (int v = 0; v < mesh.NodeCount; v++)
        {
            var p = mesh.Position(v);
            if (p.X + p.Y + p.Z > best) { best = p.X + p.Y + p.Z; free = v; }
        }
        StructuralModel Make()
        {
            var m = new StructuralModel(mesh, Materials.Steel);
            for (int node = 0; node < mesh.NodeCount; node++)
                m.FixNode(node, node == free ? Dof.Y | Dof.Z : Dof.All);
            return m;
        }
        var op = Make();
        var a = Make(); a.NodalForce(free, forceA);
        var b = Make(); b.NodalForce(free, forceB);
        double omega = ModalSolver.Solve(op, new ModalSolveOptions { ModeCount = 1 }).Mode(1).AngularFrequency;
        return (free, op, a, b, omega);
    }

    [Fact]
    public void TwoPatternsSuperpose_TheSumOfTheSingleRuns()
    {
        var (free, op, a, b, omega) = Shared(new Vector3d(800, 0, 0), new Vector3d(-500, 0, 0));
        double period = 2 * Math.PI / omega;
        double dt = period / 100;
        int steps = 100 * 6;

        Func<double, double> gA = _ => 1.0;                 // gravity-like, held
        Func<double, double> gB = t => Math.Sin(0.7 * omega * t);  // a shaker
        var damping = RayleighDamping.MassProportional(omega / (2 * Math.PI), 0.03);

        var both = TransientSolver.Solve(op, new TransientSolveOptions(dt, steps)
        {
            Damping = damping,
            LoadPatterns = [new TransientLoadPattern(a, gA), new TransientLoadPattern(b, gB)],
        });
        var onlyA = TransientSolver.Solve(a, new TransientSolveOptions(dt, steps)
        {
            Damping = damping,
            LoadFactor = gA,
        });
        var onlyB = TransientSolver.Solve(b, new TransientSolveOptions(dt, steps)
        {
            Damping = damping,
            LoadFactor = gB,
        });

        Assert.Equal(both.States.Count, onlyA.States.Count);
        double peak = 0, worst = 0;
        for (int s = 0; s < both.States.Count; s++)
        {
            double sum = onlyA.States[s].DisplacementAt(free).X + onlyB.States[s].DisplacementAt(free).X;
            double ab = both.States[s].DisplacementAt(free).X;
            peak = Math.Max(peak, Math.Abs(ab));
            worst = Math.Max(worst, Math.Abs(ab - sum));
        }
        output.WriteLine($"superposition: worst |both - (A+B)| {worst:E3}, peak {peak:E3}, relative {worst / peak:E3}");
        Assert.True(worst / peak < 1e-11, $"superposition failed at {worst / peak:E3}");
    }

    [Fact]
    public void OnePatternListEqualsTheSingleModelForm_BitForBit()
    {
        // Solve(op, LoadPatterns=[(a, g)]) IS Solve(a, LoadFactor=g) — ComputeLoad reduces to
        // Scale for one pattern, so the arithmetic is byte-identical.
        var (free, op, a, _, omega) = Shared(new Vector3d(1000, 0, 0), Vector3d.Zero);
        double dt = 2 * Math.PI / omega / 80;
        Func<double, double> g = t => 1.0 - Math.Cos(0.5 * omega * t);

        var viaModel = TransientSolver.Solve(a, new TransientSolveOptions(dt, 200) { LoadFactor = g });
        var viaList = TransientSolver.Solve(op, new TransientSolveOptions(dt, 200)
        {
            LoadPatterns = [new TransientLoadPattern(a, g)],
        });

        Assert.Equal(viaModel.States.Count, viaList.States.Count);
        for (int s = 0; s < viaModel.States.Count; s++)
        {
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(viaModel.States[s].DisplacementAt(free).X),
                BitConverter.DoubleToInt64Bits(viaList.States[s].DisplacementAt(free).X));
        }
    }

    [Fact]
    public void TheRefusalsFireByName()
    {
        var (_, op, a, b, _) = Shared(new Vector3d(1, 0, 0), new Vector3d(1, 0, 0));

        // LoadFactor and LoadPatterns cannot both be set.
        var both = Assert.Throws<FeaException>(() => TransientSolver.Solve(op,
            new TransientSolveOptions(1e-4, 2)
            {
                LoadFactor = _ => 1.0,
                LoadPatterns = [new TransientLoadPattern(a, _ => 1.0)],
            }));
        Assert.Contains("law per", both.Message);

        // The solve model must be clean when patterns are given.
        var dirty = Assert.Throws<FeaException>(() => TransientSolver.Solve(a,
            new TransientSolveOptions(1e-4, 2)
            {
                LoadPatterns = [new TransientLoadPattern(b, _ => 1.0)],
            }));
        Assert.Contains("provides only the operator", dirty.Message);

        // Patterns on a different mesh instance cannot share a factorization.
        var (_, op2, a2, _, _) = Shared(new Vector3d(1, 0, 0), Vector3d.Zero);
        var otherMesh = Assert.Throws<FeaException>(() => TransientSolver.Solve(op,
            new TransientSolveOptions(1e-4, 2)
            {
                LoadPatterns = [new TransientLoadPattern(a2, _ => 1.0)],
            }));
        Assert.Contains("different AnalysisMesh", otherMesh.Message);
        _ = op2;
    }
}
