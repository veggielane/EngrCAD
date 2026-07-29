using EngrCAD.Core;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Fixtures for transient verification, and the closed forms they are checked against.
///
/// <para><b>The single-degree-of-freedom oscillator is the fixture that matters here, and it
/// is built by restraining every degree of freedom but one.</b> That is exactly the idiom
/// this project's modal work recorded as WRONG in dynamics — a single-node restraint creates
/// a genuine spurious mode whose frequency belongs to the mesh rather than to the part — and
/// the distinction is worth stating because it is not a loophole. That warning is about
/// MODELLING: a restraint meant to remove rigid motion from a real part must be one the
/// wanted modes satisfy identically, or it changes what is being measured. Here nothing is
/// being modelled. The reduced system is deliberately made <c>1 x 1</c> so that
/// <c>m·q'' + c·q' + k·q = f(t)</c> is not an approximation of the finite element problem
/// but IS the finite element problem, which is the only way to check a time integrator
/// against a closed form with no discretization error in the way at all. Every mode this
/// fixture has is the one being measured.</para>
///
/// <para>The companion fixture is the other direction: <see cref="ModalFixtures.AxialBar"/>
/// seeded with a single MODE SHAPE, where the response provably stays in that mode
/// (<c>M·phi·(q'' + omega²·q) = 0</c> follows from <c>K·phi = omega²·M·phi</c>), so the same
/// scalar closed forms apply to a real multi-thousand-degree-of-freedom system and the two
/// solvers can be made to check each other.</para>
/// </summary>
internal static class TransientFixtures
{
    /// <summary>
    /// A model whose reduced system is exactly <c>1 x 1</c>: one node free along X, every
    /// other degree of freedom held.
    /// </summary>
    /// <param name="freeNode">The node left free along X.</param>
    /// <param name="material">The material (default <c>Materials.Steel</c>).</param>
    public static StructuralModel SingleDof(out int freeNode, Material? material = null)
    {
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(20, 20, 20), 1, 1, 1);
        var mesh = AnalysisMesh.Of(tets);
        var model = new StructuralModel(mesh, material ?? Materials.Steel);

        // The node at the far corner, chosen by position so the fixture does not depend on
        // the mesher's node ordering.
        freeNode = 0;
        double best = double.MinValue;
        for (int node = 0; node < mesh.NodeCount; node++)
        {
            double reach = mesh.Position(node).X + mesh.Position(node).Y + mesh.Position(node).Z;
            if (reach > best)
            {
                best = reach;
                freeNode = node;
            }
        }

