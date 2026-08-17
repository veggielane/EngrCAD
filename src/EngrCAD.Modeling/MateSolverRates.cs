using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>One body's motion state at a driven pose: world-space velocity and
/// acceleration of the occurrence's origin, plus angular velocity and acceleration —
/// all exact consequences of the analytic Jacobian, no finite differences anywhere.</summary>
/// <param name="Path">The occurrence's path relative to the mechanism's assembly.</param>
/// <param name="Velocity">World velocity of the occurrence's origin.</param>
/// <param name="AngularVelocity">World angular velocity (rad per driver-time unit).</param>
/// <param name="Acceleration">World acceleration of the occurrence's origin.</param>
/// <param name="AngularAcceleration">World angular acceleration.</param>
public sealed record OccurrenceRate(
    string Path,
    Vector3d Velocity,
    Vector3d AngularVelocity,
    Vector3d Acceleration,
    Vector3d AngularAcceleration);

/// <summary>One joint's coordinate rates at a driven pose.</summary>
/// <param name="Joint">The joint's name.</param>
/// <param name="AngleRate">dθ/dt (rad per driver-time unit).</param>
/// <param name="AngleAcceleration">d²θ/dt².</param>
/// <param name="SlideRate">dz/dt (model units per driver-time unit).</param>
/// <param name="SlideAcceleration">d²z/dt².</param>
public sealed record JointRates(
    string Joint,
    double AngleRate,
    double AngleAcceleration,
    double SlideRate,
    double SlideAcceleration);

/// <summary>
/// Velocity and acceleration analysis of a driven mechanism at a converged pose.
/// Velocities solve J·q̇ = −∂C/∂t (the driver rows carry the only explicit time
/// dependence) and accelerations solve J·q̈ = −r̈₀ where r̈₀ is each residual's
/// second time derivative under the constant-rate flow with q̈ = 0 — computed
/// ANALYTICALLY from the same end geometry the Jacobian is built on. Finite
/// differencing sampled poses would cap accuracy near 1e-8, an order worse than the
/// weld tier, which is the same reason the mate solver's Jacobian is analytic.
/// </summary>
public sealed class MechanismRates
{
    private readonly Func<AxisJoint, JointRates> _jointRates;

    internal MechanismRates(IReadOnlyList<OccurrenceRate> occurrences, Func<AxisJoint, JointRates> jointRates)
    {
        Occurrences = occurrences;
        _jointRates = jointRates;
    }

    /// <summary>Motion state of every occurrence the mates move, in solver order.</summary>
    public IReadOnlyList<OccurrenceRate> Occurrences { get; }

    /// <summary>The rates of the occurrence a path names.</summary>
    public OccurrenceRate For(string path) =>
        Occurrences.FirstOrDefault(o => o.Path == path)
        ?? throw new ArgumentException(
            $"No moving occurrence '{path}' in the rate report. It contains: " +
            $"{string.Join(", ", Occurrences.Select(o => o.Path))}.", nameof(path));

    /// <summary>The coordinate rates of an axis joint (spin and slide).</summary>
    public JointRates For(AxisJoint joint) => _jointRates(joint);
}

public sealed partial class MateSet
{
    /// <summary>Velocity/acceleration analysis at the CURRENT (converged) frames —
    /// the seam <see cref="Mechanism.RatesAt"/> drives. The extras must include the
    /// driver whose <see cref="DriverConstraint.TargetRate"/> is set.</summary>
    internal MechanismRates Rates(MateSolverSettings settings, IReadOnlyList<AuxiliaryConstraint> extras) =>
        new Solver(this, settings, extras).ComputeRates();

