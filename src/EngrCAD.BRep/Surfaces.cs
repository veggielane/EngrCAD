using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>Parametric surface, evaluated over <see cref="DomainU"/> × <see cref="DomainV"/>.</summary>
public abstract class Surface
{
    public abstract Interval DomainU { get; }
    public abstract Interval DomainV { get; }
    public abstract Vector3d PointAt(double u, double v);

    /// <summary>
    /// The UNNORMALISED normal <c>∂P/∂u × ∂P/∂v</c>, by central differences unless a
    /// subclass supplies the closed form.
    ///
    /// <para>It exists beside <see cref="NormalAt"/> for two reasons that are the same
    /// reason. A silhouette is the zero set of <c>N·d</c>, where the SIGN is the whole
    /// content and the division a unit normal costs can only lose precision — the
    /// recorded rule that a degeneracy guard reads the sign of an unnormalised cross
    /// product. And a normal is not defined at a POLE, where <see cref="NormalAt"/>
    /// throws rather than returning something a caller can test; here the answer is
    /// simply the zero vector, which a caller checks with a length.</para>
    ///
    /// <para>Nothing that existed before calls this, and <see cref="NormalAt"/> is
    /// unchanged, so adding the exact overrides moved no incumbent answer.</para>
    /// </summary>
    public virtual Vector3d NormalRawAt(double u, double v)
    {
        double hu = double.IsFinite(DomainU.Length) ? Math.Max(1e-7, DomainU.Length * 1e-7) : 1e-7;
        double hv = double.IsFinite(DomainV.Length) ? Math.Max(1e-7, DomainV.Length * 1e-7) : 1e-7;
        double u0 = DomainU.Clamp(u - hu), u1 = DomainU.Clamp(u + hu);
        double v0 = DomainV.Clamp(v - hv), v1 = DomainV.Clamp(v + hv);
        var du = (PointAt(u1, v) - PointAt(u0, v)) / (u1 - u0);
        var dv = (PointAt(u, v1) - PointAt(u, v0)) / (v1 - v0);
        return du.Cross(dv);
    }

    /// <summary>Unit normal by central differences; subclasses override with exact normals.</summary>
    public virtual Vector3d NormalAt(double u, double v)
    {
        double hu = double.IsFinite(DomainU.Length) ? Math.Max(1e-7, DomainU.Length * 1e-7) : 1e-7;
        double hv = double.IsFinite(DomainV.Length) ? Math.Max(1e-7, DomainV.Length * 1e-7) : 1e-7;
        double u0 = DomainU.Clamp(u - hu), u1 = DomainU.Clamp(u + hu);
        double v0 = DomainV.Clamp(v - hv), v1 = DomainV.Clamp(v + hv);
        var du = (PointAt(u1, v) - PointAt(u0, v)) / (u1 - u0);
        var dv = (PointAt(u, v1) - PointAt(u, v0)) / (v1 - v0);
        return du.Cross(dv).Normalized();
    }

    /// <summary>
    /// Inverse evaluation: parameters of a point lying on (or near) the surface. The base
    /// implementation seeds from a coarse grid and runs damped Gauss–Newton (finite
    /// domains only); plane/cylinder/sphere override with exact formulas. Returns false
    /// when the point cannot be brought within <paramref name="tolerance"/> of the surface.
    /// The 1e-8 default suits exact overrides; pullback of traced/sampled geometry passes
    /// the looser <see cref="FaceGeometry.InverseEvaluationTolerance"/> explicitly.
    /// </summary>
    /// <summary>
    /// Brings a refinement iterate back into a generator's parameter domain: clamped for
    /// an open generator, wrapped for a closed one (where the domain ends are the same
    /// point, so clamping would pin the solve at a seam it should walk across).
    /// </summary>
    private protected static double FoldIntoDomain(double t, in Interval domain, bool periodic)
    {
        if (!periodic)
            return domain.Clamp(t);
        double period = domain.Length;
        double local = (t - domain.Start) % period;
        if (local < 0)
            local += period;
        return domain.Start + local;
    }

