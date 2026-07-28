using EngrCAD.Core;
using EngrCAD.Core.Spatial;
using EngrCAD.Mesh;

namespace EngrCAD.Modeling;

// Hidden-line removal: the projection step that turns 3D geometry into the classified
// 2D line work an engineering drawing is made of.
//
// The v1 fidelity contract, stated once here and repeated in the docs because it is the
// thing a user must know: the DRAWN geometry is exact wherever the kernel has it (a
// B-Rep part's feature edges are sampled from the actual edge curves at display
// resolution, so a bore rim is a smooth circle at any mesh quality), while the
// VISIBILITY QUESTION is answered against the display mesh. True silhouette curves on
// curved surfaces do not exist in the kernel yet, so the outline of a cylinder seen
// from the side comes from the mesh's own view-dependent silhouette and is faceted at
// mesh resolution. Exact edges, mesh-decided visibility, mesh-derived curved outlines.

/// <summary>Whether a run of projected line work is drawn solid or dashed.</summary>
public enum EdgeVisibility
{
    /// <summary>Nothing is between this run and the viewer: solid line.</summary>
    Visible,

    /// <summary>Material lies between this run and the viewer: dashed line.</summary>
    Hidden,
}

/// <summary>Where a run of line work came from, so a consumer can style or filter it
/// (and so the honest-fidelity story survives into the output).</summary>
public enum EdgeSource
{
    /// <summary>A modelled edge: exact for B-Rep parts, mesh dihedral otherwise.</summary>
    Feature,

    /// <summary>A view-dependent outline of a SMOOTH surface, taken from the display
    /// mesh (the true-silhouette-curve gap; faceted at mesh resolution).</summary>
    Silhouette,

    /// <summary>The boundary of a cut face in a section view.</summary>
    Cut,
}

/// <summary>
/// One classified run of projected line work: a polyline in the view's sheet
/// coordinates (millimetres, y up), all of it either visible or hidden.
/// </summary>
/// <param name="Points">The polyline, at least two points.</param>
/// <param name="Visibility">Solid or dashed.</param>
/// <param name="Source">What produced it.</param>
public sealed record HiddenLineRun(
    IReadOnlyList<Vector2d> Points, EdgeVisibility Visibility, EdgeSource Source)
{
    /// <summary>Total 2D length of the run.</summary>
    public double Length
    {
        get
        {
            double total = 0;
            for (int i = 0; i + 1 < Points.Count; i++)
                total += Points[i].DistanceTo(Points[i + 1]);
            return total;
        }
    }
}

/// <summary>
/// The result of projecting a set of instances through <see cref="HiddenLineRemoval"/>:
/// classified line work plus the bounds it occupies in sheet coordinates.
/// </summary>
/// <param name="Runs">Every classified run, in edge-discovery order (deterministic).</param>
/// <param name="Bounds">2D extent of the line work (z is always 0).</param>
public sealed record HiddenLineResult(IReadOnlyList<HiddenLineRun> Runs, Aabb Bounds)
{
    /// <summary>The visible runs only.</summary>
    public IEnumerable<HiddenLineRun> Visible =>
        Runs.Where(r => r.Visibility == EdgeVisibility.Visible);

    /// <summary>The hidden runs only.</summary>
    public IEnumerable<HiddenLineRun> Hidden =>
        Runs.Where(r => r.Visibility == EdgeVisibility.Hidden);

    /// <summary>An empty result (no geometry projected).</summary>
    public static readonly HiddenLineResult Empty = new([], Aabb.Empty);
}

/// <summary>
/// Knobs for <see cref="HiddenLineRemoval"/>. Every length is expressed as a FRACTION of
/// the projected geometry's extent — a drawing of a 4 mm dowel and a drawing of a 4 m
/// beam should come out the same, and an absolute default would be wrong for one of
/// them (the scale-free tier, as everywhere else in this kernel).
/// </summary>
public sealed record HiddenLineOptions
{
    /// <summary>Mesh quality for the occluders and for a non-B-Rep part's edges;
    /// null takes the scene/global default.</summary>
    public MeshQuality? Quality { get; init; }

