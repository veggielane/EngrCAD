namespace EngrCAD.Core.Geometry2;

/// <summary>
/// How thick a planar region is, measured by an OPPOSING-EDGE ray cast — the 2D twin of the wall
/// thickness `Manufacturability` measures on a solid, and the local measure a connectivity test
/// cannot give: whether a piece of a region has a NECK narrower than the pass a fill is about to
/// lay through it.
///
/// <para><b>What is reported is the perpendicular distance to the LINE of the segment hit</b>
/// (<c>t·|d̂·n̂_hit|</c>), not the raw ray length. That is EXACT wherever the opposing boundary is
/// a straight run at any angle — which, a region being a polygon, is everywhere — and it is what
/// makes a tapered slot read its true width where the raw ray over-reports by <c>1/cos</c>. It
/// under-reports against a locally convex opposite and over-reports against a concave one, and
/// since every boundary point is probed, the conservative reading is the one
/// <see cref="Region2dThicknessReport.Minimum"/> keeps.</para>
///
/// <para><b>Cost is O(samples × edges) and the input is the OUTLINE</b> — tens to hundreds of
/// segments, not the thousands a filled path carries — so the scan is linear and there is no
/// index to keep in step. A caller measuring a region with thousands of edges at many samples
/// per edge should expect that product.</para>
///
/// <para><b>What it deliberately is not.</b> Not the medial axis: the largest inscribed disc at
/// a fillet or an inside corner is a better local width and needs a different computation, so it
/// is a NAMED alternative rather than a silent upgrade (the same call the 3D twin makes). And
/// not a refusal — this measures, and the consumer decides.</para>
/// </summary>
public static class Region2dThickness
{
    /// <summary>
    /// Measures <paramref name="regions"/>, probing inward from <paramref name="samplesPerEdge"/>
    /// evenly spaced points on every boundary segment (1 = the midpoint).
    /// </summary>
    public static Region2dThicknessReport Measure(
        IReadOnlyList<Region2d> regions, int samplesPerEdge = 1)
    {
        ArgumentNullException.ThrowIfNull(regions);
        if (samplesPerEdge < 1)
            throw new ArgumentOutOfRangeException(nameof(samplesPerEdge), "A measurement needs at least one sample per edge.");

        // Every boundary segment of every region and every hole, flattened once: the probe reads
        // them all, since a neck can be between an outer wall and a hole as easily as between two
        // outer walls.
        var from = new List<Vector2d>();
        var to = new List<Vector2d>();
        foreach (var region in regions)
        {
            foreach (var loop in region.AllLoops())
            {
                for (int i = 0; i < loop.Count; i++)
                {
                    from.Add(loop[i]);
                    to.Add(loop[(i + 1) % loop.Count]);
                }
            }
        }
        if (from.Count == 0)
            return new Region2dThicknessReport(double.PositiveInfinity, Vector2d.Zero, 0, 0, 0);

        double minimum = double.PositiveInfinity;
        var thinnestAt = Vector2d.Zero;
        double total = 0;
        int measured = 0;
        int unmeasurable = 0;

        for (int e = 0; e < from.Count; e++)
        {
            var edge = to[e] - from[e];
            // A zero-length segment has no direction to probe along and no width to report; it
            // is skipped rather than counted as unmeasurable, since it is not a place.
            if (!edge.TryNormalize(Tolerance.Default, out var tangent))
                continue;
            // The region's loops are CCW outer and CW holes, so walking any loop forward keeps
            // the MATERIAL on the left: one inward rule, both kinds of loop.
            var inward = tangent.Perpendicular;

            for (int s = 0; s < samplesPerEdge; s++)
            {
                // The probe starts exactly ON the boundary, with no stand-off: the source segment
                // is excluded by INDEX, which is exact, so an offset would only bias every
                // reading low by its own length (measured: it did, by extent x 1e-9).
                var origin = from[e] + edge * ((s + 0.5) / samplesPerEdge);
                if (!TryCast(origin, inward, from, to, e, out double thickness, out _))
                {
                    unmeasurable++;
                    continue;
                }
                measured++;
                total += thickness;
                if (thickness < minimum)
                {
                    minimum = thickness;
                    thinnestAt = origin;
                }
            }
        }

        return new Region2dThicknessReport(
            measured > 0 ? minimum : double.PositiveInfinity,
            thinnestAt,
            measured > 0 ? total / measured : 0,
            measured,
            unmeasurable);
    }

    /// <summary>The nearest opposing segment along the ray, and the perpendicular distance to its
    /// line. The source segment is excluded by INDEX rather than by an epsilon, which is exact:
    /// a probe cannot be stopped by the wall it started from.</summary>
    private static bool TryCast(
        in Vector2d origin, in Vector2d direction,
        List<Vector2d> from, List<Vector2d> to, int source,
        out double thickness, out int hitEdge)
    {
        thickness = double.PositiveInfinity;
        hitEdge = -1;
        double bestT = double.PositiveInfinity;

        for (int i = 0; i < from.Count; i++)
        {
            if (i == source)
                continue;
            var edge = to[i] - from[i];
            double denominator = direction.Cross(edge);
            // Exact-zero guard only: a ray parallel to a segment either misses it or runs along
            // it, and neither is a crossing.
            if (denominator == 0)
                continue;
            var offset = from[i] - origin;
            double t = offset.Cross(edge) / denominator;
            double u = offset.Cross(direction) / denominator;
            if (!(t > 0) || !(u >= 0) || !(u <= 1) || !(t < bestT))
                continue;
            bestT = t;
            hitEdge = i;
        }
        if (hitEdge < 0)
            return false;

        var hit = to[hitEdge] - from[hitEdge];
        if (!hit.TryNormalize(Tolerance.Default, out var hitTangent))
            return false;
        // The perpendicular distance to the hit segment's LINE: t times the cosine between the
        // ray and that line's normal. Exact wherever the opposing boundary is straight, which
        // for a polygon is everywhere.
        var hitNormal = hitTangent.Perpendicular;
        thickness = bestT * Math.Abs(direction.Dot(hitNormal));
        return true;
    }
}

/// <summary>What <see cref="Region2dThickness.Measure"/> found.</summary>
public sealed class Region2dThicknessReport
{
    internal Region2dThicknessReport(
        double minimum, in Vector2d thinnestAt, double mean, int samples, int unmeasurable)
    {
        Minimum = minimum;
        ThinnestAt = thinnestAt;
        Mean = mean;
        Samples = samples;
        Unmeasurable = unmeasurable;
    }

    /// <summary>The thinnest wall found — the number a fill's spacing is compared against.
    /// Infinite when nothing could be measured at all.</summary>
    public double Minimum { get; }

    /// <summary>Where that reading was taken, so a refusal can LOCATE the neck rather than only
    /// naming its width.</summary>
    public Vector2d ThinnestAt { get; }

    /// <summary>The mean over the samples that returned a value. Reported beside
    /// <see cref="Minimum"/> and never instead of it — a mean says nothing about a neck.</summary>
    public double Mean { get; }

    /// <summary>How many probes returned a value.</summary>
    public int Samples { get; }

    /// <summary>How many probes found no opposing segment at all. Non-zero only on a region a
    /// probe can escape from, which a simple closed outline cannot be — so any value here is
    /// itself the finding.</summary>
    public int Unmeasurable { get; }
}
