using EngrCAD.Core;

namespace EngrCAD.Fea;

/// <summary>How the discontinuous element stress field is turned into one value per node.</summary>
public enum StressRecovery
{
    /// <summary>
    /// Evaluate the element's own stress AT each node and average (the default).
    /// Simple, exact where the answer is constant, and the value a colour map has always
    /// shown here. Its weakness is where it samples: for a quadratic element the stress is
    /// linear and a node is the WORST place to read it.
    /// </summary>
    Direct,

    /// <summary>
    /// Zienkiewicz-Zhu superconvergent patch recovery: fit a polynomial to the stress at
    /// each patch's INTEGRATION points, where it is superconvergent, and read the fit at the
    /// node. Costs one small least-squares solve per corner node and converges an order
    /// faster than <see cref="Direct"/>.
    /// </summary>
    Superconvergent,
}

/// <summary>
/// The Zienkiewicz-Zhu error estimate: how far the element field is from its own
/// superconvergent recovery, measured in the energy norm.
///
/// <para><b>The same type serves stress and heat flux.</b> The record carries only norms and
/// counts, not a tensor, so it is the answer whether the recovered field is a structural stress
/// (<see cref="StructuralResults.ErrorEstimate"/>) or a thermal flux
/// (<see cref="ThermalResults.ErrorEstimate"/>) — a flux is one derivative down from
/// temperature exactly as a stress is from displacement, so the estimator's shape is one and
/// only the energy inner product differs (compliance for stress, <c>1/k</c> for flux).</para>
///
/// <para><b>An error estimator is worth more than the accuracy the recovery buys</b>, which
/// is the reason recovery was worth building at all. A solve returns a number whatever the
/// mesh; this is the answer to "is the mesh good enough", and
/// <see cref="ElementError"/> is where it is not — the input an adaptive refinement loop
/// wants. It is an ESTIMATE, and honest about it: the argument is that the recovered field
/// is a closer approximation to the true field than the finite-element field is, so the
/// gap between them stands in for the gap to the truth. Where that premise is weakest — a
/// genuine singularity, a material interface, a boundary layer — the estimate is
/// biased LOW, which is the direction that matters.</para>
/// </summary>
/// <param name="ElementError">The energy-norm error attributed to each element — the map an
/// adaptive scheme refines against.</param>
/// <param name="ErrorNorm">The global energy norm of the estimated error, or <b>NaN</b> when
/// no patch could be assembled at all — see <see cref="RelativeError"/>.</param>
/// <param name="SolutionNorm">The energy norm of the finite-element field.</param>
/// <param name="RelativeError">The classical ZZ figure: the error norm over the estimated
/// norm of the EXACT solution, i.e. over <c>sqrt(SolutionNorm² + ErrorNorm²)</c> rather than
/// over the finite-element norm alone. At a coarse mesh that difference is not small.
/// <b>NaN when the mesh has no interior corner node</b>, so no patch exists and the recovered
/// field is the finite-element field itself: the honest reading is UNKNOWN, and reporting the
/// zero that arithmetic gives would call the mesh perfect on exactly the mesh too coarse to
/// assess (measured on a 24-element box: 9.9e-15 against a true error of 0.31).</param>
/// <param name="FallbackNodes">Nodes no viable patch reached, which kept their averaged
/// value. A recovery that quietly did not happen must not look like one that did.</param>
/// <param name="RankDeficientPatches">Patches whose normal equations were singular — too
/// few samples, or samples on a plane.</param>
public readonly record struct ErrorEstimate(
    IReadOnlyList<double> ElementError,
    double ErrorNorm,
    double SolutionNorm,
    double RelativeError,
    int FallbackNodes,
    int RankDeficientPatches)
{
    /// <summary>The element carrying the largest share of the estimated error.</summary>
    public int WorstElement
    {
        get
        {
            int best = 0;
            for (int e = 1; e < ElementError.Count; e++)
            {
                if (ElementError[e] > ElementError[best])
                    best = e;
            }
            return best;
        }
    }

    /// <inheritdoc/>
    public override string ToString() =>
        double.IsNaN(RelativeError)
            ? "estimated error UNKNOWN: no patch could be assembled, so nothing was recovered "
              + "(the mesh has no interior corner node)"
            : $"estimated error {RelativeError * 100:F2}% "
              + $"(energy norm {ErrorNorm:G6} against {SolutionNorm:G6})"
              + (FallbackNodes > 0 ? $", {FallbackNodes} node(s) not recovered" : "");
}

