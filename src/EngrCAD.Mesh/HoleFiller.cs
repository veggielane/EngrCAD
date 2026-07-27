using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>How a boundary loop was (or was not) filled by <see cref="HoleFiller.FillAll"/>.</summary>
public enum HoleFillMethod
{
    /// <summary>The loop (possibly together with coplanar sibling loops) was ear-clipped in its best-fit plane.</summary>
    Planar,
    /// <summary>The loop was filled with a centroid-vertex triangle fan.</summary>
    Simple,
    /// <summary>
    /// The loop was spanned by the minimum-weight triangulation of its own vertices
    /// (<see cref="HoleFiller.FillMinimal"/>) — no new vertices, creases reconstructed.
    /// </summary>
    Minimal,
    /// <summary>
    /// The loop was spanned by a remeshed, Laplacian-relaxed patch
    /// (<see cref="HoleFiller.FillSmoothed"/>) — new vertices, membrane-like surface.
    /// </summary>
    Smoothed,
    /// <summary>The loop was left open; <see cref="HoleFillOutcome.Message"/> says why.</summary>
    Skipped,
}

/// <summary>Which upper tier <see cref="HoleFiller.FillAll"/> falls back to when neither the planar nor the simple fill applies.</summary>
public enum HoleFillFallback
{
    /// <summary>Report the loop as <see cref="HoleFillMethod.Skipped"/> — fill nothing you cannot fill well.</summary>
    None,
    /// <summary>Try <see cref="HoleFiller.FillMinimal"/>. The default.</summary>
    Minimal,
    /// <summary>Try <see cref="HoleFiller.FillSmoothed"/>, falling back to <see cref="HoleFiller.FillMinimal"/> if the patch cannot be built.</summary>
    Smoothed,
}

/// <summary>Settings for <see cref="HoleFiller.FillSmoothed"/>.</summary>
public sealed record SmoothedHoleFillOptions
{
    /// <summary>Defaults: target edge length from the loop, 20 relaxation passes at half speed.</summary>
    public static SmoothedHoleFillOptions Default { get; } = new();

    /// <summary>
    /// Target edge length for the patch. Null (the default) derives it from the hole itself —
    /// the mean length of its boundary edges — so the fill matches the surrounding
    /// tessellation at any model scale. (g3's equivalent defaults to an absolute 2.5 world
    /// units, which is silently wrong for anything not modelled in millimetres.)
    /// </summary>
    public double? TargetEdgeLength { get; init; }

    /// <summary>Remesh/relax passes over the patch. More passes = flatter, more membrane-like.</summary>
    public int Iterations { get; init; } = 20;

    /// <summary>Laplacian damping per pass, in [0, 1].</summary>
    public double SmoothSpeed { get; init; } = 0.5;
}

/// <summary>Per-loop outcome of <see cref="HoleFiller.FillAll"/>. <paramref name="LoopIndex"/>
/// indexes the input mesh's <see cref="HalfEdgeMesh.BoundaryLoops"/> order.</summary>
public sealed record HoleFillOutcome(int LoopIndex, int VertexCount, HoleFillMethod Method, string Message);

/// <summary>Result of <see cref="HoleFiller.FillAll"/>: the (closer-to-closed) mesh plus one outcome per boundary loop.</summary>
public sealed record HoleFillResult(HalfEdgeMesh Mesh, IReadOnlyList<HoleFillOutcome> Outcomes);

/// <summary>Options for <see cref="HoleFiller.FillAll"/>.</summary>
public sealed record HoleFillOptions
{
    public static HoleFillOptions Default { get; } = new();

    /// <summary>
    /// Maximum orthogonal distance of any loop vertex from the loops' best-fit plane for the
    /// loop to qualify for the planar fill. Absolute, defaulting to the weld tolerance
    /// (<see cref="Tolerance.Default"/>.Linear): planar holes produced by cuts/tessellation
    /// are exactly on-plane, while genuinely curved rims (sphere caps at coarse resolution,
    /// saddles) miss by their sagitta and fall through to the simple fill.
    /// </summary>
    public double PlanarityTolerance { get; init; } = Tolerance.Default.Linear;

    /// <summary>Largest loop (vertex count) the simple centroid-fan fallback will attempt.</summary>
    public int MaxSimpleFillVertices { get; init; } = 64;

    /// <summary>
    /// Which upper tier to try for loops neither the planar nor the simple fill can handle.
    /// Defaults to <see cref="HoleFillFallback.Minimal"/>: the minimum-weight triangulation
    /// of the rim's own vertices invents <b>no</b> geometry — every vertex of the patch is
    /// already a vertex of the hole — so it is not the "guess something" tier the honest
    /// default was guarding against. It cannot bulge, it restores creases rather than
    /// chording across them, and where it genuinely cannot decide (no admissible
    /// triangulation, or a rim past <see cref="MaxMinimalFillVertices"/>) it still reports
    /// <see cref="HoleFillMethod.Skipped"/> with the reason. <see cref="HoleFillFallback.None"/>
    /// remains available for callers who want a hole reported rather than closed at all;
    /// <see cref="HoleFillFallback.Smoothed"/> is the opt-in that does add vertices.
    /// </summary>
    public HoleFillFallback Fallback { get; init; } = HoleFillFallback.Minimal;

