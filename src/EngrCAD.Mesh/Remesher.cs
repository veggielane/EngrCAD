using System.Runtime.InteropServices;
using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>Vertex smoothing rule used by <see cref="Remesher"/>'s smoothing pass.</summary>
public enum RemeshSmoothing
{
    /// <summary>No smoothing (topological operations only).</summary>
    None,

    /// <summary>
    /// Uniform (umbrella) Laplacian: move toward the average of the one-ring neighbours.
    /// Cheap, unconditionally stable, and the right default — it equalizes edge lengths,
    /// which is exactly what isotropic remeshing wants.
    /// </summary>
    Uniform,

    /// <summary>
    /// Cotangent-weighted Laplacian: moves toward the discrete mean-curvature centroid, so
    /// it distorts the surface less than <see cref="Uniform"/> but does not equalize edge
    /// lengths as aggressively. Falls back to leaving the vertex alone where the weights are
    /// degenerate (a sliver triangle makes a cotangent blow up).
    /// </summary>
    Cotangent,
}

/// <summary>How <see cref="Remesher"/>'s projection pass puts vertices back on the target.</summary>
public enum RemeshProjection
{
    /// <summary>
    /// Move each free vertex to its own closest point on the target. Simple and the default —
    /// and it rounds sharp features off, because the closest point to a vertex sitting near a
    /// crease is on whichever side happens to be nearer.
    /// </summary>
    Vertex,

    /// <summary>
    /// Face-aligned (RZN-flow) reprojection — geometry3Sharp's
    /// <c>RemesherPro.SharpEdgeReprojectionRemesh</c>. Each TRIANGLE is moved rigidly: its
    /// centroid goes to the centroid's closest point on the target and its plane is rotated to
    /// the target's normal there, and every vertex then takes the weighted average of where
    /// its incident triangles would put it, weighted by <c>area × (n·n')³</c>.
    /// <para>
    /// The cube of the normal agreement is what preserves creases. A triangle lying flat
    /// against the target contributes with full weight, a triangle straddling a crease agrees
    /// with neither side and contributes almost nothing, so a vertex ON a crease is pulled by
    /// the two flat groups either side and lands where their planes meet — the crease — rather
    /// than being dragged onto the nearer face. Vertex projection has no way to know that.
    /// </para>
    /// <para>
    /// Needs an <b>oriented</b> target (<see cref="IProjectionTarget.Project(in Vector3d, out Vector3d)"/>
    /// reporting a non-zero normal). Triangles whose projection comes back unoriented fall back
    /// to plain closest-point projection, so an unoriented target degrades to
    /// <see cref="Vertex"/> rather than failing.
    /// </para>
    /// <para>
    /// It <b>composes with <see cref="RemeshScheduling.Queue"/></b>: the accumulation walks
    /// only the faces incident to the active set, which is exactly the set that can
    /// contribute to a vertex the pass writes, visited in ascending face index so every sum
    /// lands in the same order and the restriction is bit-identical rather than merely
    /// equivalent.
    /// </para>
    /// </summary>
    FaceAligned,
}

/// <summary>How <see cref="Remesher"/> decides which edges and vertices a pass looks at.</summary>
public enum RemeshScheduling
{
    /// <summary>
    /// Every pass sweeps every edge, then smooths and projects every free vertex. Simple,
    /// predictable, and the default — the cost of a pass is the size of the mesh whether or
    /// not anything is left to do.
    /// </summary>
    Sweep,

    /// <summary>
    /// geometry3Sharp's <c>RemesherPro</c> scheduling: a pass processes only the edges and
    /// vertices that the previous pass disturbed. The first pass seeds the queues with the
    /// whole mesh; afterwards, a successful operation re-queues the one-ring it changed, and
    /// a vertex that moved appreciably re-queues its own edges. Regions that have reached the
    /// target length therefore go quiet and cost nothing.
    /// <para>
    /// <b>Output is not bit-identical to <see cref="Sweep"/>, and the difference is not only
    /// a visit order.</b> A quiet region stops being smoothed, so a converged mesh is
    /// slightly less relaxed than the sweep's — which is the point (the sweep keeps paying
    /// for smoothing that moves nothing) but it is a different answer, not the same one
    /// faster. Both remain fully deterministic: queues are FIFO, seeded in the sweep's own
    /// stride order, and no random number generator is involved.
    /// </para>
    /// </summary>
    Queue,
}

/// <summary>
/// Settings for <see cref="Remesher.Remesh"/>.
/// </summary>
/// <param name="TargetEdgeLength">
/// The edge length to converge on. Everything else in the algorithm is measured relative to
/// it, so the remesher is scale-free: there is no absolute tolerance anywhere in it.
/// </param>
public sealed record RemeshOptions(double TargetEdgeLength)
{
    /// <summary>
    /// Split threshold factor: edges longer than <c>1.33 × target</c> are split.
    /// <para>
    /// Botsch's classic factors are 4/5 and 4/3, and they thrash: an edge just over
    /// <c>4/3·L</c> splits into two halves of ≈<c>0.667·L</c>, which is <b>below</b> the
    /// <c>0.8·L</c> collapse threshold — so every split immediately produces two collapse
    /// candidates. With 0.66/1.33 (geometry3Sharp's correction, and the reason its comment
    /// says "much nicer!!"), a split can never produce an immediately collapsible edge.
    /// </para>
    /// </summary>
    public const double MaxLengthFactor = 1.33;

    /// <summary>Collapse threshold factor: edges shorter than <c>0.66 × target</c> are collapsed. See <see cref="MaxLengthFactor"/>.</summary>
    public const double MinLengthFactor = 0.66;

    /// <summary>Number of remesh passes. Each pass is one topological sweep + one smoothing pass + one projection pass.</summary>
    public int Iterations { get; init; } = 10;

    /// <summary>
    /// Which edges and vertices each pass visits. <see cref="RemeshScheduling.Sweep"/> (the
    /// default) visits everything every pass; <see cref="RemeshScheduling.Queue"/> visits only
    /// what the previous pass disturbed and produces a different — not merely faster — result.
    /// </summary>
    public RemeshScheduling Scheduling { get; init; } = RemeshScheduling.Sweep;

    /// <summary>
    /// Split-only sweeps to run before the first full pass. Each one splits every edge longer
    /// than <see cref="MaxEdgeLength"/> and does nothing else — no collapses, no flips, no
    /// smoothing, no projection (g3's <c>RemesherPro.FastSplitIteration</c>).
    /// <para>
    /// This is pure throughput for a mesh much coarser than the target: bringing an edge that
    /// is 8× too long down to length needs three rounds of splitting whatever else happens,
    /// and doing them here costs three edge sweeps instead of three full passes, each of which
    /// would otherwise smooth and project a mesh that is still nowhere near its final vertex
    /// count. Halving stops as soon as the edges are in band, so an over-generous count costs
    /// only the sweeps that find nothing.
    /// </para>
    /// </summary>
    public int FastSplitPasses { get; init; }

    /// <summary>Split edges longer than <see cref="MaxEdgeLength"/>.</summary>
    public bool EnableSplits { get; init; } = true;

    /// <summary>Collapse edges shorter than <see cref="MinEdgeLength"/>.</summary>
    public bool EnableCollapses { get; init; } = true;

    /// <summary>Flip interior edges when that brings the four incident valences closer to 6.</summary>
    public bool EnableFlips { get; init; } = true;

