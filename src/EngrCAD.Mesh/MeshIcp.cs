using EngrCAD.Core;
using EngrCAD.Core.Solvers;

namespace EngrCAD.Mesh;

/// <summary>Options for <see cref="MeshIcp.Align(IReadOnlyList{Vector3d}, HalfEdgeMesh, IcpOptions?)"/>.</summary>
public sealed record IcpOptions
{
    /// <summary>Iteration cap.</summary>
    public int MaxIterations { get; init; } = 50;

    /// <summary>
    /// Convergence test, relative to the target's bounding-box diagonal (scale-free):
    /// the solve converges when either the point-to-plane RMS residual or the incremental
    /// step's largest point displacement drops below tolerance × diagonal.
    /// </summary>
    public double ConvergenceTolerance { get; init; } = 1e-9;

    /// <summary>
    /// Correspondences farther than this are rejected as outliers (absolute model
    /// units). Null accepts every closest point — right when the source is known to lie
    /// on the target; set it when the source only partially overlaps.
    /// </summary>
    public double? MaxCorrespondenceDistance { get; init; }
}

/// <summary>
/// Result of an ICP alignment. <see cref="Transform"/> is a rigid transform mapping the
/// source (points or mesh) into the target's frame; on non-convergence it is the best
/// transform reached, and <see cref="Converged"/> says so honestly.
/// </summary>
public sealed record IcpResult(Matrix4d Transform, int Iterations, double RmsError, bool Converged, int Correspondences);

