using System.Diagnostics;
using System.Numerics;
using EngrCAD.Core;
using EngrCAD.Core.Solvers;
using EngrCAD.Mesh;

namespace EngrCAD.Fea;

/// <summary>Options for <see cref="DirectHarmonicSolver.Solve"/>.</summary>
public sealed record DirectHarmonicOptions
{
    /// <summary>The frequencies to evaluate, in Hz. See <see cref="HarmonicSweep"/>.
    /// Each one costs a complex factorization — see the solver remarks for why that is
    /// the honest price rather than a defect.</summary>
    public required IReadOnlyList<double> Frequencies { get; init; }

    /// <summary>
    /// Elimination ordering for the per-frequency factorization (default
    /// <see cref="SparseOrdering.Amd"/>). The shifted matrix <c>K - omega²·M</c> has the
    /// stiffness's own structurally full diagonal, which is exactly the family
    /// <c>SparseLdlt</c>'s remarks name as the one AMD serves without the saddle-system
    /// hazard — and with one factorization PER FREQUENCY, the fill reduction is paid back
    /// at every sweep point rather than once.
    /// </summary>
    public SparseOrdering Ordering { get; init; } = SparseOrdering.Amd;
}

/// <summary>
/// Steady-state frequency response by a DIRECT solve: <c>(K - omega²·M + i·omega·C)·u = f</c>
/// factorized at every frequency, over <c>SparseLdlt</c>'s complex symmetric LDLᵀ.
///
/// <para><b>This is the expensive method, and its value is fidelity rather than speed —
/// choose it for what modal superposition structurally cannot express.</b>
/// <see cref="HarmonicSolver"/> answers the same question with one complex division per
/// (mode, frequency) pair and nothing factorized; this pays one complex factorization per
/// sweep point (the amortisation is per-frequency: the factor serves every right-hand side
/// AT that frequency, of which a sweep has exactly one). What the price buys is the full
/// system with no modal basis in the way: <b>non-proportional damping</b> — the damping this
/// model itself carries (<see cref="StructuralModel.Dashpot(int, Vector3d, double)"/>,
/// <see cref="StructuralModel.SetDamping(int, RayleighDamping)"/>), whose
/// <c>phi'·C·phi</c> has off-diagonal terms so the undamped modes stop diagonalising the
/// equations — plus, in principle, frequency-dependent moduli and frequency-dependent load
/// distributions, neither of which has a vocabulary here yet. On a PROPORTIONALLY damped
/// model the two methods answer the same question and agree to the modal route's truncation
/// error, which is the cross-check the tests run.</para>
///
/// <para><b>Damping comes from the MODEL, not from these options.</b> A per-mode ratio is a
/// property of a modal basis; the damping this solver integrates is geometry-attached data —
/// a coefficient per region, a dashpot at a node — so it lives on
/// <see cref="StructuralModel"/> where the geometry is, and this solver assembles the one
/// damping matrix in the project from it (<c>FeaAssembly.Damping</c> records why that
/// assembly exists at all). A model carrying no damping statement is accepted and answered
/// undamped: the response is then real and grows without bound toward each resonance, which
/// is the true answer to the question asked — and AT a resonance the factorization refuses
/// loudly, because an undamped structure driven exactly at a natural frequency has no steady
/// state to report (the response grows linearly in time, forever).</para>
///
/// <para><b>The load is the model's own applied forces</b>, exactly as the modal route reads
/// it, driven in phase at every frequency; a thermal load is refused by name for the same
/// reason. <b>A non-zero prescribed displacement is refused</b>: a support held at a
/// constant offset is a static answer riding on the oscillation (superpose a static solve),
/// and a support whose motion oscillates is base excitation, which is filed rather than
/// half-built. <b>An unrestrained body is accepted</b>: <c>K - omega²·M</c> is nonsingular
/// at almost every omega even when K alone is not, and the low-frequency response is the
/// rigid body's own <c>|u| = F/(omega²·m)</c> — the inertia-restrained answer, pinned by
/// test so the static solver's refusal is not copied across (the same rule the transient
/// records).</para>
/// </summary>
public static class DirectHarmonicSolver
{
    /// <summary>Computes the steady-state response at every requested frequency.</summary>
    /// <param name="model">The model: mesh, materials, supports, loads — and its own
    /// damping (<see cref="StructuralModel.SetDamping(RayleighDamping)"/> /
    /// <see cref="StructuralModel.Dashpot(int, Vector3d, double)"/>).</param>
    /// <param name="options">The sweep and the elimination ordering.</param>
    /// <param name="progress">Optional cooperative cancellation and progress; the reported
    /// fraction is the sweep position, since the run's cost is one factorization per
    /// frequency and each costs the same.</param>
    public static DirectHarmonicResponse Solve(
        StructuralModel model, DirectHarmonicOptions options, ProgressCancel? progress = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Frequencies);
        if (options.Frequencies.Count == 0)
            throw new ArgumentException(
                "A harmonic solve needs at least one frequency to evaluate at.", nameof(options));
        foreach (double f in options.Frequencies)
        {
            if (f < 0 || double.IsNaN(f))
                throw new ArgumentException(
                    $"Frequencies must be non-negative and finite; {f} was given.", nameof(options));
        }

