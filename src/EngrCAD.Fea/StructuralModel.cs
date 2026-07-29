using EngrCAD.Core;

namespace EngrCAD.Fea;

/// <summary>Thrown when a structural model or solve cannot proceed. Every message names
/// what failed and, where there is one, the way out.</summary>
public sealed class FeaException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public FeaException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and an inner cause.</summary>
    public FeaException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Which translational degrees of freedom a support removes.</summary>
[Flags]
public enum Dof
{
    /// <summary>Nothing restrained.</summary>
    None = 0,

    /// <summary>Displacement along X.</summary>
    X = 1,

    /// <summary>Displacement along Y.</summary>
    Y = 2,

    /// <summary>Displacement along Z.</summary>
    Z = 4,

    /// <summary>All three translations — a fully fixed support.</summary>
    All = X | Y | Z,
}

/// <summary>
/// One boundary facet as a selector sees it: its index, its tag, and the geometry a
/// predicate needs. The tag is <see cref="TetFacet.SourceTriangle"/> — the caller's
/// <c>TetMeshOptions.FacetTags</c> entry when one was supplied (B-Rep face ids, say), and
/// the raw input-triangle index otherwise.
/// </summary>
/// <param name="Index">Facet index in the analysis mesh.</param>
/// <param name="Tag">The facet's tag.</param>
/// <param name="Centroid">Centroid of the facet's corner triangle.</param>
/// <param name="Normal">Outward unit normal (zero for a degenerate facet).</param>
/// <param name="Area">Facet area.</param>
public readonly record struct FacetRef(int Index, int Tag, Vector3d Centroid, Vector3d Normal, double Area);

/// <summary>
/// Facet selectors — how a boundary condition says <i>which</i> surface it acts on.
///
/// <para><b>Tags are the durable handle</b> (<see cref="Tag"/>): pass B-Rep face ids
/// through <c>TetMeshOptions.FacetTags</c> and a condition names a face rather than a
/// coordinate, so it survives a mesh change the way the selector vocabulary elsewhere in
/// EngrCAD survives a regeneration. The geometric selectors are for the cases where no
/// tag exists — an imported STL, or a quick script.</para>
/// </summary>
public static class Facets
{
    /// <summary>Every boundary facet.</summary>
    public static Func<FacetRef, bool> All => _ => true;

    /// <summary>Facets carrying one tag.</summary>
    public static Func<FacetRef, bool> Tag(int tag) => f => f.Tag == tag;

    /// <summary>Facets carrying any of several tags.</summary>
    public static Func<FacetRef, bool> Tags(params int[] tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var set = new HashSet<int>(tags);
        return f => set.Contains(f.Tag);
    }

