using EngrCAD.Core;
using EngrCAD.Core.Spatial;
using EngrCAD.Mesh;

namespace EngrCAD.Ecad;

/// <summary>
/// A point located ON a <see cref="MidSurface"/>: its 3D <see cref="Position"/>, the interpolated
/// surface <see cref="Normal"/>, and the triangulated-mesh <see cref="Face"/> plus barycentric weights
/// it was found in — so a <see cref="LocalExpChart"/> can express it in (u, v) with NO second lookup
/// (the barycentric weights carry straight over, because a triangle's own map is affine both ways).
///
/// <para>This is what a MID pad, a trace control point and a seated component all reduce to on an
/// ARBITRARY surface: a point the routing can measure distances from, a normal the conductor lift is
/// extruded along, and a tangent frame a body sits in — with NO global exp-map chart required.</para>
/// </summary>
/// <param name="Position">The point on the surface (on the located triangle).</param>
/// <param name="Normal">The interpolated (angle-blended vertex) surface normal there.</param>
/// <param name="Face">The triangulated-mesh face it lies in.</param>
/// <param name="Wa">Barycentric weight of the face's first corner.</param>
/// <param name="Wb">Barycentric weight of the face's second corner.</param>
/// <param name="Wc">Barycentric weight of the face's third corner.</param>
public readonly record struct SurfacePoint(
    Vector3d Position, Vector3d Normal, int Face, double Wa, double Wb, double Wc);

/// <summary>
/// The intrinsic model of a MOULDED routing surface — an ARBITRARY triangle mesh with NO required
/// global seed+radius exp-map chart. This is the generalisation that makes MID/LDS routing work on any
/// geometry: a torus, a bumpy blob, a whole moulded shell, a closed surface that a single global exp map
/// would wrap onto itself where it degenerates.
///
/// <para><b>What it owns:</b> the triangulated mesh, its per-vertex normals, and a <see cref="Bvh"/> over
/// the triangles in 3D. From those it answers the three questions the routing asks — <b>where is the
/// nearest surface point</b> (<see cref="Locate"/>, so a caller states a pad's world position and it is
/// snapped to the shell), <b>what tangent frame sits there</b> (<see cref="Frame"/>, so a component is
/// posed on the surface), and <b>what does the surface look like LOCALLY</b> (<see cref="Chart"/>, a
/// small exp-map chart around a point used to measure a geodesic clearance and to report the distortion,
/// exact on a developable patch and honestly banded where curvature concentrates).</para>
///
/// <para><b>The chart is LOCAL and per query, which is the whole point:</b> a clearance is between two
/// features that are close, so it is measured in a small chart around them; a closed surface never wraps
/// because no chart is ever asked to cover the whole part. The single global chart the earlier MID board
/// required is now just the degenerate case where one chart happens to cover every feature — kept for
/// developable patches where it gives exact numbers, but no longer a requirement.</para>
/// </summary>
public sealed class MidSurface
{
    private readonly Vector3d[] _positions;
    private readonly Vector3d[] _normals;
    private readonly int[][] _faces;
    private readonly Bvh _bvh;
    private readonly List<int> _hits = [];

    private MidSurface(HalfEdgeMesh mesh, Vector3d[] positions, Vector3d[] normals, int[][] faces, Bvh bvh)
    {
        Mesh = mesh;
        _positions = positions;
        _normals = normals;
        _faces = faces;
        _bvh = bvh;
    }

    /// <summary>The triangulated routing surface (vertex indices preserved from the input mesh).</summary>
    public HalfEdgeMesh Mesh { get; }

    /// <summary>The triangulated faces (each a triangle of vertex indices).</summary>
    internal IReadOnlyList<int[]> Faces => _faces;

    /// <summary>Per-vertex position and normal, shared with a <see cref="LocalExpChart"/> so a face's
    /// corners map consistently.</summary>
    internal Vector3d[] Positions => _positions;
    internal Vector3d[] Normals => _normals;