    /// <summary>
    /// Refuse a valence flip that would leave an edge longer than
    /// <see cref="MaxEdgeLength"/> — the <b>bounded-maximum</b> rule. <b>On by default.</b>
    /// <para>
    /// Without it a remesh converges its edge-length <i>distribution</i> fast and its
    /// <i>maximum</i> hardly at all: on a Ø20 × 20 cylinder at a 2 mm target, 95% of edges
    /// reach the <c>[0.66 L, 1.33 L]</c> band within 14 passes while the longest sits near
    /// 2 L however many passes are spent. <b>The cause is the flip stage, and only the flip
    /// stage.</b> It is not a collapse leaving a long edge for the next pass to find, and it
    /// is not smoothing or projection — with flips switched off the same run ends at exactly
    /// 1.33 L with nothing out of band, because a sweep already splits everything too long.
    /// The flip predicate is pure valence arithmetic that never looks at a length, so on an
    /// elongated quad it swaps the short diagonal for the long one and manufactures exactly
    /// the edge the split stage exists to remove.
    /// </para>
    /// <para>
    /// <b>The rule is monotone, not absolute, and that distinction is the whole of it.</b>
    /// A flip is refused only when the edge it would create is out of band <i>and no shorter
    /// than the one it replaces</i>. Refusing every out-of-band flip instead — the obvious
    /// form — strands the sliver whose only remedy was a flip from 2.5 L to 1.5 L, measured
    /// as a worst triangle angle of <b>0.02°</b> on a remeshed box against 31.7° for the
    /// monotone form. What the monotone form buys is an invariant: a flip can no longer raise
    /// the longest edge, so the maximum falls monotonically toward
    /// <see cref="MaxEdgeLength"/> under the sweep instead of being churned upward every
    /// pass. It is a bound approached over passes rather than a cap at pass one (a 4 L edge
    /// still needs two passes to halve twice), and the smoothing and projection stages run
    /// after the sweep and move vertices by their own damped step.
    /// </para>
    /// <para>
    /// <b>It shipped opt-in and became the default on measurement, and the numbers are the
    /// argument.</b> Over a box, a cylinder, two UV spheres and an open height-field grid at
    /// 10 / 14 / 40 passes, <i>every</i> measure improves — maximum 1.37–2.65 → 1.27–1.67 L,
    /// shortest 0.03–0.26 → 0.10–0.67 L, out of band 2.5–11.8% → 0.0–0.9%, worst free
    /// triangle angle 0.24–17.1° → 22.8–36.3°, worst radius ratio 0.0001–0.30 → 0.38–0.81,
    /// slivers 4–226 → 0–7 — and the run is <i>faster</i> every time (a box at 10 passes,
    /// 79 ms → 22 ms), because a mesh full of slivers is a mesh full of work. The one filed
    /// counter-example, a cylinder whose worst triangle angle was said to go 0.89° → 0.58°,
    /// <b>does not reproduce</b>: swept over 32 / 48 / 64 segments and nine pass counts it
    /// reads 0.20 → 29.19, 0.03 → 0.11 and 0.24 → 2.81, better in all 27 rows.
    /// </para>
    /// <para>
    /// <b>One measure does trade on a heavily pinned model, and it is an extremum rather than
    /// a population.</b> A 60 × 40 × 8 plate with three drilled bores — whose rim chains are
    /// pinned creases far finer than the target, so they cannot be coarsened — keeps a single
    /// 0.01° free triangle against a bore at 10 / 14 / 20 / 30 passes where the unguarded run
    /// reads 0.09 / 1.40 / 1.66 / 1.98°, all of them slivers either way. Every population
    /// figure on the same fixture goes the other way and by a wide margin: slivers
    /// 588 / 419 / 349 / 286 → 184 / 157 / 156 / 152, out of band 17.4–10.6% → 8.4–7.6%,
    /// longest edge 2.51–2.14 → 1.83–1.49 L. That is the project's own "judge a remesh by the
    /// share in band, never by its extremes" lesson reappearing for SHAPE, which is why
    /// <see cref="TriangleQualityReport.SliverCount"/> is the figure to compare and
    /// <see cref="TriangleQualityReport.MinAngleDegrees"/> is the one to locate a defect with.
    /// </para>
    /// <para>
    /// The deciding argument is downstream, though, and it arrived after the option did:
    /// <c>TetMesher</c>'s boundary recovery needs a Delaunay-clean surface, and this is
    /// exactly the difference between every refused remeshed-sphere row and every
    /// zero-recovery-round one — a valence flip can replace a Delaunay diagonal with a longer
    /// one, and refusing that is what stops it. A default that produces surfaces the
    /// project's own tet mesher refuses is the wrong default.
    /// </para>
    /// <para>
    /// Set it false to get the pre-existing behaviour back bit for bit; the only reason to is
    /// to reproduce an older result.
    /// </para>
    /// </summary>
    public bool PreventLongEdgeFlips { get; init; } = true;

    /// <summary>Which smoothing rule the per-pass smoothing uses.</summary>
    public RemeshSmoothing Smoothing { get; init; } = RemeshSmoothing.Uniform;

    /// <summary>
    /// Smoothing damping in [0, 1]: <c>v ← (1 − t)·v + t·centroid</c> per pass. Small values
    /// converge slowly but never overshoot; 0 disables smoothing as surely as
    /// <see cref="RemeshSmoothing.None"/>.
    /// </summary>
    public double SmoothSpeed { get; init; } = 0.1;

    /// <summary>
    /// Pin every boundary vertex (open meshes keep their outline exactly). Boundary
    /// <i>edges</i> may still be split — a split lands on the boundary polyline itself, so
    /// the outline's geometry is unchanged.
    /// </summary>
    public bool PreserveBoundary { get; init; } = true;

    /// <summary>
    /// Pin the vertices of edges whose dihedral deviates from flat by more than this angle,
    /// in degrees — creases and corners of CAD geometry, which a plain isotropic remesh
    /// would round off. 0 disables feature detection. Recomputed from the current geometry
    /// at the start of every pass, so it needs no index bookkeeping (see the class remarks).
    /// </summary>
    public double FeatureAngleDegrees { get; init; } = 30;

    /// <summary>
    /// Whether an edge with two pinned ends may still be split. True (the default) lets a
    /// constrained chain gain resolution without moving — the midpoint lies on the chain
    /// itself. Set false when the pinned ring must come out vertex for vertex, which is what
    /// a patch that has to be stitched back into a surrounding mesh needs: an extra ring
    /// vertex there is a T-junction.
    /// </summary>
    public bool SplitFixedEdges { get; init; } = true;

    /// <summary>
    /// Extra vertices to pin, by index into the <b>input</b> mesh. Indices survive the
    /// internal triangulation (it never renumbers vertices) and pinned vertices are never
    /// removed, so these stay valid for the whole remesh.
    /// </summary>
    public IReadOnlyCollection<int>? FixedVertices { get; init; }

    /// <summary>
    /// Surface to pull free vertices back onto after each pass. Without one, smoothing
    /// shrinks the model (Laplacian flow is curvature flow); with one, the remesh changes
    /// the tessellation and leaves the shape. <see cref="MeshProjectionTarget"/> projects
    /// onto the original mesh; EngrCAD.Interop's <c>SdfProjectionTarget</c> projects onto any
    /// signed distance field, which is the quality-control pass for Surface Nets output.
    /// </summary>
    public IProjectionTarget? ProjectionTarget { get; init; }

    /// <summary>
    /// How the projection pass uses <see cref="ProjectionTarget"/>: per vertex (the default)
    /// or face-aligned, which preserves sharp features. Ignored when there is no target.
    /// </summary>
    public RemeshProjection Projection { get; init; } = RemeshProjection.Vertex;