        var mesh = model.Mesh;
        HarmonicSolver.RequireLoad(model);
        RequireNoPrescribedOffset(model);

        var stiffnessRule = TetQuadrature.For(mesh.Order);
        var massRule = TetQuadrature.ForMass(mesh.Order);
        FeaGuards.RequireUsableElements(mesh, stiffnessRule, "stiffness");
        FeaGuards.RequireDensity(
            model,
            "A harmonic solve factors K - omega²·M + i·omega·C, so a body with no mass has "
            + "no inertia term and every frequency would return the same static answer — "
            + "which StructuralSolver.Solve already gives without the sweep.");

        int nodeCount = mesh.NodeCount;
        int totalDofs = 3 * nodeCount;
        var reduced = FeaAssembly.ReducedIndices(model, out int freeCount);
        if (freeCount == 0)
            throw new FeaException(
                "Every degree of freedom is restrained; there is nothing to respond.");

        var stopwatch = Stopwatch.StartNew();
        var stiffness = FeaAssembly.Reduce(
            FeaAssembly.Stiffness(model, stiffnessRule), reduced, freeCount);
        var (fullMass, _) = FeaAssembly.Mass(model, massRule);
        var mass = FeaAssembly.Reduce(fullMass, reduced, freeCount);

        bool damped = model.HasDamping;
        var dampingMatrix = damped
            ? FeaAssembly.Reduce(
                FeaAssembly.Damping(model, stiffnessRule, massRule), reduced, freeCount)
            : ZeroDiagonal(freeCount);

        var loadRe = new double[freeCount];
        double loadNorm = 0;
        for (int node = 0; node < nodeCount; node++)
        {
            var force = model.ForceOf(node);
            for (int axis = 0; axis < 3; axis++)
            {
                int r = reduced[3 * node + axis];
                if (r < 0)
                    continue;
                double value = axis == 0 ? force.X : axis == 1 ? force.Y : force.Z;
                loadRe[r] = value;
                loadNorm += value * value;
            }
        }
        loadNorm = Math.Sqrt(loadNorm);
        var loadIm = new double[freeCount];
        double assembleMs = stopwatch.Elapsed.TotalMilliseconds;

        int count = options.Frequencies.Count;
        var real = new Vector3d[count][];
        var imaginary = new Vector3d[count][];
        var solutionRe = new double[freeCount];
        var solutionIm = new double[freeCount];
        double worstResidual = 0;
        double smallestPivot = double.PositiveInfinity;
        int factorNonZeros = 0;
        int matrixNonZeros = 0;

