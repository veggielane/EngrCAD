using System.Text;
using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Fea;

/// <summary>
/// How far <see cref="TopologyResult.Release"/> takes the extracted iso-surface toward a
/// printable, re-meshable part. Three honestly-different outputs, each with its own measured
/// cost — the same spirit as <c>Shape.Remeshed</c> being a graph node with an honest
/// <c>Explain</c> rather than a hidden clean-up: a caller sees what each stage traded, not one
/// opaque "cleaned up" result.
/// </summary>
public enum TopologyReleaseStage
{
    /// <summary>The extracted iso-surface verbatim — a stair-stepped Surface-Nets-style mesh
    /// faceted at ELEMENT scale, because the density field is thresholded on tetrahedra. It is
    /// the exact level set and a poor part.</summary>
    IsoSurface,

    /// <summary>The iso-surface after Laplacian fairing (<see cref="LaplacianMeshSmoother"/>),
    /// which removes the element-scale stair-steps. <b>It MOVES the surface</b>, so it is no
    /// longer the exact iso-surface of the field — <see cref="ReleasedTopology.SmoothingVolumeDelta"/>
    /// and <see cref="ReleasedTopology.SmoothingMaxDisplacement"/> say by how much.</summary>
    Smoothed,

    /// <summary>The smoothed surface re-triangulated to a uniform edge length
    /// (<see cref="Remesher"/>). This is the stage that makes the part <i>usable</i>: a
    /// thresholded iso-surface has slivers and wildly varying edge lengths even after
    /// smoothing, and remeshing is what makes it printable and re-meshable for a confirmation
    /// solve. <see cref="ReleasedTopology.IsoSurfaceQuality"/> versus
    /// <see cref="ReleasedTopology.FinalQuality"/> is the improvement, as a number.</summary>
    Remeshed,
}

/// <summary>
/// What <see cref="TopologyResult.Release"/> is asked for. Defaults produce the full
/// smooth-then-remesh pipeline; drop <see cref="Stage"/> to <see cref="TopologyReleaseStage.Smoothed"/>
/// or <see cref="TopologyReleaseStage.IsoSurface"/> for the intermediate outputs.
/// </summary>
public sealed record TopologyReleaseOptions
{
    /// <summary>The density level the extracted surface bounds, in (0, 1). 0.5 is
    /// conventional; it is NOT volume-preserving unless the field is symmetric about it, which
    /// is why <see cref="ReleasedTopology"/> reports the actual volume beside the target.</summary>
    public double Threshold { get; init; } = 0.5;

    /// <summary>How far to take the surface. <see cref="TopologyReleaseStage.Remeshed"/> (the
    /// default) runs the whole pipeline.</summary>
    public TopologyReleaseStage Stage { get; init; } = TopologyReleaseStage.Remeshed;

    /// <summary>
    /// Keep only the LARGEST connected component of the extracted iso-surface, dropping any
    /// disconnected islands before smoothing and remeshing. False by default, deliberately:
    /// silently deleting material the optimiser put there is exactly the tidying that should be
    /// a stated act, so <see cref="ReleasedTopology.ComponentCount"/> REPORTS the islands by
    /// default and this OPTS IN to removing them.
    ///
    /// <para>"Largest" is by enclosed VOLUME (each component of a thresholded field is its own
    /// closed blob), so it drops small floating fragments and keeps the load-carrying body; a
    /// tie in volume breaks on face count. When the extraction found one component this does
    /// nothing (the deliverable is unchanged), and <see cref="ReleasedTopology.ComponentCount"/>
    /// still reports what WAS found so a caller sees how many were dropped.</para>
    /// </summary>
    public bool KeepLargestComponentOnly { get; init; }

    /// <summary>
    /// Implicit-fairing steps over the iso-surface. Three is a stated default that clears the
    /// element-scale stair-steps at a modest, reported cost in volume; 0 skips smoothing (the
    /// remesh then runs on the raw iso-surface, which is legal but keeps the faceting the
    /// smoothing exists to remove).
    /// </summary>
    public int SmoothingIterations { get; init; } = 3;

