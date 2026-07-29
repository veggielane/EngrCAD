using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Fea;

/// <summary>
/// The answer to a conduction solve: nodal temperatures, the derived heat-flux field, and
/// the solve report — plus the same two ways out a structural solve has, a
/// <see cref="MeshField"/> set for the document model and a <c>.vtu</c> file for ParaView.
///
/// <para><b>Flux is discontinuous, and averaging is a choice made visible</b> — exactly as
/// stress is, and for exactly the same reason: <c>q = -k·grad T</c> is one derivative down
/// from the solved field, so it is constant per element for 4-node tets and linear per
/// element for 10-node ones, and it JUMPS across element faces. The nodal values here are
/// a volume-weighted average of the elements meeting at a node, which is what a colour map
/// wants and what converges as the mesh refines. It also smooths a genuine discontinuity —
/// at a material interface the NORMAL flux is continuous but the tangential gradient is
/// not, and averaging across the interface reports neither side's answer — so
/// <see cref="ElementFlux"/> stays public, and the jump between neighbouring elements
/// remains the standard error indicator.</para>
/// </summary>
public sealed class ThermalResults
{
    private readonly double[] _temperature;
    private Vector3d[]? _nodalFlux;

    // Not thread-safe: the lazy cache below is unsynchronised, so a first read from several
    // threads would recompute rather than corrupt. A results object belongs to whoever
    // solved for it. (StructuralResults says the same about its two caches.)
    internal ThermalResults(
        ThermalModel model, double[] temperature, ThermalSolveReport report, double time)
    {
        Model = model;
        _temperature = temperature;
        Report = report;
        Time = time;
    }

    /// <summary>The model that was solved.</summary>
    public ThermalModel Model { get; }

    /// <summary>The analysis mesh.</summary>
    public AnalysisMesh Mesh => Model.Mesh;

    /// <summary>What the solve did.</summary>
    public ThermalSolveReport Report { get; }

    /// <summary>The time this state belongs to — 0 for a steady solve, and the step's own
    /// time within a transient run.</summary>
    public double Time { get; }

    /// <summary>Nodal temperatures, indexed by node.</summary>
    public IReadOnlyList<double> Temperature => _temperature;

    /// <summary>Temperature at one node.</summary>
    public double TemperatureAt(int node)
    {
        RequireNode(node);
        return _temperature[node];
    }

    /// <summary>The lowest nodal temperature.</summary>
    public double MinTemperature => Report.MinTemperature;

    /// <summary>The highest nodal temperature.</summary>
    public double MaxTemperature => Report.MaxTemperature;

    /// <summary>The node carrying the highest temperature (the lowest index on a tie).</summary>
    public int MaxTemperatureNode
    {
        get
        {
            int best = 0;
            for (int v = 1; v < _temperature.Length; v++)
            {
                if (_temperature[v] > _temperature[best])
                    best = v;
            }
            return best;
        }
    }

    /// <summary>The node carrying the lowest temperature (the lowest index on a tie).</summary>
    public int MinTemperatureNode
    {
        get
        {
            int best = 0;
            for (int v = 1; v < _temperature.Length; v++)
            {
                if (_temperature[v] < _temperature[best])
                    best = v;
            }
            return best;
        }
    }