        stopwatch.Restart();
        for (int k = 0; k < count; k++)
        {
            if (progress is not null)
            {
                progress.ThrowIfCancelled();
                progress.Report((double)k / count);
            }

            double hertz = options.Frequencies[k];
            double omega = 2.0 * Math.PI * hertz;
            // Real part K - omega²·M; imaginary part omega·C. The scale is applied to the
            // stored matrices per frequency — cheap beside the factorization it feeds.
            var realPart = FeaAssembly.Combine(stiffness, 1.0, mass, -omega * omega);
            var imagPart = omega == 0 ? ZeroDiagonal(freeCount) : FeaAssembly.Scaled(dampingMatrix, omega);

            SparseLdlt factor;
            try
            {
                factor = SparseLdlt.Factorize(
                    realPart, imagPart, options.Ordering,
                    progress is null ? null : new ProgressCancel(() => progress.CancelRequested));
            }
            catch (InvalidOperationException ex)
            {
                throw new FeaException(
                    $"The harmonic system would not factor at {hertz:G6} Hz: an exactly "
                    + "singular pivot means the model has no steady state to report there. At "
                    + "an undamped structure's own resonance the response grows linearly in "
                    + "time forever, so the loud refusal is the answer rather than a "
                    + "limitation — perturb the frequency, or give the model damping "
                    + "(SetDamping, Dashpot). At 0 Hz on an unrestrained body this is the "
                    + "static singularity: K alone cannot answer, which is the static "
                    + $"solver's own refusal. (Underlying: {ex.Message})", ex);
            }

            factorNonZeros = factor.FactorNonZeroCount;
            matrixNonZeros = realPart.NonZeroCount;
            if (factor.SmallestPivotMagnitude < smallestPivot)
                smallestPivot = factor.SmallestPivotMagnitude;

            factor.Solve(loadRe, loadIm, solutionRe, solutionIm);

            worstResidual = Math.Max(
                worstResidual,
                Residual(realPart, imagPart, solutionRe, solutionIm, loadRe, loadNorm));

            var re = new Vector3d[nodeCount];
            var im = new Vector3d[nodeCount];
            for (int node = 0; node < nodeCount; node++)
            {
                int rx = reduced[3 * node], ry = reduced[3 * node + 1], rz = reduced[3 * node + 2];
                re[node] = new Vector3d(
                    rx >= 0 ? solutionRe[rx] : 0,
                    ry >= 0 ? solutionRe[ry] : 0,
                    rz >= 0 ? solutionRe[rz] : 0);
                im[node] = new Vector3d(
                    rx >= 0 ? solutionIm[rx] : 0,
                    ry >= 0 ? solutionIm[ry] : 0,
                    rz >= 0 ? solutionIm[rz] : 0);
            }
            real[k] = re;
            imaginary[k] = im;
        }
        progress?.Report(1);
        double sweepMs = stopwatch.Elapsed.TotalMilliseconds;

        var report = new DirectHarmonicReport
        {
            NodeCount = nodeCount,
            ElementCount = mesh.ElementCount,
            Order = mesh.Order,
            TotalDofs = totalDofs,
            FreeDofs = freeCount,
            FrequencyCount = count,
            Factorizations = count,
            MatrixNonZeros = matrixNonZeros,
            FactorNonZeros = factorNonZeros,
            Ordering = options.Ordering,
            Damping = model.DampingDescription,
            NonProportional = model.HasNonProportionalDamping,
            WorstRelativeResidual = worstResidual,
            SmallestPivotMagnitude = smallestPivot,
            AssembleMs = assembleMs,
            SweepMs = sweepMs,
        };
        return new DirectHarmonicResponse(
            model, [.. options.Frequencies], real, imaginary, report);
    }

    /// <summary>
    /// A same-size matrix of exact zeros on the diagonal — the imaginary part of an
    /// undamped system. Diagonal entries rather than an empty pattern, so the union
    /// pattern the complex factorization sees is exactly the real part's own (the diagonal
    /// is already there) and the arithmetic reduces to the real factorization bit for bit
    /// (<c>SparseLdlt</c>'s complex divide is exactly the real one at a zero imaginary
    /// part).
    /// </summary>
    private static PackedSparseMatrix ZeroDiagonal(int n)
    {
        var builder = new SparseMatrixBuilder(n, n);
        for (int i = 0; i < n; i++)
            builder.Add(i, i, 0.0);
        return builder.ToSymmetricUpper();
    }

    /// <summary>The relative backward residual <c>|Z·u - f| / |f|</c> in the complex
    /// 2-norm — the honest per-frequency check on an unpivoted factorization, reported
    /// rather than assumed (the <c>SmallestPivotMagnitude</c> contract).</summary>
    private static double Residual(
        PackedSparseMatrix realPart, PackedSparseMatrix imagPart,
        double[] uRe, double[] uIm, double[] fRe, double loadNorm)
    {
        var are = realPart.Multiply(uRe);
        var aim = realPart.Multiply(uIm);
        var bre = imagPart.Multiply(uRe);
        var bim = imagPart.Multiply(uIm);
        double sum = 0;
        for (int i = 0; i < uRe.Length; i++)
        {
            double re = are[i] - bim[i] - fRe[i];
            double im = aim[i] + bre[i];
            sum += re * re + im * im;
        }
        // Exact-zero division guard: a model with no load was refused before the sweep.
        return loadNorm > 0 ? Math.Sqrt(sum) / loadNorm : Math.Sqrt(sum);
    }

    private static void RequireNoPrescribedOffset(StructuralModel model)
    {
        for (int node = 0; node < model.Mesh.NodeCount; node++)
        {
            var restraint = model.RestraintOf(node);
            if (restraint == Dof.None)
                continue;
            var held = model.PrescribedOf(node);
            for (int axis = 0; axis < 3; axis++)
            {
                // Exact-zero semantic test: "was a value stated", not a measurement.
                bool fixedHere = ((int)restraint & (1 << axis)) != 0;
                if (fixedHere && held[axis] != 0)
                    throw new FeaException(
                        $"Node {node} has a prescribed displacement of {held[axis]:G6} along "
                        + $"{"XYZ"[axis]}, and a harmonic response has nowhere to put it: a "
                        + "support held at a constant offset contributes a STATIC answer that "
                        + "rides unchanged on the oscillation (solve statics separately and "
                        + "superpose), and a support whose motion itself oscillates is base "
                        + "excitation, which is filed rather than half-built. Fix the support "
                        + "at zero for the harmonic model.");
            }
        }
    }
}

