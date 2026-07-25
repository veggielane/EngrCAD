using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EngrCAD.Core;

namespace EngrCAD.Implicit;

// SIMD batch evaluation plumbing.
//
// Layout decision (the load-bearing one): the public batch entry point takes points as
// an interleaved ReadOnlySpan<Vector3d> (x,y,z,x,y,z,…), which is the wrong shape for
// SIMD — a lane-wise kernel wants all the x's contiguous. Rather than change the public
// signature (every caller — SurfaceNets, GridSdf, SdfContours — already has AoS point
// arrays) or gather/shuffle per register (3 strided loads per vector, and no portable
// deinterleave for Vector<double>), Sdf.Evaluate deinterleaves ONCE at the root of the
// AST into pooled x/y/z scratch and hands structure-of-arrays spans to the internal
// EvaluateBatch seam. Operators forward those same spans straight to their children, so
// the transpose is paid once per batch, not once per node, and every kernel below the
// root reads and writes contiguous doubles.
//
// Chunking: the root deinterleaves ChunkLength points at a time, so the working set is
// 3 * 1024 * 8 = 24 KB of coordinates plus 8 KB per live operator temporary — L1/L2
// resident for realistic AST depths, and bounded regardless of how many points the
// caller passes.
//
// Exactness: every vector kernel mirrors its scalar Evaluate term for term, in the same
// association order, using only correctly rounded IEEE-754 operations (add/sub/mul/div/
// sqrt) plus min/max. Results are therefore bit-for-bit identical to the scalar path for
// all finite inputs, with one documented exception: Vector.Min/Vector.Max resolve a tie
// between +0.0 and -0.0 by operand position while Math.Min/Math.Max resolve it by sign,
// so a result that is exactly zero may carry the opposite sign bit. ±0 compare equal and
// no consumer of the field can distinguish them. Transcendental kernels (gyroid's
// sin/cos, the exponential falloff) are deliberately NOT vectorized: no vector transcend-
// ental is bit-identical to Math.Sin/Math.Exp, and a silently divergent fast path is
// worse than no fast path.

/// <summary>
/// A lane-wise SDF kernel over pre-broadcast parameters: given vectors of x, y and z
/// coordinates it returns the vector of signed distances. Implemented by
/// <c>readonly struct</c>s so <see cref="SdfBatch.Map{TKernel}"/> specializes and inlines
/// them (no interface dispatch in the loop).
/// </summary>
internal interface ISdfKernel
{
    Vector<double> Evaluate(Vector<double> x, Vector<double> y, Vector<double> z);
}

/// <summary>
/// Pooled scratch for one span of doubles; <c>using</c> returns it to
/// <see cref="ArrayPool{T}"/>. Never allocates on the managed heap per call.
/// </summary>
internal ref struct BatchScratch
{
    private readonly double[] _rented;

    public BatchScratch(int length)
    {
        _rented = ArrayPool<double>.Shared.Rent(length);
        Span = _rented.AsSpan(0, length);
    }

    public readonly Span<double> Span;

    public readonly void Dispose() => ArrayPool<double>.Shared.Return(_rented);
}

/// <summary>
/// Pooled scratch for a structure-of-arrays coordinate triple (one rental, three slices).
/// </summary>
internal ref struct BatchScratch3
{
    private readonly double[] _rented;

    public BatchScratch3(int length)
    {
        _rented = ArrayPool<double>.Shared.Rent(length * 3);
        X = _rented.AsSpan(0, length);
        Y = _rented.AsSpan(length, length);
        Z = _rented.AsSpan(length * 2, length);
    }

    public readonly Span<double> X;
    public readonly Span<double> Y;
    public readonly Span<double> Z;

    public readonly void Dispose() => ArrayPool<double>.Shared.Return(_rented);
}

/// <summary>
/// Shared machinery for the vectorized batch kernels: the deinterleave chunk size, the
/// kernel driver, and the elementwise combine loops the set operators are built from.
/// </summary>
internal static class SdfBatch
{
    /// <summary>
    /// Points staged per deinterleaved chunk. Sized so the x/y/z scratch (24 KB) plus a
    /// few operator temporaries (8 KB each) stay cache resident.
    /// </summary>
    public const int ChunkLength = 1024;