/// <summary>
/// The field-specific half of a recovery: how the recoverable quantity is sampled at an
/// element's quadrature points, and the arithmetic of the value type it produces. Everything
/// else in <see cref="SuperconvergentRecovery"/> — the patch assembly, the least-squares fit,
/// the boundary fill, the (node, region) slot rule — is shared, which is what makes a stress
/// recovery and a flux recovery two instantiations rather than two copies.
///
/// <para>A <c>struct</c> so the generic recovery is monomorphized and each member call is
/// devirtualized: the structural instantiation compiles to the same operations, in the same
/// order, as the hand-written stress recovery did, which is what keeps that path bit-identical.</para>
/// </summary>
/// <typeparam name="TValue">The recovered value type — a <see cref="SymmetricTensor3"/> stress
/// or a <see cref="Vector3d"/> flux.</typeparam>
internal interface IRecoveryField<TValue>
{
    /// <summary>The analysis mesh the field lives on.</summary>
    AnalysisMesh Mesh { get; }

    /// <summary>Components of the sampled quantity (6 for a stress tensor, 3 for a flux).</summary>
    int Components { get; }

    /// <summary>Solution degrees of freedom per node (3 displacements, 1 temperature) — the
    /// stride of the gathered <c>dofs</c> span.</summary>
    int DofsPerNode { get; }

    /// <summary>Fills <paramref name="positions"/> (one per node) and <paramref name="dofs"/>
    /// (<see cref="DofsPerNode"/> per node) for one element.</summary>
    void GatherDofs(int element, Span<Vector3d> positions, Span<double> dofs);

    /// <summary>The sampled quantity at natural coordinates, as its <see cref="Components"/>
    /// Voigt/vector entries.</summary>
    void SampleAt(
        int element, ReadOnlySpan<Vector3d> positions, ReadOnlySpan<double> dofs,
        double r, double s, double t, Span<double> value);

    /// <summary>The additive identity of the value type.</summary>
    TValue Zero { get; }

    /// <summary>Builds a value from its component entries.</summary>
    TValue FromComponents(ReadOnlySpan<double> value);

    /// <summary>Componentwise sum — the accumulation the patch evaluations feed.</summary>
    TValue Add(TValue a, TValue b);

    /// <summary>Componentwise scale — the average once contributions are counted.</summary>
    TValue Scale(TValue a, double s);
}

/// <summary>The recovered field: one value per node (the average across regions a colour map
/// shows), one per (node, region) slot (the honest per-material value), and the counts a
/// consumer needs to know how much of it is genuinely recovered.</summary>
/// <typeparam name="TValue">Stress or flux.</typeparam>
/// <param name="Values">One value per NODE — the average of every patch that reached it,
/// across regions.</param>
/// <param name="SlotValues">One value per (node, region) slot, indexed by
/// <see cref="AnalysisMesh.RegionSlot"/>. Equal to <paramref name="Values"/> at every node
/// touching one region, by identical arithmetic rather than by tolerance.</param>
/// <param name="SlotContributions">Patch evaluations that reached each slot; zero means that
/// slot fell back.</param>
/// <param name="SlotsAreNodes">True when a slot index IS a node index, so
/// <paramref name="SlotValues"/> may be indexed by node.</param>
/// <param name="FallbackNodes">Nodes no viable patch reached at all.</param>
/// <param name="RankDeficientPatches">Patches whose normal equations were singular.</param>
internal readonly record struct RecoveredField<TValue>(
    TValue[] Values,
    TValue[] SlotValues,
    int[] SlotContributions,
    bool SlotsAreNodes,
    int FallbackNodes,
    int RankDeficientPatches);

