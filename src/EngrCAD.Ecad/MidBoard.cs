using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Mesh;
using EngrCAD.Modeling;

namespace EngrCAD.Ecad;

/// <summary>
/// A <b>MID (moulded interconnect device) board</b>: a conductive circuit routed and seated on a
/// MOULDED, doubly-curved surface rather than on a flat board — the LDS (laser direct structuring)
/// construction, and the flagship of the ECAD campaign's stage 8.
///
/// <para><b>Everything happens in the exp map's (u, v) parameter space.</b> The routing surface is a
/// mesh, and <see cref="MeshLocalParam"/>'s discrete exponential map from a stated origin gives every
/// point of the surface a flat (u, v) coordinate. A pad is a POINT in (u, v) with a net; a
/// <see cref="SurfaceTrace"/> is a polyline in (u, v) with a width. The 2D routing and the 3D DRC
/// (<see cref="Mid3dDrc"/>) run in this parameter space with the SAME grow-and-intersect the flat
/// copper DRC uses (<see cref="PcbDrc"/>), and the surface distortion the map carries is FOLDED into
/// the clearance — never averaged away.</para>
///
/// <para><b>The exp map is exact on a plane, near-exact on a developable surface (a cylinder, a cone)
/// and genuinely distorted where Gaussian curvature concentrates.</b> So the honest failure mode of
/// the 3D DRC is a CONSERVATIVE REFUSAL: a near-tolerance pair on a high-distortion patch is refused
/// with its uncertainty stated, not passed false-precise (the tamper-mesh near-tangency rule). On a
/// developable surface the distortion band collapses and the 3D DRC agrees with the unrolled 2D DRC —
/// the decisive oracle (<c>Mid3dDrcTests</c>).</para>
///
/// <para><b>The inverse of the map</b> — which <see cref="MeshLocalParam"/> gives per VERTEX and a MID
/// board needs per POINT — is a BVH over the mesh triangles in (u, v) plus barycentric interpolation,
/// legal because a triangle's own map is affine both ways. It is the same construction
/// <see cref="SurfaceDecoration"/> uses to lay a decoration onto a surface, extended here to also
/// report the surface NORMAL (which the conductor lift needs) and shared across every pad, trace and
/// probe so the map is computed ONCE and every feature's (u, v) coordinates are consistent — a pad and
/// a trace authored at the same (u, v) lift to the SAME surface point, so a trace's endpoint lands
/// exactly on its pad.</para>
///
/// <para><b>Scope, v1.</b> A SINGLE conductive surface (no multi-shell MID — traces on both the outer
/// and an inner moulded shell are filed). Auto-routing on the surface (a geodesic maze search) is
/// filed too; v1 PLACES traces and VERIFIES them (<see cref="MidRouting"/>). A conformal solder mask /
/// pour on the surface is refused for the distortion reason, exactly as copper pours already refuse
/// curved walls. LDS process specifics (laser activation paths) are out of scope.</para>
/// </summary>
public sealed class MidBoard
{
    private readonly MidSurfaceMap _map;
    private readonly Dictionary<PinRef, string>? _netOfPin;
    private readonly List<MidPad> _pads = [];
    private readonly List<SurfaceTrace> _traces = [];
    private readonly List<MidSeatedComponent> _seated = [];
    private double? _maxDistortion;

    private MidBoard(
        HalfEdgeMesh mesh, int seedVertex, Vector3d referenceDirection, double radius,
        MeshLocalParam parameterization, MidSurfaceMap map, Schematic? schematic)
    {
        Mesh = mesh;
        SeedVertex = seedVertex;
        ReferenceDirection = referenceDirection;
        MapRadius = radius;
        Parameterization = parameterization;
        _map = map;
        Schematic = schematic;
        if (schematic is not null)
        {
            _netOfPin = [];
            foreach (var net in schematic.Nets)
                if (net.Kind is NetKind.Signal or NetKind.Stub)
                    foreach (var pin in net.Pins)
                        _netOfPin[pin] = net.Name;
        }
    }