    public virtual bool TryProjectPoint(in Vector3d point, out Vector2d uv, double tolerance = 1e-8)
    {
        uv = default;
        var domainU = DomainU;
        var domainV = DomainV;
        if (!double.IsFinite(domainU.Length) || !double.IsFinite(domainV.Length))
            return false;

        var target = point; // local copy: `in` parameters cannot be captured by local functions
        const int seedResolution = 16;
        var distances = new double[(seedResolution + 1) * (seedResolution + 1)];
        int bestI = 0, bestJ = 0;
        double bestDistance = double.PositiveInfinity;
        for (int i = 0; i <= seedResolution; i++)
        {
            for (int j = 0; j <= seedResolution; j++)
            {
                double su = domainU.ParameterAt((double)i / seedResolution);
                double sv = domainV.ParameterAt((double)j / seedResolution);
                double d = PointAt(su, sv).DistanceSquaredTo(point);
                distances[i * (seedResolution + 1) + j] = d;
                if (d < bestDistance)
                {
                    bestDistance = d;
                    bestI = i;
                    bestJ = j;
                }
            }
        }

        // Periodic-u surfaces must wrap rather than clamp: the seed can land on the
        // wrong side of the seam, and a clamped Newton step then pins at the domain
        // edge forever instead of walking across it.
        double periodU = FaceGeometry.PeriodU(this);
        double WrapU(double x) => periodU > 0
            ? domainU.Start + (((x - domainU.Start) % periodU) + periodU) % periodU
            : domainU.Clamp(x);

        bool Refine(double seedU, double seedV, ref Vector2d found)
        {
            double u = seedU, v = seedV;
            double hu = Math.Max(1e-7, domainU.Length * 1e-7);
            double hv = Math.Max(1e-7, domainV.Length * 1e-7);
            for (int iteration = 0; iteration < 25; iteration++)
            {
                var residual = PointAt(u, v) - target;
                if (residual.Length < tolerance)
                {
                    found = new Vector2d(u, v);
                    return true;
                }
                var du = (PointAt(WrapU(u + hu), v) - PointAt(WrapU(u - hu), v)) / (2 * hu);
                var dv = (PointAt(u, domainV.Clamp(v + hv)) - PointAt(u, domainV.Clamp(v - hv))) / (2 * hv);

                // Normal equations for the 2-unknown least squares step, lightly damped.
                double a11 = du.Dot(du) + 1e-12, a12 = du.Dot(dv), a22 = dv.Dot(dv) + 1e-12;
                double b1 = -du.Dot(residual), b2 = -dv.Dot(residual);
                double det = a11 * a22 - a12 * a12;
                // Near-underflow degenerate-Jacobian guard, not a geometric tolerance.
                if (Math.Abs(det) < 1e-30)
                    return false;
                u = WrapU(u + (b1 * a22 - b2 * a12) / det);
                v = domainV.Clamp(v + (b2 * a11 - b1 * a12) / det);
            }
            if ((PointAt(u, v) - target).Length < tolerance)
            {
                found = new Vector2d(u, v);
                return true;
            }
            return false;
        }

        // The global-best seed goes FIRST, alone: every call the single-seed
        // implementation used to satisfy takes exactly the same iteration and returns
        // bit-identical parameters.
        double SeedU(int i) => domainU.ParameterAt((double)i / seedResolution);
        double SeedV(int j) => domainV.ParameterAt((double)j / seedResolution);
        if (Refine(SeedU(bestI), SeedV(bestJ), ref uv))
            return true;

        // The swept-surface lesson, ported to the 2D grid: a surface can fold two
        // branches closer together than one seed interval, so the sampled distance shows
        // ONE broad minimum straddling both and Newton from the global best descends to
        // the wrong branch. Refine from every LOCAL minimum of the grid and its
        // neighbours — purely combinatorial, no epsilon — taking the first seed that
        // converges. Cost only ever paid when the fast path above has already failed.
        double At(int i, int j) => distances[i * (seedResolution + 1) + j];
        bool IsLocalMinimum(int i, int j)
        {
            double d = At(i, j);
            for (int di = -1; di <= 1; di++)
            {
                for (int dj = -1; dj <= 1; dj++)
                {
                    int ni = i + di, nj = j + dj;
                    if (ni < 0 || nj < 0 || ni > seedResolution || nj > seedResolution)
                        continue;
                    if (At(ni, nj) < d)
                        return false;
                }
            }
            return true;
        }

        var tried = new bool[distances.Length];
        tried[bestI * (seedResolution + 1) + bestJ] = true;
        for (int i = 0; i <= seedResolution; i++)
        {
            for (int j = 0; j <= seedResolution; j++)
            {
                if (!IsLocalMinimum(i, j))
                    continue;
                for (int di = -1; di <= 1; di++)
                {
                    for (int dj = -1; dj <= 1; dj++)
                    {
                        int ni = i + di, nj = j + dj;
                        if (ni < 0 || nj < 0 || ni > seedResolution || nj > seedResolution)
                            continue;
                        int index = ni * (seedResolution + 1) + nj;
                        if (tried[index])
                            continue;
                        tried[index] = true;
                        if (Refine(SeedU(ni), SeedV(nj), ref uv))
                            return true;
                    }
                }
            }
        }
        return false;
    }
}

/// <summary>
/// Infinite plane through <paramref name="origin"/> spanned by the unit orthogonal
/// directions <paramref name="xDirection"/> and <paramref name="yDirection"/>;
/// normal = x × y. Faces bound it via their loops.
/// </summary>
public sealed class PlaneSurface(Vector3d origin, Vector3d xDirection, Vector3d yDirection) : Surface
{
    private static readonly Interval Infinite = new(double.NegativeInfinity, double.PositiveInfinity);

    /// <summary>Plane spanned by the frame's X/Y through its origin (normal = frame Z).</summary>
    public PlaneSurface(in Frame3d frame) : this(frame.Origin, frame.X, frame.Y) { }

    public Vector3d Origin => origin;
    public Vector3d XDirection => xDirection;
    public Vector3d YDirection => yDirection;
    public Vector3d Normal => xDirection.Cross(yDirection);

    public override Interval DomainU => Infinite;
    public override Interval DomainV => Infinite;

