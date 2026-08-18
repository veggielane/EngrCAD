using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// Bézier decomposition of a B-spline / NURBS curve — knot insertion to full interior
/// multiplicity (The NURBS Book, algorithm A5.6), with the end clamping (A5.1) an
/// unclamped or periodic knot vector needs first.
///
/// <para><b>It is a CHANGE OF BASIS, not an approximation.</b> Raising every interior knot
/// to multiplicity p splits the curve at that parameter without moving it: each piece is
/// the same polynomial (or rational) the source carried on that span, written in the
/// Bernstein basis instead of the B-spline one. So a decomposed segment evaluates to the
/// source curve at every parameter of its own span, and nothing is fitted, sampled or
/// tolerated anywhere in this file.</para>
///
/// <para><b>A curve already in Bézier form comes back BIT-IDENTICAL, by construction
/// rather than by a fast path.</b> A5.6's inner loop runs only when an interior knot's
/// multiplicity is below the degree; where it already equals the degree the algorithm
/// performs no arithmetic at all and simply copies control points across. That is what
/// lets a consumer route every spline through here — the narrow already-Bézier case and
/// the general one — instead of keeping two paths that could disagree.</para>
///
/// <para><b>Any degree, and rational too.</b> Insertion runs on HOMOGENEOUS coordinates
/// (w·P, w), so a rational curve decomposes into rational Bézier pieces exactly as a
/// polynomial one decomposes into polynomial pieces; the weights ride along and come back
/// per segment. Whether a CONSUMER can carry the result is the consumer's own question —
/// see <c>DxfDocument</c>, which refuses a rational spline because a sketch has no
/// rational segment type, a different refusal from "I cannot decompose this".</para>
///
/// <para><b>Where it sits.</b> Beside <see cref="BSplineBasis"/>, for the same reason: the
/// arithmetic depends only on the degree, the knots and the (homogeneous) control points,
/// never on the dimension — so the 2D and 3D entry points share one core and only the
/// packing differs. Deliberately NOT in a file reader: knot insertion is NURBS-layer work
/// that several consumers want.</para>
///
/// <para><b>The other exact route, and why both exist.</b>
/// <see cref="NurbsCurve2d.TryToCurvedEdges"/> already produces exact Bézier pieces for a
/// NON-RATIONAL spline of degree ≤ 3, by reading each span's Hermite data — on one span the
/// curve IS a polynomial of degree ≤ 3, and a cubic is determined by its two endpoints and
/// two end derivatives. That is exact for its family and needs no insertion bookkeeping;
/// this one is exact for EVERY degree and for rational curves as well. They share no
/// arithmetic — one evaluates derivatives, the other interpolates control points — which is
/// why the tests cross-check them against each other rather than each against itself.</para>
/// </summary>
/// <remarks>
/// Allocation-friendly rather than allocation-free on purpose: a decomposition is a
/// per-curve conversion (a file read, a export, one arrangement build), not an inner-loop
/// kernel, so the jagged homogeneous buffers cost nothing that matters and keep the index
/// bookkeeping — which is where every published version of A5.1 and A5.6 goes wrong —
/// readable.
/// </remarks>
public static class BSplineDecomposition
{
    /// <summary>
    /// The 2D curve's Bézier segments, in parameter order. Each is a clamped degree-p
    /// NURBS curve over exactly one non-empty knot span, so its own
    /// <see cref="Curve2d.Domain"/> IS that span and
    /// <c>segment.PointAt(u) == curve.PointAt(u)</c> there.
    /// </summary>
    public static IReadOnlyList<NurbsCurve2d> ToBezierSegments(NurbsCurve2d curve)
    {
        ArgumentNullException.ThrowIfNull(curve);
        var homogeneous = new double[curve.ControlPoints.Count][];
        for (int i = 0; i < homogeneous.Length; i++)
        {
            double w = curve.Weights[i];
            homogeneous[i] = [curve.ControlPoints[i].X * w, curve.ControlPoints[i].Y * w, w];
        }

        var pieces = Decompose(curve.Degree, [.. curve.Knots], homogeneous);
        var segments = new NurbsCurve2d[pieces.Count];
        for (int s = 0; s < pieces.Count; s++)
        {
            var (points, start, end) = pieces[s];
            var control = new Vector2d[points.Length];
            var weights = new double[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                double w = points[i][2];
                weights[i] = w;
                control[i] = new Vector2d(points[i][0] / w, points[i][1] / w);
            }
            segments[s] = new NurbsCurve2d(curve.Degree, control, weights, BezierKnots(curve.Degree, start, end));
        }
        return segments;
    }

