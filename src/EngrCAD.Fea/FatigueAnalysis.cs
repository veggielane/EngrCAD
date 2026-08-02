using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Fea;

/// <summary>How a non-zero mean stress reduces the allowable alternating stress.</summary>
public enum MeanStressCorrection
{
    /// <summary>No correction: the mean stress is ignored entirely. Only right for a
    /// genuinely fully reversed history, and offered so the correction's effect can be
    /// measured rather than taken on faith.</summary>
    None,

    /// <summary>
    /// The modified Goodman line — allowable amplitude falls LINEARLY from the fatigue
    /// strength at zero mean to exactly zero at the ultimate strength. Conservative
    /// against test data for ductile metals, which is why it is the default.
    /// </summary>
    Goodman,

    /// <summary>
    /// The Gerber parabola — allowable amplitude falls with the SQUARE of the mean ratio,
    /// reaching zero at the ultimate strength. Tracks ductile-metal test data more closely
    /// than Goodman and is accordingly less conservative; the choice between them is an
    /// engineering policy, which is why it is a parameter rather than a better default.
    /// </summary>
    Gerber,
}

/// <summary>Settings for <see cref="FatigueAnalysis.Evaluate"/>.</summary>
public sealed record FatigueOptions
{
    /// <summary>Which mean-stress correction (default
    /// <see cref="MeanStressCorrection.Goodman"/> — the conservative line).</summary>
    public MeanStressCorrection Correction { get; init; } = MeanStressCorrection.Goodman;

    /// <summary>
    /// The life (cycles) the safety factor is computed against, or null to use the
    /// material's endurance limit — i.e. a safety factor against INFINITE life. A material
    /// with no endurance limit (aluminium) has no infinite-life strength to measure
    /// against, so there a design life is REQUIRED and its absence is refused by name.
    /// </summary>
    public double? DesignLife
    {
        get;
        init
        {
            if (value is { } life && !(life >= 1))
                throw new ArgumentOutOfRangeException(
                    nameof(value), value, "A design life is at least one cycle.");
            field = value;
        }
    }
}

/// <summary>
/// Stress-life (S-N) fatigue post-processing over two static load cases — arithmetic on
/// results that already exist, not a solver.
///
/// <para><b>The loading model is PROPORTIONAL</b>: the two cases are the extremes of one
/// load history through which every stress component scales together, so a scalar
/// equivalent stress per node captures the cycle. The scalar is the SIGNED von Mises
/// (<see cref="SignedVonMises"/>) so a fully reversed load reads R = -1 rather than a
/// pulsating magnitude.</para>
///
/// <para><b>What this is NOT, named rather than approximated.</b>
/// <b>Welds</b>: hot-spot and nominal-stress category methods (IIW / Eurocode) are their
/// own discipline — a weld's life is governed by its class and its geometric stress, not
/// by the parent material's Basquin line, so running this over a welded joint answers the
/// wrong question. <b>Multiaxial criteria beyond von Mises equivalence</b>: non-proportional
/// load paths rotate the principal frame and need critical-plane methods
/// (Findley, Fatemi–Socie), a different computation over a different input — and the
/// structural tell is inside <see cref="SignedVonMises"/>'s own remarks: a reversed pure
/// shear cycle is invisible to ANY scalar signed equivalent, because negating a pure shear
/// tensor is a rotation of it. <b>Variable amplitude</b>: rainflow counting needs a time
/// history, which two static cases cannot carry; the transient solver's stored states are
/// the natural input and that consumer is filed in todo.md.</para>
/// </summary>
public static class FatigueAnalysis
{
    /// <summary>
    /// The signed von Mises equivalent: the von Mises magnitude carrying the SIGN of the
    /// hydrostatic trace.
    ///
    /// <para><b>The convention is the trace, and the choice is recorded.</b> The common
    /// alternative — the sign of the absolutely largest principal stress — needs an
    /// eigensolve and JUMPS when two nearly equal principals of opposite sign swap
    /// magnitude under a smoothly varying load; the trace is linear in the stress
    /// components, so its sign boundary is a fixed plane in stress space, and for the
    /// uniaxial state the S-N data was measured in the two conventions agree exactly
    /// (trace = sigma). At exactly zero trace the sign is taken positive.</para>
    ///
    /// <para><b>The blind spot is structural, not a defect of this convention</b>: for a
    /// PURE SHEAR state, -s has the same principal values as s (negating it is a rotation),
    /// so no invariant-based sign can distinguish the two halves of a reversed shear cycle
    /// and the decomposition reads zero amplitude. That is precisely the case
    /// critical-plane methods exist for, and it is named under the class's multiaxial
    /// refusal rather than patched with a tie-break that could not be right.</para>
    /// </summary>
    public static double SignedVonMises(in SymmetricTensor3 stress) =>
        stress.Trace < 0 ? -TetElement.VonMises(stress) : TetElement.VonMises(stress);