/// <summary>
/// The direct solve's answer: the complex displacement at every node for every sweep
/// point — the same accessors <see cref="HarmonicResponse"/> offers, so a caller can swap
/// methods without learning a second vocabulary, minus the modal-coordinate ones (there
/// are no modes here to have coordinates in).
/// </summary>
public sealed class DirectHarmonicResponse
{
    private readonly double[] _frequencies;
    private readonly Vector3d[][] _real;
    private readonly Vector3d[][] _imaginary;

    internal DirectHarmonicResponse(
        StructuralModel model, double[] frequencies,
        Vector3d[][] real, Vector3d[][] imaginary, DirectHarmonicReport report)
    {
        Model = model;
        _frequencies = frequencies;
        _real = real;
        _imaginary = imaginary;
        Report = report;
    }

    /// <summary>The model that was swept — loads, supports and its own damping.</summary>
    public StructuralModel Model { get; }

    /// <summary>The analysis mesh.</summary>
    public AnalysisMesh Mesh => Model.Mesh;

    /// <summary>The sweep's frequencies, in Hz.</summary>
    public IReadOnlyList<double> Frequencies => _frequencies;

    /// <summary>What the sweep did: factorization counts, fill, the worst backward
    /// residual, the smallest pivot met.</summary>
    public DirectHarmonicReport Report { get; }

    /// <summary>The complex displacement at every node for one sweep point.</summary>
    public (Vector3d Real, Vector3d Imaginary)[] DisplacementAt(int frequencyIndex)
    {
        RequireFrequency(frequencyIndex);
        var re = _real[frequencyIndex];
        var im = _imaginary[frequencyIndex];
        var result = new (Vector3d, Vector3d)[re.Length];
        for (int v = 0; v < re.Length; v++)
            result[v] = (re[v], im[v]);
        return result;
    }

    /// <summary>The complex displacement of one node's one axis across the WHOLE sweep —
    /// the transfer function (see <see cref="HarmonicResponse.ResponseAt"/>).</summary>
    public Complex[] ResponseAt(int node, int axis)
    {
        RequireNode(node);
        if ((uint)axis >= 3)
            throw new ArgumentOutOfRangeException(nameof(axis), axis, "Axis is 0 (X), 1 (Y) or 2 (Z).");
        var response = new Complex[_frequencies.Length];
        for (int k = 0; k < _frequencies.Length; k++)
        {
            var re = _real[k][node];
            var im = _imaginary[k][node];
            response[k] = axis switch
            {
                0 => new Complex(re.X, im.X),
                1 => new Complex(re.Y, im.Y),
                _ => new Complex(re.Z, im.Z),
            };
        }
        return response;
    }

