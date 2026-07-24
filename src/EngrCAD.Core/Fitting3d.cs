namespace EngrCAD.Core;

/// <summary>
/// Oriented 3D box: a rigid <see cref="Frame3d"/> (center + unit axes) with half
/// extents along the frame axes. Produced by <see cref="Fitting3d.FitBox"/>.
/// </summary>
public readonly struct OrientedBox3d
{
    public Frame3d Frame { get; }
    /// <summary>Half sizes along (Frame.X, Frame.Y, Frame.Z).</summary>
    public Vector3d HalfExtents { get; }

    public OrientedBox3d(in Frame3d frame, in Vector3d halfExtents)
    {
        Frame = frame;
        HalfExtents = halfExtents;
    }

    public Vector3d Center => Frame.Origin;
    public double Volume => 8 * HalfExtents.X * HalfExtents.Y * HalfExtents.Z;

    /// <summary>Corner i ∈ [0, 8): bit 0 → +X/−X, bit 1 → +Y/−Y, bit 2 → +Z/−Z.</summary>
    public Vector3d Corner(int i) => Frame.ToWorld(new Vector3d(
        (i & 1) == 0 ? HalfExtents.X : -HalfExtents.X,
        (i & 2) == 0 ? HalfExtents.Y : -HalfExtents.Y,
        (i & 4) == 0 ? HalfExtents.Z : -HalfExtents.Z));

    public bool Contains(in Vector3d point, in Tolerance tolerance)
    {
        var local = Frame.ToLocal(point);
        return Math.Abs(local.X) <= HalfExtents.X + tolerance.Linear &&
               Math.Abs(local.Y) <= HalfExtents.Y + tolerance.Linear &&
               Math.Abs(local.Z) <= HalfExtents.Z + tolerance.Linear;
    }
}

/// <summary>
/// Point-cloud fits in 3D, built on a covariance eigen-analysis (cyclic Jacobi on the
/// symmetric 3×3): PCA oriented bounding box and least-squares (total least squares /
/// orthogonal-distance) plane. geometry3Sharp counterparts: ContOrientedBox3 /
/// GaussPointsFit3, OrthogonalPlaneFit3.
/// </summary>
public static class Fitting3d
{
    /// <summary>
    /// Best-fit plane through a point cloud, minimizing orthogonal distances, returned
    /// as a <see cref="Frame3d"/>: origin at the centroid, Z the plane normal
    /// (smallest covariance eigenvector), X the dominant in-plane spread direction
    /// (largest eigenvector) — a natural, deterministic in-plane basis rather than an
    /// arbitrary perpendicular. Throws when the points do not determine a plane
    /// (fewer than 3, coincident, or collinear).
    /// </summary>
    public static Frame3d FitPlane(IReadOnlyList<Vector3d> points)
    {
        var (centroid, values, vectors) = CovarianceEigen(points);
        if (points.Count < 3 || values[1] <= 1e-12 * Math.Max(values[0], 1e-300))
            throw new ArgumentException(
                "The points do not determine a plane (coincident or collinear).", nameof(points));
        // vectors[0] = largest spread (X), vectors[1] = mid (Y); FromXY makes Z = X × Y,
        // which is ± the smallest eigenvector — the plane normal either way.
        return Frame3d.FromXY(centroid, vectors[0], vectors[1]);
    }