    /// <summary>
    /// Refuse any operation or vertex move that would invert or degenerate an incident
    /// triangle. Costs one one-ring walk per candidate; leave it on unless profiling says
    /// otherwise.
    /// </summary>
    public bool PreventNormalFlips { get; init; } = true;

    /// <summary>
    /// Test seam: forces <see cref="RemeshProjection.FaceAligned"/> to accumulate over every
    /// face even under <see cref="RemeshScheduling.Queue"/>, so the restricted path can be
    /// held to bit-for-bit equality against the unrestricted one. A per-instance option
    /// rather than a mutable static, so it cannot leak between parallel tests.
    /// </summary>
    internal bool AccumulateOverEveryFace { get; init; }

    /// <summary>Edges longer than this are split. <see cref="MaxLengthFactor"/> × <see cref="TargetEdgeLength"/>.</summary>
    public double MaxEdgeLength => TargetEdgeLength * MaxLengthFactor;

    /// <summary>Edges shorter than this are collapsed. <see cref="MinLengthFactor"/> × <see cref="TargetEdgeLength"/>.</summary>
    public double MinEdgeLength => TargetEdgeLength * MinLengthFactor;
}

/// <summary>Outcome of <see cref="Remesher.Remesh"/>: the new mesh plus what it took to get there.</summary>
/// <param name="Mesh">The remeshed mesh (built through the manifold-validating <c>Build</c>).</param>
/// <param name="Splits">Edges split across all passes.</param>
/// <param name="Collapses">Edges collapsed across all passes.</param>
/// <param name="Flips">Edges flipped across all passes.</param>
/// <param name="Iterations">Passes actually run.</param>
public sealed record RemeshResult(HalfEdgeMesh Mesh, int Splits, int Collapses, int Flips, int Iterations)
{
    /// <summary>
    /// Faces the face-aligned projection actually accumulated, summed over every pass — the
    /// diagnostic that makes the restricted accumulation's skipping OBSERVABLE. Without it a
    /// bit-identity test between the restricted and unrestricted paths passes vacuously
    /// whenever the active set happens to be the whole mesh, which is exactly what it did on
    /// the first fixture tried. Zero for any other projection mode.
    /// </summary>
    internal int FacesAccumulated { get; init; }

    /// <summary>
    /// The output mesh's triangle SHAPE, measured with the remesher's own final pinned set as
    /// the constrained population.
    /// <para>
    /// Everything else on this record and every length figure the remesher reports is about
    /// edge <i>length</i>, which says nothing about slivers: a remeshed box can sit at 95% of
    /// edges inside the <c>[0.66 L, 1.33 L]</c> band with a worst triangle angle of 5.6°.
    /// This is the measure that sees it, and it is computed once at the end — one pass over
    /// the faces, against however many passes the remesh itself ran.
    /// </para>
    /// <para>
    /// The pinned set is why it lives here rather than being left to the caller: only the
    /// remesher knows which vertices its own constraints froze, and a triangle it was never
    /// free to change should not be rated against one it was
    /// (<see cref="TriangleQualityReport.ConstrainedCount"/>).
    /// </para>
    /// </summary>
    public TriangleQualityReport Quality { get; init; } = null!;
}

/// <summary>
/// Isotropic remeshing: drives a triangle mesh toward a uniform target edge length while
/// keeping its shape — geometry3Sharp's <c>Remesher</c>/<c>RemesherPro</c> algorithm
/// (Botsch &amp; Kobbelt's split/collapse/flip/smooth loop) rebuilt on
/// <see cref="EditableMesh"/>'s guarded Euler operators, which is precisely what those
/// operators exist for.
/// <para>
/// One pass is: a single sweep over the edges trying <b>collapse → flip → split</b> per edge
/// (at most one succeeds, first wins), then a full smoothing pass, then a full projection
/// pass. The sweep visits edges on a modulo-prime stride rather than in index order — with
/// sequential ids on a tessellated cylinder, every tiny edge collapses into its neighbour in
/// turn and the whole mesh erodes away; jumping around breaks that symmetry. The stride is a
/// fixed constant, so the algorithm is <b>fully deterministic</b>: no random number generator
/// is involved anywhere.
/// </para>
/// <para>
/// <b>Constraints are expressed on vertices, not edges</b>, which is a deliberate departure
/// from g3. Our undirected edges are identified by the smaller of a twin pair of half-edge
/// indices, and a collapse <i>merges</i> edge pairs — the surviving edge generally gets a
/// different canonical index, and freed indices are recycled — so an edge-keyed constraint
/// table goes stale (worse: silently aliases a different edge) after the first collapse.
/// Vertex indices have no such problem: a pinned vertex is never removed, because a collapse
/// always removes the <i>unpinned</i> end. Everything g3 expresses with edge flags follows
/// from that: an edge with two pinned ends can be neither collapsed (both ends fixed) nor
/// flipped (a flip destroys the edge), which is exactly <c>NoCollapse | NoFlip</c>; splitting
/// stays legal and the new vertex inherits the pin, so a constrained chain keeps its
/// geometry while gaining resolution.
/// </para>
/// <para>
/// Every threshold here is relative to <see cref="RemeshOptions.TargetEdgeLength"/> and every
/// degeneracy guard is relative to it squared — areas scale quadratically, so an absolute
/// area epsilon fails quadratically with model scale (the lesson the BSP path learned twice).
/// </para>
/// </summary>
public static class Remesher
{
    /// <summary>
    /// Edge-sweep stride. Any prime does; this is g3's. The sweep visits
    /// <c>(k · stride) mod capacity</c> for k = 0, 1, 2, …, which covers every slot exactly
    /// once as long as the stride and the capacity are coprime — guaranteed here because the
    /// stride is prime, <i>unless</i> the capacity happens to be a multiple of it, which
    /// <see cref="StrideFor"/> falls back from. (g3 has this latent hole; we close it.)
    /// </summary>
    private const int SweepStride = 31337;

    /// <summary>
    /// Scale-free degeneracy floor for the triangle-quality guards, as a fraction of the
    /// <b>squared</b> target edge length. Deliberately NOT a <c>Tolerance</c> tier: the
    /// quantity being guarded is a cross product — an <b>area</b> — so the floor must scale
    /// with length squared, or it rejects everything at small scale and nothing at large
    /// scale. A triangle at the target length has area ≈ 0.43·L², so this rejects triangles
    /// roughly a million times smaller than the ones being aimed for.
    /// </summary>
    private const double RelativeDegeneracy = 1e-6;

    /// <summary>
    /// How far a vertex must move, as a fraction of the target edge length, for
    /// <see cref="RemeshScheduling.Queue"/> to consider its neighbourhood still active. This
    /// is a <b>scheduling heuristic</b> — it decides what to look at next — but it is still
    /// relative to the target length rather than absolute, for the same reason everything else
    /// here is.
    /// <para>
    /// The value was measured, and the obvious first guess is wrong in the way that makes the
    /// whole feature pointless. At 1e-3 <i>nothing ever goes quiet</i>: damped smoothing at
    /// speed 0.1 still moves a converged vertex by around 5e-3 L per pass, so every vertex
    /// re-woke its own ring every pass, the active set stayed the entire mesh, and queue
    /// scheduling came out <b>slower</b> than the sweep it exists to beat (measured 783 ms
    /// against 747 ms) — it did all of the sweep's work plus a ring walk per vertex to
    /// re-queue it. At 1e-2 a settled region actually settles and the same case runs 532 ms.
    /// </para>
    /// </summary>
    private const double Quiescence = 1e-2;