    private sealed partial class Solver
    {
        public MechanismRates ComputeRates()
        {
            var mates = set.Mates;
            foreach (var mate in mates)
            {
                Register(mate.A);
                Register(mate.B);
            }
            foreach (var extra in extras)
            {
                foreach (var end in extra.Ends)
                    Register(end);
            }
            _columns = _free.Count * 6;
            _rows = mates.Sum(m => m.RowCount) + extras.Sum(x => x.RowCount);
            _poses = [.. _free.Select(v => v.Slot.Read())];
            _length = CharacteristicLength();

            if (_columns == 0)
                throw new MechanismException(
                    "Rate analysis needs at least one moving occurrence — every mated occurrence is grounded.");

            var residual = new double[_rows];
            double worst = Evaluate(residual);
            if (worst > settings.Tolerance)
                throw new MechanismException(
                    $"Rate analysis needs a CONVERGED pose, but the residual here is {worst:g3} " +
                    $"(tolerance {settings.Tolerance:g3}). Solve the mechanism first.");

            var jacobian = new double[_rows * _columns];
            Jacobian(jacobian);
            var normal = new double[_columns * _columns];
            var rhs = new double[_columns];

            // Full rank is what makes the rates UNIQUE: a driven mechanism with DOF
            // left over has a family of velocities, and picking one would be a guess.
            NormalEquations(jacobian, residual, normal, rhs);
            int rank = Rank(normal, _columns, _rankTolerance);
            if (rank < _columns)
                throw new MechanismException(
                    $"Rates are not unique: the driven mechanism still has {_columns - rank} free degree" +
                    $"{(_columns - rank == 1 ? "" : "s")} of freedom. Add a driver, a joint, or a ground " +
                    "until the driven system is fully constrained.");

            // Velocities: J q̇ = −∂C/∂t via the normal equations (JᵀJ is PD at full
            // rank). Only extras carry explicit time dependence.
            var timeDerivative = new double[_rows];
            int row = mates.Sum(m => m.RowCount);
            foreach (var extra in extras)
            {
                extra.TimeDerivative(EvaluateEnds(extra), _length, timeDerivative.AsSpan(row, extra.RowCount));
                row += extra.RowCount;
            }
            var negated = new double[_rows];
            for (int i = 0; i < _rows; i++)
                negated[i] = -timeDerivative[i];
            NormalEquations(jacobian, negated, normal, rhs);
            if (!SolveSpd(normal, rhs, out var qdot))
                throw new MechanismException("The velocity solve is singular at this pose (a dead centre).");

            // Accelerations: J q̈ = −r̈₀, with r̈₀ assembled analytically from each
            // end's motion under the constant-rate flow.
            var gamma = new double[_rows];
            SecondOrderResiduals(qdot, gamma);
            for (int i = 0; i < _rows; i++)
                negated[i] = -gamma[i];
            NormalEquations(jacobian, negated, normal, rhs);
            if (!SolveSpd(normal, rhs, out var qddot))
                throw new MechanismException("The acceleration solve is singular at this pose (a dead centre).");

            // Per-occurrence motion states, from the same chain formulas as the columns.
            var occurrences = new List<OccurrenceRate>(_free.Count);
            foreach (var variable in _free)
            {
                var target = variable.Slot.Occurrence;
                var pseudo = variable.Ancestors.Length == 0
                    ? new MateRef(target, Vector3d.Zero)
                    : new MateRef(
                        [.. variable.Ancestors.Select(link => link.Occurrence), target], Vector3d.Zero);
                var end = Evaluate(pseudo);
                var (velocity, _) = Delta(end, qdot);
                var (accelerationQdd, _) = Delta(end, qddot);
                var (accelerationFlow, _) = SecondDelta(end, qdot);
                var (omega, alpha) = AngularRates(end, qdot, qddot);
                occurrences.Add(new OccurrenceRate(
                    variable.Path, velocity, omega, accelerationQdd + accelerationFlow, alpha));
            }

            return new MechanismRates(occurrences, joint =>
            {
                var motions = new EndMotion[4];
                MateRef[] ends = [joint.A, joint.B, joint.ReferenceA, joint.ReferenceB];
                for (int i = 0; i < 4; i++)
                    motions[i] = TotalMotion(Evaluate(ends[i]), qdot, qddot);
                double angleRate = JointArithmetic.AngleRate(motions[0], motions[2], motions[3]);
                double angleAcceleration = JointArithmetic.AngleSecond(motions[0], motions[2], motions[3]);
                double slideRate = JointArithmetic.SlideDelta(
                    JointArithmetic.Value(motions[0]), JointArithmetic.Value(motions[1]),
                    JointArithmetic.Rate(motions[0]), JointArithmetic.Rate(motions[1]));
                double slideAcceleration = JointArithmetic.SlideSecond(motions[0], motions[1]);
                return new JointRates(joint.Name, angleRate, angleAcceleration, slideRate, slideAcceleration);
            });
        }

