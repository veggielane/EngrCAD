using EngrCAD.Core;
using EngrCAD.Core.Solvers;

namespace EngrCAD.Mesh;

/// <summary>
/// Handle-based Laplacian deformation (classic Laplacian surface editing, g3's
/// <c>LaplacianMeshDeformer</c> role): minimizes
/// ‖L·(x − x₀)‖² + Σ w²·‖x_h − target_h‖² — the bi-Laplacian bending energy that keeps
/// the surface's differential coordinates, plus <b>soft</b> constraints as weighted
/// rows — with the boundary (and any <see cref="PinVertex"/>ed vertices) substituted as
/// <b>hard</b> constraints. Solved per coordinate by <see cref="SparseCholesky"/> over
/// the normal equations (L² + W)·x = L²·x₀ + W·c.
/// </summary>
/// <remarks>
/// <para>
/// Soft handles are the point of the formulation: a hard handle transmits its position
/// C⁰ into the surface (a cone at the handle), while a weighted row lets the bending
/// energy round the transition — raise the weight when the handle must be interpolated,
/// lower it when it is a suggestion. Weights are dimensionless (the cotangent Laplacian
/// is), so the trade-off is scale-free; the extremes behave as expected and are pinned
/// by tests: w → ∞ approaches interpolation, w → 0 approaches no constraint.
/// </para>
/// <para>
/// Pinned vertices (boundary + explicit pins) keep their positions <b>bit-identically</b>
/// — they are substituted, never solved for — which is what lets a deformed region weld
/// back into its base mesh by exact equality (<see cref="DeformRegion"/>).
/// Deterministic: mesh-order assembly, deterministic solver.
/// </para>
/// </remarks>
public sealed class LaplacianMeshDeformer
{
    private readonly HalfEdgeMesh _mesh;
    private readonly LaplacianWeighting _weighting;
    private readonly Dictionary<int, (Vector3d Target, double Weight)> _handles = [];
    private readonly HashSet<int> _pins = [];

    /// <summary>Default soft-constraint weight — strong enough to track a handle closely, soft enough to bend into it.</summary>
    public const double DefaultHandleWeight = 10.0;