    /// <summary>
    /// True when <see cref="Vector{T}"/> maps to real SIMD registers wider than one
    /// double. Both operands are JIT constants, so the guarded scalar-only branch folds
    /// away entirely.
    /// </summary>
    public static bool Accelerated => Vector.IsHardwareAccelerated && Vector<double>.Count > 1;

    /// <summary>
    /// Runs <paramref name="kernel"/> over the vectorizable prefix of the batch and
    /// finishes the ragged tail through <paramref name="fallback"/>'s scalar
    /// <see cref="Sdf.Evaluate(in Vector3d)"/> — so the tail is exact by
    /// construction and the kernel only has to be exact, not also complete.
    /// </summary>
    public static void Map<TKernel>(
        TKernel kernel,
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z,
        Span<double> distances, Sdf fallback)
        where TKernel : struct, ISdfKernel
    {
        int n = x.Length;
        int i = 0;
        if (Accelerated)
        {
            int w = Vector<double>.Count;
            ref double xr = ref MemoryMarshal.GetReference(x);
            ref double yr = ref MemoryMarshal.GetReference(y);
            ref double zr = ref MemoryMarshal.GetReference(z);
            ref double dr = ref MemoryMarshal.GetReference(distances);
            for (; i <= n - w; i += w)
            {
                var result = kernel.Evaluate(
                    Vector.LoadUnsafe(ref xr, (nuint)i),
                    Vector.LoadUnsafe(ref yr, (nuint)i),
                    Vector.LoadUnsafe(ref zr, (nuint)i));
                result.StoreUnsafe(ref dr, (nuint)i);
            }
        }
        for (; i < n; i++)
            distances[i] = fallback.Evaluate(new Vector3d(x[i], y[i], z[i]));
    }

    /// <summary>
    /// Transposes interleaved points into three coordinate spans. Written against raw
    /// references: this runs once per point per batch and four span bounds checks per
    /// point would cost more than the kernel it feeds.
    /// </summary>
    public static void Deinterleave(
        ReadOnlySpan<Vector3d> points, Span<double> x, Span<double> y, Span<double> z)
    {
        ref var source = ref MemoryMarshal.GetReference(points);
        ref double dx = ref MemoryMarshal.GetReference(x);
        ref double dy = ref MemoryMarshal.GetReference(y);
        ref double dz = ref MemoryMarshal.GetReference(z);
        for (int i = 0; i < points.Length; i++)
        {
            ref readonly var p = ref Unsafe.Add(ref source, i);
            Unsafe.Add(ref dx, i) = p.X;
            Unsafe.Add(ref dy, i) = p.Y;
            Unsafe.Add(ref dz, i) = p.Z;
        }
    }

    // ---- elementwise combines (accumulator in place) ----

    /// <summary>acc = min(acc, other) — the exact union.</summary>
    public static void Min(Span<double> acc, ReadOnlySpan<double> other)
    {
        int n = acc.Length;
        int i = 0;
        if (Accelerated)
        {
            int w = Vector<double>.Count;
            ref double ar = ref MemoryMarshal.GetReference(acc);
            ref double br = ref MemoryMarshal.GetReference(other);
            for (; i <= n - w; i += w)
                Vector.Min(Vector.LoadUnsafe(ref ar, (nuint)i), Vector.LoadUnsafe(ref br, (nuint)i))
                    .StoreUnsafe(ref ar, (nuint)i);
        }
        for (; i < n; i++)
            acc[i] = Math.Min(acc[i], other[i]);
    }

    /// <summary>acc = max(acc, other) — the exact intersection.</summary>
    public static void Max(Span<double> acc, ReadOnlySpan<double> other)
    {
        int n = acc.Length;
        int i = 0;
        if (Accelerated)
        {
            int w = Vector<double>.Count;
            ref double ar = ref MemoryMarshal.GetReference(acc);
            ref double br = ref MemoryMarshal.GetReference(other);
            for (; i <= n - w; i += w)
                Vector.Max(Vector.LoadUnsafe(ref ar, (nuint)i), Vector.LoadUnsafe(ref br, (nuint)i))
                    .StoreUnsafe(ref ar, (nuint)i);
        }
        for (; i < n; i++)
            acc[i] = Math.Max(acc[i], other[i]);
    }