    /// <summary>The routing surface.</summary>
    public HalfEdgeMesh Mesh { get; }

    /// <summary>The exp map's origin — the vertex the parameterization is seeded from, at (u, v) =
    /// (0, 0).</summary>
    public int SeedVertex { get; }

    /// <summary>The +u direction (projected into the seed's tangent plane), so the (u, v) coordinates
    /// mean the same thing every time — pass a meaningful reference, since an arbitrary perpendicular
    /// is stable but not meaningful.</summary>
    public Vector3d ReferenceDirection { get; }

    /// <summary>The geodesic radius the map covers.</summary>
    public double MapRadius { get; }

    /// <summary>The exp map itself (the per-vertex parameterization).</summary>
    public MeshLocalParam Parameterization { get; }

    /// <summary>The schematic that names the nets, or null when pads carry explicit net names. When
    /// present, <see cref="PlacePin"/> resolves a pad's net from its pin — the one-declaration
    /// identity: a pad's net IS its pin's net.</summary>
    public Schematic? Schematic { get; }

    /// <summary>The placed pads, in placement order.</summary>
    public IReadOnlyList<MidPad> Pads => _pads;

    /// <summary>The routed surface traces, in placement order.</summary>
    public IReadOnlyList<SurfaceTrace> Traces => _traces;

    /// <summary>The seated components, in placement order.</summary>
    public IReadOnlyList<MidSeatedComponent> Seated => _seated;

    /// <summary>
    /// Parameterizes a moulded routing surface by the exp map from <paramref name="seedVertex"/>.
    /// </summary>
    /// <param name="mesh">The routing surface (triangulated internally; vertex indices preserved, so
    /// <paramref name="seedVertex"/> means the same either way).</param>
    /// <param name="seedVertex">Where the (u, v) origin lands.</param>
    /// <param name="referenceDirection">Projected into the seed's tangent plane to become +u — pass a
    /// meaningful direction, since without one the coordinates are stable but arbitrary.</param>
    /// <param name="radius">The geodesic radius to map — the ROUTING PATCH, a real design parameter
    /// (which part of the moulding carries the circuit) and deliberately explicit rather than a
    /// footgun default: on a CLOSED surface (a cylinder, a cone shell) a radius past the far side wraps
    /// the exp map onto itself where it degenerates, and the distortion there is meaningless. State a
    /// radius that covers your features and stays local; <see cref="MaxDistortion"/> reports how
    /// developable that patch turned out.</param>
    /// <param name="schematic">Optional net source. With it, <see cref="PlacePin"/> resolves a pad's
    /// net from the schematic (the one-declaration rule).</param>
    public static MidBoard OnSurface(
        HalfEdgeMesh mesh, int seedVertex, Vector3d referenceDirection,
        double radius, Schematic? schematic = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (!(radius > 0) || !double.IsFinite(radius))
            throw new ArgumentOutOfRangeException(nameof(radius), "The map radius must be positive and finite.");
        var param = MeshLocalParam.Compute(mesh, seedVertex, radius, referenceDirection);
        var map = new MidSurfaceMap(mesh, param);
        return new MidBoard(mesh, seedVertex, referenceDirection, radius, param, map, schematic);
    }

    /// <summary>
    /// The worst departure from length-preservation the parameterization carries over the whole mapped
    /// region — <c>max |edge3DLength / edgeUvLength − 1|</c> over every mapped mesh edge. This is the
    /// number that says how DEVELOPABLE the surface is: ~0 on a plane, a few 1e-4 on a cylinder, and
    /// several percent on a strongly curved cap (the same distortion <see cref="SurfaceCurve"/> reports
    /// on a laid curve, measured here on the map's own edges). The 3D DRC folds this — per feature —
    /// into its clearance, so a caller can read it to know how much the surface will cost the routing.
    /// </summary>
    public double MaxDistortion => _maxDistortion ??= MeasureMaxDistortion();

