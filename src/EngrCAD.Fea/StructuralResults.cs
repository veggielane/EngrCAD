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
    private readonly IReadOnlyList<Vector3d> _displacementView;
    private readonly IReadOnlyList<Vector3d> _reactionView;
    private IReadOnlyList<SymmetricTensor3>? _nodalStress;
    private IReadOnlyList<double>? _nodalVonMises;
    private SymmetricTensor3[]? _averagedStress;
    private SymmetricTensor3[]? _averagedStressPerRegion;
    // The recovery and the error estimate are readonly record STRUCTS, cached boxed: an
    // object reference publishes atomically where a Nullable<T> write of a multi-word struct
    // can tear, and tearing would hand a concurrent reader half of one value and half of
    // another — corruption, not merely wasted work.
    private object? _recovered;
    private object? _errorEstimate;

    // Concurrency contract: concurrent READS are safe — every lazy cache below is a pure
    // deterministic function of immutable data published by a single reference assignment,
    // so racing first reads duplicate the work and agree on the value. What is NOT safe is
    // setting Averaging or Recovery while another thread reads: the setters clear the caches,
    // and a reader between the clear and its own recompute may pair values from different
    // settings. A results object still belongs to whoever solved for it; the guarantee
    // exists so a viewer that only READS resolved fields off another thread cannot corrupt
    // one.
    internal StructuralResults(
        StructuralModel model,
        Vector3d[] displacement,
        Vector3d[] reaction,
        FeaSolveReport report)
    {
        Model = model;
        _displacement = displacement;
        _reaction = reaction;
        _displacementView = Array.AsReadOnly(displacement);
        _reactionView = Array.AsReadOnly(reaction);
        Report = report;
    }

    /// <summary>The model that was solved.</summary>
    public StructuralModel Model { get; }

    /// <summary>The analysis mesh.</summary>
    public AnalysisMesh Mesh => Model.Mesh;

    /// <summary>What the solve did.</summary>
    public FeaSolveReport Report { get; }

    /// <summary>Nodal displacements, indexed by node. A read-only VIEW, not the internal
    /// array — the same array feeds every derived stress pass, so handing it out castable
    /// back to <c>Vector3d[]</c> would let a consumer silently change the answer.</summary>
    public IReadOnlyList<Vector3d> Displacement => _displacementView;

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
    public IReadOnlyList<Vector3d> Reactions => _reactionView;

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
            _averagedStress = null;
            _averagedStressPerRegion = null;
            // The recovery and the estimate consume AveragedStress too (boundary nodes fall
            // back to it), so they are stale under a changed averaging as well — clearing
            // only the averaged caches would let a later superconvergent read pair a fresh
            // average with a recovery computed under the old one.
            _recovered = null;
            _errorEstimate = null;
        }
    } = NodalAveraging.VolumeWeighted;

    /// <summary>
    /// Which recovery <see cref="NodalStress"/> reports. <see cref="StressRecovery.Direct"/>
    /// is the default, so nothing about an existing result moves.
    ///
    /// <para><see cref="StressRecovery.Superconvergent"/> converges one order faster and
    /// costs a small least-squares solve per corner node. It is not the default for the
    /// reason `FeaSolveMethod.Direct` is not: every verification figure this project quotes
    /// was measured through the simple path, and making the better answer the default would
    /// silently change every one of them. It is also NOT better everywhere — a recovered
    /// field is smooth by construction, so at a genuine discontinuity (a material interface,
    /// a re-entrant corner) it smooths harder than averaging does.</para>
    /// </summary>
    public StressRecovery Recovery
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
    } = StressRecovery.Direct;

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

    /// <summary>
    /// The nodal stress field, by whichever <see cref="Recovery"/> is selected. A read-only
    /// view — the array behind it is a cache shared with the error estimator.
    ///
    /// <para><b>One value per node, which at a MATERIAL INTERFACE is one value too few.</b>
    /// Stress is genuinely discontinuous where two materials meet — only the traction across
    /// the interface is continuous — so a node on that interface has one right answer per
    /// material and this field averages them. That is a property of a node-indexed field
    /// rather than of the recovery, and it is reported rather than left to be discovered:
    /// <see cref="AnalysisMesh.InterfaceNodeCount"/> says how many nodes are affected (zero
    /// for a single-region mesh and for disjoint bodies) and
    /// <see cref="NodalStressIn"/> gives the per-material value.</para>
    /// </summary>
    public IReadOnlyList<SymmetricTensor3> NodalStress =>
        Publish(ref _nodalStress, () => Array.AsReadOnly(
            Recovery == StressRecovery.Direct ? AveragedStress : RecoveredNodalStress.Stress));

    /// <summary>
    /// The nodal stress at one node <b>as seen from one material region</b> — the honest
    /// value where <see cref="NodalStress"/> can only report a blend.
    ///
    /// <para>Equal to <see cref="NodalStress"/> at every node touching one region, by
    /// identical arithmetic rather than by tolerance, so switching to this accessor cannot
    /// move an answer away from an interface. Refused by name when the node touches no
    /// element of that region; <see cref="AnalysisMesh.RegionsAt"/> says which regions it
    /// does touch.</para>
    /// </summary>
    /// <param name="region">A material region id (see <see cref="AnalysisMesh.Regions"/>).</param>
    /// <param name="node">The node.</param>
    public SymmetricTensor3 NodalStressIn(int region, int node)
    {
        RequireNode(node);
        int slot = Mesh.RegionSlot(node, region);
        return Recovery == StressRecovery.Direct
            ? AveragedStressPerRegion[slot]
            : RecoveredNodalStress.SlotStress[slot];
    }

    /// <summary>The von Mises stress at one node as seen from one material region — the
    /// per-material reading of <see cref="NodalVonMises"/> (see
    /// <see cref="NodalStressIn"/>).</summary>
    public double NodalVonMisesIn(int region, int node) =>
        TetElement.VonMises(NodalStressIn(region, node));

    /// <summary>The directly averaged nodal stress, whatever <see cref="Recovery"/> says —
    /// the fallback the recovery itself uses, and the field the error estimator's residual
    /// is measured against having replaced.</summary>
    private SymmetricTensor3[] AveragedStress =>
        Publish(ref _averagedStress, () => ComputeNodalStress(perRegion: false));

    /// <summary>
    /// The directly averaged stress per (node, region) SLOT — the same rule
    /// <see cref="Averaging"/> states, restricted to one material.
    /// <para>Where a slot IS a node (every mesh with one region, and every mesh of disjoint
    /// bodies) this is <see cref="AveragedStress"/> itself rather than a second array holding
    /// the same numbers: the two accumulations would be the same additions in the same order,
    /// so computing it twice would buy nothing but a chance to drift.</para>
    /// </summary>
    private SymmetricTensor3[] AveragedStressPerRegion =>
        Mesh.RegionSlotsAreNodes
            ? AveragedStress
            : Publish(ref _averagedStressPerRegion, () => ComputeNodalStress(perRegion: true));

    private SuperconvergentRecovery.RecoveredStress RecoveredNodalStress =>
        (SuperconvergentRecovery.RecoveredStress)Publish(
            ref _recovered,
            () => (object)SuperconvergentRecovery.Recover(this, AveragedStressPerRegion));

    /// <summary>
    /// First-publish-wins lazy initialization (the <see cref="AnalysisMesh"/> pattern):
    /// compute on a miss, install by CompareExchange, return whichever copy won. Every
    /// compute routed through here is deterministic over data that does not change under it,
    /// which is what makes a racing loser's value identical to the winner's.
    /// </summary>
    private static T Publish<T>(ref T? field, Func<T> compute) where T : class
    {
        var current = field;
        if (current is not null)
            return current;
        var computed = compute();
        return Interlocked.CompareExchange(ref field, computed, null) ?? computed;
    }

    /// <summary>
    /// The Zienkiewicz-Zhu error estimate: the energy-norm distance between the element
    /// stress field and its superconvergent recovery, per element and overall.
    ///
    /// <para>Always computed from the RECOVERED field, whatever <see cref="Recovery"/> is
    /// set to, because that is what makes it an estimator: the recovered field is a better
    /// approximation than the finite-element one, so the gap between them estimates the
    /// error in the finite-element one. Using the averaged field instead is the older and
    /// markedly weaker form of the same idea.</para>
    /// </summary>
    public ErrorEstimate ErrorEstimate =>
        (ErrorEstimate)Publish(
            ref _errorEstimate, () => (object)ComputeErrorEstimate(RecoveredNodalStress));

    /// <summary>
    /// The recovery, with the material-region rule optionally switched OFF — the test seam
    /// that lets the defect the rule fixes be MEASURED rather than asserted. Production never
    /// asks for false; on a single-region mesh the two agree bit for bit, which is what makes
    /// the comparison a measurement of the interface and of nothing else.
    /// </summary>
    internal SuperconvergentRecovery.RecoveredStress RecoverStress(bool respectRegions) =>
        respectRegions
            ? RecoveredNodalStress
            : SuperconvergentRecovery.Recover(this, AveragedStress, respectRegions: false);

    /// <summary><see cref="ErrorEstimate"/> over a recovery the caller supplies — the same
    /// seam, so the estimate's own sensitivity to a cross-interface fit is measurable.</summary>
    internal ErrorEstimate ErrorEstimateFrom(SuperconvergentRecovery.RecoveredStress recovered) =>
        ComputeErrorEstimate(recovered);

    /// <summary>Averaged von Mises stress at every node — what a colour map shows.</summary>
    public IReadOnlyList<double> NodalVonMises =>
        Publish(ref _nodalVonMises, () =>
        {
            var stress = NodalStress;
            var values = new double[stress.Count];
            for (int v = 0; v < values.Length; v++)
                values[v] = TetElement.VonMises(stress[v]);
            return Array.AsReadOnly(values);
        });

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
        Model.ElasticityOf(element).Stress(strain, stress);
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
        var law = Model.ElasticityOf(element);
        double expansion = Model.MaterialOf(element).ThermalExpansion;
        // The same two-part branch the LOAD takes: an expansion stated on the law wins over
        // the material's scalar whether or not the law is elastically isotropic, and the
        // scalar route is legal only for an isotropic law, whose Lame parameters ARE the
        // material's. The anisotropic-law-with-a-material-scalar case is refused at the
        // load, so it cannot reach here.
        bool scalar = law.IsIsotropic && !law.StatesOwnThermalExpansion;
        // Exact-zero semantic test, matching the load: no stated expansion, no thermal
        // strain to remove.
        if (scalar ? expansion == 0 : !law.HasThermalStrain)
            return;

        int perElement = Mesh.NodesPerElement;
        Span<double> values = stackalloc double[10];
        var nodes = Mesh.Element(element);
        for (int i = 0; i < perElement; i++)
            values[i] = deltaT[nodes[i]];

        double rise = ThermalElement.InterpolateAt(Mesh.Order, values[..perElement], r, s, t);
        if (scalar)
        {
            double thermal = expansion * rise;
            strain[0] -= thermal;
            strain[1] -= thermal;
            strain[2] -= thermal;
            // Shear is untouched: an isotropic material's thermal strain is a pure dilatation.
            return;
        }

        // A directional expansion rotated off its own axes has SHEAR components, so all six
        // are subtracted. The law is asked for the vector rather than the vector being
        // rebuilt here, so the strain removed and the load applied come from one number.
        var free = law.ThermalStrainPerDegree;
        for (int i = 0; i < 6; i++)
            strain[i] -= free[i] * rise;
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

    private ErrorEstimate ComputeErrorEstimate(SuperconvergentRecovery.RecoveredStress recovered)
    {
        int perElement = Mesh.NodesPerElement;
        // The integrand is the SQUARE of a difference between a degree-p field and a
        // degree-(p-1) one, i.e. degree 2p; the rule is chosen to be exact for it rather
        // than reused from the stiffness, whose 2(p-1) would under-integrate the estimate
        // and make it look better than it is.
        var rule = Mesh.Order == ElementOrder.Linear
            ? TetQuadrature.Degree3
            : TetQuadrature.Degree5;

        var perElementError = new double[Mesh.ElementCount];
        double totalError = 0, totalEnergy = 0;

        Span<Vector3d> positions = stackalloc Vector3d[10];
        Span<double> ue = stackalloc double[30];
        Span<double> stress = stackalloc double[6];
        Span<double> smooth = stackalloc double[6];
        Span<double> shape = stackalloc double[10];
        Span<Vector3d> grad = stackalloc Vector3d[10];

        // Read from the RECOVERY, not from the mesh: a recovery run with regions collapsed
        // has node-indexed slots whatever the mesh's own slots are.
        bool perRegion = !recovered.SlotsAreNodes;

        for (int e = 0; e < Mesh.ElementCount; e++)
        {
            Gather(e, positions, ue);
            var compliance = Model.ElasticityOf(e).ComplianceMatrix;
            var nodes = Mesh.Element(e);
            // An element reads its OWN material's recovered values, never the node-indexed
            // blend: at an interface element the blend carries the other material's stress,
            // and the estimator would book that modelling jump as this element's error.
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

                VoigtStressAt(e, positions[..perElement], ue[..(3 * perElement)], r, s, t, stress);

                // The recovered field INSIDE the element is its nodal values interpolated by
                // the element's own shape functions, which is what makes the two fields
                // comparable pointwise rather than only at the nodes.
                smooth.Clear();
                for (int i = 0; i < perElement; i++)
                {
                    var value = perRegion
                        ? recovered.SlotStress[Mesh.RegionSlot(nodes[i], region)]
                        : recovered.Stress[nodes[i]];
                    double n = shape[i];
                    smooth[0] += n * value.Xx;
                    smooth[1] += n * value.Yy;
                    smooth[2] += n * value.Zz;
                    smooth[3] += n * value.Xy;
                    smooth[4] += n * value.Yz;
                    smooth[5] += n * value.Xz;
                }

                error += weight * ComplianceNorm(compliance, smooth, stress);
                energy += weight * ComplianceNorm(compliance, stress, default);
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

        // NOTHING was recovered, so the "error" is the distance from the finite-element
        // field to itself. Reporting that as a number would say the mesh is perfect on
        // exactly the mesh too coarse to assess — measured on a 24-element box with no
        // interior corner node at all: 9.9e-15 against a true error of 0.31. NaN is the
        // established spelling here for "not small, UNKNOWN" (HarmonicResponse's truncation
        // error without a static solution), and it is the only answer that cannot be
        // mistaken for good news.
        if (recovered.FallbackNodes >= Mesh.NodeCount)
        {
            errorNorm = double.NaN;
            relative = double.NaN;
            Array.Fill(perElementError, double.NaN);
        }

        return new ErrorEstimate(
            perElementError,
            errorNorm,
            energyNorm,
            relative,
            recovered.FallbackNodes,
            recovered.RankDeficientPatches);
    }

    /// <summary>
    /// <c>(a - b)' S (a - b)</c> with S the compliance — the energy inner product of a
    /// stress difference. Passing an empty <paramref name="b"/> gives the norm of
    /// <paramref name="a"/> itself, which is what the denominator wants.
    /// </summary>
    private static double ComplianceNorm(
        ReadOnlySpan<double> compliance, ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        Span<double> d = stackalloc double[6];
        for (int i = 0; i < 6; i++)
            d[i] = b.IsEmpty ? a[i] : a[i] - b[i];
        double sum = 0;
        for (int i = 0; i < 6; i++)
        {
            double row = 0;
            for (int j = 0; j < 6; j++)
                row += compliance[i * 6 + j] * d[j];
            sum += d[i] * row;
        }
        return sum;
    }

    /// <summary>
    /// The averaged nodal stress, indexed either by NODE or by (node, region) SLOT.
    /// <para>One implementation for both: the per-region pass is the same loop writing to a
    /// different index, so the two cannot disagree about the averaging rule, the thermal
    /// subtraction or the constitutive law — and for a mesh whose slots are its nodes the
    /// index expression is the node itself, so the two are the same additions in the same
    /// order.</para>
    /// </summary>
    private SymmetricTensor3[] ComputeNodalStress(bool perRegion)
    {
        int perElement = Mesh.NodesPerElement;
        int slots = perRegion ? Mesh.RegionSlotCount : Mesh.NodeCount;
        var accumulated = new SymmetricTensor3[slots];
        var weights = new double[slots];

        Span<Vector3d> positions = stackalloc Vector3d[10];
        Span<double> ue = stackalloc double[30];
        Span<double> strain = stackalloc double[6];
        Span<double> stress = stackalloc double[6];

        for (int e = 0; e < Mesh.ElementCount; e++)
        {
            var nodes = Mesh.Element(e);
            Gather(e, positions, ue);
            var law = Model.ElasticityOf(e);
            int region = perRegion ? Mesh.RegionOf(e) : 0;
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
                law.Stress(strain, stress);
                int target = perRegion ? Mesh.RegionSlot(nodes[i], region) : nodes[i];
                accumulated[target] += TetElement.ToTensor(stress, engineeringShear: false) * weight;
                weights[target] += weight;
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