/// <summary>
/// Zienkiewicz-Zhu superconvergent patch recovery, and the error estimator that comes with
/// it — shared by the structural stress recovery and the thermal flux recovery through
/// <see cref="IRecoveryField{TValue}"/>.
///
/// <para><b>The whole idea is WHERE the field is sampled.</b> A displacement- or
/// temperature-based element gives a derived field (stress, flux) that is one order lower than
/// the primary field and discontinuous across faces, and its accuracy is not uniform inside
/// the element: at the Gauss points of the element's own rule it is <i>superconvergent</i> —
/// accurate to the same order as the primary field rather than one lower — while at the nodes,
/// where a colour map most wants it, it is at its worst. So recovery samples the good points
/// and interpolates to the bad ones, rather than evaluating at the bad ones directly.</para>
///
/// <para><b>Vertex patches, evaluated at every node they touch.</b> A patch is the set of
/// elements meeting at one CORNER node; a polynomial of the primary field's own degree is
/// fitted to the patch's samples by least squares, and then evaluated at every node of
/// every element in the patch, with each node averaging the patches that reached it. That
/// is the textbook rule — mid-side nodes take their value from the adjacent vertex patches
/// rather than from a patch of their own, which would be a smaller and worse-conditioned
/// fit — and it also disposes of the boundary problem for free: a corner node's own patch
/// is one-sided and often rank-deficient, but the node still appears in its interior
/// neighbours' patches, so it gets a value from a well-posed fit instead of an extrapolation
/// from a bad one.</para>
///
/// <para><b>The fit is in LOCAL coordinates, and that is not a nicety.</b> Sample positions
/// are shifted to the patch node and scaled by the patch's own radius, so the normal
/// equations are O(1) and their conditioning is a property of the patch's SHAPE rather than
/// of where the part happens to sit in space. It also makes the answer free: the recovered
/// value at the patch node is exactly the fitted constant term, since every other basis
/// function vanishes at the origin.</para>
///
/// <para><b>A patch that cannot be fitted falls back, and says so.</b> Fewer samples than
/// terms, or samples lying on a plane, leaves the normal equations singular; that patch
/// contributes nothing and any node left with no contribution keeps its averaged value.
/// <see cref="RecoveredField{TValue}.FallbackNodes"/> counts them, because "the recovery
/// quietly did not happen" and "the recovery happened" must not look the same.</para>
///
/// <para><b>A patch never spans a MATERIAL INTERFACE, and that restriction is not cosmetic.</b>
/// At a bonded interface the derived field is genuinely discontinuous — only the traction (or
/// the normal flux) across the interface is continuous, the tangential part jumps with the
/// modulus (or the conductivity) — and a polynomial fitted across the jump is wrong on both
/// sides. What makes that worse than the smoothing plain averaging does is REACH: averaging is
/// local, so only the shared node is blended, while a patch is written to every node of every
/// element it contains, so one cross-interface fit corrupts a whole element layer of nodes
/// that touch ONE material and have an unambiguous right answer. So patches are assembled at
/// corner nodes touching exactly one region, contributions are accumulated per (node, region)
/// SLOT, and the boundary fill walks within a region. A node on the interface then carries one
/// recovered value per material (<see cref="StructuralResults.NodalStressIn"/> /
/// <see cref="ThermalResults.NodalFluxIn"/>); the node-indexed field averages them, because a
/// field with one value per node has nowhere else to go.</para>
/// </summary>
internal static class SuperconvergentRecovery
{
    /// <summary>
    /// Recovers a nodal field from a solved model, over an arbitrary
    /// <see cref="IRecoveryField{TValue}"/>. Generic over a <c>struct</c> field so the
    /// structural instantiation is monomorphized to the exact operations the hand-written
    /// stress recovery ran, in the same order — the bit-identity contract.
    /// </summary>
    /// <param name="field">The field-specific sampler and value arithmetic.</param>
    /// <param name="averaged">The per-SLOT directly averaged value, used wherever a slot is
    /// reached by no viable patch. Indexed by slot, which is the node index whenever
    /// <see cref="AnalysisMesh.RegionSlotsAreNodes"/>.</param>
    /// <param name="respectRegions">
    /// False collapses every element into ONE region, which is exactly the algorithm before
    /// material interfaces were handled — a test seam, so the defect the region rule fixes can
    /// be measured rather than asserted. Production never passes false; the single-region case
    /// takes the same code path either way, which is what makes the identity checkable.
    /// </param>
    public static RecoveredField<TValue> Recover<TValue, TField>(
        TField field,
        IReadOnlyList<TValue> averaged,
        bool respectRegions = true)
        where TField : struct, IRecoveryField<TValue>
    {
        var mesh = field.Mesh;
        int nodes = mesh.NodeCount;
        int perElement = mesh.NodesPerElement;
        int dofsPerElement = perElement * field.DofsPerNode;
        int components = field.Components;
        // The element's OWN rule: its points are the superconvergent ones, which is the
        // entire premise. Asking for a richer rule would sample places with no such property
        // and dilute the fit with ordinary-accuracy values.
        var rule = TetQuadrature.For(mesh.Order);
        int terms = mesh.Order == ElementOrder.Linear ? 4 : 10;

        var adjacency = BuildNodeToElement(mesh);
        var isCorner = CornerNodes(mesh);
        var isBoundary = BoundaryNodes(mesh);

        // One slot per (node, region). Collapsing to one region reproduces the pre-interface
        // algorithm exactly, because the slot of a node is then the node.
        int slotCount = respectRegions ? mesh.RegionSlotCount : nodes;
        (int Start, int End) SlotsAt(int node) =>
            respectRegions ? mesh.RegionSlotRange(node) : (node, node + 1);
        int SlotOf(int node, int region) => respectRegions ? mesh.RegionSlot(node, region) : node;

        var accumulated = new TValue[slotCount];
        var contributions = new int[slotCount];

        // Per-patch scratch, sized for the largest patch we might meet. Grown on demand.
        var normal = new double[terms * terms];
        var rhs = new double[terms * components];
        Span<double> basis = stackalloc double[10];
        Span<Vector3d> positions = stackalloc Vector3d[10];
        Span<double> dofs = stackalloc double[30];
        Span<double> sample = stackalloc double[6];
        // Hoisted out of the loops below: a stackalloc inside a loop grows the frame every
        // iteration instead of popping.
        Span<double> shape = stackalloc double[10];
        Span<double> value = stackalloc double[6];
        var coefficients = new double[terms * components];

        // Every fitted patch is KEPT, because the boundary fill below evaluates one at a
        // node that is not in its own elements. Three arrays rather than a list of objects:
        // a patch is (origin, scale, coefficients) and nothing else.
        var patchOrigin = new List<Vector3d>();
        var patchScale = new List<double>();
        var patchCoefficients = new List<double[]>();
        // Which patch reached each SLOT first, for the fill's breadth-first walk.
        var source = new int[slotCount];
        Array.Fill(source, -1);

        int rankDeficient = 0;

        for (int node = 0; node < nodes; node++)
        {
            if (!isCorner[node] || isBoundary[node])
                continue;
            // A node on a material interface gets no patch of its own: a patch there would
            // either span the jump or be one-sided, and a one-sided fit is exactly what the
            // boundary exclusion above exists to avoid. Its values come from its neighbours'
            // patches instead, one per region, as a boundary corner's do.
            var (slotStart, slotEnd) = SlotsAt(node);
            if (slotEnd - slotStart != 1)
                continue;
            int patchRegion = respectRegions ? mesh.RegionOfSlot(slotStart) : 0;
            var (start, end) = adjacency.Range(node);
            int patchElements = end - start;
            if (patchElements == 0)
                continue;

            var origin = mesh.Position(node);

            // The patch radius: the largest distance from the patch node to any of its own
            // nodes. Relative by construction, so the scaling carries no length unit.
            double radius = 0;
            for (int k = start; k < end; k++)
            {
                var element = mesh.Element(adjacency.Items[k]);
                for (int i = 0; i < perElement; i++)
                    radius = Math.Max(radius, mesh.Position(element[i]).DistanceTo(origin));
            }
            if (!(radius > 0))
                continue;
            double inv = 1.0 / radius;

            Array.Clear(normal);
            Array.Clear(rhs);
            int samples = 0;

            for (int k = start; k < end; k++)
            {
                int e = adjacency.Items[k];
                field.GatherDofs(e, positions, dofs);
                for (int q = 0; q < rule.Count; q++)
                {
                    var (r, s, t) = rule.Point(q);
                    field.SampleAt(
                        e, positions[..perElement], dofs[..dofsPerElement], r, s, t, sample);

                    // The sample's physical position, from the element's own shape functions
                    // - the same map the field was evaluated through, so the two cannot
                    // disagree about where the point is.
                    TetElement.ShapeValues(mesh.Order, r, s, t, shape);
                    var at = Vector3d.Zero;
                    var element = mesh.Element(e);
                    for (int i = 0; i < perElement; i++)
                        at += mesh.Position(element[i]) * shape[i];

                    Basis(terms, (at - origin) * inv, basis);
                    samples++;
                    for (int a = 0; a < terms; a++)
                    {
                        double ba = basis[a];
                        int row = a * terms;
                        for (int b = a; b < terms; b++)
                            normal[row + b] += ba * basis[b];
                        int atc = a * components;
                        for (int c = 0; c < components; c++)
                            rhs[atc + c] += ba * sample[c];
                    }
                }
            }

            if (samples < terms
                || !SolveNormalEquations(normal, rhs, terms, components, coefficients))
            {
                rankDeficient++;
                continue;
            }

            int patch = patchOrigin.Count;
            patchOrigin.Add(origin);
            patchScale.Add(inv);
            patchCoefficients.Add((double[])coefficients.Clone());

            // Evaluate the fit at every node the patch touches. Corners AND mid-sides: this
            // is where a mid-side node gets its value, and where a boundary corner gets one
            // from a well-posed interior fit.
            for (int k = start; k < end; k++)
            {
                var element = mesh.Element(adjacency.Items[k]);
                for (int i = 0; i < perElement; i++)
                {
                    // Every element of this patch is in patchRegion, so the slot always
                    // exists — a node on the interface takes its patchRegion slot here and
                    // the other material's from the other side's patches.
                    int target = SlotOf(element[i], patchRegion);
                    Evaluate(
                        terms, components, origin, inv, coefficients,
                        mesh.Position(element[i]), basis, value);
                    accumulated[target] = field.Add(accumulated[target], field.FromComponents(value[..components]));
                    contributions[target]++;
                    if (source[target] < 0)
                        source[target] = patch;
                }
            }
        }

        FillFromNearestPatch<TValue, TField>(
            field, mesh, adjacency, terms, components, patchOrigin, patchScale, patchCoefficients,
            source, accumulated, contributions, basis, value, respectRegions);

        int fallbacks = 0;
        var slotValues = new TValue[slotCount];
        var recovered = new TValue[nodes];
        for (int v = 0; v < nodes; v++)
        {
            var (from, to) = SlotsAt(v);
            // Summed from the FIRST slot rather than from a zero value, so a node with one
            // slot reproduces the pre-interface expression bit for bit (adding a zero is not
            // the identity on a negative zero).
            var sum = from < to ? accumulated[from] : field.Zero;
            int total = from < to ? contributions[from] : 0;
            for (int s = from; s < to; s++)
            {
                if (s > from)
                {
                    sum = field.Add(sum, accumulated[s]);
                    total += contributions[s];
                }
                slotValues[s] = contributions[s] > 0
                    ? field.Scale(accumulated[s], 1.0 / contributions[s])
                    : averaged[s];
            }

            if (total > 0)
            {
                recovered[v] = field.Scale(sum, 1.0 / total);
            }
            else
            {
                // No patch reached this node in ANY of its regions, so it keeps the directly
                // averaged value. A node in NO element has no slot and no average either —
                // the value type's zero is what the averaging pass leaves there, so it is
                // what this leaves too.
                recovered[v] = from < to ? averaged[from] : field.Zero;
                fallbacks++;
            }
        }
        return new RecoveredField<TValue>(
            recovered, slotValues, contributions,
            !respectRegions || mesh.RegionSlotsAreNodes, fallbacks, rankDeficient);
    }

