using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Fea;

/// <summary>
/// One natural mode of vibration: a frequency and the shape the body moves in at it.
///
/// <para><b>A mode shape has no amplitude and no sign.</b> <c>phi</c> and <c>-2.7·phi</c>
/// solve <c>K phi = lambda M phi</c> equally, so any particular vector is a
/// representative of a ray, not a displacement. Two conventions make the answer
/// reproducible without pretending otherwise: <see cref="Shape"/> is MASS-NORMALISED
/// (<c>phi' M phi = 1</c>), which is the scale every modal identity in the literature is
/// stated in and the one <see cref="EffectiveMass"/> needs; and the sign is pinned by
/// making the largest-magnitude component positive, so re-solving the same model twice
/// gives the same vector. Neither is physics.</para>
/// </summary>
public sealed class VibrationMode
{
    private readonly Vector3d[] _shape;

    internal VibrationMode(
        int number,
        double eigenvalue,
        Vector3d[] shape,
        double residual,
        Vector3d participation,
        double peak)
    {
        Number = number;
        Eigenvalue = eigenvalue;
        _shape = shape;
        Residual = residual;
        ParticipationFactor = participation;
        PeakDisplacement = peak;
    }

    /// <summary>Which mode this is, counting the lowest ELASTIC mode as 1. Rigid-body
    /// modes are not numbered here — they are reported separately, by
    /// <see cref="ModalResults.RigidBodyModes"/>.</summary>
    public int Number { get; }

    /// <summary>The eigenvalue <c>lambda = omega²</c>, in 1/time² for the model's own
    /// consistent unit system.</summary>
    public double Eigenvalue { get; }

    /// <summary>The circular natural frequency <c>omega = sqrt(lambda)</c> — rad/s in the
    /// mm/N/tonne system, where <c>tonne = N·s²/mm</c> makes the units come out in
    /// seconds with no conversion.</summary>
    public double AngularFrequency => Math.Sqrt(Math.Max(0, Eigenvalue));

    /// <summary>The natural frequency in cycles per unit time — Hz in the mm/N/tonne
    /// system.</summary>
    public double Frequency => AngularFrequency / (2.0 * Math.PI);

    /// <summary>The natural period, the reciprocal of <see cref="Frequency"/>. Infinite for
    /// a zero frequency.</summary>
    public double Period =>
        // Exact-zero division guard (the scale-free tier): a mode at exactly zero frequency
        // has an infinite period, which is the right answer rather than a small number to be
        // protected against.
        Frequency == 0 ? double.PositiveInfinity : 1.0 / Frequency;

    /// <summary>The mode shape at every node, MASS-NORMALISED (<c>phi' M phi = 1</c>). Its
    /// magnitude is one over the square root of a mass, so it is not a displacement; see
    /// <see cref="ModalResults.Fields(int)"/> for the display-scaled form.</summary>
    public IReadOnlyList<Vector3d> Shape => _shape;

    /// <summary>Shape at one node.</summary>
    public Vector3d ShapeAt(int node) => _shape[node];

    /// <summary>The MEASURED relative residual
    /// <c>|K phi - lambda M phi| / (|K phi| + |lambda||M phi|)</c> — not a bound, and not
    /// the eigensolver's internal estimate.</summary>
    public double Residual { get; }

    /// <summary>
    /// The modal participation factor per global axis, <c>Gamma_d = phi' M iota_d</c> with
    /// <c>iota_d</c> the unit rigid translation along that axis over the FREE degrees of
    /// freedom. How strongly a base acceleration along each axis excites this mode.
    /// </summary>
    public Vector3d ParticipationFactor { get; }

    /// <summary>
    /// The modal effective mass per global axis, <c>Gamma_d²</c> — the mass that actually
    /// participates in this mode. Summed over ALL modes it recovers
    /// <see cref="ModalResults.ParticipatingMass"/> exactly, which is what makes "have I
    /// extracted enough modes?" a question with a numeric answer.
    /// </summary>
    public Vector3d EffectiveMass => new(
        ParticipationFactor.X * ParticipationFactor.X,
        ParticipationFactor.Y * ParticipationFactor.Y,
        ParticipationFactor.Z * ParticipationFactor.Z);

    /// <summary>The largest nodal magnitude of <see cref="Shape"/> — the number the
    /// published field is divided by, so that its peak displacement is exactly 1.</summary>
    public double PeakDisplacement { get; }

    /// <inheritdoc/>
    public override string ToString() =>
        $"mode {Number}: {Frequency:G6} Hz (lambda {Eigenvalue:G6}, residual {Residual:E2})";
}

