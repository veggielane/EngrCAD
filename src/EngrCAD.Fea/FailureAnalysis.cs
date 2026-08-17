using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Fea;

/// <summary>
/// Directional failure criteria over a solved model — the answer to the question
/// <c>MaxVonMises</c> cannot ask of a composite.
///
/// <para><b>Why von Mises is meaningless here.</b> A scalar equivalent stress compares a
/// state against ONE allowable, which presumes the material is equally strong in every
/// direction. A lamina is not: a carbon/epoxy ply is twenty times stronger along the fibre
/// than across it and stronger still in compression than in transverse tension, so the same
/// von Mises number can be perfectly safe in one direction and well past failure in
/// another. The criteria here take the stress apart in the MATERIAL frame and measure each
/// piece against its own allowable.</para>
///
/// <para><b>Arithmetic over a solved result, not a solver change.</b> Nothing is assembled
/// and nothing is factored: the stress is the one <see cref="StructuralResults"/> already
/// recovered, the frame is the one the region's <see cref="ElasticLaw"/> was rotated by, and
/// the allowables are the region's <see cref="LaminaStrength"/>. That is the same shape
/// <see cref="FatigueAnalysis"/> takes, for the same reason.</para>
///
/// <para><b>Evaluated per (node, region) SLOT.</b> Both the frame and the allowables belong
/// to a region, so at a material interface a node has one honest answer per material —
/// <see cref="FailureResults.FailureIndexIn"/> — and the published per-node field takes the
/// WORST of them. That is deliberate and different from how the stress field blends there: a
/// failure index is a max-type quantity, and averaging two materials' indices would report a
/// number neither material carries.</para>
/// </summary>
public static class FailureAnalysis
{
    /// <summary>
    /// Evaluates a criterion at every node of a solved model.
    ///
    /// <para>Refuses by name when NO region states a strength set: there is nothing to
    /// measure against, and returning an all-NaN field would look like a solve that had run
    /// and found nothing. Where SOME regions state one, the others are NaN — the "no value"
    /// spelling ranging and the colour map already skip.</para>
    /// </summary>
    /// <param name="results">A solved structural result.</param>
    /// <param name="criterion">Which criterion to evaluate.</param>
    public static FailureResults Evaluate(StructuralResults results, FailureCriterion criterion)
    {
        ArgumentNullException.ThrowIfNull(results);
        var model = results.Model;
        var mesh = results.Mesh;

        if (!model.HasStrengths)
        {
            throw new FeaException(
                $"No region of this model states a LaminaStrength, so the {criterion} criterion "
                + "has nothing to measure against. Call StructuralModel.SetStrength(region, ...) "
                + $"for at least one of the {mesh.Regions.Count} region(s) present "
                + $"({string.Join(", ", mesh.Regions)}).");
        }

        int nodes = mesh.NodeCount;
        var index = new double[nodes];
        var ratio = new double[nodes];
        var modes = new FailureMode[nodes];
        var slotIndex = new double[mesh.RegionSlotCount];
        var slotRatio = new double[mesh.RegionSlotCount];
        Array.Fill(index, double.NaN);
        Array.Fill(ratio, double.NaN);
        Array.Fill(slotIndex, double.NaN);
        Array.Fill(slotRatio, double.NaN);

        double maxOutOfPlane = 0;
        double maxInPlane = 0;
        int worstOutOfPlaneNode = -1;
        int covered = 0;

        for (int node = 0; node < nodes; node++)
        {
            bool any = false;
            foreach (int region in mesh.RegionsAt(node))
            {
                var strength = model.StrengthOfRegion(region);
                if (strength is null)
                    continue;

                var law = model.ElasticityOfRegion(region);
                var material = law.ToMaterialFrame(results.NodalStressIn(region, node));
                var evaluation = strength.Evaluate(criterion, material);

                int slot = mesh.RegionSlot(node, region);
                slotIndex[slot] = evaluation.Index;
                slotRatio[slot] = evaluation.StrengthRatio;

                // The worst over the materials meeting here — see the type remarks.
                if (!any || evaluation.Index > index[node])
                {
                    index[node] = evaluation.Index;
                    ratio[node] = evaluation.StrengthRatio;
                    modes[node] = evaluation.Mode;
                }
                any = true;

                double inPlane = Math.Max(
                    Math.Max(Math.Abs(material.Xx), Math.Abs(material.Yy)), Math.Abs(material.Xy));
                double outOfPlane = Math.Max(
                    Math.Max(Math.Abs(material.Zz), Math.Abs(material.Yz)), Math.Abs(material.Xz));
                // The two extremes are tracked separately and divided ONCE at the end,
                // deliberately: a per-node ratio is dominated by whichever node happens to
                // carry almost no in-plane stress, where the quotient is large and means
                // nothing. Measured on a tension panel with a hole, the per-node form
                // reported 4.4 - "440% out-of-plane" on a plate loaded purely in its plane -
                // from a lightly stressed corner. Normalising by the GLOBAL in-plane scale
                // is the same small-denominator lesson the epsilon ladder records, applied
                // to a diagnostic.
                maxInPlane = Math.Max(maxInPlane, inPlane);
                if (outOfPlane > maxOutOfPlane)
                {
                    maxOutOfPlane = outOfPlane;
                    worstOutOfPlaneNode = node;
                }
            }
            if (any)
                covered++;
        }

        return new FailureResults(
            results, criterion, index, ratio, modes, slotIndex, slotRatio, covered,
            maxInPlane > 0 ? maxOutOfPlane / maxInPlane : 0, worstOutOfPlaneNode);
    }
}