    /// <summary>Evaluates a fitted patch polynomial at a physical point, into its component
    /// entries.</summary>
    private static void Evaluate(
        int terms, int components, Vector3d origin, double inv, double[] coefficients,
        Vector3d at, Span<double> basis, Span<double> value)
    {
        Basis(terms, (at - origin) * inv, basis);
        value.Clear();
        for (int a = 0; a < terms; a++)
        {
            double ba = basis[a];
            int atc = a * components;
            for (int c = 0; c < components; c++)
                value[c] += ba * coefficients[atc + c];
        }
    }

    /// <summary>
    /// Gives every remaining node the polynomial of the NEAREST patch, by a breadth-first
    /// walk over the share-an-element graph.
    ///
    /// <para><b>This is the boundary problem, and skipping it costs the whole point of the
    /// feature.</b> Patches are assembled only at INTERIOR corners — a one-sided patch is
    /// poorly conditioned and extrapolating from it is what caps SPR's rate at a boundary —
    /// but that leaves the nodes near a box's edges and corners in elements with no interior
    /// corner at all, and those are exactly where a peak usually sits. Measured on
    /// the manufactured-solution fixture: excluding boundary patches lifted the quadratic
    /// rate from a drifting 2.47 to 3.08/2.91, i.e. onto the theoretical p+1, and left 228
    /// of ~2000 nodes with no value; this walk closes those without putting the one-sided
    /// fits back.</para>
    ///
    /// <para>Extrapolating a patch polynomial a short way outside its own patch is the
    /// textbook boundary treatment and is sound for the reason the fit is: the polynomial
    /// approximates the field over a neighbourhood, not only over the sample hull.
    /// It degrades with distance, which is why the walk takes the NEAREST patch in graph
    /// distance rather than any patch, and why nodes are filled in rounds rather than all
    /// from one seed.</para>
    ///
    /// <para><b>The walk stays inside one material.</b> It fills SLOTS, and a slot's region
    /// admits only elements of that region and only patches fitted in it — otherwise the
    /// interface exclusion above would be undone one round later, by extrapolating the other
    /// material's polynomial into this one.</para>
    /// </summary>
    private static void FillFromNearestPatch<TValue, TField>(
        TField field,
        AnalysisMesh mesh,
        Adjacency adjacency,
        int terms,
        int components,
        List<Vector3d> patchOrigin,
        List<double> patchScale,
        List<double[]> patchCoefficients,
        int[] source,
        TValue[] accumulated,
        int[] contributions,
        Span<double> basis,
        Span<double> value,
        bool respectRegions)
        where TField : struct, IRecoveryField<TValue>
    {
        if (patchOrigin.Count == 0)
            return;

        int nodes = mesh.NodeCount;
        var pending = new List<int>();
        var pendingNode = new List<int>();
        var assigned = new List<int>();
        while (true)
        {
            pending.Clear();
            pendingNode.Clear();
            assigned.Clear();
            for (int node = 0; node < nodes; node++)
            {
                var (slotStart, slotEnd) =
                    respectRegions ? mesh.RegionSlotRange(node) : (node, node + 1);
                for (int slot = slotStart; slot < slotEnd; slot++)
                {
                    if (contributions[slot] > 0)
                        continue;
                    int region = respectRegions ? mesh.RegionOfSlot(slot) : 0;

                    // The nearest already-covered neighbour's patch, within this region.
                    // Index order throughout, so the answer does not depend on a traversal
                    // the caller cannot see.
                    int found = -1;
                    var (start, end) = adjacency.Range(node);
                    for (int k = start; k < end && found < 0; k++)
                    {
                        int element = adjacency.Items[k];
                        if (respectRegions && mesh.RegionOf(element) != region)
                            continue;
                        foreach (int neighbour in mesh.Element(element))
                        {
                            int neighbourSlot =
                                respectRegions ? mesh.RegionSlot(neighbour, region) : neighbour;
                            if (source[neighbourSlot] >= 0)
                            {
                                found = source[neighbourSlot];
                                break;
                            }
                        }
                    }
                    if (found >= 0)
                    {
                        pending.Add(slot);
                        pendingNode.Add(node);
                        assigned.Add(found);
                    }
                }
            }
            if (pending.Count == 0)
                return;

            for (int i = 0; i < pending.Count; i++)
            {
                int slot = pending[i];
                int patch = assigned[i];
                Evaluate(
                    terms, components, patchOrigin[patch], patchScale[patch],
                    patchCoefficients[patch], mesh.Position(pendingNode[i]), basis, value);
                accumulated[slot] = field.FromComponents(value[..components]);
                contributions[slot] = 1;
                source[slot] = patch;
            }
        }
    }

