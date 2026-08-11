using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Mesh;
using EngrCAD.Modeling;

namespace EngrCAD.Ecad;

/// <summary>
/// A <b>MID (moulded interconnect device) board</b>: a conductive circuit routed and seated on a
/// MOULDED, doubly-curved surface rather than on a flat board — the LDS (laser direct structuring)
/// construction, and the flagship of the ECAD campaign's stage 9.
///
/// <para><b>It works on ANY surface, not one exp-map chart.</b> The routing surface is an arbitrary
/// triangle mesh — a torus, a bumpy blob, a whole moulded shell, a closed surface a single global exp
/// map would wrap onto itself. A <see cref="MidSurface"/> owns it and answers three questions
/// intrinsically: where the nearest surface point is (so a pad states its world position and snaps to
/// the shell), what tangent frame sits there (so a component poses on the surface), and what the surface
/// does LOCALLY (a small exp-map <see cref="LocalExpChart"/> around a point, the geodesic-distance
/// approximator the DRC measures a clearance in and reads the distortion from). No feature depends on a
/// chart covering the whole part; every chart is local and per query.</para>
///
/// <para><b>Two ways to place features, one surface model.</b>
/// <list type="bullet">
/// <item><see cref="OnMesh"/> — the INTRINSIC mode. Pads, traces and seats are placed at world
/// positions snapped to the surface; the DRC measures clearances as geodesic surface distances through
/// LOCAL charts built around each pair. This is what works on any geometry, including closed
/// surfaces.</item>
/// <item><see cref="OnSurface"/> — the GLOBAL-CHART mode. A single exp map from a seed+radius covers a
/// developable patch, and features are authored in its (u, v). Where one chart is exact (a flat or
/// developable patch) this gives EXACT numbers — the decisive developable oracle, a cylinder's 3D DRC
/// equal to its unrolled flat sheet's bit for bit — so it is kept for that case; it is not required, and
/// on a closed or strongly-curved part the intrinsic mode is the one to use.</item>
/// </list></para>
///
/// <para><b>The honest failure mode is a CONSERVATIVE REFUSAL.</b> The exp map (any chart's) is exact on
/// a plane, near-exact on a developable surface and genuinely distorted where Gaussian curvature
/// concentrates. So the 3D DRC (<see cref="Mid3dDrc"/>) is THREE-valued: a near-tolerance pair on a
/// high-distortion patch is REFUSED with its uncertainty stated rather than passed false-precise (the
/// tamper-mesh near-tangency rule). It does NOT mean "exact everywhere" — the distortion is real; it
/// means the machinery runs and verifies on any surface and is honest about what it cannot certify.</para>
///
/// <para><b>Scope, v1.</b> A SINGLE conductive surface (no multi-shell MID — traces on both the outer and
/// an inner moulded shell are filed). Auto-routing on the surface (a geodesic maze search) is filed too;
/// v1 PLACES traces (as geodesics, <see cref="MidRouting"/>) and VERIFIES them. A conformal solder mask /
/// pour on the surface is refused for the distortion reason, exactly as copper pours already refuse
/// curved walls. LDS process specifics (laser activation paths) are out of scope.</para>
/// </summary>
public sealed class MidBoard
{
    private readonly Dictionary<PinRef, string>? _netOfPin;
    private readonly List<MidPad> _pads = [];
    private readonly List<SurfaceTrace> _traces = [];
    private readonly List<MidSeatedComponent> _seated = [];
    private double? _maxDistortion;

