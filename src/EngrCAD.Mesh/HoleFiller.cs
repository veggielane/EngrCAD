using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>How a boundary loop was (or was not) filled by <see cref="HoleFiller.FillAll"/>.</summary>
public enum HoleFillMethod
{
    /// <summary>The loop (possibly together with coplanar sibling loops) was ear-clipped in its best-fit plane.</summary>
    Planar,
    /// <summary>The loop was filled with a centroid-vertex triangle fan.</summary>
    Simple,
    /// <summary>The loop was left open; <see cref="HoleFillOutcome.Message"/> says why.</summary>
    Skipped,
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
                outcomes[i] = new HoleFillOutcome(i, indices.Length, HoleFillMethod.Skipped,
                    $"Loop has {indices.Length} vertices, above MaxSimpleFillVertices = " +
                    $"{opts.MaxSimpleFillVertices}; smoothed fills are future work.");
            }
            else if (IsWildlyNonPlanar(indices, positionList, out double ratio))
            {
                outcomes[i] = new HoleFillOutcome(i, indices.Length, HoleFillMethod.Skipped,
                    $"Loop deviates from its best-fit plane by {ratio:P0} of its extent — a " +
                    "centroid fan would self-intersect; smoothed fills are future work.");
            }
            else
            {
                AppendSimpleFill(indices, positionList, faces);
                outcomes[i] = new HoleFillOutcome(i, indices.Length, HoleFillMethod.Simple,
                    "Simple centroid-fan fill.");
            }
        }

        return new HoleFillResult(HalfEdgeMesh.Build(positionList, faces), [.. outcomes.Select(o => o!)]);
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