    /// <summary>The displacement AMPLITUDE per component at one sweep point — the
    /// magnitude of each complex component (see
    /// <see cref="HarmonicResponse.AmplitudeAt"/> for why the vector of component
    /// amplitudes is not the amplitude of the vector).</summary>
    public Vector3d[] AmplitudeAt(int frequencyIndex)
    {
        RequireFrequency(frequencyIndex);
        var re = _real[frequencyIndex];
        var im = _imaginary[frequencyIndex];
        var amplitude = new Vector3d[re.Length];
        for (int v = 0; v < re.Length; v++)
        {
            amplitude[v] = new Vector3d(
                Math.Sqrt(re[v].X * re[v].X + im[v].X * im[v].X),
                Math.Sqrt(re[v].Y * re[v].Y + im[v].Y * im[v].Y),
                Math.Sqrt(re[v].Z * re[v].Z + im[v].Z * im[v].Z));
        }
        return amplitude;
    }

    /// <summary>The largest nodal component amplitude at one sweep point.</summary>
    public double PeakAmplitudeAt(int frequencyIndex)
    {
        double peak = 0;
        foreach (var a in AmplitudeAt(frequencyIndex))
            peak = Math.Max(peak, Math.Max(a.X, Math.Max(a.Y, a.Z)));
        return peak;
    }

    /// <summary>The sweep point carrying the largest peak amplitude.</summary>
    public int PeakFrequencyIndex
    {
        get
        {
            int best = 0;
            double bestValue = -1;
            for (int k = 0; k < _frequencies.Length; k++)
            {
                double value = PeakAmplitudeAt(k);
                if (value > bestValue)
                {
                    bestValue = value;
                    best = k;
                }
            }
            return best;
        }
    }

    /// <summary>One sweep point's displacement amplitude as a <see cref="MeshField"/> —
    /// the SAME field name the modal route publishes
    /// (<see cref="HarmonicResponse.FieldNames"/>), so a display cannot tell the two
    /// methods apart, which is the point: they answer one question.</summary>
    public IReadOnlyList<MeshField> Fields(int frequencyIndex, string lengthUnits = "mm")
    {
        RequireFrequency(frequencyIndex);
        return [MeshField.Vector(
            HarmonicResponse.FieldNames.Amplitude(_frequencies[frequencyIndex]),
            lengthUnits,
            AmplitudeAt(frequencyIndex))];
    }

    /// <summary>One sweep point's amplitude resampled onto a display mesh.</summary>
    public IReadOnlyList<MeshField> SampleOnto(
        HalfEdgeMesh displayMesh, int frequencyIndex, string lengthUnits = "mm")
    {
        ArgumentNullException.ThrowIfNull(displayMesh);
        RequireFrequency(frequencyIndex);
        var sampler = SurfaceSampler.Build(Mesh, displayMesh);
        return [MeshField.Vector(
            HarmonicResponse.FieldNames.Amplitude(_frequencies[frequencyIndex]),
            lengthUnits,
            sampler.Sample(AmplitudeAt(frequencyIndex)))];
    }

