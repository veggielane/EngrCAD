using EngrCAD.Core;
using EngrCAD.Core.Spatial;
using EngrCAD.Mesh;

namespace EngrCAD.Fea;

/// <summary>How element stresses are combined into one value per node.</summary>
public enum NodalAveraging
{
    /// <summary>
    /// Each contributing element is weighted by its VOLUME (the default). Large elements
    /// carry more of the domain, so they should carry more of the average; the
    /// alternative lets a sliver at a corner count as much as the element beside it.
    /// </summary>
    VolumeWeighted,

    /// <summary>Every contributing element counts equally.</summary>
    Unweighted,
}

/// <summary>
/// The answer: nodal displacements, the derived strain and stress fields, and the solve
/// report — plus the two ways out, a <see cref="MeshField"/> set for the document model
/// and a <c>.vtu</c> file for ParaView.
///
/// <para><b>Stress is discontinuous, and averaging is a choice made visible.</b> A
/// displacement-based element gives a stress field that jumps across element faces:
/// constant per element for 4-node tets, linear per element for 10-node ones. The nodal
/// values here are a volume-weighted average of the elements meeting at that node
/// (<see cref="NodalAveraging"/>), which is what every viewer's colour map wants and what
/// converges to the true field as the mesh refines. It also SMOOTHS genuine
/// discontinuities — a material interface, or a re-entrant corner where the true stress
/// is singular — so <see cref="ElementStress"/> is kept public: the size of the jump
/// between neighbouring elements is the standard error indicator, and averaging it away
/// is the standard way to hide a mesh that is too coarse.</para>
///
/// <para><b>Quadratic stress is evaluated AT the nodes</b>, not extrapolated from the
/// integration points. Direct evaluation is the exact derivative of the computed
/// displacement field at that point; Gauss-point extrapolation exploits superconvergence
/// and can be more accurate before averaging. The measured difference on this project's
/// verification cases is small (see the README), so the simpler rule is the one that
/// ships, and recovery-based smoothing is filed rather than half-implemented.</para>
/// </summary>
public sealed class StructuralResults
{
    private readonly Vector3d[] _displacement;
    private readonly Vector3d[] _reaction;
    private SymmetricTensor3[]? _nodalStress;
    private double[]? _nodalVonMises;

    // Not thread-safe: the two lazy caches below are unsynchronised, so a first read from
    // several threads would recompute rather than corrupt, but would waste the work. A
    // results object belongs to whoever solved for it.
    internal StructuralResults(
        StructuralModel model,
        Vector3d[] displacement,
        Vector3d[] reaction,
        FeaSolveReport report)
    {
        Model = model;
        _displacement = displacement;
        _reaction = reaction;
        Report = report;
    }

    /// <summary>The model that was solved.</summary>
    public StructuralModel Model { get; }

    /// <summary>The analysis mesh.</summary>
    public AnalysisMesh Mesh => Model.Mesh;

    /// <summary>What the solve did.</summary>
    public FeaSolveReport Report { get; }

    /// <summary>Nodal displacements, indexed by node.</summary>
    public IReadOnlyList<Vector3d> Displacement => _displacement;

    /// <summary>Displacement of one node.</summary>
    public Vector3d DisplacementAt(int node)
    {
        RequireNode(node);
        return _displacement[node];
    }

    private void RequireNode(int node)
    {
        if ((uint)node >= (uint)_displacement.Length)
            throw new ArgumentOutOfRangeException(
                nameof(node), node, $"The analysis mesh has {_displacement.Length} nodes.");
    }

    private void RequireElement(int element)
    {
        if ((uint)element >= (uint)Mesh.ElementCount)
            throw new ArgumentOutOfRangeException(
                nameof(element), element, $"The analysis mesh has {Mesh.ElementCount} elements.");
    }

    /// <summary>
    /// Nodal force residuals: the support reaction at a restrained degree of freedom, and
    /// the solve's own residual (near zero) at a free one.
    /// </summary>
    public IReadOnlyList<Vector3d> Reactions => _reaction;

    /// <summary>The reaction at one node (see <see cref="Reactions"/>).</summary>
    public Vector3d ReactionAt(int node)
    {
        RequireNode(node);
        return _reaction[node];
    }

    /// <summary>Strain energy ½·u'·K·u.</summary>
    public double StrainEnergy => Report.StrainEnergy;