        /// <summary>First-order motion of an end under variable rates
        /// <paramref name="u"/>: the Jacobian columns contracted with u.</summary>
        private (Vector3d Point, Vector3d Direction) Delta(in End end, double[] u)
        {
            var dPoint = Vector3d.Zero;
            var dDirection = Vector3d.Zero;
            foreach (var contribution in end.Contributions)
            {
                var (v, w) = BlockRates(u, contribution.Block);
                dPoint += v + w.Cross(end.Point - contribution.Origin);
                dDirection += w.Cross(end.Direction);
            }
            return (dPoint, dDirection);
        }

        /// <summary>Second-order (q̈ = 0) motion of an end under rates
        /// <paramref name="u"/>. Contributions are stored outermost-first, and the
        /// composed rigid flow x(t) = Δ₁(t)(Δ₂(t)(…x)) gives, for constant rates,
        /// ẍ = Σᵢ ωᵢ×(ωᵢ×(x−oᵢ)) + 2·Σ_{outer i, inner j} ωᵢ×(vⱼ + ωⱼ×(x−oⱼ)) —
        /// the centripetal terms plus the Coriolis-style cross terms between chain
        /// levels. Exact; no finite differences.</summary>
        private (Vector3d Point, Vector3d Direction) SecondDelta(in End end, double[] u)
        {
            var ddPoint = Vector3d.Zero;
            var ddDirection = Vector3d.Zero;
            var contributions = end.Contributions;
            for (int i = 0; i < contributions.Length; i++)
            {
                var (_, wi) = BlockRates(u, contributions[i].Block);
                ddPoint += wi.Cross(wi.Cross(end.Point - contributions[i].Origin));
                ddDirection += wi.Cross(wi.Cross(end.Direction));
                for (int j = i + 1; j < contributions.Length; j++)
                {
                    var (vj, wj) = BlockRates(u, contributions[j].Block);
                    ddPoint += 2 * wi.Cross(vj + wj.Cross(end.Point - contributions[j].Origin));
                    ddDirection += 2 * wi.Cross(wj.Cross(end.Direction));
                }
            }
            return (ddPoint, ddDirection);
        }

        /// <summary>An end's full motion state: first-order from q̇, acceleration with
        /// the q̈ contribution INCLUDED (for reporting true joint accelerations —
        /// contrast the q̈ = 0 flow used to build γ).</summary>
        private EndMotion TotalMotion(in End end, double[] qdot, double[] qddot)
        {
            var (velocity, directionRate) = Delta(end, qdot);
            var (accelQdd, dirAccelQdd) = Delta(end, qddot);
            var (accelFlow, dirAccelFlow) = SecondDelta(end, qdot);
            return new EndMotion(
                end.Point, end.Direction, velocity, directionRate,
                accelQdd + accelFlow, dirAccelQdd + dirAccelFlow);
        }

        /// <summary>The q̈ = 0 flow motion used to assemble γ.</summary>
        private EndMotion FlowMotion(in End end, double[] qdot)
        {
            var (velocity, directionRate) = Delta(end, qdot);
            var (accel, dirAccel) = SecondDelta(end, qdot);
            return new EndMotion(end.Point, end.Direction, velocity, directionRate, accel, dirAccel);
        }

        private (Vector3d Linear, Vector3d Angular) BlockRates(double[] u, int block)
        {
            var linear = new Vector3d(u[block * 6], u[block * 6 + 1], u[block * 6 + 2]);
            // Rotation variables are scaled by the characteristic length; undo it to
            // get the physical angular rate (same rule as Apply).
            var angular = new Vector3d(u[block * 6 + 3], u[block * 6 + 4], u[block * 6 + 5]) / _length;
            return (linear, angular);
        }