    /// <summary>The sweep as comma-separated text — the same columns
    /// <see cref="HarmonicResponse.ToCsv"/> writes.</summary>
    public string ToCsv(int node, int axis)
    {
        var probe = ResponseAt(node, axis);
        var lines = new List<string>
        {
            $"frequency_hz,peak_amplitude,node{node}_{"XYZ"[axis]}_amplitude,node{node}_{"XYZ"[axis]}_phase_deg",
        };
        for (int k = 0; k < _frequencies.Length; k++)
        {
            lines.Add(string.Join(',',
                _frequencies[k].ToString("R"),
                PeakAmplitudeAt(k).ToString("R"),
                probe[k].Magnitude.ToString("R"),
                (probe[k].Phase * 180.0 / Math.PI).ToString("R")));
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>A readable summary.</summary>
    public string ToText()
    {
        int peak = PeakFrequencyIndex;
        return string.Join(Environment.NewLine,
            $"{_frequencies.Length} frequencies from {_frequencies[0]:N2} to "
            + $"{_frequencies[^1]:N2} Hz, direct per-frequency solve, {Report.Damping}",
            Report.ToText(),
            $"peak response {PeakAmplitudeAt(peak):G6} at {_frequencies[peak]:N2} Hz");
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{_frequencies.Length} frequencies, direct solve, {Report.Damping}";

    private void RequireFrequency(int frequencyIndex)
    {
        if ((uint)frequencyIndex >= (uint)_frequencies.Length)
            throw new ArgumentOutOfRangeException(
                nameof(frequencyIndex), frequencyIndex,
                $"The sweep has {_frequencies.Length} frequencies.");
    }

    private void RequireNode(int node)
    {
        if ((uint)node >= (uint)_real[0].Length)
            throw new ArgumentOutOfRangeException(
                nameof(node), node, $"The analysis mesh has {_real[0].Length} nodes.");
    }
}

/// <summary>What a direct harmonic sweep did.</summary>
public sealed record DirectHarmonicReport
{
    /// <summary>Nodes in the analysis mesh.</summary>
    public required int NodeCount { get; init; }

    /// <summary>Elements in the analysis mesh.</summary>
    public required int ElementCount { get; init; }

    /// <summary>Linear or quadratic elements.</summary>
    public required ElementOrder Order { get; init; }

    /// <summary>Total degrees of freedom, <c>3·nodes</c>.</summary>
    public required int TotalDofs { get; init; }

    /// <summary>Degrees of freedom actually solved.</summary>
    public required int FreeDofs { get; init; }

    /// <summary>Sweep points evaluated.</summary>
    public required int FrequencyCount { get; init; }

    /// <summary>Complex factorizations performed — one per frequency, which IS the
    /// method's cost model: unlike the transient's one-factorization-per-run, nothing here
    /// amortises across sweep points because the matrix carries the frequency.</summary>
    public required int Factorizations { get; init; }

    /// <summary>Stored entries of the shifted matrix (upper triangle).</summary>
    public required int MatrixNonZeros { get; init; }

    /// <summary>Stored entries of the complex factor (last frequency; the pattern is the
    /// same at every one, since the symbolic pass sees only the union pattern).</summary>
    public required int FactorNonZeros { get; init; }

    /// <summary>The elimination ordering used.</summary>
    public required SparseOrdering Ordering { get; init; }

    /// <summary>The model's own damping, as text.</summary>
    public required string Damping { get; init; }

    /// <summary>True when the model's damping is not expressible as one
    /// <c>alpha·M + beta·K</c> — the case this solver exists for.</summary>
    public required bool NonProportional { get; init; }

    /// <summary>The worst relative backward residual <c>|Z·u - f|/|f|</c> over the sweep —
    /// computed and reported because the factorization is unpivoted, so its accuracy is a
    /// property of each frequency's conditioning rather than a guarantee.</summary>
    public required double WorstRelativeResidual { get; init; }

    /// <summary>The smallest complex pivot magnitude met anywhere in the sweep — small
    /// against its largest sibling means a frequency sat close to a resonance of an
    /// undamped subsystem.</summary>
    public required double SmallestPivotMagnitude { get; init; }

    /// <summary>Milliseconds spent assembling K, M and C.</summary>
    public required double AssembleMs { get; init; }

    /// <summary>Milliseconds spent on the sweep (all factorizations and solves).</summary>
    public required double SweepMs { get; init; }

    /// <summary>A readable summary.</summary>
    public string ToText() =>
        string.Join(Environment.NewLine,
            $"{ElementCount:N0} {(Order == ElementOrder.Linear ? "linear" : "quadratic")} "
            + $"elements, {NodeCount:N0} nodes, {FreeDofs:N0} of {TotalDofs:N0} DOF free",
            $"{Factorizations:N0} complex factorizations ({Ordering}) for "
            + $"{FrequencyCount:N0} frequencies, factor {FactorNonZeros:N0} nnz "
            + $"(fill {(double)FactorNonZeros / Math.Max(1, MatrixNonZeros):F1}x)",
            $"damping: {Damping}{(NonProportional ? " (non-proportional)" : "")}",
            $"worst |Zu-f|/|f| = {WorstRelativeResidual:E2}, smallest pivot "
            + $"{SmallestPivotMagnitude:E2}",
            $"assemble {AssembleMs:F1} ms, sweep {SweepMs:F1} ms");

    /// <inheritdoc/>
    public override string ToString() => ToText();
}