    /// <summary>
    /// Remeshes toward <see cref="RemeshOptions.TargetEdgeLength"/>. The input is
    /// triangulated if needed and is never modified; the result comes back through the
    /// manifold-validating <see cref="HalfEdgeMesh.Build"/>, so it is genuinely manifold.
    /// </summary>
    /// <param name="mesh">Mesh to remesh (polygons are fan-triangulated first).</param>
    /// <param name="options">Target length and behaviour.</param>
    /// <param name="progress">Optional cooperative progress/cancellation, polled per pass.</param>
    public static RemeshResult Remesh(HalfEdgeMesh mesh, RemeshOptions options, ProgressCancel? progress = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(options);
        if (!(options.TargetEdgeLength > 0))
            throw new ArgumentOutOfRangeException(nameof(options), "TargetEdgeLength must be positive.");
        if (options.Iterations < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Iterations cannot be negative.");
        if (options.SmoothSpeed is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(options), "SmoothSpeed must be in [0, 1].");
        if (options.FeatureAngleDegrees is < 0 or > 180)
            throw new ArgumentOutOfRangeException(nameof(options), "FeatureAngleDegrees must be in [0, 180].");
        if (options.FastSplitPasses < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "FastSplitPasses cannot be negative.");

        var triangles = mesh.Faces.Any(f => f.Degree != 3) ? mesh.Triangulated() : mesh;
        var editable = EditableMesh.FromMesh(triangles);
        var state = new RemeshState(editable, options);

        for (int i = 0; i < options.FastSplitPasses; i++)
        {
            progress?.ThrowIfCancelled();
            if (!state.FastSplitPass())
                break; // nothing left too long: the remaining prepasses would find nothing
        }

        for (int i = 0; i < options.Iterations; i++)
        {
            progress?.ThrowIfCancelled();
            state.Pass();
            progress?.Report((double)(i + 1) / options.Iterations);
        }
        progress?.ThrowIfCancelled();

        // The pins are re-derived from the FINAL geometry (feature edges are recomputed every
        // pass, so the set the last pass used may not be the set the output deserves) and
        // carried across the compaction by ToMesh's own map rather than by a second walk.
        var result = editable.ToMesh(out var vertexMap);
        return new RemeshResult(result, state.Splits, state.Collapses, state.Flips, options.Iterations)
        {
            FacesAccumulated = state.FacesAccumulated,
            Quality = TriangleQuality.Analyze(result, state.FinalConstrainedVertices(vertexMap)),
        };
    }

    // The stride must be coprime with the half-edge capacity or the sweep would revisit a
    // sub-cycle and miss most of the mesh. 31337 is prime, so the only failure is a capacity
    // that is a multiple of it; then a plain sequential sweep is correct (just symmetric).
    private static int StrideFor(int capacity) => capacity % SweepStride != 0 ? SweepStride : 1;

    private sealed class RemeshState
    {
        private readonly EditableMesh _mesh;
        private readonly RemeshOptions _options;
        private readonly double _minLengthSquared;
        private readonly double _maxLengthSquared;
        private readonly double _featureNormalDot;
        private readonly double _degenerateAreaSquared;
        private readonly HashSet<int> _pinned;

        // Per-pass scratch. Instance fields (not per-call arrays) so the inner loops allocate
        // nothing; they grow with the mesh and are reused across passes.
        private bool[] _fixed = [];
        private Vector3d[] _buffer = [];
        private bool[] _modified = [];
        private int[] _ring = new int[16];
        private readonly List<int> _moved = [];
        private readonly List<Vector3d> _movedTo = [];
        private Vector3d[] _accumulated = [];      // face-aligned projection
        private double[] _accumulatedWeight = [];

        // Queue scheduling. Two generations of each queue: work found during a pass goes into
        // the NEXT one, which mirrors the sweep's own rule that elements created mid-pass are
        // left for the pass after — and, more importantly, makes a pass terminate by
        // construction rather than by a cap on how much it may cascade.
        private Queue<int> _edges = new();
        private Queue<int> _nextEdges = new();
        private bool[] _edgeQueued = [];
        private List<int> _vertices = [];
        private List<int> _nextVertices = [];
        private bool[] _vertexQueued = []; // membership of _nextVertices
        private bool[] _vertexActive = []; // membership of _vertices
        private double _quiescenceSquared;
        private bool _seeded;

        public RemeshState(EditableMesh mesh, RemeshOptions options)
        {
            _mesh = mesh;
            _options = options;
            _minLengthSquared = options.MinEdgeLength * options.MinEdgeLength;
            _maxLengthSquared = options.MaxEdgeLength * options.MaxEdgeLength;
            // Sharp iff the angle between the adjacent face normals exceeds the feature
            // angle, i.e. their dot falls below its cosine. 0 degrees would make every edge
            // a feature, so it is spelled as "disabled" instead.
            _featureNormalDot = options.FeatureAngleDegrees > 0
                ? Math.Cos(options.FeatureAngleDegrees * Math.PI / 180.0)
                : double.NegativeInfinity;
            double floor = RelativeDegeneracy * options.TargetEdgeLength * options.TargetEdgeLength;
            _degenerateAreaSquared = floor * floor;
            _pinned = options.FixedVertices is null ? [] : [.. options.FixedVertices];
            double quiescence = Quiescence * options.TargetEdgeLength;
            _quiescenceSquared = quiescence * quiescence;
        }

        public int Splits { get; private set; }
        public int Collapses { get; private set; }
        public int Flips { get; private set; }
        public int FacesAccumulated { get; private set; }

        public void Pass()
        {
            RecomputeFixedVertices();
            if (_options.Scheduling == RemeshScheduling.Queue)
            {
                EnsureQueueCapacity();
                if (!_seeded)
                {
                    SeedQueues();
                    _seeded = true;
                }
                DrainEdgeQueue();
                MergePending();
                SmoothPass(_vertices);
                ProjectPass(_vertices);
                RollQueues();
                return;
            }

            SweepEdges();
            // The smoothing and projection passes work from a vertex list either way; in
            // sweep scheduling it is simply refilled with every live vertex, in ascending
            // index order, which is exactly the order the old whole-capacity loops used. One
            // code path, and no per-vertex interface dispatch in the inner loops.
            RefreshAllVertices();
            SmoothPass(_vertices);
            ProjectPass(_vertices);
        }

        /// <summary>
        /// The pinned vertices, re-derived from the final geometry and mapped into the output
        /// mesh's indices — the constrained population <see cref="RemeshResult.Quality"/>
        /// measures separately. Re-deriving rather than reading the last pass's array is the
        /// same rule the pass itself follows: constraints are stateless and come from the
        /// dihedral angles the mesh has NOW, and the last pass's topological sweep moved
        /// geometry after computing them.
        /// </summary>
        public IReadOnlyCollection<int> FinalConstrainedVertices(int[] vertexMap)
        {
            RecomputeFixedVertices();
            var pinned = new List<int>();
            for (int v = 0; v < _mesh.VertexCapacity && v < _fixed.Length; v++)
            {
                if (_fixed[v] && _mesh.IsVertex(v) && vertexMap[v] >= 0)
                    pinned.Add(vertexMap[v]);
            }
            return pinned;
        }

        private void RefreshAllVertices()
        {
            _vertices.Clear();
            for (int v = 0; v < _mesh.VertexCapacity; v++)
            {
                if (_mesh.IsVertex(v))
                    _vertices.Add(v);
            }
        }

        /// <summary>
        /// A split-only sweep: halve every edge that is too long, touching nothing else.
        /// Returns whether anything split, so the caller can stop early.
        /// </summary>
        public bool FastSplitPass()
        {
            RecomputeFixedVertices();
            int capacity = _mesh.HalfEdgeCapacity;
            if (capacity == 0)
                return false;
            int stride = StrideFor(capacity);
            int he = 0;
            int before = Splits;
            while (true)
            {
                if (_mesh.IsHalfEdge(he))
                {
                    int twin = _mesh.Twin(he);
                    if (he < twin)
                    {
                        int a = _mesh.Origin(he);
                        int b = _mesh.Origin(twin);
                        if ((_mesh.GetPosition(b) - _mesh.GetPosition(a)).LengthSquared > _maxLengthSquared &&
                            TrySplit(he, a, b))
                            Splits++;
                    }
                }
                he = (he + stride) % capacity;
                if (he == 0)
                    break;
            }
            return Splits > before;
        }

        // ---------------------------------------------------------------- constraints

        /// <summary>
        /// Rebuilds the pinned-vertex set from the current geometry: explicit pins, boundary
        /// vertices, and the endpoints of every sharp edge. Recomputing per pass is what lets
        /// constraints be stateless — feature edges are re-derived from the dihedral angles
        /// the mesh actually has now, and vertices created mid-pass inherit their pin from
        /// the split's two endpoints (splitting a crease keeps the crease; splitting a flat
        /// edge does not invent one).
        /// </summary>
        private void RecomputeFixedVertices()
        {
            EnsureVertexCapacity();
            Array.Clear(_fixed);
            for (int v = 0; v < _mesh.VertexCapacity; v++)
            {
                if (!_mesh.IsVertex(v))
                    continue;
                if (_pinned.Contains(v) || (_options.PreserveBoundary && _mesh.IsBoundaryVertex(v)))
                    _fixed[v] = true;
            }

            if (double.IsNegativeInfinity(_featureNormalDot))
                return;
            for (int he = 0; he < _mesh.HalfEdgeCapacity; he++)
            {
                if (!_mesh.IsHalfEdge(he))
                    continue;
                int twin = _mesh.Twin(he);
                if (he > twin || _mesh.IsBoundaryEdge(he))
                    continue;
                if (!IsSharp(he, twin))
                    continue;
                _fixed[_mesh.Origin(he)] = true;
                _fixed[_mesh.Origin(twin)] = true;
            }
        }

        private bool IsSharp(int he, int twin)
        {
            var n0 = FaceNormal(_mesh.FaceOf(he));
            var n1 = FaceNormal(_mesh.FaceOf(twin));
            // Degeneracy is decided against the SAME relative area floor as everything else,
            // never Tolerance.Default: that would apply an absolute 1e-9 to a cross product,
            // so every face of a 1e-5-scale model would read as degenerate and every crease
            // would be silently rounded away (the BSP lesson, third occurrence).
            if (n0.LengthSquared < _degenerateAreaSquared || n1.LengthSquared < _degenerateAreaSquared)
                return false;
            return n0.Dot(n1) / (n0.Length * n1.Length) < _featureNormalDot;
        }

        private bool IsFixed(int vertex) => vertex < _fixed.Length && _fixed[vertex];

        private void EnsureVertexCapacity()
        {
            int capacity = _mesh.VertexCapacity;
            if (_fixed.Length < capacity)
                Array.Resize(ref _fixed, Math.Max(capacity, _fixed.Length * 2));
            if (_buffer.Length < capacity)
            {
                Array.Resize(ref _buffer, Math.Max(capacity, _buffer.Length * 2));
                Array.Resize(ref _modified, _buffer.Length);
            }
        }

        // ---------------------------------------------------------------- topological sweep

        private void SweepEdges()
        {
            int capacity = _mesh.HalfEdgeCapacity;
            if (capacity == 0)
                return;
            int stride = StrideFor(capacity);
            int he = 0;
            while (true)
            {
                // Slots die and are recycled mid-sweep, so every visit re-validates. Edges
                // created with fresh indices beyond the snapshot are simply left for the next
                // pass (a 4x-too-long edge therefore needs two passes to resolve, as in g3).
                if (_mesh.IsHalfEdge(he))
                {
                    int twin = _mesh.Twin(he);
                    if (he < twin) // canonical half-edge: visit each undirected edge once
                        ProcessEdge(he, twin);
                }
                he = (he + stride) % capacity;
                if (he == 0)
                    break;
            }
        }

        // ---------------------------------------------------------------- queue scheduling

        private void EnsureQueueCapacity()
        {
            if (_edgeQueued.Length < _mesh.HalfEdgeCapacity)
                Array.Resize(ref _edgeQueued, Math.Max(_mesh.HalfEdgeCapacity, _edgeQueued.Length * 2));
            if (_vertexQueued.Length < _mesh.VertexCapacity)
            {
                int size = Math.Max(_mesh.VertexCapacity, _vertexQueued.Length * 2);
                Array.Resize(ref _vertexQueued, size);
                Array.Resize(ref _vertexActive, size);
            }
        }

        /// <summary>First pass: the whole mesh, in the sweep's own stride order — so the very
        /// first pass of queue scheduling visits edges exactly as <see cref="SweepEdges"/>
        /// would, and only the passes after it differ.</summary>
        private void SeedQueues()
        {
            int capacity = _mesh.HalfEdgeCapacity;
            if (capacity > 0)
            {
                int stride = StrideFor(capacity);
                int he = 0;
                while (true)
                {
                    if (_mesh.IsHalfEdge(he) && he < _mesh.Twin(he))
                        _edges.Enqueue(he);
                    he = (he + stride) % capacity;
                    if (he == 0)
                        break;
                }
            }
            for (int v = 0; v < _mesh.VertexCapacity; v++)
            {
                if (!_mesh.IsVertex(v))
                    continue;
                _vertexActive[v] = true;
                _vertices.Add(v);
            }
        }

        private void DrainEdgeQueue()
        {
            while (_edges.Count > 0)
            {
                int he = _edges.Dequeue();
                // Slots are recycled, so a queued id may name a different edge by now — or
                // none. Re-validating is not optional and re-canonicalizing is not either:
                // a collapse merges edge pairs, so the survivor is generally named by the
                // other half of the pair.
                if (!_mesh.IsHalfEdge(he))
                    continue;
                int twin = _mesh.Twin(he);
                if (he > twin)
                    (he, twin) = (twin, he);
                ProcessEdge(he, twin);
            }
        }

        /// <summary>
        /// Re-queues a vertex, its one-ring and every edge around it for the NEXT pass. The
        /// one-ring is included because smoothing a vertex is a function of its neighbours:
        /// waking only the vertex that changed would leave its settled neighbours frozen
        /// against geometry that has moved underneath them.
        /// </summary>
        private void Touch(int vertex)
        {
            if (_options.Scheduling != RemeshScheduling.Queue || vertex < 0 || !_mesh.IsVertex(vertex))
                return;
            EnsureQueueCapacity();
            MarkActive(vertex);
            int count = FillRing(vertex);
            for (int i = 0; i < count; i++)
            {
                int he = _ring[i];
                int twin = _mesh.Twin(he);
                int canonical = Math.Min(he, twin);
                if (!_edgeQueued[canonical])
                {
                    _edgeQueued[canonical] = true;
                    _nextEdges.Enqueue(canonical);
                }
                MarkActive(_mesh.Origin(twin));
            }
        }

        private void MarkActive(int vertex)
        {
            if (vertex < 0 || _vertexQueued[vertex])
                return;
            _vertexQueued[vertex] = true;
            _nextVertices.Add(vertex);
        }

        /// <summary>
        /// Folds the vertices this pass disturbed into the set this pass will smooth. Without
        /// it a vertex a split just created would not be smoothed until the pass after — which
        /// is not merely a scheduling nicety: it is what makes a queue-scheduled pass see the
        /// same vertex set the equivalent sweep pass sees, so the seeded first pass produces
        /// the identical mesh and the two paths diverge only where they should.
        /// </summary>
        private void MergePending()
        {
            EnsureQueueCapacity();
            foreach (int v in _nextVertices)
            {
                if (_vertexActive[v])
                    continue;
                _vertexActive[v] = true;
                _vertices.Add(v);
            }
        }

        private void RollQueues()
        {
            foreach (int he in _nextEdges)
                _edgeQueued[he] = false;
            (_edges, _nextEdges) = (_nextEdges, _edges);
            _nextEdges.Clear();

            foreach (int v in _vertices)
                _vertexActive[v] = false;
            foreach (int v in _nextVertices)
                _vertexQueued[v] = false;
            (_vertices, _nextVertices) = (_nextVertices, _vertices);
            _nextVertices.Clear();
            foreach (int v in _vertices)
                _vertexActive[v] = true;
        }

        private void ProcessEdge(int he, int twin)
        {
            int a = _mesh.Origin(he);
            int b = _mesh.Origin(twin);
            var pa = _mesh.GetPosition(a);
            var pb = _mesh.GetPosition(b);
            double lengthSquared = (pb - pa).LengthSquared;

            if (_options.EnableCollapses && lengthSquared < _minLengthSquared && TryCollapse(he, twin, a, b, pa, pb))
            {
                Collapses++;
                return;
            }

            // The flip is deliberately tried BEFORE the split, including on an edge that is
            // too long: a flip is itself a remedy for a long edge, since the other diagonal
            // of an elongated quad is the short one. Splitting such an edge first instead
            // pins the bad configuration and adds a vertex to it — measured on the cylinder,
            // reordering to split-before-flip took the in-band share 92.4% -> 85.6% and the
            // worst triangle angle 0.89 degrees -> 0.17. What the flip stage must not do is
            // CREATE a long edge, which is PreventLongEdgeFlips' job in TryFlip.
            if (_options.EnableFlips && !_mesh.IsBoundaryEdge(he) && TryFlip(he, twin, a, b))
            {
                Flips++;
                return;
            }
            if (_options.EnableSplits && lengthSquared > _maxLengthSquared && TrySplit(he, a, b))
                Splits++;
        }

        private bool TryCollapse(int he, int twin, int a, int b, in Vector3d pa, in Vector3d pb)
        {
            bool fixedA = IsFixed(a), fixedB = IsFixed(b);
            if (fixedA && fixedB)
                return false; // a constrained edge: collapsing it would move constrained geometry

            // Collapse toward the constrained end when there is one (its position is what the
            // constraint protects); otherwise to the midpoint. The end-of-pass projection pass
            // puts the merged vertex back on the target surface.
            int collapseHalfEdge = fixedB ? twin : he;
            var newPosition = fixedA ? pa : fixedB ? pb : (pa + pb) * 0.5;
            int keep = fixedB ? b : a;
            int remove = fixedB ? a : b;

            if (_options.PreventNormalFlips)
            {
                int face0 = _mesh.FaceOf(he);
                int face1 = _mesh.FaceOf(twin);
                if (!RingSurvivesMove(keep, newPosition, face0, face1) ||
                    !RingSurvivesMove(remove, newPosition, face0, face1))
                    return false;
            }

            // Every remaining guard (link condition, bow-ties, tetrahedra, last triangles) is
            // the operator's, and a refusal leaves the mesh untouched.
            if (_mesh.CollapseEdge(collapseHalfEdge, out _) != MeshOperationResult.Ok)
                return false;
            _mesh.SetPosition(keep, newPosition);
            Touch(keep); // `remove` is gone; the whole merged ring hangs off `keep`
            return true;
        }

        private bool TryFlip(int he, int twin, int a, int b)
        {
            if (IsFixed(a) && IsFixed(b))
                return false; // a flip destroys the edge, so a constrained edge may not flip
            int c = _mesh.Origin(_mesh.Prev(he));
            int d = _mesh.Origin(_mesh.Prev(twin));

            // A flip replaces one diagonal of the quad (a, c, b, d) with the other, and the
            // valence rule below never looks at a length — so on an elongated quad it will
            // swap the short diagonal for the long one and manufacture exactly the edge the
            // split stage exists to remove. That churn is the entire reason a plain remesh's
            // maximum edge stalls near 2 L; see RemeshOptions.PreventLongEdgeFlips.
            if (_options.PreventLongEdgeFlips)
            {
                double flipped = (_mesh.GetPosition(d) - _mesh.GetPosition(c)).LengthSquared;
                if (flipped > _maxLengthSquared &&
                    // STRICTLY shorter, exactly — a monotone-decrease rule, not a tolerance.
                    // Permitting an equal length would let a pair of flips ping-pong; and
                    // refusing an improving flip outright strands slivers whose only remedy
                    // it was (measured: a worst triangle angle of 0.02 degrees on a box).
                    flipped >= (_mesh.GetPosition(b) - _mesh.GetPosition(a)).LengthSquared)
                    return false;
            }

            // Valence-based: does the flip bring the four incident valences closer to 6?
            // Boundary vertices target their own current valence, which makes any flip that
            // touches them score worse — the standard way of leaving boundaries alone.
            int valenceA = _mesh.Valence(a), valenceB = _mesh.Valence(b);
            int valenceC = _mesh.Valence(c), valenceD = _mesh.Valence(d);
            int targetA = _mesh.IsBoundaryVertex(a) ? valenceA : 6;
            int targetB = _mesh.IsBoundaryVertex(b) ? valenceB : 6;
            int targetC = _mesh.IsBoundaryVertex(c) ? valenceC : 6;
            int targetD = _mesh.IsBoundaryVertex(d) ? valenceD : 6;
            int before = Math.Abs(valenceA - targetA) + Math.Abs(valenceB - targetB) +
                         Math.Abs(valenceC - targetC) + Math.Abs(valenceD - targetD);
            int after = Math.Abs(valenceA - 1 - targetA) + Math.Abs(valenceB - 1 - targetB) +
                        Math.Abs(valenceC + 1 - targetC) + Math.Abs(valenceD + 1 - targetD);
            if (after >= before)
                return false;

            if (_options.PreventNormalFlips && FlipInvertsNormals(a, b, c, d))
                return false;

            if (_mesh.FlipEdge(he, out _) != MeshOperationResult.Ok)
                return false;
            Touch(a);
            Touch(b);
            Touch(c);
            Touch(d);
            return true;
        }

        private bool TrySplit(int he, int a, int b)
        {
            bool inherit = IsFixed(a) && IsFixed(b);
            if (inherit && !_options.SplitFixedEdges)
                return false;
            if (_mesh.SplitEdge(he, out var info) != MeshOperationResult.Ok)
                return false;
            EnsureVertexCapacity();
            // A split of a constrained edge keeps the constraint: the midpoint lies on the
            // constrained polyline itself, so pinning it preserves that geometry exactly
            // while letting the chain gain resolution.
            _fixed[info.NewVertex] = inherit;
            Touch(info.NewVertex);
            Touch(a);
            Touch(b);
            return true;
        }

        // ---------------------------------------------------------------- smoothing

        /// <summary>
        /// Smooths the vertices in <paramref name="candidates"/> — every live vertex under
        /// sweep scheduling, only the ones still awake under queue scheduling.
        /// </summary>
        private void SmoothPass(List<int> candidates)
        {
            if (_options.Smoothing == RemeshSmoothing.None || _options.SmoothSpeed <= 0)
                return;
            EnsureVertexCapacity();
            ClearModified(candidates);

            // Double-buffered: every new position is computed from the pass's starting
            // geometry, so the result cannot depend on vertex visit order.
            foreach (int v in candidates)
            {
                if (!_mesh.IsVertex(v) || IsFixed(v))
                    continue;
                if (!TryCentroid(v, out var centroid))
                    continue;
                var current = _mesh.GetPosition(v);
                var smoothed = current + (centroid - current) * _options.SmoothSpeed;
                _buffer[v] = smoothed;
                _modified[v] = true;
            }
            ApplyBuffer(candidates);
        }

        private void ClearModified(List<int> candidates)
        {
            foreach (int v in candidates)
            {
                if (v < _modified.Length)
                    _modified[v] = false;
            }
        }

        private bool TryCentroid(int vertex, out Vector3d centroid) =>
            _options.Smoothing == RemeshSmoothing.Cotangent
                ? TryCotangentCentroid(vertex, out centroid)
                : TryUniformCentroid(vertex, out centroid);

        private bool TryUniformCentroid(int vertex, out Vector3d centroid)
        {
            int count = FillRing(vertex);
            centroid = Vector3d.Zero;
            if (count == 0)
                return false;
            for (int i = 0; i < count; i++)
                centroid += _mesh.GetPosition(_mesh.Origin(_mesh.Twin(_ring[i])));
            centroid /= count;
            return true;
        }

        /// <summary>
        /// Cotangent centroid: w_ij = cot α + cot β over the two triangles sharing edge ij.
        /// Returns false — leaving the vertex alone — whenever a weight or the weight sum is
        /// degenerate, which is what sliver triangles produce (the cotangent of a zero angle
        /// is infinite). That is a semantic exact-zero/finiteness test, not a tolerance.
        /// </summary>
        private bool TryCotangentCentroid(int vertex, out Vector3d centroid)
        {
            int count = FillRing(vertex);
            centroid = Vector3d.Zero;
            if (count == 0)
                return false;
            var p = _mesh.GetPosition(vertex);
            var sum = Vector3d.Zero;
            double weightSum = 0;
            for (int i = 0; i < count; i++)
            {
                int he = _ring[i];
                int twin = _mesh.Twin(he);
                int neighbor = _mesh.Origin(twin);
                var q = _mesh.GetPosition(neighbor);
                double weight = 0;
                if (!_mesh.IsBoundaryHalfEdge(he))
                    weight += Cotangent(_mesh.GetPosition(_mesh.Origin(_mesh.Prev(he))), p, q);
                if (!_mesh.IsBoundaryHalfEdge(twin))
                    weight += Cotangent(_mesh.GetPosition(_mesh.Origin(_mesh.Prev(twin))), p, q);
                if (!double.IsFinite(weight))
                    return false;
                sum += q * weight;
                weightSum += weight;
            }
            if (!double.IsFinite(weightSum) || weightSum == 0)
                return false;
            centroid = sum / weightSum;
            return double.IsFinite(centroid.X) && double.IsFinite(centroid.Y) && double.IsFinite(centroid.Z);
        }

        private static double Cotangent(in Vector3d apex, in Vector3d a, in Vector3d b)
        {
            var u = a - apex;
            var v = b - apex;
            double cross = u.Cross(v).Length;
            // Exact-zero division guard (algorithmic tier): a zero cross product is a
            // collinear corner, whose cotangent is infinite; the caller rejects it.
            return cross == 0 ? double.PositiveInfinity : u.Dot(v) / cross;
        }

        // ---------------------------------------------------------------- projection

        private void ProjectPass(List<int> candidates)
        {
            if (_options.ProjectionTarget is not { } target)
                return;
            if (_options.Projection == RemeshProjection.FaceAligned)
            {
                ProjectFaceAlignedPass(target, candidates);
                return;
            }
            EnsureVertexCapacity();
            ClearModified(candidates);
            foreach (int v in candidates)
            {
                if (!_mesh.IsVertex(v) || IsFixed(v))
                    continue;
                var projected = target.Project(_mesh.GetPosition(v));
                _buffer[v] = projected;
                _modified[v] = true;
            }
            ApplyBuffer(candidates);
        }

        /// <summary>
        /// Face-aligned (RZN-flow) reprojection: every triangle is moved rigidly onto the
        /// target — centroid to its closest point, plane rotated to the target's normal there
        /// — and each vertex takes the <c>area × (n·n')³</c>-weighted average of where its
        /// incident triangles put it. See <see cref="RemeshProjection.FaceAligned"/> for why
        /// the cube is what preserves creases.
        /// </summary>
        private void ProjectFaceAlignedPass(IProjectionTarget target, List<int> candidates)
        {
            EnsureVertexCapacity();
            EnsureAccumulators();
            ClearModified(candidates);

            // A vertex's position here is a function of its INCIDENT triangles, so the faces
            // that can contribute to anything this pass writes are exactly those incident to
            // the active set. Under sweep scheduling that is every face, so nothing is gained
            // by testing; under queue scheduling, skipping the rest is what makes face-aligned
            // projection compose with the scheduler instead of costing O(faces) regardless.
            bool restricted = _options.Scheduling == RemeshScheduling.Queue &&
                              !_options.AccumulateOverEveryFace;
            if (restricted)
            {
                // Only the vertices the pass WRITES need a zeroed accumulator, and it writes
                // exactly the candidates. A non-candidate vertex of a visited face is
                // accumulated onto too, and that value is never read — it is zeroed again the
                // moment the vertex is itself a candidate, which is the only way it is read.
                foreach (int v in candidates)
                {
                    _accumulated[v] = Vector3d.Zero;
                    _accumulatedWeight[v] = 0;
                }
            }
            else
            {
                Array.Clear(_accumulated, 0, _mesh.VertexCapacity);
                Array.Clear(_accumulatedWeight, 0, _mesh.VertexCapacity);
            }

            for (int f = 0; f < _mesh.FaceCapacity; f++)
            {
                if (!_mesh.IsFace(f))
                    continue;
                int h0 = _mesh.FaceHalfEdge(f);
                int h1 = _mesh.Next(h0);
                int h2 = _mesh.Next(h1);
                int v0 = _mesh.Origin(h0), v1 = _mesh.Origin(h1), v2 = _mesh.Origin(h2);

                // The whole cost of this stage is the projection below — a nearest-point query
                // against the target — so the restriction skips THAT, not the cheap walk.
                // Keeping the ascending face scan is what makes the restriction bit-identical
                // for FREE: every surviving vertex sees its own incident faces in the same
                // order the whole-mesh walk gave them, and floating-point addition is not
                // associative. (Gathering the incident faces into a list instead needs an
                // explicit sort to restore that order; it was built and measured no faster.)
                if (restricted && !_vertexActive[v0] && !_vertexActive[v1] && !_vertexActive[v2])
                    continue;
                FacesAccumulated++;

                var p0 = _mesh.GetPosition(v0);
                var p1 = _mesh.GetPosition(v1);
                var p2 = _mesh.GetPosition(v2);

                var area = (p1 - p0).Cross(p2 - p0);
                double doubleArea = area.Length;
                if (doubleArea == 0)
                    continue; // no plane to align: an exact-zero test, not an epsilon
                var normal = area / doubleArea;

                var centroid = (p0 + p1 + p2) / 3.0;
                var landed = target.Project(centroid, out var targetNormal);
                double agreement = normal.Dot(targetNormal);
                if (agreement <= 0)
                {
                    // Either the target is unoriented (zero normal — the interface's spelling
                    // for "no orientation available") or this triangle disagrees with the
                    // surface outright. Both get plain closest-point behaviour, which is what
                    // makes an unoriented target degrade to Vertex projection rather than fail.
                    Contribute(v0, landed + (p0 - centroid), 1);
                    Contribute(v1, landed + (p1 - centroid), 1);
                    Contribute(v2, landed + (p2 - centroid), 1);
                    continue;
                }

                double weight = doubleArea * agreement * agreement * agreement;
                Contribute(v0, landed + RotateOnto(p0 - centroid, normal, targetNormal, agreement), weight);
                Contribute(v1, landed + RotateOnto(p1 - centroid, normal, targetNormal, agreement), weight);
                Contribute(v2, landed + RotateOnto(p2 - centroid, normal, targetNormal, agreement), weight);
            }

            foreach (int v in candidates)
            {
                if (!_mesh.IsVertex(v) || IsFixed(v) || _accumulatedWeight[v] == 0)
                    continue;
                _buffer[v] = _accumulated[v] / _accumulatedWeight[v];
                _modified[v] = true;
            }
            ApplyBuffer(candidates);

            void Contribute(int vertex, in Vector3d position, double weight)
            {
                _accumulated[vertex] += position * weight;
                _accumulatedWeight[vertex] += weight;
            }
        }

        /// <summary>
        /// Rodrigues rotation of <paramref name="v"/> by the rotation taking
        /// <paramref name="from"/> onto <paramref name="to"/> (both unit), with their dot
        /// product already in hand. The caller guarantees it is positive, so the axis can only
        /// vanish when the two coincide — where the rotation is the identity.
        /// </summary>
        private static Vector3d RotateOnto(in Vector3d v, in Vector3d from, in Vector3d to, double cos)
        {
            var axis = from.Cross(to);
            double sin = axis.Length;
            if (sin == 0)
                return v;
            var k = axis / sin;
            return v * cos + k.Cross(v) * sin + k * (k.Dot(v) * (1 - cos));
        }

        private void EnsureAccumulators()
        {
            int capacity = _mesh.VertexCapacity;
            if (_accumulated.Length >= capacity)
                return;
            int size = Math.Max(capacity, _accumulated.Length * 2);
            Array.Resize(ref _accumulated, size);
            Array.Resize(ref _accumulatedWeight, size);
        }

        /// <summary>
        /// Writes the buffered positions back, skipping any move that would invert or
        /// degenerate an incident triangle. The guard is evaluated against the pass's
        /// starting geometry (buffered, not in-place), so acceptance does not depend on
        /// visit order either.
        /// </summary>
        private void ApplyBuffer(List<int> candidates)
        {
            if (_options.PreventNormalFlips)
            {
                foreach (int v in candidates)
                {
                    if (_modified[v] && !RingSurvivesMove(v, _buffer[v], -1, -1))
                        _modified[v] = false;
                }
            }
            _moved.Clear();
            _movedTo.Clear();
            foreach (int v in candidates)
            {
                if (!_modified[v])
                    continue;
                _moved.Add(v);
                _movedTo.Add(_buffer[v]);
                // A vertex that moved appreciably keeps its neighbourhood awake: a move
                // changes incident edge lengths, so the edges around it must be looked at
                // again even if no topological operation touched them.
                if ((_buffer[v] - _mesh.GetPosition(v)).LengthSquared > _quiescenceSquared)
                    Touch(v);
            }
            // One bulk operation, not one per vertex: a whole-mesh smoothing pass would
            // otherwise emit one change record and one full DEBUG validation per vertex.
            _mesh.SetPositions(CollectionsMarshal.AsSpan(_moved), CollectionsMarshal.AsSpan(_movedTo));
        }

        // ---------------------------------------------------------------- geometric guards

        /// <summary>
        /// True when moving <paramref name="vertex"/> to <paramref name="newPosition"/> leaves
        /// every incident triangle (except the two being destroyed by a collapse) pointing the
        /// same way and above the degeneracy floor. The test reads the <b>sign</b> of the dot
        /// of two unnormalized cross products, which is scale-free, plus one relative area
        /// floor — never <c>TryNormalize</c> against an absolute tolerance, which would be an
        /// absolute epsilon applied to an area.
        /// </summary>
        private bool RingSurvivesMove(int vertex, in Vector3d newPosition, int skipFace0, int skipFace1)
        {
            int count = FillRing(vertex);
            for (int i = 0; i < count; i++)
            {
                int face = _mesh.FaceOf(_ring[i]);
                if (face < 0 || face == skipFace0 || face == skipFace1)
                    continue;
                int h0 = _mesh.FaceHalfEdge(face);
                int h1 = _mesh.Next(h0);
                int h2 = _mesh.Next(h1);
                int v0 = _mesh.Origin(h0), v1 = _mesh.Origin(h1), v2 = _mesh.Origin(h2);
                var p0 = _mesh.GetPosition(v0);
                var p1 = _mesh.GetPosition(v1);
                var p2 = _mesh.GetPosition(v2);
                var before = (p1 - p0).Cross(p2 - p0);
                var q0 = v0 == vertex ? newPosition : p0;
                var q1 = v1 == vertex ? newPosition : p1;
                var q2 = v2 == vertex ? newPosition : p2;
                var after = (q1 - q0).Cross(q2 - q0);
                if (after.LengthSquared < _degenerateAreaSquared || before.Dot(after) <= 0)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// True when flipping edge (a,b) to (c,d) would turn a triangle over. Both post-flip
        /// normals are compared against both pre-flip normals — a flip is only safe when the
        /// quad really is locally convex, and comparing one pairing lets a fold through.
        /// </summary>
        private bool FlipInvertsNormals(int a, int b, int c, int d)
        {
            var pa = _mesh.GetPosition(a);
            var pb = _mesh.GetPosition(b);
            var pc = _mesh.GetPosition(c);
            var pd = _mesh.GetPosition(d);
            var before0 = (pb - pa).Cross(pc - pa); // (a, b, c)
            var before1 = (pa - pb).Cross(pd - pb); // (b, a, d)
            var after0 = (pd - pc).Cross(pb - pc);  // (c, d, b)
            var after1 = (pc - pd).Cross(pa - pd);  // (d, c, a)
            if (after0.LengthSquared < _degenerateAreaSquared || after1.LengthSquared < _degenerateAreaSquared)
                return true;
            return before0.Dot(after0) <= 0 || before1.Dot(after0) <= 0 ||
                   before0.Dot(after1) <= 0 || before1.Dot(after1) <= 0;
        }

        private Vector3d FaceNormal(int face)
        {
            int h0 = _mesh.FaceHalfEdge(face);
            int h1 = _mesh.Next(h0);
            int h2 = _mesh.Next(h1);
            var p0 = _mesh.GetPosition(_mesh.Origin(h0));
            var p1 = _mesh.GetPosition(_mesh.Origin(h1));
            var p2 = _mesh.GetPosition(_mesh.Origin(h2));
            return (p1 - p0).Cross(p2 - p0);
        }

        // Reused ring buffer: the one-ring walk must not allocate per call (it runs a few
        // times per edge visit), so the buffer grows and stays.
        private int FillRing(int vertex)
        {
            int count;
            while ((count = _mesh.OutgoingHalfEdges(vertex, _ring)) < 0)
                _ring = new int[_ring.Length * 2];
            return count;
        }
    }
}