    /// <summary>
    /// How far a visibility probe steps off the surface before casting, as a fraction of
    /// the geometry's extent.
    ///
    /// <para><b>Why any step is needed.</b> The edges are exact and the occluders are a
    /// tessellation of the same surfaces, so the two disagree by up to the chord sagitta:
    /// an exact point on a bore wall sits INSIDE the inscribed mesh by that much, and a
    /// probe started there would immediately hit its own solid. The step must therefore
    /// exceed the tessellation's chord error — which is why the default is a fraction of
    /// the model rather than a weld-tier constant, and why a deliberately coarse mesh
    /// wants a larger one.</para>
    /// </summary>
    public double BiasFraction { get; init; } = DefaultBiasFraction;

    /// <summary>Default <see cref="BiasFraction"/>: 1/1000 of the extent, comfortably
    /// above the chord sagitta at any sane tessellation and far below anything a
    /// drawing shows.</summary>
    public const double DefaultBiasFraction = 1e-3;

    /// <summary>Spacing of visibility samples along an edge, as a fraction of the
    /// extent. Finer resolves shorter hidden runs and costs one ray each.</summary>
    public double SampleFraction { get; init; } = DefaultSampleFraction;

    /// <summary>Default <see cref="SampleFraction"/>: 1/200 of the extent, so a
    /// full-width edge is classified at 200 points.</summary>
    public const double DefaultSampleFraction = 1 / 200.0;

    /// <summary>
    /// How precisely a visibility change is located, as a fraction of the extent.
    /// Bisection between two differently-classified samples stops here, so this — not
    /// <see cref="SampleFraction"/> — is the accuracy of a dashed run's ENDS.
    /// </summary>
    public double SplitFraction { get; init; } = DefaultSplitFraction;

    /// <summary>Default <see cref="SplitFraction"/>: 1e-5 of the extent, two decades
    /// finer than a drawn line is wide.</summary>
    public const double DefaultSplitFraction = 1e-5;

    /// <summary>
    /// Runs shorter than this fraction of the extent are absorbed into their neighbour
    /// rather than emitted — the "shorter than a pen stroke" rule.
    ///
    /// <para><b>It is not cosmetic.</b> Within one <see cref="BiasFraction"/> step of a
    /// model VERTEX, the probe's local-surface read picks up the faces on the far side
    /// of that vertex, so a hidden edge reads visible for its last bias-length. Every
    /// HLR implementation has some version of this artifact, because "the surface near
    /// this point" is genuinely ambiguous at a corner; the honest response is to drop
    /// runs too short to draw rather than to pretend the corner is unambiguous. Set it
    /// to 0 to see the raw classification.</para>
    /// </summary>
    public double MinimumRunFraction { get; init; } = DefaultMinimumRunFraction;

    /// <summary>Default <see cref="MinimumRunFraction"/>: twice the bias, so the
    /// corner artifact is always covered and nothing longer is.</summary>
    public const double DefaultMinimumRunFraction = 2 * DefaultBiasFraction;

    /// <summary>Douglas–Peucker tolerance applied to each emitted run, as a fraction of
    /// the extent. Its job is to undo the visibility SAMPLING — a straight edge sampled
    /// at 200 points is still a straight edge — so it sits at the weld tier, where it
    /// removes exactly-collinear inserted points and nothing a curve carries.</summary>
    public double SimplifyFraction { get; init; } = DefaultSimplifyFraction;

    /// <summary>Default <see cref="SimplifyFraction"/>: 1e-9 of the extent.</summary>
    public const double DefaultSimplifyFraction = 1e-9;

    /// <summary>Include the mesh-derived outline of smooth surfaces (a cylinder seen
    /// from the side has no modelled edge there). On by default; turning it off leaves
    /// only exact geometry, which is occasionally what a diagnostic wants.</summary>
    public bool IncludeSilhouette { get; init; } = true;

    /// <summary>Emit hidden runs at all. Off gives a visible-only drawing.</summary>
    public bool IncludeHidden { get; init; } = true;

    /// <summary>Dihedral above which a mesh edge counts as a modelled crease (and so is
    /// already in the feature-edge set, and is not re-emitted as a silhouette). The
    /// viewer's own 30-degree feature angle.</summary>
    public double SharpAngleRadians { get; init; } = Math.PI / 6;
}