    /// <summary>
    /// The equivalent FULLY REVERSED amplitude for a cycle of (<paramref name="amplitude"/>,
    /// <paramref name="meanStress"/>) — the amplitude whose R = -1 life equals the cycle's,
    /// which is what lets an R = -1 S-N curve answer for any mean.
    ///
    /// <para>At zero mean both corrections are the identity EXACTLY — a mean of zero or
    /// below takes the uncorrected branch, so the amplitude comes back verbatim rather
    /// than through a division that happens to be by one. A compressive mean takes
    /// <b>no benefit by default</b>: compression
    /// genuinely retards crack growth, but crediting it requires confidence that the mean
    /// stays compressive over the whole service life and at the surface where cracks
    /// start — residual-stress relaxation alone can void it — so every standard tool
    /// defaults to "amplitude unchanged" on the compressive side and so does this. A mean
    /// at or beyond the ultimate strength has failed statically, not in fatigue:
    /// the equivalent amplitude is +infinity, whose life is the floor value (see
    /// <see cref="FatigueResults.Log10Life"/>).</para>
    /// </summary>
    public static double EquivalentAlternating(
        SnCurve curve, double amplitude, double meanStress, MeanStressCorrection correction)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (amplitude < 0)
            throw new ArgumentOutOfRangeException(
                nameof(amplitude), amplitude, "An amplitude is non-negative by construction.");
        if (correction == MeanStressCorrection.None || meanStress <= 0)
            return amplitude;