    /// <summary>Lifts a parameter point onto the surface — the exp-map inverse. Returns false when the
    /// point falls outside the mapped region (a run reaching past the map BREAKS there rather than
    /// inventing surface).</summary>
    public bool TryLift(in Vector2d uv, out Vector3d point) => _map.TryLift(uv, out point, out _);

    /// <summary>Lifts a parameter point onto the surface, reporting the surface NORMAL there (the
    /// interpolated vertex normal of the containing triangle) — what the conductor ribbon is extruded
    /// along.</summary>
    public bool TryLift(in Vector2d uv, out Vector3d point, out Vector3d normal) =>
        _map.TryLift(uv, out point, out normal);

    /// <summary>
    /// The local scale band the map carries in a neighbourhood of <paramref name="uv"/> spanning
    /// <paramref name="span"/> in each direction — <c>[minScale, maxScale]</c> of
    /// (surface length / parameter length), probed on a small cross. On a developable surface the two
    /// are ~equal (the band collapses); where curvature concentrates they spread, and the DRC folds the
    /// spread into its clearance. A point with nothing measurable (off the map) returns <c>[1, 1]</c>.
    /// </summary>
    public (double Min, double Max) LocalScaleBand(in Vector2d uv, double span)
    {
        double min = double.PositiveInfinity, max = 0;
        void Probe(in Vector2d a, in Vector2d b)
        {
            double flat = a.DistanceTo(b);
            if (!(flat > 0))
                return;
            if (!_map.TryLift(a, out var pa, out _) || !_map.TryLift(b, out var pb, out _))
                return;
            double scale = pa.DistanceTo(pb) / flat;
            min = Math.Min(min, scale);
            max = Math.Max(max, scale);
        }
        double h = Math.Max(span, 1e-12);
        var c = uv;
        Probe(new Vector2d(c.X - h, c.Y), c);
        Probe(c, new Vector2d(c.X + h, c.Y));
        Probe(new Vector2d(c.X, c.Y - h), c);
        Probe(c, new Vector2d(c.X, c.Y + h));
        return max <= 0 ? (1, 1) : (min, max);
    }

    // ---- placing pads --------------------------------------------------------