    public override Vector3d PointAt(double u, double v) => origin + xDirection * u + yDirection * v;
    public override Vector3d NormalAt(double u, double v) => Normal;

    /// <summary>Exact: x x y is already the unnormalised normal.</summary>
    public override Vector3d NormalRawAt(double u, double v) => Normal;

    /// <summary>Plane coordinates of a 3D point (assumed on or near the plane).</summary>
    public Vector2d Project(in Vector3d point)
    {
        var d = point - origin;
        return new Vector2d(d.Dot(xDirection), d.Dot(yDirection));
    }

    public override bool TryProjectPoint(in Vector3d point, out Vector2d uv, double tolerance = 1e-8)
    {
        uv = Project(point);
        return Math.Abs((point - origin).Dot(Normal)) < tolerance;
    }
}

/// <summary>
/// Infinite cylinder about the axis z = x × y through <paramref name="origin"/>;
/// u is the angle [0, 2π] (closed), v the axial coordinate. Normal points outward.
/// </summary>
public sealed class CylinderSurface(Vector3d origin, Vector3d xDirection, Vector3d yDirection, double radius) : Surface
{
    public Vector3d Origin => origin;
    public Vector3d XDirection => xDirection;
    public Vector3d YDirection => yDirection;
    public Vector3d Axis => xDirection.Cross(yDirection);
    public double Radius => radius;

    public override Interval DomainU => new(0, 2 * Math.PI);
    public override Interval DomainV => new(double.NegativeInfinity, double.PositiveInfinity);

    public override Vector3d PointAt(double u, double v) =>
        origin + xDirection * (radius * Math.Cos(u)) + yDirection * (radius * Math.Sin(u)) + Axis * v;

    public override Vector3d NormalAt(double u, double v) =>
        xDirection * Math.Cos(u) + yDirection * Math.Sin(u);

    /// <summary>Exact: dP/du x dP/dv is the outward radial scaled by the radius.</summary>
    public override Vector3d NormalRawAt(double u, double v) =>
        (xDirection * Math.Cos(u) + yDirection * Math.Sin(u)) * radius;

    public override bool TryProjectPoint(in Vector3d point, out Vector2d uv, double tolerance = 1e-8)
    {
        var d = point - origin;
        double u = Math.Atan2(d.Dot(yDirection), d.Dot(xDirection));
        if (u < 0)
            u += 2 * Math.PI;
        uv = new Vector2d(u, d.Dot(Axis));
        return Math.Abs((d - Axis * d.Dot(Axis)).Length - radius) < tolerance;
    }
}

/// <summary>Sphere; u is azimuth [0, 2π], v is latitude [−π/2, π/2]. Normal points outward.</summary>
public sealed class SphereSurface(Vector3d center, double radius) : Surface
{
    public Vector3d Center => center;
    public double Radius => radius;

    public override Interval DomainU => new(0, 2 * Math.PI);
    public override Interval DomainV => new(-Math.PI / 2, Math.PI / 2);

    public override Vector3d PointAt(double u, double v) => center + NormalAt(u, v) * radius;

    public override Vector3d NormalAt(double u, double v) => new(
        Math.Cos(v) * Math.Cos(u),
        Math.Cos(v) * Math.Sin(u),
        Math.Sin(v));

    /// <summary>Exact: the outward radial scaled by r^2 cos v, which vanishes at the poles.</summary>
    public override Vector3d NormalRawAt(double u, double v) =>
        NormalAt(u, v) * (radius * radius * Math.Cos(v));

    public override bool TryProjectPoint(in Vector3d point, out Vector2d uv, double tolerance = 1e-8)
    {
        var d = point - center;
        double u = Math.Atan2(d.Y, d.X);
        if (u < 0)
            u += 2 * Math.PI;
        uv = new Vector2d(u, Math.Asin(Math.Clamp(d.Z / Math.Max(d.Length, 1e-300), -1, 1)));
        return Math.Abs(d.Length - radius) < tolerance;
    }
}

/// <summary>Tensor-product rational B-spline surface.</summary>
public sealed class NurbsSurface : Surface
{
    public int DegreeU { get; }
    public int DegreeV { get; }

    /// <summary>[countU, countV] grids.</summary>
    public Vector3d[,] ControlPoints { get; }

    public double[,] Weights { get; }
    public IReadOnlyList<double> KnotsU { get; }
    public IReadOnlyList<double> KnotsV { get; }

    public NurbsSurface(
        int degreeU, int degreeV,
        Vector3d[,] controlPoints, double[,]? weights,
        IReadOnlyList<double> knotsU, IReadOnlyList<double> knotsV)
    {
        int countU = controlPoints.GetLength(0);
        int countV = controlPoints.GetLength(1);
        if (degreeU < 1 || degreeV < 1)
            throw new ArgumentOutOfRangeException(nameof(degreeU));
        if (countU < degreeU + 1 || countV < degreeV + 1)
            throw new ArgumentException("Not enough control points for the requested degrees.");
        if (knotsU.Count != countU + degreeU + 1 || knotsV.Count != countV + degreeV + 1)
            throw new ArgumentException("Knot count does not match control net and degree.");
        if (weights is not null &&
            (weights.GetLength(0) != countU || weights.GetLength(1) != countV))
            throw new ArgumentException("Weight grid must match the control net.");

        DegreeU = degreeU;
        DegreeV = degreeV;
        ControlPoints = controlPoints;
        KnotsU = knotsU;
        KnotsV = knotsV;
        if (weights is null)
        {
            Weights = new double[countU, countV];
            for (int i = 0; i < countU; i++)
            {
                for (int j = 0; j < countV; j++)
                    Weights[i, j] = 1;
            }
        }
        else
        {
            Weights = weights;
        }
    }

