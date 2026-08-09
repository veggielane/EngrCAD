using System.Linq.Expressions;
using System.Numerics;
using EngrCAD.Core;

namespace EngrCAD.Implicit;

// Domain operations: nodes that move the QUERY POINT rather than combining values.
//
// The whole file turns on one distinction. A translate, a rotate and a mirror are
// ISOMETRIES, so composing a field with them leaves the distance a distance — that is why
// those three live in Operators.cs with no caveat attached. A repetition is a translation
// per cell, so it joins them. A twist, a bend and a taper are NOT isometries: they shear or
// scale space, so the composed value changes faster than the query point moves and the
// number stops being a distance. It stays a correct SIGN everywhere (the solid is exactly
// the pre-image of the child), and it becomes an OVER-estimate of the true distance by at
// most the map's largest singular value.
//
// That factor is what Sdf.LipschitzBound reports, and it is not decoration: SurfaceNets'
// block cull, the narrow-band octree and the projection target all reason "a value of |d|
// proves there is no surface within |d| of here", which is false by exactly this factor.
// Unwidened, they would drop geometry silently.
//
// ONE derivation covers the three non-isometries. Each of their Jacobians reduces, after an
// orthogonal change of basis that costs nothing because singular values are invariant under
// one, to a 2x2 of the form [[g, w], [0, 1]] beside an untouched unit direction — a scale
// with a shear. DomainMath.ShearedScaleNorm is that matrix's spectral norm in closed form,
// and twist, bend and taper each supply their own (g, w).

/// <summary>
/// Closed forms shared by the non-isometric domain operators. See the file header for why
/// there is only one of them.
/// </summary>
internal static class DomainMath
{
    /// <summary>
    /// The spectral norm (largest singular value) of <c>[[g, w], [0, 1]]</c>, in closed
    /// form. Both eigenvalues of the 2x2 Gram matrix are available exactly, so this needs no
    /// iteration and no tolerance.
    /// <para>
    /// Every non-isometric map here reduces to this matrix. A <b>twist</b> about Z has
    /// Jacobian <c>[[1,0,k],[0,1,0],[0,0,1]]</c> with k = |rate|·r, whose non-trivial block
    /// is this with (g, w) = (1, k) — giving the tidy <c>(k + sqrt(k² + 4)) / 2</c>. A
    /// <b>bend</b> gives <c>[[1−k·y, 0],[k·x, 1]]</c>, which is this matrix transposed, and a
    /// matrix and its transpose share singular values. A <b>taper</b> gives
    /// <c>[[1/f, w],[0, 1]]</c> directly. The function is increasing in both |g| and |w|, so
    /// substituting each argument's largest magnitude over a region bounds the norm over
    /// that whole region.
    /// </para>
    /// </summary>
    public static double ShearedScaleNorm(double g, double w)
    {
        double g2 = g * g, w2 = w * w;
        double sum = g2 + w2 + 1;
        double diff = g2 - w2 - 1;
        // The larger Gram eigenvalue; the discriminant cannot be negative.
        return Math.Sqrt(0.5 * (sum + Math.Sqrt(diff * diff + 4 * g2 * w2)));
    }

    /// <summary>The largest |(x, y)| over an axis-aligned box — the furthest of its four
    /// XY corners from the Z axis. Infinite bounds give infinity, which callers must
    /// propagate rather than clamp.</summary>
    public static double MaxRadiusXY(in Aabb region)
    {
        double x = Math.Max(Math.Abs(region.Min.X), Math.Abs(region.Max.X));
        double y = Math.Max(Math.Abs(region.Min.Y), Math.Abs(region.Max.Y));
        return Math.Sqrt(x * x + y * y);
    }

    /// <summary>The box of half-width <paramref name="radius"/> about the Z axis over the
    /// region's own z range — the smallest axis-aligned box guaranteed to contain the image
    /// of <paramref name="region"/> under any map that preserves radius and z (a twist, a
    /// bend). Used both for bounds and to hand a child the region it will actually see.</summary>
    public static Aabb RadialHull(in Aabb region, double radius) =>
        new((-radius, -radius, region.Min.Z), (radius, radius, region.Max.Z));
}