    /// <summary>
    /// The 3D curve's Bézier segments, in parameter order — see
    /// <see cref="ToBezierSegments(NurbsCurve2d)"/>.
    /// </summary>
    public static IReadOnlyList<NurbsCurve> ToBezierSegments(NurbsCurve curve)
    {
        ArgumentNullException.ThrowIfNull(curve);
        var homogeneous = new double[curve.ControlPoints.Count][];
        for (int i = 0; i < homogeneous.Length; i++)
        {
            double w = curve.Weights[i];
            var p = curve.ControlPoints[i];
            homogeneous[i] = [p.X * w, p.Y * w, p.Z * w, w];
        }

        var pieces = Decompose(curve.Degree, [.. curve.Knots], homogeneous);
        var segments = new NurbsCurve[pieces.Count];
        for (int s = 0; s < pieces.Count; s++)
        {
            var (points, start, end) = pieces[s];
            var control = new Vector3d[points.Length];
            var weights = new double[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                double w = points[i][3];
                weights[i] = w;
                control[i] = new Vector3d(points[i][0] / w, points[i][1] / w, points[i][2] / w);
            }
            segments[s] = new NurbsCurve(curve.Degree, control, weights, BezierKnots(curve.Degree, start, end));
        }
        return segments;
    }

    /// <summary>The clamped knot vector of one Bézier segment over [a, b].</summary>
    private static double[] BezierKnots(int degree, double a, double b)
    {
        var knots = new double[2 * (degree + 1)];
        for (int i = 0; i <= degree; i++)
        {
            knots[i] = a;
            knots[degree + 1 + i] = b;
        }
        return knots;
    }

    /// <summary>
    /// The dimension-agnostic core: clamp both ends, then run A5.6. Control points are
    /// homogeneous, one array per point, the last component being the weight.
    /// </summary>
    private static List<(double[][] Points, double Start, double End)> Decompose(
        int degree, double[] knots, double[][] pw)
    {
        RequireDecomposable(degree, knots, pw.Length);
        (knots, pw) = ClampStart(degree, knots, pw);
        (knots, pw) = ClampEnd(degree, knots, pw);

        int p = degree;
        int n = pw.Length - 1;
        int m = n + p + 1;
        var pieces = new List<(double[][], double, double)>();
        var alphas = new double[Math.Max(p, 1)];

        int a = p, b = p + 1, nb = 0;
        var current = NewSegment(p, pw[0].Length);
        double[][]? next = null;
        for (int i = 0; i <= p; i++)
            Array.Copy(pw[i], current[i], pw[i].Length);

        while (b < m)
        {
            int start = b;
            // Exact knot equality: a knot vector is a list of values a writer either
            // repeated or did not, so multiplicity is a count and never a tolerance.
            while (b < m && knots[b + 1] == knots[b])
                b++;
            int multiplicity = b - start + 1;

            if (multiplicity < p)
            {
                double numerator = knots[b] - knots[a];
                for (int j = p; j > multiplicity; j--)
                    alphas[j - multiplicity - 1] = numerator / (knots[a + j] - knots[a]);

                int repeats = p - multiplicity;
                for (int j = 1; j <= repeats; j++)
                {
                    int save = repeats - j;
                    int s = multiplicity + j;
                    for (int k = p; k >= s; k--)
                    {
                        double alpha = alphas[k - s];
                        Blend(current[k], current[k - 1], alpha);
                    }
                    if (b < m)
                    {
                        next ??= NewSegment(p, pw[0].Length);
                        Array.Copy(current[p], next[save], current[p].Length);
                    }
                }
            }

            pieces.Add((current, knots[a], knots[b]));
            nb++;
            if (b < m)
            {
                next ??= NewSegment(p, pw[0].Length);
                current = next;
                next = null;
                for (int i = p - multiplicity; i <= p; i++)
                    Array.Copy(pw[b - p + i], current[i], pw[0].Length);
                a = b;
                b++;
            }
        }

        if (nb == 0)
            throw new ArgumentException("The curve's knot vector encloses no non-empty span.", nameof(knots));
        return pieces;
    }