        double ratio = meanStress / curve.UltimateStrength;
        double divisor = correction == MeanStressCorrection.Goodman
            ? 1.0 - ratio
            : 1.0 - ratio * ratio;
        if (!(divisor > 0))
            return double.PositiveInfinity;   // static failure: mean at or beyond S_ut
        return amplitude / divisor;
    }

    /// <summary>
    /// The allowable alternating stress (MPa) at a given mean — the mean-stress line
    /// itself, anchored at the design strength on the alternating axis and at the ultimate
    /// strength on the mean axis. At <paramref name="meanStress"/> = 0 it is exactly the
    /// design strength; at S_ut it is exactly zero (the divisor's complement is the
    /// literal 0.0) — the two structural identities the tests pin.
    /// </summary>
    /// <param name="curve">The material.</param>
    /// <param name="meanStress">Mean stress, MPa. A compressive mean earns no credit: the
    /// allowable stays at the design strength (see
    /// <see cref="EquivalentAlternating"/>).</param>
    /// <param name="correction">Which line.</param>
    /// <param name="designLife">See <see cref="FatigueOptions.DesignLife"/>.</param>
    public static double AllowableAmplitude(
        SnCurve curve, double meanStress, MeanStressCorrection correction,
        double? designLife = null)
    {
        ArgumentNullException.ThrowIfNull(curve);
        double strength = DesignStrength(curve, designLife);
        if (correction == MeanStressCorrection.None || meanStress <= 0)
            return strength;
        double ratio = meanStress / curve.UltimateStrength;
        double factor = correction == MeanStressCorrection.Goodman
            ? 1.0 - ratio
            : 1.0 - ratio * ratio;
        return strength * Math.Max(0, factor);
    }

    /// <summary>The alternating strength the safety factor measures against: the curve at
    /// the stated design life, or at the endurance knee — and a material with no endurance
    /// limit MUST state a design life, refused by name.</summary>
    internal static double DesignStrength(SnCurve curve, double? designLife)
    {
        double? life = designLife ?? curve.EnduranceLife;
        if (life is not { } cycles)
            throw new FeaException(
                $"'{curve.Name}' has no endurance limit, so a safety factor against infinite "
                + "life does not exist for it. State FatigueOptions.DesignLife — the cycle "
                + "count the part must survive — and the factor is measured against the "
                + "curve's strength there.");
        return curve.StressAt(cycles);
    }

    /// <summary>
    /// Evaluates fatigue at every node from two static load cases — the extremes of one
    /// proportional load history, in either order (the maximum and minimum are taken PER
    /// NODE, so which case is "worse" may differ across the part).
    ///
    /// <para>The two results must be on the same <see cref="AnalysisMesh"/> instance
    /// (<see cref="StructuralSolver.SolveAll"/> returns exactly such a pair from one
    /// factorization) and must answer with the same stress recovery and averaging —
    /// mixing a <see cref="StressRecovery.Direct"/> field with a
    /// <see cref="StressRecovery.Superconvergent"/> one would difference two fields of
    /// different accuracy and book the gap as alternating stress.</para>
    /// </summary>
    public static FatigueResults Evaluate(
        StructuralResults a,
        StructuralResults b,
        SnCurve curve,
        FatigueOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(curve);
        options ??= new FatigueOptions();

        if (!ReferenceEquals(a.Mesh, b.Mesh))
            throw new FeaException(
                "The two load cases were solved on different AnalysisMesh instances, so "
                + "their nodes do not correspond and a per-node stress cycle would pair "
                + "unrelated points. Solve both cases over one mesh — "
                + "StructuralSolver.SolveAll returns them from one factorization.");
        if (a.Recovery != b.Recovery || a.Averaging != b.Averaging)
            throw new FeaException(
                $"The two results answer with different nodal stress settings "
                + $"({a.Recovery}/{a.Averaging} against {b.Recovery}/{b.Averaging}). "
                + "Differencing fields of different accuracy books the recovery gap as "
                + "alternating stress; set the same Recovery and Averaging on both.");

        // The safety factor's strength is resolved up front so a curve that cannot answer
        // (no endurance limit, no design life) refuses at the call rather than on the
        // first field read.
        double designStrength = DesignStrength(curve, options.DesignLife);

        var stressA = a.NodalStress;
        var stressB = b.NodalStress;
        int nodes = a.Mesh.NodeCount;
        var alternating = new double[nodes];
        var mean = new double[nodes];
        var corrected = new double[nodes];
        var safety = new double[nodes];
        var log10Life = new double[nodes];

        for (int v = 0; v < nodes; v++)
        {
            double s1 = SignedVonMises(stressA[v]);
            double s2 = SignedVonMises(stressB[v]);
            double max = Math.Max(s1, s2);
            double min = Math.Min(s1, s2);
            double amp = 0.5 * (max - min);
            double m = 0.5 * (max + min);
            alternating[v] = amp;
            mean[v] = m;

            double equivalent = EquivalentAlternating(curve, amp, m, options.Correction);
            corrected[v] = equivalent;

            // Life. Infinite life is NaN — the established "no value" spelling (the VTU
            // writer's own convention, which FieldRange.Of already skips when ranging), so
            // a part that mostly lives forever still gets a usable legend over the nodes
            // that do not. A life below ONE cycle — including the static-failure branch,
            // where the corrected amplitude is +infinity — is floored at 1 (log10 = 0):
            // a log field must stay finite because ranging skips only NaN and -infinity
            // would poison the legend's minimum, sub-cycle "lives" are outside Basquin's
            // high-cycle validity anyway, and the floor errs conservative.
            double life = double.IsPositiveInfinity(equivalent) ? 0 : curve.LifeAt(equivalent);
            log10Life[v] = double.IsPositiveInfinity(life)
                ? double.NaN
                : Math.Log10(Math.Max(1.0, life));

            safety[v] = SafetyFactor(curve, designStrength, amp, m, options.Correction);
        }

        return new FatigueResults(
            a, b, curve, options, designStrength,
            alternating, mean, corrected, safety, log10Life);
    }

    /// <summary>
    /// The safety factor is the LOAD MULTIPLIER to the mean-stress line — the radial
    /// (proportional-loading) factor: scale the whole history by n and both the amplitude
    /// and the mean scale with it, so n solves
    /// <c>n·amp/strength + n·mean/S_ut = 1</c> (Goodman) or
    /// <c>n·amp/strength + (n·mean/S_ut)² = 1</c> (Gerber). That definition is also its
    /// own oracle: re-solving the cases with the loads scaled by n puts the critical node
    /// exactly ON the line, which the tests assert.
    /// <para>A compressive mean contributes nothing (no benefit, no penalty — the term is
    /// dropped, matching <see cref="EquivalentAlternating"/>'s rule). A node with zero
    /// amplitude AND no tensile mean has no fatigue mechanism at all, so its factor is
    /// NaN — the no-value spelling — rather than an infinity that would poison a
    /// legend's range.</para>
    /// </summary>
    private static double SafetyFactor(
        SnCurve curve, double designStrength, double amplitude, double meanStress,
        MeanStressCorrection correction)
    {
        double a = amplitude / designStrength;
        double m = correction == MeanStressCorrection.None
            ? 0
            : Math.Max(0, meanStress) / curve.UltimateStrength;
        if (a == 0 && m == 0)
            return double.NaN;
        if (correction != MeanStressCorrection.Gerber || m == 0)
            return 1.0 / (a + m);
        // Gerber: m²·n² + a·n − 1 = 0, positive root. a = 0 collapses to n = 1/m — the
        // static edge of the parabola — through the same formula.
        return (-a + Math.Sqrt(a * a + 4 * m * m)) / (2 * m * m);
    }
}