/// <summary>
/// Lattice repetition, finite or infinite. See <see cref="Sdf.Repeat(in Vector3d)"/> for the
/// contract; the interesting parts are all here.
/// </summary>
internal sealed class RepeatSdf : Sdf
{
    private readonly Sdf _source;
    private readonly Vector3d _spacing;
    private readonly Vector3i? _counts;
    private readonly bool _rx, _ry, _rz;

    private RepeatSdf(Sdf source, in Vector3d spacing, Vector3i? counts)
    {
        _source = source;
        _spacing = spacing;
        _counts = counts;
        _rx = Repeated(spacing.X, counts?.X);
        _ry = Repeated(spacing.Y, counts?.Y);
        _rz = Repeated(spacing.Z, counts?.Z);
    }

    private static bool Repeated(double spacing, int? count) =>
        spacing > 0 && (count is null || count > 1);

    public static Sdf Create(Sdf source, in Vector3d spacing, Vector3i? counts)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (spacing.X < 0 || spacing.Y < 0 || spacing.Z < 0)
            throw new ArgumentOutOfRangeException(nameof(spacing), "Spacing must be non-negative.");
        if (counts is { } c && (c.X < 0 || c.Y < 0 || c.Z < 0))
            throw new ArgumentOutOfRangeException(nameof(counts), "Counts must be non-negative.");

