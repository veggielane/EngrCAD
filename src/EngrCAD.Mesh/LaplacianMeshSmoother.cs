using EngrCAD.Core;
using EngrCAD.Core.Solvers;

namespace EngrCAD.Mesh;

/// <summary>Edge-weight scheme for the Laplacian mesh tools.</summary>
public enum LaplacianWeighting
{
    /// <summary>
    /// Cotangent weights (Meyer et al. / the Botsch–Sorkine survey's discretization):
    /// the geometry-aware Laplace–Beltrami approximation, invariant to tessellation
    /// density, with the uniform fallback below for degenerate triangles.
    /// </summary>
    Cotangent,

    /// <summary>Uniform (combinatorial) weights: every edge counts 1. Cruder, but immune to bad triangles.</summary>
    Uniform,
}

/// <summary>Options for <see cref="LaplacianMeshSmoother.Smooth"/>.</summary>
public sealed record LaplacianSmoothOptions
{
    /// <summary>
    /// Diffusion step size, <b>dimensionless</b>: the solved system is
    /// (M + λ·L)·x' = M·x with λ = TimeStep · h̄² for mean edge length h̄, so the same
    /// value smooths the same amount at 1e-5 scale and at metre scale (the repo's
    /// scale-free rule). 1 is a visible fairing step; large values approach the
    /// membrane spanned by the pinned boundary.
    /// </summary>
    public double TimeStep { get; init; } = 1.0;

    /// <summary>
    /// Number of implicit steps. The operator is rebuilt from the current geometry each
    /// step (honest curvature flow), so k steps of λ are not one step of k·λ.
    /// </summary>
    public int Iterations { get; init; } = 1;

    /// <summary>Edge weighting scheme.</summary>
    public LaplacianWeighting Weighting { get; init; } = LaplacianWeighting.Cotangent;

    /// <summary>
    /// Extra vertices to pin at their input positions (bit-identical), in addition to
    /// the boundary, which is always pinned.
    /// </summary>
    public IReadOnlyCollection<int>? FixedVertices { get; init; }
}