    public override Interval DomainU => new(KnotsU[DegreeU], KnotsU[ControlPoints.GetLength(0)]);
    public override Interval DomainV => new(KnotsV[DegreeV], KnotsV[ControlPoints.GetLength(1)]);

    public override Vector3d PointAt(double u, double v)
    {
        u = DomainU.Clamp(u);
        v = DomainV.Clamp(v);
        int spanU = BSplineBasis.FindSpan(u, DegreeU, ControlPoints.GetLength(0), KnotsU);
        int spanV = BSplineBasis.FindSpan(v, DegreeV, ControlPoints.GetLength(1), KnotsV);
        Span<double> basisU = stackalloc double[DegreeU + 1];
        Span<double> basisV = stackalloc double[DegreeV + 1];
        BSplineBasis.Evaluate(spanU, u, DegreeU, KnotsU, basisU);
        BSplineBasis.Evaluate(spanV, v, DegreeV, KnotsV, basisV);

        var numerator = Vector3d.Zero;
        double denominator = 0;
        for (int i = 0; i <= DegreeU; i++)
        {
            for (int j = 0; j <= DegreeV; j++)
            {
                int iu = spanU - DegreeU + i;
                int jv = spanV - DegreeV + j;
                double bw = basisU[i] * basisV[j] * Weights[iu, jv];
                numerator += ControlPoints[iu, jv] * bw;
                denominator += bw;
            }
        }
        return numerator / denominator;
    }

    /// <summary>
    /// Tensor-product B-spline surface passing exactly through a <c>[countU, countV]</c>
    /// grid of points (the surface generalization of
    /// <see cref="NurbsCurve.InterpolatePoints"/>; <c>GeomAPI_PointsToBSpline</c>-style
    /// global interpolation).
    /// </summary>
    /// <remarks>
    /// Chord-length parameters are computed along every column and every row and
    /// AVERAGED per direction (The NURBS Book eqn 9.7), so the two directions share ONE
    /// parameterization — the loft-crack rule: a per-line reparameterization would put
    /// every strip on its own mapping and the fit would no longer pass through the grid.
    /// The surface is then built by two passes of the same natural-end cubic the curve
    /// uses (algorithm A9.4): interpolate each column in u, then interpolate the
    /// resulting control rows in v. Because parameters and knots are shared, the
    /// collocation matrix is identical for every line in a direction, so it is built once
    /// and re-solved per line. A direction with exactly two points drops to a straight
    /// (degree-1) segment, exactly as the curve does; three or more take the cubic, whose
    /// two natural-end conditions add one control point beyond each end
    /// (<c>countU + 2</c> × <c>countV + 2</c> control points for a cubic × cubic fit).
    /// The surface is polynomial (unit weights) and reproduces every input point to
    /// round-off. Periodic/closed grids and least-squares approximation are not yet
    /// supported (a grid with more points than control points wanted is a different fit).
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The grid is smaller than 2×2, or a whole column/row is degenerate so a direction
    /// cannot be parameterized, or the averaged parameters are not strictly increasing
    /// (coincident point rows or columns).
    /// </exception>
    public static NurbsSurface InterpolatePoints(Vector3d[,] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        int countU = points.GetLength(0);
        int countV = points.GetLength(1);
        if (countU < 2 || countV < 2)
            throw new ArgumentException(
                "Surface interpolation needs at least a 2×2 grid of points.", nameof(points));

        var dirU = new InterpolationDirection(AveragedChordParameters(points, alongU: true), alongU: true);
        var dirV = new InterpolationDirection(AveragedChordParameters(points, alongU: false), alongU: false);

        // Stage 1: interpolate each column (fixed v index) in u → intermediate[a, j].
        var intermediate = new Vector3d[dirU.ControlCount, countV];
        var columnData = new Vector3d[countU];
        for (int j = 0; j < countV; j++)
        {
            for (int i = 0; i < countU; i++)
                columnData[i] = points[i, j];
            Vector3d[] column = dirU.Solve(columnData);
            for (int a = 0; a < dirU.ControlCount; a++)
                intermediate[a, j] = column[a];
        }

        // Stage 2: interpolate each intermediate control row in v → control[a, b].
        var control = new Vector3d[dirU.ControlCount, dirV.ControlCount];
        var rowData = new Vector3d[countV];
        for (int a = 0; a < dirU.ControlCount; a++)
        {
            for (int j = 0; j < countV; j++)
                rowData[j] = intermediate[a, j];
            Vector3d[] row = dirV.Solve(rowData);
            for (int b = 0; b < dirV.ControlCount; b++)
                control[a, b] = row[b];
        }

        return new NurbsSurface(dirU.Degree, dirV.Degree, control, null, dirU.Knots, dirV.Knots);
    }