        var node = new RepeatSdf(source, spacing, counts);
        if (!node._rx && !node._ry && !node._rz)
            return source;   // nothing is repeated; the node would be an identity wrapper
        node.RequireChildFitsItsCell();
        return node;
    }

    /// <summary>
    /// The precondition that makes this operator sound, checked rather than assumed.
    /// <para>
    /// A point inside instance <c>m</c> must be one the evaluation actually visits, or the
    /// SIGN comes back wrong — the one failure this engine cannot tolerate, since boolean
    /// classification reads it kernel-wide. Visiting the two cells nearest the query point
    /// per axis makes that true exactly when the child lies inside its own cell, and the same
    /// condition is what makes the field CONTINUOUS: at a cell boundary the candidate set
    /// changes, and the candidate being dropped is never the nearest one only because the
    /// child does not reach out of its cell towards it. A discontinuous field is not
    /// Lipschitz at any constant, so the cull could not be widened to cover it — this is a
    /// refusal rather than a wider bound for that reason.
    /// </para>
    /// <para>The slack is the 1e-9 absolute weld tier: a child assembled by exactly-matching
    /// construction (a sphere of radius 1 at spacing 2) compares equal with none, and a
    /// rotated child's bounds carry a few ulps of it.</para>
    /// </summary>
    private void RequireChildFitsItsCell()
    {
        var b = _source.Bounds;
        Check(_rx, b.Min.X, b.Max.X, _spacing.X, "X");
        Check(_ry, b.Min.Y, b.Max.Y, _spacing.Y, "Y");
        Check(_rz, b.Min.Z, b.Max.Z, _spacing.Z, "Z");

        static void Check(bool repeated, double min, double max, double spacing, string axis)
        {
            if (!repeated)
                return;
            double half = spacing / 2 + Tolerance.Default.Linear;
            if (!double.IsFinite(min) || !double.IsFinite(max))
                throw new ArgumentException(
                    $"Repetition along {axis} needs a child with finite bounds on that axis; " +
                    $"intersect the unbounded field with a finite solid first.");
            if (min < -half || max > half)
                throw new ArgumentException(
                    $"Repetition along {axis} at spacing {spacing} needs the child to fit inside one " +
                    $"cell, i.e. bounds within [{-spacing / 2:R}, {spacing / 2:R}]; this child spans " +
                    $"[{min:R}, {max:R}]. Outside that a query point can lie inside an instance the " +
                    $"evaluation never visits and the sign would be wrong.");
        }
    }

    /// <summary>
    /// The cell indices to evaluate along one axis: the two nearest, clamped to the instance
    /// range when the repetition is limited. Returns 1 when the two collapse (an unrepeated
    /// axis, or a point past the end of a limited run).
    /// </summary>
    private static int Candidates(double coordinate, double spacing, int? count, bool repeated, Span<int> into)
    {
        if (!repeated)
        {
            into[0] = 0;
            return 1;
        }
        double t = coordinate / spacing;
        int lo = (int)Math.Floor(t);
        int hi = lo + 1;
        if (count is { } n)
        {
            lo = Math.Clamp(lo, 0, n - 1);
            hi = Math.Clamp(hi, 0, n - 1);
        }
        into[0] = lo;
        if (hi == lo)
            return 1;
        into[1] = hi;
        return 2;
    }

    public override double Evaluate(in Vector3d p)
    {
        Span<int> ci = stackalloc int[2], cj = stackalloc int[2], ck = stackalloc int[2];
        int ni = Candidates(p.X, _spacing.X, _counts?.X, _rx, ci);
        int nj = Candidates(p.Y, _spacing.Y, _counts?.Y, _ry, cj);
        int nk = Candidates(p.Z, _spacing.Z, _counts?.Z, _rz, ck);

        double best = double.PositiveInfinity;
        for (int a = 0; a < ni; a++)
        {
            for (int b = 0; b < nj; b++)
            {
                for (int c = 0; c < nk; c++)
                {
                    var q = new Vector3d(
                        p.X - _spacing.X * ci[a],
                        p.Y - _spacing.Y * cj[b],
                        p.Z - _spacing.Z * ck[c]);
                    best = Math.Min(best, _source.Evaluate(q));
                }
            }
        }
        return best;
    }

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        int n = x.Length;
        if (n == 0)
            return;

        // Two passes per repeated axis, each a whole batch through the child (so the child
        // still vectorizes), folded with the same Min the union uses.
        int passes = (_rx ? 2 : 1) * (_ry ? 2 : 1) * (_rz ? 2 : 1);
        using var moved = new BatchScratch3(n);
        using var scratch = new BatchScratch(n);
        bool first = true;

        for (int pass = 0; pass < passes; pass++)
        {
            int sx = _rx ? pass & 1 : 0;
            int sy = _ry ? (pass >> (_rx ? 1 : 0)) & 1 : 0;
            int sz = _rz ? (pass >> ((_rx ? 1 : 0) + (_ry ? 1 : 0))) & 1 : 0;

            for (int i = 0; i < n; i++)
            {
                moved.X[i] = x[i] - _spacing.X * Index(x[i], _spacing.X, _counts?.X, _rx, sx);
                moved.Y[i] = y[i] - _spacing.Y * Index(y[i], _spacing.Y, _counts?.Y, _ry, sy);
                moved.Z[i] = z[i] - _spacing.Z * Index(z[i], _spacing.Z, _counts?.Z, _rz, sz);
            }

            if (first)
            {
                _source.EvaluateBatch(moved.X, moved.Y, moved.Z, distances);
                first = false;
            }
            else
            {
                _source.EvaluateBatch(moved.X, moved.Y, moved.Z, scratch.Span);
                SdfBatch.Min(distances, scratch.Span);
            }
        }
    }

    /// <summary>The <paramref name="which"/>-th (0 or 1) candidate index for one coordinate —
    /// the scalar path's <see cref="Candidates"/> unrolled, so the two agree by construction.</summary>
    private static int Index(double coordinate, double spacing, int? count, bool repeated, int which)
    {
        if (!repeated)
            return 0;
        int index = (int)Math.Floor(coordinate / spacing) + which;
        return count is { } n ? Math.Clamp(index, 0, n - 1) : index;
    }

    public override Aabb Bounds
    {
        get
        {
            var b = _source.Bounds;
            var min = b.Min;
            var max = b.Max;
            Extend(_rx, _counts?.X, _spacing.X, ref min, ref max, 0);
            Extend(_ry, _counts?.Y, _spacing.Y, ref min, ref max, 1);
            Extend(_rz, _counts?.Z, _spacing.Z, ref min, ref max, 2);
            return new Aabb(min, max);

            static void Extend(
                bool repeated, int? count, double spacing, ref Vector3d min, ref Vector3d max, int axis)
            {
                if (!repeated)
                    return;
                if (count is { } n)
                {
                    double reach = spacing * (n - 1);
                    max = axis switch
                    {
                        0 => new Vector3d(max.X + reach, max.Y, max.Z),
                        1 => new Vector3d(max.X, max.Y + reach, max.Z),
                        _ => new Vector3d(max.X, max.Y, max.Z + reach),
                    };
                }
                else
                {
                    min = axis switch
                    {
                        0 => new Vector3d(double.NegativeInfinity, min.Y, min.Z),
                        1 => new Vector3d(min.X, double.NegativeInfinity, min.Z),
                        _ => new Vector3d(min.X, min.Y, double.NegativeInfinity),
                    };
                    max = axis switch
                    {
                        0 => new Vector3d(double.PositiveInfinity, max.Y, max.Z),
                        1 => new Vector3d(max.X, double.PositiveInfinity, max.Z),
                        _ => new Vector3d(max.X, max.Y, double.PositiveInfinity),
                    };
                }
            }
        }
    }

    /// <summary>
    /// Unrolls the candidate lattice: every combination is emitted, and a run-time collapse
    /// (a clamp that makes two candidates equal) simply evaluates the same offset twice,
    /// whose <c>Min</c> is that value — the identical double the scalar path's shorter loop
    /// produces, since <see cref="Math.Min"/> is symmetric even on a signed-zero tie.
    /// </summary>
    internal override Expression BuildExpression(SdfExpression e)
    {
        Expression? best = null;
        int nx = _rx ? 2 : 1, ny = _ry ? 2 : 1, nz = _rz ? 2 : 1;
        for (int a = 0; a < nx; a++)
        {
            for (int b = 0; b < ny; b++)
            {
                for (int c = 0; c < nz; c++)
                {
                    var value = e.BuildAt(_source,
                        Offset(e.X, _spacing.X, _counts?.X, _rx, a),
                        Offset(e.Y, _spacing.Y, _counts?.Y, _ry, b),
                        Offset(e.Z, _spacing.Z, _counts?.Z, _rz, c));
                    best = best is null ? value : SdfExpression.Min(e.Let(best), value);
                }
            }
        }
        return best!;

        // p − spacing · index, forming the index through int exactly as Evaluate does.
        static Expression Offset(
            Expression coordinate, double spacing, int? count, bool repeated, int which)
        {
            if (!repeated)
                return coordinate;
            var t = Expression.Divide(coordinate, SdfExpression.Const(spacing));
            Expression index = Expression.Add(
                Expression.Convert(SdfExpression.Floor(t), typeof(int)),
                Expression.Constant(which, typeof(int)));
            if (count is { } n)
                index = Expression.Call(
                    typeof(Math).GetMethod(nameof(Math.Clamp), [typeof(int), typeof(int), typeof(int)])!,
                    index, Expression.Constant(0), Expression.Constant(n - 1));
            return Expression.Subtract(
                coordinate,
                Expression.Multiply(
                    SdfExpression.Const(spacing), Expression.Convert(index, typeof(double))));
        }
    }

    /// <summary>A translation per cell is an isometry, so the child's own bound carries over.
    /// The child sees its own cell wherever the query lands, so its bound is asked for over
    /// the cell-sized region around the origin rather than over the caller's whole lattice.</summary>
    public override double LipschitzBound(in Aabb region)
    {
        var min = region.Min;
        var max = region.Max;
        Fold(_rx, _spacing.X, ref min, ref max, 0);
        Fold(_ry, _spacing.Y, ref min, ref max, 1);
        Fold(_rz, _spacing.Z, ref min, ref max, 2);
        return _source.LipschitzBound(new Aabb(min, max));

        static void Fold(bool repeated, double spacing, ref Vector3d min, ref Vector3d max, int axis)
        {
            if (!repeated)
                return;
            // A candidate offset is within one full spacing of the child's own cell.
            min = axis switch
            {
                0 => new Vector3d(-spacing, min.Y, min.Z),
                1 => new Vector3d(min.X, -spacing, min.Z),
                _ => new Vector3d(min.X, min.Y, -spacing),
            };
            max = axis switch
            {
                0 => new Vector3d(spacing, max.Y, max.Z),
                1 => new Vector3d(max.X, spacing, max.Z),
                _ => new Vector3d(max.X, max.Y, spacing),
            };
        }
    }
}

