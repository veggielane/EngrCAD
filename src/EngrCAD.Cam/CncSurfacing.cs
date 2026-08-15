using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Modeling;

namespace EngrCAD.Cam;

/// <summary>
/// 3-axis surfacing — CAM stage 3: ball-nose finishing passes over the same `Shape` graph, and
/// the place the implicit engine pays directly. <b>The cutter-location surface of a ball-nose
/// tool IS the field's r-offset</b> — a ball of radius r touches the part exactly when its
/// centre is at distance r from the surface — so both strategies read the shape's own SDF
/// (<see cref="Shape.ToImplicit"/>) instead of approximating an offset mesh:
///
/// <para><b>Raster</b> (parallel finishing): serpentine rows, the tool dropped at each sample —
/// a SPHERE TRACE down the vertical ray to the r-isolevel, which the field's 1-Lipschitz bound
/// makes gouge-free BY CONSTRUCTION (a step of <c>sdf − r</c> can never cross the offset
/// surface, so a stalled trace stops HIGH — stock left, never a gouge). <b>Waterline</b>
/// (constant-z contouring): the CL contour at a centre plane IS the SDF's r-isolevel there —
/// <see cref="SdfContours.OnPlane"/>'s marching squares, chained into loops by its exact
/// endpoint equality, then polished onto the isolevel by an IN-PLANE Newton step (the
/// correction must not leave the plane, or the pass stops being a waterline).</para>
///
/// <para>Passes are in the SHAPE's own coordinates (tip z = the surface's own z; the G-code
/// z word is the TIP, the machining convention). The default cutter is a BALL-NOSE of the
/// tool's own radius; a <see cref="MillCutter"/> selects flat or bull-nose, which RASTER
/// carries over the tessellation (see <see cref="DropCutter"/> for why the field route does
/// not survive a flat bottom) while waterline refuses them by name. Still filed: a raster
/// direction other than X, holder/shank collision checking and rest machining. Where the
/// field is a correct-sign LOWER BOUND (a CSG difference near its tool's fictitious faces), the
/// r-isolevel lies FARTHER from the part than the true offset — stock left, never a gouge: the
/// conservative direction, inherited from the field contract rather than arranged.</para>
/// </summary>
public static class CncSurfacing
{
    private const double TraceTolerance = 1e-7;

    /// <summary>
    /// Parallel (raster) ball-nose finishing: serpentine rows along X, one
    /// <c>Stepover·Diameter</c> apart in Y, each sample's tip height read off the field by a
    /// vertical sphere trace to the r-offset. Rows and samples are anchored to the GLOBAL grid
    /// (the slicer's phase rule — the pattern is a function of the stated spacing, never of
    /// where the part sits) and extend a tool radius past the part so the edge is machined.
    /// <paramref name="sampleStep"/> is the along-row sampling distance
    /// (null = half the stepover).
    /// </summary>
    public static MillOperation Raster(
        Shape shape, MillTool tool, double? sampleStep = null, string name = "raster",
        MillCutter? cutter = null)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(tool);
        tool.Validate();
        if (cutter is not null)
        {
            cutter.Validate();
            if (cutter.Diameter != tool.Diameter)
                throw new ArgumentException(
                    $"The cutter (Ø{cutter.Diameter:0.###}) and the tool (Ø{tool.Diameter:0.###}) "
                    + "state different diameters — two stated diameters disagreeing is a "
                    + "modelling error, not a preference.", nameof(cutter));
            // A flat or bull-nose rides the tessellation (see DropCutter's remarks for why
            // the field route does not survive the disc); a ball-nose IS the exact route.
            if (!cutter.IsBallNose)
                return DropCutter.Raster(shape, tool, cutter, sampleStep, name);
        }