/// <summary>
/// Turns 3D geometry into classified 2D line work: project every part's edges into a
/// view plane and mark each piece visible or hidden by testing it against the geometry
/// in front of it.
///
/// <para><b>What is drawn.</b> Two edge sets, kept distinguishable in the output via
/// <see cref="EdgeSource"/>: a part's <see cref="Part.GetFeatureEdges"/> — the ACTUAL
/// B-Rep edge curves for a B-Rep-backed part, sampled at display resolution, so a bore
/// rim stays a smooth circle however coarse the mesh — plus, for the curved surfaces
/// that have no modelled edge at their outline, the display mesh's view-dependent
/// silhouette. The second set is faceted at mesh resolution and says so; true
/// silhouette CURVES on curved surfaces are the known upgrade.</para>
///
/// <para><b>How visibility is decided</b>, and this is the part worth reading. Each
/// sample point takes a two-stage test:</para>
/// <list type="number">
/// <item><b>Its own surface first, and exactly.</b> The surface immediately around the
/// point is read from the owning instance's mesh (every triangle within the bias step).
/// If every one of them faces AWAY from the viewer, the point is buried in its own
/// material and is hidden — no ray needed. That is the classic back-face rule, and it
/// settles the majority of a solid's edges for free.</item>
/// <item><b>Otherwise, a ray.</b> The probe starts at the point stepped off along the
/// most eye-facing local normal (which is what makes a grazing case work: on a bore's
/// bottom rim, that normal points into the bore's void, so the ray runs up the empty
/// hole instead of scraping along the wall it is tangent to) and then a further step
/// toward the viewer, and any hit on any visible instance means hidden.</item>
/// </list>
///
/// <para>A run's ENDS are then refined by bisection to
/// <see cref="HiddenLineOptions.SplitFraction"/>, so the dash boundary is far finer
/// than the sample spacing that found it.</para>
///
/// <para><b>Occlusion is scene-wide</b>: a drawing shows an assembly, so every visible
/// instance occludes every other, not just its owner.</para>
///
/// <para>Deterministic: no randomness, no parallelism, edges emitted in discovery
/// order.</para>
/// </summary>
public static class HiddenLineRemoval
{
    /// <summary>
    /// Projects one part (with its own transform applied) into <paramref name="view"/>.
    /// </summary>
    public static HiddenLineResult Project(
        Part part, in Frame3d view, HiddenLineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(part);
        return Project([new PartInstance(part, part.Transform, part.Name)], view, options);
    }

    /// <summary>
    /// Projects a scene's instances into <paramref name="view"/> — the whole document,
    /// assemblies flattened.
    /// </summary>
    public static HiddenLineResult Project(
        Scene scene, in Frame3d view, HiddenLineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var quality = scene.ResolveQuality(options?.Quality);
        return Project([.. scene.AllInstances], view, options is null
            ? new HiddenLineOptions { Quality = quality }
            : options with { Quality = options.Quality ?? quality });
    }

    /// <summary>
    /// Projects a list of instances into <paramref name="view"/>: the view frame's X is
    /// sheet-right, its Y sheet-up and its Z points toward the viewer (see
    /// <see cref="StandardViews.SheetFrame"/>). Parts are filtered by
    /// <see cref="DebugFilter.Exported"/> — a drawing is a deliverable, so a hidden part
    /// is absent and a GHOSTED one is too (ghosting is a viewport aid, and a translucent
    /// line has no meaning on paper).
    /// </summary>
    public static HiddenLineResult Project(
        IReadOnlyList<PartInstance> instances, in Frame3d view, HiddenLineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(instances);
        var opts = options ?? new HiddenLineOptions();
        var shown = DebugFilter.Exported(instances);
        if (shown.Count == 0)
            return HiddenLineResult.Empty;

        var toViewer = view.Z.Normalized(Tolerance.Default);
        var occluders = new List<Occluder>(shown.Count);
        var world = Aabb.Empty;
        foreach (var instance in shown)
        {
            var occluder = Occluder.Build(instance, opts.Quality, toViewer);
            if (occluder is null)
                continue;
            occluders.Add(occluder);
            world = world.Union(occluder.Bounds);
        }
        if (occluders.Count == 0)
            return HiddenLineResult.Empty;

        double extent = Extent(world);
        var probe = new VisibilityProbe(occluders, toViewer, extent, opts);
        var runs = new List<HiddenLineRun>();
        var bounds = Aabb.Empty;

        for (int i = 0; i < occluders.Count; i++)
        {
            foreach (var (points, source) in occluders[i].Edges(opts))
                Classify(points, source, i, probe, view, extent, opts, runs, ref bounds);
        }
        return new HiddenLineResult(runs, bounds);
    }

