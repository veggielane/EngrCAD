using EngrCAD.Mesh;

namespace EngrCAD.Fea;

/// <summary>
/// One counted stress cycle (or half cycle): the full RANGE between its peak and valley,
/// their mean, and how many times it counts — 1.0 for a closed cycle, 0.5 for a residual
/// half. <see cref="Amplitude"/> is what an S-N curve consumes.
/// </summary>
/// <param name="Range">Peak-to-valley stress range (MPa).</param>
/// <param name="Mean">The mean of the peak and valley (MPa, signed).</param>
/// <param name="Count">1.0 for a full cycle, 0.5 for a half; aggregated entries may carry
/// larger counts in the repeating mode.</param>
public readonly record struct RainflowCycle(double Range, double Mean, double Count)
{
    /// <summary>Half the range — the alternating amplitude the S-N line is stated in.</summary>
    public double Amplitude => 0.5 * Range;

    /// <inheritdoc/>
    public override string ToString() =>
        $"range {Range:G6}, mean {Mean:G6}, x{Count:G3}";
}

/// <summary>Options for rainflow-based fatigue over a stress history.</summary>
public sealed record RainflowFatigueOptions
{
    /// <summary>Which mean-stress correction, applied PER COUNTED CYCLE (default
    /// <see cref="MeanStressCorrection.Goodman"/> — each cycle's own mean corrects its own
    /// amplitude before the S-N lookup, which is what lets one R = -1 curve answer a
    /// history whose mean wanders).</summary>
    public MeanStressCorrection Correction { get; init; } = MeanStressCorrection.Goodman;

    /// <summary>
    /// How the OPEN END of the history is read — the design question ASTM E1049 names and
    /// leaves to the analyst, answered here as an option because both readings are honest
    /// for different inputs.
    ///
    /// <para><b>False (default): the history is one-shot</b>, which is what a transient
    /// solve's stored states are — a load event with a beginning and an end — and the
    /// residual ranges the counting cannot close are E1049 HALF cycles, no more and no
    /// less.</para>
    ///
    /// <para><b>True: the history is ONE PERIOD of a repeating load.</b> The series is
    /// rearranged to begin at its extremum of largest magnitude (E1049's own prescription,
    /// under which every count closes) and the residual halves pair into full cycles, so a
    /// block program counted per block accumulates no phantom boundary halves per
    /// repetition.</para>
    /// </summary>
    public bool AssumeRepeating { get; init; }

    /// <summary>
    /// How many REPETITIONS of the stored history the safety factor is measured against —
    /// the spectrum's version of <see cref="FatigueOptions.DesignLife"/>, and the same two
    /// readings: null (the default) measures against INFINITE life, i.e. the load multiplier
    /// at which the worst counted cycle first reaches the endurance limit and damage first
    /// appears; a stated R measures against a Miner damage of <c>1/R</c>, the multiplier at
    /// which one pass in R exhausts the part.
    ///
    /// <para>A material with no endurance limit (the aluminium rows) has no infinite life to
    /// measure against, so there this is REQUIRED and its absence is refused by name —
    /// verbatim the static path's rule, for verbatim its reason.</para>
    ///
    /// <para>Repetitions rather than cycles because a variable-amplitude history's own unit
    /// IS one pass of itself: the counted spectrum can hold any number of cycles of any
    /// amplitudes, and only their accumulated damage composes.</para>
    /// </summary>
    public double? DesignRepetitions
    {
        get;
        init
        {
            if (value is { } repetitions && !(repetitions >= 1))
                throw new ArgumentOutOfRangeException(
                    nameof(value), value,
                    "A design life is at least one repetition of the history.");
            field = value;
        }
    }
}

