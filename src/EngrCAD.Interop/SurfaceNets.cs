using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Mesh;

namespace EngrCAD.Interop;

/// <summary>
/// Implicit → mesh conversion by Surface Nets (dual contouring without normals-based
/// vertex placement): one vertex per sign-changing cell, placed at the mean of its edge
/// crossings; one quad per interior sign-changing grid edge. Produces closed quad-dominant
/// meshes for smooth fields whose surface stays inside the sampled region; surfaces that
/// cross the region boundary come out open there.
/// </summary>
public static class SurfaceNets
{
    /// <summary>Polygonizes over the field's own bounds plus a small margin. Requires finite bounds.</summary>
    public static HalfEdgeMesh Polygonize(Sdf sdf, int resolution = 64, ProgressCancel? progress = null)
    {
        var bounds = sdf.Bounds;
        if (!Sdf.IsFinite(bounds) || bounds.IsEmpty)
            throw new ArgumentException(
                "The field has unbounded or empty bounds; pass an explicit sampling region.", nameof(sdf));
        double margin = bounds.Size[bounds.LongestAxis] / resolution * 2;
        return Polygonize(sdf, bounds.Expanded(margin), resolution, progress);
    }

    /// <summary>
    /// Polygonizes the iso-surface d = 0 over <paramref name="region"/>.
    /// <paramref name="resolution"/> is the cell count along the region's longest axis.
    /// Grid sampling is evaluated in parallel (bit-for-bit deterministic — every sample
    /// lands in its own slot); the topology passes stay sequential so output ordering
    /// never depends on scheduling. <paramref name="progress"/> adds cooperative
    /// progress/cancellation (cancellation throws <see cref="OperationCanceledException"/>).
    /// </summary>
    public static HalfEdgeMesh Polygonize(Sdf sdf, in Aabb region, int resolution, ProgressCancel? progress = null)
    {
        if (region.IsEmpty)
            throw new ArgumentException("Sampling region is empty.", nameof(region));
        if (resolution < 2 || resolution > 1024)
            throw new ArgumentOutOfRangeException(nameof(resolution));

        var size = region.Size;
        double cell = size[region.LongestAxis] / resolution;
        int nx = Math.Max(1, (int)Math.Ceiling(size.X / cell - 1e-9));
        int ny = Math.Max(1, (int)Math.Ceiling(size.Y / cell - 1e-9));
        int nz = Math.Max(1, (int)Math.Ceiling(size.Z / cell - 1e-9));
        var origin = region.Min;

        // Sample the grid corners: i-slabs are contiguous slices of the flat arrays, so
        // each parallel block fills and evaluates a disjoint range (deterministic output).
        int sy = ny + 1, sz = nz + 1;
        var values = new double[(nx + 1) * sy * sz];
        var points = new Vector3d[values.Length];
        ParallelFor.Blocks(0, nx + 1, (i0, i1) =>
        {
            if (progress is not null && progress.CancelRequested)
                return;
            for (int i = i0; i < i1; i++)
            {
                for (int j = 0; j <= ny; j++)
                {
                    for (int k = 0; k <= nz; k++)
                        points[(i * sy + j) * sz + k] = origin + (i * cell, j * cell, k * cell);
                }
            }
            int offset = i0 * sy * sz;
            int length = (i1 - i0) * sy * sz;
            sdf.Evaluate(points.AsSpan(offset, length), values.AsSpan(offset, length));
        });
        progress?.ThrowIfCancelled();
        progress?.Report(0.6);

        int Corner(int i, int j, int k) => (i * sy + j) * sz + k;
        bool Inside(int index) => values[index] < 0;

        // One vertex per connected component of inside-corners per cell (manifold surface
        // nets): a plain one-vertex-per-cell scheme produces non-manifold edges on grid
        // faces with diagonal sign patterns (thin sheets, saddles). cornerVertex maps each
        // mixed cell to an int[8] giving, per inside corner, its component's vertex.
        var cornerVertex = new Dictionary<int, int[]>();
        int CellIndex(int i, int j, int k) => (i * ny + j) * nz + k;

        var positions = new List<Vector3d>();
        Span<int> corners = stackalloc int[8];
        Span<int> stack = stackalloc int[8];
        ReadOnlySpan<(int A, int B)> edges =
        [
            (0, 1), (2, 3), (4, 5), (6, 7),
            (0, 2), (1, 3), (4, 6), (5, 7),
            (0, 4), (1, 5), (2, 6), (3, 7),
        ];

        for (int i = 0; i < nx; i++)
        {
            if (progress is not null && (i & 15) == 0)
            {
                progress.ThrowIfCancelled();
                progress.Report(0.6 + 0.3 * i / nx);
            }
            for (int j = 0; j < ny; j++)
            {
                for (int k = 0; k < nz; k++)
                {
                    corners[0] = Corner(i, j, k);
                    corners[1] = Corner(i + 1, j, k);
                    corners[2] = Corner(i, j + 1, k);
                    corners[3] = Corner(i + 1, j + 1, k);
                    corners[4] = Corner(i, j, k + 1);
                    corners[5] = Corner(i + 1, j, k + 1);
                    corners[6] = Corner(i, j + 1, k + 1);
                    corners[7] = Corner(i + 1, j + 1, k + 1);

                    int insideMask = 0;
                    for (int c = 0; c < 8; c++)
                    {
                        if (Inside(corners[c]))
                            insideMask |= 1 << c;
                    }
                    if (insideMask is 0 or 255)
                        continue;

                    var map = new int[8];
                    Array.Fill(map, -1);

                    // Flood-fill inside corners over the cube's face adjacency (bit flips).
                    int seenMask = 0;
                    for (int seed = 0; seed < 8; seed++)
                    {
                        if ((insideMask & (1 << seed)) == 0 || (seenMask & (1 << seed)) != 0)
                            continue;
                        int componentMask = 0;
                        int top = 0;
                        stack[top++] = seed;
                        seenMask |= 1 << seed;
                        while (top > 0)
                        {
                            int c = stack[--top];
                            componentMask |= 1 << c;
                            foreach (int neighbor in (ReadOnlySpan<int>)[c ^ 1, c ^ 2, c ^ 4])
                            {
                                int bit = 1 << neighbor;
                                if ((insideMask & bit) != 0 && (seenMask & bit) == 0)
                                {
                                    seenMask |= bit;
                                    stack[top++] = neighbor;
                                }
                            }
                        }

                        // Component vertex: mean of the crossings on edges leaving it.
                        var sum = Vector3d.Zero;
                        int crossings = 0;
                        foreach (var (ea, eb) in edges)
                        {
                            bool aIn = (componentMask & (1 << ea)) != 0;
                            bool bIn = (componentMask & (1 << eb)) != 0;
                            if (aIn == bIn)
                                continue;
                            int inside = aIn ? corners[ea] : corners[eb];
                            int outside = aIn ? corners[eb] : corners[ea];
                            if (Inside(outside))
                                continue; // both grid-inside: edge internal to the solid
                            double t = values[inside] / (values[inside] - values[outside]);
                            sum += Vector3d.Lerp(points[inside], points[outside], t);
                            crossings++;
                        }

                        int vertex = positions.Count;
                        positions.Add(sum / crossings);
                        for (int c = 0; c < 8; c++)
                        {
                            if ((componentMask & (1 << c)) != 0)
                                map[c] = vertex;
                        }
                    }

                    cornerVertex[CellIndex(i, j, k)] = map;
                }
            }
        }

        // One quad per interior sign-changing grid edge, wound so normals point outward.
        // Each adjacent cell contributes the vertex of the component that contains its
        // local copy of the edge's inside endpoint.
        var faces = new List<int[]>();

        void EmitQuad(
            int c0, int corner0, int c1, int corner1, int c2, int corner2, int c3, int corner3, bool flip)
        {
            if (!cornerVertex.TryGetValue(c0, out var m0) || !cornerVertex.TryGetValue(c1, out var m1) ||
                !cornerVertex.TryGetValue(c2, out var m2) || !cornerVertex.TryGetValue(c3, out var m3))
                return;
            int v0 = m0[corner0], v1 = m1[corner1], v2 = m2[corner2], v3 = m3[corner3];
            if (v0 < 0 || v1 < 0 || v2 < 0 || v3 < 0)
                return;
            faces.Add(flip ? [v3, v2, v1, v0] : [v0, v1, v2, v3]);
        }

        // X-aligned edges: quad over cells varying in (y, z); CCW in (y, z) → +X normal.
        for (int i = 0; i < nx; i++)
        {
            for (int j = 1; j < ny; j++)
            {
                for (int k = 1; k < nz; k++)
                {
                    bool insideStart = Inside(Corner(i, j, k));
                    if (insideStart == Inside(Corner(i + 1, j, k)))
                        continue;
                    int d = insideStart ? 0 : 1; // local x-bit of the inside endpoint
                    EmitQuad(
                        CellIndex(i, j - 1, k - 1), d | 2 | 4,
                        CellIndex(i, j, k - 1), d | 4,
                        CellIndex(i, j, k), d,
                        CellIndex(i, j - 1, k), d | 2,
                        flip: !insideStart);
                }
            }
        }

        // Y-aligned edges: quad over cells varying in (z, x); CCW in (z, x) → +Y normal.
        for (int j = 0; j < ny; j++)
        {
            for (int i = 1; i < nx; i++)
            {
                for (int k = 1; k < nz; k++)
                {
                    bool insideStart = Inside(Corner(i, j, k));
                    if (insideStart == Inside(Corner(i, j + 1, k)))
                        continue;
                    int d = (insideStart ? 0 : 1) << 1; // local y-bit of the inside endpoint
                    EmitQuad(
                        CellIndex(i - 1, j, k - 1), d | 1 | 4,
                        CellIndex(i - 1, j, k), d | 1,
                        CellIndex(i, j, k), d,
                        CellIndex(i, j, k - 1), d | 4,
                        flip: !insideStart);
                }
            }
        }

        // Z-aligned edges: quad over cells varying in (x, y); CCW in (x, y) → +Z normal.
        for (int k = 0; k < nz; k++)
        {
            for (int i = 1; i < nx; i++)
            {
                for (int j = 1; j < ny; j++)
                {
                    bool insideStart = Inside(Corner(i, j, k));
                    if (insideStart == Inside(Corner(i, j, k + 1)))
                        continue;
                    int d = (insideStart ? 0 : 1) << 2; // local z-bit of the inside endpoint
                    EmitQuad(
                        CellIndex(i - 1, j - 1, k), d | 1 | 2,
                        CellIndex(i, j - 1, k), d | 2,
                        CellIndex(i, j, k), d,
                        CellIndex(i - 1, j, k), d | 1,
                        flip: !insideStart);
                }
            }
        }

        progress?.ThrowIfCancelled();
        var mesh = HalfEdgeMesh.Build(positions, faces);
        progress?.Report(1);
        return mesh;
    }
}