/// <summary>Twist about Z. See <see cref="Sdf.Twist"/> for the contract.</summary>
internal sealed class TwistSdf(Sdf source, double radiansPerUnit) : Sdf
{
    public override double Evaluate(in Vector3d p)
    {
        double angle = -radiansPerUnit * p.Z;
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return source.Evaluate(new Vector3d(c * p.X - s * p.Y, s * p.X + c * p.Y, p.Z));
    }

    /// <summary>
    /// The coordinate map runs scalar (see the class docs — no bit-identical vector sine),
    /// but it runs over the whole batch before the child sees it, so the subtree below still
    /// vectorizes. That is where the time is: the map is three multiplies and a sin/cos pair,
    /// the child is a CSG tree.
    /// </summary>
    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        int n = x.Length;
        if (n == 0)
            return;
        using var moved = new BatchScratch3(n);
        for (int i = 0; i < n; i++)
        {
            double angle = -radiansPerUnit * z[i];
            double c = Math.Cos(angle), s = Math.Sin(angle);
            moved.X[i] = c * x[i] - s * y[i];
            moved.Y[i] = s * x[i] + c * y[i];
            moved.Z[i] = z[i];
        }
        source.EvaluateBatch(moved.X, moved.Y, moved.Z, distances);
    }

    /// <summary>The map preserves radius and z, so the solid stays inside the child's own
    /// radial shell over the same z range.</summary>
    public override Aabb Bounds
    {
        get
        {
            var b = source.Bounds;
            return DomainMath.RadialHull(b, DomainMath.MaxRadiusXY(b));
        }
    }

    public override double LipschitzBound(in Aabb region)
    {
        double radius = DomainMath.MaxRadiusXY(region);
        double child = source.LipschitzBound(DomainMath.RadialHull(region, radius));
        return child * DomainMath.ShearedScaleNorm(1, Math.Abs(radiansPerUnit) * radius);
    }

    internal override Expression BuildExpression(SdfExpression e)
    {
        var angle = e.Let(Expression.Multiply(
            SdfExpression.Const(-radiansPerUnit), e.Z));
        var c = e.Let(SdfExpression.Cos(angle));
        var s = e.Let(SdfExpression.Sin(angle));
        var (x, y) = (e.X, e.Y);
        return e.BuildAt(source,
            Expression.Subtract(Expression.Multiply(c, x), Expression.Multiply(s, y)),
            Expression.Add(Expression.Multiply(s, x), Expression.Multiply(c, y)),
            e.Z);
    }
}

