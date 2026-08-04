using EngrCAD.Mesh;

namespace EngrCAD.Fea;

/// <summary>Why <see cref="TopologyOptimizer.Minimize"/> stopped.</summary>
public enum TopologyStop
{
    /// <summary>No design variable moved further than
    /// <see cref="TopologyOptions.ChangeTolerance"/> in the last update.</summary>
    Converged,

    /// <summary><see cref="TopologyOptions.MaxIterations"/> was reached with the design still
    /// moving. The answer is the last iterate, not a converged one.</summary>
    IterationLimit,
}

/// <summary>One iteration of the optimisation — the row a convergence plot is drawn from.</summary>
/// <param name="Number">The iteration, from 1.</param>
/// <param name="Compliance">The compliance <c>c = u'Ku = f'u</c> AFTER this iteration's solve,
/// i.e. of the design the previous update produced.</param>
/// <param name="VolumeFraction">The PHYSICAL volume fraction the update landed on — exactly
/// <see cref="TopologyOptions.VolumeFraction"/> whenever the move limits allowed it (see
/// <paramref name="VolumeConstraintMet"/>).</param>
/// <param name="MaxChange">The largest change in any design variable, the convergence
/// measure.</param>
/// <param name="VolumeConstraintMet">Whether the bisection could place the multiplier so the
/// volume is met exactly. False means every element clamped against its move limit before the
/// target was reached — the constraint is then met as closely as one step allows, and this
/// says so rather than the miss being hidden in a tolerance.</param>
public readonly record struct TopologyIteration(
    int Number,
    double Compliance,
    double VolumeFraction,
    double MaxChange,
    bool VolumeConstraintMet);

/// <summary>
/// The answer: a DENSITY FIELD, and that is the honest form rather than a limitation to hide.
///
/// <para><b>An element at 0.5 is an unresolved decision, not half material.</b> SIMP's
/// penalisation exists to make intermediate densities uneconomic so the answer tends toward
/// solid-or-void, but nothing forces it there, and <see cref="Discreteness"/> is the
/// measurement of how far it got. Turning the field into a shape is a THRESHOLD plus a
/// polygonisation (<see cref="ExtractSurface"/>), the threshold is a stated parameter, and its
/// effect on the volume is reported (<see cref="ExtractedVolumeFraction"/>) rather than chosen
/// quietly.</para>
///
/// <para><b>A B-Rep is refused by name.</b> The level set of a piecewise-linear field over a
/// tetrahedral mesh is a faceted surface with as many facets as the mesh has crossings, and
/// there is no parametric surface in it to recover — offering one would mean fitting, whose
/// tolerance would silently become part of the answer. The route out is
/// <c>Shape.From(result.ExtractSurface(0.5))</c>, and re-modelling the result by hand is the
/// step every topology-optimisation workflow has.</para>
/// </summary>
public sealed class TopologyResult
{
    private readonly double[] _design;
    private readonly double[] _physical;
    private readonly double[] _volumes;
    private double[]? _nodal;

    internal TopologyResult(
        StructuralModel model,
        TopologyOptions options,
        double[] design,
        double[] physical,
        double[] volumes,
        IReadOnlyList<TopologyIteration> history,
        TopologyStop stop,
        int meanNeighbours,
        int maxNeighbours)
    {
        Model = model;
        Options = options;
        _design = design;
        _physical = physical;
        _volumes = volumes;
        History = history;
        Stop = stop;
        MeanNeighbours = meanNeighbours;
        MaxNeighbours = maxNeighbours;
    }

    /// <summary>The model that was optimised — its mesh, materials, supports and loads.</summary>
    public StructuralModel Model { get; }

    /// <summary>The settings the run used.</summary>
    public TopologyOptions Options { get; }

    /// <summary>The analysis mesh the densities are indexed by.</summary>
    public AnalysisMesh Mesh => Model.Mesh;

    /// <summary>Why the run stopped.</summary>
    public TopologyStop Stop { get; }