    /// <summary>
    /// Classifies caller-supplied world-space polylines against the same instances —
    /// how a section view gets its cut-face boundaries drawn without them having to be
    /// edges of any part.
    /// </summary>
    public static HiddenLineResult Project(
        IReadOnlyList<PartInstance> instances, in Frame3d view,
        IReadOnlyList<IReadOnlyList<Vector3d>> extraEdges, EdgeSource extraSource,
        HiddenLineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(extraEdges);
        var baseResult = Project(instances, view, options);
        if (extraEdges.Count == 0)
            return baseResult;

        var opts = options ?? new HiddenLineOptions();
        var toViewer = view.Z.Normalized(Tolerance.Default);
        var occluders = new List<Occluder>();
        var world = Aabb.Empty;
        foreach (var instance in DebugFilter.Exported(instances))
        {
            var occluder = Occluder.Build(instance, opts.Quality, toViewer);
            if (occluder is null)
                continue;
            occluders.Add(occluder);
            world = world.Union(occluder.Bounds);
        }

        double extent = Extent(world);
        // Owner −1: a supplied polyline belongs to no instance, so the local back-face
        // stage has nothing to read and the ray decides on its own.
        var probe = new VisibilityProbe(occluders, toViewer, extent, opts);
        var runs = new List<HiddenLineRun>(baseResult.Runs);
        var bounds = baseResult.Bounds;
        foreach (var polyline in extraEdges)
        {
            if (polyline.Count >= 2)
                Classify(polyline, extraSource, -1, probe, view, extent, opts, runs, ref bounds);
        }
        return new HiddenLineResult(runs, bounds);
    }

    /// <summary>The characteristic length every fraction in
    /// <see cref="HiddenLineOptions"/> multiplies: the bounding box's diagonal, or 1 for
    /// degenerate (single-point) input so no fraction becomes zero.</summary>
    internal static double Extent(in Aabb bounds)
    {
        if (bounds.IsEmpty)
            return 1;
        double diagonal = bounds.Size.Length;
        return diagonal > 0 ? diagonal : 1;
    }

    // ---------------------------------------------------------------- classification

    /// <summary>
    /// Samples one world-space polyline, classifies each sample, refines every
    /// transition by bisection, and appends the resulting runs projected into the sheet.
    /// </summary>
    private static void Classify(
        IReadOnlyList<Vector3d> points, EdgeSource source, int owner, VisibilityProbe probe,
        in Frame3d view, double extent, HiddenLineOptions options,
        List<HiddenLineRun> runs, ref Aabb bounds)
    {
        double step = Math.Max(extent * options.SampleFraction, extent * 1e-9);
        double split = Math.Max(extent * options.SplitFraction, extent * 1e-12);

        // One flat sample list per polyline: every input vertex is kept (they are the
        // exact curve samples) and long segments are subdivided to the sample spacing.
        var samples = new List<Vector3d>(points.Count * 2);
        samples.Add(points[0]);
        for (int i = 0; i + 1 < points.Count; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            int pieces = Math.Max(1, (int)Math.Ceiling(a.DistanceTo(b) / step));
            for (int k = 1; k <= pieces; k++)
                samples.Add(a + (b - a) * (k / (double)pieces));
        }
        if (samples.Count < 2)
            return;

        var flags = new bool[samples.Count];
        for (int i = 0; i < samples.Count; i++)
            flags[i] = probe.IsVisible(samples[i], owner);
        Despeckle(samples, flags, extent * options.MinimumRunFraction);

        // Walk the samples, emitting a run per constant-visibility stretch and splitting
        // at a bisected transition point wherever the flag changes.
        var current = new List<Vector3d> { samples[0] };
        bool currentVisible = flags[0];
        for (int i = 1; i < samples.Count; i++)
        {
            if (flags[i] == currentVisible)
            {
                current.Add(samples[i]);
                continue;
            }
            var transition = Bisect(samples[i - 1], samples[i], currentVisible, probe, owner, split);
            current.Add(transition);
            Emit(current, currentVisible, source, view, extent, options, runs, ref bounds);
            current = [transition, samples[i]];
            currentVisible = flags[i];
        }
        Emit(current, currentVisible, source, view, extent, options, runs, ref bounds);
    }