    /// <summary>
    /// The smoother's dimensionless <see cref="LaplacianSmoothOptions.TimeStep"/> — scale-free,
    /// so the same value fairs the same amount at any model scale.
    /// <para><b>Deliberately GENTLE (0.1), not the smoother's own "visible step" of 1.</b> An
    /// optimised structure is made of THIN members, and a stair-step is material on the convex
    /// side of the mid-surface, so fairing necessarily removes some — a full step at strength 1
    /// melts thin members (measured: −42% of the volume in three steps on the docs beam), where
    /// three steps at 0.1 fair the steps away for a reported ≈−6%. The volume it does spend is
    /// returned rather than hidden (<see cref="ReleasedTopology.SmoothingVolumeDelta"/>).</para>
    /// </summary>
    public double SmoothingStrength { get; init; } = 0.1;

    /// <summary>
    /// The uniform edge length the remesh drives toward, in model units. Null (the default)
    /// takes the MEDIAN edge of the raw iso-surface, so the released mesh keeps the extraction's
    /// own resolution while making it uniform.
    /// </summary>
    public double? RemeshTargetEdgeLength { get; init; }

    /// <summary>Remesh passes. Ten is the remesher's own default and is ample for a surface
    /// already close to the target length.</summary>
    public int RemeshIterations { get; init; } = 10;

    /// <summary>
    /// Dihedral angle above which the remesh PINS a crease. <b>0 by default</b>, deliberately:
    /// an optimised part is an organic shape with no CAD creases to protect, and a tessellated
    /// blob's facets meet at large dihedrals, so the remesher's usual 30° default would pin
    /// most of the mesh and leave the slivers this stage exists to remove (the recorded
    /// "feature detection reads the mesh you give it, not the surface you meant" lesson). Raise
    /// it only to keep a flat mounting face crisp, and expect fewer, worse triangles near it.
    /// </summary>
    public double RemeshFeatureAngleDegrees { get; init; }
}

/// <summary>
/// A released topology-optimisation result: the deliverable surface mesh plus a MEASURED
/// account of what each stage cost. Nothing here hides a movement — smoothing and remeshing
/// both move the surface, and both contributions are reported separately so a caller can see
/// exactly what was traded for a printable part.
///
/// <para><b>Turning it into a <c>Shape</c> is one call in the modelling layer</b>, deliberately
/// not a method here: <c>EngrCAD.Fea</c> is a leaf that references only Core and Mesh and
/// cannot see <c>Shape</c> or <c>Part</c>, which live in <c>EngrCAD.Modeling</c>. The seam is
/// the <see cref="HalfEdgeMesh"/> both layers share — <c>Shape.From(released.Mesh)</c> takes it
/// into the construction graph, and from there the <c>--export</c> path (STL / 3MF / OBJ) and
/// <c>EngrCad.Show</c> consume it unchanged.</para>
/// </summary>
public sealed class ReleasedTopology
{
    internal ReleasedTopology(
        TopologyReleaseOptions options,
        double totalVolume,
        HalfEdgeMesh isoSurface,
        HalfEdgeMesh smoothed,
        HalfEdgeMesh mesh,
        double isoVolume,
        double smoothedVolume,
        double finalVolume,
        double smoothingMaxDisplacement,
        double smoothingMeanDisplacement,
        double remeshingMaxDisplacement,
        double remeshingMeanDisplacement,
        double surfaceMaxDisplacementFromIso,
        double surfaceMeanDisplacementFromIso,
        TriangleQualityReport isoQuality,
        TriangleQualityReport smoothedQuality,
        TriangleQualityReport finalQuality,
        int componentCount,
        double remeshTargetEdgeLength,
        int splits,
        int collapses,
        int flips)
    {
        Options = options;
        TotalVolume = totalVolume;
        IsoSurface = isoSurface;
        Smoothed = smoothed;
        Mesh = mesh;
        IsoVolume = isoVolume;
        SmoothedVolume = smoothedVolume;
        FinalVolume = finalVolume;
        SmoothingMaxDisplacement = smoothingMaxDisplacement;
        SmoothingMeanDisplacement = smoothingMeanDisplacement;
        RemeshingMaxDisplacement = remeshingMaxDisplacement;
        RemeshingMeanDisplacement = remeshingMeanDisplacement;
        SurfaceMaxDisplacementFromIso = surfaceMaxDisplacementFromIso;
        SurfaceMeanDisplacementFromIso = surfaceMeanDisplacementFromIso;
        IsoSurfaceQuality = isoQuality;
        SmoothedQuality = smoothedQuality;
        FinalQuality = finalQuality;
        ComponentCount = componentCount;
        RemeshTargetEdgeLength = remeshTargetEdgeLength;
        RemeshSplits = splits;
        RemeshCollapses = collapses;
        RemeshFlips = flips;
    }