    public LaplacianMeshDeformer(HalfEdgeMesh mesh, LaplacianWeighting weighting = LaplacianWeighting.Cotangent)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        _mesh = mesh;
        _weighting = weighting;
    }

    /// <summary>
    /// Sets (or replaces) a soft positional constraint pulling <paramref name="vertex"/>
    /// toward <paramref name="target"/> with the given weight (&gt; 0).
    /// </summary>
    public void SetHandle(int vertex, in Vector3d target, double weight = DefaultHandleWeight)
    {
        if ((uint)vertex >= (uint)_mesh.VertexCount)
            throw new ArgumentOutOfRangeException(nameof(vertex));
        if (weight <= 0)
            throw new ArgumentOutOfRangeException(nameof(weight), "Handle weight must be positive.");
        _handles[vertex] = (target, weight);
    }

    /// <summary>Removes the handle on <paramref name="vertex"/>, if any.</summary>
    public void ClearHandle(int vertex) => _handles.Remove(vertex);

    /// <summary>
    /// Hard-pins <paramref name="vertex"/> at its current position (bit-identical in the
    /// output). The mesh boundary is always pinned; this adds interior pins.
    /// </summary>
    public void PinVertex(int vertex)
    {
        if ((uint)vertex >= (uint)_mesh.VertexCount)
            throw new ArgumentOutOfRangeException(nameof(vertex));
        _pins.Add(vertex);
    }

    /// <summary>
    /// Solves the deformation and returns a new mesh with the same topology. With no
    /// handles the energy's minimizer is the input itself, so the input mesh is returned
    /// (safe: <see cref="HalfEdgeMesh"/> is immutable).
    /// </summary>
    public HalfEdgeMesh Solve()
    {
        if (_handles.Count == 0)
            return _mesh;

        int n = _mesh.VertexCount;
        var pinned = new bool[n];
        for (int v = 0; v < n; v++)
            pinned[v] = _mesh.GetVertex(v).IsBoundary;
        foreach (int v in _pins)
            pinned[v] = true;
        foreach (int v in _handles.Keys)
        {
            if (pinned[v])
                throw new InvalidOperationException(
                    $"Vertex {v} is hard-pinned (boundary or PinVertex) and cannot also carry a soft handle.");
        }

        var positions = new Vector3d[n];
        for (int v = 0; v < n; v++)
            positions[v] = _mesh.GetPosition(v);

        var triangulated = _mesh.Triangulated(); // vertex indices preserved
        var laplacian = MeshLaplacian.Build(triangulated, positions, _weighting);

        // L as a general CSR matrix, then S = L·L (L is symmetric, so this is LᵀL).
        var lBuilder = new SparseMatrixBuilder(n, n);
        for (int v = 0; v < n; v++)
            lBuilder.Add(v, v, laplacian.WeightSums[v]);
        for (int e = 0; e < laplacian.EdgeA.Length; e++)
        {
            int a = laplacian.EdgeA[e], b = laplacian.EdgeB[e];
            double w = laplacian.Weights[e];
            lBuilder.Add(a, b, -w);
            lBuilder.Add(b, a, -w);
        }
        var l = lBuilder.ToMatrix();
        var s = l.Multiply(l);

        // Restrict to unknowns; pinned columns substitute into the right-hand side.
        var unknownOf = new int[n];
        int unknowns = 0;
        for (int v = 0; v < n; v++)
            unknownOf[v] = pinned[v] ? -1 : unknowns++;
        if (unknowns == 0)
            return _mesh;

        var builder = new SparseMatrixBuilder(unknowns, unknowns);
        var rhs = new Vector3d[unknowns];
        for (int r = 0; r < n; r++)
        {
            int ur = unknownOf[r];
            if (ur < 0)
                continue;
            var cols = s.RowColumns(r);
            var vals = s.RowValues(r);
            for (int p = 0; p < cols.Length; p++)
            {
                int c = cols[p];
                double v = vals[p];
                int uc = unknownOf[c];
                if (uc >= 0)
                {
                    // b = S·x₀ restricted to the unknown block (the pinned-column terms of
                    // S·x₀ cancel against the substituted A_uk·x_k exactly, so only the
                    // unknown columns remain — see the derivation in the class remarks).
                    rhs[ur] += positions[c] * v;
                    if (uc >= ur)
                        builder.Add(ur, uc, v);
                }
                // Pinned column: its A_uk·x_k term equals its S·x₀ term (x_k = x₀_k), so
                // both sides drop it — nothing to add.
            }
        }
        foreach (var (vertex, (target, weight)) in _handles)
        {
            int u = unknownOf[vertex];
            double w2 = weight * weight;
            builder.Add(u, u, w2);
            rhs[u] += target * w2;
        }

        var chol = SparseCholesky.Factorize(builder.ToSymmetricUpper());
        var result = new Vector3d[n];
        positions.CopyTo(result, 0);

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

        var (_, faces) = _mesh.ToIndexed();
        return HalfEdgeMesh.Build(result, faces);
    }

    /// <summary>
    /// Region-of-interest deformation: extracts <paramref name="region"/> via
    /// <see cref="MeshRegionOperator"/>, deforms the submesh with its rim pinned (the
    /// extracted patch's boundary IS the rim, and the deformer always pins boundaries
    /// bit-identically, which is exactly the seam contract reinsertion enforces), and
    /// reinserts. The rest of the mesh is untouched. Handle vertices are
    /// <b>base-mesh</b> indices and must lie strictly inside the region.
    /// </summary>
    public static HalfEdgeMesh DeformRegion(
        HalfEdgeMesh mesh,
        MeshFaceSelection region,
        IEnumerable<(int Vertex, Vector3d Target, double Weight)> handles,
        LaplacianWeighting weighting = LaplacianWeighting.Cotangent)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(handles);

        var op = MeshRegionOperator.Extract(mesh, region);
        var baseToRegion = new Dictionary<int, int>();
        for (int r = 0; r < op.RegionToBaseVertex.Count; r++)
            baseToRegion[op.RegionToBaseVertex[r]] = r;

        var deformer = new LaplacianMeshDeformer(op.Region, weighting);
        foreach (var (vertex, target, weight) in handles)
        {
            if (!baseToRegion.TryGetValue(vertex, out int regionVertex))
                throw new ArgumentException($"Handle vertex {vertex} is not inside the region.", nameof(handles));
            deformer.SetHandle(regionVertex, target, weight);
        }

        return op.Reinsert(deformer.Solve()).Base;
    }
}
