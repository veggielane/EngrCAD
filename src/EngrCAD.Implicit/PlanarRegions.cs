using System.Buffers;
using EngrCAD.Core;

namespace EngrCAD.Implicit;

/// <summary>
/// A closed 2D region with an exact signed distance (negative inside). Implemented by
/// higher layers (sketches in EngrCAD.Modeling); this project only needs to evaluate
/// it to build exact extruded/revolved solids from 2D profiles.
/// </summary>
public interface IPlanarRegion
{
    double SignedDistance(in Vector2d point);

    /// <summary>Conservative 2D bounds of the region (x/y; z is ignored).</summary>
    Aabb Bounds { get; }

    /// <summary>
    /// Batch form of <see cref="SignedDistance(in Vector2d)"/> over deinterleaved
    /// coordinates — the seam <see cref="Sdf.ExtrudedRegion"/> and
    /// <see cref="Sdf.RevolvedRegion"/> drive, so a region built from many boundary
    /// segments can hoist its per-segment setup out of the per-point loop and run
    /// lane-wise kernels instead of one virtual call per segment per point.
    /// <para>
    /// The default implementation loops the scalar entry point, so an implementer need not
    /// provide it. An override MUST return, for every index, exactly the double the scalar
    /// entry point would return for that point — this field's magnitude and its even–odd
    /// sign are exact by construction and downstream boolean classification depends on it.
    /// </para>
    /// </summary>
    void SignedDistance(ReadOnlySpan<double> x, ReadOnlySpan<double> y, Span<double> distances)
    {
        for (int i = 0; i < x.Length; i++)
            distances[i] = SignedDistance(new Vector2d(x[i], y[i]));
    }
}

/// <summary>Prism: the region extruded along +Z from z = 0 to z = height. Exact
/// wherever the region's 2D distance is exact.</summary>
internal sealed class ExtrudedRegionSdf(IPlanarRegion region, double height) : Sdf
{
    public override double Evaluate(in Vector3d point)
    {
        double d2 = region.SignedDistance(new Vector2d(point.X, point.Y));
        double dz = Math.Max(-point.Z, point.Z - height);
        double ox = Math.Max(d2, 0);
        double oz = Math.Max(dz, 0);
        return Math.Min(Math.Max(d2, dz), 0) + Math.Sqrt(ox * ox + oz * oz);
    }

    /// <summary>
    /// Term for term the scalar form, with two structural savings. The 2D distance is
    /// asked for once per <em>distinct</em> (x, y) rather than once per sample: a prism's
    /// field is constant along z, and bulk consumers sample z fastest (Surface Nets walks
    /// a grid corner run per (x, y)), so a batch is normally a handful of long constant-xy
    /// runs — an exact memoization, since the same input returns the same double. And the
    /// z combination runs through the batch seam's lane-wise helpers.
    /// </summary>
    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        int n = x.Length;
        if (n == 0)
            return;

        var runs = ArrayPool<int>.Shared.Rent(n + 1);
        using var planar = new BatchScratch(n);
        using var representatives = new BatchScratch3(n);
        try
        {
            // Run boundaries. The equality test is an identity test for memoization, NOT a
            // geometric comparison: a coordinate that differs by an ulp simply misses and
            // gets its own evaluation, so no tolerance is involved or wanted.
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                if (i == 0 || x[i] != x[i - 1] || y[i] != y[i - 1])
                {
                    representatives.X[count] = x[i];
                    representatives.Y[count] = y[i];
                    runs[count++] = i;
                }
            }
            runs[count] = n;

            region.SignedDistance(
                representatives.X[..count], representatives.Y[..count], representatives.Z[..count]);

            var d2 = planar.Span;
            for (int r = 0; r < count; r++)
                d2.Slice(runs[r], runs[r + 1] - runs[r]).Fill(representatives.Z[r]);

            for (int i = 0; i < n; i++)
            {
                double dz = Math.Max(-z[i], z[i] - height);
                double ox = Math.Max(d2[i], 0);
                double oz = Math.Max(dz, 0);
                distances[i] = Math.Min(Math.Max(d2[i], dz), 0) + Math.Sqrt(ox * ox + oz * oz);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(runs);
        }
    }

    public override Aabb Bounds
    {
        get
        {
            var b = region.Bounds;
            return new Aabb((b.Min.X, b.Min.Y, Math.Min(0, height)), (b.Max.X, b.Max.Y, Math.Max(0, height)));
        }
    }
}

/// <summary>
/// Solid of revolution: the region — read as (radius, height) coordinates, x ≥ 0 —
/// swept a full turn about the Z axis. A true 3D distance wherever the region's 2D
/// distance is exact (full turns only; the map p → (√(x²+y²), z) is an isometry
/// transverse to the revolution).
/// </summary>
internal sealed class RevolvedRegionSdf(IPlanarRegion region) : Sdf
{
    public override double Evaluate(in Vector3d point) =>
        region.SignedDistance(new Vector2d(
            Math.Sqrt(point.X * point.X + point.Y * point.Y),
            point.Z));

    /// <summary>
    /// The radius map is the same expression lane by lane; the region then sees one batch
    /// instead of one virtual call per point. No run memoization here — z is the region's
    /// <em>second</em> coordinate, so a z-fastest sample run is a run of distinct points.
    /// </summary>
    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        int n = x.Length;
        if (n == 0)
            return;

        using var radii = new BatchScratch(n);
        var r = radii.Span;
        for (int i = 0; i < n; i++)
            r[i] = Math.Sqrt(x[i] * x[i] + y[i] * y[i]);
        region.SignedDistance(r, z, distances);
    }

    public override Aabb Bounds
    {
        get
        {
            var b = region.Bounds;
            double r = Math.Max(0, b.Max.X);
            return new Aabb((-r, -r, b.Min.Y), (r, r, b.Max.Y));
        }
    }
}