    /// <summary>In-place <c>point = alpha·point + (1 − alpha)·previous</c>.</summary>
    private static void Blend(double[] point, double[] previous, double alpha)
    {
        for (int c = 0; c < point.Length; c++)
            point[c] = alpha * point[c] + (1 - alpha) * previous[c];
    }

    private static double[][] NewSegment(int degree, int components)
    {
        var points = new double[degree + 1][];
        for (int i = 0; i <= degree; i++)
            points[i] = new double[components];
        return points;
    }

    /// <summary>
    /// What this file will not decompose, refused BY NAME rather than answered wrongly.
    /// An INTERIOR knot of multiplicity above the degree is the one that matters: the
    /// curve is then genuinely discontinuous there and "the Bézier pieces of one curve"
    /// is not a well-formed request — it is two curves that happen to share a knot vector.
    /// </summary>
    private static void RequireDecomposable(int degree, double[] knots, int controlPointCount)
    {
        if (degree < 1)
            throw new ArgumentOutOfRangeException(nameof(degree), "The degree must be at least 1.");
        if (knots.Length != controlPointCount + degree + 1)
            throw new ArgumentException(
                $"A degree-{degree} curve over {controlPointCount} control points needs "
                + $"{controlPointCount + degree + 1} knots (got {knots.Length}).", nameof(knots));
        for (int i = 1; i < knots.Length; i++)
        {
            if (knots[i] < knots[i - 1])
                throw new ArgumentException("The knot vector must be non-decreasing.", nameof(knots));
        }

        int n = controlPointCount - 1;
        double domainStart = knots[degree], domainEnd = knots[n + 1];
        if (!(domainEnd > domainStart))
            throw new ArgumentException(
                $"The curve's domain [{domainStart}, {domainEnd}] is empty.", nameof(knots));

        for (int i = degree + 1; i <= n; i++)
        {
            if (knots[i] <= domainStart || knots[i] >= domainEnd)
                continue;
            int multiplicity = 1;
            while (i + multiplicity <= n && knots[i + multiplicity] == knots[i])
                multiplicity++;
            if (multiplicity > degree)
            {
                throw new ArgumentException(
                    $"The interior knot {knots[i]} has multiplicity {multiplicity}, above the degree "
                    + $"{degree}: the curve is discontinuous there, so it is two curves sharing a knot "
                    + "vector rather than one curve with Bezier pieces. Split it at that parameter first.",
                    nameof(knots));
            }
            i += multiplicity - 1;
        }
    }

    /// <summary>
    /// Raises the START of the domain to a clamped end — insert u = knots[degree] until its
    /// multiplicity reaches the degree, then drop the control points and knots that only
    /// ever influenced the curve BELOW the domain.
    ///
    /// <para>An already-clamped curve takes no insertion and no trim: the multiplicity is
    /// already degree + 1 and the retained range is the whole curve, so the arrays come
    /// back verbatim. That is what keeps a Bézier-form input bit-identical end to end.</para>
    /// </summary>
    private static (double[] Knots, double[][] Points) ClampStart(int degree, double[] knots, double[][] pw)
    {
        double u = knots[degree];
        int multiplicity = 0;
        for (int i = 0; i < knots.Length && knots[i] <= u; i++)
        {
            if (knots[i] == u)
                multiplicity++;
        }
        if (multiplicity < degree)
            (knots, pw) = InsertKnot(degree, knots, pw, u, degree - multiplicity);

        // The LAST knot equal to u is the span index there; the curve at u is the control
        // point `span - degree`, and everything before it is below the domain.
        int span = 0;
        for (int i = 0; i < knots.Length; i++)
        {
            if (knots[i] == u)
                span = i;
        }
        int first = span - degree;
        if (first == 0 && multiplicity > degree)
            return (knots, pw);   // already clamped: nothing to copy, nothing to move

        var trimmedPoints = new double[pw.Length - first][];
        Array.Copy(pw, first, trimmedPoints, 0, trimmedPoints.Length);
        var trimmedKnots = new double[trimmedPoints.Length + degree + 1];
        for (int i = 0; i <= degree; i++)
            trimmedKnots[i] = u;
        Array.Copy(knots, span + 1, trimmedKnots, degree + 1, trimmedKnots.Length - degree - 1);
        return (trimmedKnots, trimmedPoints);
    }