/// <summary>
/// The answer: per-node failure index and strength ratio, plus the two ways out every
/// results type here has — <see cref="Fields"/> over the analysis nodes and
/// <see cref="SampleOnto(HalfEdgeMesh)"/> onto a display mesh.
/// </summary>
public sealed class FailureResults
{
    private readonly double[] _index;
    private readonly double[] _ratio;
    private readonly FailureMode[] _modes;
    private readonly double[] _slotIndex;
    private readonly double[] _slotRatio;

    internal FailureResults(
        StructuralResults results,
        FailureCriterion criterion,
        double[] index,
        double[] ratio,
        FailureMode[] modes,
        double[] slotIndex,
        double[] slotRatio,
        int coveredNodes,
        double maxOutOfPlaneFraction,
        int maxOutOfPlaneNode)
    {
        Results = results;
        Criterion = criterion;
        _index = index;
        _ratio = ratio;
        _modes = modes;
        _slotIndex = slotIndex;
        _slotRatio = slotRatio;
        CoveredNodes = coveredNodes;
        MaxOutOfPlaneFraction = maxOutOfPlaneFraction;
        MaxOutOfPlaneNode = maxOutOfPlaneNode;
        FailureIndex = Array.AsReadOnly(index);
        StrengthRatio = Array.AsReadOnly(ratio);
    }

    /// <summary>The solved result this was evaluated over.</summary>
    public StructuralResults Results { get; }

    /// <summary>The criterion evaluated.</summary>
    public FailureCriterion Criterion { get; }

    /// <summary>The analysis mesh.</summary>
    public AnalysisMesh Mesh => Results.Mesh;

    /// <summary>
    /// Per-node failure index — 1 exactly at the limit, above 1 failed, and LINEAR in the
    /// load for every criterion (it is <c>1 / StrengthRatio</c>, not the raw quadratic
    /// polynomial; see <see cref="LaminaStrength.Evaluate(FailureCriterion, double, double, double)"/>).
    /// NaN where the node's material states no strength.
    /// </summary>
    public IReadOnlyList<double> FailureIndex { get; }

    /// <summary>Per-node strength ratio R: the multiplier on the load at which the criterion
    /// is met. Positive infinity where the state is unstressed, NaN where no strength is
    /// stated.</summary>
    public IReadOnlyList<double> StrengthRatio { get; }

    /// <summary>How many nodes carry a value — the rest are NaN because their material
    /// states no strength.</summary>
    public int CoveredNodes { get; }

    /// <summary>
    /// The largest out-of-plane stress magnitude anywhere evaluated, as a fraction of the
    /// largest IN-PLANE stress magnitude anywhere evaluated.
    ///
    /// <para><b>The plane-stress criteria consume only sigma1, sigma2 and tau12</b>, so this
    /// is the number that says whether that idealisation is defensible: near zero in a thin
    /// laminate loaded in its plane (which is what laminates are for), and appreciable at a
    /// free edge, under a bolt, or beneath a contact patch — exactly the places interlaminar
    /// failure starts. It is REPORTED rather than folded into the index because delamination
    /// is a different mechanism against different allowables, and a smeared law has no ply
    /// interfaces to separate.</para>
    ///
    /// <para><b>Both extremes are global, and dividing per NODE would be wrong</b> — a node
    /// carrying almost no in-plane stress makes the quotient large and meaningless, which
    /// measured 4.4 on a tension panel loaded purely in its plane. The quantity here answers
    /// "is there anywhere an out-of-plane stress comparable with the in-plane stresses that
    /// matter", which is the question worth asking.</para>
    /// </summary>
    public double MaxOutOfPlaneFraction { get; }

    /// <summary>The node carrying the largest out-of-plane stress magnitude, or -1.</summary>
    public int MaxOutOfPlaneNode { get; }

