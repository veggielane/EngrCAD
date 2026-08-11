using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Mesh;
using EngrCAD.Modeling;

namespace EngrCAD.Ecad;

/// <summary>
/// A net's conductor ROUTED on a <see cref="MidBoard"/>'s moulded surface — a centre-line lifted onto
/// the surface with a width. It is the surface twin of a flat <c>PcbTrace</c>, and it carries the same
/// information plus the one thing the surface adds: the DISTORTION the exp map put into it.
///
/// <para>Two ways it is authored, one lifted result. On a GLOBAL-chart board it is a (u, v) polyline
/// lifted through the chart, distortion measured against the flat (u, v) spacing. On an INTRINSIC board
/// it is already a polyline of surface points (a geodesic path from <see cref="MidRouting"/>, or control
/// points snapped to the surface), so the lift and the length are exact on any surface and the
/// distortion is measured against a LOCAL exp-map chart around the trace.</para>
///
/// <para><b>A point past the map BREAKS the run</b> (<see cref="UnmappedPoints"/> counts them, on the
/// global-chart path) rather than inventing surface, so a trace that runs off its patch comes back as
/// several <see cref="Runs"/>. The exported form (<see cref="Conductor"/>) is a thin conductive
/// <see cref="Shape"/> on the surface — a ribbon offset in the tangent plane and extruded along the
/// surface normal — that round-trips through STL / STEP.</para>
/// </summary>
public sealed class SurfaceTrace
{
    private readonly MidBoard _board;
    private readonly IReadOnlyList<Vector2d>? _chartCentreLine;      // global-chart mode
    private readonly IReadOnlyList<SurfacePoint>? _surfaceCentreLine; // intrinsic mode
    private IReadOnlyList<SurfaceRun>? _runs;
    private (double Min, double Max)? _surfaceBand;

    private SurfaceTrace(
        MidBoard board, string? net, IReadOnlyList<Vector2d>? chartCentreLine,
        IReadOnlyList<SurfacePoint>? surfaceCentreLine, double width, string source)
    {
        _board = board;
        _chartCentreLine = chartCentreLine;
        _surfaceCentreLine = surfaceCentreLine;
        Net = net;
        Width = width;
        Source = source;
    }

    internal static SurfaceTrace OnChart(MidBoard board, string? net, IReadOnlyList<Vector2d> centreLine, double width, string source) =>
        new(board, net, centreLine, null, width, source);

    internal static SurfaceTrace OnSurface(MidBoard board, string? net, IReadOnlyList<SurfacePoint> centreLine, double width, string source) =>
        new(board, net, null, centreLine, width, source);

    /// <summary>The net this trace carries, or null for copper on no electrical net.</summary>
    public string? Net { get; }

    /// <summary>The trace width (mm).</summary>
    public double Width { get; }

    /// <summary>A name for reports.</summary>
    public string Source { get; }

    /// <summary>Whether the trace is intrinsic (a surface polyline) rather than global-chart (u, v).</summary>
    public bool IsIntrinsic => _surfaceCentreLine is not null;

    /// <summary>The authored global-chart centre-line, on a chart trace. Throws on an intrinsic
    /// trace.</summary>
    public IReadOnlyList<Vector2d> CentreLine => _chartCentreLine
        ?? throw new InvalidOperationException($"Trace '{Source}' is intrinsic, so it has no (u, v) centre-line.");

    /// <summary>The lifted conductor, broken where a chart centre-line left the map (one run for an
    /// intrinsic trace or one that stayed on its patch).</summary>
    public IReadOnlyList<SurfaceRun> Runs => _runs ??= Lift();

    /// <summary>How many centre-line points had no surface to land on — 0 for an intrinsic trace (its
    /// points are already on the surface).</summary>
    public int UnmappedPoints
    {
        get
        {
            if (_surfaceCentreLine is not null)
                return 0;
            int mapped = Runs.Sum(r => r.Parameters.Count);
            return _chartCentreLine!.Count - mapped;
        }
    }

    /// <summary>The smallest surface-to-flat length ratio over the drawn segments — how much the surface
    /// SHRANK the tightest place, the number a guaranteed pitch is read from.</summary>
    public double MinScale => Band().Min;