/// <summary>Bend about Z. See <see cref="Sdf.Bend"/> for the contract.</summary>
internal sealed class BendSdf(Sdf source, double curvature) : Sdf
{
    public override double Evaluate(in Vector3d p)
    {
        double angle = curvature * p.X;
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return source.Evaluate(new Vector3d(c * p.X - s * p.Y, s * p.X + c * p.Y, p.Z));
    }

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        int n = x.Length;
        if (n == 0)
            return;
        using var moved = new BatchScratch3(n);
        for (int i = 0; i < n; i++)
        {
            double angle = curvature * x[i];
            double c = Math.Cos(angle), s = Math.Sin(angle);
            moved.X[i] = c * x[i] - s * y[i];
            moved.Y[i] = s * x[i] + c * y[i];
            moved.Z[i] = z[i];
        }
        source.EvaluateBatch(moved.X, moved.Y, moved.Z, distances);
    }

    /// <summary>A rotation about Z whose angle depends on x still preserves |(x, y)| and z,
    /// so the bound is the twist's.</summary>
    public override Aabb Bounds
    {
        get
        {
            var b = source.Bounds;
            return DomainMath.RadialHull(b, DomainMath.MaxRadiusXY(b));
        }
    }

    /// <summary>
    /// The Jacobian is <c>[[1 − k·y, 0], [k·x, 1]]</c> beside an untouched Z, whose singular
    /// values are those of its transpose <c>[[1 − k·y, k·x], [0, 1]]</c> — the shared
    /// sheared-scale form. Both arguments take their largest magnitude over the region.
    /// </summary>
    public override double LipschitzBound(in Aabb region)
    {
        double radius = DomainMath.MaxRadiusXY(region);
        double child = source.LipschitzBound(DomainMath.RadialHull(region, radius));
        double xMax = Math.Max(Math.Abs(region.Min.X), Math.Abs(region.Max.X));
        double yMax = Math.Max(Math.Abs(region.Min.Y), Math.Abs(region.Max.Y));
        double g = 1 + Math.Abs(curvature) * yMax;   // max |1 − k·y| over the region
        double w = Math.Abs(curvature) * xMax;
        return child * DomainMath.ShearedScaleNorm(g, w);
    }

    internal override Expression BuildExpression(SdfExpression e)
    {
        var angle = e.Let(Expression.Multiply(SdfExpression.Const(curvature), e.X));
        var c = e.Let(SdfExpression.Cos(angle));
        var s = e.Let(SdfExpression.Sin(angle));
        var (x, y) = (e.X, e.Y);
        return e.BuildAt(source,
            Expression.Subtract(Expression.Multiply(c, x), Expression.Multiply(s, y)),
            Expression.Add(Expression.Multiply(s, x), Expression.Multiply(c, y)),
            e.Z);
    }
}

