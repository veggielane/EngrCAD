using System.Numerics;
using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Fea;

/// <summary>
/// The steady-state response to a harmonic load over a frequency sweep.
///
/// <para><b>What is stored is the MODAL coordinates, not the fields.</b> A sweep of 500
/// frequencies over a 100 000-node model is 1.2 GB of complex nodal displacement and about
/// 40 kB of modal coordinates, and every field is one weighted sum of mode shapes away — so
/// the small thing is kept and the large ones are produced on request. It also means
/// <see cref="ResponseAt(int, int)"/>, the transfer function at one degree of freedom over the
/// whole sweep, costs one pass over the modes rather than a reconstruction of every field.</para>
///
/// <para><b>The response is COMPLEX and the phase is part of the answer.</b> A magnitude
/// alone cannot say that a point moves against the load rather than with it, which is what
/// happens above a resonance and what makes a two-mode response smaller than either mode's
/// contribution between them. <see cref="AmplitudeAt(int)"/> is the magnitude per component
/// for plotting; <see cref="ResponseAt(int, int)"/> keeps the phase.</para>
/// </summary>
public sealed class HarmonicResponse
{
    private readonly double[] _frequencies;
    private readonly double[] _ratios;
    private readonly double[] _modalForces;
    private readonly Complex[][] _coordinates;
    private readonly Vector3d[]? _static;

    internal HarmonicResponse(
        ModalResults modes,
        double[] frequencies,
        double[] ratios,
        double[] modalForces,
        Complex[][] coordinates,
        Vector3d[]? staticDisplacement,
        double truncationError,
        string dampingDescription,
        bool relativeToBase = false)
    {
        Modes = modes;
        _frequencies = frequencies;
        _ratios = ratios;
        _modalForces = modalForces;
        _coordinates = coordinates;
        _static = staticDisplacement;
        TruncationError = truncationError;
        DampingDescription = dampingDescription;
        IsRelativeToBase = relativeToBase;
    }

    /// <summary>
    /// True when the response is a base (support) excitation's, so the displacement is measured
    /// RELATIVE to the moving support rather than absolutely. That is the right quantity for
    /// STRESS (a rigid ground motion carries none); the absolute displacement is this plus the
    /// rigid ground field, which a caller adds if they need it. False for the ordinary
    /// nodal-force response, whose displacement is absolute.
    /// </summary>
    public bool IsRelativeToBase { get; }

    /// <summary>The modal basis the response was built on.</summary>
    public ModalResults Modes { get; }

    /// <summary>The model — the modal solve's own, which is also where the load came from.</summary>
    public StructuralModel Model => Modes.Model;

    /// <summary>The analysis mesh.</summary>
    public AnalysisMesh Mesh => Modes.Mesh;

    /// <summary>The frequencies evaluated, in Hz.</summary>
    public IReadOnlyList<double> Frequencies => _frequencies;

    /// <summary>The damping ratio used for each mode, in mode order — reported because a
    /// Rayleigh curve's value at a mode nobody looked at is exactly the kind of thing that
    /// silently over-damps a response.</summary>
    public IReadOnlyList<double> DampingRatios => _ratios;

    /// <summary>The modal force <c>F_n = phi_n' f</c> for each mode. A mode with a modal force
    /// of zero is not excited by this load at all, however close its frequency — which is why
    /// a resonance can be missing from a response that "should" show it.</summary>
    public IReadOnlyList<double> ModalForces => _modalForces;

    /// <summary>A description of the damping model, for a report.</summary>
    public string DampingDescription { get; }

    /// <summary>True when the mode-acceleration correction was applied.</summary>
    public bool HasStaticCorrection => _static is not null;

    /// <summary>
    /// The relative difference at zero frequency between the plain modal sum and the true
    /// static response — exactly the flexibility the extracted modes leave out.
    /// <b>NaN when no static solve was supplied</b>, which is honest: without one the
    /// truncation error is not small, it is unknown.
    /// </summary>
    public double TruncationError { get; }