        /// <summary>A body's angular velocity and acceleration from its chain: ω is
        /// the sum of the free links' rates; α picks up the q̈ terms plus the
        /// ωᵢ×ωⱼ cross terms of composed rotations (outermost-first).</summary>
        private (Vector3d Omega, Vector3d Alpha) AngularRates(in End end, double[] qdot, double[] qddot)
        {
            var omega = Vector3d.Zero;
            var alpha = Vector3d.Zero;
            var contributions = end.Contributions;
            for (int i = 0; i < contributions.Length; i++)
            {
                var (_, wi) = BlockRates(qdot, contributions[i].Block);
                var (_, ai) = BlockRates(qddot, contributions[i].Block);
                omega += wi;
                alpha += ai;
                for (int j = i + 1; j < contributions.Length; j++)
                {
                    var (_, wj) = BlockRates(qdot, contributions[j].Block);
                    alpha += wi.Cross(wj);
                }
            }
            return (omega, alpha);
        }

        /// <summary>Each residual row's second time derivative under the constant-rate
        /// flow with q̈ = 0 — the γ vector of the acceleration solve, assembled by the
        /// product rule on exactly the formulas <see cref="Evaluate(double[])"/> uses.</summary>
        private void SecondOrderResiduals(double[] qdot, double[] gamma)
        {
            int row = 0;
            foreach (var mate in set.Mates)
            {
                var a = Evaluate(mate.A);
                var b = Evaluate(mate.B);
                var ma = FlowMotion(a, qdot);
                var mb = FlowMotion(b, qdot);
                var w = b.Point - a.Point;
                var wd = mb.Velocity - ma.Velocity;
                var wdd = mb.Acceleration - ma.Acceleration;
                var da = a.Direction;
                var dad = ma.DirectionRate;
                var dadd = ma.DirectionAcceleration;
                var db = b.Direction;
                var dbd = mb.DirectionRate;
                var dbdd = mb.DirectionAcceleration;
                switch (mate.Kind)
                {
                    case MateKind.Coincident:
                        Write(gamma, ref row, wdd);
                        break;

                    case MateKind.Planar:
                        Write(gamma, ref row, (dadd + dbdd) * _length);
                        gamma[row++] = wdd.Dot(da) + 2 * wd.Dot(dad) + w.Dot(dadd);
                        break;

                    case MateKind.Concentric:
                        Write(gamma, ref row,
                            (dadd.Cross(db) + 2 * dad.Cross(dbd) + da.Cross(dbdd)) * _length);
                        // d²/dt² of w − (w·n)n by the product rule.
                        Write(gamma, ref row,
                            wdd - dadd * w.Dot(da)
                                - dad * (2 * (wd.Dot(da) + w.Dot(dad)))
                                - da * (wdd.Dot(da) + 2 * wd.Dot(dad) + w.Dot(dadd)));
                        break;

                    case MateKind.Parallel:
                        Write(gamma, ref row,
                            (dadd.Cross(db) + 2 * dad.Cross(dbd) + da.Cross(dbdd)) * _length);
                        break;

                    case MateKind.Perpendicular:
                    case MateKind.Angle:
                        gamma[row++] = (dadd.Dot(db) + 2 * dad.Dot(dbd) + da.Dot(dbdd)) * _length;
                        break;

                    default:   // Distance: r = |w|, r̈ = (|ẇ|² + w·ẅ)/|w| − (w·ẇ)²/|w|³
                    {
                        double norm = w.Length;
                        gamma[row++] = norm > 0
                            ? (wd.Dot(wd) + w.Dot(wdd)) / norm
                              - (w.Dot(wd) * w.Dot(wd)) / (norm * norm * norm)
                            : 0;   // exact-zero guard: coincident points have no rate
                        break;
                    }
                }
            }

            foreach (var extra in extras)
            {
                var motions = new EndMotion[extra.Ends.Count];
                for (int i = 0; i < motions.Length; i++)
                    motions[i] = FlowMotion(Evaluate(extra.Ends[i]), qdot);
                var values = new EndValue[motions.Length];
                for (int i = 0; i < motions.Length; i++)
                    values[i] = new EndValue(motions[i].Point, motions[i].Direction);
                extra.SecondOrder(values, motions, _length, gamma.AsSpan(row, extra.RowCount));
                row += extra.RowCount;
            }
        }
    }
}