    /// <summary>
    /// Facets lying in a plane: centroid within <paramref name="tolerance"/> of it AND
    /// normal parallel to the plane's. Both tests matter — the centroid alone would pick
    /// up a facet that merely straddles the plane, and for a straight-sided facet the
    /// pair is exact (a planar triangle whose normal is parallel and whose centroid is on
    /// the plane lies in it entirely).
    /// </summary>
    public static Func<FacetRef, bool> OnPlane(
        Vector3d point, Vector3d normal, double relativeTolerance = 1e-3)
    {
        if (!normal.TryNormalize(Tolerance.Default, out var n))
            throw new FeaException($"Facets.OnPlane needs a non-zero plane normal; got {normal}.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(relativeTolerance);
        double offset = n.Dot(point);
        return f =>
            // Distance measured against the facet's OWN size (the square root of its
            // area) — the scale-free tier. An absolute length here would be wrong in both
            // directions away from unit scale: it selects everything on a 5 um part and
            // nothing on a 2 m weldment. A facet genuinely in the plane sits at round-off
            // while the nearest one that is not sits about its own size away, so the two
            // are separated by orders of magnitude and the constant can be loose.
            Math.Abs(n.Dot(f.Centroid) - offset) <= relativeTolerance * Math.Sqrt(f.Area)
            // ...and its normal is parallel to the plane's. This test is DIMENSIONLESS —
            // a dot product of unit vectors — so its epsilon is an angle, not a length.
            && Math.Abs(n.Dot(f.Normal)) >= 1.0 - ParallelTolerance;
    }

    /// <summary>How far a facet's normal may lean off a plane's and still count as lying
    /// in it: 1e-6 on a dot product of unit vectors, about 0.08 degrees. Dimensionless, so
    /// it carries no model scale.</summary>
    private const double ParallelTolerance = 1e-6;

    /// <summary>Facets whose outward normal is within <paramref name="maxAngleDegrees"/>
    /// of <paramref name="direction"/> — "the top surface", "the loaded flank".</summary>
    public static Func<FacetRef, bool> FacingAlong(Vector3d direction, double maxAngleDegrees = 45)
    {
        var d = direction.Normalized();
        double minimumDot = Math.Cos(maxAngleDegrees * Math.PI / 180.0);
        return f => f.Normal.Dot(d) >= minimumDot;
    }

    /// <summary>Facets whose centroid lies inside a box.</summary>
    public static Func<FacetRef, bool> InBox(Aabb box) => f => box.Contains(f.Centroid);

    /// <summary>Both predicates.</summary>
    public static Func<FacetRef, bool> And(Func<FacetRef, bool> a, Func<FacetRef, bool> b) =>
        f => a(f) && b(f);

    /// <summary>Either predicate.</summary>
    public static Func<FacetRef, bool> Or(Func<FacetRef, bool> a, Func<FacetRef, bool> b) =>
        f => a(f) || b(f);
}

/// <summary>
/// A small-strain linear-elastic structural model: an <see cref="AnalysisMesh"/>, a
/// material per region, supports, and loads — everything
/// <see cref="StructuralSolver.Solve(StructuralModel)"/> needs.
///
/// <para><b>A builder, not a value.</b> Conditions accumulate by calling methods, each of
/// which returns the model so calls chain. That is the shape the job has (a load case is
/// assembled from many statements) and it matches <c>Sketch</c>; a solve does not mutate
/// the model, so one model can be solved twice.</para>
///
/// <para><b>Loads are reduced to nodal forces as they are added.</b> A pressure, a
/// traction, a total force or gravity all become consistent nodal forces immediately, so
/// <see cref="AppliedForce"/> is always the true resultant and there is no deferred
/// interpretation to get wrong later. The consistent weights come from
/// <c>TetElement</c>'s quadrature, which is what makes a 10-node facet's <i>zero</i>
/// corner loads and a 10-node element's <i>negative</i> corner body loads fall out
/// rather than being special-cased.</para>
///
/// <para><b>A selector that matches nothing is refused at the call.</b> A mis-typed plane
/// offset that silently fixes no nodes surfaces much later as a singular system with a
/// message about rigid-body modes; refusing where the mistake was made names the tags
/// that do exist.</para>
/// </summary>
public sealed class StructuralModel
{
    private readonly Dof[] _restraint;
    private readonly Vector3d[] _prescribed;
    private readonly Vector3d[] _force;
    private readonly Dictionary<int, Material> _materials = [];
    private readonly List<string> _conditions = [];
    private double[]? _deltaT;

    /// <summary>A model over an analysis mesh with one material everywhere.</summary>
    public StructuralModel(AnalysisMesh mesh, Material material)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(material);
        if (mesh.ElementCount == 0)
            throw new FeaException("The analysis mesh has no elements.");
        RequireElasticity(material);
        Mesh = mesh;
        DefaultMaterial = material;
        _restraint = new Dof[mesh.NodeCount];
        _prescribed = new Vector3d[mesh.NodeCount];
        _force = new Vector3d[mesh.NodeCount];
    }

    /// <summary>A model over a linear (4-node) tet mesh.</summary>
    public StructuralModel(TetMesh mesh, Material material)
        : this(AnalysisMesh.Of(mesh), material) { }

    /// <summary>A model over a quadratic (10-node) tet mesh.</summary>
    public StructuralModel(QuadraticTetMesh mesh, Material material)
        : this(AnalysisMesh.Of(mesh), material) { }

