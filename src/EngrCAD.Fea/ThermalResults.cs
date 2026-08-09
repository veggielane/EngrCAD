using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Fea;

/// <summary>How the discontinuous element heat-flux field is turned into one value per node —
/// the thermal twin of <see cref="StressRecovery"/>.</summary>
public enum FluxRecovery
{
    /// <summary>Average the element flux values meeting at each node (the default). Simple,
    /// exact where the flux is constant, and the value a colour map has always shown here. Its
    /// weakness is where it samples: for a quadratic element the flux is linear and a node is
    /// the WORST place to read it.</summary>
    Direct,

    /// <summary>Zienkiewicz-Zhu superconvergent patch recovery of the flux vector: fit a
    /// polynomial per corner-node patch to the flux at the element's superconvergent points and
    /// read it at the nodes. Converges one order faster than <see cref="Direct"/>, and is the
    /// same machinery <see cref="StressRecovery.Superconvergent"/> runs — a smaller fit (3
    /// components rather than 6) over the same slot table.</summary>
    Superconvergent,
}

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
/// wants and what converges as the mesh refines. <see cref="ElementFlux"/> stays public, and
/// the jump between neighbouring elements remains the standard error indicator.</para>
///
/// <para><b>A MATERIAL INTERFACE is the one place that average smooths something real.</b>
/// Where two conductivities meet, temperature is continuous, so the tangential gradient is
/// continuous too and the tangential flux JUMPS with <c>k</c> — only the normal component is
/// continuous. A node on that interface therefore has one right answer per material and
/// <see cref="NodalFlux"/>, being indexed by node, holds one value.
/// <see cref="NodalFluxIn"/> is the honest per-material value and
/// <see cref="AnalysisMesh.InterfaceNodeCount"/> says how many nodes are affected (zero for a
/// single-region mesh and for disjoint bodies, which share no node). This mirrors
/// <see cref="StructuralResults.NodalStressIn"/> exactly, over the same
/// <see cref="AnalysisMesh.RegionsAt"/> slot table.</para>
/// </summary>
public sealed class ThermalResults
{
    private readonly double[] _temperature;
    private Vector3d[]? _nodalFlux;
    private Vector3d[]? _nodalFluxPerRegion;
    // The recovered flux and the error estimate — lazy like the averaged caches above, and
    // unsynchronised for the same reason (a results object belongs to whoever solved for it).
    private RecoveredField<Vector3d>? _recovered;
    private ErrorEstimate? _errorEstimate;

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
            _nodalFluxPerRegion = null;
            // The recovery and the estimate consume the averaged flux (boundary nodes fall
            // back to it), so a changed averaging staleness them as well — clearing only the
            // averaged caches would let a later recovered read pair a fresh average with a
            // recovery computed under the old one.
            _recovered = null;
            _errorEstimate = null;
        }
    } = NodalAveraging.VolumeWeighted;

    /// <summary>
    /// Which recovery <see cref="NodalFlux"/> reports. <see cref="FluxRecovery.Direct"/> is the
    /// default, so nothing about an existing result moves — every thermal verification figure
    /// this project quotes was measured through the simple path.
    ///
    /// <para><see cref="FluxRecovery.Superconvergent"/> converges one order faster and costs a
    /// small least-squares solve per corner node — the flux twin of
    /// <see cref="StructuralResults.Recovery"/>, and NOT the default for the same reason it is
    /// not there: a recovered field is smooth by construction, so at a genuine discontinuity (a
    /// material interface, a re-entrant corner) it smooths harder than averaging does.</para>
    ///
    /// <para>A plain property: <see cref="NodalFlux"/> and <see cref="NodalFluxIn"/> switch on
    /// it and cache nothing at their own level, so changing it invalidates nothing.</para>
    /// </summary>
    public FluxRecovery FluxRecovery { get; set; } = FluxRecovery.Direct;

    /// <summary>The temperature GRADIENT at the centroid of one element.</summary>
    public Vector3d ElementGradient(int element)
    {
        RequireElement(element);
        return GradientAt(element, 0.25, 0.25, 0.25);
    }

    /// <summary>The heat flux <c>q = -K·grad T</c> at the centroid of one element — the
    /// per-element value, before any averaging. Directional where the element's region carries a
    /// conductivity law, and bit-for-bit <c>-k·grad T</c> where it does not.</summary>
    public Vector3d ElementFlux(int element)
    {
        RequireElement(element);
        return Model.ConductivityLawOf(element).Flux(ElementGradient(element));
    }

    /// <summary>
    /// Heat flux at every node, by whichever <see cref="FluxRecovery"/> is selected — what a
    /// colour map shows.
    ///
    /// <para><b>One value per node, which at a MATERIAL INTERFACE is one value too few</b> —
    /// see the class remarks, <see cref="AnalysisMesh.InterfaceNodeCount"/> for how many nodes
    /// are affected, and <see cref="NodalFluxIn"/> for the per-material value.</para>
    /// </summary>
    public IReadOnlyList<Vector3d> NodalFlux =>
        FluxRecovery == FluxRecovery.Direct ? AveragedFluxByNode : RecoveredFlux.Values;

    private Vector3d[] AveragedFluxByNode => _nodalFlux ??= ComputeNodalFlux(perRegion: false);

    /// <summary>
    /// The averaged flux per (node, region) SLOT — the same rule <see cref="Averaging"/>
    /// states, restricted to one material, and the fallback the recovery itself uses where a
    /// slot is reached by no patch.
    /// <para>Where a slot IS a node (every mesh with one region, and every mesh of disjoint
    /// bodies) this is <see cref="AveragedFluxByNode"/> itself rather than a second array
    /// holding the same numbers: the two accumulations would be the same additions in the same
    /// order, so computing it twice would buy nothing but a chance to drift.</para>
    /// </summary>
    private Vector3d[] AveragedFluxPerRegion =>
        Mesh.RegionSlotsAreNodes
            ? AveragedFluxByNode
            : _nodalFluxPerRegion ??= ComputeNodalFlux(perRegion: true);

    /// <summary>The superconvergent recovery — the SAME machinery
    /// <see cref="StressRecovery.Superconvergent"/> runs, over a 3-component flux through
    /// <see cref="FluxField"/>, with the averaged flux as its per-slot fallback.</summary>
    private RecoveredField<Vector3d> RecoveredFlux =>
        _recovered ??= SuperconvergentRecovery.Recover<Vector3d, FluxField>(
            new FluxField(this), AveragedFluxPerRegion);

    /// <summary>
    /// The heat flux at one node <b>as seen from one material region</b> — the honest value
    /// where <see cref="NodalFlux"/> can only report a blend, by whichever
    /// <see cref="FluxRecovery"/> is selected.
    ///
    /// <para>Equal to <see cref="NodalFlux"/> at every node touching one region, by identical
    /// arithmetic rather than by tolerance, so switching to this accessor cannot move an
    /// answer away from an interface. Refused by name when the node touches no element of that
    /// region; <see cref="AnalysisMesh.RegionsAt"/> says which regions it does touch.</para>
    /// </summary>
    /// <param name="region">A material region id (see <see cref="AnalysisMesh.Regions"/>).</param>
    /// <param name="node">The node.</param>
    public Vector3d NodalFluxIn(int region, int node)
    {
        RequireNode(node);
        int slot = Mesh.RegionSlot(node, region);
        return FluxRecovery == FluxRecovery.Direct
            ? AveragedFluxPerRegion[slot]
            : RecoveredFlux.SlotValues[slot];
    }

    /// <summary>
    /// The Zienkiewicz-Zhu error estimate: the energy-norm distance between the element
    /// heat-flux field and its superconvergent recovery, per element and overall — the answer
    /// to "is this conduction mesh good enough" a solve otherwise never gives.
    ///
    /// <para>Always computed from the RECOVERED flux, whatever <see cref="FluxRecovery"/> is
    /// set to, because that is what makes it an estimator: the recovered field is a better
    /// approximation than the finite-element one, so the gap between them estimates the error
    /// in the finite-element one. The energy inner product of a flux difference is
    /// <c>|q|²/k</c> — the thermal analogue of the structural compliance norm, and
    /// <c>k·|grad T|²</c> by another name.</para>
    /// </summary>
    public ErrorEstimate ErrorEstimate =>
        _errorEstimate ??= ComputeThermalErrorEstimate(RecoveredFlux);

    /// <summary>
    /// The averaged flux with the material-region rule switched on or off, computed FRESH —
    /// the test seam that lets the blend be measured against the per-material values, and that
    /// makes the single-region neutrality claim a comparison of two independent computations
    /// rather than of one array with itself.
    /// <para>Production reads <see cref="NodalFlux"/> and <see cref="NodalFluxIn"/>, both of
    /// which cache.</para>
    /// </summary>
    internal Vector3d[] AveragedFlux(bool perRegion) => ComputeNodalFlux(perRegion);

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

    /// <summary>
    /// The averaged nodal flux, indexed either by NODE or by (node, region) SLOT.
    /// <para>One implementation for both — the per-region pass is the same loop writing to a
    /// different index, so the two cannot disagree about the averaging rule or the
    /// conductivity — and for a mesh whose slots are its nodes the index expression is the
    /// node itself, so the two are the same additions in the same order.
    /// (<c>StructuralResults.ComputeNodalStress</c> is this method's twin.)</para>
    /// </summary>
    private Vector3d[] ComputeNodalFlux(bool perRegion)
    {
        int perElement = Mesh.NodesPerElement;
        int slots = perRegion ? Mesh.RegionSlotCount : Mesh.NodeCount;
        var accumulated = new Vector3d[slots];
        var weights = new double[slots];

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
            var law = Model.ConductivityLawOf(e);
            int region = perRegion ? Mesh.RegionOf(e) : 0;
            double weight = Averaging == NodalAveraging.VolumeWeighted ? Mesh.ElementVolume(e) : 1.0;
            if (!(weight > 0))
                continue;

            for (int i = 0; i < perElement; i++)
            {
                var (r, s, t) = TetElement.NodeCoordinates(Mesh.Order, i);
                ThermalElement.GradientAt(
                    Mesh.Order, positions[..perElement], values[..perElement], r, s, t,
                    out var gradient);
                int target = perRegion ? Mesh.RegionSlot(nodes[i], region) : nodes[i];
                // Bit-for-bit the incumbent arithmetic for an isotropic law; the tensor path
                // takes the flux q = -K·grad first, then applies the averaging weight.
                accumulated[target] += law.IsIsotropic
                    ? gradient * (-law.IsotropicConductivity * weight)
                    : law.Flux(gradient) * weight;
                weights[target] += weight;
            }
        }

        for (int v = 0; v < accumulated.Length; v++)
        {
            if (weights[v] > 0)
                accumulated[v] /= weights[v];
        }
        return accumulated;
    }

    /// <summary>
    /// The energy-norm error between the finite-element flux and the recovered flux, per
    /// element and overall — the flux twin of <c>StructuralResults.ComputeErrorEstimate</c>,
    /// with the compliance replaced by the scalar thermal one <c>1/k</c>.
    /// </summary>
    private ErrorEstimate ComputeThermalErrorEstimate(RecoveredField<Vector3d> recovered)
    {
        int perElement = Mesh.NodesPerElement;
        // The integrand is the SQUARE of a difference between a degree-p field (the recovered
        // flux, interpolated) and a degree-(p-1) one (the finite-element flux), i.e. degree
        // 2p; the rule is chosen to be exact for it — the same choice the structural estimate
        // makes, and NOT the conductivity's 2(p-1), which would under-integrate the estimate
        // and make it look better than it is.
        var rule = Mesh.Order == ElementOrder.Linear
            ? TetQuadrature.Degree3
            : TetQuadrature.Degree5;

        var perElementError = new double[Mesh.ElementCount];
        double totalError = 0, totalEnergy = 0;

        Span<Vector3d> positions = stackalloc Vector3d[10];
        Span<double> temps = stackalloc double[10];
        Span<double> shape = stackalloc double[10];
        Span<Vector3d> grad = stackalloc Vector3d[10];

        // Read from the RECOVERY, not from the mesh: a recovery run with regions collapsed has
        // node-indexed slots whatever the mesh's own slots are.
        bool perRegion = !recovered.SlotsAreNodes;

        for (int e = 0; e < Mesh.ElementCount; e++)
        {
            var nodes = Mesh.Element(e);
            for (int i = 0; i < perElement; i++)
            {
                positions[i] = Mesh.Position(nodes[i]);
                temps[i] = _temperature[nodes[i]];
            }
            var law = Model.ConductivityLawOf(e);
            // An element reads its OWN material's recovered values, never the node-indexed
            // blend: at an interface element the blend carries the other material's flux, and
            // the estimator would book that modelling jump as this element's error.
            int region = perRegion ? Mesh.RegionOf(e) : 0;
            double error = 0, energy = 0;

            for (int q = 0; q < rule.Count; q++)
            {
                var (r, s, t) = rule.Point(q);
                if (!TetElement.ShapeGradients(
                        Mesh.Order, positions[..perElement], r, s, t, grad, out double detJ))
                    continue;
                double weight = rule.Weight(q) * detJ;
                TetElement.ShapeValues(Mesh.Order, r, s, t, shape);

                // The finite-element flux from the element's own shape gradients — the same
                // arithmetic ThermalElement.GradientAt runs, inlined so detJ is not computed
                // twice.
                var gradT = Vector3d.Zero;
                for (int i = 0; i < perElement; i++)
                    gradT += grad[i] * temps[i];
                var fe = law.Flux(gradT);

                // The recovered flux INSIDE the element is its nodal values interpolated by the
                // element's own shape functions, which is what makes the two fields comparable
                // pointwise rather than only at the nodes.
                var smooth = Vector3d.Zero;
                for (int i = 0; i < perElement; i++)
                {
                    var value = perRegion
                        ? recovered.SlotValues[Mesh.RegionSlot(nodes[i], region)]
                        : recovered.Values[nodes[i]];
                    smooth += value * shape[i];
                }

                // The complementary energy norm of a flux is q·K⁻¹·q — |q|²/k for an isotropic
                // law (bit-for-bit the incumbent form) and the resistivity quadratic form for a
                // directional one, so an anisotropic error estimate measures the flux difference
                // in the same energy the physics stores it in.
                var d = smooth - fe;
                error += weight * law.FluxEnergy(d);
                energy += weight * law.FluxEnergy(fe);
            }

            perElementError[e] = Math.Sqrt(Math.Max(0, error));
            totalError += error;
            totalEnergy += energy;
        }

        double errorNorm = Math.Sqrt(Math.Max(0, totalError));
        double energyNorm = Math.Sqrt(Math.Max(0, totalEnergy));
        // The classical ZZ normalisation: the estimated error over the estimated norm of the
        // EXACT solution, which is the finite-element norm plus the error rather than the
        // finite-element norm alone. At the coarse end the difference is not small.
        double denominator = Math.Sqrt(totalEnergy + totalError);
        double relative = denominator > 0 ? errorNorm / denominator : 0;

        // NOTHING was recovered, so the "error" is the distance from the finite-element field
        // to itself — the same UNKNOWN case the structural estimate reports as NaN rather than
        // as arithmetic that calls the mesh perfect on exactly the mesh too coarse to assess.
        if (recovered.FallbackNodes >= Mesh.NodeCount)
        {
            errorNorm = double.NaN;
            relative = double.NaN;
            Array.Fill(perElementError, double.NaN);
        }

        return new ErrorEstimate(
            perElementError, errorNorm, energyNorm, relative,
            recovered.FallbackNodes, recovered.RankDeficientPatches);
    }

    /// <summary>
    /// The flux field for the recovery: the finite-element heat flux <c>q = -k·grad T</c>,
    /// evaluated through the element's own shape-function gradients. The 3-component twin of
    /// <c>SuperconvergentRecovery.StressField</c>.
    /// </summary>
    private readonly struct FluxField(ThermalResults results) : IRecoveryField<Vector3d>
    {
        public AnalysisMesh Mesh => results.Mesh;
        public int Components => 3;
        public int DofsPerNode => 1;

        public void GatherDofs(int element, Span<Vector3d> positions, Span<double> dofs)
        {
            var nodes = results.Mesh.Element(element);
            for (int i = 0; i < nodes.Length; i++)
            {
                positions[i] = results.Mesh.Position(nodes[i]);
                dofs[i] = results._temperature[nodes[i]];
            }
        }

        public void SampleAt(
            int element, ReadOnlySpan<Vector3d> positions, ReadOnlySpan<double> dofs,
            double r, double s, double t, Span<double> value)
        {
            ThermalElement.GradientAt(
                results.Mesh.Order, positions, dofs, r, s, t, out var gradient);
            var q = results.Model.ConductivityLawOf(element).Flux(gradient);
            value[0] = q.X;
            value[1] = q.Y;
            value[2] = q.Z;
        }

        public Vector3d Zero => Vector3d.Zero;
        public Vector3d FromComponents(ReadOnlySpan<double> value) =>
            new(value[0], value[1], value[2]);
        public Vector3d Add(Vector3d a, Vector3d b) => a + b;
        public Vector3d Scale(Vector3d a, double s) => a * s;
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