    /// <summary>The options the release ran with.</summary>
    public TopologyReleaseOptions Options { get; }

    /// <summary>The design space's total volume — the denominator every fraction here uses.</summary>
    public double TotalVolume { get; }

    /// <summary>The extracted iso-surface (stage 0) — verbatim, or its largest component when
    /// <see cref="TopologyReleaseOptions.KeepLargestComponentOnly"/> dropped the islands. Kept so
    /// a caller can render or export the level set the pipeline started from.</summary>
    public HalfEdgeMesh IsoSurface { get; }

    /// <summary>The surface after fairing; equal by reference to <see cref="IsoSurface"/> when
    /// no smoothing ran.</summary>
    public HalfEdgeMesh Smoothed { get; }

    /// <summary>The deliverable — the surface at the requested <see cref="TopologyReleaseStage"/>.
    /// This is what <c>Shape.From(...)</c> takes.</summary>
    public HalfEdgeMesh Mesh { get; }

    /// <summary>Whether the deliverable is a closed manifold. A thresholded iso-surface is closed
    /// by construction (the extraction caps it against the design-space boundary); this states
    /// it survived smoothing and remeshing.</summary>
    public bool IsClosed => Mesh.IsClosed;

    /// <summary>Volume of the extracted iso-surface.</summary>
    public double IsoVolume { get; }

    /// <summary>Volume after smoothing; equal to <see cref="IsoVolume"/> when no smoothing ran.</summary>
    public double SmoothedVolume { get; }

    /// <summary>Volume of the deliverable.</summary>
    public double FinalVolume { get; }

    /// <summary>The material fraction the deliverable holds — compare against the run's
    /// <c>VolumeFraction</c>, which the release has moved away from by exactly the two deltas
    /// below.</summary>
    public double VolumeFraction => TotalVolume > 0 ? FinalVolume / TotalVolume : 0;

    /// <summary>What smoothing cost in volume (negative = shrinkage, the usual sign of fairing a
    /// convex blob).</summary>
    public double SmoothingVolumeDelta => SmoothedVolume - IsoVolume;

    /// <summary>What remeshing cost in volume — near zero, because remeshing REDISTRIBUTES
    /// vertices onto the smoothed surface rather than moving it (a chorded facet cuts across a
    /// convex patch, so the sign is slightly negative).</summary>
    public double RemeshingVolumeDelta => FinalVolume - SmoothedVolume;

    /// <summary>How far the farthest vertex moved OFF the iso-surface during smoothing — the
    /// headline "how much did fairing change the shape" number.</summary>
    public double SmoothingMaxDisplacement { get; }

    /// <summary>Mean vertex displacement during smoothing.</summary>
    public double SmoothingMeanDisplacement { get; }