    /// <summary>
    /// The mode's own dynamic coordinate <c>q_n(W) = F_n / (w_n² - W² + 2i·zeta_n·w_n·W)</c>
    /// at one sweep point.
    ///
    /// <para><b>Deliberately the RAW coordinate, uncorrected.</b> The mode-acceleration
    /// correction subtracts each kept mode's static term and adds the true static response
    /// back as a whole, which is a statement about the SUM rather than about any one mode — so
    /// folding it into a per-mode coordinate would make <c>q_n</c> stop being the answer to
    /// <c>q_n'' + 2·zeta·w_n·q_n' + w_n²·q_n = F_n</c>, which is the only thing it means.
    /// <see cref="ResponseAt(int, int)"/> and <see cref="DisplacementAt(int)"/> carry the
    /// correction.</para>
    /// </summary>
    /// <param name="frequencyIndex">Index into <see cref="Frequencies"/>.</param>
    /// <param name="modeNumber">1-based mode number.</param>
    public Complex ModalCoordinate(int frequencyIndex, int modeNumber)
    {
        RequireFrequency(frequencyIndex);
        if (modeNumber < 1 || modeNumber > _modalForces.Length)
            throw new ArgumentOutOfRangeException(
                nameof(modeNumber), modeNumber, $"Modes are numbered 1 to {_modalForces.Length}.");
        return _coordinates[frequencyIndex][modeNumber - 1];
    }

    /// <summary>
    /// The complex displacement of one node's one axis across the WHOLE sweep — the transfer
    /// function, and the thing a frequency-response plot is made of.
    /// </summary>
    /// <param name="node">Analysis-mesh node index.</param>
    /// <param name="axis">0 = X, 1 = Y, 2 = Z.</param>
    public Complex[] ResponseAt(int node, int axis)
    {
        RequireNode(node);
        if ((uint)axis >= 3)
            throw new ArgumentOutOfRangeException(nameof(axis), axis, "Axis is 0 (X), 1 (Y) or 2 (Z).");

        var response = new Complex[_frequencies.Length];
        for (int k = 0; k < _frequencies.Length; k++)
        {
            var sum = Complex.Zero;
            for (int i = 0; i < _modalForces.Length; i++)
            {
                var mode = Modes.Modes[i];
                double component = Component(mode.ShapeAt(node), axis);
                // Exact-zero skip: a restrained degree of freedom has a zero shape in every
                // mode, so its whole transfer function is zero and there is nothing to add.
                if (component == 0)
                    continue;
                sum += component * Coordinate(k, i);
            }
            if (_static is { } exact)
                sum += Component(exact[node], axis);
            response[k] = sum;
        }
        return response;
    }

    /// <summary>
    /// The complex displacement at every node for one sweep point.
    /// </summary>
    /// <param name="frequencyIndex">Index into <see cref="Frequencies"/>.</param>
    public (Vector3d Real, Vector3d Imaginary)[] DisplacementAt(int frequencyIndex)
    {
        RequireFrequency(frequencyIndex);
        int nodes = Mesh.NodeCount;
        var real = new Vector3d[nodes];
        var imaginary = new Vector3d[nodes];
        for (int i = 0; i < _modalForces.Length; i++)
        {
            var q = Coordinate(frequencyIndex, i);
            // Exact-zero skip: an unexcited mode contributes nothing at any frequency.
            if (q == Complex.Zero)
                continue;
            var mode = Modes.Modes[i];
            for (int v = 0; v < nodes; v++)
            {
                var shape = mode.ShapeAt(v);
                real[v] += shape * q.Real;
                imaginary[v] += shape * q.Imaginary;
            }
        }
        if (_static is { } exact)
        {
            for (int v = 0; v < nodes; v++)
                real[v] += exact[v];
        }

        var result = new (Vector3d, Vector3d)[nodes];
        for (int v = 0; v < nodes; v++)
            result[v] = (real[v], imaginary[v]);
        return result;
    }

    /// <summary>
    /// The displacement AMPLITUDE per component at one sweep point — the magnitude of each
    /// complex component, which is what a colour map and a plot want. Note that the vector of
    /// component amplitudes is NOT the amplitude of the vector: the three components peak at
    /// different instants unless they share a phase.
    /// </summary>
    public Vector3d[] AmplitudeAt(int frequencyIndex)
    {
        var complex = DisplacementAt(frequencyIndex);
        var amplitude = new Vector3d[complex.Length];
        for (int v = 0; v < complex.Length; v++)
        {
            var (re, im) = complex[v];
            amplitude[v] = new Vector3d(
                Math.Sqrt(re.X * re.X + im.X * im.X),
                Math.Sqrt(re.Y * re.Y + im.Y * im.Y),
                Math.Sqrt(re.Z * re.Z + im.Z * im.Z));
        }
        return amplitude;
    }

