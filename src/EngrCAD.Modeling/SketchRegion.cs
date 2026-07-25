using System.Numerics;
using System.Runtime.InteropServices;
using EngrCAD.Core;
using EngrCAD.Implicit;

namespace EngrCAD.Modeling;

/// <summary>
/// A <see cref="Sketch"/> as an exact 2D signed distance field
/// (<see cref="IPlanarRegion"/>): magnitude is the distance to the nearest segment
/// (lines/arcs exact, béziers Newton-refined), sign is even–odd ray parity computed
/// from precomputed y-monotone pieces with exact crossings — holes fall out for free.
/// <para>
/// This field is the inner loop of <see cref="Sdf.ExtrudedRegion"/> and
/// <see cref="Sdf.RevolvedRegion"/>, so the constructor flattens the segment list into
/// structure-of-arrays form: line and full-circle distances become lane-wise kernels over
/// contiguous doubles (no virtual call per segment per point), everything else keeps its
/// own exact <c>Distance</c> behind a bounding-box reject, and the ray-parity pieces go
/// into a bucket index on y so a query touches only the pieces that can possibly cross it.
/// <b>Every one of those is a pure restructuring: the returned double is bit-for-bit what
/// the plain segment loop returns</b> — the kernels transcribe the segment classes'
/// arithmetic term for term, the reject is proven conservative, and both the bucket index
/// and the min-fold are order-independent. <c>SketchRegionKernelTests</c> holds that line.
/// </para>
/// </summary>
public sealed class SketchRegion : IPlanarRegion
{
    private readonly List<SketchSegment> _segments = [];
    private readonly List<MonotonePiece> _pieces = [];

    public Aabb Bounds { get; }

    /// <param name="forRevolution">
    /// When the region is destined for <c>Sdf.RevolvedRegion</c>, boundary segments
    /// lying on the axis (x = 0) are excluded from the *distance* — the axis is
    /// interior to the solid of revolution, not a surface. Parity is unaffected: a +x
    /// ray from any r ≥ 0 query never crosses x = 0.
    /// </param>
    public SketchRegion(Sketch sketch, bool forRevolution = false)
    {
        Collect(sketch, forRevolution);
        foreach (var hole in sketch.Holes)
            Collect(hole, forRevolution);
        Bounds = sketch.Bounds;

        BuildDistanceTables();
        BuildParityIndex();
    }

    private void Collect(Sketch sketch, bool forRevolution)
    {
        foreach (var segment in sketch.Segments)
        {
            // Weld-scale (1e-9 = Tolerance.Default.Linear) on-axis classification —
            // must agree with RevolveFullTurn's pole detection so all representations
            // drop the same on-axis stretches.
            bool onAxis = forRevolution
                && Math.Abs(segment.Start.X) <= 1e-9
                && Math.Abs(segment.End.X) <= 1e-9
                && segment.Bounds().Max.X <= 1e-9;
            if (!onAxis)
                _segments.Add(segment);
            _pieces.AddRange(segment.MonotonePieces());
        }
    }

    public double SignedDistance(in Vector2d point)
    {
        double distance = Distance(point.X, point.Y);
        return (Crossings(point.X, point.Y) & 1) == 1 ? -distance : distance;
    }

    /// <inheritdoc/>
    public void SignedDistance(ReadOnlySpan<double> x, ReadOnlySpan<double> y, Span<double> distances)
    {
        int n = x.Length;
        if (n == 0)
            return;

        distances[..n].Fill(double.PositiveInfinity);

        // Segments in construction order: the fold is a running minimum, which is
        // order-independent over these non-negative results (no NaN, and every distance
        // comes out of Math.Sqrt/Math.Abs so none is a negative zero), but keeping the
        // order makes this a literal transcription of the scalar loop.
        for (int s = 0; s < _kinds.Length; s++)
        {
            switch (_kinds[s])
            {
                case SegmentKind.Line:
                    LineMinimum(x, y, distances, _a[s], _b[s], _c[s], _d[s], _e[s]);
                    break;
                case SegmentKind.FullCircle:
                    CircleMinimum(x, y, distances, _a[s], _b[s], _c[s]);
                    break;
                default:
                    GeneralMinimum(x, y, distances, s);
                    break;
            }
        }

        for (int i = 0; i < n; i++)
        {
            if ((Crossings(x[i], y[i]) & 1) == 1)
                distances[i] = -distances[i];
        }
    }

