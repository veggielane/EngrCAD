using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Fea;

/// <summary>
/// One buckling mode: the multiple of the reference load case at which the structure loses
/// stability, and the shape it goes unstable in.
///
/// <para><b><see cref="LoadFactor"/> multiplies the whole reference load case, not one
/// number in it.</b> A linear static solve's stress field is homogeneous of degree one in
/// every load it was given — forces, pressures, tractions, gravity, a thermal field, an
/// enforced displacement — so the geometric stiffness scales with all of them together and
/// the eigenvalue is a factor on the case as a whole. That is why nothing here is called
/// "the critical load": for a column pushed from both ends the applied RESULTANT is exactly
/// zero, and a scalar critical load would either be zero or would have to guess which half
/// of the load case the caller meant. Multiply your own reference load by the factor.</para>
///
/// <para><b>The shape has no amplitude and no sign</b>, for the same reason a vibration
/// mode does not — it is the eigenvector of a homogeneous problem. The conventions are
/// <see cref="VibrationMode"/>'s: <see cref="Shape"/> is normalised in the STIFFNESS inner
/// product (<c>phi' K phi = 1</c>, which is the metric this eigenproblem runs in), the
/// published field is rescaled to a peak nodal magnitude of exactly 1 model unit, and the
/// sign is pinned by making the largest component positive so two solves agree bit for
/// bit.</para>
/// </summary>
public sealed class BucklingMode
{
    private readonly Vector3d[] _shape;

    internal BucklingMode(int number, double loadFactor, Vector3d[] shape, double residual, double peak)
    {
        Number = number;
        LoadFactor = loadFactor;
        _shape = shape;
        Residual = residual;
        PeakDisplacement = peak;
    }

    /// <summary>Which mode this is, counting the lowest positive load factor as 1.</summary>
    public int Number { get; }

    /// <summary>
    /// The critical load factor <c>lambda</c>: the reference load case multiplied by this
    /// makes <c>K + lambda·Kg</c> singular. Greater than 1 means the structure is stable
    /// under the reference load with that much margin; less than 1 means it has already
    /// buckled.
    /// </summary>
    public double LoadFactor { get; }

    /// <summary>The buckled shape at every node, normalised so <c>phi' K phi = 1</c>. Not a
    /// displacement — see the class remarks.</summary>
    public IReadOnlyList<Vector3d> Shape => _shape;

    /// <summary>Shape at one node.</summary>
    public Vector3d ShapeAt(int node) => _shape[node];

    /// <summary>The MEASURED relative residual
    /// <c>|K phi - lambda·(-Kg) phi| / (|K phi| + |lambda||Kg phi|)</c> — not a bound, and
    /// not the eigensolver's internal estimate.</summary>
    public double Residual { get; }

    /// <summary>The largest nodal magnitude of <see cref="Shape"/> — the number the published
    /// field is divided by, so that its peak displacement is exactly 1.</summary>
    public double PeakDisplacement { get; }

    /// <inheritdoc/>
    public override string ToString() =>
        $"buckling mode {Number}: load factor {LoadFactor:G6} (residual {Residual:E2})";
}

/// <summary>
/// The answer of a linear buckling (eigenvalue stability) analysis: the critical load
/// factors, the shapes that go unstable, and the reference solve they were linearised about.
/// </summary>
public sealed class BucklingResults
{
    private readonly BucklingMode[] _modes;

    internal BucklingResults(
        StructuralResults reference,
        BucklingMode[] modes,
        Vector3d referenceForce,
        double referenceStrainEnergy,
        BucklingSolveReport report)
    {
        Reference = reference;
        _modes = modes;
        ReferenceForce = referenceForce;
        ReferenceStrainEnergy = referenceStrainEnergy;
        Report = report;
    }

    /// <summary>The static solve the geometric stiffness was built from.</summary>
    public StructuralResults Reference { get; }

    /// <summary>The model that was solved — the reference solve's own.</summary>
    public StructuralModel Model => Reference.Model;

    /// <summary>The analysis mesh.</summary>
    public AnalysisMesh Mesh => Model.Mesh;

    /// <summary>What the solve did.</summary>
    public BucklingSolveReport Report { get; }

    /// <summary>The buckling modes, ascending in load factor. Only POSITIVE factors are
    /// reported; see <see cref="BucklingSolver"/> for why, and for what a load case that
    /// buckles only when reversed does instead.</summary>
    public IReadOnlyList<BucklingMode> Modes => _modes;

    /// <summary>The critical load factors, ascending — the headline numbers.</summary>
    public IReadOnlyList<double> LoadFactors => _modes.Select(m => m.LoadFactor).ToArray();

    /// <summary>The lowest critical load factor: the margin against buckling under the
    /// reference load case.</summary>
    public double CriticalLoadFactor => _modes.Length > 0
        ? _modes[0].LoadFactor
        : throw new InvalidOperationException("No buckling modes were extracted.");

    /// <summary>
    /// The reference load case's applied force RESULTANT, for scaling convenience.
    /// <b>Frequently zero and legitimately so</b> — a self-equilibrated case (a strut pushed
    /// from both ends, an enforced displacement, a thermal load) has no resultant, and the
    /// load factor is meaningful anyway. It is reported rather than derived from because
    /// nothing here can know which quantity a caller thinks of as "the load".
    /// </summary>
    public Vector3d ReferenceForce { get; }