    /// <summary>acc = max(acc, -other) — the exact difference.</summary>
    public static void MaxNegated(Span<double> acc, ReadOnlySpan<double> other)
    {
        int n = acc.Length;
        int i = 0;
        if (Accelerated)
        {
            int w = Vector<double>.Count;
            ref double ar = ref MemoryMarshal.GetReference(acc);
            ref double br = ref MemoryMarshal.GetReference(other);
            for (; i <= n - w; i += w)
                Vector.Max(Vector.LoadUnsafe(ref ar, (nuint)i), -Vector.LoadUnsafe(ref br, (nuint)i))
                    .StoreUnsafe(ref ar, (nuint)i);
        }
        for (; i < n; i++)
            acc[i] = Math.Max(acc[i], -other[i]);
    }

    /// <summary>acc = smoothMin(acc, other, k) — mirrors <see cref="BlendMath.SmoothMin"/>.</summary>
    public static void SmoothMin(Span<double> acc, ReadOnlySpan<double> other, double k)
    {
        if (k <= 0)
        {
            Min(acc, other);
            return;
        }

        int n = acc.Length;
        int i = 0;
        if (Accelerated)
        {
            int w = Vector<double>.Count;
            var vk = new Vector<double>(k);
            ref double ar = ref MemoryMarshal.GetReference(acc);
            ref double br = ref MemoryMarshal.GetReference(other);
            for (; i <= n - w; i += w)
                SmoothMin(Vector.LoadUnsafe(ref ar, (nuint)i), Vector.LoadUnsafe(ref br, (nuint)i), vk)
                    .StoreUnsafe(ref ar, (nuint)i);
        }
        for (; i < n; i++)
            acc[i] = BlendMath.SmoothMin(acc[i], other[i], k);
    }

    /// <summary>acc = -smoothMin(-acc, -other, k) — the smooth intersection.</summary>
    public static void SmoothMax(Span<double> acc, ReadOnlySpan<double> other, double k)
    {
        if (k <= 0)
        {
            Max(acc, other);
            return;
        }

        int n = acc.Length;
        int i = 0;
        if (Accelerated)
        {
            int w = Vector<double>.Count;
            var vk = new Vector<double>(k);
            ref double ar = ref MemoryMarshal.GetReference(acc);
            ref double br = ref MemoryMarshal.GetReference(other);
            for (; i <= n - w; i += w)
                (-SmoothMin(-Vector.LoadUnsafe(ref ar, (nuint)i), -Vector.LoadUnsafe(ref br, (nuint)i), vk))
                    .StoreUnsafe(ref ar, (nuint)i);
        }
        for (; i < n; i++)
            acc[i] = -BlendMath.SmoothMin(-acc[i], -other[i], k);
    }

    /// <summary>acc = -smoothMin(-acc, other, k) — the smooth difference.</summary>
    public static void SmoothSubtract(Span<double> acc, ReadOnlySpan<double> other, double k)
    {
        int n = acc.Length;
        int i = 0;
        if (Accelerated && k > 0)
        {
            int w = Vector<double>.Count;
            var vk = new Vector<double>(k);
            ref double ar = ref MemoryMarshal.GetReference(acc);
            ref double br = ref MemoryMarshal.GetReference(other);
            for (; i <= n - w; i += w)
                (-SmoothMin(-Vector.LoadUnsafe(ref ar, (nuint)i), Vector.LoadUnsafe(ref br, (nuint)i), vk))
                    .StoreUnsafe(ref ar, (nuint)i);
        }
        for (; i < n; i++)
            acc[i] = -BlendMath.SmoothMin(-acc[i], other[i], k);
    }

    /// <summary>
    /// The polynomial smooth minimum, term for term and in the same association order as
    /// <see cref="BlendMath.SmoothMin"/> (callers guarantee k &gt; 0).
    /// <c>Math.Clamp(v, 0, 1)</c> becomes <c>min(max(v, 0), 1)</c>, which agrees for every
    /// finite v (they differ only on a −0.0 input, which cannot arise from <c>0.5 + …</c>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<double> SmoothMin(Vector<double> a, Vector<double> b, Vector<double> k)
    {
        var half = new Vector<double>(0.5);
        var one = Vector<double>.One;
        var h = Vector.Min(Vector.Max(half + half * (b - a) / k, Vector<double>.Zero), one);
        return b + (a - b) * h - k * h * (one - h);
    }