    private MidBoard(MidSurface surface, LocalExpChart? globalChart, int seedVertex, Vector3d referenceDirection, double radius, Schematic? schematic)
    {
        Surface = surface;
        _globalChart = globalChart;
        SeedVertex = seedVertex;
        ReferenceDirection = referenceDirection;
        MapRadius = radius;
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

    private readonly LocalExpChart? _globalChart;

    /// <summary>The intrinsic surface model — the mesh, its normals and the locate/frame/chart machinery
    /// every feature reads.</summary>
    public MidSurface Surface { get; }

    /// <summary>The routing surface (triangulated; vertex indices preserved).</summary>
    public HalfEdgeMesh Mesh => Surface.Mesh;

    /// <summary>True for a board built by <see cref="OnMesh"/> (no global chart): features are surface
    /// points and the DRC measures geodesic distances through local charts. False for a
    /// <see cref="OnSurface"/> global-chart board.</summary>
    public bool IsIntrinsic => _globalChart is null;

    /// <summary>The global chart's origin (the vertex the parameterization is seeded from). −1 for an
    /// intrinsic board.</summary>
    public int SeedVertex { get; }

    /// <summary>The global chart's +u direction. Meaningless (zero) for an intrinsic board.</summary>
    public Vector3d ReferenceDirection { get; }

    /// <summary>The global chart's geodesic radius. Not applicable (∞) for an intrinsic board.</summary>
    public double MapRadius { get; }

    /// <summary>The global exp map, or null for an intrinsic board.</summary>
    public LocalExpChart? GlobalChart => _globalChart;

    /// <summary>The schematic that names the nets, or null when pads carry explicit net names.</summary>
    public Schematic? Schematic { get; }

    /// <summary>The placed pads, in placement order.</summary>
    public IReadOnlyList<MidPad> Pads => _pads;

    /// <summary>The routed surface traces, in placement order.</summary>
    public IReadOnlyList<SurfaceTrace> Traces => _traces;

    /// <summary>The seated components, in placement order.</summary>
    public IReadOnlyList<MidSeatedComponent> Seated => _seated;

    // ---- construction --------------------------------------------------------

    /// <summary>
    /// Wraps a moulded mesh for INTRINSIC routing — the general case, working on ANY geometry (a torus, a
    /// bumpy blob, a closed shell). No seed or radius: pads, traces and seats are placed at world
    /// positions and snapped to the surface, and the DRC measures geodesic clearances through local
    /// charts built where they are needed.
    /// </summary>
    public static MidBoard OnMesh(HalfEdgeMesh mesh, Schematic? schematic = null)
    {
        var surface = MidSurface.Wrap(mesh);
        return new MidBoard(surface, null, -1, Vector3d.Zero, double.PositiveInfinity, schematic);
    }

    /// <summary>
    /// Parameterizes a developable routing patch by ONE exp map from <paramref name="seedVertex"/> — the
    /// global-chart mode, kept for the case where a single chart is exact (a flat or developable patch,
    /// where it gives the exact developable oracle). On a closed or strongly-curved part use
    /// <see cref="OnMesh"/> instead: a global chart there wraps onto itself where it degenerates.
    /// </summary>
    /// <param name="radius">The geodesic radius to map — a real design parameter (which part of the
    /// moulding carries the circuit) and deliberately explicit, since a footgun default that mapped a
    /// closed surface past its far side would wrap. State one that covers your features and stays
    /// local.</param>
    public static MidBoard OnSurface(
        HalfEdgeMesh mesh, int seedVertex, Vector3d referenceDirection,
        double radius, Schematic? schematic = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (!(radius > 0) || !double.IsFinite(radius))
            throw new ArgumentOutOfRangeException(nameof(radius), "The map radius must be positive and finite.");
        var surface = MidSurface.Wrap(mesh);
        var param = MeshLocalParam.Compute(surface.Mesh, seedVertex, radius, referenceDirection);
        var chart = new LocalExpChart(surface, param);
        return new MidBoard(surface, chart, seedVertex, referenceDirection, radius, schematic);
    }

    // ---- distortion ----------------------------------------------------------

    /// <summary>
    /// The worst departure from length-preservation the routing carries. On a GLOBAL-chart board this is
    /// the exp map's worst edge distortion over the whole mapped patch (<c>max |edge3DLength /
    /// edgeUvLength − 1|</c>); on an INTRINSIC board there is no single chart, so it is the worst LOCAL
    /// distortion measured around any placed feature — computed per region rather than assuming one chart
    /// covers everything. ~0 on a plane, a few 1e-4 on a developable surface, several percent on a
    /// strongly curved cap.
    /// </summary>
    public double MaxDistortion => _maxDistortion ??= MeasureMaxDistortion();

    private double MeasureMaxDistortion()
    {
        if (_globalChart is { } chart)
        {
            double worst = 0;
            var param = chart.Parameterization;
            foreach (var edge in Mesh.Edges)
            {
                int a = edge.Origin.Index, b = edge.Twin.Origin.Index;
                if (!param.HasUv(a) || !param.HasUv(b))
                    continue;
                double uvLen = param.Uv(a).DistanceTo(param.Uv(b));
                if (!(uvLen > 0))
                    continue;
                double len = Mesh.GetPosition(a).DistanceTo(Mesh.GetPosition(b));
                worst = Math.Max(worst, Math.Abs(len / uvLen - 1));
            }
            return worst;
        }

        // Intrinsic: the worst local distortion around any feature.
        double d = 0;
        foreach (var pad in _pads)
            d = Math.Max(d, LocalDistortion(pad.Located, pad.LandWidth));
        foreach (var trace in _traces)
            d = Math.Max(d, trace.Distortion);
        return d;
    }

    /// <summary>The local distortion (band spread) a small chart around a surface point carries — the
    /// distortion "per region" an intrinsic board reports instead of one global figure.</summary>
    internal double LocalDistortion(in SurfacePoint at, double featureSize)
    {
        var (min, max) = LocalScaleBandAround(at, featureSize, 0);
        return Math.Max(max - 1, 1 / min - 1);
    }

    /// <summary>The local surface-to-parameter scale band a small exp-map chart around a surface point
    /// carries — <c>[min, max]</c>. The chart's radius clears the feature and a few mesh edges, so the
    /// band is never starved; probed at the point over the feature's own extent.</summary>
    internal (double Min, double Max) LocalScaleBandAround(in SurfacePoint at, double featureSize, double extent)
    {
        double radius = ChartRadius(featureSize) + extent;
        var chart = Surface.Chart(at, radius);
        if (chart.IsEmpty || !chart.TryProject(at, out var uv))
            return (1, 1);
        return chart.ScaleBand(uv, Math.Max(featureSize, radius * 0.25));
    }

    /// <summary>A generous local-chart radius for measuring around a feature of the given size — a few
    /// mesh edges at least, so the chart is never starved, and comfortably above the feature so the scale
    /// band has room to spread.</summary>
    internal double ChartRadius(double featureSize) =>
        Math.Max(featureSize * 6, LongestEdge() * 4);

    private double? _longestEdge;

    internal double LongestEdge()
    {
        if (_longestEdge is { } cached)
            return cached;
        double longest = 0;
        foreach (var edge in Mesh.Edges)
            longest = Math.Max(longest,
                Mesh.GetPosition(edge.Origin.Index).DistanceTo(Mesh.GetPosition(edge.Twin.Origin.Index)));
        return (_longestEdge = longest).Value;
    }

    // ---- global-chart lift / frame (OnSurface only) --------------------------

    /// <summary>Lifts a global-chart (u, v) onto the surface. Throws on an intrinsic board (there is no
    /// global chart). Returns false when the point falls outside the mapped patch.</summary>
    public bool TryLift(in Vector2d uv, out Vector3d point) => RequireChart().TryLift(uv, out point);

    /// <summary>Lifts a global-chart (u, v) onto the surface with the normal there.</summary>
    public bool TryLift(in Vector2d uv, out Vector3d point, out Vector3d normal) =>
        RequireChart().TryLift(uv, out point, out normal);

    /// <summary>The local scale band the global chart carries around a (u, v).</summary>
    public (double Min, double Max) LocalScaleBand(in Vector2d uv, double span) =>
        RequireChart().ScaleBand(uv, span);

    /// <summary>The surface tangent frame at a global-chart (u, v).</summary>
    public Frame3d SeatFrame(in Vector2d uv)
    {
        var chart = RequireChart();
        if (!chart.TryLift(uv, out var point, out var normal))
            throw new ArgumentException(
                $"Cannot seat at parameter ({uv.X:g4}, {uv.Y:g4}): it falls outside the exp map.", nameof(uv));
        var xHint = chart.TryLift(new Vector2d(uv.X + 1e-3, uv.Y), out var ahead)
            ? ahead - point : ReferenceDirection;
        return Frame3d.FromZX(point, normal, xHint);
    }

    private LocalExpChart RequireChart() => _globalChart ?? throw new InvalidOperationException(
        "This MID board is intrinsic (built with MidBoard.OnMesh), so it has no global exp-map chart and "
        + "no (u, v) coordinates. Place features at world positions instead (PlacePad/PlaceTrace/Seat "
        + "taking a Vector3d) and read them as surface points.");

    // ---- intrinsic locate ----------------------------------------------------

    /// <summary>Snaps a world point onto the surface — where an intrinsic pad, trace point or seat lands.</summary>
    public SurfacePoint Locate(in Vector3d near) => Surface.Locate(near);

    // ---- placing pads --------------------------------------------------------

    /// <summary>Places a net PAD at a global-chart <paramref name="uv"/> — a small circular land of
    /// diameter <paramref name="landWidth"/> (a surface dimension). Global-chart board only.</summary>
    public MidPad PlacePad(string? net, in Vector2d uv, double landWidth, string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        RequireLand(landWidth);
        var chart = RequireChart();
        if (!chart.TryLift(uv, out var point, out var normal))
            throw new ArgumentException(
                $"Pad '{source}' at parameter ({uv.X:g4}, {uv.Y:g4}) falls outside the exp map around "
                + $"vertex {SeedVertex} (radius {MapRadius}): there is no surface to seat it on. Seed the "
                + "map where the surface reaches it, or raise the radius.", nameof(uv));
        var located = Surface.Locate(point) with { Position = point, Normal = normal };
        var pad = new MidPad(this, net, located, landWidth, source, uv);
        _pads.Add(pad);
        return pad;
    }

    /// <summary>Places a net PAD at a WORLD position, snapped onto the surface — the intrinsic path,
    /// working on any geometry. <paramref name="near"/> need only be near the surface; it lands on the
    /// nearest point.</summary>
    public MidPad PlacePad(string? net, in Vector3d near, double landWidth, string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        RequireLand(landWidth);
        var located = Surface.Locate(near);
        var pad = new MidPad(this, net, located, landWidth, source, null);
        _pads.Add(pad);
        return pad;
    }

    /// <summary>Places a pad for a schematic PIN in global-chart (u, v) — the one-declaration path: the
    /// pad's net is resolved from the pin through the board's <see cref="Schematic"/>.</summary>
    public MidPad PlacePin(in PinRef pin, in Vector2d uv, double landWidth) =>
        PlacePad(NetOfPin(pin), uv, landWidth, pin.ToString());

    /// <summary>Places a pad for a schematic PIN at a world position (intrinsic).</summary>
    public MidPad PlacePin(in PinRef pin, in Vector3d near, double landWidth) =>
        PlacePad(NetOfPin(pin), near, landWidth, pin.ToString());

    private string? NetOfPin(in PinRef pin)
    {
        if (_netOfPin is null)
            throw new InvalidOperationException(
                "This MID board carries no schematic, so a pad cannot be placed by pin. Build it with a "
                + "schematic (MidBoard.OnMesh(mesh, sch) / OnSurface(..., schematic: sch)), or use PlacePad "
                + "with an explicit net.");
        return _netOfPin.TryGetValue(pin, out var n) ? n : null;
    }

    private static void RequireLand(double landWidth)
    {
        if (!(landWidth > 0) || !double.IsFinite(landWidth))
            throw new ArgumentOutOfRangeException(nameof(landWidth), "A pad's land width must be positive.");
    }

    // ---- placing traces ------------------------------------------------------

    /// <summary>Routes a surface trace along a global-chart (u, v) centre-line. Global-chart board
    /// only.</summary>
    public SurfaceTrace PlaceTrace(string? net, IReadOnlyList<Vector2d> centreLine, double width, string source)
    {
        ArgumentNullException.ThrowIfNull(centreLine);
        ArgumentNullException.ThrowIfNull(source);
        RequireChart();
        if (centreLine.Count < 2)
            throw new ArgumentException("A surface trace needs at least two centre-line points.", nameof(centreLine));
        RequireWidth(width);
        var trace = SurfaceTrace.OnChart(this, net, [.. centreLine], width, source);
        _traces.Add(trace);
        return trace;
    }

    /// <summary>Routes a surface trace along a SURFACE centre-line — a polyline of points already ON the
    /// mesh (a geodesic path from <see cref="MidRouting"/>, or caller control points snapped to the
    /// surface). The intrinsic path: the lift and the length are correct on any surface, and the
    /// distortion is measured locally.</summary>
    public SurfaceTrace PlaceSurfaceTrace(string? net, IReadOnlyList<SurfacePoint> centreLine, double width, string source)
    {
        ArgumentNullException.ThrowIfNull(centreLine);
        ArgumentNullException.ThrowIfNull(source);
        if (centreLine.Count < 2)
            throw new ArgumentException("A surface trace needs at least two centre-line points.", nameof(centreLine));
        RequireWidth(width);
        var trace = SurfaceTrace.OnSurface(this, net, [.. centreLine], width, source);
        _traces.Add(trace);
        return trace;
    }

    private static void RequireWidth(double width)
    {
        if (!(width > 0) || !double.IsFinite(width))
            throw new ArgumentOutOfRangeException(nameof(width), "A trace width must be positive.");
    }

    // ---- seating a component -------------------------------------------------

    /// <summary>Seats a catalogue <see cref="HardwareComponent"/> at a global-chart <paramref name="uv"/>
    /// on the surface, posing its body in the surface's own tangent frame. Global-chart board only.</summary>
    public MidSeatedComponent Seat(HardwareComponent component, in Vector2d uv)
    {
        ArgumentNullException.ThrowIfNull(component);
        var frame = SeatFrame(uv);
        return SeatBody(component, component.Body, frame);
    }

    /// <summary>Seats a catalogue <see cref="HardwareComponent"/> at a WORLD position, snapped onto the
    /// surface — the intrinsic path, working on any geometry. The tangent frame's Z is the surface normal
    /// there; +X is <paramref name="xHint"/> projected in (or a stable perpendicular).</summary>
    public MidSeatedComponent Seat(HardwareComponent component, in Vector3d near, in Vector3d? xHint = null)
    {
        ArgumentNullException.ThrowIfNull(component);
        var located = Surface.Locate(near);
        var frame = Surface.Frame(located, xHint);
        return SeatBody(component, component.Body, frame);
    }

    /// <summary>Seats an arbitrary <see cref="Shape"/> body at a WORLD position on the surface — the way
    /// a non-catalogue part (an MCU, an LED, a connector modelled as a small solid) is placed on a
    /// moulded shell. The body is posed in the surface tangent frame at the located point (Z = normal,
    /// +X = <paramref name="xHint"/> projected in), so it is modeled +Z out of the surface with its
    /// seating datum at its own origin.</summary>
    public MidSeatedComponent Seat(Shape body, in Vector3d near, in Vector3d? xHint = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        var located = Surface.Locate(near);
        var frame = Surface.Frame(located, xHint);
        var posed = body.Transform(frame.ToMatrix());
        var seated = new MidSeatedComponent(null, default, frame, frame, posed);
        _seated.Add(seated);
        return seated;
    }

    private MidSeatedComponent SeatBody(HardwareComponent component, Shape body, in Frame3d surfaceFrame)
    {
        var seatFrame = component.SeatFrame(new SketchPlane(surfaceFrame), Vector2d.Zero);
        var posed = body.Transform(seatFrame.ToMatrix());
        var seated = new MidSeatedComponent(component, Vector2d.Zero, surfaceFrame, seatFrame, posed);
        _seated.Add(seated);
        return seated;
    }

    // ---- the DRC's feature view ----------------------------------------------

    /// <summary>The board's copper as global-chart parameter-space features (global board only).</summary>
    internal IReadOnlyList<MidFeature> Features()
    {
        var features = new List<MidFeature>(_pads.Count + _traces.Count);
        foreach (var pad in _pads)
            features.Add(pad.ChartFeature());
        foreach (var trace in _traces)
            features.AddRange(trace.ChartFeatures());
        return features;
    }

    /// <summary>The board's copper as intrinsic SURFACE features — a centre-line of surface points and a
    /// copper width, the footprint the geodesic DRC measures.</summary>
    internal IReadOnlyList<MidSurfaceFeature> SurfaceFeatures()
    {
        var features = new List<MidSurfaceFeature>(_pads.Count + _traces.Count);
        foreach (var pad in _pads)
            features.Add(pad.SurfaceFeature());
        foreach (var trace in _traces)
            features.AddRange(trace.SurfaceFeatures());
        return features;
    }
}

/// <summary>
/// One copper pad of a <see cref="MidBoard"/>: a small circular land at a surface point, on a net. On a
/// global-chart board it also carries the authored (u, v); on an intrinsic board it is a surface point
/// with no parameter.
/// </summary>
public sealed class MidPad
{
    private readonly MidBoard _board;
    private readonly SurfacePoint _located;
    private readonly Vector2d? _parameter;