    /// <summary>
    /// A multi-material model built from the SAME <see cref="AnalysisBody"/> list that was
    /// meshed — region <c>i</c> takes <c>bodies[i].Material</c>, so nothing has to restate
    /// a region id and the materials cannot drift from the geometry.
    ///
    /// <code>
    /// var bodies = new[] { new AnalysisBody(steelHalf, Materials.Steel, "steel"),
    ///                      new AnalysisBody(alloyHalf, Materials.Aluminium6061, "alloy") };
    /// var model  = StructuralModel.For(TetMesher.Mesh(bodies), bodies);
    /// </code>
    ///
    /// <para>Refuses, by name and before anything is assembled: a body with no material, a
    /// mesh region no body declares, a declared body that contributed no elements, and (per
    /// <see cref="SetMaterial"/>) a material with no Young's modulus.</para>
    /// </summary>
    public static StructuralModel For(AnalysisMesh mesh, IReadOnlyList<AnalysisBody> bodies)
    {
        AnalysisBodies.Require(mesh, bodies, "structural");
        var model = new StructuralModel(mesh, bodies[0].Material!);
        for (int b = 0; b < bodies.Count; b++)
            model.SetMaterial(b, bodies[b].Material!);
        return model;
    }

    /// <summary>The linear (4-node) spelling of
    /// <see cref="For(AnalysisMesh, IReadOnlyList{AnalysisBody})"/>.</summary>
    public static StructuralModel For(TetMesh mesh, IReadOnlyList<AnalysisBody> bodies) =>
        For(AnalysisMesh.Of(mesh), bodies);

    /// <summary>The quadratic (10-node) spelling of
    /// <see cref="For(AnalysisMesh, IReadOnlyList{AnalysisBody})"/>.</summary>
    public static StructuralModel For(QuadraticTetMesh mesh, IReadOnlyList<AnalysisBody> bodies) =>
        For(AnalysisMesh.Of(mesh), bodies);

    /// <summary>The analysis mesh.</summary>
    public AnalysisMesh Mesh { get; }

    /// <summary>The material used for any region with no explicit assignment.</summary>
    public Material DefaultMaterial { get; }

    /// <summary>Human-readable list of the conditions applied, in order — what a report prints.</summary>
    public IReadOnlyList<string> Conditions => _conditions;

    /// <summary>The material of one element, by its region id.</summary>
    public Material MaterialOf(int element) =>
        _materials.TryGetValue(Mesh.RegionOf(element), out var m) ? m : DefaultMaterial;

    /// <summary>Assigns a material to one region id (from multi-body meshing).</summary>
    public StructuralModel SetMaterial(int region, Material material)
    {
        ArgumentNullException.ThrowIfNull(material);
        RequireElasticity(material);
        _materials[region] = material;
        _conditions.Add($"material '{material.Name}' on region {region}");
        return this;
    }

    /// <summary>
    /// Refuses a material with no Young's modulus, by name, at the point it is attached to
    /// a structural model.
    ///
    /// <para><b>This is where the refusal belongs, and it used to sit in
    /// <see cref="Material"/>'s constructor.</b> A material with a density and no modulus is
    /// a perfectly good <i>document</i> material — most of a bill of materials is made of
    /// them — so building one must be legal; only an analysis that integrates a stiffness
    /// needs E, and only it can say so by name. Refusing here rather than at the solve is the
    /// model's own doctrine (a selector that matches nothing is refused at the call), and it
    /// matters more than usual because <see cref="Material.Lambda"/> and
    /// <see cref="Material.Mu"/> are both <i>zero</i> without a modulus: the solve would not
    /// be inaccurate, it would assemble an identically zero stiffness and then report
    /// rigid-body modes for a model that has none.</para>
    /// </summary>
    private static void RequireElasticity(Material material)
    {
        if (material.HasElasticity)
            return;
        throw new FeaException(
            $"A structural model needs elastic properties, and the material '{material.Name}' "
            + "states no Young's modulus. Lame's parameters are then both zero, so the "
            + "stiffness matrix would be identically zero rather than merely wrong. A material "
            + "with only a name and a density is a legal DOCUMENT material (it is what a bill "
            + "of materials is made of), which is why this is refused here and not when the "
            + "material is built: give it a modulus with the constructor's youngsModulus and "
            + "poissonsRatio, or call Material.WithElasticity(E, nu) - remembering that the "
            + "mm/N/MPa/tonne system wants E in MPa, so steel is 210000.");
    }

    /// <summary>The restraint mask on one node.</summary>
    public Dof RestraintOf(int node)
    {
        RequireNode(node);
        return _restraint[node];
    }

    /// <summary>The prescribed displacement of one node (components without a restraint are ignored).</summary>
    public Vector3d PrescribedOf(int node)
    {
        RequireNode(node);
        return _prescribed[node];
    }