        var sdf = shape.ToImplicit();
        var bounds = shape.Bounds();
        double r = tool.Radius;
        double top = bounds.Max.Z + r + 1;
        double floor = bounds.Min.Z + r;
        return SerpentineRaster(tool, bounds, sampleStep, name,
            (x, y) => Drop(sdf, x, y, top, floor, r) - r);
    }

    /// <summary>The serpentine raster loop both routes share — grid-anchored rows one
    /// <c>Stepover·Diameter</c> apart, samples one <paramref name="sampleStep"/> apart
    /// (null = half the stepover), extended a tool radius past the part —
    /// <paramref name="tipAt"/> answering the TIP height at each sample.</summary>
    internal static MillOperation SerpentineRaster(
        MillTool tool, in Aabb bounds, double? sampleStep, string name,
        Func<double, double, double> tipAt)
    {
        double stepover = tool.Stepover * tool.Diameter;
        double step = sampleStep ?? stepover / 2;
        if (!(step > 0) || !double.IsFinite(step))
            throw new ArgumentException(
                $"sampleStep must be finite and positive; got {step:0.###}.", nameof(sampleStep));
        double r = tool.Radius;

        var passes = new List<MillPass>();
        int firstRow = (int)Math.Ceiling((bounds.Min.Y - r) / stepover - 1e-12);
        int lastRow = (int)Math.Floor((bounds.Max.Y + r) / stepover + 1e-12);
        int firstCol = (int)Math.Ceiling((bounds.Min.X - r) / step - 1e-12);
        int lastCol = (int)Math.Floor((bounds.Max.X + r) / step + 1e-12);
        bool reverse = false;
        for (int k = firstRow; k <= lastRow; k++)
        {
            double y = k * stepover;
            var points = new List<Vector3d>(lastCol - firstCol + 1);
            for (int j = firstCol; j <= lastCol; j++)
            {
                double x = (reverse ? lastCol - (j - firstCol) : j) * step;
                points.Add(new Vector3d(x, y, tipAt(x, y)));
            }
            if (points.Count >= 2)
                passes.Add(new MillPass(points, IsClosed: false));
            reverse = !reverse;
        }
        return new MillOperation(name, tool, passes);
    }

    /// <summary>
    /// Waterline (constant-z) ball-nose contouring: tip levels one <c>StepDown</c> apart from
    /// the part's top down, the last clamped to its bottom; at each level the CL contour is the
    /// SDF's r-isolevel on the CENTRE plane (tip + r), extracted by marching squares
    /// (<paramref name="sampleStep"/> sets the grid, null = half the tool radius; crossing
    /// error is O(h²·curvature)) and polished onto the isolevel by an in-plane Newton step, so
    /// on the steep walls waterline exists for, the path is exact to round-off.
    /// </summary>
    public static MillOperation Waterline(
        Shape shape, MillTool tool, double? sampleStep = null, string name = "waterline",
        MillCutter? cutter = null)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(tool);
        tool.Validate();
        if (cutter is not null && !cutter.IsBallNose)
            throw new ArgumentException(
                "Waterline contouring supports the ball-nose only: a flat or bull-nose "
                + "waterline is the contour of the mesh dilated by the tool's bottom disc — a "
                + "2D arrangement the field identity does not reach (certifying a min over the "
                + "disc through a 1-Lipschitz oracle is quadratic in the flatness, and flat is "
                + "the common case). Filed; Raster carries those cutters.", nameof(cutter));
        double r = tool.Radius;
        double step = sampleStep ?? r / 2;
        if (!(step > 0) || !double.IsFinite(step))
            throw new ArgumentException(
                $"sampleStep must be finite and positive; got {step:0.###}.", nameof(sampleStep));

        var sdf = shape.ToImplicit();
        var bounds = shape.Bounds();
        double fdStep = Math.Max(1e-9, 1e-6 * (bounds.Max - bounds.Min).Length);

        // The grid must hold the whole r-isolevel strictly inside it, or a contour exits the
        // boundary and a waterline loop comes back open: span the part plus the radius plus
        // two cells of margin.
        double pad = r + 2 * step;
        var origin0 = new Vector2d(bounds.Min.X - pad, bounds.Min.Y - pad);
        double spanX = bounds.Max.X - bounds.Min.X + 2 * pad;
        double spanY = bounds.Max.Y - bounds.Min.Y + 2 * pad;
        int uSamples = Math.Max(2, (int)Math.Ceiling(spanX / step) + 1);
        int vSamples = Math.Max(2, (int)Math.Ceiling(spanY / step) + 1);

        var passes = new List<MillPass>();
        foreach (double level in CncMill.DepthLevels(bounds.Max.Z - bounds.Min.Z, tool.StepDown))
        {
            double tipZ = bounds.Max.Z + level;
            double centreZ = tipZ + r;
            var contours = SdfContours.OnPlane(
                sdf,
                new Vector3d(origin0.X, origin0.Y, centreZ),
                new Vector3d(spanX, 0, 0), new Vector3d(0, spanY, 0),
                uSamples, vSamples,
                [r]);
            foreach (var chain in ChainSegments(contours[0].Segments))
            {
                var points = new List<Vector3d>(chain.Points.Count);
                foreach (var point in chain.Points)
                {
                    var refined = RefineInPlane(sdf, point, r, fdStep);
                    points.Add(new Vector3d(refined.X, refined.Y, tipZ));
                }
                if (points.Count >= 2)
                    passes.Add(new MillPass(points, chain.IsClosed));
            }
        }
        return new MillOperation(name, tool, passes);
    }

    /// <summary>The cusp a ball of radius <paramref name="toolRadius"/> leaves between rows
    /// <paramref name="stepover"/> apart on a flat surface — the EXACT chord form
    /// <c>r − √(r² − (s/2)²)</c>, whose small-stepover expansion is the classic
    /// <c>s²/8r</c>. A stepover past the diameter leaves full-height ridges and is refused.</summary>
    public static double ScallopHeight(double toolRadius, double stepover)
    {
        if (!(toolRadius > 0) || !double.IsFinite(toolRadius))
            throw new ArgumentException($"toolRadius must be finite and positive; got {toolRadius:0.###}.");
        if (!(stepover > 0) || stepover >= 2 * toolRadius)
            throw new ArgumentException(
                $"stepover must lie in (0, 2r) — past the diameter adjacent passes no longer "
                + $"overlap and the scallop is the full flank; got {stepover:0.###} against r = {toolRadius:0.###}.");
        double half = stepover / 2;
        return toolRadius - Math.Sqrt(toolRadius * toolRadius - half * half);
    }

    /// <summary>The stepover that leaves a stated scallop height: the inverse chord form
    /// <c>2·√(h·(2r − h))</c>, so <see cref="ScallopHeight"/> round-trips it exactly.</summary>
    public static double StepoverForScallop(double toolRadius, double scallopHeight)
    {
        if (!(toolRadius > 0) || !double.IsFinite(toolRadius))
            throw new ArgumentException($"toolRadius must be finite and positive; got {toolRadius:0.###}.");
        if (!(scallopHeight > 0) || scallopHeight > toolRadius)
            throw new ArgumentException(
                $"scallopHeight must lie in (0, r]; got {scallopHeight:0.###} against r = {toolRadius:0.###}.");
        return 2 * Math.Sqrt(scallopHeight * (2 * toolRadius - scallopHeight));
    }

    /// <summary>Sphere-traces the vertical ray at (x, y) down from <paramref name="top"/> to
    /// the field's r-isolevel — the ball centre's height at first contact. The 1-Lipschitz
    /// bound makes a step of <c>sdf − r</c> unable to cross the offset surface, so the trace
    /// cannot gouge; a graze that stalls the trace (the classic sphere-tracing failure, a ray
    /// running parallel to a vertical wall's offset) leaves the centre HIGH, which is stock
    /// left rather than a gouge. A ray that never meets the offset clamps to
    /// <paramref name="floor"/> (tip at the part's own bottom).</summary>
    private static double Drop(Sdf sdf, double x, double y, double top, double floor, double r)
    {
        double z = top;
        for (int i = 0; i < 256; i++)
        {
            double d = sdf.Evaluate(new Vector3d(x, y, z)) - r;
            if (d < TraceTolerance)
                break;
            z = Math.Max(z - d, floor);
            if (z == floor)
                break;
        }
        return z;
    }

    /// <summary>Newton-polishes a marching-squares contour point onto the r-isolevel WITHIN
    /// its own plane — the correction uses only the field's in-plane gradient, because a full
    /// gradient step would move the point off the waterline's z. Where the in-plane gradient
    /// is weak (the contour crossing a near-horizontal region) the sampled point stands —
    /// there is no in-plane direction that changes the field, and the honest accuracy is the
    /// marching-squares crossing error.</summary>
    private static Vector3d RefineInPlane(Sdf sdf, in Vector3d point, double r, double fdStep)
    {
        var p = point;
        for (int i = 0; i < 8; i++)
        {
            double f = sdf.Evaluate(p);
            if (Math.Abs(f - r) < 1e-9)
                break;
            double gx = (sdf.Evaluate(new Vector3d(p.X + fdStep, p.Y, p.Z))
                - sdf.Evaluate(new Vector3d(p.X - fdStep, p.Y, p.Z))) / (2 * fdStep);
            double gy = (sdf.Evaluate(new Vector3d(p.X, p.Y + fdStep, p.Z))
                - sdf.Evaluate(new Vector3d(p.X, p.Y - fdStep, p.Z))) / (2 * fdStep);
            double g2 = gx * gx + gy * gy;
            if (g2 < 0.01)
                break;
            double t = (r - f) / g2;
            p = new Vector3d(p.X + gx * t, p.Y + gy * t, p.Z);
        }
        return p;
    }

    /// <summary>Chains marching-squares segments into polylines by EXACT endpoint equality —
    /// the <see cref="SdfContours"/> contract (shared cell-edge endpoints are interpolated
    /// from the same two samples with the same expression). A chain returning to its first
    /// point is a closed loop; one that cannot extend is emitted open (honest — a contour
    /// leaving the sampled grid is a margin bug the tests would catch as an open waterline).</summary>
    internal static List<(List<Vector3d> Points, bool IsClosed)> ChainSegments(
        IReadOnlyList<(Vector3d A, Vector3d B)> segments)
    {
        var chains = new List<(List<Vector3d>, bool)>();
        var used = new bool[segments.Count];
        var byPoint = new Dictionary<Vector3d, List<int>>();
        for (int i = 0; i < segments.Count; i++)
        {
            Add(segments[i].A, i);
            Add(segments[i].B, i);
        }

        for (int i = 0; i < segments.Count; i++)
        {
            if (used[i])
                continue;
            used[i] = true;
            var points = new List<Vector3d> { segments[i].A, segments[i].B };
            bool closed = false;
            while (true)
            {
                var tail = points[^1];
                if (tail == points[0])
                {
                    points.RemoveAt(points.Count - 1);
                    closed = true;
                    break;
                }
                int next = -1;
                if (byPoint.TryGetValue(tail, out var candidates))
                {
                    foreach (int c in candidates)
                    {
                        if (!used[c])
                        {
                            next = c;
                            break;
                        }
                    }
                }
                if (next < 0)
                    break;
                used[next] = true;
                points.Add(segments[next].A == tail ? segments[next].B : segments[next].A);
            }
            chains.Add((points, closed));
        }
        return chains;

        void Add(in Vector3d p, int index)
        {
            if (!byPoint.TryGetValue(p, out var list))
                byPoint[p] = list = [];
            list.Add(index);
        }
    }
}