    /// <summary>The failure mode at one node (the driving component for
    /// <see cref="FailureCriterion.MaxStress"/>, <see cref="FailureMode.Interactive"/> for
    /// the quadratic criteria).</summary>
    public FailureMode ModeAt(int node) => _modes[node];

    /// <summary>The failure index at one node AS SEEN FROM one material region — the honest
    /// value where <see cref="FailureIndex"/> can only report the worst of several. Refused
    /// by name when the node touches no element of that region.</summary>
    public double FailureIndexIn(int region, int node) => _slotIndex[Mesh.RegionSlot(node, region)];

    /// <summary>The strength ratio at one node as seen from one material region.</summary>
    public double StrengthRatioIn(int region, int node) => _slotRatio[Mesh.RegionSlot(node, region)];

    /// <summary>
    /// The failure index at one ELEMENT's centroid, from the element's own stress before any
    /// nodal averaging — the value the assembly integrated, and the one to quote when a
    /// recovery's smoothing is in question.
    /// </summary>
    public double ElementFailureIndex(int element)
    {
        var strength = Results.Model.StrengthOf(element);
        if (strength is null)
            return double.NaN;
        var law = Results.Model.ElasticityOf(element);
        var material = law.ToMaterialFrame(Results.ElementStress(element));
        return strength.Evaluate(Criterion, material).Index;
    }

    /// <summary>The largest failure index over all nodes (NaN entries skipped), or NaN when
    /// nothing was covered.</summary>
    public double MaxFailureIndex
    {
        get
        {
            double worst = double.NaN;
            foreach (double v in _index)
            {
                if (!double.IsNaN(v) && !(v <= worst))
                    worst = v;
            }
            return worst;
        }
    }

    /// <summary>The node carrying <see cref="MaxFailureIndex"/> (lowest index on a tie), or
    /// -1.</summary>
    public int MaxFailureIndexNode
    {
        get
        {
            int best = -1;
            double worst = double.NaN;
            for (int i = 0; i < _index.Length; i++)
            {
                if (double.IsNaN(_index[i]) || _index[i] <= worst)
                    continue;
                worst = _index[i];
                best = i;
            }
            return best;
        }
    }

    /// <summary>The smallest strength ratio over all nodes — the load multiplier the whole
    /// part survives to.</summary>
    public double MinStrengthRatio
    {
        get
        {
            double best = double.NaN;
            foreach (double v in _ratio)
            {
                if (!double.IsNaN(v) && !(v >= best))
                    best = v;
            }
            return best;
        }
    }

    /// <summary>True when every covered node is strictly inside the failure surface.</summary>
    public bool IsSafe => !(MaxFailureIndex >= 1.0);

    /// <summary>The field names this class produces — so a <c>FieldDisplay</c> and this
    /// post-processor cannot disagree about a spelling.</summary>
    public static class FieldNames
    {
        /// <summary>Per-node failure index (dimensionless; 1 is the limit).</summary>
        public const string FailureIndex = "Failure index";

        /// <summary>Per-node strength ratio (dimensionless load multiplier to failure).</summary>
        public const string StrengthRatio = "Strength ratio";
    }

    /// <summary>The results as fields over the analysis nodes.</summary>
    public IReadOnlyList<MeshField> Fields() =>
    [
        MeshField.Scalar(FieldNames.FailureIndex, "", _index),
        MeshField.Scalar(FieldNames.StrengthRatio, "", _ratio),
    ];

    /// <summary>The results resampled onto a display mesh — exact where the meshes share a
    /// vertex (see <see cref="StructuralResults.SampleOnto(HalfEdgeMesh, out double, string, string)"/>).</summary>
    public IReadOnlyList<MeshField> SampleOnto(HalfEdgeMesh displayMesh, out double maxSampleDistance)
    {
        ArgumentNullException.ThrowIfNull(displayMesh);
        var sampler = SurfaceSampler.Build(Mesh, displayMesh);
        maxSampleDistance = sampler.MaxSampleDistance;
        return
        [
            MeshField.Scalar(FieldNames.FailureIndex, "", sampler.Sample(_index)),
            MeshField.Scalar(FieldNames.StrengthRatio, "", sampler.Sample(_ratio)),
        ];
    }

    /// <summary><see cref="SampleOnto(HalfEdgeMesh, out double)"/> without the diagnostic.</summary>
    public IReadOnlyList<MeshField> SampleOnto(HalfEdgeMesh displayMesh) =>
        SampleOnto(displayMesh, out _);

    /// <inheritdoc/>
    public override string ToString() =>
        $"{Criterion}: max index {MaxFailureIndex:G6} at node {MaxFailureIndexNode}, "
        + $"{CoveredNodes:N0} of {Mesh.NodeCount:N0} nodes covered";
}