    // ------------------------------------------------------------------ distance tables

    private enum SegmentKind : byte
    {
        /// <summary>Anything whose own <c>Distance</c> is called through the abstraction
        /// (partial arcs — <c>Atan2</c> has no bit-exact vector form — and béziers).</summary>
        General = 0,
        Line = 1,
        FullCircle = 2,
    }

    private SegmentKind[] _kinds = [];
    private double[] _a = [], _b = [], _c = [], _d = [], _e = [];
    private double[] _minX = [], _maxX = [], _minY = [], _maxY = [];
    private SketchSegment[] _general = [];

    /// <summary>
    /// Relative slack that makes the bounding-box reject provably conservative. The
    /// computed lower bound carries at most ~4 ulps of arithmetic error, so requiring it to
    /// exceed the running best by ~9 ulps guarantees the rejected segment really is farther
    /// — the reject can never change which segment wins. Scale-free by construction (it
    /// multiplies a squared distance), which is the point: an absolute epsilon on a squared
    /// quantity fails quadratically with model scale.
    /// </summary>
    private const double RejectSlack = 1e-15;

    private void BuildDistanceTables()
    {
        int n = _segments.Count;
        _kinds = new SegmentKind[n];
        _a = new double[n];
        _b = new double[n];
        _c = new double[n];
        _d = new double[n];
        _e = new double[n];
        _minX = new double[n];
        _maxX = new double[n];
        _minY = new double[n];
        _maxY = new double[n];
        _general = new SketchSegment[n];

        for (int s = 0; s < n; s++)
        {
            var segment = _segments[s];
            var bounds = segment.Bounds();
            _minX[s] = bounds.Min.X;
            _maxX[s] = bounds.Max.X;
            _minY[s] = bounds.Min.Y;
            _maxY[s] = bounds.Max.Y;
            _general[s] = segment;

            switch (segment)
            {
                // Transcribed from LineSeg.Distance: direction and its squared length are
                // loop invariants, so they are computed once here instead of per query.
                case LineSeg line:
                {
                    var direction = line.End - line.Start;
                    _kinds[s] = SegmentKind.Line;
                    _a[s] = line.Start.X;
                    _b[s] = line.Start.Y;
                    _c[s] = direction.X;
                    _d[s] = direction.Y;
                    _e[s] = direction.LengthSquared;
                    break;
                }
                // Transcribed from ArcSeg.Distance: a full circle's AngleInSweep is
                // unconditionally true, so the Atan2 it feeds is dead and the distance is
                // just the radial residual.
                case ArcSeg { IsFullCircle: true } arc:
                    _kinds[s] = SegmentKind.FullCircle;
                    _a[s] = arc.Center.X;
                    _b[s] = arc.Center.Y;
                    _c[s] = arc.Radius;
                    break;
                default:
                    _kinds[s] = SegmentKind.General;
                    break;
            }
        }
    }

    private double Distance(double px, double py)
    {
        double best = double.PositiveInfinity;
        for (int s = 0; s < _kinds.Length; s++)
        {
            switch (_kinds[s])
            {
                case SegmentKind.Line:
                    best = Math.Min(best, LineDistance(px, py, _a[s], _b[s], _c[s], _d[s], _e[s]));
                    break;
                case SegmentKind.FullCircle:
                    best = Math.Min(best, CircleDistance(px, py, _a[s], _b[s], _c[s]));
                    break;
                default:
                    if (!Rejected(px, py, s, best))
                        best = Math.Min(best, _general[s].Distance(new Vector2d(px, py)));
                    break;
            }
        }
        return best;
    }

    /// <summary>
    /// True when the segment's bounding box is provably farther from (px, py) than
    /// <paramref name="best"/>, so evaluating it cannot lower the running minimum. Only
    /// worth its own cost in front of the general kernels (a bézier costs 17 curve
    /// evaluations plus 8 Newton steps); the line and circle kernels are cheaper than the
    /// test.
    /// </summary>
    private bool Rejected(double px, double py, int s, double best)
    {
        if (double.IsPositiveInfinity(best))
            return false;
        double gapX = Math.Max(Math.Max(_minX[s] - px, px - _maxX[s]), 0);
        double gapY = Math.Max(Math.Max(_minY[s] - py, py - _maxY[s]), 0);
        return gapX * gapX + gapY * gapY > best * best * (1 + RejectSlack);
    }

