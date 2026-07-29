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
    public static Func<FacetRef, bool> OnPlane(Vector3d point, Vector3d normal, double tolerance = 1e-6)
    {
        var n = normal.Normalized();
        double offset = n.Dot(point);
        return f => Math.Abs(n.Dot(f.Centroid) - offset) <= tolerance
                 && Math.Abs(n.Dot(f.Normal)) >= 1.0 - 1e-6;
    }

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

    /// <summary>A model over an analysis mesh with one material everywhere.</summary>
    public StructuralModel(AnalysisMesh mesh, Material material)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(material);
        if (mesh.ElementCount == 0)
            throw new FeaException("The analysis mesh has no elements.");
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
        _materials[region] = material;
        _conditions.Add($"material '{material.Name}' on region {region}");
        return this;
    }

    /// <summary>The restraint mask on one node.</summary>
    public Dof RestraintOf(int node) => _restraint[node];

    /// <summary>The prescribed displacement of one node (components without a restraint are ignored).</summary>
    public Vector3d PrescribedOf(int node) => _prescribed[node];

    /// <summary>The applied nodal force on one node.</summary>
    public Vector3d ForceOf(int node) => _force[node];

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
            if (_force[v] != Vector3d.Zero)
                sum += (Mesh.Position(v) - about).Cross(_force[v]);
        }
        return sum;
    }

    // ---- supports -------------------------------------------------------------------

    /// <summary>Fully or partially fixes every node of the selected facets.</summary>
    public StructuralModel Fix(Func<FacetRef, bool> facets, Dof dofs = Dof.All)
    {
        ArgumentNullException.ThrowIfNull(facets);
        var nodes = SelectNodes(facets, nameof(Fix));
        foreach (int node in nodes)
            _restraint[node] |= dofs;
        _conditions.Add($"fix {dofs} on {nodes.Count} nodes");
        return this;
    }

    /// <summary>Fixes every node of the facets carrying one tag.</summary>
    public StructuralModel Fix(int tag, Dof dofs = Dof.All) => Fix(Facets.Tag(tag), dofs);

    /// <summary>Fixes one node directly — the escape hatch, and how a statically
    /// determinate 3-2-1 restraint is built.</summary>
    public StructuralModel FixNode(int node, Dof dofs = Dof.All)
    {
        RequireNode(node);
        _restraint[node] |= dofs;
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
        double area = 0;
        int matched = 0;
        for (int f = 0; f < Mesh.FacetCount; f++)
        {
            if (!facets(Describe(f)))
                continue;
            matched++;
            area += Mesh.FacetArea(f).Length;
        }
        if (matched == 0)
            throw NothingSelected(nameof(Force));
        if (!(area > 0))
            throw new FeaException(
                $"Force selected {matched} facets whose total area is zero; a traction cannot be derived.");

        var traction = totalForce / area;
        ApplySurfaceLoad(facets, nameof(Force), _ => traction);
        _conditions.Add($"force {totalForce} over {matched} facets ({area:G6} area)");
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

    /// <summary>Clears every applied load, keeping supports and materials — how a second
    /// load case is built on one model.</summary>
    public StructuralModel ClearLoads()
    {
        Array.Clear(_force);
        _conditions.Add("loads cleared");
        return this;
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
        int perFacet = Mesh.NodesPerFacet;
        Span<Vector3d> positions = stackalloc Vector3d[perFacet];
        Span<double> weights = stackalloc double[perFacet];
        int matched = 0;

        for (int f = 0; f < Mesh.FacetCount; f++)
        {
            var reference = Describe(f);
            if (!facets(reference))
                continue;
            matched++;
            var nodes = Mesh.Facet(f);
            for (int i = 0; i < perFacet; i++)
                positions[i] = Mesh.Position(nodes[i]);
            TetElement.FacetLoadWeights(Mesh.Order, positions, weights);
            var t = traction(reference);
            for (int i = 0; i < perFacet; i++)
                _force[nodes[i]] += t * weights[i];
        }

        if (matched == 0)
            throw NothingSelected(caller);
        return matched;
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