    /// <summary>
    /// Places a net PAD at <paramref name="uv"/> — a small circular land of diameter
    /// <paramref name="landWidth"/> (a SURFACE dimension). The net is explicit here; use
    /// <see cref="PlacePin"/> to resolve it from a schematic pin instead.
    /// </summary>
    /// <param name="net">The net this pad belongs to, or null for a pad on no electrical net (its own
    /// net — it must still clear every other pad, since two floating pads are electrically distinct).</param>
    /// <param name="uv">The land centre in parameter (u, v) coordinates.</param>
    /// <param name="landWidth">The land diameter on the surface (mm).</param>
    /// <param name="source">A name for reports (<c>"R1.1"</c>).</param>
    public MidPad PlacePad(string? net, in Vector2d uv, double landWidth, string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!(landWidth > 0) || !double.IsFinite(landWidth))
            throw new ArgumentOutOfRangeException(nameof(landWidth), "A pad's land width must be positive.");
        if (!_map.TryLift(uv, out _, out _))
            throw new ArgumentException(
                $"Pad '{source}' at parameter ({uv.X:g4}, {uv.Y:g4}) falls outside the exp map around "
                + $"vertex {SeedVertex} (radius {MapRadius}): there is no surface to seat it on. Seed the "
                + "map where the surface reaches it, or raise the radius.",
                nameof(uv));
        var pad = new MidPad(this, net, uv, landWidth, source);
        _pads.Add(pad);
        return pad;
    }

    /// <summary>
    /// Places a pad for a schematic PIN — the one-declaration path: the pad's net is resolved from the
    /// pin through the board's <see cref="Schematic"/> (a Signal/Stub net's name, or null for a
    /// NoConnect / unconnected pin), and its source is the pin's <c>"R1.2"</c> spelling.
    /// </summary>
    /// <exception cref="InvalidOperationException">The board carries no schematic.</exception>
    public MidPad PlacePin(in PinRef pin, in Vector2d uv, double landWidth)
    {
        if (_netOfPin is null)
            throw new InvalidOperationException(
                "This MID board carries no schematic, so a pad cannot be placed by pin. Build it with "
                + "MidBoard.OnSurface(..., schematic: sch), or use PlacePad with an explicit net.");
        string? net = _netOfPin.TryGetValue(pin, out var n) ? n : null;
        return PlacePad(net, uv, landWidth, pin.ToString());
    }

    // ---- placing traces ------------------------------------------------------

    /// <summary>
    /// Routes a surface trace along a parameter (u, v) centre-line — the place-traces API v1 offers
    /// (auto-routing is filed; see <see cref="MidRouting"/>). The width is a PARAMETER-space stroke
    /// width; the SURFACE width the map carries is reported by the returned trace's distortion band. A
    /// trace whose centre-line runs at the same (u, v) as a pad lands EXACTLY on that pad.
    /// </summary>
    public SurfaceTrace PlaceTrace(string? net, IReadOnlyList<Vector2d> centreLine, double width, string source)
    {
        ArgumentNullException.ThrowIfNull(centreLine);
        ArgumentNullException.ThrowIfNull(source);
        if (centreLine.Count < 2)
            throw new ArgumentException("A surface trace needs at least two centre-line points.", nameof(centreLine));
        if (!(width > 0) || !double.IsFinite(width))
            throw new ArgumentOutOfRangeException(nameof(width), "A trace width must be positive.");
        var trace = new SurfaceTrace(this, net, [.. centreLine], width, source);
        _traces.Add(trace);
        return trace;
    }

    // ---- seating a component -------------------------------------------------

    /// <summary>
    /// Seats a catalogue <see cref="HardwareComponent"/> at <paramref name="uv"/> on the surface,
    /// posing its body in the surface's own local frame at that point — the component's seating
    /// convention (its body modeled +Z out of the host with its origin at the seating datum,
    /// <see cref="HardwareComponent.SeatDepth"/> below the surface) TRANSPORTED onto the moulded
    /// surface. The tangent frame's Z is the surface normal there; +X is the exp-map +u direction, so
    /// the seat is oriented by the same coordinates the routing uses.
    /// </summary>
    public MidSeatedComponent Seat(HardwareComponent component, in Vector2d uv)
    {
        ArgumentNullException.ThrowIfNull(component);
        var frame = SeatFrame(uv);
        var seatFrame = component.SeatFrame(new SketchPlane(frame), Vector2d.Zero);
        var body = component.Body.Transform(seatFrame.ToMatrix());
        var seated = new MidSeatedComponent(component, uv, frame, seatFrame, body);
        _seated.Add(seated);
        return seated;
    }

    /// <summary>The surface tangent frame at <paramref name="uv"/> — origin on the surface, Z the
    /// surface normal, X the exp-map +u direction (the routing's own reference). What a seated body is
    /// posed on.</summary>
    public Frame3d SeatFrame(in Vector2d uv)
    {
        if (!_map.TryLift(uv, out var point, out var normal))
            throw new ArgumentException(
                $"Cannot seat at parameter ({uv.X:g4}, {uv.Y:g4}): it falls outside the exp map.",
                nameof(uv));
        // +X = the exp-map +u tangent projected into the surface tangent plane, so the seat's own x
        // axis is the routing's +u. A nearby +u probe gives the direction on the surface.
        var xHint = _map.TryLift(new Vector2d(uv.X + 1e-3, uv.Y), out var ahead, out _)
            ? ahead - point
            : ReferenceDirection;
        return Frame3d.FromZX(point, normal, xHint);
    }

    // ---- the DRC's feature view ----------------------------------------------

    /// <summary>The board's copper as parameter-space features the DRC reasons about — each a region in
    /// (u, v), its net, its source, and the local scale band the exp map carries there. Pads become
    /// discs; traces become stroked centre-lines.</summary>
    internal IReadOnlyList<MidFeature> Features()
    {
        var features = new List<MidFeature>(_pads.Count + _traces.Count);
        foreach (var pad in _pads)
            features.Add(pad.Feature());
        foreach (var trace in _traces)
            features.AddRange(trace.Features());
        return features;
    }

    private double MeasureMaxDistortion()
    {
        double worst = 0;
        foreach (var edge in Mesh.Edges)
        {
            int a = edge.Origin.Index, b = edge.Twin.Origin.Index;
            if (!Parameterization.HasUv(a) || !Parameterization.HasUv(b))
                continue;
            double uvLen = Parameterization.Uv(a).DistanceTo(Parameterization.Uv(b));
            if (!(uvLen > 0))
                continue;
            double len = Mesh.GetPosition(a).DistanceTo(Mesh.GetPosition(b));
            worst = Math.Max(worst, Math.Abs(len / uvLen - 1));
        }
        return worst;
    }
}