    /// <summary>The mirror of <see cref="ClampStart"/> at the far end.</summary>
    private static (double[] Knots, double[][] Points) ClampEnd(int degree, double[] knots, double[][] pw)
    {
        double v = knots[pw.Length];
        int multiplicity = 0;
        foreach (double knot in knots)
        {
            if (knot == v)
                multiplicity++;
        }
        if (multiplicity < degree)
            (knots, pw) = InsertKnot(degree, knots, pw, v, degree - multiplicity);

        // The FIRST knot equal to v opens the run; the last control point the domain can
        // reach is the one just before it.
        int run = knots.Length;
        for (int i = knots.Length - 1; i >= 0; i--)
        {
            if (knots[i] == v)
                run = i;
        }
        int count = run;
        if (count == pw.Length && multiplicity > degree)
            return (knots, pw);   // already clamped

        var trimmedPoints = new double[count][];
        Array.Copy(pw, trimmedPoints, count);
        var trimmedKnots = new double[count + degree + 1];
        Array.Copy(knots, trimmedKnots, count);
        for (int i = count; i < trimmedKnots.Length; i++)
            trimmedKnots[i] = v;
        return (trimmedKnots, trimmedPoints);
    }

    /// <summary>
    /// Knot insertion (The NURBS Book, algorithm A5.1) — inserts <paramref name="u"/>
    /// <paramref name="times"/> times without moving the curve.
    /// </summary>
    private static (double[] Knots, double[][] Points) InsertKnot(
        int degree, double[] knots, double[][] pw, double u, int times)
    {
        int p = degree;
        int np = pw.Length - 1;
        int mp = np + p + 1;
        int components = pw[0].Length;

        // The span holding u, and u's current multiplicity there.
        int k = BSplineBasis.FindSpan(u, p, pw.Length, knots);
        // FindSpan clamps at the domain start; when u IS a knot it must name the LAST index
        // carrying it, or the insertion writes into the wrong columns.
        while (k + 1 < knots.Length && knots[k + 1] == u)
            k++;
        int s = 0;
        for (int i = k; i >= 0 && knots[i] == u; i--)
            s++;
        // Never past full multiplicity: a knot repeated more than the degree would split the
        // curve rather than clamp it.
        times = Math.Min(times, p - s);
        if (times <= 0)
            return (knots, pw);

        int r = times;
        int nq = np + r;
        var uq = new double[mp + r + 1];
        var qw = new double[nq + 1][];
        for (int i = 0; i <= nq; i++)
            qw[i] = new double[components];

        for (int i = 0; i <= k; i++)
            uq[i] = knots[i];
        for (int i = 1; i <= r; i++)
            uq[k + i] = u;
        for (int i = k + 1; i <= mp; i++)
            uq[i + r] = knots[i];

        for (int i = 0; i <= k - p; i++)
            Array.Copy(pw[i], qw[i], components);
        for (int i = k - s; i <= np; i++)
            Array.Copy(pw[i], qw[i + r], components);

        var rw = new double[p - s + 1][];
        for (int i = 0; i <= p - s; i++)
        {
            rw[i] = new double[components];
            Array.Copy(pw[k - p + i], rw[i], components);
        }

        int l = k - p;
        for (int j = 1; j <= r; j++)
        {
            l = k - p + j;
            for (int i = 0; i <= p - j - s; i++)
            {
                double alpha = (u - knots[l + i]) / (knots[i + k + 1] - knots[l + i]);
                for (int c = 0; c < components; c++)
                    rw[i][c] = alpha * rw[i + 1][c] + (1 - alpha) * rw[i][c];
            }
            Array.Copy(rw[0], qw[l], components);
            Array.Copy(rw[p - j - s], qw[k + r - j - s], components);
        }
        for (int i = l + 1; i < k - s; i++)
            Array.Copy(rw[i - l], qw[i], components);

        return (uq, qw);
    }
}
