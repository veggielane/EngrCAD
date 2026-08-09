using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Base (support) motion driving the transient solver — a shaker or seismic input applied
/// through the supports rather than as a nodal force (<see cref="TransientSolveOptions.BaseMotion"/>).
///
/// <para><b>Three oracles, each a different kind of check.</b> The DELIVERABLE is a closed
/// form: a single-degree-of-freedom oscillator on a harmonically accelerating base has a
/// transmissibility whose steady-state relative amplitude is
/// <c>A / sqrt((omega² - Omega²)² + (2·zeta·omega·Omega)²)</c>, and the relative solver is
/// asserted against it. The two structural checks are that a ZERO base motion reproduces a
/// plain force-driven run bit for bit (so the feature is off-by-default and the load-pattern
/// seam adds nothing when the amplitude is zero), and that the relative formulation the public
/// API uses AGREES to round-off with the absolute formulation (prescribing the support motion)
/// on one problem — two genuinely different integrations landing on the same answer, which is
/// what proves they describe the same physics and justifies keeping the cleaner one
/// (design.md §3g).</para>
/// </summary>
public class TransientBaseMotionTests(ITestOutputHelper output)
{
    /// <summary>
    /// The transmissibility closed form, measured as an AMPLIFICATION so the fixture's
    /// participation factor cancels. A single-degree-of-freedom oscillator on a base
    /// accelerating as <c>a_g·sin(Omega·t)</c> has, in relative coordinates,
    /// <c>m·q'' + c·q' + k·q = -p·a_g·sin(Omega·t)</c> for some reduced forcing coefficient
    /// <c>p</c> (the consistent-mass participation of the one free degree of freedom, not the
    /// reduced mass). Under a CONSTANT base acceleration the response settles to the static
    /// relative deflection <c>p·a_g/k</c>, and under a harmonic one the steady amplitude is that
    /// static deflection times <c>1/sqrt((1-r²)² + (2·zeta·r)²)</c> with <c>r = Omega/omega</c>.
    /// The ratio of the two — the amplification — has <c>p</c> divided out, so it is a clean
    /// closed form that needs nothing about the fixture's mass distribution.
    /// </summary>
    [Fact]
    public void SingleDofOnAMovingBase_MatchesTheTransmissibilityAmplification()
    {
        var model = TransientFixtures.SingleDof(out int node);
        var (_, _, omega) = TransientFixtures.Properties(model, node);

        // Stiffness-proportional Rayleigh damping is CLEAN for base excitation: C·iota_d =
        // beta·K·iota_d = 0 (a rigid translation carries no elastic force), so the relative
        // load is exactly -M·iota_d·a_g with no ground-velocity term to drop. The modal ratio
        // is zeta = beta·omega/2, which StiffnessProportional(f, zeta) supplies.
        const double zeta = 0.05;
        var damping = RayleighDamping.StiffnessProportional(omega / (2 * Math.PI), zeta);

        const double accel = 9810.0;   // 1 g, in mm/s²
        double staticDeflection = SettledBaseDeflection(model, node, omega, damping, accel);

        double drive = 0.7 * omega;    // below resonance: a stable, well-separated response
        double dynamicAmplitude = SteadyBaseAmplitude(model, node, damping, accel, drive, 40);

        var results = SteadyRun(model, damping, accel, drive, 40);
        Assert.True(results.IsRelativeToBase);
        Assert.Equal(new Vector3d(1, 0, 0), results.BaseDirection);

        double measuredAmplification = dynamicAmplitude / Math.Abs(staticDeflection);
        double r = drive / omega;
        double exactAmplification =
            1.0 / Math.Sqrt((1 - r * r) * (1 - r * r) + 4 * zeta * zeta * r * r);

        double error = Math.Abs(measuredAmplification - exactAmplification) / exactAmplification;
        output.WriteLine($"omega = {omega:G6} rad/s, drive = {drive:G6}, zeta = {zeta}");
        output.WriteLine($"static deflection {staticDeflection:G6}, dynamic amplitude "
            + $"{dynamicAmplitude:G6}");
        output.WriteLine($"amplification: measured {measuredAmplification:G6}, exact "
            + $"{exactAmplification:G6}, error {error:P4}");
        Assert.True(error < 5e-3, $"transmissibility amplification error {error:P4}");
    }

    /// <summary>
    /// A resonant drive (<c>Omega = omega</c>) reaches the resonant amplification
    /// <c>1/(2·zeta)</c> — the same closed form at <c>r = 1</c>, and the case a base-excitation
    /// feature exists to get right.
    /// </summary>
    [Fact]
    public void AtResonance_ReachesTheResonantAmplification()
    {
        var model = TransientFixtures.SingleDof(out int node);
        var (_, _, omega) = TransientFixtures.Properties(model, node);

        const double zeta = 0.05;
        var damping = RayleighDamping.StiffnessProportional(omega / (2 * Math.PI), zeta);
        const double accel = 9810.0;

        double staticDeflection = SettledBaseDeflection(model, node, omega, damping, accel);
        // Resonance builds up over ~1/zeta cycles, so run long enough to reach steady state.
        double dynamicAmplitude = SteadyBaseAmplitude(model, node, damping, accel, omega, 60);

        double measuredAmplification = dynamicAmplitude / Math.Abs(staticDeflection);
        double exactAmplification = 1.0 / (2 * zeta);
        double error = Math.Abs(measuredAmplification - exactAmplification) / exactAmplification;
        output.WriteLine($"resonant amplification: measured {measuredAmplification:G6}, exact "
            + $"{exactAmplification:G6}, error {error:P4}");
        Assert.True(error < 1e-2, $"resonant amplification error {error:P4}");
    }

