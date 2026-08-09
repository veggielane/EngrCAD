using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Core.Spatial;
using EngrCAD.Mesh;

namespace EngrCAD.Modeling;

/// <summary>
/// A flat curve laid ON a doubly-curved surface — engraving, a decal outline, a heater track or
/// a space-filling texture that follows the shape it decorates. The surface consumer of
/// <see cref="SpaceFillingCurve"/>, and the one whose limit has to be stated rather than
/// averaged away.
///
/// <para><b>The map is <see cref="MeshLocalParam"/>'s discrete exponential map</b>, which is
/// exact on a plane, within about 2% on a developable tube and within about 5% radially on a 35°
/// sphere cap. A curve conforming through it therefore carries that distortion into its own
/// SPACING — the number a bead width or a pass pitch is chosen from — so this class MEASURES it
/// on the curve that was actually laid rather than quoting the map's published figures:
/// <see cref="SurfaceCurve.MinScale"/>, <see cref="SurfaceCurve.MaxScale"/> and
/// <see cref="SurfaceCurve.Distortion"/> come from comparing each drawn segment's surface length
/// against the flat length it was asked for. A caller that needs a guaranteed pitch reads
/// <c>MinScale</c>; one that needs a guaranteed clearance reads <c>MaxScale</c>.</para>
///
/// <para><b>What a point off the map does, and why it is a BREAK rather than an extrapolation.</b>
/// The exp map covers a geodesic disc around its seed, and a flat curve reaching past that disc
/// has nowhere to go — continuing it would mean inventing surface. So the run BREAKS there and
/// <see cref="SurfaceCurve.UnmappedPoints"/> counts what was dropped; a decoration that silently
/// ran off its patch is exactly the failure this reports instead.</para>
///
/// <para><b>Placement, in one sentence:</b> the map's origin is the seed vertex and its +u axis
/// is the reference direction projected into the seed's tangent plane, so the flat curve's own
/// coordinates are millimetres on the surface measured from there — pass a reference whenever
/// the orientation matters, since without one an arbitrary perpendicular is stable per mesh and
/// means nothing.</para>
/// </summary>
public static class SurfaceDecoration
{
    /// <summary>
    /// Lays a flat polyline onto <paramref name="mesh"/> around <paramref name="seedVertex"/>.
    /// </summary>
    /// <param name="mesh">The surface to decorate. Triangulated internally; vertex indices are
    /// preserved, so <paramref name="seedVertex"/> means the same thing either way.</param>
    /// <param name="seedVertex">Where the flat curve's origin lands.</param>
    /// <param name="points">The flat curve, in surface millimetres from the seed.</param>
    /// <param name="referenceDirection">Projected into the seed's tangent plane to become the
    /// +u axis. Null takes an arbitrary perpendicular — stable, but not meaningful.</param>
    /// <param name="radius">The geodesic radius to map. Null takes <c>reach·√2 + 2·longest
    /// edge</c> — a margin rather than a bound, for two reasons worth stating: a query point is
    /// placed from the TRIANGLE around it, so vertices up to one edge beyond it must be mapped
    /// as well; and <see cref="MeshLocalParam"/>'s radius is a GRAPH distance, which
    /// over-estimates the straight-line one by however indirectly the mesh's edges run — on a
    /// grid whose quads carry one diagonal, half the directions cost the full MANHATTAN distance,
    /// which is where the √2 comes from. Neither is a guarantee, which is why
    /// <see cref="SurfaceCurve.UnmappedPoints"/> is reported: a non-zero count is the signal to
    /// state a radius.</param>
    public static SurfaceCurve Wrap(
        HalfEdgeMesh mesh, int seedVertex, IReadOnlyList<Vector2d> points,
        Vector3d? referenceDirection = null, double? radius = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
            throw new ArgumentException("A decoration needs at least one point.", nameof(points));

        double reach = 0;
        foreach (var p in points)
            reach = Math.Max(reach, p.Length);
        // sqrt(2) covers the worst case a GRAPH distance can be against a straight-line one on a
        // mesh whose edges run along two axes — a Manhattan path — and the two edge lengths
        // cover the triangle a query point is placed from. Both are margins, not bounds; see the
        // parameter's own remarks and UnmappedPoints.
        double mapRadius = radius ?? reach * Math.Sqrt(2) + 2 * LongestEdge(mesh);
        if (!(mapRadius > 0) || !double.IsFinite(mapRadius))
            throw new ArgumentOutOfRangeException(nameof(radius), "The map radius must be positive and finite.");

        var param = MeshLocalParam.Compute(mesh, seedVertex, mapRadius, referenceDirection);
        var lookup = new UvTriangles(mesh, param);

        var runs = new List<IReadOnlyList<Vector3d>>();
        var flatRuns = new List<IReadOnlyList<Vector2d>>();
        int unmapped = AppendRuns(lookup, points, runs, flatRuns);

        if (runs.Count == 0)
        {
            throw new ArgumentException(
                $"No point of this curve landed on the exponential map around vertex {seedVertex} "
                + $"(radius {mapRadius}): the curve reaches {reach} from its origin and the map "
                + "covers less than that, or the seed's patch is smaller than the mesh. Reduce the "
                + "curve, raise the radius, or seed somewhere the surface is bigger.",
                nameof(points));
        }
        return new SurfaceCurve(param, runs, flatRuns, unmapped);
    }

