using EngrCAD.Core;
using EngrCAD.Core.Spatial;

namespace EngrCAD.Fea;

/// <summary>Which filter <see cref="TopologyOptimizer"/> applies over
/// <see cref="TopologyOptions.FilterRadius"/>.</summary>
public enum TopologyFilter
{
    /// <summary>
    /// The DENSITY filter (Bruns-Tortorelli / Bourdin) and the default: the design variable
    /// is convolved into a physical density <c>rho~ = W x</c>, the stiffness is built from
    /// <c>rho~</c>, and the sensitivity is carried back by <c>W'</c>.
    ///
    /// <para>It is the default because it is a genuine CHANGE OF VARIABLES: the problem being
    /// solved is a real optimisation problem in <c>x</c>, so the reported sensitivity is the
    /// exact gradient of the compliance that was actually computed — which is what makes a
    /// finite-difference check of it meaningful, and <see cref="Sensitivity"/> is precisely
    /// the option that check cannot be run against.</para>
    ///
    /// <para>The cost is that the physical volume fraction is not identical to the design one
    /// (the normalised weights are not exactly volume-preserving near a boundary), so
    /// <see cref="TopologyResult.PhysicalVolumeFraction"/> is reported beside the design
    /// fraction rather than the difference being hidden. And the result is BLURRIER at the
    /// same radius, because material genuinely spreads.</para>
    /// </summary>
    Density,

    /// <summary>
    /// The SENSITIVITY filter (Sigmund 1997), the one the 99-line paper uses: the densities
    /// are untouched and the SENSITIVITY is smoothed before the update.
    ///
    /// <para><b>It is a heuristic and is offered as one.</b> The filtered sensitivity is not
    /// the gradient of anything — no objective has it as its derivative — so it is verified by
    /// what it produces (no checkerboard, a mesh-independent structure) and never by a
    /// finite-difference comparison, which it would fail by construction. What it buys is a
    /// crisper result at the same radius, because the density field itself is never
    /// convolved: design density IS physical density, so the volume constraint and the field
    /// a threshold is applied to are the same numbers.</para>
    /// </summary>
    Sensitivity,

    /// <summary>
    /// No filter — <b>which is not a setting, it is a defect</b>, and it exists so the defect
    /// can be MEASURED.
    ///
    /// <para>Unfiltered SIMP checkerboards (alternating solid and void is an artefact of that
    /// pattern overestimating its own stiffness in a displacement-based element, not a
    /// structure) and is MESH-DEPENDENT: refining gives a different, finer truss forever
    /// rather than converging on one. Both are pinned by test against the filtered runs, which
    /// is the only reason this member is public.</para>
    /// </summary>
    None,
}

/// <summary>
/// The neighbourhood convolution both filters share: for every element, the elements whose
/// CENTROID lies within <c>r_min</c> of its own, with the classic linear hat weight
/// <c>w = r_min - d</c>.
///
/// <para><b><c>r_min</c> is an engineering input, not a numerical knob.</b> It is what sets
/// the minimum member size the answer can contain, so it is stated in model units and is a
/// property of the manufacturing route (a printer's smallest reliable wall, a cutter's
/// diameter) rather than something to turn until the picture looks right. That is also why
/// there is no default for it: a default would be a manufacturing decision made by a
/// library.</para>
///
/// <para><b>Weights carry element VOLUME, which the published uniform-grid forms do not have
/// to.</b> On a structured grid every element has the same volume and it cancels; on a
/// tetrahedral mesh it does not, and without it a patch of small elements would out-vote a
/// neighbouring patch of large ones purely by being numerous. Including <c>v_j</c> reduces
/// EXACTLY to the published form when the volumes are equal, which is the property that makes
/// it a generalisation rather than a different filter.</para>
/// </summary>
internal sealed class DensityFilter
{
    // CSR over the neighbour lists: one contiguous array of neighbour indices and one of
    // weights already multiplied by the neighbour's volume, so the inner loops are two
    // sequential reads. Neighbour lists are symmetric as SETS (distance is), but the stored
    // weight w_ij*v_j is NOT symmetric, which is exactly why the density filter's transpose
    // has to be spelled out rather than reusing the forward pass.
    private readonly int[] _starts;
    private readonly int[] _items;
    private readonly double[] _weights;
    private readonly double[] _rowSum;

    private DensityFilter(int[] starts, int[] items, double[] weights, double[] rowSum)
    {
        _starts = starts;
        _items = items;
        _weights = weights;
        _rowSum = rowSum;
    }

    /// <summary>The largest number of neighbours any element has — the report's honest
    /// statement of how wide the radius reached in this mesh.</summary>
    public int MaxNeighbours
    {
        get
        {
            int best = 0;
            for (int e = 0; e + 1 < _starts.Length; e++)
                best = Math.Max(best, _starts[e + 1] - _starts[e]);
            return best;
        }
    }

    /// <summary>The mean number of neighbours.</summary>
    public double MeanNeighbours =>
        _starts.Length > 1 ? (double)_items.Length / (_starts.Length - 1) : 0;