    /// <summary>The largest surface-to-flat length ratio — how much it STRETCHED the worst place, a
    /// guaranteed clearance reading.</summary>
    public double MaxScale => Band().Max;

    /// <summary>The worst relative departure from flat spacing: <c>max(MaxScale − 1, 1/MinScale − 1)</c>
    /// — exact on a plane, a few 1e-4 on a developable surface, several percent on a strongly curved
    /// one.</summary>
    public double Distortion
    {
        get
        {
            var (min, max) = Band();
            return Math.Max(max - 1, 1 / min - 1);
        }
    }

    /// <summary>The lifted centre-line surface length (over every run).</summary>
    public double SurfaceLength => Runs.Sum(r => r.SurfaceLength);

    /// <summary>
    /// The conductor as a thin solid <see cref="Shape"/> on the surface, of the given copper
    /// <paramref name="thickness"/> — a ribbon offset laterally by <see cref="Width"/>/2 in the surface
    /// tangent plane and extruded along the surface normal. One ribbon per run, unioned.
    /// </summary>
    public Shape Conductor(double thickness)
    {
        if (!(thickness > 0) || !double.IsFinite(thickness))
            throw new ArgumentOutOfRangeException(nameof(thickness), "The copper thickness must be positive.");
        Shape? solid = null;
        foreach (var run in Runs)
        {
            var mesh = BuildRibbon(run, Width, thickness);
            if (mesh is null)
                continue;
            var piece = Shape.From(mesh);
            solid = solid is null ? piece : solid.Union(piece);
        }
        return solid ?? throw new InvalidOperationException(
            $"Trace '{Source}' has no run long enough to build a conductor: every run is a single point "
            + "or ran off the map. Check UnmappedPoints and the centre-line.");
    }

    /// <summary>The chart trace lifted through <see cref="SurfaceDecoration.Wrap"/> — the honest
    /// cross-check that the MID board consumes the surface-decoration primitive. Global-chart trace
    /// only.</summary>
    public SurfaceCurve AsSurfaceCurve()
    {
        if (_chartCentreLine is null)
            throw new InvalidOperationException(
                $"Trace '{Source}' is intrinsic; SurfaceDecoration.Wrap is the global-chart lift. Its "
                + "points are already on the surface (see Runs).");
        return SurfaceDecoration.Wrap(_board.Mesh, _board.SeedVertex, _chartCentreLine, _board.ReferenceDirection, _board.MapRadius);
    }

    // ---- the DRC's feature view ----------------------------------------------

    /// <summary>One global-chart parameter-space feature per run.</summary>
    internal IEnumerable<MidFeature> ChartFeatures()
    {
        int r = 0;
        foreach (var run in Runs)
        {
            if (run.Parameters.Count < 2)
            {
                r++;
                continue;
            }
            var strokes = Region2dOffset.Stroke(run.Parameters, Width, StrokeCap.Round, OffsetJoin.Round);
            var (min, max) = run.ScaleBand();
            string source = Runs.Count > 1 ? $"{Source}#{r}" : Source;
            foreach (var stroke in strokes)
                yield return new MidFeature(Net, source, CurvedRegion2d.FromRegion(stroke), Width, min, max);
            r++;
        }
    }

    /// <summary>The intrinsic surface feature(s) — one per run, a centre-line of surface points with the
    /// trace width, the footprint the geodesic DRC measures.</summary>
    internal IEnumerable<MidSurfaceFeature> SurfaceFeatures()
    {
        int r = 0;
        foreach (var run in Runs)
        {
            if (run.Points.Count < 2)
            {
                r++;
                continue;
            }
            var points = new SurfacePoint[run.Points.Count];
            for (int i = 0; i < points.Length; i++)
                points[i] = new SurfacePoint(run.Points[i], run.Normals[i], run.Faces[i], 0, 0, 0);
            string source = Runs.Count > 1 ? $"{Source}#{r}" : Source;
            yield return new MidSurfaceFeature(Net, source, Width, points);
            r++;
        }
    }