    /// <summary>
    /// Chord-length parameters averaged over every cross-line (The NURBS Book eqn 9.7).
    /// For the u direction each column is parameterized by its own chord lengths and the
    /// per-index values are averaged over the columns; the result is indexed by the u
    /// index and shared by every column of the fit. Degenerate (zero-length) lines are
    /// skipped so a single bad line cannot poison the parameterization.
    /// </summary>
    private static double[] AveragedChordParameters(Vector3d[,] points, bool alongU)
    {
        int countU = points.GetLength(0);
        int countV = points.GetLength(1);
        int count = alongU ? countU : countV; // length of the result
        int lines = alongU ? countV : countU; // cross-lines to average over
        var averaged = new double[count];
        var line = new double[count];
        int valid = 0;
        for (int l = 0; l < lines; l++)
        {
            double total = 0;
            line[0] = 0;
            for (int t = 1; t < count; t++)
            {
                Vector3d a = alongU ? points[t, l] : points[l, t];
                Vector3d b = alongU ? points[t - 1, l] : points[l, t - 1];
                total += a.DistanceTo(b);
                line[t] = total;
            }
            if (total <= 0)
                continue; // this line collapses to a point; it says nothing about the spacing
            for (int t = 1; t < count - 1; t++)
                averaged[t] += line[t] / total;
            valid++;
        }
        string dir = alongU ? "u" : "v";
        string cross = alongU ? "column" : "row";
        if (valid == 0)
            throw new ArgumentException(
                $"Surface interpolation cannot parameterize the {dir} direction; every {cross} of points is degenerate (zero length).",
                nameof(points));

        averaged[0] = 0;
        averaged[count - 1] = 1; // exact, despite the division round-off
        for (int t = 1; t < count; t++)
        {
            if (t < count - 1)
                averaged[t] /= valid;
            if (!(averaged[t] > averaged[t - 1]))
                throw new ArgumentException(
                    $"Surface interpolation cannot parameterize the {dir} direction; the averaged parameters are not strictly increasing (coincident point {cross}s).",
                    nameof(points));
        }
        return averaged;
    }

    /// <summary>
    /// One direction of the tensor-product interpolation: the shared knot vector plus a
    /// collocation system built once from the averaged parameters and re-solved per line.
    /// Two points give a straight degree-1 segment; three or more give the natural-end
    /// cubic (the curve's <c>InterpolateOpen</c>, restated so the surface shares no
    /// mutable curve state and each direction can re-run its own factorization).
    /// </summary>
    private sealed class InterpolationDirection
    {
        public int Degree { get; }
        public double[] Knots { get; }
        public int ControlCount { get; }

        private readonly int _dataCount;
        private readonly double[] _sub;
        private readonly double[] _diag;
        private readonly double[] _sup;
        private readonly double _startCoeff; // −N″₀(0), scales the fixed start control point into the RHS
        private readonly double _endCoeff;   // −N″_{k+1}(1), scales the fixed end control point into the RHS

        public InterpolationDirection(double[] parameters, bool alongU)
        {
            _dataCount = parameters.Length;
            if (_dataCount == 2)
            {
                // A straight segment interpolates two points exactly; no natural-end system.
                Degree = 1;
                ControlCount = 2;
                Knots = [0, 0, 1, 1];
                _sub = _diag = _sup = [];
                return;
            }

            Degree = 3;
            int k = _dataCount;
            ControlCount = k + 2;
            var knots = new double[k + 6];
            for (int i = 0; i < 4; i++)
            {
                knots[i] = 0;
                knots[k + 2 + i] = 1;
            }
            for (int j = 1; j <= k - 2; j++)
                knots[j + 3] = parameters[j];
            Knots = knots;

            _sub = new double[k];
            _diag = new double[k];
            _sup = new double[k];

            const int stride = 4;
            Span<double> ders = stackalloc double[3 * stride];
            // Natural start: N″₁(0) y₀ + N″₂(0) y₁ = −N″₀(0) Q₀ (N″₃(0) is 0 at the clamped end).
            BSplineBasis.EvaluateDerivatives(3, 0.0, 3, knots, 2, ders);
            _diag[0] = ders[2 * stride + 1];
            _sup[0] = ders[2 * stride + 2];
            _startCoeff = -ders[2 * stride + 0];

            // Collocation at each interior parameter (knot of multiplicity 1 ⇒ tridiagonal).
            Span<double> basis = stackalloc double[4];
            for (int j = 1; j <= k - 2; j++)
            {
                int span = BSplineBasis.FindSpan(parameters[j], 3, k + 2, knots);
                BSplineBasis.Evaluate(span, parameters[j], 3, knots, basis);
                _sub[j] = CoefficientOf(basis, span, j);
                _diag[j] = CoefficientOf(basis, span, j + 1);
                _sup[j] = CoefficientOf(basis, span, j + 2);
            }

            // Natural end: N″_{k−1}(1) y_{k−2} + N″_k(1) y_{k−1} = −N″_{k+1}(1) Q_{k−1}.
            BSplineBasis.EvaluateDerivatives(k + 1, 1.0, 3, knots, 2, ders);
            _sub[k - 1] = ders[2 * stride + 1];
            _diag[k - 1] = ders[2 * stride + 2];
            _endCoeff = -ders[2 * stride + 3];
            _ = alongU; // reserved for future direction-specific diagnostics
        }