    /// <summary>
    /// PCA oriented bounding box: covariance eigenvectors give the axes, and the box is
    /// shrunk to contain the exact projections onto them (center re-fit, so it is the
    /// tightest box WITH those axes). PCA axes are a good-fit heuristic, not the
    /// minimum-volume box (that is an open follow-up; g3 ships the same compromise).
    /// Degenerate clouds are fine — flat or collinear sets just get zero extents along
    /// the empty directions. Throws on an empty input.
    /// </summary>
    public static OrientedBox3d FitBox(IReadOnlyList<Vector3d> points)
    {
        var (_, _, vectors) = CovarianceEigen(points);
        var x = vectors[0];
        var y = vectors[1];
        var z = x.Cross(y); // right-handed; ± the third eigenvector

        var min = new Vector3d(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var max = -min;
        foreach (var p in points)
        {
            var projected = new Vector3d(p.Dot(x), p.Dot(y), p.Dot(z));
            min = Vector3d.Min(min, projected);
            max = Vector3d.Max(max, projected);
        }

        var mid = (min + max) * 0.5;
        var center = x * mid.X + y * mid.Y + z * mid.Z;
        return new OrientedBox3d(Frame3d.FromOrthonormal(center, x, y), (max - min) * 0.5);
    }

    /// <summary>
    /// Centroid plus eigen-decomposition of the point covariance: eigenvalues sorted
    /// descending with matching unit eigenvectors (orthonormal; the first two are
    /// returned, the third is their cross product up to sign).
    /// </summary>
    private static (Vector3d Centroid, double[] Values, Vector3d[] Vectors) CovarianceEigen(
        IReadOnlyList<Vector3d> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
            throw new ArgumentException("Cannot fit an empty point set.", nameof(points));

        var sum = Vector3d.Zero;
        foreach (var p in points)
            sum += p;
        var centroid = sum / points.Count;

        double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (var p in points)
        {
            var d = p - centroid;
            xx += d.X * d.X;
            xy += d.X * d.Y;
            xz += d.X * d.Z;
            yy += d.Y * d.Y;
            yz += d.Y * d.Z;
            zz += d.Z * d.Z;
        }
        double inv = 1.0 / points.Count;
        var (values, vectors) = SymmetricEigen3.Solve(xx * inv, xy * inv, xz * inv, yy * inv, yz * inv, zz * inv);
        return (centroid, values, vectors);
    }
}

/// <summary>
/// Eigen-decomposition of a symmetric 3×3 matrix by cyclic Jacobi rotations —
/// unconditionally convergent and accurate for the (positive semi-definite) covariance
/// matrices the fitting code feeds it.
/// </summary>
internal static class SymmetricEigen3
{
    /// <summary>
    /// Eigenvalues sorted descending with matching orthonormal eigenvectors for the
    /// symmetric matrix [[xx, xy, xz], [xy, yy, yz], [xz, yz, zz]].
    /// </summary>
    public static (double[] Values, Vector3d[] Vectors) Solve(
        double xx, double xy, double xz, double yy, double yz, double zz)
    {
        // a = working copy (upper triangle), v = accumulated rotations (columns are
        // eigenvectors).
        Span<double> a = [xx, xy, xz, yy, yz, zz]; // a00 a01 a02 a11 a12 a22
        Span<double> v = [1, 0, 0, 0, 1, 0, 0, 0, 1]; // row-major identity

        for (int sweep = 0; sweep < 50; sweep++)
        {
            double off = a[1] * a[1] + a[2] * a[2] + a[4] * a[4];
            double scale = a[0] * a[0] + a[3] * a[3] + a[5] * a[5];
            if (off <= 1e-30 * Math.Max(scale, 1e-300))
                break;
            Rotate(a, v, 0, 1); // (p, q) = (0, 1) zeroes a01
            Rotate(a, v, 0, 2);
            Rotate(a, v, 1, 2);
        }

        Span<double> values = [a[0], a[3], a[5]];
        Span<int> order = [0, 1, 2];
        // Sort 3 values descending (insertion sort on indices).
        for (int i = 1; i < 3; i++)
        {
            for (int j = i; j > 0 && values[order[j]] > values[order[j - 1]]; j--)
                (order[j], order[j - 1]) = (order[j - 1], order[j]);
        }

        var outValues = new double[3];
        var outVectors = new Vector3d[3];
        for (int i = 0; i < 3; i++)
        {
            int c = order[i];
            outValues[i] = values[c];
            outVectors[i] = new Vector3d(v[c], v[3 + c], v[6 + c]);
        }
        return (outValues, outVectors);
    }

    /// <summary>One Jacobi rotation zeroing element (p, q), p &lt; q.</summary>
    private static void Rotate(Span<double> a, Span<double> v, int p, int q)
    {
        // Map (row, col) upper-triangle indices into the packed layout.
        static int I(int r, int c) => (r, c) switch
        {
            (0, 0) => 0, (0, 1) => 1, (0, 2) => 2,
            (1, 1) => 3, (1, 2) => 4, (2, 2) => 5,
            _ => throw new InvalidOperationException(),
        };

        double apq = a[I(p, q)];
        if (apq == 0)
            return;
        double app = a[I(p, p)];
        double aqq = a[I(q, q)];

        double theta = (aqq - app) / (2 * apq);
        double t = Math.Sign(theta == 0 ? 1 : theta) /
                   (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
        double c = 1 / Math.Sqrt(t * t + 1);
        double s = t * c;

        int r = 3 - p - q; // the remaining index
        double apr = a[I(Math.Min(p, r), Math.Max(p, r))];
        double aqr = a[I(Math.Min(q, r), Math.Max(q, r))];

        a[I(p, p)] = app - t * apq;
        a[I(q, q)] = aqq + t * apq;
        a[I(p, q)] = 0;
        a[I(Math.Min(p, r), Math.Max(p, r))] = c * apr - s * aqr;
        a[I(Math.Min(q, r), Math.Max(q, r))] = s * apr + c * aqr;

        // Accumulate the rotation into the eigenvector columns p and q.
        for (int row = 0; row < 3; row++)
        {
            double vp = v[row * 3 + p];
            double vq = v[row * 3 + q];
            v[row * 3 + p] = c * vp - s * vq;
            v[row * 3 + q] = s * vp + c * vq;
        }
    }
}