/// <summary>Linear taper along Z. See <see cref="Sdf.Taper"/> for the contract.</summary>
internal sealed class TaperSdf : Sdf
{
    private readonly Sdf _source;
    private readonly double _z0, _z1, _bottom, _top, _slope;

    public TaperSdf(Sdf source, double bottomScale, double topScale)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (bottomScale <= 0 || topScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(bottomScale), "Taper scales must be positive.");
        var b = source.Bounds;
        if (!double.IsFinite(b.Min.Z) || !double.IsFinite(b.Max.Z) || b.Max.Z <= b.Min.Z)
            throw new ArgumentException(
                "Taper needs a child with a finite, non-degenerate Z extent — the scale is " +
                "interpolated across it.", nameof(source));
        _source = source;
        _z0 = b.Min.Z;
        _z1 = b.Max.Z;
        _bottom = bottomScale;
        _top = topScale;
        _slope = (topScale - bottomScale) / (_z1 - _z0);
    }

    /// <summary>The scale at height z: linear across the child's own Z extent and held at the
    /// end values beyond it, so it is bounded away from zero at every query point in space
    /// rather than only inside the model.</summary>
    private double ScaleAt(double z)
    {
        if (z <= _z0)
            return _bottom;
        if (z >= _z1)
            return _top;
        return _bottom + _slope * (z - _z0);
    }

    public override double Evaluate(in Vector3d p)
    {
        double f = ScaleAt(p.Z);
        return _source.Evaluate(new Vector3d(p.X / f, p.Y / f, p.Z));
    }

    /// <summary>Division only — no transcendental — so this one really does vectorize; the
    /// clamped ramp is a min/max pair.</summary>
    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        int n = x.Length;
        if (n == 0)
            return;
        using var moved = new BatchScratch3(n);
        int i = 0;
        if (SdfBatch.Accelerated)
        {
            int w = Vector<double>.Count;
            var vz0 = new Vector<double>(_z0);
            var vz1 = new Vector<double>(_z1);
            var vbottom = new Vector<double>(_bottom);
            var vtop = new Vector<double>(_top);
            var vslope = new Vector<double>(_slope);
            ref double xr = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(x);
            ref double yr = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(y);
            ref double zr = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(z);
            ref double mx = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(moved.X);
            ref double my = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(moved.Y);
            ref double mz = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(moved.Z);
            for (; i <= n - w; i += w)
            {
                var vx = Vector.LoadUnsafe(ref xr, (nuint)i);
                var vy = Vector.LoadUnsafe(ref yr, (nuint)i);
                var vzz = Vector.LoadUnsafe(ref zr, (nuint)i);
                // Term for term with ScaleAt: the ramp, then its two clamps as selects, in
                // the same order — so a z landing exactly on an end takes the same branch.
                var f = vbottom + vslope * (vzz - vz0);
                f = Vector.ConditionalSelect(Vector.LessThanOrEqual(vzz, vz0), vbottom, f);
                f = Vector.ConditionalSelect(Vector.GreaterThanOrEqual(vzz, vz1), vtop, f);
                (vx / f).StoreUnsafe(ref mx, (nuint)i);
                (vy / f).StoreUnsafe(ref my, (nuint)i);
                vzz.StoreUnsafe(ref mz, (nuint)i);
            }
        }
        for (; i < n; i++)
        {
            double f = ScaleAt(z[i]);
            moved.X[i] = x[i] / f;
            moved.Y[i] = y[i] / f;
            moved.Z[i] = z[i];
        }
        _source.EvaluateBatch(moved.X, moved.Y, moved.Z, distances);
    }

    public override Aabb Bounds
    {
        get
        {
            var b = _source.Bounds;
            double s = Math.Max(_bottom, _top);
            return new Aabb(
                (b.Min.X * s, b.Min.Y * s, b.Min.Z),
                (b.Max.X * s, b.Max.Y * s, b.Max.Z));
        }
    }

    /// <summary>
    /// The Jacobian is <c>diag(1/f, 1/f, 1)</c> plus a z-column <c>(−x f′/f², −y f′/f², 0)</c>,
    /// which after rotating the XY plane onto that column is the shared sheared-scale form
    /// with g = 1/f and w = r·|f′|/f², beside an untouched 1/f direction.
    /// </summary>
    public override double LipschitzBound(in Aabb region)
    {
        double fMin = Math.Min(_bottom, _top);
        double g = 1 / fMin;
        double radius = DomainMath.MaxRadiusXY(region);
        double w = radius * Math.Abs(_slope) / (fMin * fMin);
        // The child sees the region divided by the scale, so its widest reach is region/fMin.
        var seen = new Aabb(
            (region.Min.X / fMin, region.Min.Y / fMin, region.Min.Z),
            (region.Max.X / fMin, region.Max.Y / fMin, region.Max.Z));
        return _source.LipschitzBound(seen) * Math.Max(g, DomainMath.ShearedScaleNorm(g, w));
    }

    internal override Expression BuildExpression(SdfExpression e)
    {
        // ScaleAt's two early returns become nested conditions in the same order.
        var ramp = Expression.Add(
            SdfExpression.Const(_bottom),
            Expression.Multiply(
                SdfExpression.Const(_slope),
                Expression.Subtract(e.Z, SdfExpression.Const(_z0))));
        var f = e.Let(Expression.Condition(
            Expression.LessThanOrEqual(e.Z, SdfExpression.Const(_z0)),
            SdfExpression.Const(_bottom),
            Expression.Condition(
                Expression.GreaterThanOrEqual(e.Z, SdfExpression.Const(_z1)),
                SdfExpression.Const(_top),
                ramp)));
        return e.BuildAt(_source,
            Expression.Divide(e.X, f), Expression.Divide(e.Y, f), e.Z);
    }
}