    /// <summary>Every iteration, in order.</summary>
    public IReadOnlyList<TopologyIteration> History { get; }

    /// <summary>Iterations performed.</summary>
    public int Iterations => History.Count;

    /// <summary>The compliance of the final design, <c>c = u'Ku</c> — twice its strain
    /// energy, and the quantity minimised.</summary>
    public double Compliance => History.Count > 0 ? History[^1].Compliance : double.NaN;

    /// <summary>
    /// The PHYSICAL density of each element — what the stiffness was built from, and what a
    /// threshold is applied to.
    /// <para>Identical to <see cref="DesignDensity"/> under
    /// <see cref="TopologyFilter.Sensitivity"/> and <see cref="TopologyFilter.None"/>, which
    /// leave the densities alone; under <see cref="TopologyFilter.Density"/> it is the
    /// convolution of them.</para>
    /// </summary>
    public IReadOnlyList<double> Density => _physical;

    /// <summary>The DESIGN variable of each element — what the volume constraint is stated
    /// on and what the optimiser updates.</summary>
    public IReadOnlyList<double> DesignDensity => _design;

    /// <summary>Element volumes, in the mesh's own order — the weights every fraction here
    /// is taken with.</summary>
    public IReadOnlyList<double> ElementVolumes => _volumes;

    /// <summary>The mesh's total volume.</summary>
    public double TotalVolume
    {
        get
        {
            double sum = 0;
            foreach (double v in _volumes)
                sum += v;
            return sum;
        }
    }

    /// <summary>
    /// The PHYSICAL volume fraction — how much material the structure contains, which is the
    /// CONSTRAINED quantity and is therefore equal to
    /// <see cref="TopologyOptions.VolumeFraction"/> to round-off whenever the last update's
    /// bisection could reach it.
    /// </summary>
    public double VolumeFraction => Fraction(_physical);

    /// <summary>
    /// The DESIGN volume fraction — the mean of the design variables, which is NOT the
    /// constrained quantity under <see cref="TopologyFilter.Density"/> and generally differs
    /// from <see cref="VolumeFraction"/> there.
    /// <para>A row-normalised filter is not volume-preserving (a boundary element's
    /// neighbourhood is truncated), so the two are genuinely different numbers and the one
    /// that means something — the material the structure holds — is the one constrained. This
    /// is reported so the gap is visible rather than assumed away.</para>
    /// </summary>
    public double DesignVolumeFraction => Fraction(_design);

    /// <summary>
    /// How far the answer got toward solid-or-void: the volume-weighted mean of
    /// <c>4·rho·(1 - rho)</c>, which is <b>0 for a fully discrete design and 1 for one that is
    /// uniformly grey</b> (the classical measure of non-discreteness).
    /// <para>It is the number that says whether thresholding the field means anything. A
    /// design at 0.6 is not a structure with a bit of blur; it is a structure the penalisation
    /// has not decided.</para>
    /// </summary>
    public double Discreteness
    {
        get
        {
            double sum = 0, total = 0;
            for (int e = 0; e < _physical.Length; e++)
            {
                double rho = _physical[e];
                sum += _volumes[e] * 4 * rho * (1 - rho);
                total += _volumes[e];
            }
            return total > 0 ? sum / total : 0;
        }
    }

    /// <summary>The mean number of elements inside one filter radius — how much
    /// neighbourhood <see cref="TopologyOptions.FilterRadius"/> found in THIS mesh. A value
    /// near 1 means the radius is smaller than an element and the filter is doing
    /// nothing.</summary>
    public int MeanNeighbours { get; }

    /// <summary>The largest neighbourhood any element has.</summary>
    public int MaxNeighbours { get; }

    /// <summary>
    /// The nodal density field — the element densities volume-averaged onto nodes, which is
    /// what <see cref="ExtractSurface"/> takes the level set of and what
    /// <see cref="Fields"/> publishes.
    /// </summary>
    public IReadOnlyList<double> NodalDensity =>
        _nodal ??= DensityIsoSurface.NodalDensity(Mesh, _physical, _volumes);