/// <summary>
/// ASTM E1049 rainflow cycle counting — the standard's own three-point algorithm
/// (§5.4.4), verbatim: X the range under consideration, Y the previous range, S the
/// starting point, with Y counted as a FULL cycle when it does not contain S, a HALF when
/// it does, and every range remaining at the end a half. The worked example in the
/// standard's Fig. 6 is transcribed as a test, cycle for cycle.
/// </summary>
public static class Rainflow
{
    /// <summary>
    /// Counts the cycles in a stress (or load, or strain) history.
    ///
    /// <para>The series is reduced to its turning points first — a sample that is not a
    /// reversal cannot start or end a cycle, and equal consecutive values merge — so the
    /// caller may pass raw samples. NaN is refused by name: every comparison against one
    /// answers false, which would not crash but would silently drop cycles.</para>
    /// </summary>
    /// <param name="history">The samples, in time order.</param>
    /// <param name="assumeRepeating">See
    /// <see cref="RainflowFatigueOptions.AssumeRepeating"/>.</param>
    public static IReadOnlyList<RainflowCycle> Count(
        ReadOnlySpan<double> history, bool assumeRepeating = false)
    {
        foreach (double value in history)
        {
            if (double.IsNaN(value))
                throw new FeaException(
                    "The history contains NaN, and rainflow counting is made of comparisons "
                    + "— every one against a NaN answers false, so cycles would be dropped "
                    + "silently rather than counted wrongly. Clean the series first.");
        }

        if (!assumeRepeating)
            return CountTurningPoints(TurningPoints(history));

        // One period of a repeating load: rearrange to begin at the extremum of largest
        // magnitude and CLOSE the loop by appending the start, so the sequence begins and
        // ends at the global extremum — E1049's prescription, under which every count
        // pairs. The rotation is of the RAW samples (the turning-point structure at the
        // old seam differs from the rotated one's, so extracting first would be wrong).
        if (history.Length == 0)
            return [];
        int start = 0;
        for (int i = 1; i < history.Length; i++)
        {
            if (Math.Abs(history[i]) > Math.Abs(history[start]))
                start = i;
        }
        var rotated = new double[history.Length + 1];
        for (int i = 0; i < history.Length; i++)
            rotated[i] = history[(start + i) % history.Length];
        rotated[^1] = history[start];

        var counted = CountTurningPoints(TurningPoints(rotated));

        // Pair the residual halves: with the rearranged start every half has a partner of
        // bit-identical (range, mean) — the two flanks of one closed excursion — so
        // aggregation by exact key is a pairing, not a tolerance. Full cycles pass through
        // untouched; first-occurrence order is kept so the output is deterministic.
        var order = new List<(double Range, double Mean)>();
        var totals = new Dictionary<(double Range, double Mean), double>();
        foreach (var cycle in counted)
        {
            var key = (cycle.Range, cycle.Mean);
            if (!totals.TryGetValue(key, out double count))
                order.Add(key);
            totals[key] = count + cycle.Count;
        }
        var paired = new List<RainflowCycle>(order.Count);
        foreach (var key in order)
            paired.Add(new RainflowCycle(key.Range, key.Mean, totals[key]));
        return paired;
    }

    /// <summary>The reversals of a series: first and last samples kept, interior samples
    /// kept only where the slope changes sign, equal consecutive values merged.</summary>
    private static List<double> TurningPoints(ReadOnlySpan<double> history)
    {
        var points = new List<double>();
        foreach (double value in history)
        {
            // Exact-equality merge: a repeated sample is the same reversal, and "did the
            // value change" is a semantic test, not a measurement.
            if (points.Count > 0 && points[^1] == value)
                continue;
            if (points.Count >= 2)
            {
                double a = points[^2], b = points[^1];
                // Interior point on a monotone run: replace rather than append.
                if ((b > a && value > b) || (b < a && value < b))
                {
                    points[^1] = value;
                    continue;
                }
            }
            points.Add(value);
        }
        return points;
    }