    internal MidPad(MidBoard board, string? net, in SurfacePoint located, double landWidth, string source, Vector2d? parameter)
    {
        _board = board;
        _located = located;
        _parameter = parameter;
        Net = net;
        LandWidth = landWidth;
        Source = source;
    }

    /// <summary>The net this pad belongs to, or null for a pad on no electrical net.</summary>
    public string? Net { get; }

    /// <summary>The land diameter on the surface (mm).</summary>
    public double LandWidth { get; }

    /// <summary>A name for reports.</summary>
    public string Source { get; }

    /// <summary>Where the pad lands on the moulded surface — its 3D point. On both modes a trace authored
    /// to start here lands on exactly this point.</summary>
    public Vector3d SurfacePoint => _located.Position;

    /// <summary>The surface normal at the pad.</summary>
    public Vector3d Normal => _located.Normal;

    /// <summary>The located surface point (position, normal, face, barycentric).</summary>
    internal SurfacePoint Located => _located;

    /// <summary>The pad's authored (u, v) on a global-chart board. Throws on an intrinsic pad.</summary>
    public Vector2d Parameter => _parameter ?? throw new InvalidOperationException(
        $"Pad '{Source}' was placed intrinsically (at a world position), so it has no (u, v) parameter.");

    /// <summary>Whether the pad carries a global-chart (u, v).</summary>
    public bool HasParameter => _parameter is not null;