/// <summary>
/// One copper pad of a <see cref="MidBoard"/>: a small circular land at a parameter (u, v) point, on
/// a net. It knows its board, so it can report where it lands on the surface and the local scale the
/// map carries there.
/// </summary>
public sealed class MidPad
{
    private readonly MidBoard _board;

    internal MidPad(MidBoard board, string? net, in Vector2d uv, double landWidth, string source)
    {
        _board = board;
        Net = net;
        Parameter = uv;
        LandWidth = landWidth;
        Source = source;
    }

    /// <summary>The net this pad belongs to, or null for a pad on no electrical net.</summary>
    public string? Net { get; }

    /// <summary>The land centre in parameter (u, v).</summary>
    public Vector2d Parameter { get; }

    /// <summary>The land diameter on the surface (mm).</summary>
    public double LandWidth { get; }

    /// <summary>A name for reports.</summary>
    public string Source { get; }

    /// <summary>Where the pad lands on the moulded surface — the exp-map lift of its (u, v). Exact at
    /// the map's origin and an affine interpolation of the containing triangle elsewhere, so a trace
    /// authored to start here lands on exactly this point.</summary>
    public Vector3d SurfacePoint =>
        _board.TryLift(Parameter, out var p) ? p
            : throw new InvalidOperationException($"Pad '{Source}' is off the map.");

    internal MidFeature Feature()
    {
        var region = CurvedRegion2d.Disc(Parameter, LandWidth / 2);
        var band = _board.LocalScaleBand(Parameter, LandWidth / 2);
        return new MidFeature(Net, Source, region, LandWidth, band.Min, band.Max);
    }
}

/// <summary>One component seated on a <see cref="MidBoard"/>: which component, where (its parameter
/// point and the surface tangent frame it sits in), and its posed 3D <see cref="Body"/> ready to be
/// an assembly occurrence.</summary>
/// <param name="Component">The catalogue component.</param>
/// <param name="Parameter">The seating point in parameter (u, v).</param>
/// <param name="SurfaceFrame">The surface tangent frame at the seat (Z = surface normal, X = +u).</param>
/// <param name="SeatFrame">The pose of the component's body (the surface frame dropped
/// <see cref="HardwareComponent.SeatDepth"/> below the surface).</param>
/// <param name="Body">The component body posed on the surface.</param>
public sealed record MidSeatedComponent(
    HardwareComponent Component, Vector2d Parameter, Frame3d SurfaceFrame, Frame3d SeatFrame, Shape Body);

/// <summary>One piece of copper the 3D DRC reasons about, in PARAMETER (u, v) space — a region, its
/// net (the load-bearing tag), a source name, the authored parameter WIDTH (a trace's stroke width or a
/// pad's land diameter), and the local scale band the exp map carries over it (the distortion folded
/// into the clearance). Pads and traces both reduce to this.</summary>
/// <param name="Net">The net, or null for copper on no electrical net (its own net).</param>
/// <param name="Source">A name for reports.</param>
/// <param name="Region">The copper outline in parameter (u, v) coordinates.</param>
/// <param name="Width">The authored parameter width — a trace's stroke width, a pad's land diameter.
/// The surface width is <c>Width × scale</c>; the DRC checks it directly rather than re-measuring the
/// region (round joins never pinch a width, and an opposing-wall measure under-reports on a round
/// cap).</param>
/// <param name="MinScale">The smallest surface-to-parameter length ratio over the region — how much
/// the map SHRANK it (a guaranteed-pitch reading).</param>
/// <param name="MaxScale">The largest — how much the map STRETCHED it (a guaranteed-clearance reading).</param>
internal readonly record struct MidFeature(
    string? Net, string Source, CurvedRegion2d Region, double Width, double MinScale, double MaxScale);