    /// <summary>The reference solve's strain energy — the scalar that is non-zero for every
    /// load case that produces stress, self-equilibrated or not, and therefore the one this
    /// solver checks before deciding there is a prestress at all.</summary>
    public double ReferenceStrainEnergy { get; }

    /// <summary>The mode with a given 1-based number.</summary>
    public BucklingMode Mode(int number)
    {
        if (number < 1 || number > _modes.Length)
            throw new ArgumentOutOfRangeException(
                nameof(number), number,
                _modes.Length == 0
                    ? "No buckling modes were extracted."
                    : $"Buckling modes are numbered 1 to {_modes.Length}, ascending in load "
                      + "factor.");
        return _modes[number - 1];
    }

    // ---- publishing -----------------------------------------------------------------

    /// <summary>The field names this class produces — so a <c>FieldDisplay</c> and the solver
    /// cannot disagree about a spelling.</summary>
    public static class FieldNames
    {
        /// <summary>What every buckling-shape field name starts with.</summary>
        public const string Prefix = "Buckling ";

        /// <summary>The vector-field name for a 1-based mode number: "Buckling 1", … The
        /// load factor is deliberately not in the name, for the reason
        /// <see cref="ModalResults.FieldNames"/> gives: a field name is a document handle
        /// that a saved document round-trips, and a name carrying a computed number would
        /// stop resolving the moment a parameter changed.</summary>
        public static string Shape(int number) => $"{Prefix}{number}";
    }

    /// <summary>The unit label buckling-shape fields carry. Not a length: the values are
    /// scaled to a peak of 1 and their amplitude is a display choice.</summary>
    public const string ShapeUnits = "buckling shape";

    /// <summary>One mode's shape as a <see cref="MeshField"/> over the ANALYSIS mesh's
    /// nodes, rescaled so its largest nodal magnitude is exactly 1 model length unit.</summary>
    /// <param name="number">1-based mode number.</param>
    public IReadOnlyList<MeshField> Fields(int number) =>
        [MeshField.Vector(FieldNames.Shape(number), ShapeUnits, ScaledShape(Mode(number)))];

    /// <summary>Every extracted mode's shape, one vector field each.</summary>
    public IReadOnlyList<MeshField> AllFields()
    {
        var fields = new MeshField[_modes.Length];
        for (int i = 0; i < _modes.Length; i++)
            fields[i] = MeshField.Vector(
                FieldNames.Shape(_modes[i].Number), ShapeUnits, ScaledShape(_modes[i]));
        return fields;
    }

    /// <summary>
    /// EVERY mode's shape resampled onto a display mesh, in one pass — the correspondence is
    /// built once and shared, which is the whole reason this overload exists.
    /// </summary>
    /// <param name="displayMesh">The mesh to sample onto.</param>
    /// <param name="maxSampleDistance">The furthest any display vertex sat from the analysis
    /// boundary. Zero means every vertex matched exactly.</param>
    public IReadOnlyList<MeshField> SampleOnto(
        HalfEdgeMesh displayMesh, out double maxSampleDistance)
    {
        ArgumentNullException.ThrowIfNull(displayMesh);
        var sampler = SurfaceSampler.Build(Mesh, displayMesh);
        maxSampleDistance = sampler.MaxSampleDistance;
        var fields = new MeshField[_modes.Length];
        for (int i = 0; i < _modes.Length; i++)
            fields[i] = MeshField.Vector(
                FieldNames.Shape(_modes[i].Number), ShapeUnits, sampler.Sample(ScaledShape(_modes[i])));
        return fields;
    }

    /// <summary><see cref="SampleOnto(HalfEdgeMesh, out double)"/> without the diagnostic.</summary>
    public IReadOnlyList<MeshField> SampleOnto(HalfEdgeMesh displayMesh) =>
        SampleOnto(displayMesh, out _);

    private Vector3d[] ScaledShape(BucklingMode mode)
    {
        var scaled = new Vector3d[Mesh.NodeCount];
        // Exact-zero division guard: a shape with no displacement anywhere cannot happen (it
        // would not be K-normalised), but dividing by a peak read off a possibly empty mesh
        // is not something to leave to luck.
        double inverse = mode.PeakDisplacement > 0 ? 1.0 / mode.PeakDisplacement : 1.0;
        for (int v = 0; v < scaled.Length; v++)
            scaled[v] = mode.ShapeAt(v) * inverse;
        return scaled;
    }

    /// <summary>Writes the VOLUME mesh with EVERY buckling shape as its own point-data
    /// array — ParaView's warp filter then shows whichever mode is selected.</summary>
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

    /// <summary>A readable table of the load factors — what a report prints.</summary>
    public string ToText()
    {
        var lines = new List<string>
        {
            $"reference load case: applied resultant {ReferenceForce}, strain energy "
                + $"{ReferenceStrainEnergy:G6}",
        };
        foreach (var mode in _modes)
        {
            lines.Add(
                $"mode {mode.Number,2}: load factor {mode.LoadFactor,14:N4}   "
                + $"residual {mode.Residual:E1}");
        }
        lines.Add(Report.ToText());
        return string.Join(Environment.NewLine, lines);
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{_modes.Length} buckling mode{(_modes.Length == 1 ? "" : "s")}"
        + (_modes.Length > 0
            ? $", critical load factor {_modes[0].LoadFactor:G6}"
            : "");
}