    /// <summary>
    /// Largest loop the <see cref="HoleFillFallback.Minimal"/> tier will attempt. Its
    /// dynamic program is O(n³) in the loop length — 256 vertices is ~17 M operations, a
    /// fraction of a second; ten thousand would be a week.
    /// </summary>
    public int MaxMinimalFillVertices { get; init; } = 256;

    /// <summary>Settings for the <see cref="HoleFillFallback.Smoothed"/> tier.</summary>
    public SmoothedHoleFillOptions Smoothed { get; init; } = SmoothedHoleFillOptions.Default;
}

/// <summary>
/// Hole filling for open meshes — the construct-new counterpart of geometry3Sharp's
/// <c>SimpleHoleFiller</c> / <c>PlanarHoleFiller</c> / <c>AutoHoleFill</c> dispatch.
/// Loops come from <see cref="HalfEdgeMesh.BoundaryLoops"/>; every fill returns a new mesh
/// (topology is immutable after <see cref="HalfEdgeMesh.Build"/>). Boundary half-edges are
/// wound opposite their interior twins, so a fill face that follows the boundary walk order
/// supplies exactly the free directed edges and the manifold-validating <c>Build</c> welds it
/// seamlessly. The smoothed / minimal-surface fill tiers of g3's <c>AutoHoleFill</c> are
/// future work; <see cref="FillAll"/> reports such loops as
/// <see cref="HoleFillMethod.Skipped"/> instead.
/// </summary>
public static class HoleFiller
{
    // Scale-free shape-classification heuristic for the simple (centroid-fan) fill, NOT a
    // geometric tolerance (epsilon-ladder: algorithmic guard tier): a fan from the centroid
    // of a large, wildly non-planar loop (saddle rims) self-intersects and produces garbage,
    // so loops with more than this many vertices whose best-fit-plane deviation exceeds this
    // fraction of the loop's own extent are refused. Small loops (<= the count) are always
    // fannable regardless of curvature — the local one-ring case.
    private const int SimpleGuardVertexCount = 8;
    private const double SimpleGuardDeviationRatio = 0.2;

    /// <summary>
    /// Fills one boundary loop with a triangle fan from the loop centroid (a single triangle
    /// when the loop has 3 vertices) — g3's <c>SimpleHoleFiller</c>. Suited to small holes;
    /// throws <see cref="InvalidOperationException"/> for large, wildly non-planar loops
    /// (deviation from the best-fit plane above a fixed fraction of the loop extent), where a
    /// centroid fan would self-intersect — use <see cref="FillAll"/> to get a per-hole report
    /// instead, or wait for the smoothed-fill tier (future work).
    /// </summary>
    public static HalfEdgeMesh FillSimple(HalfEdgeMesh mesh, IReadOnlyList<HalfEdge> loop)
    {
        var indices = ValidatedLoopIndices(mesh, loop);
        var (positions, faces) = mesh.ToIndexed();
        var positionList = new List<Vector3d>(positions);

        if (IsWildlyNonPlanar(indices, positionList, out double ratio))
            throw new InvalidOperationException(
                $"Loop with {indices.Length} vertices deviates from its best-fit plane by " +
                $"{ratio:P0} of its extent — a centroid fan would self-intersect. Use FillAll " +
                "for dispatch/reporting; smoothed fills are future work.");

        AppendSimpleFill(indices, positionList, faces);
        return HalfEdgeMesh.Build(positionList, faces);
    }

    /// <summary>
    /// Fills one boundary loop with the <b>minimum-weight triangulation of its own vertices</b>
    /// — the Barequet–Sharir / Liepa dynamic program, g3's <c>MinimalHoleFill</c> tier. No new
    /// vertices are created, so the patch interpolates the rim exactly and cannot bulge; the
    /// weight is the pair (largest dihedral angle, total area) compared lexicographically, so
    /// the fill is as flat as the rim allows and reconstructs a crease that runs across the
    /// hole instead of averaging it away. This is the tier for holes in <i>faceted</i>
    /// geometry — a bite out of a corner, a missing sliver between two planes.
    /// </summary>
    /// <remarks>
    /// <b>Deliberate deviation from geometry3Sharp:</b> g3 seeds a fan, refines it, and then
    /// runs four iterative edge-flip passes whose own comments describe them as unstable
    /// ("strong ordering effects", "will frequently not converge", a hard 20/40-pass cap to
    /// stop the oscillation) and which leave interior vertices behind often enough to need a
    /// forced-removal stage with a debugger break in it. The dynamic program is the standard
    /// algorithm for this exact problem: deterministic, globally optimal for the stated
    /// weight, O(n³) time and O(n²) memory in the loop length, and it cannot oscillate.
    /// <para>
    /// Chords that already exist elsewhere in the mesh are forbidden (using one would make the
    /// result non-manifold), which can leave a loop with no admissible triangulation at all;
    /// that throws, and <see cref="FillAll"/> reports it rather than throwing.
    /// </para>
    /// </remarks>
    public static HalfEdgeMesh FillMinimal(HalfEdgeMesh mesh, IReadOnlyList<HalfEdge> loop)
    {
        var indices = ValidatedLoopIndices(mesh, loop);
        var (positions, faces) = mesh.ToIndexed();
        var positionList = new List<Vector3d>(positions);
        if (!TryAppendMinimalFill(mesh, loop, indices, positionList, faces, out string? failure))
            throw new InvalidOperationException(failure);
        return HalfEdgeMesh.Build(positionList, faces);
    }