    /// <summary>The largest nodal component amplitude at one sweep point — the scalar a sweep
    /// is usually plotted as.</summary>
    public double PeakAmplitudeAt(int frequencyIndex)
    {
        double peak = 0;
        foreach (var a in AmplitudeAt(frequencyIndex))
            peak = Math.Max(peak, Math.Max(a.X, Math.Max(a.Y, a.Z)));
        return peak;
    }

    /// <summary>The index of the sweep point carrying the largest peak amplitude — the
    /// resonance the sweep actually found, which is not always the mode nearest the peak.</summary>
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

    // ---- publishing -----------------------------------------------------------------

    /// <summary>The field names this class produces.</summary>
    public static class FieldNames
    {
        /// <summary>What every response field name starts with.</summary>
        public const string Prefix = "Response ";

        /// <summary>The vector-field name for a sweep point. The FREQUENCY is in the name
        /// here, unlike a mode shape's — a response field is meaningless without it, and a
        /// sweep is a set of answers to different questions rather than one model property
        /// that a document handle should survive a parameter edit.</summary>
        public static string Amplitude(double hertz) => $"{Prefix}{hertz:G6} Hz";
    }

    /// <summary>One sweep point's displacement amplitude as a <see cref="MeshField"/> over the
    /// ANALYSIS mesh's nodes. Unlike a mode shape this is a real displacement in model units,
    /// because a harmonic response has an amplitude the load sets.</summary>
    public IReadOnlyList<MeshField> Fields(int frequencyIndex, string lengthUnits = "mm")
    {
        RequireFrequency(frequencyIndex);
        return [MeshField.Vector(
            FieldNames.Amplitude(_frequencies[frequencyIndex]),
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
            FieldNames.Amplitude(_frequencies[frequencyIndex]),
            lengthUnits,
            sampler.Sample(AmplitudeAt(frequencyIndex)))];
    }

    /// <summary>
    /// The sweep as comma-separated text: frequency, peak amplitude, and the amplitude and
    /// phase of one probed degree of freedom. What a plot is made from, and the honest way to
    /// hand a frequency response to anything outside this project.
    /// </summary>
    /// <param name="node">The node to probe.</param>
    /// <param name="axis">0 = X, 1 = Y, 2 = Z.</param>
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

    /// <summary>A readable summary — the modes, their damping, and where the sweep peaked.</summary>
    public string ToText()
    {
        var lines = new List<string>
        {
            $"{_frequencies.Length} frequencies from {_frequencies[0]:N2} to "
                + $"{_frequencies[^1]:N2} Hz, {_modalForces.Length} modes, {DampingDescription}"
                + (HasStaticCorrection
                    ? $", mode-acceleration corrected (truncation error {TruncationError:P3})"
                    : ""),
        };
        for (int i = 0; i < _modalForces.Length; i++)
        {
            var mode = Modes.Modes[i];
            lines.Add(
                $"mode {mode.Number,2}: {mode.Frequency,12:N2} Hz   zeta {_ratios[i]:P3}   "
                + $"modal force {_modalForces[i]:G6}");
        }
        int peak = PeakFrequencyIndex;
        lines.Add(
            $"peak response {PeakAmplitudeAt(peak):G6} at {_frequencies[peak]:N2} Hz");
        return string.Join(Environment.NewLine, lines);
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{_frequencies.Length} frequencies, {_modalForces.Length} modes, {DampingDescription}";

    private Complex Coordinate(int frequencyIndex, int modeIndex)
    {
        var q = _coordinates[frequencyIndex][modeIndex];
        if (_static is null)
            return q;
        // The mode-acceleration form: the static answer carries every mode's zero-frequency
        // contribution already, so each kept mode contributes only the DIFFERENCE between its
        // dynamic and its static term. The bracket vanishes at zero frequency by construction,
        // which is what makes the corrected response exactly static there.
        return q - _modalForces[modeIndex] / Modes.Modes[modeIndex].Eigenvalue;
    }

    private static double Component(Vector3d v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    private void RequireFrequency(int index)
    {
        if ((uint)index >= (uint)_frequencies.Length)
            throw new ArgumentOutOfRangeException(
                nameof(index), index, $"The sweep has {_frequencies.Length} frequencies.");
    }

    private void RequireNode(int node)
    {
        if ((uint)node >= (uint)Mesh.NodeCount)
            throw new ArgumentOutOfRangeException(
                nameof(node), node, $"The analysis mesh has {Mesh.NodeCount} nodes.");
    }
}