/// <summary>
/// Global implicit Laplacian smoothing: solves (M + λL)·x' = M·x per coordinate —
/// Desbrun-style implicit fairing over the cotangent (or uniform) Laplacian, boundary
/// pinned, backed by <see cref="SparseCholesky"/>. One linear solve smooths globally and
/// unconditionally stably, where an explicit scheme needs many small damped passes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not the remesher's smoothing pass.</b> <see cref="Remesher"/> runs a local
/// double-buffered explicit relaxation *between topology operations*, per pass, with
/// projection pulling vertices back to a target — its job is equalizing triangle shape
/// while the shape is preserved by projection. This class is the complement: geometry
/// changes ARE the point (fairing, denoising), topology never changes, and the implicit
/// solve reaches equilibrium against the pinned boundary in one step instead of
/// diffusing one ring per pass.
/// </para>
/// <para>
/// Deterministic: assembly order is the mesh's own edge order and the solver is
/// deterministic, so identical inputs give bit-identical output. Pinned vertices keep
/// their positions bit-identically (they are copied, never solved for) — which is what
/// lets a smoothed patch weld back into a surrounding mesh by exact equality.
/// </para>
/// </remarks>
public static class LaplacianMeshSmoother
{
    /// <summary>
    /// Smooths <paramref name="mesh"/> and returns a new mesh with the same topology.
    /// Boundary vertices (and <see cref="LaplacianSmoothOptions.FixedVertices"/>) keep
    /// their positions bit-identically.
    /// </summary>
    public static HalfEdgeMesh Smooth(HalfEdgeMesh mesh, LaplacianSmoothOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        options ??= new LaplacianSmoothOptions();
        if (options.TimeStep <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "TimeStep must be positive.");
        if (options.Iterations < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Iterations must be at least 1.");

        int n = mesh.VertexCount;
        var pinned = new bool[n];
        for (int v = 0; v < n; v++)
            pinned[v] = mesh.GetVertex(v).IsBoundary;
        if (options.FixedVertices is not null)
        {
            foreach (int v in options.FixedVertices)
            {
                if ((uint)v >= (uint)n)
                    throw new ArgumentOutOfRangeException(nameof(options), $"Fixed vertex {v} is out of range.");
                pinned[v] = true;
            }
        }

        var positions = new Vector3d[n];
        for (int v = 0; v < n; v++)
            positions[v] = mesh.GetPosition(v);

        var triangulated = mesh.Triangulated(); // vertex indices are preserved
        for (int step = 0; step < options.Iterations; step++)
            positions = SmoothStep(triangulated, positions, pinned, options);

        var (_, faces) = mesh.ToIndexed();
        return HalfEdgeMesh.Build(positions, faces);
    }

    private static Vector3d[] SmoothStep(
        HalfEdgeMesh triangulated, Vector3d[] positions, bool[] pinned, LaplacianSmoothOptions options)
    {
        int n = positions.Length;
        var laplacian = MeshLaplacian.Build(triangulated, positions, options.Weighting);
        double lambda = options.TimeStep * laplacian.MeanEdgeLength * laplacian.MeanEdgeLength;

        // Unknown numbering: solve only the free vertices; pinned columns move to the RHS.
        var unknownOf = new int[n];
        int unknowns = 0;
        for (int v = 0; v < n; v++)
            unknownOf[v] = pinned[v] ? -1 : unknowns++;
        if (unknowns == 0)
            return positions; // everything pinned — nothing to smooth

        var builder = new SparseMatrixBuilder(unknowns, unknowns);
        var rhs = new Vector3d[unknowns];

        // Diagonal: mass + λ·(weight sum). Off-diagonal: -λ·w per edge.
        for (int v = 0; v < n; v++)
        {
            int uv = unknownOf[v];
            if (uv < 0)
                continue;
            builder.Add(uv, uv, laplacian.VertexAreas[v] + lambda * laplacian.WeightSums[v]);
            rhs[uv] = positions[v] * laplacian.VertexAreas[v];
        }
        for (int e = 0; e < laplacian.EdgeA.Length; e++)
        {
            int a = laplacian.EdgeA[e], b = laplacian.EdgeB[e];
            double coefficient = -lambda * laplacian.Weights[e];
            int ua = unknownOf[a], ub = unknownOf[b];
            if (ua >= 0 && ub >= 0)
            {
                if (ua <= ub)
                    builder.Add(ua, ub, coefficient);
                else
                    builder.Add(ub, ua, coefficient);
            }
            else if (ua >= 0)
            {
                rhs[ua] -= positions[b] * coefficient;
            }
            else if (ub >= 0)
            {
                rhs[ub] -= positions[a] * coefficient;
            }
        }

        var chol = SparseCholesky.Factorize(builder.ToSymmetricUpper());
        var result = new Vector3d[n];
        positions.CopyTo(result, 0); // pinned vertices verbatim

        var b3 = new double[unknowns];
        var solvedX = new double[unknowns];
        var solvedY = new double[unknowns];
        var solvedZ = new double[unknowns];
        for (int axis = 0; axis < 3; axis++)
        {
            for (int u = 0; u < unknowns; u++)
                b3[u] = rhs[u][axis];
            chol.Solve(b3, axis == 0 ? solvedX : axis == 1 ? solvedY : solvedZ);
        }
        for (int v = 0; v < n; v++)
        {
            int u = unknownOf[v];
            if (u >= 0)
                result[v] = new Vector3d(solvedX[u], solvedY[u], solvedZ[u]);
        }
        return result;
    }
}

/// <summary>
/// Shared assembly of the symmetric edge-weight Laplacian both Laplacian tools consume:
/// per-undirected-edge weights, per-vertex weight sums, and lumped (barycentric)
/// vertex areas. L is implied as L_ii = Σw, L_ij = -w_ij — consumers assemble it into
/// their own systems rather than materializing it, because each restricts to a
/// different unknown set.
/// </summary>
internal sealed class MeshLaplacian
{
    /// <summary>Lower vertex index per undirected edge.</summary>
    public required int[] EdgeA { get; init; }

    /// <summary>Higher vertex index per undirected edge.</summary>
    public required int[] EdgeB { get; init; }

    /// <summary>Symmetric edge weight per undirected edge (≥ 0).</summary>
    public required double[] Weights { get; init; }

    /// <summary>Per-vertex sum of incident edge weights (the Laplacian diagonal).</summary>
    public required double[] WeightSums { get; init; }

    /// <summary>Lumped barycentric vertex areas (⅓ of incident triangle areas; strictly positive — see Build).</summary>
    public required double[] VertexAreas { get; init; }

    /// <summary>Mean edge length — the scale reference for dimensionless step sizes.</summary>
    public required double MeanEdgeLength { get; init; }

