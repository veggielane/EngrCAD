using EngrCAD.Core;

namespace EngrCAD.Fea;

/// <summary>
/// A heat-conduction model: an <see cref="AnalysisMesh"/>, a material per region, and the
/// boundary conditions <see cref="ThermalSolver"/> needs — <b>one degree of freedom per
/// node</b> instead of the three a structural model carries, over the same mesh, the same
/// facet selectors and the same builder shape.
///
/// <para><b>It is deliberately the same object as <see cref="StructuralModel"/> with the
/// physics swapped.</b> Conditions accumulate by chained calls; loads are reduced to
/// nodal heat as they are added, so <see cref="AppliedHeat"/> is always the true
/// resultant; a selector that matches nothing is refused at the call, naming the tags that
/// do exist; and a solve does not mutate the model, so one model can be solved steady and
/// then transient. Anyone who has written a <see cref="StructuralModel"/> already knows
/// this API — <c>Fix</c> becomes <see cref="Temperature(Func{FacetRef, bool}, double)"/>,
/// <c>Pressure</c> becomes <see cref="HeatFlux"/>, <c>Force</c> becomes
/// <see cref="HeatLoad"/>, <c>Gravity</c>/<c>BodyForce</c> become
/// <see cref="Generation(double)"/>.</para>
///
/// <para><b>The one condition with no structural analogue is convection</b>, and it is
/// structurally different rather than merely differently named: Newton's law of cooling
/// <c>q = h(T - T_inf)</c> has a term proportional to the UNKNOWN, so it contributes to
/// the conduction matrix as well as to the load vector. That is why
/// <see cref="Convection"/> is the one condition NOT pre-reduced to nodal heat — its load
/// half is meaningless without its matrix half, and applying one without the other gives a
/// body that cools to the wrong temperature while looking perfectly well posed. Two films
/// on one facet accumulate (h adds, and so does h·T_inf), which is the physically correct
/// composition of parallel paths and makes the call order irrelevant.</para>
///
/// <para><b>The constitutive law is Fourier's, <c>q = -k·grad T</c></b>, with k taken from
/// each element's own material, so a multi-region mesh conducts at the right rate per
/// body. Units are the caller's and must be consistent; see <see cref="Material"/> for
/// the mm/N/tonne/s figures, where conductivity is numerically the SI W/(m·K).</para>
/// </summary>
public sealed class ThermalModel
{
    private readonly bool[] _fixed;
    private readonly double[] _temperature;
    private readonly double[] _heat;
    private readonly double[] _filmCoefficient;   // per facet, accumulated
    private readonly double[] _filmSupply;        // per facet, sum of h·T_inf
    private readonly Dictionary<int, Material> _materials = [];
    private readonly List<string> _conditions = [];
    private int _convectiveFacets;