    /// <summary>Lays a space-filling curve onto a surface — the fill form. The curve's points are
    /// used verbatim, so its ACHIEVED spacing is the flat pitch and
    /// <see cref="SurfaceCurve.MinScale"/> says what the surface did to it.</summary>
    public static SurfaceCurve Wrap(
        HalfEdgeMesh mesh, int seedVertex, SpaceFillingCurve curve,
        Vector3d? referenceDirection = null, double? radius = null)
    {
        ArgumentNullException.ThrowIfNull(curve);
        return Wrap(mesh, seedVertex, curve.Points, referenceDirection, radius);
    }

    /// <summary>
    /// Lays a <see cref="Sketch"/>'s outline — a glyph (via <see cref="Shape.Text"/>'s own
    /// sketches), a logo, an engraving pattern — onto <paramref name="mesh"/> around
    /// <paramref name="seedVertex"/>: every loop of every region, each drawn CLOSED and each its
    /// own run (or runs, where it leaves the map). The sketch's coordinates are surface
    /// millimetres from the seed exactly as the flat-polyline overload's are, so the exp map's
    /// distortion is MEASURED on the laid outline (<see cref="SurfaceCurve.MinScale"/>) rather
    /// than assumed. Flattening follows <see cref="Sketch.ToRegions(double)"/>: arcs and béziers
    /// become polylines at <paramref name="chordTolerance"/>.
    /// <para>This wraps a curve ONTO the surface; cutting a groove INTO the solid (engraving,
    /// embossing) is a separate operation and is not what this does — take the laid
    /// <see cref="SurfaceCurve"/> and stroke/offset/boolean it against the body if a groove is
    /// wanted.</para>
    /// </summary>
    public static SurfaceCurve Wrap(
        HalfEdgeMesh mesh, int seedVertex, Sketch sketch,
        Vector3d? referenceDirection = null, double? radius = null,
        double chordTolerance = Sketch.DefaultChordTolerance)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(sketch);

        var loops = new List<IReadOnlyList<Vector2d>>();
        foreach (var region in sketch.ToRegions(chordTolerance))
            foreach (var loop in region.AllLoops())
                loops.Add(Closed(loop));
        if (loops.Count == 0)
            throw new ArgumentException(
                "The sketch enclosed no region to lay onto the surface.", nameof(sketch));