    /// <summary>Lane-wise <c>Math.Clamp(v, 0, 1)</c> (see the note on
    /// <see cref="SmoothMin(Vector{double}, Vector{double}, Vector{double})"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<double> Clamp01(Vector<double> v) =>
        Vector.Min(Vector.Max(v, Vector<double>.Zero), Vector<double>.One);

    // ---- scalar-broadcast modifiers ----

    /// <summary>acc = acc − value.</summary>
    public static void Subtract(Span<double> acc, double value)
    {
        int n = acc.Length;
        int i = 0;
        if (Accelerated)
        {
            int w = Vector<double>.Count;
            var v = new Vector<double>(value);
            ref double ar = ref MemoryMarshal.GetReference(acc);
            for (; i <= n - w; i += w)
                (Vector.LoadUnsafe(ref ar, (nuint)i) - v).StoreUnsafe(ref ar, (nuint)i);
        }
        for (; i < n; i++)
            acc[i] -= value;
    }

    /// <summary>acc = acc × value.</summary>
    public static void Multiply(Span<double> acc, double value)
    {
        int n = acc.Length;
        int i = 0;
        if (Accelerated)
        {
            int w = Vector<double>.Count;
            var v = new Vector<double>(value);
            ref double ar = ref MemoryMarshal.GetReference(acc);
            for (; i <= n - w; i += w)
                (Vector.LoadUnsafe(ref ar, (nuint)i) * v).StoreUnsafe(ref ar, (nuint)i);
        }
        for (; i < n; i++)
            acc[i] *= value;
    }

    /// <summary>acc = |acc| − value (the shell modifier).</summary>
    public static void AbsSubtract(Span<double> acc, double value)
    {
        int n = acc.Length;
        int i = 0;
        if (Accelerated)
        {
            int w = Vector<double>.Count;
            var v = new Vector<double>(value);
            ref double ar = ref MemoryMarshal.GetReference(acc);
            for (; i <= n - w; i += w)
                (Vector.Abs(Vector.LoadUnsafe(ref ar, (nuint)i)) - v).StoreUnsafe(ref ar, (nuint)i);
        }
        for (; i < n; i++)
            acc[i] = Math.Abs(acc[i]) - value;
    }

    /// <summary>destination = source − value.</summary>
    public static void Subtract(ReadOnlySpan<double> source, double value, Span<double> destination)
    {
        int n = source.Length;
        int i = 0;
        if (Accelerated)
        {
            int w = Vector<double>.Count;
            var v = new Vector<double>(value);
            ref double sr = ref MemoryMarshal.GetReference(source);
            ref double dr = ref MemoryMarshal.GetReference(destination);
            for (; i <= n - w; i += w)
                (Vector.LoadUnsafe(ref sr, (nuint)i) - v).StoreUnsafe(ref dr, (nuint)i);
        }
        for (; i < n; i++)
            destination[i] = source[i] - value;
    }

    /// <summary>destination = source ÷ value (a true divide — the scalar path divides,
    /// and a reciprocal multiply would not round identically).</summary>
    public static void Divide(ReadOnlySpan<double> source, double value, Span<double> destination)
    {
        int n = source.Length;
        int i = 0;
        if (Accelerated)
        {
            int w = Vector<double>.Count;
            var v = new Vector<double>(value);
            ref double sr = ref MemoryMarshal.GetReference(source);
            ref double dr = ref MemoryMarshal.GetReference(destination);
            for (; i <= n - w; i += w)
                (Vector.LoadUnsafe(ref sr, (nuint)i) / v).StoreUnsafe(ref dr, (nuint)i);
        }
        for (; i < n; i++)
            destination[i] = source[i] / value;
    }

    // ---- coordinate transforms (mirroring EngrCAD.Core term for term) ----