/// <summary>
/// A rigid-body mode: a motion the supports permit at zero strain energy, and therefore at
/// zero frequency.
///
/// <para><b>These are an answer, not an error.</b> A free-free body has six of them and a
/// partially restrained one has as many as its supports leave; they are exact solutions of
/// <c>K phi = 0·M phi</c> and every modal analysis has to report them. What it must NOT do
/// is present them as the first six structural modes, which is why they live in their own
/// list and <see cref="VibrationMode.Number"/> starts at 1 on the lowest ELASTIC mode.</para>
///
/// <para><see cref="Eigenvalue"/> is the MEASURED Rayleigh quotient
/// <c>phi'K phi / phi'M phi</c> of the exact rigid field, so it is a number about this
/// model's conditioning rather than a constant: in exact arithmetic it is zero, and what it
/// actually reads is how much round-off the assembled stiffness carries.</para>
/// </summary>
public sealed class RigidBodyMode
{
    private readonly Vector3d[] _shape;

    internal RigidBodyMode(int body, string description, double eigenvalue, Vector3d[] shape)
    {
        Body = body;
        Description = description;
        Eigenvalue = eigenvalue;
        _shape = shape;
    }

    /// <summary>Zero-based index of the connected body this motion belongs to.</summary>
    public int Body { get; }

    /// <summary>A readable name — "translation along (0, 0, 1)", or "rotation about the axis
    /// through (50, 5, 5) along (1, 0, 0)". The same description
    /// <see cref="StructuralSolver"/> uses when it REFUSES such a model.</summary>
    public string Description { get; }

    /// <summary>The measured Rayleigh quotient of the rigid field: zero in exact arithmetic,
    /// and a conditioning measurement in practice.</summary>
    public double Eigenvalue { get; }

    /// <summary>The corresponding frequency, <c>sqrt(max(0, lambda))/2pi</c>. A tiny
    /// positive number rather than an exact zero, and reported as measured.</summary>
    public double Frequency => Math.Sqrt(Math.Max(0, Eigenvalue)) / (2.0 * Math.PI);

    /// <summary>The motion as a displacement field over every node, normalised to a peak
    /// nodal magnitude of 1.</summary>
    public IReadOnlyList<Vector3d> Shape => _shape;

    /// <inheritdoc/>
    public override string ToString() =>
        $"rigid body mode: {Description} (lambda {Eigenvalue:E2}, {Frequency:E2} Hz)";
}

/// <summary>
/// The answer of a modal analysis: the natural frequencies, the shapes they move in, the
/// rigid-body modes kept separate from them, and the two ways out — a
/// <see cref="MeshField"/> per mode for the document model and a <c>.vtu</c> file carrying
/// every mode at once.
/// </summary>
public sealed class ModalResults
{
    private readonly VibrationMode[] _modes;
    private readonly RigidBodyMode[] _rigid;

    internal ModalResults(
        StructuralModel model,
        VibrationMode[] modes,
        RigidBodyMode[] rigid,
        double totalMass,
        Vector3d participatingMass,
        ModalSolveReport report)
    {
        Model = model;
        _modes = modes;
        _rigid = rigid;
        TotalMass = totalMass;
        ParticipatingMass = participatingMass;
        Report = report;
    }

    /// <summary>The model that was solved.</summary>
    public StructuralModel Model { get; }

    /// <summary>The analysis mesh.</summary>
    public AnalysisMesh Mesh => Model.Mesh;

    /// <summary>What the solve did.</summary>
    public ModalSolveReport Report { get; }

    /// <summary>The elastic modes, ascending in frequency.</summary>
    public IReadOnlyList<VibrationMode> Modes => _modes;

    /// <summary>
    /// The rigid-body modes the supports permit — six for a free-free body, none for a
    /// fully restrained one. Deliberately NOT part of <see cref="Modes"/>: they carry no
    /// stiffness, their "frequency" is round-off, and counting them among the structural
    /// modes is how a report comes to claim that a bracket's first natural frequency is
    /// 3e-4 Hz.
    /// </summary>
    public IReadOnlyList<RigidBodyMode> RigidBodyModes => _rigid;

    /// <summary>Natural frequencies of the elastic modes, ascending — the headline
    /// numbers.</summary>
    public IReadOnlyList<double> Frequencies => _modes.Select(m => m.Frequency).ToArray();

    /// <summary>The body's total mass, from the assembled mass matrix's own total (which is
    /// exactly <c>sum(rho·V)</c> over the elements) rather than recomputed from densities
    /// and volumes — the same arithmetic, asked once.</summary>
    public double TotalMass { get; }

    /// <summary>
    /// The mass a modal sum can account for, per axis: <c>iota_d' M_ff iota_d</c> over the
    /// FREE degrees of freedom. Equal to <see cref="TotalMass"/> for a free-free body and
    /// smaller wherever supports remove mass from the free system — which is why the modal
    /// effective masses sum to this and not to the total.
    /// </summary>
    public Vector3d ParticipatingMass { get; }