    /// <summary>
    /// Absorbs runs shorter than <paramref name="minimum"/> into a neighbour, shortest
    /// first, until none remain — see <see cref="HiddenLineOptions.MinimumRunFraction"/>
    /// for why they exist at all. A run with two neighbours joins the LONGER one, which
    /// is what keeps the pass from cascading: the decision never depends on the order
    /// the runs happen to sit in.
    /// </summary>
    private static void Despeckle(List<Vector3d> samples, bool[] flags, double minimum)
    {
        if (!(minimum > 0) || flags.Length < 2)
            return;

        while (true)
        {
            // Run boundaries: start index of each maximal equal-flag stretch, plus the
            // end sentinel.
            var starts = new List<int> { 0 };
            for (int i = 1; i < flags.Length; i++)
            {
                if (flags[i] != flags[i - 1])
                    starts.Add(i);
            }
            if (starts.Count < 2)
                return;
            starts.Add(flags.Length);

            int shortest = -1;
            double shortestLength = minimum;
            for (int r = 0; r + 1 < starts.Count; r++)
            {
                double length = ArcLength(samples, starts[r], starts[r + 1] - 1);
                if (length < shortestLength)
                {
                    shortestLength = length;
                    shortest = r;
                }
            }
            if (shortest < 0)
                return;

            int runs = starts.Count - 1;
            bool takeLeft = shortest > 0 && (shortest + 1 >= runs
                || ArcLength(samples, starts[shortest - 1], starts[shortest] - 1)
                   >= ArcLength(samples, starts[shortest + 1], starts[shortest + 2] - 1));
            bool value = takeLeft ? flags[starts[shortest - 1]] : flags[starts[shortest + 1]];
            for (int i = starts[shortest]; i < starts[shortest + 1]; i++)
                flags[i] = value;
        }
    }

    private static double ArcLength(List<Vector3d> samples, int from, int to)
    {
        double total = 0;
        for (int i = from; i < to; i++)
            total += samples[i].DistanceTo(samples[i + 1]);
        return total;
    }

    /// <summary>The point on segment a-b where visibility flips, to
    /// <paramref name="tolerance"/>. Plain bisection: the predicate is a step function
    /// and only its location is wanted, so there is nothing to converge quadratically
    /// on — and a bracket that starts with disagreeing ends can never fail.</summary>
    private static Vector3d Bisect(
        in Vector3d a, in Vector3d b, bool visibleAtA, VisibilityProbe probe, int owner, double tolerance)
    {
        var lo = a;
        var hi = b;
        // Iteration cap AND a length test: the cap alone would be a magic number, the
        // length alone could spin on coincident endpoints.
        for (int step = 0; step < 60 && lo.DistanceTo(hi) > tolerance; step++)
        {
            var mid = (lo + hi) * 0.5;
            if (probe.IsVisible(mid, owner) == visibleAtA)
                lo = mid;
            else
                hi = mid;
        }
        return (lo + hi) * 0.5;
    }

    private static void Emit(
        List<Vector3d> points, bool visible, EdgeSource source, in Frame3d view, double extent,
        HiddenLineOptions options, List<HiddenLineRun> runs, ref Aabb bounds)
    {
        if (points.Count < 2)
            return;
        if (!visible && !options.IncludeHidden)
            return;

        var flat = new Vector2d[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            var local = view.ToLocal(points[i]);
            flat[i] = new Vector2d(local.X, local.Y);
            bounds = bounds.Union(new Vector3d(local.X, local.Y, 0));
        }
        // Undo the visibility sampling: the inserted points sit exactly on their chord,
        // so a weld-tier Douglas-Peucker drops them and leaves a curve's own samples.
        var simplified = PolylineSimplify.Simplify(flat, extent * options.SimplifyFraction);
        runs.Add(new HiddenLineRun(
            simplified, visible ? EdgeVisibility.Visible : EdgeVisibility.Hidden, source));
    }

    // ------------------------------------------------------------------- the probe

