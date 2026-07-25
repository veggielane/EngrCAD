using System.Buffers;
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
    /// <summary>
    /// Grid samples held in memory at once, as a count of x-slabs sized to this budget
    /// (8 Mi doubles = 64 MB). The sampler walks the grid in a sliding window of whole
    /// slabs rather than materializing all (nx+1)(ny+1)(nz+1) of them, so peak memory
    /// scales with the grid's <em>cross-section</em>, not its volume: a 1024³ grid needs
    /// 16 MB of samples instead of 8.6 GB. When the whole grid fits inside the budget
    /// (about resolution 200 and below) the window IS the whole grid and the sampler
    /// behaves exactly as a dense one — same single parallel pass, same values.
    /// </summary>
    private const int WindowSampleBudget = 8 << 20;

    /// <summary>
    /// Sample positions staged per batch call. Matches the implicit engine's own batch
    /// chunk so the coordinate scratch (24 KB) and the operator temporaries below it stay
    /// cache resident; the value is not load bearing for correctness.
    /// </summary>
    private const int SampleChunk = 1024;

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
    /// <para>
    /// Sampling feeds the field <em>deinterleaved</em> coordinates
    /// (<see cref="Sdf.Evaluate(ReadOnlySpan{double}, ReadOnlySpan{double},
    /// ReadOnlySpan{double}, Span{double})"/>) generated on the fly from the grid indices,
    /// in a sliding window of x-slabs — no <c>Vector3d</c> corner array is ever built, so
    /// the grid costs 8 bytes per live sample instead of 32 and peak memory is bounded by
    /// the slab window rather than the grid volume. Slabs are sampled in parallel
    /// (bit-for-bit deterministic — every sample lands in its own slot); the topology
    /// passes stay sequential and quads are emitted into per-axis buckets, so the output
    /// mesh's vertex and face ordering never depends on scheduling or on the window size.
    /// </para>
    /// <paramref name="progress"/> adds cooperative progress/cancellation (cancellation
    /// throws <see cref="OperationCanceledException"/>).
    /// </summary>
    public static HalfEdgeMesh Polygonize(Sdf sdf, in Aabb region, int resolution, ProgressCancel? progress = null) =>
        Polygonize(sdf, region, resolution, progress, WindowSampleBudget);

    /// <summary>
    /// The implementation, with the slab window's sample budget exposed so tests can force
    /// streaming on a small grid and assert the output is bit-for-bit window-independent.
    /// </summary>
    internal static HalfEdgeMesh Polygonize(
        Sdf sdf, in Aabb region, int resolution, ProgressCancel? progress, int windowSampleBudget)
    {
        if (region.IsEmpty)
            throw new ArgumentException("Sampling region is empty.", nameof(region));
        if (resolution < 2 || resolution > 1024)
            throw new ArgumentOutOfRangeException(nameof(resolution));

        var size = region.Size;
        double cell = size[region.LongestAxis] / resolution;
        // Epsilon-guard the Ceiling: an exact multiple of the cell size computed through
        // different arithmetic can land an ulp high and must not gain a cell.
        var cells = new Vector3i(
            Math.Max(1, (int)Math.Ceiling(size.X / cell - 1e-9)),
            Math.Max(1, (int)Math.Ceiling(size.Y / cell - 1e-9)),
            Math.Max(1, (int)Math.Ceiling(size.Z / cell - 1e-9)));
        int nx = cells.X, ny = cells.Y, nz = cells.Z; // scalar copies for the hot loops
        var origin = region.Min;

        int sy = ny + 1, sz = nz + 1;
        int slabSamples = sy * sz;
        // Whole slabs held at once; at least two (a cell spans slabs i and i+1).
        int window = Math.Clamp(windowSampleBudget / slabSamples, 2, nx + 1);
        var values = new double[window * slabSamples];

        int baseSlab = 0; // global x index of the slab stored at local offset 0

        void SampleSlabs(int from, int to)
        {
            int rows = (to - from) * sy;
            int localOrigin = (from - baseSlab) * slabSamples;
            ParallelFor.Blocks(0, rows, (r0, r1) =>
            {
                if (progress is not null && progress.CancelRequested)
                    return;
                var rented = ArrayPool<double>.Shared.Rent(SampleChunk * 3);
                try
                {
                    var xs = rented.AsSpan(0, SampleChunk);
                    var ys = rented.AsSpan(SampleChunk, SampleChunk);
                    var zs = rented.AsSpan(SampleChunk * 2, SampleChunk);
                    int filled = 0;
                    int flush = localOrigin + r0 * sz;
                    for (int r = r0; r < r1; r++)
                    {
                        // Coordinates are recomputed from the indices with the same
                        // expression the dense sampler used, so values are unchanged.
                        double px = origin.X + (from + r / sy) * cell;
                        double py = origin.Y + r % sy * cell;
                        for (int k = 0; k < sz; k++)
                        {
                            xs[filled] = px;
                            ys[filled] = py;
                            zs[filled] = origin.Z + k * cell;
                            if (++filled != SampleChunk)
                                continue;
                            sdf.Evaluate(xs, ys, zs, values.AsSpan(flush, SampleChunk));
                            flush += SampleChunk;
                            filled = 0;
                        }
                    }
                    if (filled > 0)
                        sdf.Evaluate(xs[..filled], ys[..filled], zs[..filled], values.AsSpan(flush, filled));
                }
                finally
                {
                    ArrayPool<double>.Shared.Return(rented);
                }
            }, minBlockSize: Math.Max(1, SampleChunk / sz));
        }

        // One vertex per connected component of inside-corners per cell (manifold surface
        // nets): a plain one-vertex-per-cell scheme produces non-manifold edges on grid
        // faces with diagonal sign patterns (thin sheets, saddles). Each slab's map gives,
        // per mixed cell (j, k) and per inside corner, that component's vertex; only the
        // current and previous slabs are ever needed, so the map does not grow with nx.
        int[]?[] previousMap = new int[ny * nz][];
        int[]?[] currentMap = new int[ny * nz][];
        var previousTouched = new List<int>();
        var currentTouched = new List<int>();

        var positions = new List<Vector3d>();
        // Quads are bucketed by the loop variable that was OUTERMOST in the dense version's
        // three emission passes, and concatenated at the end: that reproduces the dense
        // face ordering exactly while letting the passes run interleaved, slab by slab.
        var facesX = new List<int[]>();
        var facesY = new List<int[]>?[ny];
        var facesZ = new List<int[]>?[nz];

        SampleSlabs(0, Math.Min(window, nx + 1));
        int available = Math.Min(window, nx + 1);
        progress?.ThrowIfCancelled();

        for (int i = 0; i < nx;)
        {
            // Cells [i, baseSlab + available - 2] are covered by the sampled window.
            int last = Math.Min(baseSlab + available - 2, nx - 1);
            for (; i <= last; i++)
            {
                if (progress is not null && (i & 15) == 0)
                {
                    progress.ThrowIfCancelled();
                    progress.Report(0.95 * i / nx);
                }
                ProcessSlab(i);
            }
            if (i >= nx)
                break;

            // Slide the window: the last sampled slab becomes the new first one.
            Array.Copy(values, (available - 1) * slabSamples, values, 0, slabSamples);
            baseSlab += available - 1;
            int to = Math.Min(baseSlab + window, nx + 1);
            SampleSlabs(baseSlab + 1, to);
            available = to - baseSlab;
            progress?.ThrowIfCancelled();
        }

        progress?.ThrowIfCancelled();
        var mesh = HalfEdgeMesh.Build(positions, AllFaces());
        progress?.Report(1);
        return mesh;

        IEnumerable<IReadOnlyList<int>> AllFaces()
        {
            foreach (var face in facesX)
                yield return face;
            foreach (var bucket in facesY)
            {
                if (bucket is null)
                    continue;
                foreach (var face in bucket)
                    yield return face;
            }
            foreach (var bucket in facesZ)
            {
                if (bucket is null)
                    continue;
                foreach (var face in bucket)
                    yield return face;
            }
        }

        // ---- per-slab work: cell vertices, then the three quad passes ----

        void ProcessSlab(int i)
        {
            Span<int> corners = stackalloc int[8];
            Span<int> stack = stackalloc int[8];
            ReadOnlySpan<(int A, int B)> edges =
            [
                (0, 1), (2, 3), (4, 5), (6, 7),
                (0, 2), (1, 3), (4, 6), (5, 7),
                (0, 4), (1, 5), (2, 6), (3, 7),
            ];

            (previousMap, currentMap) = (currentMap, previousMap);
            (previousTouched, currentTouched) = (currentTouched, previousTouched);
            foreach (int slot in currentTouched)
                currentMap[slot] = null;
            currentTouched.Clear();

            int local = i - baseSlab;
            int slab0 = local * slabSamples;
            int slab1 = slab0 + slabSamples;

            // Corner of cell (i, j, k): local x offset dx picks the slab, (j+dy, k+dz) the
            // sample within it.
            int Corner(int dx, int j, int k) => (dx == 0 ? slab0 : slab1) + j * sz + k;

            for (int j = 0; j < ny; j++)
            {
                for (int k = 0; k < nz; k++)
                {
                    corners[0] = Corner(0, j, k);
                    corners[1] = Corner(1, j, k);
                    corners[2] = Corner(0, j + 1, k);
                    corners[3] = Corner(1, j + 1, k);
                    corners[4] = Corner(0, j, k + 1);
                    corners[5] = Corner(1, j, k + 1);
                    corners[6] = Corner(0, j + 1, k + 1);
                    corners[7] = Corner(1, j + 1, k + 1);

                    int insideMask = 0;
                    for (int c = 0; c < 8; c++)
                    {
                        if (values[corners[c]] < 0)
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
                            int insideCorner = aIn ? ea : eb;
                            int outsideCorner = aIn ? eb : ea;
                            if (values[corners[outsideCorner]] < 0)
                                continue; // both grid-inside: edge internal to the solid
                            double t = values[corners[insideCorner]] /
                                (values[corners[insideCorner]] - values[corners[outsideCorner]]);
                            sum += Vector3d.Lerp(
                                CornerPosition(i, j, k, insideCorner),
                                CornerPosition(i, j, k, outsideCorner), t);
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

                    int slot = j * nz + k;
                    currentMap[slot] = map;
                    currentTouched.Add(slot);
                }
            }

            // X-aligned edges: quad over cells varying in (y, z); CCW in (y, z) → +X normal.
            for (int j = 1; j < ny; j++)
            {
                for (int k = 1; k < nz; k++)
                {
                    bool insideStart = values[Corner(0, j, k)] < 0;
                    if (insideStart == values[Corner(1, j, k)] < 0)
                        continue;
                    int d = insideStart ? 0 : 1; // local x-bit of the inside endpoint
                    Emit(facesX,
                        currentMap[(j - 1) * nz + k - 1], d | 2 | 4,
                        currentMap[j * nz + k - 1], d | 4,
                        currentMap[j * nz + k], d,
                        currentMap[(j - 1) * nz + k], d | 2,
                        flip: !insideStart);
                }
            }

            if (i < 1)
                return;

            // Y-aligned edges: quad over cells varying in (z, x); CCW in (z, x) → +Y normal.
            for (int j = 0; j < ny; j++)
            {
                for (int k = 1; k < nz; k++)
                {
                    bool insideStart = values[Corner(0, j, k)] < 0;
                    if (insideStart == values[Corner(0, j + 1, k)] < 0)
                        continue;
                    int d = (insideStart ? 0 : 1) << 1; // local y-bit of the inside endpoint
                    Emit(facesY[j] ??= [],
                        previousMap[j * nz + k - 1], d | 1 | 4,
                        previousMap[j * nz + k], d | 1,
                        currentMap[j * nz + k], d,
                        currentMap[j * nz + k - 1], d | 4,
                        flip: !insideStart);
                }
            }

            // Z-aligned edges: quad over cells varying in (x, y); CCW in (x, y) → +Z normal.
            for (int k = 0; k < nz; k++)
            {
                for (int j = 1; j < ny; j++)
                {
                    bool insideStart = values[Corner(0, j, k)] < 0;
                    if (insideStart == values[Corner(0, j, k + 1)] < 0)
                        continue;
                    int d = (insideStart ? 0 : 1) << 2; // local z-bit of the inside endpoint
                    Emit(facesZ[k] ??= [],
                        previousMap[(j - 1) * nz + k], d | 1 | 2,
                        currentMap[(j - 1) * nz + k], d | 2,
                        currentMap[j * nz + k], d,
                        previousMap[j * nz + k], d | 1,
                        flip: !insideStart);
                }
            }
        }

        // World position of corner <paramref name="c"/> of cell (i, j, k) — the same
        // expression the dense sampler stored, recomputed instead of retained.
        Vector3d CornerPosition(int i, int j, int k, int c) => origin + (
            (i + (c & 1)) * cell,
            (j + ((c >> 1) & 1)) * cell,
            (k + ((c >> 2) & 1)) * cell);

        // One quad per interior sign-changing grid edge, wound so normals point outward.
        // Each adjacent cell contributes the vertex of the component that contains its
        // local copy of the edge's inside endpoint.
        static void Emit(
            List<int[]> into,
            int[]? m0, int corner0, int[]? m1, int corner1,
            int[]? m2, int corner2, int[]? m3, int corner3, bool flip)
        {
            if (m0 is null || m1 is null || m2 is null || m3 is null)
                return;
            int v0 = m0[corner0], v1 = m1[corner1], v2 = m2[corner2], v3 = m3[corner3];
            if (v0 < 0 || v1 < 0 || v2 < 0 || v3 < 0)
                return;
            into.Add(flip ? [v3, v2, v1, v0] : [v0, v1, v2, v3]);
        }
    }
}