    /// <summary>How far the remeshed vertices sit from the SMOOTHED surface — a fidelity number
    /// (remeshing projects onto that surface, so it is small), not a shape change.</summary>
    public double RemeshingMaxDisplacement { get; }

    /// <summary>Mean remeshed-vertex distance from the smoothed surface.</summary>
    public double RemeshingMeanDisplacement { get; }

    /// <summary>The deliverable's largest stray from the EXACT iso-surface — the total the two
    /// stages together spent moving the part away from the level set of the optimised field.
    /// It is dominated by smoothing, since remeshing stays on the smoothed surface.</summary>
    public double SurfaceMaxDisplacementFromIso { get; }

    /// <summary>Mean deliverable-vertex distance from the iso-surface.</summary>
    public double SurfaceMeanDisplacementFromIso { get; }

    /// <summary>Triangle shape of the raw iso-surface — the "before" of the remesh, and it is
    /// bad (slivers, wide edge-length spread).</summary>
    public TriangleQualityReport IsoSurfaceQuality { get; }

    /// <summary>Triangle shape after smoothing (before remeshing). Smoothing improves the
    /// geometry but not the tessellation, so this is still poor.</summary>
    public TriangleQualityReport SmoothedQuality { get; }

    /// <summary>Triangle shape of the deliverable — the "after" of the remesh, uniform and
    /// sliver-free. Comparing <see cref="IsoSurfaceQuality"/> to this is the number that says
    /// what remeshing bought.</summary>
    public TriangleQualityReport FinalQuality { get; }

    /// <summary>Edge-connected components the EXTRACTION found (before any island removal).
    /// Extraction marches EVERY tetrahedron above the threshold, so a thresholded field with a
    /// disconnected island comes back as several components rather than the island being silently
    /// dropped — this is the count whether or not
    /// <see cref="TopologyReleaseOptions.KeepLargestComponentOnly"/> then kept only the largest,
    /// so a caller always sees how many islands there were.</summary>
    public int ComponentCount { get; }

    /// <summary>The uniform edge length the remesh drove toward (resolved from the median edge
    /// of the iso-surface when <see cref="TopologyReleaseOptions.RemeshTargetEdgeLength"/> was
    /// null).</summary>
    public double RemeshTargetEdgeLength { get; }

    /// <summary>Edges split during remeshing.</summary>
    public int RemeshSplits { get; }

    /// <summary>Edges collapsed during remeshing.</summary>
    public int RemeshCollapses { get; }

    /// <summary>Edges flipped during remeshing.</summary>
    public int RemeshFlips { get; }

    /// <summary>The honest per-stage account — volume, displacement and triangle shape at each
    /// step, so the reader sees three different outputs rather than one opaque result.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        double frac = TotalVolume > 0 ? 100 : 1;
        text.AppendLine($"released topology (stage {Options.Stage}, threshold {Options.Threshold:F3})");
        text.AppendLine($"  iso-surface   vol {IsoVolume:G6} ({IsoVolume / TotalVolume:P2}), "
            + $"{IsoSurface.FaceCount} faces, {ComponentCount} component(s), "
            + $"min angle {IsoSurfaceQuality.MinAngleDegrees:F1} deg, {IsoSurfaceQuality.SliverCount} slivers");
        if (Options.Stage >= TopologyReleaseStage.Smoothed)
            text.AppendLine($"  smoothed      vol {SmoothedVolume:G6} "
                + $"(delta {SmoothingVolumeDelta / IsoVolume:+0.0%;-0.0%;0.0%}), "
                + $"moved max {SmoothingMaxDisplacement:G4} / mean {SmoothingMeanDisplacement:G4}");
        if (Options.Stage >= TopologyReleaseStage.Remeshed)
        {
            text.AppendLine($"  remeshed      vol {FinalVolume:G6} "
                + $"(delta {RemeshingVolumeDelta / SmoothedVolume:+0.0%;-0.0%;0.0%}), "
                + $"edge target {RemeshTargetEdgeLength:G4}, "
                + $"{Mesh.FaceCount} faces (+{RemeshSplits}/-{RemeshCollapses}/~{RemeshFlips})");
            text.AppendLine($"                min angle {IsoSurfaceQuality.MinAngleDegrees:F1} -> "
                + $"{FinalQuality.MinAngleDegrees:F1} deg, slivers {IsoSurfaceQuality.SliverCount} -> "
                + $"{FinalQuality.SliverCount}, stays within {RemeshingMaxDisplacement:G4} of the faired surface");
        }
        text.Append($"  delivered     {Mesh.FaceCount} faces, closed {IsClosed}, "
            + $"vol {VolumeFraction:P2}, off the iso-surface by max {SurfaceMaxDisplacementFromIso:G4}");
        return text.ToString();
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"released {Options.Stage}: {Mesh.FaceCount} faces, volume {VolumeFraction:P2}, "
        + $"min angle {FinalQuality.MinAngleDegrees:F1} deg";
}