        double reach = 0;
        foreach (var loop in loops)
            foreach (var p in loop)
                reach = Math.Max(reach, p.Length);
        double mapRadius = radius ?? reach * Math.Sqrt(2) + 2 * LongestEdge(mesh);
        if (!(mapRadius > 0) || !double.IsFinite(mapRadius))
            throw new ArgumentOutOfRangeException(nameof(radius), "The map radius must be positive and finite.");

        var param = MeshLocalParam.Compute(mesh, seedVertex, mapRadius, referenceDirection);
        var lookup = new UvTriangles(mesh, param);

        var runs = new List<IReadOnlyList<Vector3d>>();
        var flatRuns = new List<IReadOnlyList<Vector2d>>();
        int unmapped = 0;
        foreach (var loop in loops)
            unmapped += AppendRuns(lookup, loop, runs, flatRuns);

        if (runs.Count == 0)
            throw new ArgumentException(
                $"No point of this sketch landed on the exponential map around vertex {seedVertex} "
                + $"(radius {mapRadius}): the outline reaches {reach} from its origin and the map "
                + "covers less. Reduce the outline, raise the radius, or seed where the surface is bigger.",
                nameof(sketch));

        return new SurfaceCurve(param, runs, flatRuns, unmapped);
    }

    /// <summary>Maps one flat loop's points into surface runs, starting a fresh run at the loop
    /// and BREAKING wherever a point leaves the exp map (the run-splitting the class's contract
    /// promises). Returns the number of dropped points.</summary>
    private static int AppendRuns(
        UvTriangles lookup, IReadOnlyList<Vector2d> points,
        List<IReadOnlyList<Vector3d>> runs, List<IReadOnlyList<Vector2d>> flatRuns)
    {
        int unmapped = 0;
        var current = new List<Vector3d>();
        var flatCurrent = new List<Vector2d>();
        foreach (var uv in points)
        {
            if (lookup.TryMap(uv, out var surface))
            {
                current.Add(surface);
                flatCurrent.Add(uv);
                continue;
            }
            unmapped++;
            if (current.Count > 0)
            {
                runs.Add(current);
                flatRuns.Add(flatCurrent);
                current = [];
                flatCurrent = [];
            }
        }
        if (current.Count > 0)
        {
            runs.Add(current);
            flatRuns.Add(flatCurrent);
        }
        return unmapped;
    }

    /// <summary>Appends the first point to close a loop, unless it already closes.</summary>
    private static IReadOnlyList<Vector2d> Closed(IReadOnlyList<Vector2d> loop) =>
        loop.Count < 2 || loop[0].DistanceTo(loop[^1]) < 1e-9 ? loop : [.. loop, loop[0]];

    /// <summary>The mesh's longest edge, which is the scale the default map radius has to clear:
    /// a point is placed from the triangle containing it, so its vertices are what must be
    /// mapped.</summary>
    private static double LongestEdge(HalfEdgeMesh mesh)
    {
        double longest = 0;
        foreach (var edge in mesh.Edges)
        {
            longest = Math.Max(
                longest,
                mesh.GetPosition(edge.Origin.Index).DistanceTo(mesh.GetPosition(edge.Twin.Origin.Index)));
        }
        return longest;
    }
}

/// <summary>
/// The triangles of a mesh in the exponential map's (u, v) coordinates, with a
/// <see cref="Bvh"/> over their uv boxes: the inverse of the map, which
/// <see cref="MeshLocalParam"/> gives per VERTEX and a decoration needs per POINT.
/// <para>A triangle is usable only when all three of its corners are mapped; the barycentric
/// coordinates of a uv inside it carry straight over to the 3D positions, because a triangle's
/// own map is affine both ways. Where several triangles overlap in uv — which the exp map does
/// not forbid, since it is a parameterization rather than an embedding — the first hit in the
/// BVH's own deterministic item order wins, so the answer is a function of the mesh.</para>
/// </summary>
internal sealed class UvTriangles
{
    private readonly Vector2d[] _a;
    private readonly Vector2d[] _b;
    private readonly Vector2d[] _c;
    private readonly Vector3d[] _pa;
    private readonly Vector3d[] _pb;
    private readonly Vector3d[] _pc;
    private readonly Bvh _bvh;
    private readonly List<int> _hits = [];