/// <summary>
/// Point-to-plane iterative closest point (Besl–McKay iteration with Chen–Medioni's
/// point-to-plane metric): aligns a point set (or a mesh's vertices) to a target mesh.
/// Correspondences come from <see cref="MeshProjectionTarget"/> (BVH closest point +
/// winning-triangle normal), and each iteration solves the small-angle linearized 6×6
/// normal equations — assembled about the correspondence centroid for conditioning —
/// through the Solvers library's <see cref="SparseCholesky"/>.
/// </summary>
/// <remarks>
/// <para>
/// Point-to-plane rather than point-to-point, deliberately: the point-to-point update
/// zig-zags along surfaces (tangential sliding is penalized even when the surfaces
/// already touch), while the plane metric lets correspondences slide and converges in a
/// handful of iterations on smooth geometry.
/// </para>
/// <para>
/// <b>It refuses loudly rather than regularizing</b> (the <c>MateSolver</c> convention):
/// correspondence geometry that under-constrains the pose — all points on one plane
/// leaves two translations and a rotation free — makes the 6×6 singular, and the result
/// reports <c>Converged = false</c> instead of a Tikhonov-damped answer that silently
/// picks one of the infinitely many minima. Degenerate (zero-normal) target triangles
/// are skipped per correspondence.
/// </para>
/// <para>Deterministic: fixed iteration order, no randomness, sequential accumulation.</para>
/// </remarks>
public static class MeshIcp
{
    /// <summary>Aligns <paramref name="sourcePoints"/> to <paramref name="target"/>.</summary>
    public static IcpResult Align(IReadOnlyList<Vector3d> sourcePoints, HalfEdgeMesh target, IcpOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sourcePoints);
        ArgumentNullException.ThrowIfNull(target);
        if (sourcePoints.Count == 0)
            throw new ArgumentException("ICP needs at least one source point.", nameof(sourcePoints));
        options ??= new IcpOptions();
        if (options.MaxIterations < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxIterations must be at least 1.");

        var projection = new MeshProjectionTarget(target);
        double scale = target.ComputeBounds().Size.Length; // bounding diagonal
        if (scale <= 0)
            scale = 1;
        double threshold = options.ConvergenceTolerance * scale;

        var transform = Matrix4d.Identity;
        bool converged = false;
        int iterations = 0;
        int correspondences = 0;

        var transformed = new Vector3d[sourcePoints.Count];
        var rows = new (Vector3d A, Vector3d N, double R)[sourcePoints.Count];

        while (iterations < options.MaxIterations)
        {
            iterations++;
            for (int i = 0; i < sourcePoints.Count; i++)
                transformed[i] = transform.TransformPoint(sourcePoints[i]);

            // Correspondences: closest target point + oriented normal, outliers dropped.
            correspondences = 0;
            var centroid = Vector3d.Zero;
            double residualSquaredSum = 0;
            foreach (var p in transformed)
            {
                var q = projection.Project(p, out var normal);
                if (normal == Vector3d.Zero)
                    continue; // unoriented (degenerate) winner
                if (options.MaxCorrespondenceDistance is { } cutoff && (p - q).Length > cutoff)
                    continue;
                double residual = (p - q).Dot(normal);
                rows[correspondences++] = (p, normal, residual);
                centroid += p;
                residualSquaredSum += residual * residual;
            }
            if (correspondences < 6)
                break; // fewer rows than degrees of freedom cannot pin a pose
            centroid /= correspondences;

            double rms = Math.Sqrt(residualSquaredSum / correspondences);
            if (rms <= threshold)
            {
                converged = true;
                break;
            }

            // 6×6 normal equations about the centroid: unknowns (ω, t), row (a, n) with
            // a = (p − c) × n, residual r; A += J·Jᵀ, b += −r·J.
            var builder = new SparseMatrixBuilder(6, 6);
            var b = new double[6];
            double radius = 0;
            for (int i = 0; i < correspondences; i++)
            {
                var (p, n, r) = rows[i];
                var d = p - centroid;
                radius = Math.Max(radius, d.Length);
                var a = d.Cross(n);
                Span<double> j = [a.X, a.Y, a.Z, n.X, n.Y, n.Z];
                for (int row = 0; row < 6; row++)
                {
                    for (int col = row; col < 6; col++)
                        builder.Add(row, col, j[row] * j[col]);
                    b[row] -= r * j[row];
                }
            }

            double[] x;
            try
            {
                x = SparseCholesky.Factorize(builder.ToSymmetricUpper()).Solve(b);
            }
            catch (InvalidOperationException)
            {
                // Singular normal equations: the correspondence geometry leaves the pose
                // under-constrained (e.g. everything on one plane). Refuse rather than
                // regularize toward an arbitrary minimum.
                converged = false;
                break;
            }

            var omega = new Vector3d(x[0], x[1], x[2]);
            var translation = new Vector3d(x[3], x[4], x[5]);
            double angle = omega.Length;
            var rotation = angle > 0
                ? Matrix4d.CreateFromAxisAngle(omega / angle, angle)
                : Matrix4d.Identity;
            // p' = R·(p − c) + c + t.
            var increment =
                Matrix4d.CreateTranslation(centroid + translation) * rotation * Matrix4d.CreateTranslation(-centroid);
            transform = increment * transform;

            // Largest displacement the increment can cause across the correspondence set.
            double step = translation.Length + angle * radius;
            if (step <= threshold)
            {
                converged = true;
                iterations++; // the final pose differs from the one the RMS was measured at
                break;
            }
        }

        // Report the residual of the FINAL pose (the loop's rms lags one increment).
        double finalRms = FinalRms(sourcePoints, projection, transform, options, out int finalCount);
        return new IcpResult(transform, iterations, finalRms, converged, finalCount > 0 ? finalCount : correspondences);
    }

    /// <summary>Aligns <paramref name="source"/>'s vertices to <paramref name="target"/>.</summary>
    public static IcpResult Align(HalfEdgeMesh source, HalfEdgeMesh target, IcpOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var points = new Vector3d[source.VertexCount];
        for (int v = 0; v < points.Length; v++)
            points[v] = source.GetPosition(v);
        return Align(points, target, options);
    }

    private static double FinalRms(
        IReadOnlyList<Vector3d> sourcePoints, MeshProjectionTarget projection, in Matrix4d transform,
        IcpOptions options, out int count)
    {
        double sum = 0;
        count = 0;
        foreach (var point in sourcePoints)
        {
            var p = transform.TransformPoint(point);
            var q = projection.Project(p, out var normal);
            if (normal == Vector3d.Zero)
                continue;
            if (options.MaxCorrespondenceDistance is { } cutoff && (p - q).Length > cutoff)
                continue;
            double r = (p - q).Dot(normal);
            sum += r * r;
            count++;
        }
        return count > 0 ? Math.Sqrt(sum / count) : double.PositiveInfinity;
    }
}
