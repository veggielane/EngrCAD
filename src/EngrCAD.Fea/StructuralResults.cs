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
    private readonly TetQuadrature _rule;
    private SymmetricTensor3[]? _nodalStress;
    private double[]? _nodalVonMises;

    internal StructuralResults(
        StructuralModel model,
        Vector3d[] displacement,
        Vector3d[] reaction,
        FeaSolveReport report,
        in TetQuadrature rule)
    {
        Model = model;
        _displacement = displacement;
        _reaction = reaction;
        Report = report;
        _rule = rule;
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
    public Vector3d DisplacementAt(int node) => _displacement[node];

    /// <summary>
    /// Nodal force residuals: the support reaction at a restrained degree of freedom, and
    /// the solve's own residual (near zero) at a free one.
    /// </summary>
    public IReadOnlyList<Vector3d> Reactions => _reaction;

    /// <summary>The reaction at one node (see <see cref="Reactions"/>).</summary>
    public Vector3d ReactionAt(int node) => _reaction[node];

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
    public SymmetricTensor3 ElementStrain(int element) =>
        StrainAt(element, 0.25, 0.25, 0.25);

    /// <summary>Stress at the centroid of one element.</summary>
    public SymmetricTensor3 ElementStress(int element) => StressAt(element, 0.25, 0.25, 0.25);

    /// <summary>von Mises stress at the centroid of one element — the per-element value,
    /// before any averaging.</summary>
    public double ElementVonMises(int element) => TetElement.VonMises(ElementStress(element));

    /// <summary>Stress at one of an element's own nodes (local index 0..3 or 0..9).</summary>
    public SymmetricTensor3 ElementStressAtNode(int element, int localNode)
    {
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
        Span<double> strain = stackalloc double[6];
        Span<double> stress = stackalloc double[6];
        Gather(element, positions, ue);
        TetElement.StrainAt(Mesh.Order, positions, ue[..(3 * perElement)], r, s, t, strain);
        Model.MaterialOf(element).Stress(strain, stress);
        return TetElement.ToTensor(stress, engineeringShear: false);
    }

    private void Gather(int element, Span<Vector3d> positions, Span<double> ue)
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
    public IReadOnlyList<MeshField> Fields(string lengthUnits = "mm", string stressUnits = "MPa")
    {
        var vonMises = NodalVonMises;
        var values = new double[vonMises.Count];
        for (int v = 0; v < values.Length; v++)
            values[v] = vonMises[v];
        return
        [
            MeshField.Vector(FieldNames.Displacement, lengthUnits, _displacement),
            MeshField.Scalar(FieldNames.VonMises, stressUnits, values),
        ];
    }

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

        var vonMises = NodalVonMises;
        int perFacet = Mesh.NodesPerFacet;

        // Exact-position lookup over the analysis boundary's nodes. Exact bits, the
        // codebase's weld doctrine: two independently computed positions are not equal,
        // and two shared ones are.
        var byPosition = new Dictionary<Vector3d, int>();
        for (int f = 0; f < Mesh.FacetCount; f++)
        {
            foreach (int node in Mesh.Facet(f))
                byPosition.TryAdd(Mesh.Position(node), node);
        }

        var boxes = new Aabb[Mesh.FacetCount];
        for (int f = 0; f < Mesh.FacetCount; f++)
        {
            var facet = Mesh.Facet(f);
            boxes[f] = Aabb.Empty
                .Union(Mesh.Position(facet[0]))
                .Union(Mesh.Position(facet[1]))
                .Union(Mesh.Position(facet[2]));
        }
        var bvh = Bvh.Build(boxes);

        var displacement = new Vector3d[displayMesh.VertexCount];
        var stress = new double[displayMesh.VertexCount];
        double worst = 0;
        Span<double> shape = stackalloc double[6];

        for (int v = 0; v < displayMesh.VertexCount; v++)
        {
            var p = displayMesh.GetPosition(v);
            if (byPosition.TryGetValue(p, out int node))
            {
                displacement[v] = _displacement[node];
                stress[v] = vonMises[node];
                continue;
            }

            if (!bvh.Nearest(p, facet => DistanceToFacet(facet, p), out int nearest, out double distance))
                throw new FeaException(
                    "The analysis mesh has no boundary facets to sample from.");
            worst = Math.Max(worst, distance);

            var f = Mesh.Facet(nearest);
            var a = Mesh.Position(f[0]);
            var b = Mesh.Position(f[1]);
            var c = Mesh.Position(f[2]);
            var q = Distance3d.ClosestPointOnTriangle(p, a, b, c);
            var (l0, l1, l2) = Barycentric(q, a, b, c);
            TetElement.TriangleShapeValues(Mesh.Order, l0, l1, l2, shape);

            var u = Vector3d.Zero;
            double s = 0;
            for (int i = 0; i < perFacet; i++)
            {
                u += _displacement[f[i]] * shape[i];
                s += vonMises[f[i]] * shape[i];
            }
            displacement[v] = u;
            stress[v] = s;
        }

        maxSampleDistance = worst;
        return
        [
            MeshField.Vector(FieldNames.Displacement, lengthUnits, displacement),
            MeshField.Scalar(FieldNames.VonMises, stressUnits, stress),
        ];
    }

    /// <summary><see cref="SampleOnto(HalfEdgeMesh, out double, string, string)"/> without
    /// the sampling-distance diagnostic.</summary>
    public IReadOnlyList<MeshField> SampleOnto(HalfEdgeMesh displayMesh) =>
        SampleOnto(displayMesh, out _);

    private double DistanceToFacet(int facet, in Vector3d p)
    {
        var f = Mesh.Facet(facet);
        var closest = Distance3d.ClosestPointOnTriangle(
            p, Mesh.Position(f[0]), Mesh.Position(f[1]), Mesh.Position(f[2]));
        return closest.DistanceTo(p);
    }

    private static (double L0, double L1, double L2) Barycentric(
        in Vector3d p, in Vector3d a, in Vector3d b, in Vector3d c)
    {
        var v0 = b - a;
        var v1 = c - a;
        var v2 = p - a;
        double d00 = v0.Dot(v0), d01 = v0.Dot(v1), d11 = v1.Dot(v1);
        double d20 = v2.Dot(v0), d21 = v2.Dot(v1);
        double denominator = d00 * d11 - d01 * d01;
        if (denominator == 0)
            return (1, 0, 0); // Exact-zero guard: a degenerate facet has no interior.
        double l1 = (d11 * d20 - d01 * d21) / denominator;
        double l2 = (d00 * d21 - d01 * d20) / denominator;
        return (1.0 - l1 - l2, l1, l2);
    }

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

    /// <inheritdoc/>
    public override string ToString() =>
        $"max displacement {MaxDisplacement:G6}, max von Mises {MaxVonMises:G6} over "
        + $"{Mesh.ElementCount:N0} elements";
}