    /// <summary>Term for term <c>LineSeg.Distance</c>. The 1e-24 degenerate-length guard is
    /// transcribed from there and must stay in lockstep with it (a test asserts it does).</summary>
    private static double LineDistance(
        double px, double py, double sx, double sy, double dx, double dy, double lengthSquared)
    {
        double t = lengthSquared < 1e-24
            ? 0
            : Math.Clamp(((px - sx) * dx + (py - sy) * dy) / lengthSquared, 0, 1);
        double ax = px - (sx + dx * t);
        double ay = py - (sy + dy * t);
        return Math.Sqrt(ax * ax + ay * ay);
    }

    /// <summary>Term for term <c>ArcSeg.Distance</c> for a full circle.</summary>
    private static double CircleDistance(double px, double py, double cx, double cy, double radius)
    {
        double ox = px - cx;
        double oy = py - cy;
        return Math.Abs(Math.Sqrt(ox * ox + oy * oy) - radius);
    }

    // ------------------------------------------------------------------- lane-wise forms

    /// <summary>
    /// True when <see cref="Vector{T}"/> maps to real SIMD registers wider than one double
    /// (a JIT constant, so the guarded branch folds away).
    /// </summary>
    private static bool Accelerated => Vector.IsHardwareAccelerated && Vector<double>.Count > 1;

    private static void LineMinimum(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, Span<double> best,
        double sx, double sy, double dx, double dy, double lengthSquared)
    {
        int n = x.Length;
        int i = 0;
        // The degenerate-length branch is a whole-segment property, so hoisting it out of
        // the loop cannot change any lane; a degenerate line stays on the scalar path.
        if (Accelerated && !(lengthSquared < 1e-24))
        {
            int w = Vector<double>.Count;
            var vsx = new Vector<double>(sx);
            var vsy = new Vector<double>(sy);
            var vdx = new Vector<double>(dx);
            var vdy = new Vector<double>(dy);
            var vlen = new Vector<double>(lengthSquared);
            ref double xr = ref MemoryMarshal.GetReference(x);
            ref double yr = ref MemoryMarshal.GetReference(y);
            ref double br = ref MemoryMarshal.GetReference(best);
            for (; i <= n - w; i += w)
            {
                var px = Vector.LoadUnsafe(ref xr, (nuint)i);
                var py = Vector.LoadUnsafe(ref yr, (nuint)i);
                var t = Vector.Min(
                    Vector.Max(((px - vsx) * vdx + (py - vsy) * vdy) / vlen, Vector<double>.Zero),
                    Vector<double>.One);
                var ax = px - (vsx + vdx * t);
                var ay = py - (vsy + vdy * t);
                var distance = Vector.SquareRoot(ax * ax + ay * ay);
                Vector.Min(Vector.LoadUnsafe(ref br, (nuint)i), distance).StoreUnsafe(ref br, (nuint)i);
            }
        }
        for (; i < n; i++)
            best[i] = Math.Min(best[i], LineDistance(x[i], y[i], sx, sy, dx, dy, lengthSquared));
    }

    private static void CircleMinimum(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, Span<double> best,
        double cx, double cy, double radius)
    {
        int n = x.Length;
        int i = 0;
        if (Accelerated)
        {
            int w = Vector<double>.Count;
            var vcx = new Vector<double>(cx);
            var vcy = new Vector<double>(cy);
            var vr = new Vector<double>(radius);
            ref double xr = ref MemoryMarshal.GetReference(x);
            ref double yr = ref MemoryMarshal.GetReference(y);
            ref double br = ref MemoryMarshal.GetReference(best);
            for (; i <= n - w; i += w)
            {
                var ox = Vector.LoadUnsafe(ref xr, (nuint)i) - vcx;
                var oy = Vector.LoadUnsafe(ref yr, (nuint)i) - vcy;
                var distance = Vector.Abs(Vector.SquareRoot(ox * ox + oy * oy) - vr);
                Vector.Min(Vector.LoadUnsafe(ref br, (nuint)i), distance).StoreUnsafe(ref br, (nuint)i);
            }
        }
        for (; i < n; i++)
            best[i] = Math.Min(best[i], CircleDistance(x[i], y[i], cx, cy, radius));
    }