public sealed partial class TopologyResult
{
    /// <summary>
    /// Turns the density field into a usable, exportable surface mesh: extract the iso-surface,
    /// then optionally FAIR it (smoothing) and RE-TRIANGULATE it to a uniform edge length
    /// (remeshing). Every stage's cost is MEASURED and returned rather than hidden — see
    /// <see cref="ReleasedTopology"/>.
    ///
    /// <para><b>The order is smooth-then-remesh, and it is the right one</b>: fairing removes the
    /// element-scale stair-steps of the thresholded field, and remeshing then redistributes
    /// vertices ONTO that faired surface (its projection target is the smoothed mesh). Doing it
    /// the other way round would remesh the stair-steps and then have to fair the uniform result,
    /// which fights the resolution the remesh just established.</para>
    ///
    /// <para><b>Islands and thin necks.</b> Extraction keeps EVERY tetrahedron above the
    /// threshold, so a disconnected island survives as its own component rather than being
    /// silently deleted (<see cref="ReleasedTopology.ComponentCount"/> reports how many). A neck
    /// one element thick is a valid closed manifold and passes through; fairing can pinch a truly
    /// hair-thin one, which is a property of the threshold rather than of the release — lower the
    /// threshold or coarsen the filter radius if a member is meant to survive.</para>
    /// </summary>
    /// <param name="options">What to extract and how far to take it. Null runs the full
    /// smooth-then-remesh pipeline at threshold 0.5.</param>
    public ReleasedTopology Release(TopologyReleaseOptions? options = null)
    {
        options ??= new TopologyReleaseOptions();

        // Stage 0 — the exact iso-surface (a stair-stepped, uneven-triangle mesh).
        var iso = ExtractSurface(options.Threshold);
        var components = MeshConnectedComponents.Find(iso);
        // ComponentCount always reports what the EXTRACTION found, so a caller sees how many
        // islands there were whether or not they were dropped.
        int componentCount = components.Count;
        if (options.KeepLargestComponentOnly && components.Count > 1)
        {
            // Largest by enclosed volume (a thresholded component is a closed blob), tie-break
            // on face count. Everything below then measures the KEPT body.
            var largest = components
                .OrderByDescending(c => Math.Abs(c.SignedVolume))
                .ThenByDescending(c => c.FaceCount)
                .First();
            iso = largest.ToMesh();
        }
        double isoVolume = MeshMassProperties.Compute(iso).Volume;
        var isoQuality = TriangleQuality.Analyze(iso);
        var isoTarget = new MeshProjectionTarget(iso);

        // Stage 1 — fairing. It MOVES the surface off the iso-surface; the displacement of each
        // vertex from the iso-surface is exactly how far it moved.
        var smoothed = iso;
        double smoothingMax = 0, smoothingMean = 0;
        var smoothedQuality = isoQuality;
        if (options.Stage >= TopologyReleaseStage.Smoothed && options.SmoothingIterations > 0)
        {
            smoothed = LaplacianMeshSmoother.Smooth(iso, new LaplacianSmoothOptions
            {
                TimeStep = options.SmoothingStrength,
                Iterations = options.SmoothingIterations,
            });
            (smoothingMax, smoothingMean) = DisplacementTo(smoothed, isoTarget);
            smoothedQuality = TriangleQuality.Analyze(smoothed);
        }
        double smoothedVolume = ReferenceEquals(smoothed, iso)
            ? isoVolume
            : MeshMassProperties.Compute(smoothed).Volume;

        // Stage 2 — remeshing. It redistributes vertices onto the SMOOTHED surface, so it
        // barely moves the surface (RemeshingMaxDisplacement measures that it stays on it) and
        // its whole benefit is triangle SHAPE, which FinalQuality carries.
        var final = smoothed;
        var finalQuality = smoothedQuality;
        double remeshingMax = 0, remeshingMean = 0;
        int splits = 0, collapses = 0, flips = 0;
        double target = options.RemeshTargetEdgeLength ?? MedianEdgeLength(iso);
        if (options.Stage >= TopologyReleaseStage.Remeshed)
        {
            var smoothedTarget = new MeshProjectionTarget(smoothed);
            var remesh = Remesher.Remesh(smoothed, new RemeshOptions(target)
            {
                Iterations = options.RemeshIterations,
                ProjectionTarget = smoothedTarget,
                FeatureAngleDegrees = options.RemeshFeatureAngleDegrees,
            });
            final = remesh.Mesh;
            finalQuality = remesh.Quality;
            (remeshingMax, remeshingMean) = DisplacementTo(final, smoothedTarget);
            splits = remesh.Splits;
            collapses = remesh.Collapses;
            flips = remesh.Flips;
        }
        double finalVolume = ReferenceEquals(final, smoothed)
            ? smoothedVolume
            : MeshMassProperties.Compute(final).Volume;

        var (isoMax, isoMean) = DisplacementTo(final, isoTarget);

        return new ReleasedTopology(
            options, TotalVolume, iso, smoothed, final,
            isoVolume, smoothedVolume, finalVolume,
            smoothingMax, smoothingMean, remeshingMax, remeshingMean, isoMax, isoMean,
            isoQuality, smoothedQuality, finalQuality, componentCount, target,
            splits, collapses, flips);
    }

