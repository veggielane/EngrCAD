using System.Buffers;
using System.Runtime.InteropServices;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Mesh;

namespace EngrCAD.Interop;

/// <summary>
/// Implicit → mesh conversion by Surface Nets (dual contouring without normals-based
/// vertex placement): one vertex per connected component of a cell's inside corners, placed
/// at the mean of that component's edge crossings; one quad per interior sign-changing grid
/// edge. Produces closed quad-dominant meshes for smooth fields whose surface stays inside
/// the sampled region; surfaces that cross the region boundary come out open there.
/// <para>
/// Output is MANIFOLD by contract, not by hope: a component is split further where an
/// <em>ambiguous</em> grid face — one whose inside corners are a diagonal pair — is joined
/// by the cells on both sides, the one configuration that puts two quads on a single
/// directed edge. See the rule and its proof in <c>ProcessSlab</c>.
/// </para>
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
    /// Sample positions a parallel worker should own at a minimum, expressed as a count and
    /// divided by the row length to get whole rows. Matches the implicit engine's own batch
    /// chunk so the coordinate scratch and the operator temporaries below it stay cache
    /// resident; the value is not load bearing for correctness.
    /// </summary>
    private const int SampleChunk = 1024;

    /// <summary>
    /// Cube edges, in the order the crossings are averaged in. The INDEX into this table is
    /// the key the per-cell map is stored under, and the quad passes name the same edges by
    /// those indices: x-aligned 0..3 (by dy + 2dz), y-aligned 4..7 (dx + 2dz), z-aligned
    /// 8..11 (dx + 2dy).
    /// </summary>
    private static readonly (int A, int B)[] CubeEdges =
    [
        (0, 1), (2, 3), (4, 5), (6, 7),
        (0, 2), (1, 3), (4, 6), (5, 7),
        (0, 4), (1, 5), (2, 6), (3, 7),
    ];

    /// <summary>
    /// The six cube FACES as the four cube edges lying on each, in the order -x, +x, -y, +y,
    /// -z, +z. Two crossings on a common face are the two ends of one arc of the surface's
    /// cross-section there, which is what <see cref="SurfacePieceTable"/> joins them by.
    /// </summary>
    private static readonly int[] FaceEdges =
    [
        4, 6, 8, 10,    // -x (corners 0 2 4 6)
        5, 7, 9, 11,    // +x (corners 1 3 5 7)
        0, 2, 8, 9,     // -y (corners 0 1 4 5)
        1, 3, 10, 11,   // +y (corners 2 3 6 7)
        0, 1, 4, 5,     // -z (corners 0 1 2 3)
        2, 3, 6, 7,     // +z (corners 4 5 6 7)
    ];

    /// <summary>
    /// Which SURFACE PIECE each of a cell's crossings belongs to — one vertex per piece —
    /// for every one of the 256 corner sign patterns, packed four bits per cube edge
    /// (<c>15</c> where the edge does not cross, otherwise the piece's root edge index).
    /// It is a TABLE because the partition is a pure function of the sign pattern: computing
    /// it per cell measured 1.15–1.51× on the whole polygonization, and this is free.
    /// <para>
    /// <b>One vertex per inside component is not enough</b>, because an inside component can
    /// bound SEVERAL sheets: the standard case is a wall about one cell thick, whose material
    /// is one connected blob while the void inside and the space outside are two — six
    /// crossings, two triangles, and giving them one vertex pinches the mesh to a point there
    /// (a non-manifold vertex; <see cref="HalfEdgeMesh.NonManifoldVertices"/> reports them).
    /// </para>
    /// <para>
    /// <b>The refinement is by the cube's own FACE adjacency</b>: two crossings on a common
    /// face are the two ends of one arc of the surface's cross-section there, so they belong
    /// to one sheet, and crossings the faces never link belong to different sheets. That is
    /// the whole rule, and it is deliberately no finer — the AMBIGUOUS face (four crossings,
    /// its inside corners a diagonal pair) keeps merging all four, which is exactly what the
    /// incumbent rule did and what <c>ProcessSlab</c>'s neighbour-consulting split then
    /// resolves. Separating an ambiguous face's two arcs HERE is measurably wrong: the two
    /// cells sharing it would have to agree about which arc is which, and neither the face's
    /// own signs (the asymptotic decider) nor a cut of the cube's connectivity gets them to
    /// agree — both were built and measured, and both returned open meshes and bow-tie
    /// vertices on the same family. See the residual in todo.md.
    /// </para>
    /// <para>
    /// The join is restricted to crossings of the SAME inside component, which costs nothing
    /// on an unambiguous face (its two inside ends are always in one component — they are
    /// equal, adjacent, or joined through the face's fourth corner) and is what keeps an
    /// ambiguous face from merging two components across a diagonal the cube itself separates.
    /// So pieces REFINE inside components and never merge across them, and a cell whose every
    /// component is a single piece is untouched bit for bit.
    /// </para>
    /// </summary>
    private static readonly long[] SurfacePieceTable = BuildSurfacePieceTable();

    private static long[] BuildSurfacePieceTable()
    {
        var table = new long[256];
        Span<int> component = stackalloc int[8];
        Span<int> scratch = stackalloc int[8];
        Span<int> piece = stackalloc int[12];
        Span<int> firstOnFace = stackalloc int[8];
        ReadOnlySpan<(int A, int B)> edges = CubeEdges;
        ReadOnlySpan<int> faceEdges = FaceEdges;

        for (int mask = 0; mask < 256; mask++)
        {
            Components(mask, component, scratch);
            for (int e = 0; e < 12; e++)
            {
                var (a, b) = edges[e];
                piece[e] = ((mask >> a) & 1) != ((mask >> b) & 1) ? e : -1;
            }
            for (int f = 0; f < 6; f++)
            {
                firstOnFace.Fill(-1);
                for (int u = 0; u < 4; u++)
                {
                    int e = faceEdges[f * 4 + u];
                    if (piece[e] < 0)
                        continue;
                    var (a, b) = edges[e];
                    int inside = component[((mask >> a) & 1) != 0 ? a : b];
                    if (firstOnFace[inside] < 0)
                        firstOnFace[inside] = e;
                    else
                        Union(piece, firstOnFace[inside], e);
                }
            }
            long packed = 0;
            for (int e = 0; e < 12; e++)
                packed |= (long)(piece[e] < 0 ? 15 : Find(piece, e)) << (e << 2);
            table[mask] = packed;
        }
        return table;

        static int Find(Span<int> piece, int e)
        {
            while (piece[e] != e)
                e = piece[e] = piece[piece[e]];
            return e;
        }

        static void Union(Span<int> piece, int a, int b)
        {
            int ra = Find(piece, a), rb = Find(piece, b);
            if (ra != rb)
                piece[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }
    }

    /// <summary>
    /// Component id per cube corner, flooding within the corner's OWN side over the cube's
    /// face adjacency (bit flips). Inside corners are seeded first and in corner order, so
    /// inside component ids — and therefore the order vertices are created in — are exactly
    /// what an inside-only fill gave.
    /// </summary>
    private static void Components(int insideMask, Span<int> component, Span<int> scratch)
    {
        component.Fill(-1);
        int next = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            int side = pass == 0 ? 1 : 0;
            for (int seed = 0; seed < 8; seed++)
            {
                if (component[seed] >= 0 || ((insideMask >> seed) & 1) != side)
                    continue;
                int id = next++;
                int top = 0;
                scratch[top++] = seed;
                component[seed] = id;
                while (top > 0)
                {
                    int c = scratch[--top];
                    foreach (int neighbor in (ReadOnlySpan<int>)[c ^ 1, c ^ 2, c ^ 4])
                    {
                        if (component[neighbor] < 0 && ((insideMask >> neighbor) & 1) == side)
                        {
                            component[neighbor] = id;
                            scratch[top++] = neighbor;
                        }
                    }
                }
            }
        }
    }

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
    /// the slab window rather than the grid volume. A <see cref="SurfaceCull"/> pass first
    /// removes the blocks the surface provably cannot reach (one evaluation per block, sound
    /// because the field is 1-Lipschitz), so the walk visits a shell around the surface
    /// instead of the whole volume; the visit ORDER is untouched, so the mesh is bit-for-bit
    /// what the full walk produces. Slabs are sampled in parallel
    /// (bit-for-bit deterministic — every sample lands in its own slot); the topology
    /// passes stay sequential and quads are emitted into per-axis buckets, so the output
    /// mesh's vertex and face ordering never depends on scheduling or on the window size.
    /// </para>
    /// <paramref name="progress"/> adds cooperative progress/cancellation (cancellation
    /// throws <see cref="OperationCanceledException"/>).
    /// </summary>
    public static HalfEdgeMesh Polygonize(Sdf sdf, in Aabb region, int resolution, ProgressCancel? progress = null) =>
        Polygonize(sdf, region, resolution, progress, WindowSampleBudget, cull: true);

    /// <summary>
    /// The implementation, with the slab window's sample budget and the surface cull exposed
    /// so tests can force streaming on a small grid, disable the cull, and assert the output
    /// is bit-for-bit independent of both.
    /// </summary>
    internal static HalfEdgeMesh Polygonize(
        Sdf sdf, in Aabb region, int resolution, ProgressCancel? progress, int windowSampleBudget,
        bool cull = true)
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

        // Blocks the surface provably cannot reach are never sampled and never walked. The
        // grid, the coordinate expressions and every loop's ORDER are untouched, so the mesh
        // is bit-identical to the full walk — the cull only removes work that would have
        // produced nothing (SurfaceCull carries the completeness argument).
        var visit = cull
            ? SurfaceCull.Build(sdf, origin, cell, cells, progress)
            : SurfaceCull.All(cells);
        const int block = SurfaceCull.BlockCells;

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
                var rented = ArrayPool<double>.Shared.Rent(sz * 3);
                var tiles = ArrayPool<bool>.Shared.Rent(visit.CellBlocksZ);
                try
                {
                    var xs = rented.AsSpan(0, sz);
                    var ys = rented.AsSpan(sz, sz);
                    var zs = rented.AsSpan(sz * 2, sz);
                    var row = tiles.AsSpan(0, visit.CellBlocksZ);
                    for (int r = r0; r < r1; r++)
                    {
                        int slab = from + r / sy;
                        int j = r % sy;
                        // Coordinates are recomputed from the indices with the same
                        // expression the dense sampler used, so values are unchanged.
                        double px = origin.X + slab * cell;
                        double py = origin.Y + j * cell;
                        int rowBase = localOrigin + r * sz;

                        // Adjacent active tiles merge into one run, so a row typically feeds
                        // the field one or two contiguous batches rather than sz/1024 of them.
                        visit.SampleTilesForRow(slab, j, row);
                        for (int bk = 0; bk < row.Length;)
                        {
                            if (!row[bk])
                            {
                                bk++;
                                continue;
                            }
                            int k0 = bk * block;
                            while (bk < row.Length && row[bk])
                                bk++;
                            // A run of cells [k0, k1) has corners [k0, k1] — hence the +1.
                            int k1 = Math.Min(bk * block + 1, sz);
                            int length = k1 - k0;
                            for (int k = k0; k < k1; k++)
                            {
                                xs[k - k0] = px;
                                ys[k - k0] = py;
                                zs[k - k0] = origin.Z + k * cell;
                            }
                            sdf.Evaluate(
                                xs[..length], ys[..length], zs[..length],
                                values.AsSpan(rowBase + k0, length));
                        }
                    }
                }
                finally
                {
                    ArrayPool<bool>.Shared.Return(tiles);
                    ArrayPool<double>.Shared.Return(rented);
                }
            }, minBlockSize: Math.Max(1, SampleChunk / sz));
        }

        // One vertex per connected component of inside-corners per cell (manifold surface
        // nets), refined at ambiguous faces (see ProcessSlab): a plain one-vertex-per-cell
        // scheme produces non-manifold edges on grid faces with diagonal sign patterns
        // (thin sheets, saddles). Each slab's map gives, per mixed cell (j, k) and per
        // CUBE EDGE, the vertex that edge's crossing belongs to — keyed by the edge rather
        // than by its inside corner, since a corner's crossings can end up on two different
        // vertices. Only the current and previous slabs are ever needed, so the map does
        // not grow with nx.
        int[]?[] previousMap = new int[ny * nz][];
        int[]?[] currentMap = new int[ny * nz][];
        var previousTouched = new List<int>();
        var currentTouched = new List<int>();
        // Whether the previous slab's cell joins the two diagonals of its far (+x) face —
        // bit 0 for corners 1~7, bit 1 for 3~5. The +x neighbour needs that answer to test
        // the face they share, and the sliding window cannot promise it the slab beyond, so
        // the one bit pair the test needs is carried forward instead of the samples.
        var previousFarJoins = new byte[ny * nz];
        var currentFarJoins = new byte[ny * nz];

        var positions = new List<Vector3d>();
        // Quads are bucketed by the loop variable that was OUTERMOST in the dense version's
        // three emission passes, and concatenated at the end: that reproduces the dense
        // face ordering exactly while letting the passes run interleaved, slab by slab.
        // Each bucket is a FLAT index buffer, four entries per quad — a quad-per-array
        // layout costs one heap allocation per face, which at resolution 256 is 131 000 of
        // them for a mesh whose whole point is that it is grid-structured.
        var facesX = new List<int>();
        var facesY = new List<int>?[ny];
        var facesZ = new List<int>?[nz];

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
        // Every face is a quad, so the half-edge builder takes the flat buffer directly:
        // no per-face array, no interface dispatch per loop, and the twin pairing is a
        // counting sort over the edges' lower endpoint rather than a hash table.
        var mesh = HalfEdgeMesh.Build(positions, AllCorners(), verticesPerFace: 4);
        progress?.Report(1);
        return mesh;

        int[] AllCorners()
        {
            int total = facesX.Count;
            foreach (var bucket in facesY)
                total += bucket?.Count ?? 0;
            foreach (var bucket in facesZ)
                total += bucket?.Count ?? 0;

            var all = new int[total];
            int at = 0;
            void Append(List<int> bucket)
            {
                CollectionsMarshal.AsSpan(bucket).CopyTo(all.AsSpan(at));
                at += bucket.Count;
            }

            Append(facesX);
            foreach (var bucket in facesY)
            {
                if (bucket is not null)
                    Append(bucket);
            }
            foreach (var bucket in facesZ)
            {
                if (bucket is not null)
                    Append(bucket);
            }
            return all;
        }

        // ---- per-slab work: cell vertices, then the three quad passes ----

        void ProcessSlab(int i)
        {
            Span<int> corners = stackalloc int[8];
            Span<int> stack = stackalloc int[8];
            Span<int> component = stackalloc int[8];
            Span<int> neighborComponent = stackalloc int[8];
            Span<int> neighborStack = stackalloc int[8];
            ReadOnlySpan<(int A, int B)> edges = CubeEdges;
            ReadOnlySpan<int> faceEdges = FaceEdges;
            // The cell's three LOW faces, each listed twice — once per diagonal corner pair,
            // as (index into faceEdges, face corner mask, the pair). A face whose inside
            // corners are EXACTLY one of its diagonals is Marching Cubes' AMBIGUOUS face: all
            // four of its edges cross, so it carries two quad edges between the same pair of
            // cells, and those two quads share a directed edge unless one of the cells
            // separates them. Only the low faces are listed because a face is owned by the
            // cell on its + side, which makes every interior face somebody's low face and
            // tests it exactly once.
            ReadOnlySpan<(int FaceIndex, int Face, int A, int B)> diagonals =
            [
                (0, 0x55, 0, 6), (0, 0x55, 2, 4),   // -x
                (2, 0x33, 0, 5), (2, 0x33, 1, 4),   // -y
                (4, 0x0F, 0, 3), (4, 0x0F, 1, 2),   // -z
            ];

            (previousMap, currentMap) = (currentMap, previousMap);
            (previousFarJoins, currentFarJoins) = (currentFarJoins, previousFarJoins);
            (previousTouched, currentTouched) = (currentTouched, previousTouched);
            foreach (int slot in currentTouched)
            {
                currentMap[slot] = null;
                currentFarJoins[slot] = 0;
            }
            currentTouched.Clear();

            int local = i - baseSlab;
            int slab0 = local * slabSamples;
            int slab1 = slab0 + slabSamples;

            // Corner of cell (i, j, k): local x offset dx picks the slab, (j+dy, k+dz) the
            // sample within it.
            int Corner(int dx, int j, int k) => (dx == 0 ? slab0 : slab1) + j * sz + k;

            // Does the cell across this LOW face join the same diagonal pair? Owning only the
            // low faces is what keeps the neighbour reachable: the -x answer was left behind
            // by the slab already processed, and -y/-z sit inside the same slab pair, so no
            // cell ever needs a slab the sliding window may not hold. A face on the grid
            // boundary carries no quad edge, so a missing neighbour is not an answer needed.
            bool NeighborJoins(
                int face, int da, int db, int j, int k, Span<int> scratch, Span<int> scratchStack)
            {
                if (face == 0x55) // -x: the slab already processed left its answer behind
                    return i > 0 && (previousFarJoins[j * nz + k] & (da == 0 ? 1 : 2)) != 0;

                int flip;
                if (face == 0x33 && j > 0) { j--; flip = 2; }        // -y
                else if (face == 0x0F && k > 0) { k--; flip = 4; }   // -z
                else return false;

                int mask = 0;
                for (int c = 0; c < 8; c++)
                {
                    if (values[Corner(c & 1, j + ((c >> 1) & 1), k + ((c >> 2) & 1))] < 0)
                        mask |= 1 << c;
                }
                Components(mask, scratch, scratchStack);
                // The pair's corners in the neighbour are ours with that axis' bit flipped.
                return scratch[da ^ flip] == scratch[db ^ flip];
            }

            // Cells are visited in exactly the (j, k) order the full walk used — the tile
            // loop only skips runs the cull proved empty, so vertex numbering is unchanged.
            int cellBlock = i / block;
            for (int j = 0; j < ny; j++)
            {
                var cellTiles = visit.CellRow(cellBlock, j / block);
                for (int bk = 0; bk < cellTiles.Length; bk++)
                {
                    if (!cellTiles[bk])
                        continue;
                    int kEnd = Math.Min((bk + 1) * block, nz);
                    for (int k = bk * block; k < kEnd; k++)
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

                        var map = new int[12];
                        Array.Fill(map, -1);

                        Components(insideMask, component, stack);
                        // Four bits per cube edge; PieceOf reads the nibble.
                        long pieces = SurfacePieceTable[insideMask];
                        int PieceOf(int e) => (int)((pieces >>> (e << 2)) & 15);

                        // Which surface pieces must be SPLIT further, one bit per piece root.
                        //
                        // A grid face whose inside corners are exactly a diagonal pair is
                        // ambiguous: all four of its edges cross, so it carries two quad
                        // edges between the same two cells. Those two quads land on the same
                        // DIRECTED edge — the mesh is non-manifold and Build refuses it —
                        // exactly when BOTH cells join that pair into one component. So each
                        // interior face is tested once, by the cell on its + side, against
                        // the neighbour across it; a face at the grid boundary carries no
                        // quad edge and is skipped.
                        //
                        // The split is by the outside blob each crossing reaches, and it is
                        // always available: a cell can never join both an ambiguous face's
                        // inside pair AND its outside pair, since a path between the inside
                        // pair must use an off-diagonal corner of the far face as INSIDE
                        // while a path between the outside pair needs both of them OUTSIDE.
                        // Nothing else is refined, so every other cell stays bit-identical.
                        int splitPieces = 0;
                        foreach (var (faceIndex, face, da, db) in diagonals)
                        {
                            if ((insideMask & face) != ((1 << da) | (1 << db)) ||
                                component[da] != component[db])
                                continue;
                            if (!NeighborJoins(face, da, db, j, k, neighborComponent, neighborStack))
                                continue;
                            // All four of an ambiguous face's crossings share that face and
                            // one inside component, so they are one piece; naming it by any
                            // of them scopes the split to the sheet that carries the face
                            // rather than to everything the component bounds.
                            splitPieces |= 1 << PieceOf(faceEdges[faceIndex * 4]);
                        }

                        // The far (+x) face's answer, for the neighbour that will test it.
                        currentFarJoins[j * nz + k] = (byte)(
                            (component[1] == component[7] ? 1 : 0) |
                            (component[3] == component[5] ? 2 : 0));

                        int seenComponents = 0;
                        for (int seed = 0; seed < 8; seed++)
                        {
                            if ((insideMask & (1 << seed)) == 0)
                                continue;
                            int id = component[seed];
                            if ((seenComponents & (1 << id)) != 0)
                                continue;
                            seenComponents |= 1 << id;

                            // Vertex: mean of the crossings on edges leaving the component —
                            // one per SURFACE PIECE, which is all of them at once when the
                            // component bounds a single sheet (the original rule, same edge
                            // order and same arithmetic), split once more by outside blob where
                            // an ambiguous face demands it. Groups come out in first-encounter
                            // order.
                            while (true)
                            {
                                int pieceKey = int.MinValue;
                                int reachedKey = 0;
                                var sum = Vector3d.Zero;
                                int crossings = 0;
                                for (int e = 0; e < edges.Length; e++)
                                {
                                    if (map[e] >= 0)
                                        continue; // taken by an earlier group of this component
                                    var (ea, eb) = edges[e];
                                    bool aIn = (insideMask & (1 << ea)) != 0;
                                    if (aIn == ((insideMask & (1 << eb)) != 0))
                                        continue;
                                    int insideCorner = aIn ? ea : eb;
                                    int outsideCorner = aIn ? eb : ea;
                                    if (component[insideCorner] != id)
                                        continue;
                                    int group = PieceOf(e);
                                    int reached = (splitPieces & (1 << group)) != 0
                                        ? component[outsideCorner]
                                        : -1;
                                    if (pieceKey == int.MinValue)
                                        (pieceKey, reachedKey) = (group, reached);
                                    else if (group != pieceKey || reached != reachedKey)
                                        continue;
                                    double t = values[corners[insideCorner]] /
                                        (values[corners[insideCorner]] - values[corners[outsideCorner]]);
                                    sum += Vector3d.Lerp(
                                        CornerPosition(i, j, k, insideCorner),
                                        CornerPosition(i, j, k, outsideCorner), t);
                                    crossings++;
                                    map[e] = positions.Count;
                                }
                                if (crossings == 0)
                                    break;
                                positions.Add(sum / crossings);
                            }
                        }

                        int slot = j * nz + k;
                        currentMap[slot] = map;
                        currentTouched.Add(slot);
                    }
                }
            }

            // The three quad passes walk grid EDGES, and an edge can only carry a quad when
            // all four cells around it produced a vertex — so the sample mask (the cell mask
            // dilated to the corners) is a superset of the edges worth testing. Over-inclusion
            // is free: Emit already returns on a missing neighbour map.

            // X-aligned edges: quad over cells varying in (y, z); CCW in (y, z) → +X normal.
            for (int j = 1; j < ny; j++)
            {
                var tiles = visit.SampleRow(cellBlock, j / block);
                for (int bk = 0; bk < tiles.Length; bk++)
                {
                    if (!tiles[bk])
                        continue;
                    int kEnd = Math.Min((bk + 1) * block, nz);
                    for (int k = Math.Max(1, bk * block); k < kEnd; k++)
                    {
                        bool insideStart = values[Corner(0, j, k)] < 0;
                        if (insideStart == values[Corner(1, j, k)] < 0)
                            continue;
                        // The grid edge is each neighbour's x-aligned cube edge at the
                        // (dy, dz) corner the cell sees it from; which END is inside only
                        // decides the winding.
                        Emit(facesX,
                            currentMap[(j - 1) * nz + k - 1], 3,
                            currentMap[j * nz + k - 1], 2,
                            currentMap[j * nz + k], 0,
                            currentMap[(j - 1) * nz + k], 1,
                            flip: !insideStart);
                    }
                }
            }

            if (i < 1)
                return;

            // Y-aligned edges: quad over cells varying in (z, x); CCW in (z, x) → +Y normal.
            for (int j = 0; j < ny; j++)
            {
                var tiles = visit.SampleRow(cellBlock, j / block);
                for (int bk = 0; bk < tiles.Length; bk++)
                {
                    if (!tiles[bk])
                        continue;
                    int kEnd = Math.Min((bk + 1) * block, nz);
                    for (int k = Math.Max(1, bk * block); k < kEnd; k++)
                    {
                        bool insideStart = values[Corner(0, j, k)] < 0;
                        if (insideStart == values[Corner(0, j + 1, k)] < 0)
                            continue;
                        Emit(facesY[j] ??= [],
                            previousMap[j * nz + k - 1], 7,
                            previousMap[j * nz + k], 5,
                            currentMap[j * nz + k], 4,
                            currentMap[j * nz + k - 1], 6,
                            flip: !insideStart);
                    }
                }
            }

            // Z-aligned edges: quad over cells varying in (x, y); CCW in (x, y) → +Z normal.
            for (int k = 0; k < nz; k++)
            {
                int sampleTileZ = k / block;
                for (int bj = 0; bj < visit.SampleBlocksY; bj++)
                {
                    if (!visit.SampleActive(cellBlock, bj, sampleTileZ))
                        continue;
                    int jEnd = Math.Min((bj + 1) * block, ny);
                    for (int j = Math.Max(1, bj * block); j < jEnd; j++)
                    {
                        bool insideStart = values[Corner(0, j, k)] < 0;
                        if (insideStart == values[Corner(0, j, k + 1)] < 0)
                            continue;
                        Emit(facesZ[k] ??= [],
                            previousMap[(j - 1) * nz + k], 11,
                            currentMap[(j - 1) * nz + k], 10,
                            currentMap[j * nz + k], 8,
                            previousMap[j * nz + k], 9,
                            flip: !insideStart);
                    }
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
        // Each adjacent cell contributes the vertex carrying that edge's crossing, named by
        // the edge's index in the cell's own cube-edge numbering.
        static void Emit(
            List<int> into,
            int[]? m0, int edge0, int[]? m1, int edge1,
            int[]? m2, int edge2, int[]? m3, int edge3, bool flip)
        {
            if (m0 is null || m1 is null || m2 is null || m3 is null)
                return;
            int v0 = m0[edge0], v1 = m1[edge1], v2 = m2[edge2], v3 = m3[edge3];
            if (v0 < 0 || v1 < 0 || v2 < 0 || v3 < 0)
                return;
            if (flip)
            {
                into.Add(v3);
                into.Add(v2);
                into.Add(v1);
                into.Add(v0);
            }
            else
            {
                into.Add(v0);
                into.Add(v1);
                into.Add(v2);
                into.Add(v3);
            }
        }
    }
}