/// <summary>Elongation along the axes. See <see cref="Sdf.Elongate"/> for the contract.</summary>
internal sealed class ElongateSdf : Sdf
{
    private readonly Sdf _source;
    private readonly Vector3d _amounts;

    public ElongateSdf(Sdf source, in Vector3d amounts)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (amounts.X < 0 || amounts.Y < 0 || amounts.Z < 0)
            throw new ArgumentOutOfRangeException(nameof(amounts), "Elongation must be non-negative.");
        _source = source;
        _amounts = amounts;
    }

    public override double Evaluate(in Vector3d p) =>
        _source.Evaluate(new Vector3d(
            p.X - Math.Clamp(p.X, -_amounts.X, _amounts.X),
            p.Y - Math.Clamp(p.Y, -_amounts.Y, _amounts.Y),
            p.Z - Math.Clamp(p.Z, -_amounts.Z, _amounts.Z)));

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        int n = x.Length;
        if (n == 0)
            return;
        using var moved = new BatchScratch3(n);
        SdfBatch.SubtractClamped(x, _amounts.X, moved.X);
        SdfBatch.SubtractClamped(y, _amounts.Y, moved.Y);
        SdfBatch.SubtractClamped(z, _amounts.Z, moved.Z);
        _source.EvaluateBatch(moved.X, moved.Y, moved.Z, distances);
    }

    public override Aabb Bounds
    {
        get
        {
            var b = _source.Bounds;
            return new Aabb(b.Min - _amounts, b.Max + _amounts);
        }
    }

    /// <summary>Each component of the map is <c>t − clamp(t)</c>, whose slope is 0 or 1, so
    /// the map is 1-Lipschitz and the child's bound carries over unchanged.</summary>
    public override double LipschitzBound(in Aabb region)
    {
        var b = new Aabb(region.Min - _amounts, region.Max + _amounts);
        return _source.LipschitzBound(b);
    }

    internal override Expression BuildExpression(SdfExpression e) =>
        e.BuildAt(_source,
            Expression.Subtract(e.X, SdfExpression.Clamp(e.X, -_amounts.X, _amounts.X)),
            Expression.Subtract(e.Y, SdfExpression.Clamp(e.Y, -_amounts.Y, _amounts.Y)),
            Expression.Subtract(e.Z, SdfExpression.Clamp(e.Z, -_amounts.Z, _amounts.Z)));
}