    /// <summary>How nodal flux values are averaged from element values (set before the
    /// first read of <see cref="NodalFlux"/>; changing it afterwards clears the cache).</summary>
    public NodalAveraging Averaging
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            _nodalFlux = null;
        }
    } = NodalAveraging.VolumeWeighted;

    /// <summary>The temperature GRADIENT at the centroid of one element.</summary>
    public Vector3d ElementGradient(int element)
    {
        RequireElement(element);
        return GradientAt(element, 0.25, 0.25, 0.25);
    }

    /// <summary>The heat flux <c>q = -k·grad T</c> at the centroid of one element — the
    /// per-element value, before any averaging.</summary>
    public Vector3d ElementFlux(int element)
    {
        RequireElement(element);
        return ElementGradient(element) * -Model.MaterialOf(element).ThermalConductivity;
    }

    /// <summary>Averaged heat flux at every node — what a colour map shows.</summary>
    public IReadOnlyList<Vector3d> NodalFlux => _nodalFlux ??= ComputeNodalFlux();

    /// <summary>The largest averaged nodal flux magnitude.</summary>
    public double MaxFluxMagnitude
    {
        get
        {
            double best = 0;
            foreach (var q in NodalFlux)
                best = Math.Max(best, q.Length);
            return best;
        }
    }

    /// <summary>
    /// The temperature at an arbitrary point of one element, interpolated with the
    /// element's own shape functions — how a coupled solve reads a temperature field, and
    /// exact for a point inside the element.
    /// </summary>
    public double TemperatureIn(int element, double r, double s, double t)
    {
        RequireElement(element);
        int perElement = Mesh.NodesPerElement;
        Span<double> values = stackalloc double[10];
        var nodes = Mesh.Element(element);
        for (int i = 0; i < perElement; i++)
            values[i] = _temperature[nodes[i]];
        return ThermalElement.InterpolateAt(Mesh.Order, values[..perElement], r, s, t);
    }

    private Vector3d GradientAt(int element, double r, double s, double t)
    {
        int perElement = Mesh.NodesPerElement;
        Span<Vector3d> positions = stackalloc Vector3d[10];
        Span<double> values = stackalloc double[10];
        var nodes = Mesh.Element(element);
        for (int i = 0; i < perElement; i++)
        {
            positions[i] = Mesh.Position(nodes[i]);
            values[i] = _temperature[nodes[i]];
        }
        ThermalElement.GradientAt(
            Mesh.Order, positions[..perElement], values[..perElement], r, s, t, out var gradient);
        return gradient;
    }

    private Vector3d[] ComputeNodalFlux()
    {
        int perElement = Mesh.NodesPerElement;
        var accumulated = new Vector3d[Mesh.NodeCount];
        var weights = new double[Mesh.NodeCount];

        Span<Vector3d> positions = stackalloc Vector3d[10];
        Span<double> values = stackalloc double[10];

        for (int e = 0; e < Mesh.ElementCount; e++)
        {
            var nodes = Mesh.Element(e);
            for (int i = 0; i < perElement; i++)
            {
                positions[i] = Mesh.Position(nodes[i]);
                values[i] = _temperature[nodes[i]];
            }
            double conductivity = Model.MaterialOf(e).ThermalConductivity;
            double weight = Averaging == NodalAveraging.VolumeWeighted ? Mesh.ElementVolume(e) : 1.0;
            if (!(weight > 0))
                continue;

            for (int i = 0; i < perElement; i++)
            {
                var (r, s, t) = TetElement.NodeCoordinates(Mesh.Order, i);
                ThermalElement.GradientAt(
                    Mesh.Order, positions[..perElement], values[..perElement], r, s, t,
                    out var gradient);
                accumulated[nodes[i]] += gradient * (-conductivity * weight);
                weights[nodes[i]] += weight;
            }
        }

        for (int v = 0; v < accumulated.Length; v++)
        {
            if (weights[v] > 0)
                accumulated[v] /= weights[v];
        }
        return accumulated;
    }

    // ---- publishing -----------------------------------------------------------------

    /// <summary>
    /// The results as <see cref="MeshField"/>s over the ANALYSIS mesh's nodes — the form
    /// <see cref="WriteVtu(string)"/> writes. To colour a <c>Part</c>, use
    /// <see cref="SampleOnto(HalfEdgeMesh)"/>, whose fields are indexed by the display
    /// mesh's vertices instead.
    /// <para><see cref="MeshField.ScalarAt"/> takes a vector field's MAGNITUDE, so
    /// "colour by heat flux" needs no extra call.</para>
    /// </summary>
    /// <param name="temperatureUnits">Unit label for temperature (default "C").</param>
    /// <param name="fluxUnits">Unit label for heat flux (default "mW/mm^2").</param>
    public IReadOnlyList<MeshField> Fields(
        string temperatureUnits = "C", string fluxUnits = "mW/mm^2") =>
    [
        // MeshField's constructor copies, so neither array is handed out by reference.
        MeshField.Scalar(FieldNames.Temperature, temperatureUnits, _temperature),
        MeshField.Vector(FieldNames.HeatFlux, fluxUnits, NodalFlux),
    ];

    /// <summary>The field names this class produces — so a <c>FieldDisplay</c> and a
    /// solver cannot disagree about a spelling.</summary>
    public static class FieldNames
    {
        /// <summary>The nodal temperature field.</summary>
        public const string Temperature = "Temperature";

        /// <summary>The averaged nodal heat-flux vector field.</summary>
        public const string HeatFlux = "Heat flux";
    }

    /// <summary>
    /// The results resampled onto an arbitrary surface mesh — the step that closes the gap
    /// between a solver's vertex set and a display mesh's. Exact where the two meshes share
    /// a vertex; see <c>SurfaceSampler</c>, which structural results use too.
    /// </summary>
    /// <param name="displayMesh">The mesh to sample onto.</param>
    /// <param name="maxSampleDistance">The furthest any display vertex sat from the
    /// analysis boundary. Zero means every vertex matched exactly.</param>
    /// <param name="temperatureUnits">Unit label for temperature.</param>
    /// <param name="fluxUnits">Unit label for heat flux.</param>
    public IReadOnlyList<MeshField> SampleOnto(
        HalfEdgeMesh displayMesh,
        out double maxSampleDistance,
        string temperatureUnits = "C",
        string fluxUnits = "mW/mm^2")
    {
        ArgumentNullException.ThrowIfNull(displayMesh);
        var sampler = SurfaceSampler.Build(Mesh, displayMesh);
        maxSampleDistance = sampler.MaxSampleDistance;
        return
        [
            MeshField.Scalar(FieldNames.Temperature, temperatureUnits, sampler.Sample(_temperature)),
            MeshField.Vector(FieldNames.HeatFlux, fluxUnits, sampler.Sample(NodalFlux)),
        ];
    }

    /// <summary><see cref="SampleOnto(HalfEdgeMesh, out double, string, string)"/> without
    /// the sampling-distance diagnostic.</summary>
    public IReadOnlyList<MeshField> SampleOnto(HalfEdgeMesh displayMesh) =>
        SampleOnto(displayMesh, out _);

    /// <summary>
    /// Writes the VOLUME mesh and its results as a ParaView <c>.vtu</c> file — linear
    /// elements as <c>VTK_TETRA</c>, quadratic as <c>VTK_QUADRATIC_TETRA</c>.
    /// </summary>
    public void WriteVtu(
        TextWriter writer, string temperatureUnits = "C", string fluxUnits = "mW/mm^2")
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
        VtuWriter.Write(Mesh.Nodes, cells, types, Fields(temperatureUnits, fluxUnits), writer);
    }

    /// <summary><see cref="WriteVtu(TextWriter, string, string)"/> to a file.</summary>
    public void WriteVtu(string path, string temperatureUnits = "C", string fluxUnits = "mW/mm^2")
    {
        using var writer = new StreamWriter(path);
        WriteVtu(writer, temperatureUnits, fluxUnits);
    }

    /// <summary>
    /// A one-line summary. Deliberately does NOT report the peak flux: reading it would
    /// trigger the whole nodal recovery pass, so a debugger tooltip or a log line would
    /// silently do the work. (<c>StructuralResults</c> takes the same care with its peak
    /// stress.)
    /// </summary>
    public override string ToString() =>
        $"{Mesh.ElementCount:N0} {(Mesh.Order == ElementOrder.Linear ? "linear" : "quadratic")} "
        + $"elements, temperature {MinTemperature:G6} to {MaxTemperature:G6}"
        + (Time > 0 ? $" at t = {Time:G6}" : "");

    private void RequireNode(int node)
    {
        if ((uint)node >= (uint)_temperature.Length)
            throw new ArgumentOutOfRangeException(
                nameof(node), node, $"The analysis mesh has {_temperature.Length} nodes.");
    }

    private void RequireElement(int element)
    {
        if ((uint)element >= (uint)Mesh.ElementCount)
            throw new ArgumentOutOfRangeException(
                nameof(element), element, $"The analysis mesh has {Mesh.ElementCount} elements.");
    }
}