    /// <summary>
    /// Fills one boundary loop with a <b>relaxed membrane</b>: a coarse fan is remeshed to the
    /// hole's own edge length with its rim pinned, and Laplacian smoothing pulls the interior
    /// into a smooth surface spanning the rim — g3's <c>SmoothedHoleFill</c> tier. This is the
    /// tier for holes in <i>curved</i> geometry, where a flat minimal patch would read as a
    /// dent.
    /// </summary>
    /// <remarks>
    /// The patch is built, remeshed and relaxed as a standalone mesh and then stitched back,
    /// which is why the surrounding surface is untouched (g3's <c>ConstrainToHoleInterior =
    /// true</c> mode; its default instead grows the region two rings into the original mesh
    /// and remeshes that too, trading fidelity for blending). Stitching is exact rather than
    /// tolerant: the rim vertices are pinned <b>and</b> its edges are barred from splitting
    /// (<see cref="RemeshOptions.SplitFixedEdges"/>), so the patch comes back with the rim it
    /// went in with, vertex for vertex, and the two halves weld by index. An extra rim vertex
    /// would be a T-junction.
    /// <para>
    /// Iterated Laplacian smoothing with a fixed boundary converges to the same membrane a
    /// linear solve would give; we take the iterations rather than carry a sparse solver.
    /// </para>
    /// </remarks>
    public static HalfEdgeMesh FillSmoothed(HalfEdgeMesh mesh, IReadOnlyList<HalfEdge> loop,
        SmoothedHoleFillOptions? options = null)
    {
        var indices = ValidatedLoopIndices(mesh, loop);
        var (positions, faces) = mesh.ToIndexed();
        var positionList = new List<Vector3d>(positions);
        AppendSmoothedFill(indices, positionList, faces, options ?? SmoothedHoleFillOptions.Default);
        return HalfEdgeMesh.Build(positionList, faces);
    }

    /// <summary>
    /// Fills one boundary loop by ear-clipping it in its best-fit plane
    /// (<see cref="Fitting3d.FitPlane"/> → <see cref="Frame3d"/> → project →
    /// <see cref="PolygonTriangulator"/> → map back). Throws
    /// <see cref="ArgumentException"/> when any vertex is farther than
    /// <paramref name="planarityTolerance"/> (default: weld tolerance) from the plane.
    /// </summary>
    public static HalfEdgeMesh FillPlanar(HalfEdgeMesh mesh, IReadOnlyList<HalfEdge> loop,
        double? planarityTolerance = null) =>
        FillPlanar(mesh, [loop], planarityTolerance);

    /// <summary>
    /// Fills a set of boundary loops lying in one common plane, handling nesting: after
    /// projection, counter-clockwise loops are outer boundaries and clockwise loops are holes
    /// (the boundary walk always keeps the hole region on the same side, so nesting parity is
    /// intrinsic — no flags needed); each hole is assigned to the smallest containing outer
    /// and each outer is ear-clipped with its holes
    /// (<see cref="PolygonTriangulator.TriangulateWithHoles"/>). This is the annulus case a
    /// plane cut of a tube produces — the case <see cref="MeshPlaneCut"/> refuses to cap.
    /// </summary>
    public static HalfEdgeMesh FillPlanar(HalfEdgeMesh mesh,
        IReadOnlyList<IReadOnlyList<HalfEdge>> coplanarLoops, double? planarityTolerance = null)
    {
        if (coplanarLoops.Count == 0)
            throw new ArgumentException("At least one loop is required.", nameof(coplanarLoops));
        var loops = new List<int[]>(coplanarLoops.Count);
        foreach (var loop in coplanarLoops)
            loops.Add(ValidatedLoopIndices(mesh, loop));

        var (positions, faces) = mesh.ToIndexed();
        var positionList = new List<Vector3d>(positions);
        AppendPlanarFill(loops, positionList, faces,
            planarityTolerance ?? Tolerance.Default.Linear);
        return HalfEdgeMesh.Build(positionList, faces);
    }