    /// <summary>
    /// The complete polynomial basis of the primary field's own degree, at a LOCAL point.
    /// <para>Degree p rather than p+1 or p-1 because that is the order the recovered field
    /// is claimed to have: sampling a superconvergent (order p) field and fitting order p
    /// preserves it, where a lower-degree fit would throw it away and a higher-degree one
    /// would interpolate noise the samples do not carry.</para>
    /// </summary>
    private static void Basis(int terms, Vector3d p, Span<double> basis)
    {
        basis[0] = 1;
        basis[1] = p.X;
        basis[2] = p.Y;
        basis[3] = p.Z;
        if (terms == 4)
            return;
        basis[4] = p.X * p.X;
        basis[5] = p.Y * p.Y;
        basis[6] = p.Z * p.Z;
        basis[7] = p.X * p.Y;
        basis[8] = p.Y * p.Z;
        basis[9] = p.Z * p.X;
    }

    /// <summary>
    /// Solves the <paramref name="components"/> normal-equation systems that share one matrix,
    /// by Cholesky. Returns false when the matrix is rank-deficient — which is the honest
    /// answer for a patch whose sample points lie on a plane, and is why the caller falls back
    /// rather than regularising: a damped fit would return a number for a patch that carries no
    /// information about the coefficient it was asked for.
    /// </summary>
    private static bool SolveNormalEquations(
        double[] normal, double[] rhs, int n, int components, double[] coefficients)
    {
        var l = new double[n * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                // The upper triangle is what was accumulated; read it symmetrically.
                double sum = j <= i ? normal[j * n + i] : normal[i * n + j];
                for (int k = 0; k < j; k++)
                    sum -= l[i * n + k] * l[j * n + k];
                if (i == j)
                {
                    // Relative rank test against the original diagonal, which is a sum of
                    // squares of O(1) basis values: an absolute floor would be a statement
                    // about the sample COUNT rather than about rank.
                    if (!(sum > 1e-12 * normal[i * n + i]))
                        return false;
                    l[i * n + j] = Math.Sqrt(sum);
                }
                else
                {
                    l[i * n + j] = sum / l[j * n + j];
                }
            }
        }