/// <summary>
/// What a transient conduction run produced: the stored states in time order, and the
/// run's report.
///
/// <para>Each stored state is a full <see cref="ThermalResults"/>, so flux recovery,
/// <see cref="ThermalResults.SampleOnto(HalfEdgeMesh)"/> and <c>.vtu</c> export work at
/// any time as they do for a steady solve — which is also what makes a time slider over a
/// document's results a matter of choosing a state rather than of a second API.</para>
/// </summary>
public sealed class ThermalTransientResults
{
    private readonly ThermalResults[] _states;
    private readonly double[] _times;

    internal ThermalTransientResults(
        IReadOnlyList<ThermalResults> states,
        IReadOnlyList<double> times,
        ThermalTransientReport report)
    {
        _states = [.. states];
        _times = [.. times];
        Report = report;
    }

    /// <summary>What the run did.</summary>
    public ThermalTransientReport Report { get; }

    /// <summary>The stored states, in time order. The first is the initial condition and
    /// the last is the final step, whatever <c>StoreEvery</c> was.</summary>
    public IReadOnlyList<ThermalResults> States => _states;

    /// <summary>The stored states' times.</summary>
    public IReadOnlyList<double> Times => _times;

    /// <summary>How many states were stored.</summary>
    public int Count => _states.Length;

    /// <summary>The initial condition, as stored.</summary>
    public ThermalResults Initial => _states[0];

    /// <summary>The final state.</summary>
    public ThermalResults Final => _states[^1];

    /// <summary>One stored state.</summary>
    public ThermalResults this[int index] => _states[index];

    /// <summary>
    /// The temperature history of one node across the stored states — the array a
    /// convergence study or a plot wants, without walking the state list.
    /// </summary>
    public double[] HistoryOf(int node)
    {
        var history = new double[_states.Length];
        for (int i = 0; i < _states.Length; i++)
            history[i] = _states[i].TemperatureAt(node);
        return history;
    }

    /// <inheritdoc/>
    public override string ToString() => Report.ToText();
}