    /// <summary>Max and mean distance of <paramref name="moved"/>'s vertices from
    /// <paramref name="target"/> — how far a stage pushed the surface off the reference it was
    /// moved from.</summary>
    private static (double Max, double Mean) DisplacementTo(HalfEdgeMesh moved, IProjectionTarget target)
    {
        int n = moved.VertexCount;
        if (n == 0)
            return (0, 0);
        double max = 0, sum = 0;
        for (int v = 0; v < n; v++)
        {
            var p = moved.GetPosition(v);
            double d = (target.Project(p) - p).Length;
            max = Math.Max(max, d);
            sum += d;
        }
        return (max, sum / n);
    }

    /// <summary>The median undirected-edge length of a triangle mesh — the "typical" facet size,
    /// median rather than mean because a marching-tetrahedra surface has a long tail of tiny
    /// edges (crossings near a node) that would drag a mean below the resolution.</summary>
    private static double MedianEdgeLength(HalfEdgeMesh mesh)
    {
        var lengths = new List<double>(mesh.HalfEdgeCount / 2 + 1);
        for (int he = 0; he < mesh.HalfEdgeCount; he++)
        {
            var h = mesh.GetHalfEdge(he);
            if (h.Twin.Index < he)
                continue; // canonical representative of each undirected edge
            lengths.Add((mesh.GetPosition(h.Destination.Index) - mesh.GetPosition(h.Origin.Index)).Length);
        }
        if (lengths.Count == 0)
            return 1.0;
        lengths.Sort();
        return lengths[lengths.Count / 2];
    }
}