    // ---- lifting -------------------------------------------------------------

    private IReadOnlyList<SurfaceRun> Lift()
    {
        if (_surfaceCentreLine is { } surf)
        {
            var faces = new int[surf.Count];
            var pu = new List<Vector2d>(surf.Count);
            var ps = new List<Vector3d>(surf.Count);
            var pn = new List<Vector3d>(surf.Count);
            for (int i = 0; i < surf.Count; i++)
            {
                faces[i] = surf[i].Face;
                ps.Add(surf[i].Position);
                pn.Add(surf[i].Normal);
                // A trivial arc-length parameter, so a chart-only reader never divides by nothing.
                double s = i == 0 ? 0 : pu[^1].X + surf[i].Position.DistanceTo(surf[i - 1].Position);
                pu.Add(new Vector2d(s, 0));
            }
            return [new SurfaceRun([.. pu], [.. ps], [.. pn], faces)];
        }

        // Global chart: lift each (u, v), breaking where it leaves the map.
        var runs = new List<SurfaceRun>();
        var cu = new List<Vector2d>();
        var cp = new List<Vector3d>();
        var cn = new List<Vector3d>();
        var cf = new List<int>();
        foreach (var uv in _chartCentreLine!)
        {
            if (_board.TryLift(uv, out var point, out var normal))
            {
                cu.Add(uv);
                cp.Add(point);
                cn.Add(normal);
                cf.Add(-1);
                continue;
            }
            if (cu.Count > 0)
            {
                runs.Add(new SurfaceRun([.. cu], [.. cp], [.. cn], [.. cf]));
                cu = [];
                cp = [];
                cn = [];
                cf = [];
            }
        }
        if (cu.Count > 0)
            runs.Add(new SurfaceRun([.. cu], [.. cp], [.. cn], [.. cf]));
        return runs;
    }

    // ---- distortion band -----------------------------------------------------

    private (double Min, double Max) Band()
    {
        if (_surfaceCentreLine is not null)
            return _surfaceBand ??= SurfaceBand();

        double min = double.PositiveInfinity, max = 0;
        foreach (var run in Runs)
        {
            if (run.Parameters.Count < 2)
                continue;
            var (rmin, rmax) = run.ScaleBand();
            min = Math.Min(min, rmin);
            max = Math.Max(max, rmax);
        }
        return max <= 0 ? (1, 1) : (min, max);
    }

    /// <summary>The intrinsic distortion: a local exp-map chart around the trace, comparing each
    /// surface segment's length to its length in the chart's (u, v). Exact on a developable patch (band
    /// ≈ 1); several percent where curvature concentrates.</summary>
    private (double Min, double Max) SurfaceBand()
    {
        var surf = _surfaceCentreLine!;
        if (surf.Count < 2)
            return (1, 1);
        var centre = surf[surf.Count / 2];
        var box = Aabb.Empty;
        foreach (var p in surf)
            box = box.Union(p.Position);
        double extent = box.Size.Length;
        double radius = extent * 1.5 + _board.ChartRadius(Width);
        var chart = _board.Surface.Chart(centre, radius);

        double min = double.PositiveInfinity, max = 0;
        for (int i = 1; i < surf.Count; i++)
        {
            if (!chart.TryProject(surf[i - 1], out var ua) || !chart.TryProject(surf[i], out var ub))
                continue;
            double flat = ua.DistanceTo(ub);
            if (!(flat > 0))
                continue;
            double drawn = surf[i].Position.DistanceTo(surf[i - 1].Position);
            double ratio = drawn / flat;
            min = Math.Min(min, ratio);
            max = Math.Max(max, ratio);
        }
        return max <= 0 ? (1, 1) : (min, max);
    }

    // ---- the conductor ribbon ------------------------------------------------