    /// <summary>The sum of every extracted mode's <see cref="VibrationMode.EffectiveMass"/>,
    /// per axis. Compare against <see cref="ParticipatingMass"/> to decide whether enough
    /// modes have been extracted; the usual bar is 90%.</summary>
    public Vector3d ExtractedEffectiveMass
    {
        get
        {
            var sum = Vector3d.Zero;
            foreach (var mode in _modes)
                sum += mode.EffectiveMass;
            return sum;
        }
    }

    /// <summary>The mode with a given 1-based number.</summary>
    public VibrationMode Mode(int number)
    {
        if (number < 1 || number > _modes.Length)
            throw new ArgumentOutOfRangeException(
                nameof(number), number,
                _modes.Length == 0
                    ? "No elastic modes were extracted."
                    : $"Modes are numbered 1 to {_modes.Length}; mode numbers count the lowest "
                      + "ELASTIC mode as 1, with rigid-body modes reported separately.");
        return _modes[number - 1];
    }

    // ---- publishing -----------------------------------------------------------------

    /// <summary>The field names this class produces — so a <c>FieldDisplay</c> and the
    /// solver cannot disagree about a spelling.</summary>
    public static class FieldNames
    {
        /// <summary>What every mode-shape field name starts with.</summary>
        public const string Prefix = "Mode ";

        /// <summary>The vector-field name for a 1-based mode number: "Mode 1", "Mode 2", …
        /// <para><b>The frequency is deliberately not in the name.</b> A field name is a
        /// document-level handle — a <c>FieldDisplay</c> stores it, a saved document
        /// round-trips it — and a name carrying a computed number would silently stop
        /// resolving the moment a wall thickness changed.</para></summary>
        public static string Shape(int number) => $"{Prefix}{number}";
    }

    /// <summary>The unit label mode-shape fields carry. Not a length: the values are scaled
    /// to a peak of 1 and their amplitude is a display choice, so labelling them "mm" would
    /// be a claim the analysis does not make.</summary>
    public const string ShapeUnits = "mode shape";

    /// <summary>
    /// One mode's shape as a <see cref="MeshField"/> over the ANALYSIS mesh's nodes,
    /// rescaled so its largest nodal magnitude is exactly 1 model length unit.
    ///
    /// <para><b>Why the published scale is not the mass-normalised one.</b>
    /// <see cref="VibrationMode.Shape"/> satisfies <c>phi' M phi = 1</c>, so its magnitude
    /// is one over the square root of a mass — it would plot a heavy part's mode as small
    /// and change if the density changed, neither of which says anything about the mode.
    /// Unit-peak scaling makes an amplitude of 1 mean "the most-displaced node moves one
    /// model unit", which is a number a viewer and a person can both reason about. The
    /// amplitude of an animation over it is a DISPLAY choice, not a predicted
    /// deflection.</para>
    /// </summary>
    /// <param name="number">1-based mode number.</param>
    public IReadOnlyList<MeshField> Fields(int number) =>
        [MeshField.Vector(FieldNames.Shape(number), ShapeUnits, ScaledShape(Mode(number)))];

    /// <summary>Every extracted mode's shape, one vector field each — what a
    /// <c>Part</c> carries so a <c>FieldDisplay</c> can switch between them.</summary>
    public IReadOnlyList<MeshField> AllFields()
    {
        var fields = new MeshField[_modes.Length];
        for (int i = 0; i < _modes.Length; i++)
            fields[i] = MeshField.Vector(
                FieldNames.Shape(_modes[i].Number), ShapeUnits, ScaledShape(_modes[i]));
        return fields;
    }

    /// <summary>
    /// One mode's shape resampled onto a display mesh — the step that closes the gap
    /// between the solver's vertex set and a <c>Part</c>'s. A display vertex whose position
    /// matches an analysis boundary node bit for bit takes that node's value directly;
    /// anything else interpolates on the nearest boundary facet with its own shape
    /// functions.
    /// </summary>
    /// <param name="displayMesh">The mesh to sample onto.</param>
    /// <param name="number">1-based mode number.</param>
    /// <param name="maxSampleDistance">The furthest any display vertex sat from the analysis
    /// boundary. Zero means every vertex matched exactly.</param>
    public IReadOnlyList<MeshField> SampleOnto(
        HalfEdgeMesh displayMesh, int number, out double maxSampleDistance)
    {
        ArgumentNullException.ThrowIfNull(displayMesh);
        var mode = Mode(number);
        var sampler = SurfaceSampler.Build(Mesh, displayMesh);
        maxSampleDistance = sampler.MaxSampleDistance;
        return [MeshField.Vector(
            FieldNames.Shape(number), ShapeUnits, sampler.Sample(ScaledShape(mode)))];
    }