    /// <summary>E1049 §5.4.4, rules (1)–(6) over an explicit stack.</summary>
    private static List<RainflowCycle> CountTurningPoints(List<double> points)
    {
        var cycles = new List<RainflowCycle>();
        if (points.Count < 2)
            return cycles;

        // The stack's bottom element IS the current starting point S: rule 5 discards S
        // from the bottom, so no separate index is needed.
        var stack = new List<double>();
        foreach (double point in points)
        {
            stack.Add(point);
            while (stack.Count >= 3)
            {
                double x = Math.Abs(stack[^1] - stack[^2]);
                double y = Math.Abs(stack[^2] - stack[^3]);
                if (x < y)
                    break;
                if (stack.Count == 3)
                {
                    // Y contains the starting point: count it as a HALF cycle, discard S,
                    // and the next point becomes the new start (rule 5).
                    cycles.Add(Half(stack[^3], stack[^2]));
                    stack.RemoveAt(stack.Count - 3);
                    continue;
                }
                // Y is interior: one FULL cycle, both its points discarded (rule 4).
                cycles.Add(new RainflowCycle(
                    y, 0.5 * (stack[^2] + stack[^3]), 1.0));
                stack.RemoveRange(stack.Count - 3, 2);
            }
        }

        // Rule 6: each remaining range is a half cycle.
        for (int i = 1; i < stack.Count; i++)
            cycles.Add(Half(stack[i - 1], stack[i]));
        return cycles;

        static RainflowCycle Half(double a, double b) =>
            new(Math.Abs(b - a), 0.5 * (a + b), 0.5);
    }
}

/// <summary>
/// Variable-amplitude fatigue over a transient run: per-node Miner's-rule damage from the
/// rainflow-counted signed von Mises history, and the life in REPETITIONS of that history.
///
/// <para><b>Damage is per pass of the stored history</b> — the one quantity that composes:
/// a block program run k times accumulates k·Damage, and the life is <c>1/Damage</c>
/// repetitions. It is published raw (a legend over damage is linear and meaningful) beside
/// the life as log10(repetitions), the static path's own spelling for a quantity spread
/// over decades.</para>
///
/// <para><b>The safety factor is the load multiplier to the damage target</b>
/// (<see cref="SafetyFactor"/> / <see cref="FatigueAnalysis.LoadFactor"/>) — the same claim
/// the static pair's radial factor makes, verified the same way (scale the history by it and
/// the damage lands ON the target). Against infinite life by default; against a stated
/// <see cref="RainflowFatigueOptions.DesignRepetitions"/> otherwise.</para>
/// </summary>
public sealed class RainflowFatigueResults
{
    private readonly double[] _damage;
    private readonly double[] _cycleCounts;
    private readonly double[] _log10Repetitions;
    private readonly double[] _safety;

    internal RainflowFatigueResults(
        TransientResults history, SnCurve curve, RainflowFatigueOptions options,
        double[] damage, double[] cycleCounts, double[] log10Repetitions, double[] safety)
    {
        History = history;
        Curve = curve;
        Options = options;
        _damage = damage;
        _cycleCounts = cycleCounts;
        _log10Repetitions = log10Repetitions;
        _safety = safety;
        Damage = Array.AsReadOnly(damage);
        CycleCounts = Array.AsReadOnly(cycleCounts);
        Log10Repetitions = Array.AsReadOnly(log10Repetitions);
        SafetyFactor = Array.AsReadOnly(safety);
    }

    /// <summary>The transient run the histories came from.</summary>
    public TransientResults History { get; }

    /// <summary>The S-N curve evaluated against.</summary>
    public SnCurve Curve { get; }

    /// <summary>The settings used.</summary>
    public RainflowFatigueOptions Options { get; }

    /// <summary>The analysis mesh.</summary>
    public AnalysisMesh Mesh => History.Mesh;

    /// <summary>Per-node Miner's-rule damage accumulated by ONE pass of the stored
    /// history — <c>sum(count_i / N_i)</c> over the counted cycles. Zero is no damage
    /// (every cycle at or below the endurance limit); +infinity is the static-failure
    /// branch (a cycle whose corrected amplitude is unbounded).</summary>
    public IReadOnlyList<double> Damage { get; }

    /// <summary>Per-node total cycle count (halves included at 0.5) — the diagnostic that
    /// says how much of a history the counting actually saw.</summary>
    public IReadOnlyList<double> CycleCounts { get; }