    /// <summary>The settled relative deflection under a CONSTANT base acceleration — the static
    /// base-excitation response, from which the participation coefficient divides out.</summary>
    private static double SettledBaseDeflection(
        StructuralModel model, int node, double omega, RayleighDamping damping, double accel)
    {
        double period = 2 * Math.PI / omega;
        double dt = period / 80;
        int periods = 80;   // ~1/zeta cycles decays the transient away
        int steps = (int)Math.Round(periods * period / dt);
        var results = TransientSolver.Solve(
            model,
            new TransientSolveOptions(dt, steps)
            {
                Damping = damping,
                BaseMotion = new BaseMotion(new Vector3d(1, 0, 0), _ => accel),
            });
        // Average over the last period to remove any residual ripple; the mean IS the static
        // deflection once the transient has decayed.
        double sum = 0;
        int count = 0;
        double windowStart = (periods - 1) * period;
        foreach (var state in results.States)
            if (state.Time >= windowStart)
            {
                sum += state.DisplacementAt(node).X;
                count++;
            }
        return sum / count;
    }

    private static TransientResults SteadyRun(
        StructuralModel model, RayleighDamping damping, double accel, double drive, int periods)
    {
        double drivePeriod = 2 * Math.PI / drive;
        double dt = drivePeriod / 80;
        int steps = (int)Math.Round(periods * drivePeriod / dt);
        return TransientSolver.Solve(
            model,
            new TransientSolveOptions(dt, steps)
            {
                Damping = damping,
                BaseMotion = new BaseMotion(new Vector3d(1, 0, 0), t => accel * Math.Sin(drive * t)),
            });
    }

    /// <summary>The steady-state relative amplitude of the free node under a harmonic base
    /// acceleration, measured over the last five drive periods once the transient has
    /// decayed.</summary>
    private static double SteadyBaseAmplitude(
        StructuralModel model, int node, RayleighDamping damping,
        double accel, double drive, int periods)
    {
        var results = SteadyRun(model, damping, accel, drive, periods);
        double drivePeriod = 2 * Math.PI / drive;
        double windowStart = (periods - 5) * drivePeriod;
        double measured = 0;
        foreach (var state in results.States)
            if (state.Time >= windowStart)
                measured = Math.Max(measured, Math.Abs(state.DisplacementAt(node).X));
        return measured;
    }

    /// <summary>
    /// A ZERO base-motion history reproduces the same run WITHOUT a base motion, bit for bit —
    /// the feature is off-by-default, and the inertial-load pattern scaled by <c>a_g(t) = 0</c>
    /// perturbs nothing. Driven by a real applied force so the run genuinely moves, which makes
    /// the bit-identity a claim rather than <c>0 == 0</c>.
    /// </summary>
    [Fact]
    public void AZeroBaseMotion_IsBitIdenticalToAPlainRun()
    {
        // A step force so the run moves; a zero base motion must not change a single bit.
        static StructuralModel Build(out int node)
        {
            var model = TransientFixtures.SingleDof(out node);
            model.NodalForce(node, new Vector3d(500, 0, 0));
            return model;
        }

        var plainModel = Build(out int node);
        var (_, _, omega) = TransientFixtures.Properties(plainModel, node);
        double dt = (2 * Math.PI / omega) / 50;
        const int steps = 400;

        var plain = TransientSolver.Solve(plainModel, new TransientSolveOptions(dt, steps));

        var withZero = TransientSolver.Solve(
            Build(out _),
            new TransientSolveOptions(dt, steps)
            {
                BaseMotion = new BaseMotion(new Vector3d(1, 0, 0), _ => 0.0),
            });

        Assert.Equal(plain.States.Count, withZero.States.Count);
        long differing = 0;
        for (int i = 0; i < plain.States.Count; i++)
        {
            var a = plain.States[i];
            var b = withZero.States[i];
            for (int n = 0; n < plainModel.Mesh.NodeCount; n++)
            {
                differing += CountBitDifferences(a.DisplacementAt(n), b.DisplacementAt(n));
                differing += CountBitDifferences(a.VelocityAt(n), b.VelocityAt(n));
                differing += CountBitDifferences(a.AccelerationAt(n), b.AccelerationAt(n));
            }
        }
        output.WriteLine($"differing bits across {plain.States.Count} states: {differing}");
        Assert.Equal(0, differing);
        // The zero run is not relative-to-base by the flag, but a base motion was stated.
        Assert.True(withZero.IsRelativeToBase);
    }