        for (int node = 0; node < mesh.NodeCount; node++)
            model.FixNode(node, node == freeNode ? Dof.Y | Dof.Z : Dof.All);
        return model;
    }

    /// <summary>
    /// The oscillator's stiffness and mass, measured through two OTHER solvers so that the
    /// closed forms the transient is checked against do not come from the transient.
    ///
    /// <para><c>k</c> comes from a static solve — apply a unit force and read the deflection —
    /// and <c>omega</c> from a modal solve, so <c>m = k/omega²</c> follows. Both are solvers
    /// with their own verification suites; nothing here reads the time integrator's own
    /// arithmetic.</para>
    /// </summary>
    public static (double Stiffness, double Mass, double AngularFrequency) Properties(
        StructuralModel model, int freeNode)
    {
        const double probe = 1.0;
        var statics = StructuralSolver.Solve(
            new StructuralModel(model.Mesh, model.DefaultMaterial)
                .Also(m => Restrain(m, model))
                .NodalForce(freeNode, new Vector3d(probe, 0, 0)));
        double stiffness = probe / statics.DisplacementAt(freeNode).X;

        var modal = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 1 });
        double omega = modal.Mode(1).AngularFrequency;
        return (stiffness, stiffness / (omega * omega), omega);
    }

    private static void Restrain(StructuralModel target, StructuralModel source)
    {
        for (int node = 0; node < source.Mesh.NodeCount; node++)
        {
            var restraint = source.RestraintOf(node);
            if (restraint != Dof.None)
                target.FixNode(node, restraint);
        }
    }

    /// <summary>A tiny fluent helper so a model can be restrained inline.</summary>
    public static StructuralModel Also(this StructuralModel model, Action<StructuralModel> act)
    {
        act(model);
        return model;
    }

    /// <summary>The undamped free response to an initial displacement,
    /// <c>u0·cos(omega·t)</c>.</summary>
    public static double FreeVibration(double u0, double omega, double time) =>
        u0 * Math.Cos(omega * time);

    /// <summary>The undamped response to an initial velocity,
    /// <c>(v0/omega)·sin(omega·t)</c>.</summary>
    public static double ImpulseResponse(double v0, double omega, double time) =>
        v0 / omega * Math.Sin(omega * time);

    /// <summary>
    /// The response to a step load switched on at <c>t = 0</c>,
    /// <c>u_static·(1 - cos(omega·t))</c> — whose peak is exactly <b>twice</b> the static
    /// deflection, the classic dynamic amplification factor of 2.
    /// </summary>
    public static double StepResponse(double staticDeflection, double omega, double time) =>
        staticDeflection * (1.0 - Math.Cos(omega * time));

    /// <summary>
    /// The damped response to a step load,
    /// <c>u_static·[1 - e^(-zeta·omega·t)·(cos(wd·t) + zeta/sqrt(1-zeta²)·sin(wd·t))]</c>
    /// with <c>wd = omega·sqrt(1 - zeta²)</c>.
    /// </summary>
    public static double DampedStepResponse(
        double staticDeflection, double omega, double zeta, double time)
    {
        double wd = omega * Math.Sqrt(1.0 - zeta * zeta);
        return staticDeflection
            * (1.0 - Math.Exp(-zeta * omega * time)
                * (Math.Cos(wd * time) + zeta / Math.Sqrt(1.0 - zeta * zeta) * Math.Sin(wd * time)));
    }

    /// <summary>
    /// The damped free response to an initial displacement,
    /// <c>u0·e^(-zeta·omega·t)·(cos(wd·t) + zeta/sqrt(1-zeta²)·sin(wd·t))</c>.
    /// </summary>
    public static double DampedFreeVibration(
        double u0, double omega, double zeta, double time)
    {
        double wd = omega * Math.Sqrt(1.0 - zeta * zeta);
        return u0 * Math.Exp(-zeta * omega * time)
            * (Math.Cos(wd * time) + zeta / Math.Sqrt(1.0 - zeta * zeta) * Math.Sin(wd * time));
    }

    /// <summary>The steady-state amplitude of a damped oscillator under
    /// <c>f·sin(drive·t)</c>: <c>u_static/sqrt((1-r²)² + (2·zeta·r)²)</c> with
    /// <c>r = drive/omega</c>.</summary>
    public static double SteadyStateAmplitude(
        double staticDeflection, double omega, double zeta, double drive)
    {
        double r = drive / omega;
        return staticDeflection / Math.Sqrt((1 - r * r) * (1 - r * r) + 4 * zeta * zeta * r * r);
    }

    /// <summary>The measured convergence order between two errors at a 2:1 refinement.</summary>
    public static double Order(double coarseError, double fineError) =>
        Math.Log2(Math.Abs(coarseError) / Math.Abs(fineError));

    /// <summary>The largest absolute value of one node's X displacement over a run.</summary>
    public static double PeakX(TransientResults results, int node)
    {
        double peak = 0;
        foreach (var state in results.States)
            peak = Math.Max(peak, Math.Abs(state.DisplacementAt(node).X));
        return peak;
    }

    /// <summary>The largest absolute difference between one node's X displacement and a
    /// stated exact function of time, over a run.</summary>
    public static double WorstError(
        TransientResults results, int node, Func<double, double> exact)
    {
        double worst = 0;
        foreach (var state in results.States)
            worst = Math.Max(worst, Math.Abs(state.DisplacementAt(node).X - exact(state.Time)));
        return worst;
    }
}
