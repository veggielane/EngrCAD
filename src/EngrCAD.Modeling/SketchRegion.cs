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
        : this(sketch, forRevolution, ellipseKernel: true) { }

    /// <summary>
    /// Test seam. <paramref name="ellipseKernel"/> = false sends elliptical arcs down the
    /// <see cref="SegmentKind.General"/> path — <c>EllipseSeg.Distance</c>, i.e.
    /// <c>Curve2d.NearestPoint</c> itself — instead of the transcription in this file.
    /// <para>
    /// It exists because this is the one transcription whose source lives in ANOTHER
    /// project: <see cref="ArcData"/> and <see cref="CubicData"/> mirror <c>ArcSeg</c> and
    /// <c>CubicSeg</c> next door in <c>SketchSegments.cs</c>, but the ellipse's distance is
    /// <c>EngrCAD.BRep</c>'s shared scan-and-Newton. A committed golden hash would catch a
    /// drift there, but only as "the number moved"; holding the two paths bit-equal on the
    /// same binary names it. <c>SketchRegionKernelTests</c> asserts both.
    /// </para>
    /// </summary>
    internal SketchRegion(Sketch sketch, bool forRevolution, bool ellipseKernel)
    {
        _ellipseKernel = ellipseKernel;
        Collect(sketch, forRevolution);
        foreach (var hole in sketch.Holes)
            Collect(hole, forRevolution);
        Bounds = sketch.Bounds;

        BuildDistanceTables();
        BuildParityIndex();
    }

    private readonly bool _ellipseKernel;

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
                case SegmentKind.Arc:
                    ArcMinimum(x, y, distances, s);
                    break;
                case SegmentKind.Cubic:
                    CubicMinimum(x, y, distances, s);
                    break;
                case SegmentKind.Ellipse:
                    EllipseMinimum(x, y, distances, s);
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
        /// <summary>Anything whose own <c>Distance</c> is called through the abstraction —
        /// today only the degenerate arcs the wedge test's preconditions exclude.</summary>
        General = 0,
        Line = 1,
        FullCircle = 2,
        /// <summary>Partial arc: <see cref="ArcData"/>, wedge test plus a certainty band.</summary>
        Arc = 3,
        /// <summary>Cubic bézier: <see cref="CubicData"/>, sampled scan plus masked Newton.</summary>
        Cubic = 4,
        /// <summary>Elliptical arc: <see cref="EllipseData"/>, baked scan plus scalar Newton.</summary>
        Ellipse = 5,
    }

    private SegmentKind[] _kinds = [];
    private double[] _a = [], _b = [], _c = [], _d = [], _e = [];
    private double[] _minX = [], _maxX = [], _minY = [], _maxY = [];
    private SketchSegment[] _general = [];
    /// <summary>Index into <see cref="_arcs"/> / <see cref="_cubics"/> for segments of that
    /// kind (the five flat <c>_a.._e</c> columns stop paying for themselves at thirteen).</summary>
    private int[] _slot = [];
    private ArcData[] _arcs = [];
    private CubicData[] _cubics = [];
    private EllipseData[] _ellipses = [];

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
        _slot = new int[n];
        var arcs = new List<ArcData>();
        var cubics = new List<CubicData>();
        var ellipses = new List<EllipseData>();

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
                case ArcSeg arc when ArcData.TryBuild(arc, out var data):
                    _kinds[s] = SegmentKind.Arc;
                    _slot[s] = arcs.Count;
                    arcs.Add(data);
                    break;
                case CubicSeg cubic:
                    _kinds[s] = SegmentKind.Cubic;
                    _slot[s] = cubics.Count;
                    cubics.Add(new CubicData(cubic));
                    break;
                case EllipseSeg ellipse when _ellipseKernel:
                    _kinds[s] = SegmentKind.Ellipse;
                    _slot[s] = ellipses.Count;
                    ellipses.Add(new EllipseData(ellipse));
                    break;
                default:
                    _kinds[s] = SegmentKind.General;
                    break;
            }
        }

        _arcs = [.. arcs];
        _cubics = [.. cubics];
        _ellipses = [.. ellipses];
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
                case SegmentKind.Arc:
                    if (!Rejected(px, py, s, best))
                        best = Math.Min(best, ArcDistance(px, py, _arcs[_slot[s]]));
                    break;
                case SegmentKind.Cubic:
                    if (!Rejected(px, py, s, best))
                        best = Math.Min(best, CubicDistance(px, py, _cubics[_slot[s]]));
                    break;
                case SegmentKind.Ellipse:
                    if (!Rejected(px, py, s, best))
                        best = Math.Min(best, EllipseDistance(px, py, _ellipses[_slot[s]]));
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

    /// <summary>True when every lane of a comparison mask is set.</summary>
    private static bool AllSet(Vector<long> mask) => Vector.EqualsAll(mask, Vector<long>.AllBitsSet);

    /// <summary>True when no lane of a comparison mask is set.</summary>
    private static bool AllClear(Vector<long> mask) => Vector.EqualsAll(mask, Vector<long>.Zero);

    /// <summary>
    /// Lane-wise <see cref="Rejected"/>. Note the lane-wise kernels do not <em>skip</em>
    /// rejected lanes — they compute them and then substitute +∞ before the min-fold, which
    /// is what "skip" means to a running minimum. That is identity by construction rather
    /// than by the reject's conservativeness argument, so the two paths cannot drift.
    /// </summary>
    private static Vector<long> RejectMask(
        Vector<double> px, Vector<double> py, Vector<double> best,
        Vector<double> minX, Vector<double> maxX, Vector<double> minY, Vector<double> maxY)
    {
        var zero = Vector<double>.Zero;
        var gapX = Vector.Max(Vector.Max(minX - px, px - maxX), zero);
        var gapY = Vector.Max(Vector.Max(minY - py, py - maxY), zero);
        // A +∞ running best makes the right-hand side +∞, which no finite gap exceeds —
        // exactly the scalar guard's early "not rejected".
        return Vector.GreaterThan(
            gapX * gapX + gapY * gapY, best * best * new Vector<double>(1 + RejectSlack));
    }

    // ------------------------------------------------------------------------ partial arcs

    /// <summary>
    /// Angular half-width of the band around either sweep boundary ray inside which the
    /// lane-wise wedge test refuses to answer and hands the block to the scalar
    /// <c>Atan2</c> path.
    /// <para>
    /// <b>Why a band and not a transcription.</b> The scalar decision is
    /// <c>delta &lt;= span</c> over <c>delta = wrap(Atan2(oy, ox) − from)</c>; the lane-wise
    /// one reads the <em>signs</em> of the two cross products c₀ = f × o and c₁ = g × o
    /// against the wedge's boundary rays f and g. Both decide the same predicate — is this
    /// direction inside the wedge — but by different arithmetic, so they can be made to
    /// agree only where neither is near flipping. Since c₀ = |o|·sin(delta) and
    /// c₁ = |o|·sin(delta − span), requiring |c₀| &gt; tol·|o| and |c₁| &gt; tol·|o| bounds
    /// delta away from 0 and from span by ≈ tol radians (and, over-conservatively, from π
    /// and span + π, where the combined test is insensitive anyway).
    /// </para>
    /// <para>
    /// <b>Why 1e-9 is comfortable.</b> Scalar-side error in delta: <c>Atan2</c>'s own (a few
    /// ulps of a result bounded by π, ≤ 1e-15), the subtraction <c>angle − from</c>
    /// (≤ 1e-14 with |from| capped at <see cref="ArcData.AngleLimit"/>) and the reduction by
    /// the <em>double</em> 2·PI rather than by real 2π (2.4e-16 per turn, ≤ 1e-14 over the
    /// same cap; the <c>%</c> itself is exact). Lane-wise side: the cross products carry
    /// ~4 ulps of |o|, so their sines are right to ~4e-16. Both are five orders inside the
    /// band, so <b>outside it the two tests provably agree, and inside it the block is
    /// recomputed scalar — the batch result is bit-identical for every input</b>. This is a
    /// certainty guard in the same family as <see cref="RejectSlack"/>, not a tolerance on
    /// the answer.
    /// </para>
    /// <para>
    /// Radians are dimensionless, so an absolute constant is legitimate here (the same
    /// reasoning the sketch builder's angular guards carry). And the inputs that most want
    /// exactness land in the band by construction: a segment endpoint, shared bit-for-bit
    /// with its neighbour, sits exactly on a boundary ray.
    /// </para>
    /// </summary>
    private const double WedgeCertainty = 1e-9;

    /// <summary>
    /// A partial arc flattened for both the scalar transcription of <c>ArcSeg.Distance</c>
    /// and its lane-wise form. <see cref="From"/>/<see cref="Span"/> reproduce
    /// <c>ArcSeg.AngleInSweep</c> exactly; <see cref="Fx"/>…<see cref="Gy"/> are the two
    /// boundary rays the wedge test reads instead. Precomputing the endpoints matters on its
    /// own account: <c>ArcSeg.Start</c>/<c>End</c> are properties that each evaluate a
    /// <c>Cos</c> and a <c>Sin</c>, so the out-of-sweep branch used to cost four
    /// transcendentals per query.
    /// </summary>
    private sealed class ArcData
    {
        /// <summary>
        /// Cap on |from| and |from + span|, which is what bounds the scalar path's
        /// subtraction and modular-reduction error inside <see cref="WedgeCertainty"/>.
        /// Sketch arc angles are <c>Atan2</c> results (|a| ≤ π) plus at most one sweep, so
        /// nothing the builders produce comes near 64; an arc that did would simply stay on
        /// the <see cref="SegmentKind.General"/> path.
        /// </summary>
        public const double AngleLimit = 64;

        public readonly double Cx, Cy, Radius;
        public readonly double Sx, Sy, Ex, Ey;
        public readonly double From, Span;
        public readonly double Fx, Fy, Gx, Gy;

        /// <summary>Sweep wider than a half turn, which inverts the wedge test — see
        /// <see cref="InSweep"/>.</summary>
        public readonly bool WideSweep;

        private ArcData(ArcSeg arc, double from, double span)
        {
            Cx = arc.Center.X;
            Cy = arc.Center.Y;
            Radius = arc.Radius;
            Sx = arc.Start.X;
            Sy = arc.Start.Y;
            Ex = arc.End.X;
            Ey = arc.End.Y;
            From = from;
            Span = span;
            Fx = Math.Cos(from);
            Fy = Math.Sin(from);
            Gx = Math.Cos(from + span);
            Gy = Math.Sin(from + span);
            WideSweep = span > Math.PI;
        }

        public static bool TryBuild(ArcSeg arc, out ArcData data)
        {
            data = null!;
            double span = Math.Abs(arc.Sweep);
            // A full circle is its own kind; a sweep of zero or one that covers more than a
            // turn breaks the wedge derivation's delta ∈ [0, 2π) premise. Both are degenerate
            // and both keep the scalar path.
            if (!(span > 0) || !(span < 2 * Math.PI))
                return false;
            double from = arc.Sweep > 0 ? arc.StartAngle : arc.StartAngle + arc.Sweep;
            if (!(Math.Abs(from) <= AngleLimit) || !(Math.Abs(from + span) <= AngleLimit))
                return false;
            data = new ArcData(arc, from, span);
            return true;
        }

        /// <summary>
        /// The wedge test, from the signs of c₀ = |o|·sin(delta) and c₁ = |o|·sin(delta − span).
        /// For span ≤ π the sweep is the intersection of the two half-planes: sin(delta) ≥ 0
        /// puts delta in [0, π] and sin(delta − span) ≤ 0 puts it in [span − π, span], and the
        /// two meet in exactly [0, span]. For span &gt; π the <em>complement</em> (span, 2π) is
        /// the narrow wedge, so the same two half-planes describe it and the test flips to a
        /// union. At span = π the forms coincide, so the branch has no boundary of its own.
        /// </summary>
        public bool InSweep(double c0, double c1) => WideSweep ? c0 >= 0 || c1 <= 0 : c0 >= 0 && c1 <= 0;
    }

    /// <summary>Term for term <c>ArcSeg.Distance</c> for a partial arc — <c>Atan2</c>
    /// included, since this is the answer the lane-wise kernel must reproduce.</summary>
    private static double ArcDistance(double px, double py, ArcData arc)
    {
        double ox = px - arc.Cx;
        double oy = py - arc.Cy;
        double angle = Math.Atan2(oy, ox);
        // ArcSeg.AngleInSweep, minus the IsFullCircle short-circuit that classification
        // already decided.
        double delta = (angle - arc.From) % (2 * Math.PI);
        if (delta < 0)
            delta += 2 * Math.PI;
        if (delta <= arc.Span)
            return Math.Abs(Math.Sqrt(ox * ox + oy * oy) - arc.Radius);
        double sx = px - arc.Sx, sy = py - arc.Sy;
        double ex = px - arc.Ex, ey = py - arc.Ey;
        return Math.Min(Math.Sqrt(sx * sx + sy * sy), Math.Sqrt(ex * ex + ey * ey));
    }

    private void ArcMinimum(ReadOnlySpan<double> x, ReadOnlySpan<double> y, Span<double> best, int s)
    {
        var arc = _arcs[_slot[s]];
        int n = x.Length;
        int i = 0;
        if (Accelerated)
        {
            int w = Vector<double>.Count;
            var zero = Vector<double>.Zero;
            var infinity = new Vector<double>(double.PositiveInfinity);
            var vminX = new Vector<double>(_minX[s]);
            var vmaxX = new Vector<double>(_maxX[s]);
            var vminY = new Vector<double>(_minY[s]);
            var vmaxY = new Vector<double>(_maxY[s]);
            var vcx = new Vector<double>(arc.Cx);
            var vcy = new Vector<double>(arc.Cy);
            var vr = new Vector<double>(arc.Radius);
            var vsx = new Vector<double>(arc.Sx);
            var vsy = new Vector<double>(arc.Sy);
            var vex = new Vector<double>(arc.Ex);
            var vey = new Vector<double>(arc.Ey);
            var vfx = new Vector<double>(arc.Fx);
            var vfy = new Vector<double>(arc.Fy);
            var vgx = new Vector<double>(arc.Gx);
            var vgy = new Vector<double>(arc.Gy);
            var vtol = new Vector<double>(WedgeCertainty);
            ref double xr = ref MemoryMarshal.GetReference(x);
            ref double yr = ref MemoryMarshal.GetReference(y);
            ref double br = ref MemoryMarshal.GetReference(best);
            for (; i <= n - w; i += w)
            {
                var px = Vector.LoadUnsafe(ref xr, (nuint)i);
                var py = Vector.LoadUnsafe(ref yr, (nuint)i);
                var b = Vector.LoadUnsafe(ref br, (nuint)i);
                var rejected = RejectMask(px, py, b, vminX, vmaxX, vminY, vmaxY);
                if (AllSet(rejected))
                    continue;

                var ox = px - vcx;
                var oy = py - vcy;
                var rho = Vector.SquareRoot(ox * ox + oy * oy);
                var c0 = vfx * oy - vfy * ox;
                var c1 = vgx * oy - vgy * ox;

                // Any lane inside the certainty band takes the whole block back to the exact
                // scalar path (see WedgeCertainty). In practice that is a measure-zero set of
                // sample points, and the branch is one compare per block.
                var margin = vtol * rho;
                var uncertain = Vector.BitwiseOr(
                    Vector.LessThanOrEqual(Vector.Abs(c0), margin),
                    Vector.LessThanOrEqual(Vector.Abs(c1), margin));
                if (!AllClear(uncertain))
                {
                    for (int k = i; k < i + w; k++)
                    {
                        if (!Rejected(x[k], y[k], s, best[k]))
                            best[k] = Math.Min(best[k], ArcDistance(x[k], y[k], arc));
                    }
                    continue;
                }

                var inside = Vector.Abs(rho - vr);
                var dsx = px - vsx;
                var dsy = py - vsy;
                var dex = px - vex;
                var dey = py - vey;
                var outside = Vector.Min(
                    Vector.SquareRoot(dsx * dsx + dsy * dsy),
                    Vector.SquareRoot(dex * dex + dey * dey));
                var low = Vector.GreaterThanOrEqual(c0, zero);
                var high = Vector.LessThanOrEqual(c1, zero);
                var inSweep = arc.WideSweep
                    ? Vector.BitwiseOr(low, high)
                    : Vector.BitwiseAnd(low, high);
                var distance = Vector.ConditionalSelect(
                    rejected, infinity, Vector.ConditionalSelect(inSweep, inside, outside));
                Vector.Min(b, distance).StoreUnsafe(ref br, (nuint)i);
            }
        }
        for (; i < n; i++)
        {
            if (!Rejected(x[i], y[i], s, best[i]))
                best[i] = Math.Min(best[i], ArcDistance(x[i], y[i], arc));
        }
    }

    // -------------------------------------------------------------------- cubic béziers

    /// <summary>
    /// A cubic bézier flattened for <c>CubicSeg.Distance</c>'s two stages. The 17 scan
    /// points are the same for every query — the scalar code re-evaluated the curve at
    /// i/16 on every call — so they are baked once; the Newton stage then needs only the
    /// Bernstein difference constants.
    /// </summary>
    private sealed class CubicData
    {
        public const int Samples = 16;
        public const int NewtonSteps = 8;

        /// <summary>The scan parameters, exactly <c>(double)i / samples</c> as the scalar
        /// loop forms them.</summary>
        public static readonly double[] SampleT = BuildSampleT();

        public readonly double[] SampleX = new double[Samples + 1];
        public readonly double[] SampleY = new double[Samples + 1];
        public readonly double P0x, P0y, C1x, C1y, C2x, C2y, P3x, P3y;
        public readonly double D0x, D0y, D1x, D1y, D2x, D2y;
        public readonly double Ax, Ay, Bx, By;

        public CubicData(CubicSeg cubic)
        {
            var p0 = cubic.P0;
            var c1 = cubic.Control1;
            var c2 = cubic.Control2;
            var p3 = cubic.P3;
            (P0x, P0y) = (p0.X, p0.Y);
            (C1x, C1y) = (c1.X, c1.Y);
            (C2x, C2y) = (c2.X, c2.Y);
            (P3x, P3y) = (p3.X, p3.Y);
            // CubicSeg.DerivativeAt's three differences and SecondDerivativeAt's two — loop
            // invariants of the Newton stage, formed by the same expressions.
            (D0x, D0y) = (c1.X - p0.X, c1.Y - p0.Y);
            (D1x, D1y) = (c2.X - c1.X, c2.Y - c1.Y);
            (D2x, D2y) = (p3.X - c2.X, p3.Y - c2.Y);
            (Ax, Ay) = (c2.X - c1.X * 2 + p0.X, c2.Y - c1.Y * 2 + p0.Y);
            (Bx, By) = (p3.X - c2.X * 2 + c1.X, p3.Y - c2.Y * 2 + c1.Y);

            for (int i = 0; i <= Samples; i++)
            {
                double t = SampleT[i];
                double u = 1 - t;
                SampleX[i] = P0x * (u * u * u) + C1x * (3 * u * u * t)
                    + C2x * (3 * u * t * t) + P3x * (t * t * t);
                SampleY[i] = P0y * (u * u * u) + C1y * (3 * u * u * t)
                    + C2y * (3 * u * t * t) + P3y * (t * t * t);
            }
        }

        private static double[] BuildSampleT()
        {
            var t = new double[Samples + 1];
            for (int i = 0; i <= Samples; i++)
                t[i] = (double)i / Samples;
            return t;
        }
    }

    /// <summary>
    /// Term for term <c>CubicSeg.Distance</c>: coarse scan for the basin, then Newton on
    /// (B − p)·B′. Every expression keeps the segment class's association order, which is
    /// what the golden bit-hashes check.
    /// </summary>
    private static double CubicDistance(double px, double py, CubicData c)
    {
        double bestT = 0, bestDistance = double.PositiveInfinity;
        for (int i = 0; i <= CubicData.Samples; i++)
        {
            double sx = px - c.SampleX[i], sy = py - c.SampleY[i];
            double d = Math.Sqrt(sx * sx + sy * sy);
            if (d < bestDistance)
            {
                bestDistance = d;
                bestT = CubicData.SampleT[i];
            }
        }

        double refined = bestT;
        for (int iteration = 0; iteration < CubicData.NewtonSteps; iteration++)
        {
            double t = refined, u = 1 - t;
            double fx = c.P0x * (u * u * u) + c.C1x * (3 * u * u * t)
                + c.C2x * (3 * u * t * t) + c.P3x * (t * t * t);
            double fy = c.P0y * (u * u * u) + c.C1y * (3 * u * u * t)
                + c.C2y * (3 * u * t * t) + c.P3y * (t * t * t);
            double offsetX = fx - px, offsetY = fy - py;
            double d1x = c.D0x * (3 * u * u) + c.D1x * (6 * u * t) + c.D2x * (3 * t * t);
            double d1y = c.D0y * (3 * u * u) + c.D1y * (6 * u * t) + c.D2y * (3 * t * t);
            double d2x = c.Ax * (6 * u) + c.Bx * (6 * t);
            double d2y = c.Ay * (6 * u) + c.By * (6 * t);
            double g = offsetX * d1x + offsetY * d1y;
            double gPrime = (d1x * d1x + d1y * d1y) + (offsetX * d2x + offsetY * d2y);
            if (Math.Abs(gPrime) < 1e-18)
                break;
            double next = Math.Clamp(refined - g / gPrime, 0, 1);
            // An EXACT fixed point, so this early exit changes nothing: g and g′ are
            // functions of `refined` alone, so an iteration reproducing it bit for bit makes
            // every later one recompute the same value and take the same branch. Deliberately
            // `==` and NOT a tolerance — a tolerant stop would move results and would need
            // the golden hashes re-derived, which is the trade this deletes rather than
            // makes. Measured: 50.0% of Newton iterations on an all-bézier outline and 35.1%
            // on an engraving-shaped one are redundant this way (exact counts). End to end
            // that is 1.09× on the former and 1.01–1.03× elsewhere — small, because Newton is
            // under half of a kernel that is itself behind the bounding-box reject.
            // The VECTOR kernel deliberately does NOT take this exit; see CubicMinimum.
            if (next == refined)
                break;
            refined = next;
        }

        double ut = 1 - refined, tt = refined;
        double ex = c.P0x * (ut * ut * ut) + c.C1x * (3 * ut * ut * tt)
            + c.C2x * (3 * ut * tt * tt) + c.P3x * (tt * tt * tt);
        double ey = c.P0y * (ut * ut * ut) + c.C1y * (3 * ut * ut * tt)
            + c.C2y * (3 * ut * tt * tt) + c.P3y * (tt * tt * tt);
        double rx = px - ex, ry = py - ey;
        return Math.Min(bestDistance, Math.Sqrt(rx * rx + ry * ry));
    }

    /// <summary>
    /// Lane-wise <see cref="CubicDistance"/>. Newton on a polynomial is pure algebra, so the
    /// arithmetic vectorizes bit-exactly; the one piece of divergent control flow is the
    /// scalar loop's <c>break</c> on a vanishing derivative, and that is reproduced by
    /// <b>masking the write to <c>refined</c></b> with a sticky per-lane flag. A stopped lane
    /// therefore keeps the value it stopped at, verbatim — the correctness does not rest on a
    /// converged Newton step being a fixed point (it is not: a vanishing g′ makes the step
    /// infinite, which the clamp would turn into 0 or 1).
    /// <para>
    /// <b>It deliberately does NOT take <see cref="CubicDistance"/>'s exact fixed-point
    /// exit, and the asymmetry was measured rather than assumed.</b> The scalar loop exits
    /// as soon as ITS parameter stops moving; a block can only exit when the SLOWEST of its
    /// lanes does, and around 30% of solves never reach an exact fixed point within the
    /// eight steps, so the maximum over four lanes is ~7.5 of 8 — about a 6% saving, to be
    /// bought with three extra vector operations and a branch on every iteration. Measured
    /// end to end at <b>0.99–1.03×</b>: nothing, and a slight loss in one case. Same shape
    /// as the arc kernel's certainty-band finding — block granularity is what destroys a
    /// per-lane saving, so an early exit that pays in scalar code usually does not
    /// vectorize.
    /// </para>
    /// </summary>
    private void CubicMinimum(ReadOnlySpan<double> x, ReadOnlySpan<double> y, Span<double> best, int s)
    {
        var cubic = _cubics[_slot[s]];
        int n = x.Length;
        int i = 0;
        if (Accelerated)
        {
            int w = Vector<double>.Count;
            var zero = Vector<double>.Zero;
            var one = Vector<double>.One;
            var three = new Vector<double>(3.0);
            var six = new Vector<double>(6.0);
            var epsilon = new Vector<double>(1e-18);
            var infinity = new Vector<double>(double.PositiveInfinity);
            var vminX = new Vector<double>(_minX[s]);
            var vmaxX = new Vector<double>(_maxX[s]);
            var vminY = new Vector<double>(_minY[s]);
            var vmaxY = new Vector<double>(_maxY[s]);
            var vp0x = new Vector<double>(cubic.P0x);
            var vp0y = new Vector<double>(cubic.P0y);
            var vc1x = new Vector<double>(cubic.C1x);
            var vc1y = new Vector<double>(cubic.C1y);
            var vc2x = new Vector<double>(cubic.C2x);
            var vc2y = new Vector<double>(cubic.C2y);
            var vp3x = new Vector<double>(cubic.P3x);
            var vp3y = new Vector<double>(cubic.P3y);
            var vd0x = new Vector<double>(cubic.D0x);
            var vd0y = new Vector<double>(cubic.D0y);
            var vd1x = new Vector<double>(cubic.D1x);
            var vd1y = new Vector<double>(cubic.D1y);
            var vd2x = new Vector<double>(cubic.D2x);
            var vd2y = new Vector<double>(cubic.D2y);
            var vax = new Vector<double>(cubic.Ax);
            var vay = new Vector<double>(cubic.Ay);
            var vbx = new Vector<double>(cubic.Bx);
            var vby = new Vector<double>(cubic.By);
            ref double xr = ref MemoryMarshal.GetReference(x);
            ref double yr = ref MemoryMarshal.GetReference(y);
            ref double br = ref MemoryMarshal.GetReference(best);
            for (; i <= n - w; i += w)
            {
                var px = Vector.LoadUnsafe(ref xr, (nuint)i);
                var py = Vector.LoadUnsafe(ref yr, (nuint)i);
                var b = Vector.LoadUnsafe(ref br, (nuint)i);
                var rejected = RejectMask(px, py, b, vminX, vmaxX, vminY, vmaxY);
                if (AllSet(rejected))
                    continue;

                var bestDistance = infinity;
                var bestT = zero;
                for (int k = 0; k <= CubicData.Samples; k++)
                {
                    var sx = px - new Vector<double>(cubic.SampleX[k]);
                    var sy = py - new Vector<double>(cubic.SampleY[k]);
                    var d = Vector.SquareRoot(sx * sx + sy * sy);
                    var closer = Vector.LessThan(d, bestDistance);
                    bestDistance = Vector.ConditionalSelect(closer, d, bestDistance);
                    bestT = Vector.ConditionalSelect(
                        closer, new Vector<double>(CubicData.SampleT[k]), bestT);
                }

                var refined = bestT;
                var active = Vector<long>.AllBitsSet;
                for (int iteration = 0; iteration < CubicData.NewtonSteps; iteration++)
                {
                    var t = refined;
                    var u = one - t;
                    var fx = vp0x * (u * u * u) + vc1x * (three * u * u * t)
                        + vc2x * (three * u * t * t) + vp3x * (t * t * t);
                    var fy = vp0y * (u * u * u) + vc1y * (three * u * u * t)
                        + vc2y * (three * u * t * t) + vp3y * (t * t * t);
                    var offsetX = fx - px;
                    var offsetY = fy - py;
                    var d1x = vd0x * (three * u * u) + vd1x * (six * u * t) + vd2x * (three * t * t);
                    var d1y = vd0y * (three * u * u) + vd1y * (six * u * t) + vd2y * (three * t * t);
                    var d2x = vax * (six * u) + vbx * (six * t);
                    var d2y = vay * (six * u) + vby * (six * t);
                    var g = offsetX * d1x + offsetY * d1y;
                    var gPrime = (d1x * d1x + d1y * d1y) + (offsetX * d2x + offsetY * d2y);
                    // Sticky: once a lane's derivative vanishes it has "broken" and never
                    // writes refined again, which is what the scalar break does.
                    active = Vector.BitwiseAnd(
                        active, Vector.GreaterThanOrEqual(Vector.Abs(gPrime), epsilon));
                    if (AllClear(active))
                        break;
                    var step = Vector.Min(Vector.Max(refined - g / gPrime, zero), one);
                    refined = Vector.ConditionalSelect(active, step, refined);
                }

                var ut = one - refined;
                var tt = refined;
                var ex = vp0x * (ut * ut * ut) + vc1x * (three * ut * ut * tt)
                    + vc2x * (three * ut * tt * tt) + vp3x * (tt * tt * tt);
                var ey = vp0y * (ut * ut * ut) + vc1y * (three * ut * ut * tt)
                    + vc2y * (three * ut * tt * tt) + vp3y * (tt * tt * tt);
                var rx = px - ex;
                var ry = py - ey;
                var distance = Vector.Min(bestDistance, Vector.SquareRoot(rx * rx + ry * ry));
                Vector.Min(b, Vector.ConditionalSelect(rejected, infinity, distance))
                    .StoreUnsafe(ref br, (nuint)i);
            }
        }
        for (; i < n; i++)
        {
            if (!Rejected(x[i], y[i], s, best[i]))
                best[i] = Math.Min(best[i], CubicDistance(x[i], y[i], cubic));
        }
    }

    // ------------------------------------------------------------------ elliptical arcs

    /// <summary>
    /// An elliptical arc flattened for <c>EllipseSeg.Distance</c>, which is
    /// <c>Ellipse2d</c>'s inherited <c>Curve2d.NearestPoint</c>: a 65-point scan for the
    /// basin, then a bracketed Newton on (C − p)·C′.
    /// <para>
    /// <b>The scan points are the same for every query</b>, exactly as
    /// <see cref="CubicData"/>'s are, and that is the whole win here — the shared curve code
    /// re-evaluates <c>PointAt</c> at i/64 on every call, and each of those is a
    /// <c>Math.Cos</c> plus a <c>Math.Sin</c>. Baking them removes 65 of each per query and
    /// leaves a scan that is pure arithmetic, hence vectorizable to the bit. Baking is exact
    /// rather than approximate for the reason memoization always is: <c>Math.Cos</c> is a
    /// function of its argument, so the value computed once at construction is the same
    /// double the value computed per query would be.
    /// </para>
    /// </summary>
    private sealed class EllipseData
    {
        public const int Samples = 64;
        public const int NewtonSteps = 20;

        /// <summary>Domain start and length, from <c>Ellipse2d.Domain</c> = <c>Interval.Unit</c>.
        /// Named rather than inlined so the transcription reads against the source.</summary>
        public const double DomainStart = 0;
        public const double DomainEnd = 1;
        public const double DomainLength = DomainEnd - DomainStart;

        /// <summary>The scan parameters, exactly <c>Domain.ParameterAt((double)i / samples)</c>
        /// as <c>Curve2d.NearestPoint</c> forms them.</summary>
        public static readonly double[] SampleT = BuildSampleT();

        public readonly double[] SampleX = new double[Samples + 1];
        public readonly double[] SampleY = new double[Samples + 1];
        public readonly double Cx, Cy, Ax, Ay, Bx, By;
        public readonly double StartAngle, Sweep;

        public EllipseData(EllipseSeg ellipse)
        {
            (Cx, Cy) = (ellipse.Center.X, ellipse.Center.Y);
            (Ax, Ay) = (ellipse.SemiAxisX.X, ellipse.SemiAxisX.Y);
            (Bx, By) = (ellipse.SemiAxisY.X, ellipse.SemiAxisY.Y);
            StartAngle = ellipse.StartAngle;
            Sweep = ellipse.Sweep;

            for (int i = 0; i <= Samples; i++)
            {
                double angle = StartAngle + Sweep * SampleT[i];
                double cos = Math.Cos(angle), sin = Math.Sin(angle);
                // Ellipse2d.PointAt's association order: (C + A·cos) + B·sin.
                SampleX[i] = (Cx + Ax * cos) + Bx * sin;
                SampleY[i] = (Cy + Ay * cos) + By * sin;
            }
        }

        private static double[] BuildSampleT()
        {
            var t = new double[Samples + 1];
            for (int i = 0; i <= Samples; i++)
                t[i] = DomainStart + (DomainEnd - DomainStart) * ((double)i / Samples);
            return t;
        }
    }

    /// <summary>
    /// Term for term <c>Curve2d.NearestPoint</c> specialized to <c>Ellipse2d</c>, then its
    /// caller <c>Curve2d.DistanceTo</c>'s final <c>.Length</c>. The scan runs over the baked
    /// samples; the refinement is <see cref="EllipseRefine"/>, shared with the lane-wise form.
    /// </summary>
    private static double EllipseDistance(double px, double py, EllipseData e)
    {
        double best = double.PositiveInfinity, bestT = EllipseData.DomainStart;
        for (int i = 0; i <= EllipseData.Samples; i++)
        {
            double dx = e.SampleX[i] - px, dy = e.SampleY[i] - py;
            double d = dx * dx + dy * dy;
            if (d < best)
            {
                best = d;
                bestT = EllipseData.SampleT[i];
            }
        }
        return EllipseRefine(px, py, best, bestT, e);
    }

    /// <summary>
    /// The Newton half of <c>Curve2d.NearestPoint</c>, plus <c>DistanceTo</c>'s square root.
    /// <para>
    /// <b>This stays scalar, and the reason is measured rather than assumed.</b> Every
    /// iteration needs <c>Math.Cos</c> and <c>Math.Sin</c> at a parameter that differs per
    /// lane, and .NET 10's <c>Vector.Cos</c>/<c>Vector.Sin</c> are <em>not</em> bit-identical
    /// to the scalar ones — measured on this machine, 11 858 of 200 000 doubles differ for
    /// <c>Cos</c> and 19 172 for <c>Sin</c>, each by one ulp. One ulp is far more than this
    /// field can spend: its sign drives boolean classification kernel-wide, so the same rule
    /// that keeps the gyroid and the exponential falloff scalar applies here.
    /// </para>
    /// <para>
    /// What the transcription <em>can</em> do for free is evaluate the angle's cosine and
    /// sine ONCE per iteration. The shared curve code calls <c>PointAt</c>,
    /// <c>DerivativeAt</c> and <c>SecondDerivativeAt</c> separately and each recomputes both
    /// at the same angle, so hoisting them is three-for-one and bit-exact by the same
    /// argument that lets the scan be baked.
    /// </para>
    /// </summary>
    private static double EllipseRefine(double px, double py, double best, double bestT, EllipseData e)
    {
        const double window = EllipseData.DomainLength / EllipseData.Samples;
        double lo = Math.Clamp(bestT - window, EllipseData.DomainStart, EllipseData.DomainEnd);
        double hi = Math.Clamp(bestT + window, EllipseData.DomainStart, EllipseData.DomainEnd);
        double parameter = bestT;
        for (int iteration = 0; iteration < EllipseData.NewtonSteps; iteration++)
        {
            double angle = e.StartAngle + e.Sweep * parameter;
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            double deltaX = (e.Cx + e.Ax * cos) + e.Bx * sin - px;
            double deltaY = (e.Cy + e.Ay * cos) + e.By * sin - py;
            double d1x = (e.Ax * -sin + e.Bx * cos) * e.Sweep;
            double d1y = (e.Ay * -sin + e.By * cos) * e.Sweep;
            double d2x = (e.Ax * -cos + e.Bx * -sin) * (e.Sweep * e.Sweep);
            double d2y = (e.Ay * -cos + e.By * -sin) * (e.Sweep * e.Sweep);
            double f = deltaX * d1x + deltaY * d1y;
            double df = (d1x * d1x + d1y * d1y) + (deltaX * d2x + deltaY * d2y);
            // Near-underflow degenerate guard, transcribed from Curve2d.NearestPoint.
            if (Math.Abs(df) < 1e-30)
                break;
            double next = Math.Clamp(parameter - f / df, lo, hi);
            if (Math.Abs(next - parameter) <= EllipseData.DomainLength * 1e-15)
            {
                parameter = next;
                break;
            }
            parameter = next;
        }

        double finalAngle = e.StartAngle + e.Sweep * parameter;
        double fcos = Math.Cos(finalAngle), fsin = Math.Sin(finalAngle);
        double rx = (e.Cx + e.Ax * fcos) + e.Bx * fsin - px;
        double ry = (e.Cy + e.Ay * fcos) + e.By * fsin - py;
        double refined = rx * rx + ry * ry;
        // NearestPoint keeps the refined point only when it beat the scan, then DistanceTo
        // takes the length. The losing branch's PointAt(bestT) is the baked sample bestT came
        // from, so its squared distance IS `best` — the same expression on the same doubles.
        return Math.Sqrt(refined <= best ? refined : best);
    }

    /// <summary>
    /// Lane-wise <see cref="EllipseDistance"/>: the 65-point scan vectorizes exactly (pure
    /// arithmetic, same order, and <c>d &lt; best</c> keeps first-wins per lane just as the
    /// scalar loop does), and each lane's <see cref="EllipseRefine"/> then runs scalar
    /// because there is no bit-exact vector cosine. So this is a partial kernel by
    /// construction — and it is the scan that was worth vectorizing, being the part that is
    /// 65 evaluations against the refinement's handful.
    /// </summary>
    private void EllipseMinimum(ReadOnlySpan<double> x, ReadOnlySpan<double> y, Span<double> best, int s)
    {
        var e = _ellipses[_slot[s]];
        int n = x.Length;
        int i = 0;
        if (Accelerated)
        {
            int w = Vector<double>.Count;
            var infinity = new Vector<double>(double.PositiveInfinity);
            var vminX = new Vector<double>(_minX[s]);
            var vmaxX = new Vector<double>(_maxX[s]);
            var vminY = new Vector<double>(_minY[s]);
            var vmaxY = new Vector<double>(_maxY[s]);
            ref double xr = ref MemoryMarshal.GetReference(x);
            ref double yr = ref MemoryMarshal.GetReference(y);
            ref double br = ref MemoryMarshal.GetReference(best);
            Span<double> scanned = stackalloc double[w];
            Span<double> scannedT = stackalloc double[w];
            for (; i <= n - w; i += w)
            {
                var px = Vector.LoadUnsafe(ref xr, (nuint)i);
                var py = Vector.LoadUnsafe(ref yr, (nuint)i);
                var b = Vector.LoadUnsafe(ref br, (nuint)i);
                var rejected = RejectMask(px, py, b, vminX, vmaxX, vminY, vmaxY);
                if (AllSet(rejected))
                    continue;

                var bestSquared = infinity;
                var bestT = new Vector<double>(EllipseData.DomainStart);
                for (int k = 0; k <= EllipseData.Samples; k++)
                {
                    var dx = new Vector<double>(e.SampleX[k]) - px;
                    var dy = new Vector<double>(e.SampleY[k]) - py;
                    var d = dx * dx + dy * dy;
                    var closer = Vector.LessThan(d, bestSquared);
                    bestSquared = Vector.ConditionalSelect(closer, d, bestSquared);
                    bestT = Vector.ConditionalSelect(
                        closer, new Vector<double>(EllipseData.SampleT[k]), bestT);
                }
                bestSquared.CopyTo(scanned);
                bestT.CopyTo(scannedT);

                for (int k = 0; k < w; k++)
                {
                    // The rejected lanes are skipped rather than blended with +∞ here: unlike
                    // the fully lane-wise kernels this refinement is a scalar call, so "skip"
                    // is available literally and matches the scalar path's own guard.
                    if (Rejected(x[i + k], y[i + k], s, best[i + k]))
                        continue;
                    best[i + k] = Math.Min(
                        best[i + k], EllipseRefine(x[i + k], y[i + k], scanned[k], scannedT[k], e));
                }
            }
        }
        for (; i < n; i++)
        {
            if (!Rejected(x[i], y[i], s, best[i]))
                best[i] = Math.Min(best[i], EllipseDistance(x[i], y[i], e));
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