    /// <summary>
    /// Builds the neighbourhood over element centroids.
    ///
    /// <para>A BVH over per-element centroid boxes, queried with a box of half-extent
    /// <paramref name="radius"/> and then filtered by true distance — the broad-phase /
    /// narrow-phase split every spatial query in this repository makes, and the reason the
    /// build is O(n log n) rather than O(n^2) on a mesh with tens of thousands of
    /// elements.</para>
    /// </summary>
    public static DensityFilter Build(
        AnalysisMesh mesh, double radius, IReadOnlyList<double> volumes,
        ProgressCancel? progress = null)
    {
        int count = mesh.ElementCount;
        var centroids = new Vector3d[count];
        var boxes = new Aabb[count];
        for (int e = 0; e < count; e++)
        {
            var nodes = mesh.Element(e);
            // CORNER nodes only: a quadratic element's mid-edge nodes are exact midpoints of
            // its own corners, so including them would weight the centroid toward nothing in
            // particular while making the linear and quadratic answers differ for a reason
            // that is about node counting rather than geometry.
            var centre = 0.25 * (mesh.Position(nodes[0]) + mesh.Position(nodes[1])
                + mesh.Position(nodes[2]) + mesh.Position(nodes[3]));
            centroids[e] = centre;
            boxes[e] = new Aabb(centre, centre);
        }

        var bvh = Bvh.Build(boxes);
        var starts = new int[count + 1];
        var items = new List<int>(count * 8);
        var weights = new List<double>(count * 8);
        var rowSum = new double[count];
        var found = new List<int>();
        var offset = new Vector3d(radius, radius, radius);

        for (int e = 0; e < count; e++)
        {
            if ((e & 1023) == 0)
                progress?.ThrowIfCancelled();
            starts[e] = items.Count;
            found.Clear();
            bvh.Query(new Aabb(centroids[e] - offset, centroids[e] + offset), found);
            // Ascending, so the accumulation order is a function of the mesh and not of the
            // BVH's leaf-visit order — two runs must sum the same terms in the same sequence.
            found.Sort();
            double sum = 0;
            foreach (int j in found)
            {
                double d = centroids[e].DistanceTo(centroids[j]);
                if (d >= radius)
                    continue;
                double w = (radius - d) * volumes[j];
                if (w <= 0)
                    continue;
                items.Add(j);
                weights.Add(w);
                sum += w;
            }
            rowSum[e] = sum;
        }
        starts[count] = items.Count;
        return new DensityFilter(starts, [.. items], [.. weights], rowSum);
    }

    /// <summary>
    /// The density filter's forward map <c>rho~ = W x</c>: each element's physical density is
    /// the weighted mean of the design densities in its neighbourhood.
    /// <para>The weights are normalised per row, so a UNIFORM design field maps to itself
    /// exactly — which is what makes the uniform-bar closed form in the tests a statement
    /// about the optimiser rather than about the filter.</para>
    /// </summary>
    public void Apply(IReadOnlyList<double> design, double[] physical)
    {
        for (int e = 0; e < physical.Length; e++)
        {
            double sum = 0;
            for (int k = _starts[e]; k < _starts[e + 1]; k++)
                sum += _weights[k] * design[_items[k]];
            physical[e] = _rowSum[e] > 0 ? sum / _rowSum[e] : design[e];
        }
    }

    /// <summary>
    /// The density filter's chain rule <c>dc/dx = W' dc/drho~</c>.
    ///
    /// <para>Spelled as a scatter rather than a gather because the stored weight is
    /// <c>w_ij·v_j</c>, which is NOT symmetric in i and j on a mesh with varying element size
    /// — reusing <see cref="Apply"/> here would silently transpose the wrong matrix and give a
    /// gradient that is plausible, smooth and wrong.</para>
    /// </summary>
    public void ApplyTranspose(IReadOnlyList<double> physicalSensitivity, double[] design)
    {
        Array.Clear(design);
        for (int e = 0; e < design.Length; e++)
        {
            if (!(_rowSum[e] > 0))
            {
                design[e] += physicalSensitivity[e];
                continue;
            }
            double scaled = physicalSensitivity[e] / _rowSum[e];
            for (int k = _starts[e]; k < _starts[e + 1]; k++)
                design[_items[k]] += _weights[k] * scaled;
        }
    }

    /// <summary>
    /// Sigmund's sensitivity filter:
    /// <c>dc~_i = sum_j(w_ij v_j rho_j dc_j) / (max(gamma, rho_i) sum_j w_ij v_j)</c>.
    ///
    /// <para><paramref name="floor"/> is the classical <c>gamma</c> guard on the divisor. It
    /// is not a tolerance on a measured quantity but a floor on a DESIGN VARIABLE that the
    /// optimiser's own bounds already keep above
    /// <see cref="TopologyOptions.MinimumDensity"/> — it exists so the expression stays finite
    /// if a caller sets that bound to exactly zero.</para>
    /// </summary>
    public void ApplySensitivity(
        IReadOnlyList<double> density, IReadOnlyList<double> sensitivity, double[] filtered,
        double floor)
    {
        for (int e = 0; e < filtered.Length; e++)
        {
            double sum = 0;
            for (int k = _starts[e]; k < _starts[e + 1]; k++)
            {
                int j = _items[k];
                sum += _weights[k] * density[j] * sensitivity[j];
            }
            double divisor = Math.Max(floor, density[e]) * _rowSum[e];
            filtered[e] = divisor > 0 ? sum / divisor : sensitivity[e];
        }
    }
}