        /// <summary>Control points interpolating <paramref name="data"/> at the shared parameters.</summary>
        public Vector3d[] Solve(Vector3d[] data)
        {
            if (Degree == 1)
                return [data[0], data[1]];

            int k = _dataCount;
            var rhs = new Vector3d[k];
            rhs[0] = data[0] * _startCoeff;
            for (int j = 1; j <= k - 2; j++)
                rhs[j] = data[j];
            rhs[k - 1] = data[k - 1] * _endCoeff;

            // Thomas algorithm on a private copy of the diagonal (sub/sup are read-only).
            var diag = new double[k];
            Array.Copy(_diag, diag, k);
            for (int i = 1; i < k; i++)
            {
                double m = _sub[i] / diag[i - 1];
                diag[i] -= m * _sup[i - 1];
                rhs[i] -= rhs[i - 1] * m;
            }
            rhs[k - 1] /= diag[k - 1];
            for (int i = k - 2; i >= 0; i--)
                rhs[i] = (rhs[i] - rhs[i + 1] * _sup[i]) / diag[i];

            var control = new Vector3d[k + 2];
            control[0] = data[0];
            for (int i = 0; i < k; i++)
                control[i + 1] = rhs[i];
            control[k + 1] = data[k - 1];
            return control;
        }

        private static double CoefficientOf(ReadOnlySpan<double> basis, int span, int controlIndex)
        {
            int offset = controlIndex - (span - 3);
            return offset is >= 0 and <= 3 ? basis[offset] : 0.0;
        }
    }

    /// <summary>
    /// Least-squares B-spline surface fit: a control net COARSER than the data, so the
    /// surface approximates rather than interpolates (the approximation half of
    /// <c>GeomAPI_PointsToBSpline</c>; The NURBS Book §9.4, algorithm A9.7).
    /// </summary>
    /// <remarks>
    /// Chord-length parameters are averaged per direction exactly as
    /// <see cref="InterpolatePoints"/> does, then the fit separates: each column is fit in
    /// u to <paramref name="controlCountU"/> control points, and the resulting control rows
    /// are fit in v to <paramref name="controlCountV"/> — the standard tensor-product
    /// approximation. Each 1-D fit FIXES the two endpoints (so the four corners interpolate
    /// the corner data points and the four boundary curves fit the boundary rows) and
    /// solves the interior control points from the normal equations `NᵀN·P = R`; `NᵀN` is a
    /// small symmetric banded (bandwidth = degree) SPD matrix, so a dense Cholesky
    /// factor-once/solve-many is used — a fit's control count is small by design, and the
    /// CSR assembly of a general sparse solver buys nothing at these sizes. Because the
    /// parameters and knots are shared, the matrix is built and factored once per direction
    /// and re-solved per line. A control count equal to the data count degenerates to an
    /// endpoint-interpolating fit; a count of exactly <c>degree + 1</c> is a single Bézier
    /// span. The fit reproduces any polynomial of the fitted degree in each variable
    /// exactly (it lies in the spline space over any knots), which is the correctness
    /// oracle. Periodic grids are not yet supported.
    /// </remarks>
    /// <exception cref="ArgumentException">The grid is smaller than 2×2, or a direction cannot be parameterized.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A degree is below 1, or a control count is outside [degree + 1, point count].</exception>
    /// <exception cref="InvalidOperationException">The fit is rank-deficient — a basis function has no supporting data point (reduce the control count or add points).</exception>
    public static NurbsSurface Approximate(
        Vector3d[,] points, int controlCountU, int controlCountV, int degreeU = 3, int degreeV = 3)
    {
        ArgumentNullException.ThrowIfNull(points);
        int countU = points.GetLength(0);
        int countV = points.GetLength(1);
        if (countU < 2 || countV < 2)
            throw new ArgumentException(
                "Surface approximation needs at least a 2×2 grid of points.", nameof(points));
        if (degreeU < 1)
            throw new ArgumentOutOfRangeException(nameof(degreeU), "Degree must be at least 1.");
        if (degreeV < 1)
            throw new ArgumentOutOfRangeException(nameof(degreeV), "Degree must be at least 1.");
        if (controlCountU < degreeU + 1 || controlCountU > countU)
            throw new ArgumentOutOfRangeException(nameof(controlCountU),
                $"controlCountU must be between degreeU + 1 ({degreeU + 1}) and the u point count ({countU}).");
        if (controlCountV < degreeV + 1 || controlCountV > countV)
            throw new ArgumentOutOfRangeException(nameof(controlCountV),
                $"controlCountV must be between degreeV + 1 ({degreeV + 1}) and the v point count ({countV}).");

        var dirU = new LeastSquaresDirection(AveragedChordParameters(points, alongU: true), controlCountU, degreeU, alongU: true);
        var dirV = new LeastSquaresDirection(AveragedChordParameters(points, alongU: false), controlCountV, degreeV, alongU: false);

        // Stage 1: fit each column (fixed v index) in u → intermediate[a, j].
        var intermediate = new Vector3d[controlCountU, countV];
        var columnData = new Vector3d[countU];
        for (int j = 0; j < countV; j++)
        {
            for (int i = 0; i < countU; i++)
                columnData[i] = points[i, j];
            Vector3d[] column = dirU.Solve(columnData);
            for (int a = 0; a < controlCountU; a++)
                intermediate[a, j] = column[a];
        }

        // Stage 2: fit each intermediate control row in v → control[a, b].
        var control = new Vector3d[controlCountU, controlCountV];
        var rowData = new Vector3d[countV];
        for (int a = 0; a < controlCountU; a++)
        {
            for (int j = 0; j < countV; j++)
                rowData[j] = intermediate[a, j];
            Vector3d[] row = dirV.Solve(rowData);
            for (int b = 0; b < controlCountV; b++)
                control[a, b] = row[b];
        }

        return new NurbsSurface(degreeU, degreeV, control, null, dirU.Knots, dirV.Knots);
    }