    /// <summary>
    /// Fills every boundary loop of <paramref name="mesh"/> it can, dispatching per hole
    /// (the g3 <c>AutoHoleFill</c> pattern, minus the smoothed/minimal-surface tiers —
    /// future work): loops fitting a plane within
    /// <see cref="HoleFillOptions.PlanarityTolerance"/> are grouped by common plane and
    /// planar-filled together (so nested outer + hole loops become one polygon-with-holes
    /// fill); the rest fall back to the simple centroid fan when small enough and not wildly
    /// non-planar, otherwise they are reported as skipped. Returns the new mesh plus one
    /// <see cref="HoleFillOutcome"/> per boundary loop (input
    /// <see cref="HalfEdgeMesh.BoundaryLoops"/> order).
    /// </summary>
    public static HoleFillResult FillAll(HalfEdgeMesh mesh, HoleFillOptions? options = null)
    {
        var opts = options ?? HoleFillOptions.Default;
        var boundaryLoops = mesh.BoundaryLoops();
        if (boundaryLoops.Count == 0)
            return new HoleFillResult(mesh, []);

        var (positions, faces) = mesh.ToIndexed();
        var positionList = new List<Vector3d>(positions);
        int loopCount = boundaryLoops.Count;
        var loopIndices = new int[loopCount][];
        var frames = new Frame3d?[loopCount];
        var isPlanar = new bool[loopCount];
        for (int i = 0; i < loopCount; i++)
        {
            loopIndices[i] = [.. boundaryLoops[i].Select(he => he.Origin.Index)];
            if (TryFitLoopPlane(loopIndices[i], positionList, out var frame, out double maxDeviation))
            {
                frames[i] = frame;
                isPlanar[i] = maxDeviation <= opts.PlanarityTolerance;
            }
        }

        var outcomes = new HoleFillOutcome?[loopCount];

        // Group planar loops by common plane (greedy: a later planar loop joins an earlier
        // one's group when all its vertices lie on that group's fitted plane).
        var grouped = new bool[loopCount];
        for (int i = 0; i < loopCount; i++)
        {
            if (!isPlanar[i] || grouped[i])
                continue;
            var group = new List<int> { i };
            grouped[i] = true;
            var plane = frames[i]!.Value;
            for (int j = i + 1; j < loopCount; j++)
            {
                if (!isPlanar[j] || grouped[j])
                    continue;
                if (MaxPlaneDeviation(loopIndices[j], positionList, plane) <= opts.PlanarityTolerance)
                {
                    group.Add(j);
                    grouped[j] = true;
                }
            }

            int faceCountBefore = faces.Count;
            int vertexCountBefore = positionList.Count;
            try
            {
                AppendPlanarFill([.. group.Select(g => loopIndices[g])], positionList, faces,
                    opts.PlanarityTolerance);
                foreach (int g in group)
                    outcomes[g] = new HoleFillOutcome(g, loopIndices[g].Length, HoleFillMethod.Planar,
                        group.Count == 1 ? "Planar fill." : $"Planar fill ({group.Count} coplanar loops).");
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
            {
                // Back out any partial append and let each loop of the group fall through
                // to the simple path below.
                faces.RemoveRange(faceCountBefore, faces.Count - faceCountBefore);
                positionList.RemoveRange(vertexCountBefore, positionList.Count - vertexCountBefore);
                foreach (int g in group)
                {
                    grouped[g] = false;
                    isPlanar[g] = false;
                    outcomes[g] = null;
                }
            }
        }

        // Simple fallback for everything not planar-filled.
        for (int i = 0; i < loopCount; i++)
        {
            if (outcomes[i] is not null)
                continue;
            var indices = loopIndices[i];
            if (indices.Length > opts.MaxSimpleFillVertices)
            {
                outcomes[i] = Fallback(i, indices, boundaryLoops[i],
                    $"Loop has {indices.Length} vertices, above MaxSimpleFillVertices = " +
                    $"{opts.MaxSimpleFillVertices}");
            }
            else if (IsWildlyNonPlanar(indices, positionList, out double ratio))
            {
                outcomes[i] = Fallback(i, indices, boundaryLoops[i],
                    $"Loop deviates from its best-fit plane by {ratio:P0} of its extent — a " +
                    "centroid fan would self-intersect");
            }
            else
            {
                AppendSimpleFill(indices, positionList, faces);
                outcomes[i] = new HoleFillOutcome(i, indices.Length, HoleFillMethod.Simple,
                    "Simple centroid-fan fill.");
            }
        }

        return new HoleFillResult(HalfEdgeMesh.Build(positionList, faces), [.. outcomes.Select(o => o!)]);

        // Upper tiers for the loops the planar and simple fills decline. Each appends into
        // the accumulating soup, so a failure has to leave it exactly as it found it.
        HoleFillOutcome Fallback(int index, int[] indices, List<HalfEdge> loop, string why)
        {
            if (opts.Fallback == HoleFillFallback.None)
                return new HoleFillOutcome(index, indices.Length, HoleFillMethod.Skipped,
                    $"{why}; set HoleFillOptions.Fallback to fill it anyway.");
            if (indices.Length > opts.MaxMinimalFillVertices && opts.Fallback == HoleFillFallback.Minimal)
                return new HoleFillOutcome(index, indices.Length, HoleFillMethod.Skipped,
                    $"{why}; and above MaxMinimalFillVertices = {opts.MaxMinimalFillVertices}, " +
                    "whose dynamic program is cubic in the loop length.");

            int faceCountBefore = faces.Count;
            int vertexCountBefore = positionList.Count;
            try
            {
                if (opts.Fallback == HoleFillFallback.Smoothed)
                {
                    AppendSmoothedFill(indices, positionList, faces, opts.Smoothed);
                    return new HoleFillOutcome(index, indices.Length, HoleFillMethod.Smoothed,
                        $"{why}; filled with a relaxed patch.");
                }
                if (TryAppendMinimalFill(mesh, loop, indices, positionList, faces, out string? failure))
                    return new HoleFillOutcome(index, indices.Length, HoleFillMethod.Minimal,
                        $"{why}; filled with the minimum-weight triangulation of its own vertices.");
                Rewind();
                return new HoleFillOutcome(index, indices.Length, HoleFillMethod.Skipped, $"{why}; {failure}");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                Rewind();
                return new HoleFillOutcome(index, indices.Length, HoleFillMethod.Skipped,
                    $"{why}; the {opts.Fallback} fill failed: {ex.Message}");
            }

            void Rewind()
            {
                faces.RemoveRange(faceCountBefore, faces.Count - faceCountBefore);
                positionList.RemoveRange(vertexCountBefore, positionList.Count - vertexCountBefore);
            }
        }
    }

    // ---- internals ----

    /// <summary>Checks the loop is a closed chain of boundary half-edges of <paramref name="mesh"/>
    /// and returns its origin vertex indices in walk order.</summary>
    private static int[] ValidatedLoopIndices(HalfEdgeMesh mesh, IReadOnlyList<HalfEdge> loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        if (loop.Count < 3)
            throw new ArgumentException($"A boundary loop needs at least 3 half-edges; got {loop.Count}.", nameof(loop));
        for (int i = 0; i < loop.Count; i++)
        {
            if (!ReferenceEquals(loop[i].Mesh, mesh))
                throw new ArgumentException("Loop half-edge belongs to a different mesh.", nameof(loop));
            if (!loop[i].IsBoundary)
                throw new ArgumentException($"Half-edge {loop[i].Index} is not a boundary half-edge.", nameof(loop));
            if (loop[i].Next.Index != loop[(i + 1) % loop.Count].Index)
                throw new ArgumentException("Half-edges do not chain into a closed boundary loop in walk order.", nameof(loop));
        }
        return [.. loop.Select(he => he.Origin.Index)];
    }

    /// <summary>Centroid-fan fill: appends the centroid vertex (unless the loop is a triangle)
    /// and fan faces following the boundary walk winding.</summary>
    private static void AppendSimpleFill(int[] loop, List<Vector3d> positions, List<int[]> faces)
    {
        int n = loop.Length;
        if (n == 3)
        {
            faces.Add([loop[0], loop[1], loop[2]]);
            return;
        }

        var centroid = Vector3d.Zero;
        for (int i = 0; i < n; i++)
            centroid += positions[loop[i]];
        centroid /= n;

        int c = positions.Count;
        positions.Add(centroid);
        for (int i = 0; i < n; i++)
            faces.Add([loop[i], loop[(i + 1) % n], c]);
    }

    // ---- minimal (minimum-weight) fill ----

    /// <summary>
    /// Weight of a (partial) triangulation: the largest dihedral angle anywhere in it and its
    /// total area. Combining two sub-triangulations takes the max of the angles and the sum of
    /// the areas; comparison is lexicographic (flattest first, smallest second) — Liepa's
    /// weight, which is what makes the fill reconstruct creases rather than balloon.
    /// </summary>
    private readonly record struct FillWeight(double MaxDihedral, double Area)
    {
        public static FillWeight Zero => new(0, 0);
        public static FillWeight Infinite => new(double.PositiveInfinity, double.PositiveInfinity);
        public bool IsInfinite => double.IsPositiveInfinity(MaxDihedral);
        public static FillWeight operator +(in FillWeight a, in FillWeight b) =>
            new(Math.Max(a.MaxDihedral, b.MaxDihedral), a.Area + b.Area);
        public bool IsBetterThan(in FillWeight other) =>
            MaxDihedral < other.MaxDihedral || (MaxDihedral == other.MaxDihedral && Area < other.Area);
    }

    /// <summary>
    /// Angle between two triangle normals, from their raw (unnormalized) cross products.
    /// <c>atan2(|a×b|, a·b)</c> is exact for any magnitudes and needs no normalization and no
    /// epsilon — a degenerate triangle gives 0 rather than a NaN, and the area term breaks the
    /// resulting tie.
    /// </summary>
    private static double NormalAngle(in Vector3d a, in Vector3d b) =>
        Math.Atan2(a.Cross(b).Length, a.Dot(b));

    private static bool TryAppendMinimalFill(HalfEdgeMesh mesh, IReadOnlyList<HalfEdge> loop,
        int[] indices, List<Vector3d> positions, List<int[]> faces, out string? failure)
    {
        failure = null;
        int n = indices.Length;
        if (n == 3)
        {
            faces.Add([indices[0], indices[1], indices[2]]);
            return true;
        }

        // Raw normal of the existing face across each rim edge (loop[i] runs indices[i] ->
        // indices[i+1]; its twin is the interior half-edge).
        var rimNormals = new Vector3d[n];
        for (int i = 0; i < n; i++)
            rimNormals[i] = mesh.FaceNormalRaw(mesh.HeFace(mesh.HeTwin(loop[i].Index)));

        // Chords already present elsewhere in the mesh cannot be used: the fill would give
        // that edge a third face.
        var loopPosition = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++)
            loopPosition[indices[i]] = i;
        var existing = new HashSet<(int, int)>();
        for (int i = 0; i < n; i++)
        {
            foreach (var he in mesh.GetVertex(indices[i]).OutgoingHalfEdges())
            {
                if (loopPosition.TryGetValue(he.Destination.Index, out int j))
                    existing.Add((Math.Min(i, j), Math.Max(i, j)));
            }
        }

        var weight = new FillWeight[n, n];
        var split = new int[n, n];
        for (int i = 0; i + 1 < n; i++)
            weight[i, i + 1] = FillWeight.Zero;

        for (int gap = 2; gap < n; gap++)
        {
            for (int i = 0; i + gap < n; i++)
            {
                int j = i + gap;
                bool isRimEdge = i == 0 && j == n - 1; // the closing edge, not a chord
                if (!isRimEdge && existing.Contains((i, j)))
                {
                    weight[i, j] = FillWeight.Infinite;
                    split[i, j] = -1;
                    continue;
                }

                var best = FillWeight.Infinite;
                int bestSplit = -1;
                for (int m = i + 1; m < j; m++)
                {
                    if (weight[i, m].IsInfinite || weight[m, j].IsInfinite)
                        continue;
                    var candidate = weight[i, m] + weight[m, j] +
                        TriangleWeight(positions, indices, rimNormals, split, i, m, j, isRimEdge, n);
                    if (candidate.IsBetterThan(best))
                    {
                        best = candidate;
                        bestSplit = m;
                    }
                }
                weight[i, j] = best;
                split[i, j] = bestSplit;
            }
        }

        if (split[0, n - 1] < 0)
        {
            failure = $"No manifold triangulation of the {n}-vertex loop exists: every candidate " +
                      "chord already joins two of its vertices elsewhere in the mesh.";
            return false;
        }

        var stack = new Stack<(int I, int J)>();
        stack.Push((0, n - 1));
        while (stack.Count > 0)
        {
            var (i, j) = stack.Pop();
            int m = split[i, j];
            faces.Add([indices[i], indices[m], indices[j]]);
            if (m > i + 1)
                stack.Push((i, m));
            if (j > m + 1)
                stack.Push((m, j));
        }
        return true;
    }