    /// <summary><see cref="SampleOnto(HalfEdgeMesh, int, out double)"/> without the
    /// sampling-distance diagnostic.</summary>
    public IReadOnlyList<MeshField> SampleOnto(HalfEdgeMesh displayMesh, int number) =>
        SampleOnto(displayMesh, number, out _);

    /// <summary>
    /// EVERY mode's shape resampled onto a display mesh, in one pass. The correspondence is
    /// built once and shared, which is the whole reason this overload exists — calling the
    /// single-mode form in a loop rebuilds the BVH per mode.
    /// </summary>
    public IReadOnlyList<MeshField> SampleOnto(
        HalfEdgeMesh displayMesh, out double maxSampleDistance)
    {
        ArgumentNullException.ThrowIfNull(displayMesh);
        var sampler = SurfaceSampler.Build(Mesh, displayMesh);
        maxSampleDistance = sampler.MaxSampleDistance;
        var fields = new MeshField[_modes.Length];
        for (int i = 0; i < _modes.Length; i++)
            fields[i] = MeshField.Vector(
                FieldNames.Shape(_modes[i].Number),
                ShapeUnits,
                sampler.Sample(ScaledShape(_modes[i])));
        return fields;
    }

    /// <summary><see cref="SampleOnto(HalfEdgeMesh, out double)"/> without the diagnostic.</summary>
    public IReadOnlyList<MeshField> SampleOnto(HalfEdgeMesh displayMesh) =>
        SampleOnto(displayMesh, out _);

    private Vector3d[] ScaledShape(VibrationMode mode)
    {
        var scaled = new Vector3d[Mesh.NodeCount];
        // Exact-zero division guard: a shape with no displacement anywhere cannot happen
        // (it would not be M-normalised), but dividing by a peak read off a possibly empty
        // mesh is not something to leave to luck.
        double inverse = mode.PeakDisplacement > 0 ? 1.0 / mode.PeakDisplacement : 1.0;
        for (int v = 0; v < scaled.Length; v++)
            scaled[v] = mode.ShapeAt(v) * inverse;
        return scaled;
    }

    /// <summary>
    /// Writes the VOLUME mesh with EVERY mode as its own point-data array — linear elements
    /// as <c>VTK_TETRA</c>, quadratic as <c>VTK_QUADRATIC_TETRA</c>. ParaView's warp filter
    /// then animates whichever mode is selected.
    /// </summary>
    public void WriteVtu(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        int perElement = Mesh.NodesPerElement;
        var cells = new List<IReadOnlyList<int>>(Mesh.ElementCount);
        var types = new VtkCellType[Mesh.ElementCount];
        var type = Mesh.Order == ElementOrder.Linear ? VtkCellType.Tetra : VtkCellType.QuadraticTetra;
        for (int e = 0; e < Mesh.ElementCount; e++)
        {
            var nodes = Mesh.Element(e);
            var cell = new int[perElement];
            for (int i = 0; i < perElement; i++)
                cell[i] = nodes[i];
            cells.Add(cell);
            types[e] = type;
        }
        VtuWriter.Write(Mesh.Nodes, cells, types, AllFields(), writer);
    }

    /// <summary><see cref="WriteVtu(TextWriter)"/> to a file.</summary>
    public void WriteVtu(string path)
    {
        using var writer = new StreamWriter(path);
        WriteVtu(writer);
    }

    /// <summary>A readable table of the frequencies — what a report prints.</summary>
    public string ToText()
    {
        var lines = new List<string>();
        if (_rigid.Length > 0)
        {
            lines.Add($"{_rigid.Length} rigid-body mode{(_rigid.Length == 1 ? "" : "s")} at zero "
                + $"frequency (largest measured lambda {_rigid.Max(r => Math.Abs(r.Eigenvalue)):E2}):");
            foreach (var rigid in _rigid)
                lines.Add($"  {rigid.Description}");
        }
        foreach (var mode in _modes)
        {
            lines.Add(
                $"mode {mode.Number,2}: {mode.Frequency,12:N2} Hz   "
                + $"effective mass {mode.EffectiveMass.X:G4} / {mode.EffectiveMass.Y:G4} / "
                + $"{mode.EffectiveMass.Z:G4}   residual {mode.Residual:E1}");
        }
        lines.Add(Report.ToText());
        return string.Join(Environment.NewLine, lines);
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{_modes.Length} mode{(_modes.Length == 1 ? "" : "s")}"
        + (_modes.Length > 0 ? $", {_modes[0].Frequency:G6} to {_modes[^1].Frequency:G6} Hz" : "")
        + (_rigid.Length > 0 ? $", {_rigid.Length} rigid-body" : "");
}