    private void GeneralMinimum(ReadOnlySpan<double> x, ReadOnlySpan<double> y, Span<double> best, int s)
    {
        var segment = _general[s];
        for (int i = 0; i < x.Length; i++)
        {
            if (Rejected(x[i], y[i], s, best[i]))
                continue;
            best[i] = Math.Min(best[i], segment.Distance(new Vector2d(x[i], y[i])));
        }
    }

    // -------------------------------------------------------------------- parity index

    private MonotonePiece[] _pieceArray = [];
    private double[] _pieceY0 = [], _pieceY1 = [];
    private int[] _bucketStart = [];
    private int[] _bucketItems = [];
    private double _bucketOrigin;
    private double _bucketScale;
    private int _bucketCount;

    /// <summary>
    /// Buckets the y-monotone pieces by the y interval they span. A piece can only cross a
    /// ray at height y if y lies inside that interval, so a query need consider one
    /// bucket's pieces and no others — exactly the same crossing set, just without walking
    /// every piece in the sketch. The bucket map is monotone in y, so bucketing a piece
    /// over [bucket(yMin), bucket(yMax)] is sufficient with no rounding slack needed.
    /// </summary>
    private void BuildParityIndex()
    {
        _pieceArray = [.. _pieces];
        int n = _pieceArray.Length;
        _pieceY0 = new double[n];
        _pieceY1 = new double[n];
        if (n == 0)
        {
            _bucketCount = 1;
            _bucketStart = [0, 0];
            _bucketItems = [];
            return;
        }

        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        for (int p = 0; p < n; p++)
        {
            _pieceY0[p] = _pieceArray[p].Y0;
            _pieceY1[p] = _pieceArray[p].Y1;
            lo = Math.Min(lo, Math.Min(_pieceY0[p], _pieceY1[p]));
            hi = Math.Max(hi, Math.Max(_pieceY0[p], _pieceY1[p]));
        }

        // One bucket per piece is the natural density; the cap keeps a pathological sketch
        // from paying more for the index than for the scan it replaces.
        _bucketCount = Math.Clamp(n, 1, 4096);
        _bucketOrigin = lo;
        double span = hi - lo;
        // An exact-zero guard, not a tolerance: a degenerate y range means one bucket.
        _bucketScale = span > 0 ? _bucketCount / span : 0;

        var counts = new int[_bucketCount + 1];
        for (int p = 0; p < n; p++)
        {
            int from = BucketOf(Math.Min(_pieceY0[p], _pieceY1[p]));
            int to = BucketOf(Math.Max(_pieceY0[p], _pieceY1[p]));
            for (int b = from; b <= to; b++)
                counts[b + 1]++;
        }
        for (int b = 0; b < _bucketCount; b++)
            counts[b + 1] += counts[b];

        _bucketStart = counts;
        _bucketItems = new int[counts[_bucketCount]];
        var cursor = new int[_bucketCount];
        for (int p = 0; p < n; p++)
        {
            int from = BucketOf(Math.Min(_pieceY0[p], _pieceY1[p]));
            int to = BucketOf(Math.Max(_pieceY0[p], _pieceY1[p]));
            for (int b = from; b <= to; b++)
                _bucketItems[_bucketStart[b] + cursor[b]++] = p;
        }
    }

    private int BucketOf(double y)
    {
        int bucket = (int)((y - _bucketOrigin) * _bucketScale);
        return bucket < 0 ? 0 : bucket >= _bucketCount ? _bucketCount - 1 : bucket;
    }

    /// <summary>
    /// Even–odd crossing count of the +x ray from (px, py). The half-open endpoint rule on
    /// y-monotone pieces is what makes it robust at shared vertices; the bucket lookup only
    /// narrows which pieces are asked, never how they answer.
    /// </summary>
    private int Crossings(double px, double py)
    {
        int crossings = 0;
        int bucket = BucketOf(py);
        int end = _bucketStart[bucket + 1];
        for (int t = _bucketStart[bucket]; t < end; t++)
        {
            int p = _bucketItems[t];
            if (_pieceY0[p] > py != _pieceY1[p] > py && _pieceArray[p].XAtY(py) > px)
                crossings++;
        }
        return crossings;
    }
}