    /// <summary>The applied nodal force on one node.</summary>
    public Vector3d ForceOf(int node)
    {
        RequireNode(node);
        return _force[node];
    }

    /// <summary>Number of nodes carrying at least one restraint.</summary>
    public int RestrainedNodeCount => _restraint.Count(d => d != Dof.None);

    /// <summary>Number of restrained degrees of freedom.</summary>
    public int RestrainedDofCount
    {
        get
        {
            int total = 0;
            foreach (var d in _restraint)
                total += System.Numerics.BitOperations.PopCount((uint)d);
            return total;
        }
    }

    /// <summary>Resultant of every applied nodal force — the load the supports must react.</summary>
    public Vector3d AppliedForce
    {
        get
        {
            var sum = Vector3d.Zero;
            foreach (var f in _force)
                sum += f;
            return sum;
        }
    }

    /// <summary>Resultant moment of the applied loads about <paramref name="about"/>.</summary>
    public Vector3d AppliedMoment(Vector3d about)
    {
        var sum = Vector3d.Zero;
        for (int v = 0; v < _force.Length; v++)
        {
            // Exact-zero skip: an unloaded node contributes exactly nothing to the moment,
            // so this is a semantic test rather than a tolerance.
            if (_force[v] != Vector3d.Zero)
                sum += (Mesh.Position(v) - about).Cross(_force[v]);
        }
        return sum;
    }

    // ---- supports -------------------------------------------------------------------

    /// <summary>
    /// Fully or partially fixes every node of the selected facets.
    /// <para><b>A fix is a prescribe-to-zero</b>, so it CLEARS any displacement already
    /// prescribed on the axes it names. Leaving the old value in place would make
    /// <c>Prescribe(...).Fix(...)</c> silently keep an enforced deflection while both the
    /// API and the condition log said "fix"; spelling it this way also makes the two
    /// orderings commute.</para>
    /// </summary>
    public StructuralModel Fix(Func<FacetRef, bool> facets, Dof dofs = Dof.All)
    {
        ArgumentNullException.ThrowIfNull(facets);
        var nodes = SelectNodes(facets, nameof(Fix));
        foreach (int node in nodes)
        {
            _restraint[node] |= dofs;
            _prescribed[node] = Combine(_prescribed[node], Vector3d.Zero, dofs);
        }
        _conditions.Add($"fix {dofs} on {nodes.Count} nodes");
        return this;
    }

    /// <summary>Fixes every node of the facets carrying one tag.</summary>
    public StructuralModel Fix(int tag, Dof dofs = Dof.All) => Fix(Facets.Tag(tag), dofs);

    /// <summary>Fixes one node directly — the escape hatch, and how a statically
    /// determinate 3-2-1 restraint is built. Clears any prescribed displacement on the
    /// axes it names (see <see cref="Fix(Func{FacetRef, bool}, Dof)"/>).</summary>
    public StructuralModel FixNode(int node, Dof dofs = Dof.All)
    {
        RequireNode(node);
        _restraint[node] |= dofs;
        _prescribed[node] = Combine(_prescribed[node], Vector3d.Zero, dofs);
        _conditions.Add($"fix {dofs} on node {node}");
        return this;
    }

    /// <summary>Prescribes a non-zero displacement on the selected facets' nodes (an
    /// enforced deflection; the reaction is what a support at that displacement carries).</summary>
    public StructuralModel Prescribe(Func<FacetRef, bool> facets, Vector3d displacement, Dof dofs = Dof.All)
    {
        ArgumentNullException.ThrowIfNull(facets);
        var nodes = SelectNodes(facets, nameof(Prescribe));
        foreach (int node in nodes)
        {
            _restraint[node] |= dofs;
            _prescribed[node] = Combine(_prescribed[node], displacement, dofs);
        }
        _conditions.Add($"prescribe {displacement} ({dofs}) on {nodes.Count} nodes");
        return this;
    }

    /// <summary>Prescribes a displacement on one node.</summary>
    public StructuralModel PrescribeNode(int node, Vector3d displacement, Dof dofs = Dof.All)
    {
        RequireNode(node);
        _restraint[node] |= dofs;
        _prescribed[node] = Combine(_prescribed[node], displacement, dofs);
        _conditions.Add($"prescribe {displacement} ({dofs}) on node {node}");
        return this;
    }

    // ---- loads ----------------------------------------------------------------------

