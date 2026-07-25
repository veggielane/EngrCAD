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

    /// <summary>Split edges longer than <see cref="MaxEdgeLength"/>.</summary>
    public bool EnableSplits { get; init; } = true;

    /// <summary>Collapse edges shorter than <see cref="MinEdgeLength"/>.</summary>
    public bool EnableCollapses { get; init; } = true;

    /// <summary>Flip interior edges when that brings the four incident valences closer to 6.</summary>
    public bool EnableFlips { get; init; } = true;

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
    /// onto the original mesh; an SDF-backed target is a few lines in the implicit engine.
    /// </summary>
    public IProjectionTarget? ProjectionTarget { get; init; }

    /// <summary>
    /// Refuse any operation or vertex move that would invert or degenerate an incident
    /// triangle. Costs one one-ring walk per candidate; leave it on unless profiling says
    /// otherwise.
    /// </summary>
    public bool PreventNormalFlips { get; init; } = true;

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
public sealed record RemeshResult(HalfEdgeMesh Mesh, int Splits, int Collapses, int Flips, int Iterations);

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

        var triangles = mesh.Faces.Any(f => f.Degree != 3) ? mesh.Triangulated() : mesh;
        var editable = EditableMesh.FromMesh(triangles);
        var state = new RemeshState(editable, options);

        for (int i = 0; i < options.Iterations; i++)
        {
            progress?.ThrowIfCancelled();
            state.Pass();
            progress?.Report((double)(i + 1) / options.Iterations);
        }
        progress?.ThrowIfCancelled();

        return new RemeshResult(editable.ToMesh(), state.Splits, state.Collapses, state.Flips, options.Iterations);
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
        }

        public int Splits { get; private set; }
        public int Collapses { get; private set; }
        public int Flips { get; private set; }

        public void Pass()
        {
            RecomputeFixedVertices();
            SweepEdges();
            SmoothPass();
            ProjectPass();
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
            return true;
        }

        private bool TryFlip(int he, int twin, int a, int b)
        {
            if (IsFixed(a) && IsFixed(b))
                return false; // a flip destroys the edge, so a constrained edge may not flip
            int c = _mesh.Origin(_mesh.Prev(he));
            int d = _mesh.Origin(_mesh.Prev(twin));

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

            return _mesh.FlipEdge(he, out _) == MeshOperationResult.Ok;
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
            return true;
        }

        // ---------------------------------------------------------------- smoothing

        private void SmoothPass()
        {
            if (_options.Smoothing == RemeshSmoothing.None || _options.SmoothSpeed <= 0)
                return;
            EnsureVertexCapacity();
            Array.Clear(_modified);

            // Double-buffered: every new position is computed from the pass's starting
            // geometry, so the result cannot depend on vertex visit order.
            for (int v = 0; v < _mesh.VertexCapacity; v++)
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
            ApplyBuffer();
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

        private void ProjectPass()
        {
            if (_options.ProjectionTarget is not { } target)
                return;
            EnsureVertexCapacity();
            Array.Clear(_modified);
            for (int v = 0; v < _mesh.VertexCapacity; v++)
            {
                if (!_mesh.IsVertex(v) || IsFixed(v))
                    continue;
                var projected = target.Project(_mesh.GetPosition(v));
                _buffer[v] = projected;
                _modified[v] = true;
            }
            ApplyBuffer();
        }

        /// <summary>
        /// Writes the buffered positions back, skipping any move that would invert or
        /// degenerate an incident triangle. The guard is evaluated against the pass's
        /// starting geometry (buffered, not in-place), so acceptance does not depend on
        /// visit order either.
        /// </summary>
        private void ApplyBuffer()
        {
            if (_options.PreventNormalFlips)
            {
                for (int v = 0; v < _mesh.VertexCapacity; v++)
                {
                    if (_modified[v] && !RingSurvivesMove(v, _buffer[v], -1, -1))
                        _modified[v] = false;
                }
            }
            _moved.Clear();
            _movedTo.Clear();
            for (int v = 0; v < _mesh.VertexCapacity; v++)
            {
                if (!_modified[v])
                    continue;
                _moved.Add(v);
                _movedTo.Add(_buffer[v]);
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