    /// <summary>Builds one ribbon: a rectangular cross-section (width × thickness) swept along the run's
    /// surface centre-line, the width laid across the tangent plane and the thickness along the normal.
    /// Null for a run of fewer than two distinct points; manifold by construction.</summary>
    private static HalfEdgeMesh? BuildRibbon(SurfaceRun run, double width, double thickness)
    {
        var c = new List<Vector3d>();
        var nrm = new List<Vector3d>();
        for (int i = 0; i < run.Points.Count; i++)
        {
            if (c.Count > 0 && run.Points[i].DistanceTo(c[^1]) <= 1e-12)
                continue;
            c.Add(run.Points[i]);
            nrm.Add(run.Normals[i]);
        }
        int n = c.Count;
        if (n < 2)
            return null;

        double half = width / 2;
        var positions = new List<Vector3d>(n * 4);
        for (int i = 0; i < n; i++)
        {
            var normal = nrm[i];
            var tangent = Tangent(c, i);
            var lateral = normal.Cross(tangent);
            if (!lateral.TryNormalize(Tolerance.Default, out lateral))
                lateral = tangent.Cross(normal);
            var left = c[i] + lateral * half;
            var right = c[i] - lateral * half;
            positions.Add(right);
            positions.Add(left);
            positions.Add(left + normal * thickness);
            positions.Add(right + normal * thickness);
        }

        var faces = new List<int[]>();
        for (int i = 0; i < n - 1; i++)
        {
            int a = i * 4, b = (i + 1) * 4;
            for (int k = 0; k < 4; k++)
            {
                int k1 = (k + 1) % 4;
                faces.Add([a + k, a + k1, b + k1, b + k]);
            }
        }
        int last = (n - 1) * 4;
        faces.Add([0, 3, 2, 1]);
        faces.Add([last, last + 1, last + 2, last + 3]);
        return HalfEdgeMesh.Build(positions, faces);
    }

    private static Vector3d Tangent(IReadOnlyList<Vector3d> c, int i)
    {
        Vector3d t = i == 0 ? c[1] - c[0]
            : i == c.Count - 1 ? c[i] - c[i - 1]
            : c[i + 1] - c[i - 1];
        return t.TryNormalize(Tolerance.Default, out var unit) ? unit : Vector3d.UnitX;
    }
}

/// <summary>
/// One unbroken stretch of a <see cref="SurfaceTrace"/> on the surface — the parameter points that
/// landed (or a trivial arc-length parameter for an intrinsic trace), the surface points they lifted to,
/// the surface normals, and the mesh face each lies in.
/// </summary>
public sealed class SurfaceRun
{
    internal SurfaceRun(IReadOnlyList<Vector2d> parameters, IReadOnlyList<Vector3d> points, IReadOnlyList<Vector3d> normals, IReadOnlyList<int> faces)
    {
        Parameters = parameters;
        Points = points;
        Normals = normals;
        Faces = faces;
        double length = 0;
        for (int i = 1; i < points.Count; i++)
            length += points[i].DistanceTo(points[i - 1]);
        SurfaceLength = length;
    }

    /// <summary>The parameter points of this run — global-chart (u, v), or an arc-length parameter for an
    /// intrinsic trace.</summary>
    public IReadOnlyList<Vector2d> Parameters { get; }

    /// <summary>The surface points this run lifted to (one per parameter point).</summary>
    public IReadOnlyList<Vector3d> Points { get; }

    /// <summary>The surface normals at the run's points.</summary>
    public IReadOnlyList<Vector3d> Normals { get; }

    /// <summary>The mesh face each point lies in (−1 for a global-chart lift, which does not track one).</summary>
    public IReadOnlyList<int> Faces { get; }

    /// <summary>The run's surface length.</summary>
    public double SurfaceLength { get; }

    /// <summary>The run's local scale band — <c>[min, max]</c> of (surface length / parameter length)
    /// over its segments. Meaningful for a global-chart run.</summary>
    public (double Min, double Max) ScaleBand()
    {
        double min = double.PositiveInfinity, max = 0;
        for (int i = 1; i < Parameters.Count; i++)
        {
            double flat = Parameters[i].DistanceTo(Parameters[i - 1]);
            if (!(flat > 0))
                continue;
            double scale = Points[i].DistanceTo(Points[i - 1]) / flat;
            min = Math.Min(min, scale);
            max = Math.Max(max, scale);
        }
        return max <= 0 ? (1, 1) : (min, max);
    }
}