    /// <summary>
    /// Least-squares B-spline surface fit whose control net is grown AUTOMATICALLY until the
    /// grid is fit to within <paramref name="tolerance"/> (the tolerance-driven mode of
    /// <c>GeomAPI_PointsToBSpline</c>).
    /// </summary>
    /// <remarks>
    /// Starting from the smallest net (one Bézier span per direction) the net is grown a
    /// control point at a time in every direction that still has room, re-fitting through
    /// <see cref="Approximate(Vector3d[,], int, int, int, int)"/> until the largest deviation
    /// at any grid point is at or below <paramref name="tolerance"/>. Growth ALWAYS
    /// terminates: a net as large as the data is a determined system whose residual is
    /// round-off, so a positive tolerance is always reached (a tolerance below round-off
    /// simply returns the full-count interpolating fit). The net is not guaranteed MINIMAL —
    /// a direction that is already flat still grows in lockstep with a curved one — but it is
    /// guaranteed to meet the tolerance, which is the contract; when a minimal or asymmetric
    /// net matters, size each direction explicitly through the control-count overload.
    /// </remarks>
    /// <exception cref="ArgumentException">The grid is smaller than 2×2, or a direction cannot be parameterized.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A degree is below 1 or exceeds one less than the point count, or the tolerance is not positive.</exception>
    public static NurbsSurface Approximate(
        Vector3d[,] points, double tolerance, int degreeU = 3, int degreeV = 3)
    {
        ArgumentNullException.ThrowIfNull(points);
        int countU = points.GetLength(0);
        int countV = points.GetLength(1);
        if (countU < 2 || countV < 2)
            throw new ArgumentException(
                "Surface approximation needs at least a 2×2 grid of points.", nameof(points));
        if (!(tolerance > 0))
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Tolerance must be positive.");
        if (degreeU < 1 || degreeU > countU - 1)
            throw new ArgumentOutOfRangeException(nameof(degreeU),
                $"degreeU must be between 1 and one less than the u point count ({countU - 1}).");
        if (degreeV < 1 || degreeV > countV - 1)
            throw new ArgumentOutOfRangeException(nameof(degreeV),
                $"degreeV must be between 1 and one less than the v point count ({countV - 1}).");

        double[] paramsU = AveragedChordParameters(points, alongU: true);
        double[] paramsV = AveragedChordParameters(points, alongU: false);

        int controlCountU = degreeU + 1, controlCountV = degreeV + 1;
        while (true)
        {
            var surface = Approximate(points, controlCountU, controlCountV, degreeU, degreeV);
            double worst = 0;
            for (int i = 0; i < countU && worst <= tolerance; i++)
                for (int j = 0; j < countV; j++)
                {
                    worst = Math.Max(worst, surface.PointAt(paramsU[i], paramsV[j]).DistanceTo(points[i, j]));
                    if (worst > tolerance)
                        break;
                }
            if (worst <= tolerance || (controlCountU == countU && controlCountV == countV))
                return surface;
            if (controlCountU < countU)
                controlCountU++;
            if (controlCountV < countV)
                controlCountV++;
        }
    }

    /// <summary>
    /// One direction of the tensor-product least-squares fit: the knot vector (de Boor's
    /// averaging over the parameters, The NURBS Book eqn 9.68), plus the normal-equations
    /// matrix `NᵀN` built once from the interior basis and re-solved per line (the endpoints
    /// are interpolated, so only the interior control points are unknown).
    /// </summary>
    private sealed class LeastSquaresDirection
    {
        public double[] Knots { get; }
        public int ControlCount { get; }

        private readonly int _dataCount;
        private readonly int _free; // interior (unknown) control points = ControlCount − 2
        private readonly int[][] _windowIndex; // per interior parameter k: nonzero basis control indices
        private readonly double[][] _windowValue;
        private readonly double[][]? _chol; // Cholesky factor of NᵀN (free × free), null when _free == 0

