namespace EngrCAD.Core;

/// <summary>
/// Eigen-decomposition of a symmetric 3×3 matrix by cyclic Jacobi rotations —
/// unconditionally convergent, accurate for the (positive semi-definite) covariance and
/// inertia matrices this codebase feeds it. Public with <b>both</b> orderings because
/// its two historical consumers disagreed: the fitting code wants dominant-first
/// (descending), the principal-inertia convention is ascending — and the Mesh project
/// once carried a near-verbatim internal copy just to re-sort, which this type deletes.
/// </summary>
public static class SymmetricEigen3
{
    /// <summary>
    /// Eigenvalues sorted descending with matching orthonormal eigenvectors for the
    /// symmetric matrix [[xx, xy, xz], [xy, yy, yz], [xz, yz, zz]].
    /// </summary>
    public static (double[] Values, Vector3d[] Vectors) SolveDescending(
        double xx, double xy, double xz, double yy, double yz, double zz) =>
        Solve(xx, xy, xz, yy, yz, zz, ascending: false);

    /// <summary>
    /// Eigenvalues sorted ascending with matching orthonormal eigenvectors — the
    /// principal-inertia convention.
    /// </summary>
    public static (double[] Values, Vector3d[] Vectors) SolveAscending(
        double xx, double xy, double xz, double yy, double yz, double zz) =>
        Solve(xx, xy, xz, yy, yz, zz, ascending: true);

    private static (double[] Values, Vector3d[] Vectors) Solve(
        double xx, double xy, double xz, double yy, double yz, double zz, bool ascending)
    {
        // a = working copy (upper triangle), v = accumulated rotations (columns are
        // eigenvectors).
        Span<double> a = [xx, xy, xz, yy, yz, zz]; // a00 a01 a02 a11 a12 a22
        Span<double> v = [1, 0, 0, 0, 1, 0, 0, 0, 1]; // row-major identity

        for (int sweep = 0; sweep < 50; sweep++)
        {
            double off = a[1] * a[1] + a[2] * a[2] + a[4] * a[4];
            double scale = a[0] * a[0] + a[3] * a[3] + a[5] * a[5];
            // Jacobi convergence: relative ~machine-epsilon² threshold on squared entries —
            // an algorithmic stop condition, not a geometric tolerance.
            if (off <= 1e-30 * Math.Max(scale, 1e-300))
                break;
            Rotate(a, v, 0, 1); // (p, q) = (0, 1) zeroes a01
            Rotate(a, v, 0, 2);
            Rotate(a, v, 1, 2);
        }

        Span<double> values = [a[0], a[3], a[5]];
        Span<int> order = [0, 1, 2];
        // Sort 3 values (insertion sort on indices) in the requested direction.
        for (int i = 1; i < 3; i++)
        {
            for (int j = i; j > 0 && (ascending
                     ? values[order[j]] < values[order[j - 1]]
                     : values[order[j]] > values[order[j - 1]]); j--)
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
        // Exact-zero guard: the rotation is exactly the identity (and theta below would
        // divide by zero) only when the off-diagonal entry is bit-zero — deliberate ==.
        if (apq == 0)
            return;
        double app = a[I(p, p)];
        double aqq = a[I(q, q)];

        double theta = (aqq - app) / (2 * apq);
        // theta == 0 is the exact equal-diagonal case where Math.Sign(0) = 0 would zero
        // the rotation; treat sign(0) as +1 (standard Jacobi convention) — deliberate ==.
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