    /// <summary>Wraps a mesh as an intrinsic routing surface. Triangulated ONCE (vertex indices
    /// preserved), normals angle-weighted per vertex, a BVH built over the triangles.</summary>
    public static MidSurface Wrap(HalfEdgeMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var tri = mesh.Triangulated();
        var normals = tri.ComputeVertexNormals();
        var (positions, faceList) = tri.ToIndexed();
        var faces = new List<int[]>();
        var boxes = new List<Aabb>();
        foreach (var f in faceList)
        {
            if (f.Length != 3)
                continue;
            faces.Add(f);
            var box = Aabb.Empty
                .Union(positions[f[0]])
                .Union(positions[f[1]])
                .Union(positions[f[2]]);
            boxes.Add(box);
        }
        if (faces.Count == 0)
            throw new ArgumentException("The routing surface has no triangles.", nameof(mesh));
        var bvh = Bvh.Build(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(boxes));
        return new MidSurface(tri, positions, normals, [.. faces], bvh);
    }

    /// <summary>
    /// Snaps a world point onto the surface — the nearest point on the nearest triangle, with the
    /// interpolated normal and the barycentric weights that lift it. This is how a caller places a pad,
    /// a trace control point or a component "at roughly here" on a moulded shell without knowing the
    /// mesh: it lands on the surface exactly.
    /// </summary>
    public SurfacePoint Locate(in Vector3d near)
    {
        var probe = near;
        if (!_bvh.Nearest(near, t => Distance3d.ClosestPointOnTriangle(
                probe, _positions[_faces[t][0]], _positions[_faces[t][1]], _positions[_faces[t][2]])
                .DistanceTo(probe), out int face, out _))
            throw new InvalidOperationException("The routing surface is empty.");
        return At(face, near);
    }

    /// <summary>The surface point in <paramref name="face"/> closest to <paramref name="near"/>.</summary>
    internal SurfacePoint At(int face, in Vector3d near)
    {
        var f = _faces[face];
        var a = _positions[f[0]];
        var b = _positions[f[1]];
        var c = _positions[f[2]];
        var q = Distance3d.ClosestPointOnTriangle(near, a, b, c);
        Barycentric(q, a, b, c, out double wa, out double wb, out double wc);
        var n = _normals[f[0]] * wa + _normals[f[1]] * wb + _normals[f[2]] * wc;
        if (!n.TryNormalize(Tolerance.Default, out var normal))
            normal = TriangleNormal(a, b, c);
        return new SurfacePoint(q, normal, face, wa, wb, wc);
    }

    /// <summary>The surface tangent frame at <paramref name="sp"/> — origin on the surface, Z the
    /// surface normal, X the <paramref name="xHint"/> projected into the tangent plane (a stable
    /// arbitrary perpendicular when omitted). What a seated component's body is posed on.</summary>
    public Frame3d Frame(in SurfacePoint sp, in Vector3d? xHint = null)
    {
        var hint = xHint ?? sp.Normal.ArbitraryPerpendicular(Tolerance.Default);
        return Frame3d.FromZX(sp.Position, sp.Normal, hint);
    }

    /// <summary>The mesh vertex a chart should seed from for <paramref name="sp"/> — the face corner
    /// carrying the most barycentric weight, i.e. the vertex the point is closest to.</summary>
    public int SeedVertex(in SurfacePoint sp)
    {
        var f = _faces[sp.Face];
        return sp.Wa >= sp.Wb ? (sp.Wa >= sp.Wc ? f[0] : f[2]) : (sp.Wb >= sp.Wc ? f[1] : f[2]);
    }

    /// <summary>
    /// A LOCAL exp-map chart around <paramref name="centre"/> out to a geodesic <paramref name="radius"/>
    /// — the geodesic parameterizer a clearance is measured in and a distortion band is read from. Exact
    /// on a developable patch (an isometry), honestly banded where curvature concentrates. Seeded at the
    /// vertex nearest the point; +u along <paramref name="referenceDirection"/> (a stable perpendicular
    /// when omitted, since a distance is orientation-free).
    /// </summary>
    public LocalExpChart Chart(in SurfacePoint centre, double radius, in Vector3d? referenceDirection = null)
    {
        if (!(radius > 0) || !double.IsFinite(radius))
            throw new ArgumentOutOfRangeException(nameof(radius), "A chart needs a positive, finite radius.");
        int seed = SeedVertex(centre);
        var refDir = referenceDirection ?? centre.Normal.ArbitraryPerpendicular(Tolerance.Default);
        var param = MeshLocalParam.Compute(Mesh, seed, radius, refDir);
        return new LocalExpChart(this, param);
    }