        public LeastSquaresDirection(double[] parameters, int controlCount, int degree, bool alongU)
        {
            _dataCount = parameters.Length;
            ControlCount = controlCount;
            _free = controlCount - 2;

            int n = controlCount - 1, p = degree, m = _dataCount - 1;
            var knots = new double[controlCount + p + 1];
            for (int i = 0; i <= p; i++)
                knots[i] = 0;
            for (int i = controlCount; i <= controlCount + p; i++)
                knots[i] = 1;
            double d = (double)(m + 1) / (n - p + 1);
            for (int j = 1; j <= n - p; j++)
            {
                int i = (int)(j * d);
                double alpha = j * d - i;
                knots[p + j] = (1 - alpha) * parameters[i - 1] + alpha * parameters[i];
            }
            Knots = knots;

            _windowIndex = new int[_dataCount][];
            _windowValue = new double[_dataCount][];
            double[][]? a = _free > 0 ? NewSquareMatrix(_free) : null;
            Span<double> basis = stackalloc double[p + 1];
            for (int k = 1; k <= m - 1; k++)
            {
                int span = BSplineBasis.FindSpan(parameters[k], p, controlCount, knots);
                BSplineBasis.Evaluate(span, parameters[k], p, knots, basis);
                var idx = new int[p + 1];
                var val = new double[p + 1];
                for (int t = 0; t <= p; t++)
                {
                    idx[t] = span - p + t;
                    val[t] = basis[t];
                }
                _windowIndex[k] = idx;
                _windowValue[k] = val;
                if (a is null)
                    continue;
                for (int s = 0; s <= p; s++)
                {
                    int gi = idx[s] - 1;
                    if (gi < 0 || gi >= _free)
                        continue;
                    for (int t = 0; t <= p; t++)
                    {
                        int gj = idx[t] - 1;
                        if (gj >= 0 && gj < _free)
                            a[gi][gj] += val[s] * val[t];
                    }
                }
            }
            _chol = a is null ? null : CholeskyFactor(a, alongU);
        }

        /// <summary>Control points least-squares-fitting <paramref name="data"/> with the two ends interpolated.</summary>
        public Vector3d[] Solve(Vector3d[] data)
        {
            var control = new Vector3d[ControlCount];
            control[0] = data[0];
            control[ControlCount - 1] = data[_dataCount - 1];
            if (_free == 0)
                return control;

            Vector3d q0 = data[0], qm = data[_dataCount - 1];
            int m = _dataCount - 1;
            var rhs = new Vector3d[_free];
            for (int k = 1; k <= m - 1; k++)
            {
                int[] idx = _windowIndex[k];
                double[] val = _windowValue[k];
                var r = data[k];
                for (int t = 0; t < idx.Length; t++)
                {
                    if (idx[t] == 0)
                        r -= q0 * val[t];
                    else if (idx[t] == ControlCount - 1)
                        r -= qm * val[t];
                }
                for (int t = 0; t < idx.Length; t++)
                {
                    int gi = idx[t] - 1;
                    if (gi >= 0 && gi < _free)
                        rhs[gi] += r * val[t];
                }
            }

            Vector3d[] freeControl = CholeskySolve(_chol!, rhs);
            for (int i = 0; i < _free; i++)
                control[i + 1] = freeControl[i];
            return control;
        }
    }

    private static double[][] NewSquareMatrix(int n)
    {
        var a = new double[n][];
        for (int i = 0; i < n; i++)
            a[i] = new double[n];
        return a;
    }

    /// <summary>Lower-triangular Cholesky factor of a symmetric positive-definite matrix.</summary>
    private static double[][] CholeskyFactor(double[][] a, bool alongU)
    {
        int n = a.Length;
        double[][] l = NewSquareMatrix(n);
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                double sum = a[i][j];
                for (int k = 0; k < j; k++)
                    sum -= l[i][k] * l[j][k];
                if (i == j)
                {
                    if (sum <= 0)
                        throw new InvalidOperationException(
                            $"Surface approximation is rank-deficient in the {(alongU ? "u" : "v")} direction; a basis function has no supporting data point (reduce the control count or add points).");
                    l[i][j] = Math.Sqrt(sum);
                }
                else
                {
                    l[i][j] = sum / l[j][j];
                }
            }
        }
        return l;
    }

    /// <summary>Solves L Lᵀ x = b for a vector-valued right-hand side (three independent components).</summary>
    private static Vector3d[] CholeskySolve(double[][] l, Vector3d[] b)
    {
        int n = l.Length;
        var y = new Vector3d[n];
        for (int i = 0; i < n; i++)
        {
            var sum = b[i];
            for (int k = 0; k < i; k++)
                sum -= y[k] * l[i][k];
            y[i] = sum / l[i][i];
        }
        var x = new Vector3d[n];
        for (int i = n - 1; i >= 0; i--)
        {
            var sum = y[i];
            for (int k = i + 1; k < n; k++)
                sum -= x[k] * l[k][i];
            x[i] = sum / l[i][i];
        }
        return x;
    }
}