    /// <summary>A model over an analysis mesh with one material everywhere.</summary>
    public ThermalModel(AnalysisMesh mesh, Material material)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(material);
        if (mesh.ElementCount == 0)
            throw new FeaException("The analysis mesh has no elements.");
        Mesh = mesh;
        DefaultMaterial = material;
        _fixed = new bool[mesh.NodeCount];
        _temperature = new double[mesh.NodeCount];
        _heat = new double[mesh.NodeCount];
        _filmCoefficient = new double[mesh.FacetCount];
        _filmSupply = new double[mesh.FacetCount];
    }

    /// <summary>A model over a linear (4-node) tet mesh.</summary>
    public ThermalModel(TetMesh mesh, Material material)
        : this(AnalysisMesh.Of(mesh), material) { }

    /// <summary>A model over a quadratic (10-node) tet mesh.</summary>
    public ThermalModel(QuadraticTetMesh mesh, Material material)
        : this(AnalysisMesh.Of(mesh), material) { }

    /// <summary>
    /// A multi-material model built from the SAME <see cref="AnalysisBody"/> list that was
    /// meshed — the thermal twin of
    /// <see cref="StructuralModel.For(AnalysisMesh, IReadOnlyList{AnalysisBody})"/>, and
    /// the reason it exists is the same: region <c>i</c> takes <c>bodies[i].Material</c>,
    /// so the conductivities cannot drift from the geometry they belong to.
    /// <para>Refuses a body with no material, a mesh region no body declares and a
    /// declared body that contributed no elements — all by name, before anything is
    /// assembled. A missing CONDUCTIVITY is <see cref="ThermalSolver"/>'s refusal, not
    /// this one, since a model is legal to build and only a solve integrates one.</para>
    /// </summary>
    public static ThermalModel For(AnalysisMesh mesh, IReadOnlyList<AnalysisBody> bodies)
    {
        AnalysisBodies.Require(mesh, bodies, "thermal");
        var model = new ThermalModel(mesh, bodies[0].Material!);
        for (int b = 0; b < bodies.Count; b++)
            model.SetMaterial(b, bodies[b].Material!);
        return model;
    }

    /// <summary>The linear (4-node) spelling of
    /// <see cref="For(AnalysisMesh, IReadOnlyList{AnalysisBody})"/>.</summary>
    public static ThermalModel For(TetMesh mesh, IReadOnlyList<AnalysisBody> bodies) =>
        For(AnalysisMesh.Of(mesh), bodies);

    /// <summary>The quadratic (10-node) spelling of
    /// <see cref="For(AnalysisMesh, IReadOnlyList{AnalysisBody})"/>.</summary>
    public static ThermalModel For(QuadraticTetMesh mesh, IReadOnlyList<AnalysisBody> bodies) =>
        For(AnalysisMesh.Of(mesh), bodies);

    /// <summary>The analysis mesh.</summary>
    public AnalysisMesh Mesh { get; }

    /// <summary>The material used for any region with no explicit assignment.</summary>
    public Material DefaultMaterial { get; }

    /// <summary>Human-readable list of the conditions applied, in order.</summary>
    public IReadOnlyList<string> Conditions => _conditions;

    /// <summary>The material of one element, by its region id.</summary>
    public Material MaterialOf(int element) =>
        _materials.TryGetValue(Mesh.RegionOf(element), out var m) ? m : DefaultMaterial;

    /// <summary>Assigns a material to one region id (from multi-body meshing).</summary>
    public ThermalModel SetMaterial(int region, Material material)
    {
        ArgumentNullException.ThrowIfNull(material);
        _materials[region] = material;
        _conditions.Add($"material '{material.Name}' on region {region}");
        return this;
    }

    /// <summary>Whether one node's temperature is prescribed (a Dirichlet node).</summary>
    public bool IsPrescribed(int node)
    {
        RequireNode(node);
        return _fixed[node];
    }

    /// <summary>The prescribed temperature of one node (meaningless unless
    /// <see cref="IsPrescribed"/>).</summary>
    public double PrescribedTemperature(int node)
    {
        RequireNode(node);
        return _temperature[node];
    }

    /// <summary>The applied nodal heat load on one node (power).</summary>
    public double HeatOf(int node)
    {
        RequireNode(node);
        return _heat[node];
    }

    /// <summary>The accumulated film coefficient on one boundary facet (0 = no
    /// convection).</summary>
    public double FilmCoefficientOf(int facet)
    {
        RequireFacet(facet);
        return _filmCoefficient[facet];
    }

    /// <summary>The convective supply <c>sum(h · T_inf)</c> on one boundary facet. Divided
    /// by <see cref="FilmCoefficientOf"/> it is the EFFECTIVE ambient temperature, which is
    /// how two films at different ambients compose.</summary>
    public double FilmSupplyOf(int facet)
    {
        RequireFacet(facet);
        return _filmSupply[facet];
    }

    /// <summary>Number of nodes with a prescribed temperature.</summary>
    public int PrescribedNodeCount => _fixed.Count(f => f);

    /// <summary>Number of facets carrying a convective condition.</summary>
    public int ConvectiveFacetCount => _convectiveFacets;

    /// <summary>
    /// Resultant of every applied nodal heat load — the power the boundary must carry
    /// away. Convection is NOT included: its supply depends on the answer, so it is
    /// reported separately by the solve as part of the energy balance.
    /// </summary>
    public double AppliedHeat
    {
        get
        {
            double sum = 0;
            foreach (double q in _heat)
                sum += q;
            return sum;
        }
    }

    // ---- prescribed temperature (Dirichlet) -----------------------------------------

    /// <summary>
    /// Prescribes a temperature on every node of the selected facets — the thermal
    /// analogue of <c>StructuralModel.Fix</c>/<c>Prescribe</c>, which are one condition
    /// here because a scalar field has no partial restraint to express.
    /// <para>A later call OVERWRITES an earlier one on the same node rather than
    /// accumulating, which is what "this face is held at 80" has to mean.</para>
    /// </summary>
    public ThermalModel Temperature(Func<FacetRef, bool> facets, double temperature)
    {
        ArgumentNullException.ThrowIfNull(facets);
        RequireFinite(temperature, nameof(temperature));
        var nodes = SelectNodes(facets, nameof(Temperature));
        foreach (int node in nodes)
        {
            _fixed[node] = true;
            _temperature[node] = temperature;
        }
        _conditions.Add($"temperature {temperature:G6} on {nodes.Count} nodes");
        return this;
    }

    /// <summary>Prescribes a temperature on the facets carrying one tag.</summary>
    public ThermalModel Temperature(int tag, double temperature) =>
        Temperature(Facets.Tag(tag), temperature);

    /// <summary>Prescribes a temperature on one node directly — the escape hatch, and how
    /// a single-point datum pins an otherwise all-Neumann model.</summary>
    public ThermalModel TemperatureNode(int node, double temperature)
    {
        RequireNode(node);
        RequireFinite(temperature, nameof(temperature));
        _fixed[node] = true;
        _temperature[node] = temperature;
        _conditions.Add($"temperature {temperature:G6} on node {node}");
        return this;
    }

    // ---- surface loads ---------------------------------------------------------------

    /// <summary>
    /// A uniform heat flux on the selected facets. <b>Positive flux flows INTO the
    /// body</b>, which is what "300 mW/mm² onto this face" means — the same sign
    /// convention <c>StructuralModel.Pressure</c> uses, for the same reason: the number an
    /// engineer states is what arrives, not what the outward normal says.
    /// <para>An insulated (adiabatic) surface needs no condition at all: zero flux is the
    /// NATURAL boundary condition of the weak form, so an unmentioned face is already
    /// insulated. Saying <c>HeatFlux(selector, 0)</c> is a legal no-op and is refused only
    /// if it selects nothing.</para>
    /// </summary>
    public ThermalModel HeatFlux(Func<FacetRef, bool> facets, double fluxPerArea)
    {
        ArgumentNullException.ThrowIfNull(facets);
        RequireFinite(fluxPerArea, nameof(fluxPerArea));
        int count = ApplySurfaceLoad(facets, nameof(HeatFlux), _ => fluxPerArea);
        _conditions.Add($"heat flux {fluxPerArea:G6} on {count} facets");
        return this;
    }

    /// <summary>A uniform heat flux on the facets carrying one tag.</summary>
    public ThermalModel HeatFlux(int tag, double fluxPerArea) =>
        HeatFlux(Facets.Tag(tag), fluxPerArea);

    /// <summary>
    /// A total heat load spread over the selected facets as a uniform flux — the
    /// ergonomic "put 5 W into this face", and the exact analogue of
    /// <c>StructuralModel.Force</c>. The resultant is exact: the consistent weights over a
    /// facet sum to its area, so the distributed load sums back to
    /// <paramref name="totalPower"/> whatever the element order.
    /// </summary>
    public ThermalModel HeatLoad(Func<FacetRef, bool> facets, double totalPower)
    {
        ArgumentNullException.ThrowIfNull(facets);
        RequireFinite(totalPower, nameof(totalPower));

        // ONE pass over the selector, and the matched list is what the load is applied to
        // — asking a stateful predicate twice would derive the flux from one facet set and
        // apply it to another, and the exact-resultant guarantee that is this overload's
        // whole point would quietly stop holding. (StructuralModel.Force, verbatim.)
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
            throw NothingSelected(nameof(HeatLoad));
        if (!(area > 0))
            throw new FeaException(
                $"HeatLoad selected {matched.Count} facets whose total area is zero; "
                + "a flux cannot be derived.");

        double flux = totalPower / area;
        ApplyToFacets(matched, _ => flux);
        _conditions.Add($"heat load {totalPower:G6} over {matched.Count} facets ({area:G6} area)");
        return this;
    }

    /// <summary>
    /// A convective boundary: <c>q = h·(T - T_inf)</c> out of the body, over the selected
    /// facets.
    ///
    /// <para><b>The condition that contributes to the matrix.</b> Unlike every other load
    /// here it is not pre-reduced to nodal heat, because half of it multiplies the unknown
    /// temperature. Both halves are assembled together by the solver — the surface matrix
    /// <c>integral(h·N_i·N_j dA)</c> and the supply <c>integral(h·T_inf·N_i dA)</c> — and
    /// keeping them as one stored condition is what makes it impossible to apply one
    /// without the other.</para>
    ///
    /// <para><b>It is also what makes an otherwise all-Neumann model solvable.</b> Pure
    /// conduction with only flux conditions has a constant null space (add any constant to
    /// T and every flux is unchanged); a convective facet removes it, because the surface
    /// matrix is positive on constants. <see cref="ThermalSolver"/>'s restraint check
    /// knows this and accepts convection in place of a prescribed temperature.</para>
    ///
    /// <para>Two films on one facet ACCUMULATE — h adds and so does h·T_inf, which is
    /// parallel heat paths and is order-independent. <paramref name="filmCoefficient"/>
    /// must be positive: a zero film is no condition (say nothing instead) and a negative
    /// one is heat flowing up its own gradient, which would make the matrix indefinite.</para>
    /// </summary>
    /// <param name="facets">Which surface.</param>
    /// <param name="filmCoefficient">h, in mW/(mm²·K) for mm/N — the SI W/(m²·K) times
    /// 1e-3, so natural convection in air (~10) is 0.01 here.</param>
    /// <param name="ambientTemperature">T_inf, the temperature the surface exchanges
    /// with.</param>
    public ThermalModel Convection(
        Func<FacetRef, bool> facets, double filmCoefficient, double ambientTemperature)
    {
        ArgumentNullException.ThrowIfNull(facets);
        RequireFinite(ambientTemperature, nameof(ambientTemperature));
        if (!(filmCoefficient > 0) || double.IsInfinity(filmCoefficient))
            throw new FeaException(
                $"Convection needs a finite positive film coefficient; {filmCoefficient:G6} was "
                + "given. Zero is not a condition (leave the surface unmentioned and it is "
                + "adiabatic, which is the weak form's natural boundary condition), and a "
                + "negative one is heat flowing up its own gradient, which would make the "
                + "conduction matrix indefinite.");

        var matched = new List<int>();
        for (int f = 0; f < Mesh.FacetCount; f++)
        {
            if (facets(Describe(f)))
                matched.Add(f);
        }
        if (matched.Count == 0)
            throw NothingSelected(nameof(Convection));

        double area = 0;
        foreach (int f in matched)
        {
            // Exact-zero test: a facet with no film yet is a NEW convective facet, and
            // this is a count rather than a measurement.
            if (_filmCoefficient[f] == 0)
                _convectiveFacets++;
            _filmCoefficient[f] += filmCoefficient;
            _filmSupply[f] += filmCoefficient * ambientTemperature;
            area += Mesh.FacetArea(f).Length;
        }

        _conditions.Add(
            $"convection h = {filmCoefficient:G6} to {ambientTemperature:G6} on "
            + $"{matched.Count} facets ({area:G6} area)");
        return this;
    }

    /// <summary>A total heat load over the facets carrying one tag.</summary>
    public ThermalModel HeatLoad(int tag, double totalPower) =>
        HeatLoad(Facets.Tag(tag), totalPower);

    /// <summary>Convection on the facets carrying one tag.</summary>
    public ThermalModel Convection(int tag, double filmCoefficient, double ambientTemperature) =>
        Convection(Facets.Tag(tag), filmCoefficient, ambientTemperature);

    // ---- volumetric loads ------------------------------------------------------------

    /// <summary>
    /// A uniform volumetric heat generation (power per unit volume) over every element —
    /// resistive heating, a curing exotherm, nuclear decay. The analogue of
    /// <c>StructuralModel.Gravity</c>: a body load whose total is exactly
    /// <c>rate · volume</c>.
    /// </summary>
    public ThermalModel Generation(double ratePerVolume)
    {
        RequireFinite(ratePerVolume, nameof(ratePerVolume));
        // Exact-zero semantic test: no generation is no load, and there is nothing here
        // for an epsilon to protect against.
        if (ratePerVolume == 0)
        {
            _conditions.Add("generation 0 (no load)");
            return this;
        }

        var order = Mesh.Order;
        int perElement = Mesh.NodesPerElement;
        // The element's OWN rule: integral(N_i dV) against a constant rate is degree p, and
        // TetElement.BodyLoadWeights is exactly that integral — the same code a gravity
        // load already asks, so a 10-node element's negative corner weights cannot be
        // rediscovered differently here.
        var rule = TetQuadrature.For(order);
        Span<Vector3d> positions = stackalloc Vector3d[10];
        Span<double> weights = stackalloc double[10];
        double total = 0;

        for (int e = 0; e < Mesh.ElementCount; e++)
        {
            var nodes = Mesh.Element(e);
            for (int i = 0; i < perElement; i++)
                positions[i] = Mesh.Position(nodes[i]);
            TetElement.BodyLoadWeights(order, positions[..perElement], rule, weights);
            for (int i = 0; i < perElement; i++)
                _heat[nodes[i]] += ratePerVolume * weights[i];
            total += ratePerVolume * Mesh.ElementVolume(e);
        }

        _conditions.Add($"generation {ratePerVolume:G6} per volume (total {total:G6})");
        return this;
    }

    /// <summary>
    /// A position-dependent volumetric heat generation, evaluated at the elements'
    /// quadrature points — the analogue of <c>StructuralModel.BodyForce</c>, and what a
    /// manufactured-solution convergence study needs.
    ///
    /// <para>Integrated with a <b>degree-5</b> rule rather than the element's own, for the
    /// reason the structural twin records: the element rule is exact for what the element
    /// itself produces, a caller's field is not a polynomial of the element's making, and
    /// under-integrating a load caps a convergence study at the quadrature's order instead
    /// of the element's — a limit that looks exactly like a formulation defect.</para>
    /// </summary>
    public ThermalModel Generation(Func<Vector3d, double> ratePerVolume)
    {
        ArgumentNullException.ThrowIfNull(ratePerVolume);
        int perElement = Mesh.NodesPerElement;
        var positions = new Vector3d[perElement];
        var loads = new double[perElement];

        for (int e = 0; e < Mesh.ElementCount; e++)
        {
            var nodes = Mesh.Element(e);
            for (int i = 0; i < perElement; i++)
                positions[i] = Mesh.Position(nodes[i]);
            ThermalElement.Generation(
                Mesh.Order, positions, TetQuadrature.Degree5, ratePerVolume, loads);
            for (int i = 0; i < perElement; i++)
                _heat[nodes[i]] += loads[i];
        }

        _conditions.Add($"generation field over {Mesh.ElementCount} elements");
        return this;
    }

    /// <summary>
    /// Adds a heat load directly to one node — the escape hatch for a point source.
    /// A genuine point source in a continuum has an infinite temperature at it, so the
    /// value there does not converge; use it for energy-balance checks and treat the local
    /// peak as meaningless. (The same caveat <c>StructuralModel.NodalForce</c> carries,
    /// for the same reason.)
    /// </summary>
    public ThermalModel NodalHeat(int node, double power)
    {
        RequireNode(node);
        RequireFinite(power, nameof(power));
        _heat[node] += power;
        _conditions.Add($"nodal heat {power:G6} on node {node}");
        return this;
    }

    /// <summary>Clears every applied heat load and every convective condition, keeping
    /// prescribed temperatures and materials — how a second load case is built on one
    /// model.</summary>
    public ThermalModel ClearLoads()
    {
        Array.Clear(_heat);
        Array.Clear(_filmCoefficient);
        Array.Clear(_filmSupply);
        _convectiveFacets = 0;
        _conditions.Add("loads cleared");
        return this;
    }

    // ---- selection -------------------------------------------------------------------

    /// <summary>The facet's selector view — identical to
    /// <c>StructuralModel.Describe</c>, so one selector reads both models.</summary>
    public FacetRef Describe(int facet)
    {
        var areaVector = Mesh.FacetArea(facet);
        double area = areaVector.Length;
        var normal = area > 0 ? areaVector / area : Vector3d.Zero;
        return new FacetRef(facet, Mesh.FacetTag(facet), Mesh.FacetCentroid(facet), normal, area);
    }

    /// <summary>Every node belonging to a selected facet, ascending.</summary>
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
        Func<FacetRef, bool> facets, string caller, Func<FacetRef, double> flux)
    {
        var matched = new List<int>();
        for (int f = 0; f < Mesh.FacetCount; f++)
        {
            if (facets(Describe(f)))
                matched.Add(f);
        }
        if (matched.Count == 0)
            throw NothingSelected(caller);
        ApplyToFacets(matched, flux);
        return matched.Count;
    }

    private void ApplyToFacets(List<int> facets, Func<FacetRef, double> flux)
    {
        int perFacet = Mesh.NodesPerFacet;
        Span<Vector3d> positions = stackalloc Vector3d[6];
        Span<double> weights = stackalloc double[6];

        foreach (int f in facets)
        {
            var nodes = Mesh.Facet(f);
            for (int i = 0; i < perFacet; i++)
                positions[i] = Mesh.Position(nodes[i]);
            ThermalElement.FacetLoad(
                Mesh.Order, positions[..perFacet], flux(Describe(f)), weights);
            for (int i = 0; i < perFacet; i++)
                _heat[nodes[i]] += weights[i];
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

    private void RequireFacet(int facet)
    {
        if ((uint)facet >= (uint)Mesh.FacetCount)
            throw new ArgumentOutOfRangeException(
                nameof(facet), facet, $"The mesh has {Mesh.FacetCount} boundary facets.");
    }

    private static void RequireFinite(double value, string parameter)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(parameter, value, "The value must be finite.");
    }
}