/// <summary>
/// The answer: per-node alternating/mean stress, safety factor and life, plus the two
/// ways out every results type here has — <see cref="Fields"/> over the analysis nodes and
/// <see cref="SampleOnto(HalfEdgeMesh)"/> onto a display mesh.
/// </summary>
public sealed class FatigueResults
{
    private readonly double[] _alternating;
    private readonly double[] _mean;
    private readonly double[] _corrected;
    private readonly double[] _safety;
    private readonly double[] _log10Life;

    internal FatigueResults(
        StructuralResults caseA,
        StructuralResults caseB,
        SnCurve curve,
        FatigueOptions options,
        double designStrength,
        double[] alternating,
        double[] mean,
        double[] corrected,
        double[] safety,
        double[] log10Life)
    {
        CaseA = caseA;
        CaseB = caseB;
        Curve = curve;
        Options = options;
        DesignStrength = designStrength;
        _alternating = alternating;
        _mean = mean;
        _corrected = corrected;
        _safety = safety;
        _log10Life = log10Life;
        AlternatingStress = Array.AsReadOnly(alternating);
        MeanStress = Array.AsReadOnly(mean);
        SafetyFactor = Array.AsReadOnly(safety);
        Log10Life = Array.AsReadOnly(log10Life);
    }

    /// <summary>One extreme of the load history.</summary>
    public StructuralResults CaseA { get; }

    /// <summary>The other extreme.</summary>
    public StructuralResults CaseB { get; }

    /// <summary>The S-N curve evaluated against.</summary>
    public SnCurve Curve { get; }

    /// <summary>The settings used.</summary>
    public FatigueOptions Options { get; }

    /// <summary>The analysis mesh both cases share.</summary>
    public AnalysisMesh Mesh => CaseA.Mesh;

    /// <summary>The alternating strength (MPa) the safety factor measures against — the
    /// curve at the design life, or the endurance limit when none was stated.</summary>
    public double DesignStrength { get; }

    /// <summary>Per-node alternating stress amplitude, MPa: half the signed von Mises
    /// swing between the two cases.</summary>
    public IReadOnlyList<double> AlternatingStress { get; }

    /// <summary>Per-node mean stress, MPa (signed — negative is compressive).</summary>
    public IReadOnlyList<double> MeanStress { get; }

    /// <summary>Per-node safety factor: the multiplier on the whole load history that
    /// reaches the mean-stress line (NaN where there is no fatigue mechanism — zero
    /// amplitude and no tensile mean).</summary>
    public IReadOnlyList<double> SafetyFactor { get; }

    /// <summary>
    /// Per-node life as <b>log10(cycles)</b>. NaN is infinite life — at or below the
    /// endurance limit, or zero amplitude — the "no value" spelling ranging already skips;
    /// 0 is the one-cycle floor (see <see cref="FatigueAnalysis.Evaluate"/>).
    /// <para>Published as the log10 rather than as raw cycles because lives spread over
    /// many decades and the colour pipeline's range mapping is linear — raw cycles would
    /// spend the whole legend on the longest-lived node. A native log-scale display mode
    /// is filed in todo.md; the units string says what the numbers are meanwhile.</para>
    /// </summary>
    public IReadOnlyList<double> Log10Life { get; }