    /// <summary>
    /// The two-stage visibility test described on <see cref="HiddenLineRemoval"/>:
    /// a local back-face read against the owning instance, then a ray against every
    /// instance.
    /// </summary>
    private sealed class VisibilityProbe(
        IReadOnlyList<Occluder> occluders, Vector3d toViewer, double extent, HiddenLineOptions options)
    {
        private readonly Vector3d _toViewer = toViewer.Normalized(Tolerance.Default);
        private readonly double _bias = Math.Max(extent * options.BiasFraction, extent * 1e-12);
        private readonly double _reach = extent * 4;   // clears any bounding box from anywhere in it
        private readonly List<int> _scratch = [];

        public bool IsVisible(in Vector3d point, int owner)
        {
            // Stage 1: the point's own surface. Every triangle of the owning instance
            // within one bias step is "the surface here"; if all of them face away, the
            // point is inside its own material.
            if (owner < 0
                || !occluders[owner].LocalNormals(point, _bias, _scratch, out var mostFacing, out double bestDot))
            {
                // Nothing local (a sample off its own mesh — a degenerate edge, or a
                // bias smaller than the tessellation error). Fall back to the ray alone
                // rather than guessing.
                mostFacing = _toViewer;
                bestDot = 1;
            }
            // Exact-sign test on a dot of unit vectors: a face turned even slightly away
            // from the viewer is behind its own solid. Grazing (exactly 0) counts as
            // potentially visible, and stepping along that normal is precisely what
            // takes the probe off a surface it runs tangent to.
            if (bestDot < 0)
                return false;

            var origin = point + mostFacing * _bias + _toViewer * _bias;
            var ray = new Ray3d(origin, _toViewer * _reach);
            foreach (var occluder in occluders)
            {
                if (occluder.Hits(ray, _scratch))
                    return false;
            }
            return true;
        }
    }

    // ---------------------------------------------------------------- the occluders

    /// <summary>
    /// One instance's world-space triangles, their normals and a BVH over them, plus the
    /// edge sets it contributes. World-space rather than the shared-BVH-plus-local-ray
    /// trick the viewport's picker uses: a drawing is computed once, and keeping every
    /// normal in one frame is what makes the local back-face read a plain dot product.
    /// </summary>
    private sealed class Occluder
    {
        private Vector3d[] _positions = [];
        private int[] _indices = [];
        private Vector3d[] _normals = [];
        private Bvh _bvh = null!;
        private HalfEdgeMesh _mesh = null!;
        private Matrix4d _world;
        private Part _part = null!;
        private MeshQuality? _quality;
        private Vector3d _toViewer = Vector3d.UnitZ;
        private Vector3d _localToViewer = Vector3d.UnitZ;
        private bool _mirrored;

        public Aabb Bounds { get; private set; } = Aabb.Empty;

        /// <summary>Builds the occluder, or null when the part has no meshable
        /// geometry (a failed lowering is a part the drawing simply cannot show; the
        /// document model has already reported it).</summary>
        public static Occluder? Build(in PartInstance instance, MeshQuality? quality, in Vector3d toViewer)
        {
            HalfEdgeMesh mesh;
            try
            {
                mesh = instance.Part.GetMesh(quality);
            }
            catch (Exception)
            {
                return null;
            }

            var occluder = new Occluder
            {
                _mesh = mesh,
                _world = instance.World,
                _part = instance.Part,
                _quality = quality,
                _toViewer = toViewer,
                _mirrored = instance.World.Determinant < 0,
            };
            // The view direction pulled back into the mesh's own frame. n_world . v has
            // the same sign as n_local . (M^-1 v) for ANY invertible affine M (the
            // normal transforms by the inverse transpose, and the two inverses cancel),
            // so the silhouette test needs no inverse-transpose of its own; a mirroring
            // placement flips the winding on top of that, hence the extra sign.
            occluder._localToViewer = instance.World.TryInvert(out var toLocal)
                ? toLocal.TransformVector(toViewer) * (occluder._mirrored ? -1 : 1)
                : toViewer;

            var positions = new Vector3d[mesh.VertexCount];
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                positions[v] = instance.World.TransformPoint(mesh.GetPosition(v));
                occluder.Bounds = occluder.Bounds.Union(positions[v]);
            }

            // Fan-triangulate in place rather than through Triangulated(): the display
            // mesh may carry Surface Nets quads, and rebuilding a whole half-edge mesh
            // to read three indices at a time is the cost that lesson warns about.
            var indices = new List<int>(mesh.FaceCount * 3);
            for (int f = 0; f < mesh.FaceCount; f++)
            {
                var corners = mesh.GetFace(f).Vertices().Select(v => v.Index).ToList();
                for (int k = 1; k + 1 < corners.Count; k++)
                {
                    indices.Add(corners[0]);
                    indices.Add(corners[k]);
                    indices.Add(corners[k + 1]);
                }
            }