    internal MidFeature ChartFeature()
    {
        var uv = Parameter;
        var region = CurvedRegion2d.Disc(uv, LandWidth / 2);
        var band = _board.LocalScaleBand(uv, LandWidth / 2);
        return new MidFeature(Net, Source, region, LandWidth, band.Min, band.Max);
    }

    internal MidSurfaceFeature SurfaceFeature() =>
        new(Net, Source, LandWidth, [_located]);
}

/// <summary>One component seated on a <see cref="MidBoard"/>: which component (null for a raw-body seat),
/// where, the surface tangent frame, the posed body pose and the body itself.</summary>
/// <param name="Component">The catalogue component, or null for a raw <see cref="Shape"/> seat.</param>
/// <param name="Parameter">The seating point in global-chart (u, v), or (0, 0) for an intrinsic seat.</param>
/// <param name="SurfaceFrame">The surface tangent frame at the seat (Z = surface normal, X = +u).</param>
/// <param name="SeatFrame">The pose of the component's body (the surface frame dropped
/// <see cref="HardwareComponent.SeatDepth"/> below the surface for a catalogue part).</param>
/// <param name="Body">The component body posed on the surface.</param>
public sealed record MidSeatedComponent(
    HardwareComponent? Component, Vector2d Parameter, Frame3d SurfaceFrame, Frame3d SeatFrame, Shape Body);

/// <summary>One piece of copper the GLOBAL-chart 3D DRC reasons about, in PARAMETER (u, v) space — a
/// region, its net, a source name, the authored parameter WIDTH, and the local scale band the exp map
/// carries over it.</summary>
internal readonly record struct MidFeature(
    string? Net, string Source, CurvedRegion2d Region, double Width, double MinScale, double MaxScale);

/// <summary>One piece of copper the INTRINSIC 3D DRC reasons about — a centre-line of surface points and
/// a copper width. A pad is a one-point centre-line (a disc); a trace is its polyline (a stroke). The
/// geodesic DRC measures clearance between these through local charts built around each pair.</summary>
internal sealed class MidSurfaceFeature
{
    public MidSurfaceFeature(string? net, string source, double width, IReadOnlyList<SurfacePoint> centreLine)
    {
        Net = net;
        Source = source;
        Width = width;
        CentreLine = centreLine;
        var box = Aabb.Empty;
        foreach (var p in centreLine)
            box = box.Union(p.Position);
        Bounds = box.Expanded(width / 2);
        var poly = new Vector3d[centreLine.Count];
        for (int i = 0; i < centreLine.Count; i++)
            poly[i] = centreLine[i].Position;
        Polyline = poly;
    }

    public string? Net { get; }
    public string Source { get; }

    /// <summary>The copper width (land diameter for a pad, trace width for a trace).</summary>
    public double Width { get; }

    /// <summary>The centre-line of surface points (one for a pad).</summary>
    public IReadOnlyList<SurfacePoint> CentreLine { get; }

    /// <summary>The centre-line's 3D polyline (its positions).</summary>
    public IReadOnlyList<Vector3d> Polyline { get; }

    /// <summary>The 3D bounds of the copper footprint (centre-line grown by half the width).</summary>
    public Aabb Bounds { get; }

    /// <summary>A point near the middle of the footprint — a good seed for a local chart.</summary>
    public SurfacePoint Centre => CentreLine[CentreLine.Count / 2];
}