    /// <summary>
    /// Weight contributed by triangle (i, m, j): its area, and the largest dihedral it makes
    /// with the triangle already across each of its edges — an existing mesh face for a rim
    /// edge, the sub-triangulation's own outermost triangle for a chord.
    /// </summary>
    private static FillWeight TriangleWeight(List<Vector3d> positions, int[] indices,
        Vector3d[] rimNormals, int[,] split, int i, int m, int j, bool closingIsRim, int n)
    {
        var pi = positions[indices[i]];
        var pm = positions[indices[m]];
        var pj = positions[indices[j]];
        var normal = (pm - pi).Cross(pj - pi);
        double area = normal.Length * 0.5;

        double dihedral = Math.Max(
            NormalAngle(normal, Across(i, m)),
            NormalAngle(normal, Across(m, j)));
        if (closingIsRim)
            dihedral = Math.Max(dihedral, NormalAngle(normal, rimNormals[n - 1]));
        return new FillWeight(dihedral, area);

        Vector3d Across(int a, int b)
        {
            if (b == a + 1)
                return rimNormals[a];
            int s = split[a, b];
            var pa = positions[indices[a]];
            return (positions[indices[s]] - pa).Cross(positions[indices[b]] - pa);
        }
    }

    // ---- smoothed (relaxed membrane) fill ----