            occluder._positions = positions;
            occluder._indices = [.. indices];
            int triangles = occluder._indices.Length / 3;
            occluder._normals = new Vector3d[triangles];
            var boxes = new Aabb[triangles];
            for (int t = 0; t < triangles; t++)
            {
                var a = positions[occluder._indices[t * 3]];
                var b = positions[occluder._indices[t * 3 + 1]];
                var c = positions[occluder._indices[t * 3 + 2]];
                // Area-weighted normal read only for its DIRECTION; a degenerate facet
                // normalizes to zero and then contributes nothing to the back-face vote,
                // which is the right answer for a sliver (the epsilon-ladder rule: never
                // apply an absolute tolerance to a cross product). A mirroring placement
                // reverses the world winding, so the sign is restored here rather than
                // by re-ordering indices.
                occluder._normals[t] = (b - a).Cross(c - a).TryNormalize(Tolerance.Default, out var n)
                    ? (occluder._mirrored ? -n : n)
                    : Vector3d.Zero;
                boxes[t] = Aabb.FromPoints([a, b, c]);
            }
            occluder._bvh = Bvh.Build(boxes);
            return occluder;
        }

        /// <summary>
        /// The most eye-facing normal among the triangles within <paramref name="radius"/>
        /// of <paramref name="point"/>. False when there are none.
        /// </summary>
        public bool LocalNormals(
            in Vector3d point, double radius, List<int> scratch,
            out Vector3d mostFacing, out double bestDot)
        {
            mostFacing = Vector3d.Zero;
            bestDot = double.NegativeInfinity;
            scratch.Clear();
            _bvh.Query(new Aabb(point - new Vector3d(radius, radius, radius),
                                point + new Vector3d(radius, radius, radius)), scratch);
            double radiusSquared = radius * radius;
            foreach (int t in scratch)
            {
                var a = _positions[_indices[t * 3]];
                var b = _positions[_indices[t * 3 + 1]];
                var c = _positions[_indices[t * 3 + 2]];
                if ((Distance3d.ClosestPointOnTriangle(point, a, b, c) - point).LengthSquared > radiusSquared)
                    continue;
                double dot = _normals[t].Dot(_toViewer);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    mostFacing = _normals[t];
                }
            }
            return bestDot > double.NegativeInfinity;
        }

        /// <summary>Does the ray hit any triangle strictly between its origin and its
        /// tip? (t in (0, 1], the direction carrying the reach.)</summary>
        public bool Hits(in Ray3d ray, List<int> scratch)
        {
            if (!ray.Intersects(Bounds))
                return false;
            scratch.Clear();
            _bvh.Query(ray, scratch);
            foreach (int t in scratch)
            {
                if (Intersect3d.RayTriangle(ray,
                        _positions[_indices[t * 3]],
                        _positions[_indices[t * 3 + 1]],
                        _positions[_indices[t * 3 + 2]],
                        out double hit)
                    && hit > 0 && hit <= 1)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The world-space polylines this instance contributes: its feature edges
        /// (exact for a B-Rep part) and, optionally, the display mesh's view-dependent
        /// silhouette of SMOOTH surface — smooth being the point, since a sharp edge is
        /// already in the feature set and would otherwise be drawn twice.
        /// </summary>
        public IEnumerable<(IReadOnlyList<Vector3d> Points, EdgeSource Source)> Edges(
            HiddenLineOptions options)
        {
            var result = new List<(IReadOnlyList<Vector3d>, EdgeSource)>();
            foreach (var chain in FeatureChains())
                result.Add((chain, EdgeSource.Feature));
            if (options.IncludeSilhouette)
            {
                foreach (var chain in SilhouetteChains(options))
                    result.Add((chain, EdgeSource.Silhouette));
            }
            return result;
        }

        /// <summary>
        /// The part's feature edges, chained into polylines.
        ///
        /// <para>Chaining is not tidying — it decides the ANSWER. A feature-edge segment
        /// is the unit a run can be split into, so a rim delivered as 96 separate chords
        /// can only ever change visibility at a chord end, and the dash boundary lands on
        /// whichever sample happened to be nearest instead of on the occluder's edge
        /// (measured on a rim of radius 8 against an occluder edge at x = 5: 4.870 —
        /// exactly the 52.5-degree sample — where the chained form bisects to 5.000).
        /// Endpoints are keyed by EXACT bits, which is sound because consecutive
        /// segments of one edge come from one sampled polyline and share the identical
        /// value; nothing is welded.</para>
        /// </summary>
        private List<IReadOnlyList<Vector3d>> FeatureChains()
        {
            var interned = new Dictionary<Vector3d, int>();
            var points = new List<Vector3d>();
            var segments = new List<(int A, int B)>();
            foreach (var (a, b) in _part.GetFeatureEdges(_quality))
            {
                int ia = Intern(a);
                int ib = Intern(b);
                // Exact-equality test: an edge whose two ends are the same point projects
                // to nothing and would only add a zero-length run.
                if (ia != ib)
                    segments.Add((ia, ib));
            }
            return Chain(segments, points);

            int Intern(in Vector3d local)
            {
                if (interned.TryGetValue(local, out int index))
                    return index;
                interned[local] = index = points.Count;
                points.Add(_world.TransformPoint(local));
                return index;
            }
        }

        /// <summary>
        /// Mesh edges where a SMOOTH surface turns away from the viewer: the two
        /// adjacent faces disagree about which way they face and the crease between them
        /// is below the feature angle. Chained end to end by vertex index (an exact
        /// integer key — nothing to weld), so a cylinder's outline arrives as one
        /// polyline rather than a pile of segments.
        /// </summary>
        private List<IReadOnlyList<Vector3d>> SilhouetteChains(HiddenLineOptions options)
        {
            double flatLimit = Math.PI - options.SharpAngleRadians;
            var segments = new List<(int A, int B)>();
            foreach (var edge in _mesh.Edges)
            {
                if (edge.IsBoundaryEdge || edge.DihedralAngle() < flatLimit)
                    continue;   // boundary or crease: already a feature edge
                // Face normals are read in the mesh's OWN frame against the view
                // direction pulled back into it, so a placed instance needs no
                // inverse-transpose: a rigid or uniformly scaled world transform
                // preserves the sign of a normal-vs-direction dot, and that sign is the
                // whole test.
                double dotA = edge.Face.NormalRaw.Dot(_localToViewer);
                double dotB = edge.Twin.Face.NormalRaw.Dot(_localToViewer);
                // Exact-sign disagreement: the outline is exactly where a smooth
                // surface's facing flips, and a tolerance here would either thicken the
                // outline into a band or lose it entirely.
                if (dotA > 0 == dotB > 0)
                    continue;
                segments.Add((edge.Origin.Index, edge.Destination.Index));
            }
            return Chain(segments, _positions);
        }

        /// <summary>Greedy end-to-end chaining of segments sharing a vertex index,
        /// walking each chain to both of its ends first so an open run comes out whole
        /// rather than split at whichever segment happened to be visited first.</summary>
        private static List<IReadOnlyList<Vector3d>> Chain(
            List<(int A, int B)> segments, IReadOnlyList<Vector3d> positions)
        {
            var adjacency = new Dictionary<int, List<int>>();
            for (int i = 0; i < segments.Count; i++)
            {
                foreach (int end in new[] { segments[i].A, segments[i].B })
                {
                    if (!adjacency.TryGetValue(end, out var list))
                        adjacency[end] = list = [];
                    list.Add(i);
                }
            }

            var used = new bool[segments.Count];
            var chains = new List<IReadOnlyList<Vector3d>>();
            for (int seed = 0; seed < segments.Count; seed++)
            {
                if (used[seed])
                    continue;
                used[seed] = true;
                var chain = new LinkedList<int>();
                chain.AddLast(segments[seed].A);
                chain.AddLast(segments[seed].B);
                Extend(chain, forward: true);
                Extend(chain, forward: false);
                chains.Add([.. chain.Select(v => positions[v])]);

                void Extend(LinkedList<int> nodes, bool forward)
                {
                    while (true)
                    {
                        int end = forward ? nodes.Last!.Value : nodes.First!.Value;
                        int next = -1;
                        if (adjacency.TryGetValue(end, out var candidates))
                        {
                            foreach (int candidate in candidates)
                            {
                                if (!used[candidate])
                                {
                                    next = candidate;
                                    break;
                                }
                            }
                        }
                        if (next < 0)
                            return;
                        used[next] = true;
                        int other = segments[next].A == end ? segments[next].B : segments[next].A;
                        if (forward)
                            nodes.AddLast(other);
                        else
                            nodes.AddFirst(other);
                    }
                }
            }
            return chains;
        }
    }
}