    /// <summary>
    /// A uniform pressure on the selected facets. <b>Positive pressure pushes INTO the
    /// body</b> (against the outward normal), which is what "500 bar in this bore" means;
    /// a negative value is suction.
    /// </summary>
    public StructuralModel Pressure(Func<FacetRef, bool> facets, double pressure)
    {
        ArgumentNullException.ThrowIfNull(facets);
        int count = ApplySurfaceLoad(facets, nameof(Pressure), f => f.Normal * -pressure);
        _conditions.Add($"pressure {pressure:G6} on {count} facets");
        return this;
    }

    /// <summary>A uniform traction (force per unit area) on the selected facets.</summary>
    public StructuralModel Traction(Func<FacetRef, bool> facets, Vector3d traction)
    {
        ArgumentNullException.ThrowIfNull(facets);
        int count = ApplySurfaceLoad(facets, nameof(Traction), _ => traction);
        _conditions.Add($"traction {traction} on {count} facets");
        return this;
    }

    /// <summary>
    /// A total force spread over the selected facets as a uniform traction — the
    /// ergonomic "put 500 N on this face". The resultant is exact: the consistent weights
    /// over a facet sum to its area, so the distributed load sums back to
    /// <paramref name="totalForce"/> whatever the element order.
    /// </summary>
    public StructuralModel Force(Func<FacetRef, bool> facets, Vector3d totalForce)
    {
        ArgumentNullException.ThrowIfNull(facets);

        // ONE pass over the selector, and the matched list is what the load is applied to.
        // Asking the predicate a second time would let a stateful one answer differently,
        // so the traction would be derived from one facet set and applied to another —
        // and the exact-resultant guarantee above, which is the whole point of this
        // overload, would quietly stop holding.
        var matched = new List<int>();
        double area = 0;
        for (int f = 0; f < Mesh.FacetCount; f++)
        {
            if (!facets(Describe(f)))
                continue;
            matched.Add(f);
            area += Mesh.FacetArea(f).Length;
        }
        if (matched.Count == 0)
            throw NothingSelected(nameof(Force));
        if (!(area > 0))
            throw new FeaException(
                $"Force selected {matched.Count} facets whose total area is zero; "
                + "a traction cannot be derived.");

        var traction = totalForce / area;
        ApplyToFacets(matched, _ => traction);
        _conditions.Add($"force {totalForce} over {matched.Count} facets ({area:G6} area)");
        return this;
    }

    /// <summary>
    /// A body load from an acceleration field — gravity being the case that matters. The
    /// element's own material density is used, so a multi-material model weighs the right
    /// amount per region. Use <see cref="Materials.GravityMillimetres"/> for the
    /// mm/N/MPa/tonne unit system.
    /// </summary>
    public StructuralModel Gravity(Vector3d acceleration)
    {
        var order = Mesh.Order;
        int perElement = Mesh.NodesPerElement;
        var rule = TetQuadrature.For(order);
        Span<Vector3d> positions = stackalloc Vector3d[perElement];
        Span<double> weights = stackalloc double[perElement];
        double mass = 0;

        for (int e = 0; e < Mesh.ElementCount; e++)
        {
            double density = MaterialOf(e).Density;
            // Exact-zero semantic test: a weightless material contributes no body load.
            // Material's constructor has already refused a negative density.
            if (density == 0)
                continue;
            var nodes = Mesh.Element(e);
            for (int i = 0; i < perElement; i++)
                positions[i] = Mesh.Position(nodes[i]);
            TetElement.BodyLoadWeights(order, positions, rule, weights);
            var load = acceleration * density;
            for (int i = 0; i < perElement; i++)
                _force[nodes[i]] += load * weights[i];
            mass += density * Mesh.ElementVolume(e);
        }

        _conditions.Add($"gravity {acceleration} on mass {mass:G6}");
        return this;
    }