    private static void AppendSmoothedFill(int[] indices, List<Vector3d> positions,
        List<int[]> faces, SmoothedHoleFillOptions options)
    {
        int n = indices.Length;
        double target = options.TargetEdgeLength ?? MeanRimEdgeLength(indices, positions);

        // Seed patch: the loop plus a centroid fan, as a standalone mesh. Its boundary IS the
        // hole rim, so the remesher's PreserveBoundary pins exactly the vertices that have to
        // come back unchanged.
        var patchPositions = new List<Vector3d>(n + 1);
        var centroid = Vector3d.Zero;
        for (int i = 0; i < n; i++)
        {
            patchPositions.Add(positions[indices[i]]);
            centroid += positions[indices[i]];
        }
        patchPositions.Add(centroid / n);
        var patchFaces = new List<int[]>(n);
        for (int i = 0; i < n; i++)
            patchFaces.Add([i, (i + 1) % n, n]);

        var patch = Remesher.Remesh(HalfEdgeMesh.Build(patchPositions, patchFaces),
            new RemeshOptions(target)
            {
                Iterations = options.Iterations,
                SmoothSpeed = options.SmoothSpeed,
                PreserveBoundary = true,
                SplitFixedEdges = false, // the rim must come back vertex for vertex
                FeatureAngleDegrees = 0, // a fill has no features to protect
            }).Mesh;

        // Stitch: rim vertices map back to the original mesh by exact position (they were
        // pinned, so they never moved a bit); interior vertices are appended.
        var rim = new Dictionary<Vector3d, int>(n);
        for (int i = 0; i < n; i++)
            rim[positions[indices[i]]] = indices[i];

        var (finalPositions, finalFaces) = patch.ToIndexed();
        var map = new int[finalPositions.Length];
        var onRim = new bool[finalPositions.Length];
        foreach (var loop in patch.BoundaryLoops())
        {
            foreach (var he in loop)
                onRim[he.Origin.Index] = true;
        }
        for (int v = 0; v < finalPositions.Length; v++)
        {
            if (onRim[v])
            {
                if (!rim.TryGetValue(finalPositions[v], out int original))
                    throw new InvalidOperationException(
                        "The relaxed patch came back with a rim vertex that is not one of the hole's " +
                        "(a pinned vertex moved, or a rim edge was split) — the fill would not weld.");
                map[v] = original;
            }
            else
            {
                map[v] = positions.Count;
                positions.Add(finalPositions[v]);
            }
        }
        foreach (var face in finalFaces)
        {
            var mapped = new int[face.Length];
            for (int k = 0; k < face.Length; k++)
                mapped[k] = map[face[k]];
            faces.Add(mapped);
        }
    }