/// <summary>Sinusoidal displacement. See <see cref="Sdf.Displace"/> for the contract.</summary>
internal sealed class DisplaceSdf : Sdf
{
    private readonly Sdf _source;
    private readonly double _amplitude;
    private readonly Vector3d _frequency;
    private readonly Aabb? _explicitBounds;

    public DisplaceSdf(Sdf source, double amplitude, in Vector3d frequency, Aabb? bounds = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (amplitude < 0)
            throw new ArgumentOutOfRangeException(nameof(amplitude), "Amplitude must be non-negative.");
        _source = source;
        _amplitude = amplitude;
        _frequency = frequency;
        _explicitBounds = bounds;
    }

    private double Ripple(in Vector3d p) =>
        _amplitude
        * Math.Sin(_frequency.X * p.X)
        * Math.Sin(_frequency.Y * p.Y)
        * Math.Sin(_frequency.Z * p.Z);

    public override double Evaluate(in Vector3d p) => _source.Evaluate(p) - Ripple(p);

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        _source.EvaluateBatch(x, y, z, distances);
        for (int i = 0; i < x.Length; i++)
            distances[i] -= Ripple(new Vector3d(x[i], y[i], z[i]));
    }

    /// <summary>
    /// The ripple adds material wherever the child reads below the amplitude, so the bound this
    /// needs of its child is <b>not</b> "the magnitude is a true distance" — it is the weaker
    /// and checkable <c>{child &lt; t} ⊆ child.Bounds.Expanded(t)</c>, i.e. the child never
    /// reports less than the per-axis escape from its own bounds. See
    /// <see cref="Sdf.Displace(double, in Vector3d)"/> for which nodes have that property and
    /// which measurably do not.
    /// </summary>
    public override Aabb Bounds => _explicitBounds ?? _source.Bounds.Expanded(_amplitude);

    /// <summary>The gradient of <c>a·sin·sin·sin</c> is bounded by <c>a·|frequency|</c>
    /// (each partial carries one frequency and a product of sines no larger than 1), and it
    /// adds to the child's.</summary>
    public override double LipschitzBound(in Aabb region) =>
        _source.LipschitzBound(region) + _amplitude * _frequency.Length;

    internal override Expression BuildExpression(SdfExpression e)
    {
        var ripple = Expression.Multiply(
            Expression.Multiply(
                Expression.Multiply(
                    SdfExpression.Const(_amplitude),
                    SdfExpression.Sin(Expression.Multiply(SdfExpression.Const(_frequency.X), e.X))),
                SdfExpression.Sin(Expression.Multiply(SdfExpression.Const(_frequency.Y), e.Y))),
            SdfExpression.Sin(Expression.Multiply(SdfExpression.Const(_frequency.Z), e.Z)));
        return Expression.Subtract(e.Build(_source), ripple);
    }
}