    public UvTriangles(HalfEdgeMesh mesh, MeshLocalParam param)
    {
        var (positions, faces) = mesh.Triangulated().ToIndexed();
        var a = new List<Vector2d>();
        var b = new List<Vector2d>();
        var c = new List<Vector2d>();
        var pa = new List<Vector3d>();
        var pb = new List<Vector3d>();
        var pc = new List<Vector3d>();
        var boxes = new List<Aabb>();

        foreach (var face in faces)
        {
            // The triangulated mesh's faces are triangles; a fan over a larger face would need
            // its own uv, which the map does not supply.
            if (face.Length != 3)
                continue;
            if (!param.HasUv(face[0]) || !param.HasUv(face[1]) || !param.HasUv(face[2]))
                continue;

            var ua = param.Uv(face[0]);
            var ub = param.Uv(face[1]);
            var uc = param.Uv(face[2]);
            a.Add(ua);
            b.Add(ub);
            c.Add(uc);
            pa.Add(positions[face[0]]);
            pb.Add(positions[face[1]]);
            pc.Add(positions[face[2]]);

            var box = Aabb.Empty;
            box = box.Union(new Vector3d(ua.X, ua.Y, 0));
            box = box.Union(new Vector3d(ub.X, ub.Y, 0));
            box = box.Union(new Vector3d(uc.X, uc.Y, 0));
            boxes.Add(box);
        }

        _a = [.. a];
        _b = [.. b];
        _c = [.. c];
        _pa = [.. pa];
        _pb = [.. pb];
        _pc = [.. pc];
        _bvh = Bvh.Build(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(boxes));
    }

    public bool TryMap(in Vector2d uv, out Vector3d surface)
    {
        surface = Vector3d.Zero;
        var probe = new Aabb(new Vector3d(uv.X, uv.Y, 0), new Vector3d(uv.X, uv.Y, 0));
        _hits.Clear();
        _bvh.Query(probe, _hits);
        foreach (int t in _hits)
        {
            if (!Barycentric(uv, _a[t], _b[t], _c[t], out double wa, out double wb, out double wc))
                continue;
            surface = _pa[t] * wa + _pb[t] * wb + _pc[t] * wc;
            return true;
        }
        return false;
    }

    /// <summary>Barycentric coordinates of a point in a uv triangle, accepting the CLOSED
    /// triangle. The degeneracy guard is RELATIVE — an area compared against the triangle's own
    /// squared extent — because an absolute epsilon on a cross product is an area threshold and
    /// fails quadratically with scale (the recorded decimator trap); the containment band is
    /// likewise a fraction of the area rather than a length, so a point exactly on a shared edge
    /// is claimed by one of the two triangles that meet there rather than by neither.</summary>
    private static bool Barycentric(
        in Vector2d p, in Vector2d a, in Vector2d b, in Vector2d c,
        out double wa, out double wb, out double wc)
    {
        wa = wb = wc = 0;
        var v0 = b - a;
        var v1 = c - a;
        double area = v0.Cross(v1);
        double scale = Math.Max(v0.LengthSquared, Math.Max(v1.LengthSquared, (c - b).LengthSquared));
        if (Math.Abs(area) <= 1e-13 * scale)
            return false;                       // degenerate in uv: it carries no information

        var v2 = p - a;
        wb = v2.Cross(v1) / area;
        wc = v0.Cross(v2) / area;
        wa = 1 - wb - wc;
        double band = 1e-9;
        return wa >= -band && wb >= -band && wc >= -band;
    }
}