    /// <summary>
    /// Rotates a batch of points by <paramref name="q"/>, mirroring
    /// <c>Quaterniond.Rotate</c> term for term: t = 2·(u × v), result = v + w·t + u × t.
    /// Only exact IEEE add/sub/mul, so it is bit-identical to the scalar path.
    /// </summary>
    public static void Rotate(
        in Quaterniond q,
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z,
        Span<double> outX, Span<double> outY, Span<double> outZ)
    {
        double ux = q.X, uy = q.Y, uz = q.Z, w = q.W;
        int n = x.Length;
        int i = 0;
        if (Accelerated)
        {
            int lanes = Vector<double>.Count;
            var vux = new Vector<double>(ux);
            var vuy = new Vector<double>(uy);
            var vuz = new Vector<double>(uz);
            var vw = new Vector<double>(w);
            var two = new Vector<double>(2.0);
            ref double xr = ref MemoryMarshal.GetReference(x);
            ref double yr = ref MemoryMarshal.GetReference(y);
            ref double zr = ref MemoryMarshal.GetReference(z);
            ref double oxr = ref MemoryMarshal.GetReference(outX);
            ref double oyr = ref MemoryMarshal.GetReference(outY);
            ref double ozr = ref MemoryMarshal.GetReference(outZ);
            for (; i <= n - lanes; i += lanes)
            {
                var vx = Vector.LoadUnsafe(ref xr, (nuint)i);
                var vy = Vector.LoadUnsafe(ref yr, (nuint)i);
                var vz = Vector.LoadUnsafe(ref zr, (nuint)i);
                var tx = (vuy * vz - vuz * vy) * two;
                var ty = (vuz * vx - vux * vz) * two;
                var tz = (vux * vy - vuy * vx) * two;
                (vx + tx * vw + (vuy * tz - vuz * ty)).StoreUnsafe(ref oxr, (nuint)i);
                (vy + ty * vw + (vuz * tx - vux * tz)).StoreUnsafe(ref oyr, (nuint)i);
                (vz + tz * vw + (vux * ty - vuy * tx)).StoreUnsafe(ref ozr, (nuint)i);
            }
        }
        for (; i < n; i++)
        {
            var p = q.Rotate(new Vector3d(x[i], y[i], z[i]));
            outX[i] = p.X;
            outY[i] = p.Y;
            outZ[i] = p.Z;
        }
    }

    /// <summary>
    /// Reflects a batch of points across the plane through <paramref name="point"/> with
    /// unit normal <paramref name="normal"/>: p − n·(2·(n · (p − point))).
    /// </summary>
    public static void Reflect(
        in Vector3d point, in Vector3d normal,
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z,
        Span<double> outX, Span<double> outY, Span<double> outZ)
    {
        int n = x.Length;
        int i = 0;
        if (Accelerated)
        {
            int lanes = Vector<double>.Count;
            var vnx = new Vector<double>(normal.X);
            var vny = new Vector<double>(normal.Y);
            var vnz = new Vector<double>(normal.Z);
            var vpx = new Vector<double>(point.X);
            var vpy = new Vector<double>(point.Y);
            var vpz = new Vector<double>(point.Z);
            var two = new Vector<double>(2.0);
            ref double xr = ref MemoryMarshal.GetReference(x);
            ref double yr = ref MemoryMarshal.GetReference(y);
            ref double zr = ref MemoryMarshal.GetReference(z);
            ref double oxr = ref MemoryMarshal.GetReference(outX);
            ref double oyr = ref MemoryMarshal.GetReference(outY);
            ref double ozr = ref MemoryMarshal.GetReference(outZ);
            for (; i <= n - lanes; i += lanes)
            {
                var vx = Vector.LoadUnsafe(ref xr, (nuint)i);
                var vy = Vector.LoadUnsafe(ref yr, (nuint)i);
                var vz = Vector.LoadUnsafe(ref zr, (nuint)i);
                var dot = vnx * (vx - vpx) + vny * (vy - vpy) + vnz * (vz - vpz);
                var scale = two * dot;
                (vx - vnx * scale).StoreUnsafe(ref oxr, (nuint)i);
                (vy - vny * scale).StoreUnsafe(ref oyr, (nuint)i);
                (vz - vnz * scale).StoreUnsafe(ref ozr, (nuint)i);
            }
        }
        for (; i < n; i++)
        {
            var p = new Vector3d(x[i], y[i], z[i]);
            var d = p - point;
            var reflected = p - normal * (2 * normal.Dot(d));
            outX[i] = reflected.X;
            outY[i] = reflected.Y;
            outZ[i] = reflected.Z;
        }
    }
}