    /// <summary>Barycentric weights of a point already ON triangle (a, b, c). Degeneracy is guarded
    /// RELATIVE to the triangle's own squared extent (an absolute epsilon on an area fails quadratically
    /// with scale), and a degenerate triangle falls back to the equal split.</summary>
    internal static void Barycentric(
        in Vector3d p, in Vector3d a, in Vector3d b, in Vector3d c,
        out double wa, out double wb, out double wc)
    {
        var v0 = b - a;
        var v1 = c - a;
        var v2 = p - a;
        double d00 = v0.Dot(v0), d01 = v0.Dot(v1), d11 = v1.Dot(v1);
        double d20 = v2.Dot(v0), d21 = v2.Dot(v1);
        double denom = d00 * d11 - d01 * d01;
        double scale = Math.Max(d00, d11);
        if (Math.Abs(denom) <= 1e-24 * scale * scale)
        {
            wa = wb = wc = 1.0 / 3;
            return;
        }
        wb = (d11 * d20 - d01 * d21) / denom;
        wc = (d00 * d21 - d01 * d20) / denom;
        wa = 1 - wb - wc;
    }

    private static Vector3d TriangleNormal(in Vector3d a, in Vector3d b, in Vector3d c) =>
        (b - a).Cross(c - a).TryNormalize(Tolerance.Default, out var n) ? n : Vector3d.UnitZ;
}

/// <summary>
/// A LOCAL exponential-map chart on a <see cref="MidSurface"/>: the surface flattened into (u, v) around
/// one seed vertex within a geodesic radius, with the map's INVERSE (uv → surface point + normal) AND
/// its FORWARD (a located <see cref="SurfacePoint"/> → uv). It is <see cref="MeshLocalParam"/>'s
/// per-vertex map made per-POINT, exactly as <see cref="SurfaceDecoration"/>'s own inverse is, extended
/// with the forward direction the DRC needs to express a feature's copper in the chart.
///
/// <para><b>The chart is the geodesic-distance approximator</b>: the (u, v) planar distance between two
/// points equals their geodesic distance exactly on a developable patch (the map is an isometry) and
/// differs by the map's local scale where curvature concentrates — which is why the DRC folds the
/// <see cref="ScaleBand"/> into every clearance rather than averaging it away.</para>
/// </summary>
public sealed class LocalExpChart
{
    private readonly MidSurface _surface;
    private readonly MeshLocalParam _param;

    // Inverse (uv -> surface) over the mapped triangles, BVH-indexed in (u, v).
    private readonly Vector2d[] _a;
    private readonly Vector2d[] _b;
    private readonly Vector2d[] _c;
    private readonly Vector3d[] _pa;
    private readonly Vector3d[] _pb;
    private readonly Vector3d[] _pc;
    private readonly Vector3d[] _na;
    private readonly Vector3d[] _nb;
    private readonly Vector3d[] _nc;
    private readonly Bvh _uvBvh;
    private readonly List<int> _hits = [];

    internal LocalExpChart(MidSurface surface, MeshLocalParam param)
    {
        _surface = surface;
        _param = param;

        var positions = surface.Positions;
        var normals = surface.Normals;
        var a = new List<Vector2d>();
        var b = new List<Vector2d>();
        var c = new List<Vector2d>();
        var pa = new List<Vector3d>();
        var pb = new List<Vector3d>();
        var pc = new List<Vector3d>();
        var na = new List<Vector3d>();
        var nb = new List<Vector3d>();
        var nc = new List<Vector3d>();
        var boxes = new List<Aabb>();
        foreach (var f in surface.Faces)
        {
            if (!param.HasUv(f[0]) || !param.HasUv(f[1]) || !param.HasUv(f[2]))
                continue;
            var ua = param.Uv(f[0]);
            var ub = param.Uv(f[1]);
            var uc = param.Uv(f[2]);
            a.Add(ua);
            b.Add(ub);
            c.Add(uc);
            pa.Add(positions[f[0]]);
            pb.Add(positions[f[1]]);
            pc.Add(positions[f[2]]);
            na.Add(normals[f[0]]);
            nb.Add(normals[f[1]]);
            nc.Add(normals[f[2]]);
            boxes.Add(Aabb.Empty
                .Union(new Vector3d(ua.X, ua.Y, 0))
                .Union(new Vector3d(ub.X, ub.Y, 0))
                .Union(new Vector3d(uc.X, uc.Y, 0)));
        }
        _a = [.. a];
        _b = [.. b];
        _c = [.. c];
        _pa = [.. pa];
        _pb = [.. pb];
        _pc = [.. pc];
        _na = [.. na];
        _nb = [.. nb];
        _nc = [.. nc];
        _uvBvh = boxes.Count > 0
            ? Bvh.Build(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(boxes))
            : Bvh.Build(ReadOnlySpan<Aabb>.Empty);
    }