    /// <summary>The equivalent fully reversed amplitude at one node (MPa) — what the
    /// mean-stress correction turned the cycle into, and +infinity on the static-failure
    /// branch. Exposed for verification; not published as a field.</summary>
    public double CorrectedAmplitudeAt(int node) => _corrected[node];

    /// <summary>The smallest safety factor over all nodes (NaN entries skipped), or NaN
    /// when no node carries a fatigue cycle at all.</summary>
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

    /// <summary>The shortest life over all nodes as log10(cycles), or NaN when every node
    /// lives forever.</summary>
    public double MinLog10Life
    {
        get
        {
            double best = double.NaN;
            foreach (double n in _log10Life)
            {
                if (!double.IsNaN(n) && !(n >= best))
                    best = n;
            }
            return best;
        }
    }

    /// <summary>How many nodes have a finite predicted life.</summary>
    public int FiniteLifeNodeCount
    {
        get
        {
            int count = 0;
            foreach (double n in _log10Life)
            {
                if (!double.IsNaN(n))
                    count++;
            }
            return count;
        }
    }

    /// <summary>The field names this class produces — so a <c>FieldDisplay</c> and this
    /// post-processor cannot disagree about a spelling.</summary>
    public static class FieldNames
    {
        /// <summary>Per-node safety factor (dimensionless).</summary>
        public const string SafetyFactor = "Fatigue safety factor";

        /// <summary>Per-node life, log10(cycles); NaN = infinite.</summary>
        public const string Life = "Fatigue life";

        /// <summary>Per-node alternating stress amplitude, MPa.</summary>
        public const string Alternating = "Alternating stress";

        /// <summary>Per-node mean stress, MPa.</summary>
        public const string Mean = "Mean stress";
    }

    /// <summary>The results as <see cref="MeshField"/>s over the analysis mesh's nodes.
    /// To colour a <c>Part</c>, use <see cref="SampleOnto(HalfEdgeMesh)"/>.</summary>
    /// <param name="stressUnits">Unit label for the stress fields (default "MPa").</param>
    public IReadOnlyList<MeshField> Fields(string stressUnits = "MPa") =>
    [
        MeshField.Scalar(FieldNames.SafetyFactor, "", _safety),
        MeshField.Scalar(FieldNames.Life, "log10(cycles)", _log10Life),
        MeshField.Scalar(FieldNames.Alternating, stressUnits, _alternating),
        MeshField.Scalar(FieldNames.Mean, stressUnits, _mean),
    ];

    /// <summary>The results resampled onto a display mesh — exact where the meshes share
    /// a vertex, closest-boundary-facet interpolation otherwise (the
    /// <see cref="StructuralResults.SampleOnto(HalfEdgeMesh, out double, string, string)"/>
    /// contract; see there for <paramref name="maxSampleDistance"/>).</summary>
    public IReadOnlyList<MeshField> SampleOnto(
        HalfEdgeMesh displayMesh, out double maxSampleDistance, string stressUnits = "MPa")
    {
        ArgumentNullException.ThrowIfNull(displayMesh);
        var sampler = SurfaceSampler.Build(Mesh, displayMesh);
        maxSampleDistance = sampler.MaxSampleDistance;
        return
        [
            MeshField.Scalar(FieldNames.SafetyFactor, "", sampler.Sample(_safety)),
            MeshField.Scalar(FieldNames.Life, "log10(cycles)", sampler.Sample(_log10Life)),
            MeshField.Scalar(FieldNames.Alternating, stressUnits, sampler.Sample(_alternating)),
            MeshField.Scalar(FieldNames.Mean, stressUnits, sampler.Sample(_mean)),
        ];
    }

    /// <summary><see cref="SampleOnto(HalfEdgeMesh, out double, string)"/> without the
    /// sampling-distance diagnostic.</summary>
    public IReadOnlyList<MeshField> SampleOnto(HalfEdgeMesh displayMesh) =>
        SampleOnto(displayMesh, out _);

    /// <inheritdoc/>
    public override string ToString()
    {
        int node = MinSafetyFactorNode;
        string factor = node >= 0
            ? $"min safety factor {_safety[node]:G4} at node {node}"
            : "no node carries a fatigue cycle";
        return $"{Curve.Name}, {Options.Correction}: {factor}; "
            + $"{FiniteLifeNodeCount:N0} of {_log10Life.Length:N0} nodes with finite life";
    }
}