        Span<double> y = stackalloc double[10];
        for (int c = 0; c < components; c++)
        {
            for (int i = 0; i < n; i++)
            {
                double sum = rhs[i * components + c];
                for (int k = 0; k < i; k++)
                    sum -= l[i * n + k] * y[k];
                y[i] = sum / l[i * n + i];
            }
            for (int i = n - 1; i >= 0; i--)
            {
                double sum = y[i];
                for (int k = i + 1; k < n; k++)
                    sum -= l[k * n + i] * coefficients[k * components + c];
                coefficients[i * components + c] = sum / l[i * n + i];
            }
        }
        return true;
    }

    /// <summary>Which nodes are element CORNERS — the ones a patch is assembled at.</summary>
    private static bool[] CornerNodes(AnalysisMesh mesh)
    {
        var corner = new bool[mesh.NodeCount];
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var element = mesh.Element(e);
            for (int i = 0; i < 4; i++)
                corner[element[i]] = true;
        }
        return corner;
    }

    /// <summary>Which nodes lie on the boundary — the ones a patch is NOT assembled at.</summary>
    private static bool[] BoundaryNodes(AnalysisMesh mesh)
    {
        var boundary = new bool[mesh.NodeCount];
        for (int f = 0; f < mesh.FacetCount; f++)
        {
            foreach (int node in mesh.Facet(f))
                boundary[node] = true;
        }
        return boundary;
    }

    /// <summary>Node-to-element adjacency in CSR form — counted, then filled, so nothing
    /// allocates per node.</summary>
    private static Adjacency BuildNodeToElement(AnalysisMesh mesh)
    {
        int nodes = mesh.NodeCount;
        var starts = new int[nodes + 1];
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            foreach (int node in mesh.Element(e))
                starts[node + 1]++;
        }
        for (int v = 0; v < nodes; v++)
            starts[v + 1] += starts[v];

        var cursor = (int[])starts.Clone();
        var items = new int[starts[nodes]];
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            foreach (int node in mesh.Element(e))
                items[cursor[node]++] = e;
        }
        return new Adjacency(starts, items);
    }

    private readonly record struct Adjacency(int[] Starts, int[] Items)
    {
        public (int Start, int End) Range(int node) => (Starts[node], Starts[node + 1]);
    }

    // ---- the structural instantiation ------------------------------------------------

    /// <summary>The recovered nodal stress and how much of it is genuinely recovered — the
    /// structural spelling of <see cref="RecoveredField{TValue}"/>, kept so
    /// <see cref="StructuralResults"/>' consumers are untouched.</summary>
    internal readonly record struct RecoveredStress(
        SymmetricTensor3[] Stress,
        SymmetricTensor3[] SlotStress,
        int[] SlotContributions,
        bool SlotsAreNodes,
        int FallbackNodes,
        int RankDeficientPatches);

    /// <summary>Recovers a nodal STRESS field from a solved structural model.</summary>
    public static RecoveredStress Recover(
        StructuralResults results,
        IReadOnlyList<SymmetricTensor3> averaged,
        bool respectRegions = true)
    {
        var recovered =
            Recover<SymmetricTensor3, StressField>(new StressField(results), averaged, respectRegions);
        return new RecoveredStress(
            recovered.Values, recovered.SlotValues, recovered.SlotContributions,
            recovered.SlotsAreNodes, recovered.FallbackNodes, recovered.RankDeficientPatches);
    }

    /// <summary>The stress field: the Voigt Cauchy stress, sampled through
    /// <see cref="StructuralResults.VoigtStressAt"/> (thermal-strain subtraction included).</summary>
    private readonly struct StressField(StructuralResults results)
        : IRecoveryField<SymmetricTensor3>
    {
        public AnalysisMesh Mesh => results.Mesh;
        public int Components => 6;
        public int DofsPerNode => 3;

        public void GatherDofs(int element, Span<Vector3d> positions, Span<double> dofs) =>
            results.Gather(element, positions, dofs);

        public void SampleAt(
            int element, ReadOnlySpan<Vector3d> positions, ReadOnlySpan<double> dofs,
            double r, double s, double t, Span<double> value) =>
            results.VoigtStressAt(element, positions, dofs, r, s, t, value);

        public SymmetricTensor3 Zero => SymmetricTensor3.Zero;

        public SymmetricTensor3 FromComponents(ReadOnlySpan<double> value) =>
            TetElement.ToTensor(value, engineeringShear: false);

        public SymmetricTensor3 Add(SymmetricTensor3 a, SymmetricTensor3 b) => a + b;

        public SymmetricTensor3 Scale(SymmetricTensor3 a, double s) => a * s;
    }
}