    /// <summary>
    /// Per-node life as <b>log10(repetitions of the stored history)</b>. NaN is infinite
    /// life (zero damage — the no-value spelling ranging already skips); 0 is the
    /// one-repetition floor, covering the static-failure branch, for the same reasons the
    /// static path floors at one cycle.
    /// </summary>
    public IReadOnlyList<double> Log10Repetitions { get; }

    /// <summary>
    /// Per-node safety factor: the multiplier on the WHOLE load history at which this
    /// node's counted spectrum first reaches its damage target
    /// (<see cref="FatigueAnalysis.LoadFactor"/>) — against infinite life by default, or
    /// against <see cref="RainflowFatigueOptions.DesignRepetitions"/> when stated. NaN
    /// where there is no fatigue mechanism at all, the no-value spelling the static path
    /// uses.
    /// <para>A node whose history is CONSTANT carries no counted cycle, so it reads NaN
    /// here whatever its steady stress is: rainflow measures cycles, and a steady load is
    /// a static-strength question rather than a fatigue one (the static pair's
    /// <see cref="FatigueAnalysis.Evaluate(StructuralResults, StructuralResults, SnCurve,
    /// FatigueOptions?)"/> answers it, reporting the S_ut/mean margin from two identical
    /// cases). <b>That NaN has a second cause and it is the recorded blind spot</b>: a
    /// reversed PURE SHEAR cycle reads the same signed von Mises at both extremes (negating
    /// a pure shear tensor is a rotation of it), so its series is constant too and is
    /// indistinguishable here from a node that never moves. The factor inherits the scalar
    /// equivalent's blind spot exactly as <see cref="Damage"/> does — it neither adds one
    /// nor repairs one — so a NaN says "no cycle IN THE SCALAR EQUIVALENT", which is
    /// critical-plane territory (see <see cref="FatigueAnalysis"/>' multiaxial
    /// refusal).</para>
    /// </summary>
    public IReadOnlyList<double> SafetyFactor { get; }

    /// <summary>The counted cycles at one node — recomputed on demand from the stored
    /// states (they are retained anyway), so verification can see exactly what the damage
    /// sum consumed without every node's list being stored.</summary>
    public IReadOnlyList<RainflowCycle> CyclesAt(int node)
    {
        if ((uint)node >= (uint)_damage.Length)
            throw new ArgumentOutOfRangeException(
                nameof(node), node, $"The analysis mesh has {_damage.Length} nodes.");
        var states = History.States;
        var series = new double[states.Count];
        for (int s = 0; s < states.Count; s++)
            series[s] = FatigueAnalysis.SignedVonMises(states[s].Results.NodalStress[node]);
        return Rainflow.Count(series, Options.AssumeRepeating);
    }

    /// <summary>The largest damage over all nodes.</summary>
    public double MaxDamage
    {
        get
        {
            double worst = 0;
            foreach (double d in _damage)
                worst = Math.Max(worst, d);
            return worst;
        }
    }

    /// <summary>The node carrying <see cref="MaxDamage"/> (lowest index on a tie), or -1
    /// for a run with no damage anywhere.</summary>
    public int MaxDamageNode
    {
        get
        {
            int best = -1;
            for (int v = 0; v < _damage.Length; v++)
            {
                if (_damage[v] > 0 && (best < 0 || _damage[v] > _damage[best]))
                    best = v;
            }
            return best;
        }
    }

    /// <summary>The shortest life over all nodes as log10(repetitions), or NaN when every
    /// node lives forever.</summary>
    public double MinLog10Repetitions
    {
        get
        {
            double best = double.NaN;
            foreach (double n in _log10Repetitions)
            {
                if (!double.IsNaN(n) && !(n >= best))
                    best = n;
            }
            return best;
        }
    }

    /// <summary>The smallest safety factor over all nodes (NaN entries skipped), or NaN
    /// when no node carries a counted cycle at all.</summary>
    public double MinSafetyFactor
    {
        get
        {
            double best = double.NaN;
            foreach (double n in _safety)
            {
                if (!double.IsNaN(n) && !(n >= best))
                    best = n;
            }
            return best;
        }
    }