    /// <summary>
    /// A general body force per unit volume, evaluated at the elements' quadrature points
    /// — thermal loads, centrifugal loads, and the manufactured solutions a convergence
    /// study needs. Density is NOT applied: <paramref name="forcePerVolume"/> is the
    /// finished load, so a caller wanting weight should use <see cref="Gravity"/>.
    ///
    /// <para>Integrated with a <b>degree-5</b> rule rather than the element's own. The
    /// element rule is exact for what the element itself produces; a caller's field is not
    /// a polynomial of the element's making, and under-integrating a load caps a
    /// convergence study at the quadrature's order instead of the element's — a limit that
    /// looks exactly like a formulation defect.</para>
    /// </summary>
    public StructuralModel BodyForce(Func<Vector3d, Vector3d> forcePerVolume)
    {
        ArgumentNullException.ThrowIfNull(forcePerVolume);
        int perElement = Mesh.NodesPerElement;
        var positions = new Vector3d[perElement];
        var loads = new Vector3d[perElement];

        for (int e = 0; e < Mesh.ElementCount; e++)
        {
            var nodes = Mesh.Element(e);
            for (int i = 0; i < perElement; i++)
                positions[i] = Mesh.Position(nodes[i]);
            TetElement.BodyLoad(Mesh.Order, positions, TetQuadrature.Degree5, forcePerVolume, loads);
            for (int i = 0; i < perElement; i++)
                _force[nodes[i]] += loads[i];
        }

        _conditions.Add($"body force over {Mesh.ElementCount} elements");
        return this;
    }

    /// <summary>Adds a force directly to one node — the escape hatch for a point load.
    /// A genuine point load on a continuum has an infinite stress under it, so the value
    /// there does not converge; use it for equilibrium checks and treat the local peak as
    /// meaningless.</summary>
    public StructuralModel NodalForce(int node, Vector3d force)
    {
        RequireNode(node);
        _force[node] += force;
        _conditions.Add($"nodal force {force} on node {node}");
        return this;
    }

    /// <summary>Clears every applied load — including any thermal field — keeping supports
    /// and materials, which is how a second load case is built on one model.</summary>
    public StructuralModel ClearLoads()
    {
        Array.Clear(_force);
        _deltaT = null;
        _conditions.Add("loads cleared");
        return this;
    }

    // ---- thermal coupling ------------------------------------------------------------

    /// <summary>
    /// The temperature RISE at every node relative to the stress-free reference, or null
    /// when no thermal load has been applied. Read by the stress recovery, which must
    /// subtract the thermal strain — see <see cref="ThermalLoad(IReadOnlyList{double}, double)"/>.
    /// </summary>
    public IReadOnlyList<double>? ThermalDeltaT => _deltaT;