    /// <summary>The per-vertex exp map the chart rides on.</summary>
    public MeshLocalParam Parameterization => _param;

    /// <summary>The seed vertex (u, v) = (0, 0).</summary>
    public int SeedVertex => _param.SeedVertex;

    /// <summary>Whether the chart covers no triangle at all (a seed whose radius reached nothing).</summary>
    public bool IsEmpty => _a.Length == 0;

    /// <summary>
    /// Expresses a located <see cref="SurfacePoint"/> in the chart's (u, v) — the FORWARD map. Returns
    /// false when the point's face is not fully within the chart, i.e. the point falls outside the
    /// covered patch: that is a genuine "this chart cannot see it", never an invented coordinate.
    /// </summary>
    public bool TryProject(in SurfacePoint sp, out Vector2d uv)
    {
        uv = default;
        if ((uint)sp.Face >= (uint)_surface.Faces.Count)
            return false;
        var f = _surface.Faces[sp.Face];
        if (!_param.HasUv(f[0]) || !_param.HasUv(f[1]) || !_param.HasUv(f[2]))
            return false;
        // Recompute the barycentric weights from the point's own position in its face (robust even for a
        // point that was rebuilt without its weights), then carry them onto the (u, v) corners — a
        // triangle's own map is affine both ways.
        var pos = _surface.Positions;
        MidSurface.Barycentric(sp.Position, pos[f[0]], pos[f[1]], pos[f[2]],
            out double wa, out double wb, out double wc);
        uv = _param.Uv(f[0]) * wa + _param.Uv(f[1]) * wb + _param.Uv(f[2]) * wc;
        return true;
    }

    /// <summary>Lifts a chart (u, v) onto the surface — the INVERSE map. Returns false past the mapped
    /// region (a run reaching there BREAKS rather than inventing surface).</summary>
    public bool TryLift(in Vector2d uv, out Vector3d point) => TryLift(uv, out point, out _);