    private static double MeanRimEdgeLength(int[] indices, List<Vector3d> positions)
    {
        double sum = 0;
        for (int i = 0; i < indices.Length; i++)
            sum += (positions[indices[(i + 1) % indices.Length]] - positions[indices[i]]).Length;
        return sum / indices.Length;
    }

    /// <summary>Best-fit plane and max orthogonal deviation for one loop; false when the
    /// points do not determine a plane (collinear/coincident).</summary>
    private static bool TryFitLoopPlane(int[] loop, List<Vector3d> positions,
        out Frame3d frame, out double maxDeviation)
    {
        var points = new Vector3d[loop.Length];
        for (int i = 0; i < loop.Length; i++)
            points[i] = positions[loop[i]];
        try
        {
            frame = Fitting3d.FitPlane(points);
        }
        catch (ArgumentException)
        {
            frame = default;
            maxDeviation = double.PositiveInfinity;
            return false;
        }
        maxDeviation = MaxPlaneDeviation(loop, positions, frame);
        return true;
    }

    private static double MaxPlaneDeviation(int[] loop, List<Vector3d> positions, in Frame3d plane)
    {
        double max = 0;
        for (int i = 0; i < loop.Length; i++)
            max = Math.Max(max, Math.Abs(plane.ToLocal(positions[loop[i]]).Z));
        return max;
    }

    /// <summary>The simple-fill guard: large loop AND plane deviation above a fixed fraction
    /// of the loop's own extent (scale-free, see the constants above).</summary>
    private static bool IsWildlyNonPlanar(int[] loop, List<Vector3d> positions, out double ratio)
    {
        ratio = 0;
        if (loop.Length <= SimpleGuardVertexCount)
            return false;
        if (!TryFitLoopPlane(loop, positions, out var frame, out double maxDeviation))
            return false; // degenerate (collinear) loop: nothing sensible to guard against
        var bounds = Aabb.Empty;
        for (int i = 0; i < loop.Length; i++)
            bounds = bounds.Union(positions[loop[i]]);
        double extent = bounds.Size.Length;
        if (extent <= 0)
            return false;
        ratio = maxDeviation / extent;
        return ratio > SimpleGuardDeviationRatio;
    }