    /// <summary>
    /// A thermal-expansion load from a nodal temperature field: an initial strain
    /// <c>eps0 = alpha·(T - T_ref)</c> on the diagonal, reduced to consistent nodal forces
    /// exactly as every other load here is.
    ///
    /// <para><b>Two halves, and leaving out the second is the classic way to get this
    /// wrong.</b> The load is <c>integral(B'·D·eps0 dV)</c>, which for an isotropic
    /// material is <c>E/(1-2·nu)·alpha·dT</c> times the shape-function gradient — that is
    /// the half a solve needs. The other half is that the STRESS is
    /// <c>sigma = D·(eps - eps0)</c>, not <c>D·eps</c>: a bar free to expand develops the
    /// full thermal strain and <b>zero</b> stress, and a recovery that forgot the
    /// subtraction would report <c>E·alpha·dT</c> of stress in a bar that is under no load
    /// at all. Both halves live here, so applying the load is what makes the recovery
    /// correct; there is no second call to forget.</para>
    ///
    /// <para><b>The load is self-equilibrated by construction</b> (the shape functions are
    /// a partition of unity, so their gradients sum to exactly zero), which means a uniform
    /// temperature rise adds nothing to <see cref="AppliedForce"/> and the solver's
    /// equilibrium check keeps its meaning through a coupled solve.</para>
    ///
    /// <para>Repeated calls ACCUMULATE the temperature rise, because thermal loads
    /// superpose linearly like every other load in this model; <see cref="ClearLoads"/>
    /// clears the field along with the forces.</para>
    /// </summary>
    /// <param name="nodalTemperature">Temperature at every node of the analysis mesh.</param>
    /// <param name="referenceTemperature">The stress-free temperature.</param>
    public StructuralModel ThermalLoad(
        IReadOnlyList<double> nodalTemperature, double referenceTemperature)
    {
        ArgumentNullException.ThrowIfNull(nodalTemperature);
        if (nodalTemperature.Count != Mesh.NodeCount)
            throw new FeaException(
                $"The temperature field has {nodalTemperature.Count:N0} values but the analysis "
                + $"mesh has {Mesh.NodeCount:N0} nodes. A thermal load is per NODE of the "
                + "analysis mesh, which for quadratic elements includes the mid-edge nodes — so "
                + "a thermal solve on a LINEAR mesh cannot drive a QUADRATIC structural one "
                + "without resampling.");

        _deltaT ??= new double[Mesh.NodeCount];
        double lowest = double.MaxValue, highest = double.MinValue;
        for (int v = 0; v < _deltaT.Length; v++)
        {
            double rise = nodalTemperature[v] - referenceTemperature;
            _deltaT[v] += rise;
            lowest = Math.Min(lowest, rise);
            highest = Math.Max(highest, rise);
        }

        var order = Mesh.Order;
        int perElement = Mesh.NodesPerElement;
        // Degree 3, not the element's own rule. The integrand is dT (degree p) times a
        // shape-function gradient (degree p-1), i.e. degree 2p-1 = 3 for quadratic
        // elements; a degree-2 rule would under-integrate the very load a convergence study
        // is measuring. It is exact for linear elements several times over, and this pass
        // runs once, so there is nothing to gain by selecting per order.
        var rule = TetQuadrature.Degree3;
        var positions = new Vector3d[perElement];
        var values = new double[perElement];
        var loads = new Vector3d[perElement];
        bool anyExpansion = false;

        for (int e = 0; e < Mesh.ElementCount; e++)
        {
            var material = MaterialOf(e);
            double expansion = material.ThermalExpansion;
            // Exact-zero semantic test: a material with no stated expansion produces no
            // thermal load, whatever the temperature.
            if (expansion == 0)
                continue;
            anyExpansion = true;

            var nodes = Mesh.Element(e);
            for (int i = 0; i < perElement; i++)
            {
                positions[i] = Mesh.Position(nodes[i]);
                values[i] = nodalTemperature[nodes[i]] - referenceTemperature;
            }
            // E/(1-2nu) = 3K = 3·lambda + 2·mu, the hydrostatic stress a unit dilatation
            // produces. Taken from the material's own Lame parameters rather than
            // recomputed from E and nu, so the two cannot disagree.
            double modulus = 3.0 * material.Lambda + 2.0 * material.Mu;
            ThermalElement.ThermalExpansionLoad(
                order, positions, values, modulus, expansion, rule, loads);
            for (int i = 0; i < perElement; i++)
                _force[nodes[i]] += loads[i];
        }

        if (!anyExpansion)
        {
            throw new FeaException(
                "A thermal load was applied but no material in the model states a thermal "
                + "expansion coefficient, so the load is identically zero and the stress "
                + "recovery would have nothing to subtract. Build the material with its "
                + "thermalExpansion, or call Material.WithThermalExpansion(alpha).");
        }

        _conditions.Add(
            $"thermal load, dT {lowest:G6} to {highest:G6} about reference "
            + $"{referenceTemperature:G6}");
        return this;
    }

    /// <summary>
    /// A thermal-expansion load straight from a conduction solve — the coupled pipeline in
    /// one call.
    /// <para>The two models must share the SAME <see cref="AnalysisMesh"/> instance, and
    /// that is checked rather than assumed: node indices are what carries the temperature
    /// across, and two meshes of the same body with the same node COUNT can still number
    /// their nodes differently, which would silently apply each node's temperature to some
    /// other node.</para>
    /// </summary>
    /// <param name="thermal">The conduction solution.</param>
    /// <param name="referenceTemperature">The stress-free temperature.</param>
    public StructuralModel ThermalLoad(ThermalResults thermal, double referenceTemperature)
    {
        ArgumentNullException.ThrowIfNull(thermal);
        if (!ReferenceEquals(thermal.Mesh, Mesh))
            throw new FeaException(
                "The thermal results were computed on a different AnalysisMesh instance than "
                + "this structural model uses. A temperature field crosses by NODE INDEX, and "
                + "two meshes of the same body can number their nodes differently, so this "
                + "would apply each node's temperature to some other node and produce a "
                + "plausible wrong answer. Build both models over one AnalysisMesh, or pass "
                + "the nodal values explicitly through the IReadOnlyList overload.");
        return ThermalLoad(thermal.Temperature, referenceTemperature);
    }