    /// <summary>Lifts a chart (u, v) onto the surface with the surface NORMAL there — what a conductor
    /// ribbon is extruded along.</summary>
    public bool TryLift(in Vector2d uv, out Vector3d point, out Vector3d normal)
    {
        point = Vector3d.Zero;
        normal = Vector3d.UnitZ;
        var probe = new Aabb(new Vector3d(uv.X, uv.Y, 0), new Vector3d(uv.X, uv.Y, 0));
        _hits.Clear();
        _uvBvh.Query(probe, _hits);
        foreach (int t in _hits)
        {
            if (!Barycentric2(uv, _a[t], _b[t], _c[t], out double wa, out double wb, out double wc))
                continue;
            point = _pa[t] * wa + _pb[t] * wb + _pc[t] * wc;
            var n = _na[t] * wa + _nb[t] * wb + _nc[t] * wc;
            normal = n.TryNormalize(Tolerance.Default, out var unit) ? unit : Vector3d.UnitZ;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The local surface-to-parameter scale band over a small cross of half-span <paramref name="span"/>
    /// about <paramref name="uv"/> — <c>[min, max]</c> of (surface length / parameter length). On a
    /// developable patch the two collapse (band ≈ 1); where curvature concentrates they spread, and the
    /// spread is the distortion the DRC folds in. A point with nothing measurable returns <c>[1, 1]</c>.
    /// </summary>
    public (double Min, double Max) ScaleBand(in Vector2d uv, double span)
    {
        double min = double.PositiveInfinity, max = 0;
        void Probe(in Vector2d a, in Vector2d b)
        {
            double flat = a.DistanceTo(b);
            if (!(flat > 0))
                return;
            if (!TryLift(a, out var pa) || !TryLift(b, out var pb))
                return;
            double scale = pa.DistanceTo(pb) / flat;
            min = Math.Min(min, scale);
            max = Math.Max(max, scale);
        }
        double h = Math.Max(span, 1e-9);
        var c = uv;
        Probe(new Vector2d(c.X - h, c.Y), c);
        Probe(c, new Vector2d(c.X + h, c.Y));
        Probe(new Vector2d(c.X, c.Y - h), c);
        Probe(c, new Vector2d(c.X, c.Y + h));
        return max <= 0 ? (1, 1) : (min, max);
    }

    private static bool Barycentric2(
        in Vector2d p, in Vector2d a, in Vector2d b, in Vector2d c,
        out double wa, out double wb, out double wc)
    {
        wa = wb = wc = 0;
        var v0 = b - a;
        var v1 = c - a;
        double area = v0.Cross(v1);
        double scale = Math.Max(v0.LengthSquared, Math.Max(v1.LengthSquared, (c - b).LengthSquared));
        if (Math.Abs(area) <= 1e-13 * scale)
            return false;
        var v2 = p - a;
        wb = v2.Cross(v1) / area;
        wc = v0.Cross(v2) / area;
        wa = 1 - wb - wc;
        const double band = 1e-9;
        return wa >= -band && wb >= -band && wc >= -band;
    }
}

/// <summary>Intrinsic 3D-surface geometry helpers the DRC's broad phase leans on — the CERTIFIED lower
/// bound that a chord never exceeds a geodesic, so a 3D distance proves a surface clearance.</summary>
internal static class SurfaceGeometry
{
    /// <summary>The minimum 3D straight-line distance between two polylines (each ≥ 1 point). This is a
    /// LOWER BOUND on the surface geodesic distance between them (a chord is never longer than a
    /// surface path), so a value at or above a clearance PROVES the clearance whatever the curvature —
    /// the tamper-mesh grow-and-intersect argument, read the certified way.</summary>
    public static double PolylineDistance(IReadOnlyList<Vector3d> a, IReadOnlyList<Vector3d> b)
    {
        double best = double.PositiveInfinity;
        if (a.Count == 1 && b.Count == 1)
            return a[0].DistanceTo(b[0]);
        if (a.Count == 1)
        {
            for (int j = 0; j + 1 < b.Count; j++)
                best = Math.Min(best, PointSegment(a[0], b[j], b[j + 1]));
            return best;
        }
        if (b.Count == 1)
        {
            for (int i = 0; i + 1 < a.Count; i++)
                best = Math.Min(best, PointSegment(b[0], a[i], a[i + 1]));
            return best;
        }
        for (int i = 0; i + 1 < a.Count; i++)
            for (int j = 0; j + 1 < b.Count; j++)
                best = Math.Min(best, SegmentSegment(a[i], a[i + 1], b[j], b[j + 1]));
        return best;
    }

    private static double PointSegment(in Vector3d p, in Vector3d a, in Vector3d b)
    {
        var ab = b - a;
        double len2 = ab.LengthSquared;
        double t = len2 > 0 ? Math.Clamp((p - a).Dot(ab) / len2, 0, 1) : 0;
        return p.DistanceTo(a + ab * t);
    }

    /// <summary>Ericson's segment–segment closest distance (Real-Time Collision Detection), the
    /// standard clamped-parameter form with the degenerate cases handled.</summary>
    private static double SegmentSegment(in Vector3d p1, in Vector3d q1, in Vector3d p2, in Vector3d q2)
    {
        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var r = p1 - p2;
        double a = d1.Dot(d1), e = d2.Dot(d2), f = d2.Dot(r);
        double s, t;
        const double eps = 1e-30;
        if (a <= eps && e <= eps)
            return p1.DistanceTo(p2);
        if (a <= eps)
        {
            s = 0;
            t = Math.Clamp(f / e, 0, 1);
        }
        else
        {
            double c = d1.Dot(r);
            if (e <= eps)
            {
                t = 0;
                s = Math.Clamp(-c / a, 0, 1);
            }
            else
            {
                double bb = d1.Dot(d2);
                double denom = a * e - bb * bb;
                s = denom > eps ? Math.Clamp((bb * f - c * e) / denom, 0, 1) : 0;
                t = (bb * s + f) / e;
                if (t < 0)
                {
                    t = 0;
                    s = Math.Clamp(-c / a, 0, 1);
                }
                else if (t > 1)
                {
                    t = 1;
                    s = Math.Clamp((bb - c) / a, 0, 1);
                }
            }
        }
        var c1 = p1 + d1 * s;
        var c2 = p2 + d2 * t;
        return c1.DistanceTo(c2);
    }
}