    /// <summary>
    /// Core planar fill for one coplanar loop group: fit a common plane, project, sort loops
    /// into outers (CCW after projection) and holes (CW), assign each hole to its smallest
    /// containing outer, ear-clip each outer with its holes, and map triangles back to mesh
    /// vertex indices — re-expanding any chord the triangulator's exactly-collinear-vertex
    /// filtering produced (the seam-zip lesson: neighbors still reference the dropped
    /// vertices, so a chord would be an unfillable crack).
    /// </summary>
    private static void AppendPlanarFill(IReadOnlyList<int[]> loops, List<Vector3d> positions,
        List<int[]> faces, double planarityTolerance)
    {
        // One common plane over all loop vertices.
        var allPoints = new List<Vector3d>();
        foreach (var loop in loops)
        {
            foreach (int v in loop)
                allPoints.Add(positions[v]);
        }
        Frame3d plane;
        try
        {
            plane = Fitting3d.FitPlane(allPoints);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException("The loop vertices do not determine a plane.", nameof(loops), ex);
        }
        foreach (var loop in loops)
        {
            double deviation = MaxPlaneDeviation(loop, positions, plane);
            if (deviation > planarityTolerance)
                throw new ArgumentException(
                    $"Loop deviates {deviation:G3} from the common best-fit plane " +
                    $"(planarity tolerance {planarityTolerance:G3}).", nameof(loops));
        }

        // Project every loop. Boundary walks keep the hole region on a fixed side, so after
        // orienting the plane basis to make the dominant (largest |area|) loop CCW, outer
        // boundaries are exactly the CCW loops and holes the CW ones.
        var projected = new Vector2d[loops.Count][];
        var areas = new double[loops.Count];
        for (int i = 0; i < loops.Count; i++)
        {
            var loop = loops[i];
            var p = new Vector2d[loop.Length];
            for (int k = 0; k < loop.Length; k++)
            {
                var local = plane.ToLocal(positions[loop[k]]);
                p[k] = (local.X, local.Y);
            }
            projected[i] = p;
            areas[i] = PolygonTriangulator.SignedArea(p);
        }

        int dominant = 0;
        for (int i = 1; i < loops.Count; i++)
        {
            if (Math.Abs(areas[i]) > Math.Abs(areas[dominant]))
                dominant = i;
        }
        if (areas[dominant] < 0)
        {
            // Mirror the basis (v → −v) so the dominant loop walks CCW; the triangulator
            // normalizes output triangles CCW in 2D, so CCW here = boundary walk winding in 3D.
            for (int i = 0; i < loops.Count; i++)
            {
                var p = projected[i];
                for (int k = 0; k < p.Length; k++)
                    p[k] = (p[k].X, -p[k].Y);
                areas[i] = -areas[i];
            }
        }

        var outers = new List<int>();
        var holes = new List<int>();
        for (int i = 0; i < loops.Count; i++)
        {
            if (areas[i] > 0)
                outers.Add(i);
            else if (areas[i] < 0)
                holes.Add(i);
            else
                throw new ArgumentException($"Loop {i} projects to zero area on the common plane.", nameof(loops));
        }

        // Each hole belongs to the smallest CCW outer containing it.
        var holesOfOuter = outers.ToDictionary(o => o, _ => new List<int>());
        foreach (int h in holes)
        {
            int best = -1;
            foreach (int o in outers)
            {
                if (ContainsPoint(projected[o], projected[h][0]) &&
                    (best < 0 || areas[o] < areas[best]))
                    best = o;
            }
            if (best < 0)
                throw new ArgumentException(
                    $"Hole loop {h} is not contained in any outer loop on the common plane.", nameof(loops));
            holesOfOuter[best].Add(h);
        }

        foreach (int o in outers)
        {
            var holeIds = holesOfOuter[o];
            // Concatenated-ring index space: [outer..., hole0..., hole1...].
            var ringLoops = new List<int[]> { loops[o] };
            var ringProjected = new List<IReadOnlyList<Vector2d>> { projected[o] };
            foreach (int h in holeIds)
            {
                ringLoops.Add(loops[h]);
                ringProjected.Add(projected[h]);
            }

            var triangles = holeIds.Count == 0
                ? PolygonTriangulator.Triangulate(projected[o])
                : PolygonTriangulator.TriangulateWithHoles(projected[o], [.. ringProjected.Skip(1)]);

            AppendTrianglesWithChordZip(ringLoops, triangles, faces);
        }
    }

    /// <summary>Even-odd point-in-polygon test in 2D.</summary>
    private static bool ContainsPoint(Vector2d[] polygon, in Vector2d point)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            if (a.Y > point.Y != b.Y > point.Y &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// Maps triangulator output (indices into the concatenated rings) back to mesh vertex
    /// indices. Earcut filters exactly-collinear ring vertices; any triangle edge that is a
    /// chord spanning a run of dropped vertices <b>within one ring</b> is expanded back into
    /// the ring's full vertex run, so the fill welds exactly against the neighbors that still
    /// reference them (same recovery as <see cref="MeshPlaneCut"/>'s cap, generalized to be
    /// ring-aware: bridge edges between outer and hole rings are never chords).
    /// </summary>
    private static void AppendTrianglesWithChordZip(IReadOnlyList<int[]> ringLoops,
        List<(int A, int B, int C)> triangles, List<int[]> faces)
    {
        int total = 0;
        foreach (var ring in ringLoops)
            total += ring.Length;

        // Flatten ring membership: for each concatenated index, its ring, position, and mesh vertex.
        var ringOf = new int[total];
        var posInRing = new int[total];
        var meshVertex = new int[total];
        int offset = 0;
        for (int r = 0; r < ringLoops.Count; r++)
        {
            var ring = ringLoops[r];
            for (int k = 0; k < ring.Length; k++)
            {
                ringOf[offset + k] = r;
                posInRing[offset + k] = k;
                meshVertex[offset + k] = ring[k];
            }
            offset += ring.Length;
        }

        var used = new bool[total];
        foreach (var (a, b, c) in triangles)
            used[a] = used[b] = used[c] = true;
        bool anyDropped = Array.IndexOf(used, false) >= 0;

        foreach (var (a, b, c) in triangles)
        {
            if (!anyDropped)
            {
                faces.Add([meshVertex[a], meshVertex[b], meshVertex[c]]);
                continue;
            }

            var polygon = new List<int>(6);
            AppendEdge(a, b);
            AppendEdge(b, c);
            AppendEdge(c, a);
            faces.Add([.. polygon]);

            void AppendEdge(int from, int to)
            {
                polygon.Add(meshVertex[from]);
                if (ringOf[from] != ringOf[to])
                    return; // hole bridge — never a chord over dropped ring vertices
                var ring = ringLoops[ringOf[from]];
                int n = ring.Length;
                int start = from - posInRing[from]; // concatenated index of the ring's first vertex
                int gap = (posInRing[to] - posInRing[from] + n) % n;
                if (gap <= 1)
                    return; // genuine ring edge
                for (int k = 1; k < gap; k++)
                {
                    if (used[start + (posInRing[from] + k) % n])
                        return; // interior diagonal, not a chord over dropped vertices
                }
                for (int k = 1; k < gap; k++)
                    polygon.Add(ring[(posInRing[from] + k) % n]);
            }
        }
    }
}
