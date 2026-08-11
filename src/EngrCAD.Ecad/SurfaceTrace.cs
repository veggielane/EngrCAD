using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Mesh;
using EngrCAD.Modeling;

namespace EngrCAD.Ecad;

/// <summary>
/// A net's conductor ROUTED on a <see cref="MidBoard"/>'s moulded surface — a centre-line polyline in
/// the exp map's (u, v) parameter space with a width, LIFTED onto the surface. It is the surface twin
/// of a flat <c>PcbTrace</c>, and it carries the same information plus the one thing the surface adds:
/// the DISTORTION the exp map put into it.
///
/// <para><b>Every trace REPORTS its carried distortion</b> (<see cref="MinScale"/>,
/// <see cref="MaxScale"/>, <see cref="Distortion"/>) — measured on the curve ACTUALLY laid, exactly as
/// <see cref="SurfaceCurve"/> does: each drawn segment's surface length against the flat length it was
/// asked for. This rides on every downstream number (the DRC folds it into the clearance) and is never
/// averaged away — the EXTREMES are the report and the mean rides beside them, because a comfortable
/// mean over a patch that stretches one way and shrinks the other hides exactly what the report exists
/// to say.</para>
///
/// <para><b>A point past the map BREAKS the run</b> (<see cref="UnmappedPoints"/> counts them) rather
/// than inventing surface, so a trace that runs off its patch comes back as several
/// <see cref="Runs"/> — which is honest and which the DRC and the connectivity read as a broken net.</para>
///
/// <para><b>The exported form</b> (<see cref="Conductor"/>) is a thin conductive <see cref="Shape"/> on
/// the surface — a ribbon offset laterally in the surface tangent plane and extruded along the surface
/// normal by the copper thickness — that round-trips through STL / STEP like any part.</para>
/// </summary>
public sealed class SurfaceTrace
{
    private readonly MidBoard _board;
    private IReadOnlyList<SurfaceRun>? _runs;

    internal SurfaceTrace(MidBoard board, string? net, IReadOnlyList<Vector2d> centreLine, double width, string source)
    {
        _board = board;
        Net = net;
        CentreLine = centreLine;
        Width = width;
        Source = source;
    }

    /// <summary>The net this trace carries, or null for copper on no electrical net.</summary>
    public string? Net { get; }

    /// <summary>The routed centre-line in parameter (u, v) coordinates — authored so its endpoints
    /// coincide with the (u, v) of the pads it connects, which is what makes the lifted endpoints land
    /// exactly on those pads.</summary>
    public IReadOnlyList<Vector2d> CentreLine { get; }

    /// <summary>The trace width (mm). Laid in (u, v) as a stroke of the same width — exact on a
    /// developable surface (scale ≈ 1), and off by the map's distortion on a curved one, which the DRC
    /// folds into the clearance.</summary>
    public double Width { get; }

    /// <summary>A name for reports.</summary>
    public string Source { get; }

    /// <summary>The lifted conductor, broken where the flat centre-line left the map. One run for a
    /// trace that stayed on its patch; several where it ran off.</summary>
    public IReadOnlyList<SurfaceRun> Runs => _runs ??= Lift();

    /// <summary>How many centre-line points had no surface to land on — the run breaks, counted rather
    /// than smoothed over. A trace that stays on its patch reports 0.</summary>
    public int UnmappedPoints
    {
        get
        {
            int mapped = Runs.Sum(r => r.Parameters.Count);
            return CentreLine.Count - mapped;
        }
    }

    /// <summary>The smallest surface-to-flat length ratio over the drawn segments — how much the map
    /// SHRANK the tightest place, the number a guaranteed pitch is read from. 1 for a trace with no
    /// measurable segment.</summary>
    public double MinScale => ScaleBand().Min;

    /// <summary>The largest surface-to-flat length ratio — how much the map STRETCHED the worst place,
    /// the number a guaranteed clearance is read from.</summary>
    public double MaxScale => ScaleBand().Max;

    /// <summary>The worst relative departure from the flat spacing:
    /// <c>max(MaxScale − 1, 1/MinScale − 1)</c>. <see cref="MeshLocalParam"/>'s stated limit measured
    /// on the curve actually laid — exact on a plane, a few 1e-4 on a developable surface, several
    /// percent on a strongly curved one.</summary>
    public double Distortion
    {
        get
        {
            var (min, max) = ScaleBand();
            return Math.Max(max - 1, 1 / min - 1);
        }
    }

    /// <summary>The lifted centre-line surface length (over every run).</summary>
    public double SurfaceLength => Runs.Sum(r => r.SurfaceLength);

    /// <summary>
    /// The conductor as a thin solid <see cref="Shape"/> on the surface, of the given copper
    /// <paramref name="thickness"/> — a ribbon offset laterally by <see cref="Width"/>/2 in the surface
    /// tangent plane and extruded along the surface normal. Round-trips through STL / STEP.
    /// <para>One ribbon per <see cref="Runs"/> run, unioned; a run of fewer than two points carries no
    /// ribbon. A trace whose every run is too short throws.</para>
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