    /// <summary>The node carrying <see cref="MinSafetyFactor"/> (lowest index on a tie),
    /// or -1 when every node is NaN.</summary>
    public int MinSafetyFactorNode
    {
        get
        {
            int best = -1;
            for (int v = 0; v < _safety.Length; v++)
            {
                if (double.IsNaN(_safety[v]))
                    continue;
                if (best < 0 || _safety[v] < _safety[best])
                    best = v;
            }
            return best;
        }
    }

    /// <summary>How many nodes accumulate any damage at all.</summary>
    public int DamagedNodeCount
    {
        get
        {
            int count = 0;
            foreach (double d in _damage)
            {
                if (d > 0)
                    count++;
            }
            return count;
        }
    }

    /// <summary>The field names this class produces — distinct from the static pair's, so
    /// the two analyses' results can sit on one part without replacing each other.</summary>
    public static class FieldNames
    {
        /// <summary>Per-node Miner damage per pass of the history (dimensionless).</summary>
        public const string Damage = "Fatigue damage";

        /// <summary>Per-node life, log10(repetitions of the history); NaN = infinite.</summary>
        public const string Repetitions = "Fatigue repetitions";

        /// <summary>Per-node load multiplier to the damage target (dimensionless).
        /// <para>Named "load factor" rather than "safety factor" so it does not COLLIDE
        /// with the static pair's field: a part carrying both analyses would otherwise
        /// have one replace the other (same name replaces, by design), and the two answer
        /// different questions over different inputs.</para></summary>
        public const string LoadFactor = "Fatigue load factor";
    }

    /// <summary>The results as <see cref="MeshField"/>s over the analysis mesh's nodes.</summary>
    public IReadOnlyList<MeshField> Fields() =>
    [
        MeshField.Scalar(FieldNames.Damage, "per history", _damage),
        MeshField.Scalar(FieldNames.Repetitions, "log10(repetitions)", _log10Repetitions),
        MeshField.Scalar(FieldNames.LoadFactor, "", _safety),
    ];

    /// <summary>The results resampled onto a display mesh (the
    /// <see cref="SurfaceSampler"/> contract).</summary>
    public IReadOnlyList<MeshField> SampleOnto(HalfEdgeMesh displayMesh, out double maxSampleDistance)
    {
        ArgumentNullException.ThrowIfNull(displayMesh);
        var sampler = SurfaceSampler.Build(Mesh, displayMesh);
        maxSampleDistance = sampler.MaxSampleDistance;
        return
        [
            MeshField.Scalar(FieldNames.Damage, "per history", sampler.Sample(_damage)),
            MeshField.Scalar(
                FieldNames.Repetitions, "log10(repetitions)", sampler.Sample(_log10Repetitions)),
            MeshField.Scalar(FieldNames.LoadFactor, "", sampler.Sample(_safety)),
        ];
    }

    /// <summary><see cref="SampleOnto(HalfEdgeMesh, out double)"/> without the
    /// diagnostic.</summary>
    public IReadOnlyList<MeshField> SampleOnto(HalfEdgeMesh displayMesh) =>
        SampleOnto(displayMesh, out _);

    /// <inheritdoc/>
    public override string ToString()
    {
        int node = MaxDamageNode;
        string worst = node >= 0
            ? $"max damage {_damage[node]:G4}/history at node {node} "
              + $"({_cycleCounts[node]:G4} cycles counted)"
            : "no node accumulates damage";
        int weakest = MinSafetyFactorNode;
        string factor = weakest >= 0
            ? $"; min load factor {_safety[weakest]:G4} at node {weakest} "
              + (Options.DesignRepetitions is { } r
                  ? $"(to {r:G4} repetitions)"
                  : "(to infinite life)")
            : "";
        return $"{Curve.Name}, {Options.Correction}"
            + $"{(Options.AssumeRepeating ? ", repeating" : "")}: {worst}{factor}";
    }
}