    /// <summary>The largest displacement magnitude.</summary>
    public double MaxDisplacement
    {
        get
        {
            double best = 0;
            foreach (var d in _displacement)
                best = Math.Max(best, d.Length);
            return best;
        }
    }

    /// <summary>The node carrying the largest displacement (the lowest index on a tie).</summary>
    public int MaxDisplacementNode
    {
        get
        {
            int best = 0;
            double bestValue = -1;
            for (int v = 0; v < _displacement.Length; v++)
            {
                double d = _displacement[v].LengthSquared;
                if (d > bestValue)
                {
                    bestValue = d;
                    best = v;
                }
            }
            return best;
        }
    }

    /// <summary>How nodal values are averaged from element values (set before the first
    /// read of <see cref="NodalStress"/>; changing it afterwards clears the cache).</summary>
    public NodalAveraging Averaging
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            _nodalStress = null;
            _nodalVonMises = null;
        }
    } = NodalAveraging.VolumeWeighted;

    /// <summary>Strain at the centroid of one element. <b>Tensor</b> shear components
    /// (e_xy), not the engineering shear (g_xy = 2·e_xy) the Voigt form carries
    /// internally — a tensor is what composes under a change of frame.</summary>
    public SymmetricTensor3 ElementStrain(int element)
    {
        RequireElement(element);
        return StrainAt(element, 0.25, 0.25, 0.25);
    }

    /// <summary>Stress at the centroid of one element.</summary>
    public SymmetricTensor3 ElementStress(int element)
    {
        RequireElement(element);
        return StressAt(element, 0.25, 0.25, 0.25);
    }

    /// <summary>von Mises stress at the centroid of one element — the per-element value,
    /// before any averaging.</summary>
    public double ElementVonMises(int element) => TetElement.VonMises(ElementStress(element));

    /// <summary>Stress at one of an element's own nodes (local index 0..3 for a linear
    /// element, 0..9 for a quadratic one).</summary>
    public SymmetricTensor3 ElementStressAtNode(int element, int localNode)
    {
        RequireElement(element);
        // Bounds-checked here rather than left to the shape-function table: the linear and
        // quadratic coordinate tables are separate static arrays, so an out-of-range local
        // index would read ANOTHER node's coordinates and return a plausible wrong stress
        // instead of throwing.
        if ((uint)localNode >= (uint)Mesh.NodesPerElement)
            throw new ArgumentOutOfRangeException(
                nameof(localNode), localNode,
                $"A {Mesh.Order} element has {Mesh.NodesPerElement} nodes.");
        var (r, s, t) = TetElement.NodeCoordinates(Mesh.Order, localNode);
        return StressAt(element, r, s, t);
    }

    /// <summary>Averaged stress at every node.</summary>
    public IReadOnlyList<SymmetricTensor3> NodalStress => _nodalStress ??= ComputeNodalStress();

    /// <summary>Averaged von Mises stress at every node — what a colour map shows.</summary>
    public IReadOnlyList<double> NodalVonMises
    {
        get
        {
            if (_nodalVonMises is not null)
                return _nodalVonMises;
            var stress = NodalStress;
            var values = new double[stress.Count];
            for (int v = 0; v < values.Length; v++)
                values[v] = TetElement.VonMises(stress[v]);
            return _nodalVonMises = values;
        }
    }

    /// <summary>The largest averaged nodal von Mises stress.</summary>
    public double MaxVonMises
    {
        get
        {
            double best = 0;
            foreach (double v in NodalVonMises)
                best = Math.Max(best, v);
            return best;
        }
    }

    /// <summary>The node carrying the largest averaged von Mises stress.</summary>
    public int MaxVonMisesNode
    {
        get
        {
            var values = NodalVonMises;
            int best = 0;
            for (int v = 1; v < values.Count; v++)
            {
                if (values[v] > values[best])
                    best = v;
            }
            return best;
        }
    }

    /// <summary>
    /// The largest von Mises stress on any BOUNDARY node. For a body loaded on its
    /// surface the peak is on the surface, and restricting the search there is what keeps
    /// a stress-concentration measurement from being decided by an interior sliver.
    /// </summary>
    public double MaxSurfaceVonMises
    {
        get
        {
            var values = NodalVonMises;
            double best = 0;
            for (int f = 0; f < Mesh.FacetCount; f++)
            {
                foreach (int node in Mesh.Facet(f))
                    best = Math.Max(best, values[node]);
            }
            return best;
        }
    }

    /// <summary>Principal stresses at one node, descending (s1 >= s2 >= s3).</summary>
    public (double S1, double S2, double S3) PrincipalStress(int node)
    {
        var s = NodalStress[node];
        var (values, _) = SymmetricEigen3.SolveDescending(s.Xx, s.Xy, s.Xz, s.Yy, s.Yz, s.Zz);
        return (values[0], values[1], values[2]);
    }

    private SymmetricTensor3 StrainAt(int element, double r, double s, double t)
    {
        int perElement = Mesh.NodesPerElement;
        Span<Vector3d> positions = stackalloc Vector3d[perElement];
        Span<double> ue = stackalloc double[30];
        Span<double> strain = stackalloc double[6];
        Gather(element, positions, ue);
        TetElement.StrainAt(Mesh.Order, positions, ue[..(3 * perElement)], r, s, t, strain);
        // The Voigt strain carries ENGINEERING shear, so the tensor halves it.
        return TetElement.ToTensor(strain, engineeringShear: true);
    }

    private SymmetricTensor3 StressAt(int element, double r, double s, double t)
    {
        int perElement = Mesh.NodesPerElement;
        Span<Vector3d> positions = stackalloc Vector3d[perElement];
        Span<double> ue = stackalloc double[30];
        Span<double> stress = stackalloc double[6];
        Gather(element, positions, ue);
        VoigtStressAt(element, positions, ue[..(3 * perElement)], r, s, t, stress);
        return TetElement.ToTensor(stress, engineeringShear: false);
    }

    /// <summary>
    /// The Cauchy stress in VOIGT form at natural coordinates of an element, from element
    /// data the caller has already gathered.
    ///
    /// <para><b>This is the seam <see cref="BucklingSolver"/> takes its prestress
    /// through</b>, so the field a geometric stiffness is built from is bit-for-bit the field
    /// this class reports — thermal-strain subtraction included, which is what makes thermal
    /// buckling right rather than nearly right. The gathered-data form exists because the
    /// assembler visits several quadrature points per element and
    /// <see cref="StressAt(int, double, double, double)"/> would re-gather at every one.</para>
    /// </summary>
    internal void VoigtStressAt(
        int element,
        ReadOnlySpan<Vector3d> positions,
        ReadOnlySpan<double> nodalDisplacements,
        double r, double s, double t,
        Span<double> stress)
    {
        Span<double> strain = stackalloc double[6];
        TetElement.StrainAt(Mesh.Order, positions, nodalDisplacements, r, s, t, strain);
        SubtractThermalStrain(element, r, s, t, strain);
        Model.MaterialOf(element).Stress(strain, stress);
    }

    /// <summary>
    /// Removes the thermal part of the strain before the constitutive law is applied:
    /// <c>sigma = D·(eps - eps0)</c> with <c>eps0 = alpha·dT</c> on the diagonal.
    ///
    /// <para><b>This is the half of thermal coupling that is invisible when it is
    /// missing.</b> A bar free to expand develops exactly the thermal strain and carries
    /// zero stress; without the subtraction the recovery would report <c>E·alpha·dT</c> —
    /// 25 MPa for steel at a 10 K rise — on a bar under no load whatever, a number that
    /// looks entirely reasonable and is wholly spurious. The load and the subtraction are
    /// applied from the same stored field, so applying the load is what turns this on and
    /// there is no second step to forget.</para>
    ///
    /// <para>The temperature is interpolated with the ELEMENT's own shape functions, which
    /// is what makes the free-expansion case exact rather than nearly exact: the
    /// displacement field the solve produces is <c>alpha·dT·x</c> exactly, and its strain
    /// therefore cancels an equally interpolated <c>eps0</c> to round-off.</para>
    /// </summary>
    private void SubtractThermalStrain(
        int element, double r, double s, double t, Span<double> strain)
    {
        if (Model.ThermalDeltaT is not { } deltaT)
            return;
        double expansion = Model.MaterialOf(element).ThermalExpansion;
        // Exact-zero semantic test, matching the load: no stated expansion, no thermal
        // strain to remove.
        if (expansion == 0)
            return;

        int perElement = Mesh.NodesPerElement;
        Span<double> values = stackalloc double[10];
        var nodes = Mesh.Element(element);
        for (int i = 0; i < perElement; i++)
            values[i] = deltaT[nodes[i]];

        double thermal = expansion
            * ThermalElement.InterpolateAt(Mesh.Order, values[..perElement], r, s, t);
        strain[0] -= thermal;
        strain[1] -= thermal;
        strain[2] -= thermal;
        // Shear is untouched: an isotropic material's thermal strain is a pure dilatation.
    }

    /// <summary>
    /// The MECHANICAL strain at an element's centroid — the total strain less the thermal
    /// part, i.e. the strain the stress is actually proportional to.
    /// <para><see cref="ElementStrain"/> reports the TOTAL, because that is what a
    /// displacement field's derivative is and what a strain gauge on the part would read.
    /// The two differ only under a thermal load, and having both spellings is what lets a
    /// coupled result be checked either way.</para>
    /// </summary>
    public SymmetricTensor3 ElementMechanicalStrain(int element)
    {
        RequireElement(element);
        int perElement = Mesh.NodesPerElement;
        Span<Vector3d> positions = stackalloc Vector3d[perElement];
        Span<double> ue = stackalloc double[30];
        Span<double> strain = stackalloc double[6];
        Gather(element, positions, ue);
        TetElement.StrainAt(
            Mesh.Order, positions, ue[..(3 * perElement)], 0.25, 0.25, 0.25, strain);
        SubtractThermalStrain(element, 0.25, 0.25, 0.25, strain);
        return TetElement.ToTensor(strain, engineeringShear: true);
    }

    /// <summary>The element's node positions and its solved nodal displacements, laid out 3
    /// per node — the form every recovery routine here (and <see cref="BucklingSolver"/>'s
    /// assembly) works from.</summary>
    internal void Gather(int element, Span<Vector3d> positions, Span<double> ue)
    {
        var nodes = Mesh.Element(element);
        for (int i = 0; i < nodes.Length; i++)
        {
            positions[i] = Mesh.Position(nodes[i]);
            var u = _displacement[nodes[i]];
            ue[3 * i] = u.X;
            ue[3 * i + 1] = u.Y;
            ue[3 * i + 2] = u.Z;
        }
    }

    private SymmetricTensor3[] ComputeNodalStress()
    {
        int perElement = Mesh.NodesPerElement;
        var accumulated = new SymmetricTensor3[Mesh.NodeCount];
        var weights = new double[Mesh.NodeCount];

        Span<Vector3d> positions = stackalloc Vector3d[10];
        Span<double> ue = stackalloc double[30];
        Span<double> strain = stackalloc double[6];
        Span<double> stress = stackalloc double[6];

        for (int e = 0; e < Mesh.ElementCount; e++)
        {
            var nodes = Mesh.Element(e);
            Gather(e, positions, ue);
            var material = Model.MaterialOf(e);
            double weight = Averaging == NodalAveraging.VolumeWeighted ? Mesh.ElementVolume(e) : 1.0;
            if (!(weight > 0))
                continue;

            for (int i = 0; i < perElement; i++)
            {
                var (r, s, t) = TetElement.NodeCoordinates(Mesh.Order, i);
                TetElement.StrainAt(
                    Mesh.Order, positions[..perElement], ue[..(3 * perElement)], r, s, t, strain);
                // The SAME subtraction StressAt makes. This loop inlines the strain pass
                // rather than calling StressAt (it would re-gather the element ten times),
                // so the thermal correction has to be asked for explicitly here — and it is
                // asked for, not restated, which is what keeps nodal and element stress
                // from disagreeing under a thermal load.
                SubtractThermalStrain(e, r, s, t, strain);
                material.Stress(strain, stress);
                accumulated[nodes[i]] += TetElement.ToTensor(stress, engineeringShear: false) * weight;
                weights[nodes[i]] += weight;
            }
        }

        for (int v = 0; v < accumulated.Length; v++)
        {
            if (weights[v] > 0)
                accumulated[v] *= 1.0 / weights[v];
        }
        return accumulated;
    }

    // ---- publishing -----------------------------------------------------------------

    /// <summary>
    /// The results as <see cref="MeshField"/>s over the ANALYSIS mesh's nodes — the form
    /// <see cref="WriteVtu(string)"/> writes. To colour a <c>Part</c>, use
    /// <see cref="SampleOnto(HalfEdgeMesh)"/>, whose fields are indexed by the display
    /// mesh's vertices instead.
    /// </summary>
    /// <param name="lengthUnits">Unit label for displacement (default "mm").</param>
    /// <param name="stressUnits">Unit label for stress (default "MPa").</param>
    public IReadOnlyList<MeshField> Fields(string lengthUnits = "mm", string stressUnits = "MPa") =>
    [
        // MeshField's constructor copies, so neither array is handed out by reference.
        MeshField.Vector(FieldNames.Displacement, lengthUnits, _displacement),
        MeshField.Scalar(FieldNames.VonMises, stressUnits, NodalVonMises),
    ];

    /// <summary>The field names this class produces — so a <c>FieldDisplay</c> and a
    /// solver cannot disagree about a spelling.</summary>
    public static class FieldNames
    {
        /// <summary>The displacement vector field.</summary>
        public const string Displacement = "Displacement";

        /// <summary>The averaged nodal von Mises stress field.</summary>
        public const string VonMises = "von Mises";
    }

    /// <summary>
    /// The results resampled onto an arbitrary surface mesh — the step that closes the
    /// gap between a solver's vertex set and a display mesh's.
    ///
    /// <para><b>Exact where the two meshes share a vertex.</b> A display vertex whose
    /// position matches an analysis boundary node <i>bit for bit</i> takes that node's
    /// value directly, which covers essentially every vertex in the normal case (the same
    /// mesh was fed to the tet mesher, and its vertices survive verbatim). Anything else
    /// — a differently tessellated display mesh, or a vertex the mesher inserted around —
    /// falls back to the closest point on the nearest boundary facet and interpolates
    /// there with the facet's own shape functions, which is exact for a point that lies
    /// on the facet and continuous across facet edges.</para>
    /// </summary>
    /// <param name="displayMesh">The mesh to sample onto.</param>
    /// <param name="maxSampleDistance">
    /// The furthest any display vertex sat from the analysis boundary. Zero means every
    /// vertex matched exactly; a value comparable to the model size means the two meshes
    /// are not the same body and the result is meaningless.
    /// </param>
    /// <param name="lengthUnits">Unit label for displacement.</param>
    /// <param name="stressUnits">Unit label for stress.</param>
    public IReadOnlyList<MeshField> SampleOnto(
        HalfEdgeMesh displayMesh,
        out double maxSampleDistance,
        string lengthUnits = "mm",
        string stressUnits = "MPa")
    {
        ArgumentNullException.ThrowIfNull(displayMesh);
        var sampler = SurfaceSampler.Build(Mesh, displayMesh);
        maxSampleDistance = sampler.MaxSampleDistance;
        return
        [
            MeshField.Vector(FieldNames.Displacement, lengthUnits, sampler.Sample(_displacement)),
            MeshField.Scalar(FieldNames.VonMises, stressUnits, sampler.Sample(NodalVonMises)),
        ];
    }

    /// <summary><see cref="SampleOnto(HalfEdgeMesh, out double, string, string)"/> without
    /// the sampling-distance diagnostic.</summary>
    public IReadOnlyList<MeshField> SampleOnto(HalfEdgeMesh displayMesh) =>
        SampleOnto(displayMesh, out _);

    /// <summary>
    /// Writes the VOLUME mesh and its results as a ParaView <c>.vtu</c> file — linear
    /// elements as <c>VTK_TETRA</c>, quadratic as <c>VTK_QUADRATIC_TETRA</c>, whose node
    /// order <see cref="QuadraticTet"/> already follows.
    /// </summary>
    public void WriteVtu(TextWriter writer, string lengthUnits = "mm", string stressUnits = "MPa")
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
        VtuWriter.Write(Mesh.Nodes, cells, types, Fields(lengthUnits, stressUnits), writer);
    }

    /// <summary><see cref="WriteVtu(TextWriter, string, string)"/> to a file.</summary>
    public void WriteVtu(string path, string lengthUnits = "mm", string stressUnits = "MPa")
    {
        using var writer = new StreamWriter(path);
        WriteVtu(writer, lengthUnits, stressUnits);
    }

    /// <summary>
    /// A one-line summary. Deliberately does NOT report peak stress: reading it would
    /// trigger the whole nodal recovery pass, so a debugger tooltip, a log line or this
    /// object appearing in an exception message would silently do the work. The stress is
    /// one property away when it is actually wanted.
    /// </summary>
    public override string ToString() =>
        $"{Mesh.ElementCount:N0} {(Mesh.Order == ElementOrder.Linear ? "linear" : "quadratic")} "
        + $"elements, max displacement {MaxDisplacement:G6}"
        + (_nodalVonMises is { } stress ? $", max von Mises {stress.Max():G6}" : "");
}