    /// <summary>A uniform temperature rise over the whole model — the case a hand
    /// calculation checks, and a legitimate load on its own.</summary>
    public StructuralModel UniformThermalLoad(double deltaT)
    {
        var field = new double[Mesh.NodeCount];
        Array.Fill(field, deltaT);
        return ThermalLoad(field, 0);
    }

    // ---- selection ------------------------------------------------------------------

    /// <summary>The facet's selector view.</summary>
    public FacetRef Describe(int facet)
    {
        var areaVector = Mesh.FacetArea(facet);
        double area = areaVector.Length;
        var normal = area > 0 ? areaVector / area : Vector3d.Zero;
        return new FacetRef(facet, Mesh.FacetTag(facet), Mesh.FacetCentroid(facet), normal, area);
    }

    /// <summary>Every node belonging to a selected facet, ascending — what a support
    /// grabs, and useful on its own for inspection.</summary>
    public IReadOnlyList<int> NodesOn(Func<FacetRef, bool> facets)
    {
        ArgumentNullException.ThrowIfNull(facets);
        return SelectNodes(facets, nameof(NodesOn));
    }

    /// <summary>Facet indices matching a selector.</summary>
    public IReadOnlyList<int> FacetsMatching(Func<FacetRef, bool> facets)
    {
        ArgumentNullException.ThrowIfNull(facets);
        var list = new List<int>();
        for (int f = 0; f < Mesh.FacetCount; f++)
        {
            if (facets(Describe(f)))
                list.Add(f);
        }
        return list;
    }

    private List<int> SelectNodes(Func<FacetRef, bool> facets, string caller)
    {
        var set = new HashSet<int>();
        for (int f = 0; f < Mesh.FacetCount; f++)
        {
            if (!facets(Describe(f)))
                continue;
            foreach (int node in Mesh.Facet(f))
                set.Add(node);
        }
        if (set.Count == 0)
            throw NothingSelected(caller);
        var nodes = set.ToList();
        nodes.Sort();
        return nodes;
    }

    private int ApplySurfaceLoad(
        Func<FacetRef, bool> facets, string caller, Func<FacetRef, Vector3d> traction)
    {
        var matched = new List<int>();
        for (int f = 0; f < Mesh.FacetCount; f++)
        {
            if (facets(Describe(f)))
                matched.Add(f);
        }
        if (matched.Count == 0)
            throw NothingSelected(caller);
        ApplyToFacets(matched, traction);
        return matched.Count;
    }

    /// <summary>Distributes a traction over an already-selected facet list.</summary>
    private void ApplyToFacets(List<int> facets, Func<FacetRef, Vector3d> traction)
    {
        int perFacet = Mesh.NodesPerFacet;
        Span<Vector3d> positions = stackalloc Vector3d[perFacet];
        Span<double> weights = stackalloc double[perFacet];

        foreach (int f in facets)
        {
            var nodes = Mesh.Facet(f);
            for (int i = 0; i < perFacet; i++)
                positions[i] = Mesh.Position(nodes[i]);
            TetElement.FacetLoadWeights(Mesh.Order, positions, weights);
            var t = traction(Describe(f));
            for (int i = 0; i < perFacet; i++)
                _force[nodes[i]] += t * weights[i];
        }
    }

    private FeaException NothingSelected(string caller)
    {
        var tags = Mesh.FacetTags;
        string available = tags.Count <= 24
            ? string.Join(", ", tags)
            : string.Join(", ", tags.Take(24)) + $", ... ({tags.Count} in all)";
        return new FeaException(
            $"{caller} selected no boundary facets, so it would have had no effect. " +
            $"The mesh has {Mesh.FacetCount} boundary facets carrying tags: {available}. " +
            "Supply TetMeshOptions.FacetTags to name B-Rep faces, or widen the geometric selector.");
    }

    private void RequireNode(int node)
    {
        if ((uint)node >= (uint)Mesh.NodeCount)
            throw new ArgumentOutOfRangeException(
                nameof(node), node, $"The mesh has {Mesh.NodeCount} nodes.");
    }

    private static Vector3d Combine(Vector3d existing, Vector3d value, Dof dofs) => new(
        dofs.HasFlag(Dof.X) ? value.X : existing.X,
        dofs.HasFlag(Dof.Y) ? value.Y : existing.Y,
        dofs.HasFlag(Dof.Z) ? value.Z : existing.Z);
}