    /// <summary>The result as a <see cref="MeshField"/> over the ANALYSIS mesh's nodes — so a
    /// density plot needs no rendering code that does not already exist.</summary>
    public IReadOnlyList<MeshField> Fields() =>
        [MeshField.Scalar(FieldNames.Density, "density", NodalDensity)];

    /// <summary>The field names this class produces.</summary>
    public static class FieldNames
    {
        /// <summary>The nodal density field.</summary>
        public const string Density = "Density";
    }

    /// <summary>
    /// The density field sampled onto an arbitrary surface mesh — the display mesh a `Part`
    /// carries, through the same correspondence every other result type publishes by.
    /// </summary>
    public IReadOnlyList<MeshField> SampleOnto(HalfEdgeMesh displayMesh)
    {
        ArgumentNullException.ThrowIfNull(displayMesh);
        var sampler = SurfaceSampler.Build(Mesh, displayMesh);
        return [MeshField.Scalar(FieldNames.Density, "density", sampler.Sample(NodalDensity))];
    }

    /// <summary>
    /// The surface bounding <c>{ density &gt;= threshold }</c>, by marching the mesh's own
    /// tetrahedra (see <see cref="DensityIsoSurface"/> for why not Surface Nets).
    ///
    /// <para><b>The threshold is a stated parameter and it moves the volume</b>, which is why
    /// <see cref="ExtractedVolumeFraction"/> exists beside it: 0.5 is the conventional choice
    /// and is not the one that preserves the volume fraction unless the design is symmetric
    /// about it. Measure, do not assume.</para>
    /// </summary>
    /// <param name="threshold">The density level the surface bounds.</param>
    public HalfEdgeMesh ExtractSurface(double threshold = 0.5)
    {
        if (!(threshold > 0) || !(threshold < 1))
            throw new ArgumentOutOfRangeException(
                nameof(threshold), threshold,
                "A density threshold lies strictly between 0 and 1; 0 would keep the whole "
                + "mesh and 1 nothing at all.");
        return DensityIsoSurface.Extract(Mesh, NodalDensity, threshold);
    }

    /// <summary>
    /// The volume fraction the extracted solid at <paramref name="threshold"/> actually has,
    /// measured from the level set rather than from the element densities.
    /// <para>This is the number to compare against
    /// <see cref="TopologyOptions.VolumeFraction"/> before believing a threshold: the two
    /// agree only when the design is discrete, and the gap IS
    /// <see cref="Discreteness"/> showing up where it matters.</para>
    /// </summary>
    public double ExtractedVolumeFraction(double threshold = 0.5) =>
        MeshMassProperties.Compute(ExtractSurface(threshold)).Volume / TotalVolume;

    private double Fraction(double[] density)
    {
        double sum = 0, total = 0;
        for (int e = 0; e < density.Length; e++)
        {
            sum += _volumes[e] * density[e];
            total += _volumes[e];
        }
        return total > 0 ? sum / total : 0;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{Iterations} iterations ({Stop}), compliance {Compliance:G6}, "
        + $"volume fraction {VolumeFraction:F4}, discreteness {Discreteness:F3}";

    /// <summary>A per-iteration table — what a convergence plot is read off.</summary>
    public string ToText()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"topology optimisation: {this}");
        text.AppendLine(
            $"  {Mesh.ElementCount:N0} elements, filter radius {Options.FilterRadius:G4} "
            + $"({Options.Filter}), mean {MeanNeighbours} / max {MaxNeighbours} neighbours");
        text.AppendLine("  iter    compliance   volume    change");
        foreach (var row in History)
        {
            text.AppendLine(
                $"  {row.Number,4}  {row.Compliance,12:G6}  {row.VolumeFraction,7:F4}  "
                + $"{row.MaxChange,8:F4}" + (row.VolumeConstraintMet ? "" : "  (volume capped)"));
        }
        return text.ToString();
    }
}