    /// <summary>
    /// Builds the weights over <paramref name="triangulated"/> (must be all triangles)
    /// using <paramref name="positions"/> — passed separately so an iterated smoother can
    /// re-weight moved geometry without rebuilding topology.
    /// </summary>
    /// <remarks>
    /// Robustness rules, in the repo's scale-free style:
    /// <list type="bullet">
    /// <item>A cotangent contribution from a triangle whose height is below
    /// 1e-13 × its longest edge (the sliver measure <c>PolygonTriangulator</c> and the
    /// tessellation audit share) is meaningless — the whole edge falls back to the
    /// uniform weight 1, the task the fallback exists for.</item>
    /// <item>A negative cotangent <i>sum</i> (obtuse-dominated edge) is clamped to 0:
    /// keeping it would make L indefinite and the solve dishonest; clamping merely stops
    /// diffusion across that edge. The clamp reads the sign only — no epsilon.</item>
    /// <item>A vertex with no positive-area incident triangle gets the mean vertex area,
    /// so the mass matrix stays positive definite.</item>
    /// </list>
    /// </remarks>
    public static MeshLaplacian Build(HalfEdgeMesh triangulated, Vector3d[] positions, LaplacianWeighting weighting)
    {
        int n = positions.Length;
        int heCount = triangulated.HalfEdgeCount;

        // Undirected edges in canonical (lower half-edge index) order — the mesh's own
        // deterministic edge order.
        var edgeA = new List<int>();
        var edgeB = new List<int>();
        var weights = new List<double>();
        double edgeLengthSum = 0;
        int edgeCount = 0;

        for (int he = 0; he < heCount; he++)
        {
            var h = triangulated.GetHalfEdge(he);
            if (h.Twin.Index < he)
                continue; // canonical representative only
            int i = h.Origin.Index;
            int j = h.Destination.Index;
            edgeLengthSum += (positions[j] - positions[i]).Length;
            edgeCount++;

            double w;
            if (weighting == LaplacianWeighting.Uniform)
            {
                w = 1.0;
            }
            else
            {
                w = 0;
                bool degenerate = false;
                AccumulateCot(h, positions, ref w, ref degenerate);
                AccumulateCot(h.Twin, positions, ref w, ref degenerate);
                if (degenerate)
                    w = 1.0; // uniform fallback for degenerate cotangents
                else if (w < 0)
                    w = 0; // sign clamp: keeps L positive semi-definite
            }

            edgeA.Add(Math.Min(i, j));
            edgeB.Add(Math.Max(i, j));
            weights.Add(w);
        }

        var weightSums = new double[n];
        for (int e = 0; e < weights.Count; e++)
        {
            weightSums[edgeA[e]] += weights[e];
            weightSums[edgeB[e]] += weights[e];
        }

        // Lumped barycentric areas.
        var areas = new double[n];
        double totalArea = 0;
        for (int f = 0; f < triangulated.FaceCount; f++)
        {
            var face = triangulated.GetFace(f);
            double area = face.Area * (1.0 / 3.0);
            totalArea += face.Area;
            foreach (var v in face.Vertices())
                areas[v.Index] += area;
        }
        double meanArea = n > 0 ? totalArea / n : 1.0;
        for (int v = 0; v < n; v++)
        {
            // Sign test: a vertex whose ring has zero total area contributes no mass;
            // give it the mean so M stays positive definite.
            if (areas[v] <= 0)
                areas[v] = meanArea > 0 ? meanArea : 1.0;
        }

        return new MeshLaplacian
        {
            EdgeA = [.. edgeA],
            EdgeB = [.. edgeB],
            Weights = [.. weights],
            WeightSums = weightSums,
            VertexAreas = areas,
            MeanEdgeLength = edgeCount > 0 ? edgeLengthSum / edgeCount : 1.0,
        };
    }

    /// <summary>Adds ½·cot(apex angle) of the triangle on <paramref name="side"/>'s face, or flags degeneracy.</summary>
    private static void AccumulateCot(HalfEdge side, Vector3d[] positions, ref double w, ref bool degenerate)
    {
        if (side.IsBoundary)
            return;
        int i = side.Origin.Index;
        int j = side.Destination.Index;
        int k = side.Next.Destination.Index; // apex of the triangle
        var u = positions[i] - positions[k];
        var v = positions[j] - positions[k];
        var cross = u.Cross(v);
        double crossLength = cross.Length; // 2 × area
        double longestSquared = Math.Max(u.LengthSquared, Math.Max(v.LengthSquared, (u - v).LengthSquared));
        // Scale-free sliver test: triangle height ≤ 1e-13 × longest edge
        // ⇔ 2·area ≤ 1e-13 × longestEdge² (the tier PolygonTriangulator uses).
        if (crossLength <= 1e-13 * longestSquared)
        {
            degenerate = true;
            return;
        }
        w += 0.5 * u.Dot(v) / crossLength;
    }
}