    /// <summary>
    /// The trace lifted through <see cref="SurfaceDecoration.Wrap"/> — the modelling-layer surface
    /// decoration consumer. Returned for the caller who wants the standard <see cref="SurfaceCurve"/>
    /// report; the board's own lift uses the SAME <see cref="MeshLocalParam"/> parameters, so the two
    /// agree to arithmetic precision (asserted by <c>SurfaceTraceTests</c>), which is why the board
    /// computes the map ONCE and this recomputes it only when a caller explicitly asks.
    /// </summary>
    public SurfaceCurve AsSurfaceCurve() =>
        SurfaceDecoration.Wrap(_board.Mesh, _board.SeedVertex, CentreLine, _board.ReferenceDirection, _board.MapRadius);

    // ---- the DRC's feature view ----------------------------------------------

    /// <summary>One parameter-space copper feature per run — the run's centre-line stroked to its
    /// width, tagged with the net and the run's local scale band.</summary>
    internal IEnumerable<MidFeature> Features()
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

    // ---- lifting -------------------------------------------------------------

    private IReadOnlyList<SurfaceRun> Lift()
    {
        var runs = new List<SurfaceRun>();
        var pu = new List<Vector2d>();
        var ps = new List<Vector3d>();
        var pn = new List<Vector3d>();
        foreach (var uv in CentreLine)
        {
            if (_board.TryLift(uv, out var point, out var normal))
            {
                pu.Add(uv);
                ps.Add(point);
                pn.Add(normal);
                continue;
            }
            if (pu.Count > 0)
            {
                runs.Add(new SurfaceRun([.. pu], [.. ps], [.. pn]));
                pu = [];
                ps = [];
                pn = [];
            }
        }
        if (pu.Count > 0)
            runs.Add(new SurfaceRun([.. pu], [.. ps], [.. pn]));
        return runs;
    }

    private (double Min, double Max) ScaleBand()
    {
        double min = double.PositiveInfinity, max = 0;
        foreach (var run in Runs)
        {
            var (rmin, rmax) = run.ScaleBand();
            if (run.Parameters.Count < 2)
                continue;
            min = Math.Min(min, rmin);
            max = Math.Max(max, rmax);
        }
        return max <= 0 ? (1, 1) : (min, max);
    }

    // ---- the conductor ribbon ------------------------------------------------

    /// <summary>Builds one ribbon: a rectangular cross-section (width × thickness) swept along the
    /// run's surface centre-line, the width laid across the surface tangent plane and the thickness
    /// along the surface normal. Returns null for a run of fewer than two distinct points. The mesh is
    /// manifold by construction (a swept closed quad ring plus two caps); a self-intersecting bend is a
    /// v1 limitation stated on the class, not caught here.</summary>
    private static HalfEdgeMesh? BuildRibbon(SurfaceRun run, double width, double thickness)
    {
        // Drop consecutive duplicate surface points (a zero-length segment carries no direction).
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
            // Lateral in the tangent plane: b = normalize(n × t), so (t, b, n) is right-handed.
            var lateral = normal.Cross(tangent);
            if (!lateral.TryNormalize(Tolerance.Default, out lateral))
                lateral = tangent.Cross(normal);   // degenerate frame fallback
            var left = c[i] + lateral * half;
            var right = c[i] - lateral * half;
            // Cross-section ring, four corners: bottom-right, bottom-left, top-left, top-right,
            // ordered so the swept quads and caps come out consistently wound (verified by test).
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
                // Swept quad k of segment i: outward-wound.
                faces.Add([a + k, a + k1, b + k1, b + k]);
            }
        }
        // End caps. The start cap faces backward (the ring reversed), the end cap forward.
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
/// landed, the surface points they lifted to, and the surface normals there. A trace that stays on its
/// patch has one run; one that runs off the map has several.
/// </summary>
public sealed class SurfaceRun
{
    internal SurfaceRun(IReadOnlyList<Vector2d> parameters, IReadOnlyList<Vector3d> points, IReadOnlyList<Vector3d> normals)
    {
        Parameters = parameters;
        Points = points;
        Normals = normals;
        double length = 0;
        for (int i = 1; i < points.Count; i++)
            length += points[i].DistanceTo(points[i - 1]);
        SurfaceLength = length;
    }

    /// <summary>The parameter (u, v) points of this run.</summary>
    public IReadOnlyList<Vector2d> Parameters { get; }

    /// <summary>The surface points this run lifted to (one per parameter point).</summary>
    public IReadOnlyList<Vector3d> Points { get; }

    /// <summary>The surface normals at the run's points.</summary>
    public IReadOnlyList<Vector3d> Normals { get; }

    /// <summary>The run's surface length.</summary>
    public double SurfaceLength { get; }

    /// <summary>The run's local scale band — <c>[min, max]</c> of (surface length / parameter length)
    /// over its segments. <c>[1, 1]</c> for a run with no measurable segment.</summary>
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