/// <summary>
/// One flat curve laid onto a surface: the runs it survived as, and the DISTORTION the map put
/// into it — see <see cref="SurfaceDecoration"/>.
/// </summary>
public sealed class SurfaceCurve
{
    internal SurfaceCurve(
        MeshLocalParam map,
        IReadOnlyList<IReadOnlyList<Vector3d>> runs,
        IReadOnlyList<IReadOnlyList<Vector2d>> flatRuns,
        int unmappedPoints)
    {
        Map = map;
        Runs = runs;
        UnmappedPoints = unmappedPoints;

        double planar = 0;
        double surface = 0;
        double min = double.PositiveInfinity;
        double max = 0;
        int points = 0;
        int segments = 0;
        for (int r = 0; r < runs.Count; r++)
        {
            points += runs[r].Count;
            for (int i = 1; i < runs[r].Count; i++)
            {
                double flat = flatRuns[r][i].DistanceTo(flatRuns[r][i - 1]);
                double drawn = runs[r][i].DistanceTo(runs[r][i - 1]);
                planar += flat;
                surface += drawn;
                if (flat <= 0)
                    continue;                   // a repeated point says nothing about scale
                double ratio = drawn / flat;
                min = Math.Min(min, ratio);
                max = Math.Max(max, ratio);
                segments++;
            }
        }

        PlanarLength = planar;
        SurfaceLength = surface;
        PointCount = points;
        // A single-point decoration has no segment to measure, so the scales report exactly 1
        // rather than an infinity a reader would have to interpret.
        MinScale = segments > 0 ? min : 1;
        MaxScale = segments > 0 ? max : 1;
        MeanScale = planar > 0 ? surface / planar : 1;
    }

    /// <summary>The exponential map the curve was laid through, kept so a caller can place a
    /// second decoration in the same coordinates.</summary>
    public MeshLocalParam Map { get; }

    /// <summary>The decoration on the surface, broken where the flat curve left the map.</summary>
    public IReadOnlyList<IReadOnlyList<Vector3d>> Runs { get; }

    /// <summary>How many flat points had no surface to land on — the run breaks, counted rather
    /// than smoothed over.</summary>
    public int UnmappedPoints { get; }

    /// <summary>How many points were placed.</summary>
    public int PointCount { get; }

    /// <summary>The flat length of what was drawn (the stretches that landed, measured in the
    /// flat curve's own coordinates).</summary>
    public double PlanarLength { get; }

    /// <summary>The length actually drawn on the surface.</summary>
    public double SurfaceLength { get; }

    /// <summary>The smallest surface-to-flat length ratio over the drawn segments: how much the
    /// map SHRANK the tightest place, which is the number a guaranteed pitch is read from.</summary>
    public double MinScale { get; }

    /// <summary>The largest surface-to-flat length ratio: how much the map STRETCHED the worst
    /// place, which is the number a guaranteed clearance is read from.</summary>
    public double MaxScale { get; }

    /// <summary>The length-weighted mean ratio — <c>SurfaceLength / PlanarLength</c>. Reported
    /// BESIDE the extremes and never instead of them: a mean near 1 over a cap that stretches
    /// one side and shrinks the other says nothing about either.</summary>
    public double MeanScale { get; }

    /// <summary>The worst relative departure from the flat curve's own spacing, as a fraction:
    /// <c>max(MaxScale − 1, 1/MinScale − 1)</c>. This is <see cref="MeshLocalParam"/>'s stated
    /// limit MEASURED on the curve that was actually laid — exact on a plane, a couple of
    /// percent on a developable surface, several on a strongly curved one.</summary>
    public double Distortion => Math.Max(MaxScale - 1, 1 / MinScale - 1);

    /// <summary>The travel between runs, ordered — the SAME linker the 2D and 3D fills use.</summary>
    public PathLinkage Link() => RunLinker.Link(RunLinker.EndsOf(Runs));
}