/// <summary>
/// The exp-map inverse (u, v) → surface point + normal — <see cref="MeshLocalParam"/> gives the map
/// per VERTEX and a MID board needs it per POINT. A BVH over the mesh triangles in (u, v) plus
/// barycentric interpolation, exactly as <see cref="SurfaceDecoration"/>'s own (internal)
/// <c>UvTriangles</c> lifts a decoration, extended here to also interpolate the surface NORMAL (which
/// the conductor lift needs and which the decoration path does not report). Computed ONCE per board, so
/// every pad, trace and probe reads one consistent map.
/// </summary>
internal sealed class MidSurfaceMap
{
    private readonly Vector2d[] _a;
    private readonly Vector2d[] _b;
    private readonly Vector2d[] _c;
    private readonly Vector3d[] _pa;
    private readonly Vector3d[] _pb;
    private readonly Vector3d[] _pc;
    private readonly Vector3d[] _na;
    private readonly Vector3d[] _nb;
    private readonly Vector3d[] _nc;
    private readonly Core.Spatial.Bvh _bvh;

    public MidSurfaceMap(HalfEdgeMesh mesh, MeshLocalParam param)
    {
        var triangulated = mesh.Triangulated();
        var normals = triangulated.ComputeVertexNormals();
        var (positions, faces) = triangulated.ToIndexed();

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

        foreach (var face in faces)
        {
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
            na.Add(normals[face[0]]);
            nb.Add(normals[face[1]]);
            nc.Add(normals[face[2]]);
            var box = Aabb.Empty
                .Union(new Vector3d(ua.X, ua.Y, 0))
                .Union(new Vector3d(ub.X, ub.Y, 0))
                .Union(new Vector3d(uc.X, uc.Y, 0));
            boxes.Add(box);
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
        _bvh = Core.Spatial.Bvh.Build(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(boxes));
    }

    public bool TryLift(in Vector2d uv, out Vector3d point, out Vector3d normal)
    {
        point = Vector3d.Zero;
        normal = Vector3d.UnitZ;
        var probe = new Aabb(new Vector3d(uv.X, uv.Y, 0), new Vector3d(uv.X, uv.Y, 0));
        var hits = new List<int>();
        _bvh.Query(probe, hits);
        foreach (int t in hits)
        {
            if (!Barycentric(uv, _a[t], _b[t], _c[t], out double wa, out double wb, out double wc))
                continue;
            point = _pa[t] * wa + _pb[t] * wb + _pc[t] * wc;
            var n = _na[t] * wa + _nb[t] * wb + _nc[t] * wc;
            normal = n.TryNormalize(Tolerance.Default, out var unit) ? unit : Vector3d.UnitZ;
            return true;
        }
        return false;
    }

    /// <summary>Barycentric coordinates of a point in a uv triangle, accepting the CLOSED triangle —
    /// the degeneracy guard is RELATIVE (an area against the triangle's own squared extent) and the
    /// containment band is a fraction of the area, so a point on a shared edge is claimed by one of the
    /// two triangles that meet there, and both give the same surface point (the shared edge). The same
    /// rule <see cref="SurfaceDecoration"/>'s inverse uses.</summary>
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
            return false;
        var v2 = p - a;
        wb = v2.Cross(v1) / area;
        wc = v0.Cross(v2) / area;
        wa = 1 - wb - wc;
        const double band = 1e-9;
        return wa >= -band && wb >= -band && wc >= -band;
    }
}