    /// <summary>
    /// The RELATIVE formulation (the public inertial-load form) and the ABSOLUTE formulation
    /// (the internal seam that prescribes the support motion) agree to ROUND-OFF on one
    /// problem — the check design.md §3g uses to keep the relative one. They are genuinely
    /// different integrations: one applies <c>-M·iota_d·a_g</c> over fixed supports, the other
    /// prescribes <c>iota_d·u_g(t)</c> at the supports and solves for absolute motion. The
    /// change of variables <c>u_absolute = u_relative + iota_d·u_g</c> is exact at the discrete
    /// level for an UNDAMPED body (so <c>C·iota_d = 0</c>) under average acceleration when the
    /// relative load uses the SAME Newmark-consistent ground acceleration the absolute run
    /// produces — which is why the relative run is fed the absolute run's own constrained-DOF
    /// acceleration.
    /// </summary>
    [Fact]
    public void RelativeAndAbsoluteFormulations_AgreeToRoundOff()
    {
        var model = TransientFixtures.SingleDof(out int node);
        var (_, _, omega) = TransientFixtures.Properties(model, node);
        int totalDofs = 3 * model.Mesh.NodeCount;

        // A ground displacement starting from COMPLETE rest (u_g(0) = v_g(0) = a_g(0) = 0), so
        // the absolute run's initial constrained acceleration of 0 is correct and the Newmark
        // ground-acceleration sequence is self-consistent from the first step.
        double drive = 0.6 * omega;
        const double amplitude = 1e-3;
        double GroundDisplacement(double t) =>
            amplitude * (drive * t - Math.Sin(drive * t));

        double drivePeriod = 2 * Math.PI / drive;
        double dt = drivePeriod / 64;
        int steps = (int)Math.Round(10 * drivePeriod / dt);

        // A restrained node whose X is prescribed (every node except the free one is fully
        // fixed, so any other node's X carries the ground motion).
        int constrainedNode = node == 0 ? 1 : 0;

        // Absolute: prescribe iota_d·u_g(t) on the X degree of freedom of every restrained node.
        double[] Prescribe(double t)
        {
            double u = GroundDisplacement(t);
            var v = new double[totalDofs];
            for (int n = 0; n < model.Mesh.NodeCount; n++)
            {
                var restraint = model.RestraintOf(n);
                if (((int)restraint & 1) != 0)  // X restrained
                    v[3 * n] = u;
            }
            return v;
        }

        var absolute = TransientSolver.Solve(
            model,
            new TransientSolveOptions(dt, steps) { AbsolutePrescribedMotion = Prescribe });

        // The Newmark-consistent ground acceleration is the constrained node's X acceleration
        // at each stored step; average acceleration evaluates the load at t = step·dt.
        var groundAccel = new double[absolute.States.Count];
        for (int i = 0; i < absolute.States.Count; i++)
            groundAccel[i] = absolute.States[i].AccelerationAt(constrainedNode).X;

        double AgLookup(double t)
        {
            int index = (int)Math.Round(t / dt);
            index = Math.Clamp(index, 0, groundAccel.Length - 1);
            return groundAccel[index];
        }

        var relative = TransientSolver.Solve(
            model,
            new TransientSolveOptions(dt, steps)
            {
                BaseMotion = new BaseMotion(new Vector3d(1, 0, 0), AgLookup),
            });

        Assert.Equal(absolute.States.Count, relative.States.Count);
        double worst = 0, peak = 0;
        for (int i = 0; i < relative.States.Count; i++)
        {
            double t = relative.States[i].Time;
            // absolute = relative + ground translation
            double abs = absolute.States[i].DisplacementAt(node).X - GroundDisplacement(t);
            double rel = relative.States[i].DisplacementAt(node).X;
            worst = Math.Max(worst, Math.Abs(abs - rel));
            peak = Math.Max(peak, Math.Abs(rel));
        }
        double relativeError = worst / peak;
        output.WriteLine($"peak relative motion {peak:G6}, worst |absolute-relative| {worst:G6}, "
            + $"relative {relativeError:E3}");
        // Two different integrations, one change of variables: round-off.
        Assert.True(relativeError < 1e-9, $"formulations differ by {relativeError:E3}");
    }

    /// <summary>A base motion whose direction is the zero vector is refused by name.</summary>
    [Fact]
    public void AZeroDirection_IsRefused()
    {
        var model = TransientFixtures.SingleDof(out _);
        var ex = Assert.Throws<FeaException>(() => TransientSolver.Solve(
            model,
            new TransientSolveOptions(1e-4, 10)
            {
                BaseMotion = new BaseMotion(Vector3d.Zero, _ => 1.0),
            }));
        Assert.Contains("non-zero", ex.Message);
    }

    private static long CountBitDifferences(Vector3d a, Vector3d b) =>
        (BitDiff(a.X, b.X) ? 1 : 0) + (BitDiff(a.Y, b.Y) ? 1 : 0) + (BitDiff(a.Z, b.Z) ? 1 : 0);

    private static bool BitDiff(double a, double b) =>
        BitConverter.DoubleToInt64Bits(a) != BitConverter.DoubleToInt64Bits(b);
}
