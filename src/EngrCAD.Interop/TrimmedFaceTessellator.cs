using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Interop;

/// <summary>
/// Parameter-space triangulation for trimmed faces on curved surfaces — faces whose loops
/// do not cover the surface's natural grid domain (fragments produced by
/// <see cref="FaceSplitter.SplitByCurve"/>, e.g. a bore wall cut through by a slot).
/// The loops' shared edge-polyline samples are pulled into (u, v) space; non-wrapping
/// regions are ear-clipped with an exact-coordinate clipper, band-like regions (loops
/// winding the periodic direction — rings subdivided into arcs, which the grid path can
/// no longer match) are strip-zipped between their two ring chains (or fanned to the
/// pole). Oversized interior edges are then midpoint-split down to the natural grid
/// density with new vertices evaluated on the exact surface. Boundary vertices are the
/// exact shared edge samples — never re-evaluated approximations — so neighboring faces
/// weld without cracks.
///
/// The clipper is deliberately not <see cref="Mesh.PolygonTriangulator"/> (earcut):
/// iso-parameter boundary runs (ring arcs at constant v) are exactly collinear in uv, and
/// earcut filters exactly-collinear vertices — on a curved surface a dropped sample opens
/// a crack no zip pass can repair, because uv-collinear points are not 3D-collinear.
/// This clipper keeps every vertex, never emits zero-area uv triangles, and treats points
/// lying exactly on a candidate ear as blocking so no diagonal ever passes through a
/// vertex.
///
/// It is also the LAST resort, not the first: a region whose loop is a BAND between two
/// boundary chains monotone in one parameter (every mitered rim-fillet band, every
/// partial cylinder or cone fragment) is zipped chain-to-chain instead — see
/// <see cref="TriangulateStrip"/>. Ear-clipping such a region is not merely wasteful, it
/// is WRONG to look at: the clipper's shortest-diagonal rule eats the dense boundary
/// chains first, and three consecutive samples of a smooth boundary curve span a sliver
/// whose normal is <c>T × K</c> — the curve's binormal — not the surface's. Where the
/// boundary's geodesic curvature passes through zero (a miter ellipse at the top tangency
/// of a fillet) that binormal is perpendicular to the surface normal and its sign is
/// rounding noise, so half the slivers face inward. That is exactly the folded, dark
/// "lens" the mitered fillet corners used to render as.
/// </summary>
internal static class TrimmedFaceTessellator
{
    /// <inheritdoc cref="TryTessellate(BrepFace, Dictionary{BrepEdge, List{Vector3d}}, int, int, List{IReadOnlyList{Vector3d}}, out string?)"/>
    public static bool TryTessellate(
        BrepFace face,
        Dictionary<BrepEdge, List<Vector3d>> edgePolylines,
        int segmentsPerCircle,
        int curveSamples,
        List<IReadOnlyList<Vector3d>> polygons) =>
        TryTessellate(face, edgePolylines, segmentsPerCircle, curveSamples, polygons, out _);

    /// <summary>
    /// Attempts to tessellate a trimmed face, appending triangles to
    /// <paramref name="polygons"/> (counter-clockwise in uv, i.e. along the surface
    /// normal — the caller flips reversed faces). Returns false without touching
    /// <paramref name="polygons"/> when the face cannot be handled: a loop point fails
    /// inverse evaluation, the winding structure is unsupported (a pole-bounded band
    /// with extra hole loops, |winding| &gt; 1), or clipping gets stuck on degenerate
    /// input. Two-ring bands with interior hole loops (e.g. a cross-drilled bore wall)
    /// are unrolled at a seam clear of the holes and ear-clipped with hole bridging.
    /// <para><paramref name="failure"/> names the reason on a false return, so the caller
    /// can refuse loudly instead of falling back to a grid that would silently produce an
    /// open mesh.</para>
    /// </summary>
    public static bool TryTessellate(
        BrepFace face,
        Dictionary<BrepEdge, List<Vector3d>> edgePolylines,
        int segmentsPerCircle,
        int curveSamples,
        List<IReadOnlyList<Vector3d>> polygons,
        out string? failure)
    {
        failure = null;
        var surface = face.Surface;
        double period = FaceGeometry.PeriodU(surface);

        // 1. Pull every loop's shared edge samples into parameter space, unwrapping the
        //    periodic u direction along the loop, and record each loop's winding number.
        var loopUv = new List<List<Vector2d>>(face.Loops.Count);
        var loopPoints = new List<List<Vector3d>>(face.Loops.Count);
        var windings = new List<int>(face.Loops.Count);
        foreach (var loop in face.Loops)
        {
            var points = BRepTessellator.LoopPolyline(loop, edgePolylines);
            if (points.Count < 3)
            {
                failure = $"a loop has only {points.Count} sample(s)";
                return false;
            }
            var uv = new List<Vector2d>(points.Count);
            foreach (var p in points)
            {
                if (!surface.TryProjectPoint(p, out var q, FaceGeometry.InverseEvaluationTolerance))
                {
                    failure = $"the loop point {p} does not pull back onto the face's surface";
                    return false;
                }
                if (period > 0 && uv.Count > 0)
                    q = new Vector2d(q.X + period * Math.Round((uv[^1].X - q.X) / period), q.Y);
                uv.Add(q);
            }
            int winding = period > 0 ? (int)Math.Round((uv[^1].X - uv[0].X) / period) : 0;
            if (Math.Abs(winding) > 1)
            {
                failure = $"a loop winds the periodic direction {winding} times (only 0 or +-1 is supported)";
                return false;
            }
            loopUv.Add(uv);
            loopPoints.Add(points);
            windings.Add(winding);
        }

        var (stepU, stepV) = NaturalSteps(surface, segmentsPerCircle, curveSamples);
        var uvAll = new List<Vector2d>();
        var pointsAll = new List<Vector3d>();
        var boundaryEdges = new HashSet<(int, int)>();
        List<(int A, int B, int C)>? triangles;
        if (windings.All(w => w == 0))
        {
            // A band between two paired boundary chains zips; anything else ear-clips.
            triangles =
                TriangulateStrip(loopUv, loopPoints, stepU, stepV, uvAll, pointsAll, boundaryEdges)
                ?? TriangulateRegion(loopUv, loopPoints, period, uvAll, pointsAll, boundaryEdges);
        }
        else if (windings.Any(w => w == 0))
        {
            // Band with extra hole loops: cut the band open at a seam clear of every
            // hole and ear-clip the unrolled rectangle-with-holes.
            triangles = TriangulateBandWithHoles(period, loopUv, loopPoints, windings, uvAll, pointsAll, boundaryEdges);
        }
        else
        {
            // Band-like: winding loops zip as strips (or fan to the pole).
            triangles = TriangulateBand(surface, period, loopUv, loopPoints, windings, uvAll, pointsAll, boundaryEdges);
        }
        if (triangles is null || triangles.Count == 0)
        {
            failure = "the loops' winding structure is unsupported, or triangulation stalled on degenerate input";
            return false;
        }

        // 2. Refine oversized interior edges to the natural grid density so the surface
        //    keeps its curvature between distant boundary samples. A refinement that
        //    cannot converge must fail the whole face — emitting a partially refined
        //    set would break the no-touch-on-failure contract above.
        if (!Refine(surface, period, uvAll, pointsAll, triangles, boundaryEdges, stepU, stepV))
        {
            failure = "curvature refinement did not converge";
            return false;
        }

        foreach (var (a, b, c) in triangles)
            polygons.Add([pointsAll[a], pointsAll[b], pointsAll[c]]);
        return true;
    }

    // ---- non-wrapping bands: strip zipping between the paired boundary chains ----

    /// <summary>
    /// Triangulates a single-loop trimmed region whose boundary is a BAND: two chains
    /// monotone in one surface parameter, joined at each end by a single rung. That is
    /// the shape of every mitered rim-fillet band (two miter curves across a quarter
    /// cylinder, closed by the top and bottom tangency lines) and of every partial
    /// cylinder or cone fragment. The two chains are already paired by construction, so
    /// the correct triangulation is a monotone zip — the same merge walk
    /// <see cref="TriangulateBand"/> uses on periodic rings, minus the period closure.
    /// <para>Returns null when the loop is not a band (more than one loop, more or fewer
    /// than two rungs, a non-monotone chain, or a zip that would fold), leaving the
    /// shared vertex arrays untouched so the caller can ear-clip instead.</para>
    /// </summary>
    private static List<(int A, int B, int C)>? TriangulateStrip(
        List<List<Vector2d>> loopUv,
        List<List<Vector3d>> loopPoints,
        double stepU,
        double stepV,
        List<Vector2d> uvAll,
        List<Vector3d> pointsAll,
        HashSet<(int, int)> boundaryEdges)
    {
        if (loopUv.Count != 1)
            return null;
        var uv = loopUv[0];
        int n = uv.Count;
        if (n < 4)
            return null;

        // Walk the loop counter-clockwise so the run with INCREASING key is always the
        // strip's first side: on a CCW loop the interior lies to the left of travel.
        var order = new int[n];
        bool alreadyCcw = FaceGeometry.LoopSignedArea(uv) > 0;
        for (int i = 0; i < n; i++)
            order[i] = alreadyCcw ? i : n - 1 - i;

        // The chains run along the parameter that carries the natural sampling, so the
        // rungs lie across the direction whose chords are already exact (an extrusion is
        // ruled in v) or at least coarser. Getting this backwards would fan a 2-sample
        // rung against a 25-sample chain.
        bool uFirst = StepSpan(uv, alongU: true, stepU) >= StepSpan(uv, alongU: false, stepV);
        foreach (bool alongU in (ReadOnlySpan<bool>)[uFirst, !uFirst])
        {
            // The stack sweep first: it is the correct triangulation of ANY monotone
            // region, so it covers the two shapes the rung-counting split cannot see —
            // an end sampled at more than two points (a curved cross edge) and two
            // chains meeting at a point (a rung of no steps at all). The rung split
            // stays behind it because a band whose chains are monotone in neither
            // parameter is not a band, and ear clipping is the honest answer there.
            if ((SweepStrip(uv, order, alongU) ?? ZipStrip(uv, order, alongU)) is { } local)
            {
                int start = uvAll.Count;
                for (int i = 0; i < n; i++)
                {
                    uvAll.Add(uv[order[i]]);
                    pointsAll.Add(loopPoints[0][order[i]]);
                }
                // Every loop chord is shared geometry: refinement must never split one.
                for (int i = 0; i < n; i++)
                    boundaryEdges.Add(EdgeKey(start + i, start + (i + 1) % n));
                return [.. local.Select(t => (start + t.A, start + t.B, start + t.C))];
            }
        }
        return null;
    }

    /// <summary>The rung-counting split followed by the merge zip, as one step.</summary>
    private static List<(int A, int B, int C)>? ZipStrip(List<Vector2d> uv, int[] order, bool alongU) =>
        TrySplitBand(uv, order, alongU, out var rising, out var falling)
            ? ZipBand(uv, order, rising, falling, alongU)
            : null;

    /// <summary>
    /// Triangulates a single-loop band by splitting it at its extreme-key vertices into
    /// two chains and running <see cref="SweepMonotone"/> over them — the textbook stack
    /// sweep, which is correct on ANY monotone polygon rather than only on one whose ends
    /// are single rungs. Returns null (so the caller falls back) when either chain runs
    /// backwards in the key, when one side has no interior vertex at all, or when the
    /// sweep hits a degeneracy.
    /// <para>Two shapes the rung-counting split refuses fall out of this for free.</para>
    /// <list type="bullet">
    /// <item>A <b>rung sampled at more than two points</b> — a curved cross edge — is
    /// several consecutive vertices at one key. The sweep stacks them (collinear is
    /// deliberately not a turn, so nothing pops between them) and then fans them from the
    /// OPPOSITE chain's first vertex when the funnel closes, which is exactly the
    /// treatment that avoids the zero-area facets fanning them among themselves would
    /// produce. The tie-breaking below is what makes that work: the whole tied run has to
    /// land on ONE chain, or the merge interleaves the two sides at equal keys and the
    /// sweep is asked to triangulate collinear points.</item>
    /// <item>A <b>band whose chains meet at a point</b> — a rung of no steps — is just a
    /// monotone polygon with a single extreme vertex, which is where the sweep starts
    /// anyway.</item>
    /// </list>
    /// <para>Neither shape is reachable from the <c>Shape</c> API today: the constructions
    /// that would make one (a spherical band between two meridian cuts, a cone fragment
    /// through the apex) are refused earlier by the exact B-Rep boolean. They are covered
    /// by direct unit tests on hand-built faces — see <c>TrimmedBandGapTests</c>.</para>
    /// </summary>
    private static List<(int A, int B, int C)>? SweepStrip(List<Vector2d> uv, int[] order, bool alongU)
    {
        int n = order.Length;

        // Sweep coordinates: the key first, the cross direction second. For a v-keyed band
        // that is (v, -u) — a rotation, NOT a coordinate swap, because a swap is a
        // reflection and would invert every facet the sweep orients by area sign.
        var sweep = new List<Vector2d>(n);
        for (int i = 0; i < n; i++)
        {
            var p = uv[order[i]];
            sweep.Add(alongU ? p : new Vector2d(p.Y, -p.X));
        }

        double min = sweep.Min(p => p.X), max = sweep.Max(p => p.X);
        double extent = max - min;
        if (!(extent > 0))
            return null;
        // The same relative expression of the 1e-6 inverse-evaluation tier TrySplitBand
        // uses: the two ends of a rung pull back to the same key to about that much.
        double flat = FaceGeometry.InverseEvaluationTolerance * extent;

        // The extreme vertices, tie-broken so a whole tied run sits on one chain: the LAST
        // of the tied minimum run and the FIRST of the tied maximum run, both in traversal
        // order, which puts every other member of each run on the other chain.
        int lo = -1, hi = -1;
        for (int i = 0; i < n; i++)
        {
            if (sweep[i].X - min <= flat && sweep[(i + 1) % n].X - min > flat)
                lo = i;
            if (max - sweep[i].X <= flat && max - sweep[(i + n - 1) % n].X > flat)
                hi = i;
        }
        if (lo < 0 || hi < 0 || lo == hi)
            return null; // a tied run wraps the whole loop: not a band

        var forward = Walk(lo, hi, +1, n);
        var backward = Walk(lo, hi, -1, n);
        if (!NonDecreasingU(sweep, forward) || !NonDecreasingU(sweep, backward))
            return null;

        // Both walks carry the two shared extremes; the sweep wants each vertex once, so
        // whichever chain is the upper one contributes only its interior.
        bool forwardIsLower =
            forward.Average(i => sweep[i].Y) <= backward.Average(i => sweep[i].Y);
        var lower = forwardIsLower ? forward : backward;
        var other = forwardIsLower ? backward : forward;
        if (other.Count <= 2)
            return null; // one side is a single edge: no band to sweep
        var upper = other.GetRange(1, other.Count - 2);

        var triangles = new List<(int A, int B, int C)>();
        return SweepMonotone(triangles, sweep, lower, upper) ? triangles : null;

        static List<int> Walk(int from, int to, int step, int n)
        {
            var chain = new List<int>();
            for (int i = from; ; i = (i + step + n) % n)
            {
                chain.Add(i);
                if (i == to)
                    return chain;
            }
        }
    }

    /// <summary>How many natural grid steps the loop spans in one parameter; zero where
    /// the surface is ruled in that direction (an infinite step, chords exact).</summary>
    private static double StepSpan(List<Vector2d> uv, bool alongU, double step)
    {
        if (!double.IsFinite(step) || step <= 0)
            return 0;
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var p in uv)
        {
            double key = alongU ? p.X : p.Y;
            min = Math.Min(min, key);
            max = Math.Max(max, key);
        }
        return (max - min) / step;
    }

    /// <summary>
    /// Splits a CCW loop into the two chains of a band: a run of strictly rising key
    /// steps and a run of strictly falling ones, separated by exactly two FLAT steps —
    /// the rungs that close the band at each end. Vertex indices are loop positions in
    /// <paramref name="order"/>, each chain including both of its rung vertices.
    /// <para>Deliberately strict: a rung sampled at more than two points (a curved cross
    /// edge) shows up as several flat steps and is refused rather than fanned, because
    /// fanning collinear rung samples would emit the zero-area triangles this whole path
    /// exists to avoid. Such a face ear-clips instead.</para>
    /// </summary>
    private static bool TrySplitBand(
        List<Vector2d> uv, int[] order, bool alongU, out List<int> rising, out List<int> falling)
    {
        rising = [];
        falling = [];
        int n = order.Length;

        double Key(int i)
        {
            var p = uv[order[i]];
            return alongU ? p.X : p.Y;
        }

        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (int i = 0; i < n; i++)
        {
            min = Math.Min(min, Key(i));
            max = Math.Max(max, Key(i));
        }
        double extent = max - min;
        if (!(extent > 0))
            return false;

        // The 1e-6 inverse-evaluation tier expressed RELATIVELY, in parameter units: the
        // two ends of a rung pull back to the same parameter to about that much, while a
        // genuine chain step spans a natural grid step — orders of magnitude larger. An
        // absolute epsilon here would be meaningless: u and v carry no model units.
        double flat = FaceGeometry.InverseEvaluationTolerance * extent;

        var signs = new int[n];
        int rungs = 0;
        for (int i = 0; i < n; i++)
        {
            double delta = Key((i + 1) % n) - Key(i);
            signs[i] = delta > flat ? 1 : delta < -flat ? -1 : 0;
            if (signs[i] == 0)
                rungs++;
        }
        if (rungs != 2)
            return false;

        int first = Array.IndexOf(signs, 0);
        int second = Array.LastIndexOf(signs, 0);
        var between = Run(signs, first, second, n);
        var around = Run(signs, second, first, n);
        if (between is null || around is null || between.Value.Sign == around.Value.Sign)
            return false;

        var risingRun = between.Value.Sign > 0 ? between.Value : around.Value;
        var fallingRun = between.Value.Sign > 0 ? around.Value : between.Value;
        rising = Chain(risingRun, n);
        falling = Chain(fallingRun, n);
        return true;

        // Steps strictly between two rung indices, all sharing one sign.
        static (int Start, int Count, int Sign)? Run(int[] signs, int from, int to, int n)
        {
            int count = ((to - from) % n + n) % n - 1;
            if (count <= 0)
                return null;
            int sign = signs[(from + 1) % n];
            for (int k = 1; k < count; k++)
            {
                if (signs[(from + 1 + k) % n] != sign)
                    return null;
            }
            return ((from + 1) % n, count, sign);
        }

        // A run of `Count` steps touches `Count + 1` vertices.
        static List<int> Chain((int Start, int Count, int Sign) run, int n)
        {
            var chain = new List<int>(run.Count + 1);
            for (int k = 0; k <= run.Count; k++)
                chain.Add((run.Start + k) % n);
            return chain;
        }
    }

    /// <summary>
    /// Zips the two chains into a triangle strip by a monotone merge walk, returning
    /// loop-local triangles or null if any of them folds. The fold test is the whole
    /// safety net: a merge zip is a valid triangulation of a monotone region only while
    /// neither chain overhangs the other, and an overhang shows up as a non-positive uv
    /// area. Rejecting there costs one ear-clipped face; accepting would emit inverted
    /// geometry.
    /// </summary>
    private static List<(int A, int B, int C)>? ZipBand(
        List<Vector2d> uv, int[] order, List<int> rising, List<int> falling, bool alongU)
    {
        // The rising run is the strip's first side and the falling run, reversed, its
        // second: on a CCW loop that pairs them end for end (see TrySplitBand).
        var bottom = rising;
        var top = new List<int>(falling);
        top.Reverse();

        double Key(int i)
        {
            var p = uv[order[i]];
            return alongU ? p.X : p.Y;
        }

        var triangles = new List<(int, int, int)>(bottom.Count + top.Count);
        int a = 0, b = 0;
        while (a < bottom.Count - 1 || b < top.Count - 1)
        {
            bool advanceBottom =
                a < bottom.Count - 1 &&
                (b >= top.Count - 1 || Key(bottom[a + 1]) <= Key(top[b + 1]));
            if (advanceBottom)
            {
                triangles.Add((bottom[a], bottom[a + 1], top[b]));
                a++;
            }
            else
            {
                triangles.Add((bottom[a], top[b + 1], top[b]));
                b++;
            }
        }

        foreach (var (x, y, z) in triangles)
        {
            // Exact-zero comparison on purpose: a fold or a zero-area rung triangle is a
            // structural refusal, not a tolerance question.
            if ((uv[order[y]] - uv[order[x]]).Cross(uv[order[z]] - uv[order[x]]) <= 0)
                return null;
        }
        return triangles;
    }

    // ---- non-wrapping regions: exact ear clipping ----

    /// <summary>
    /// Triangulates a plain (non-wrapping) region: outer loop plus holes, ear-clipped on
    /// exact coordinates. Fills the shared vertex arrays and the boundary edge set, and
    /// verifies every boundary sample survived into the triangulation — a dropped vertex
    /// would leave a chord across the curved boundary that welding cannot close.
    /// </summary>
    private static List<(int A, int B, int C)>? TriangulateRegion(
        List<List<Vector2d>> loopUv,
        List<List<Vector3d>> loopPoints,
        double period,
        List<Vector2d> uvAll,
        List<Vector3d> pointsAll,
        HashSet<(int, int)> boundaryEdges)
    {
        // Bring hole loops into the outer loop's period window.
        if (period > 0 && loopUv.Count > 1)
        {
            double outerMid = loopUv[0].Average(p => p.X);
            for (int i = 1; i < loopUv.Count; i++)
            {
                double shift = period * Math.Round((outerMid - loopUv[i].Average(p => p.X)) / period);
                // Deliberate exact test: Math.Round yields whole periods, so shift is
                // bit-zero for the already-aligned case; skipping is a pure no-op guard.
                if (shift != 0)
                {
                    for (int j = 0; j < loopUv[i].Count; j++)
                        loopUv[i][j] = new Vector2d(loopUv[i][j].X + shift, loopUv[i][j].Y);
                }
            }
        }

        var rings = new List<List<int>>();
        for (int i = 0; i < loopUv.Count; i++)
        {
            int start = uvAll.Count;
            uvAll.AddRange(loopUv[i]);
            pointsAll.AddRange(loopPoints[i]);
            rings.Add([.. Enumerable.Range(start, loopUv[i].Count)]);
            for (int j = 0; j < loopUv[i].Count; j++)
                boundaryEdges.Add(EdgeKey(start + j, start + (j + 1) % loopUv[i].Count));
        }

        double extent = Math.Max(
            uvAll.Max(p => p.X) - uvAll.Min(p => p.X),
            uvAll.Max(p => p.Y) - uvAll.Min(p => p.Y));
        if (extent <= 0)
            return null;
        // Relative degenerate-loop test in uv-area units (extent-scaled, so it works for
        // any parameterization); not a model-unit tolerance.
        if (Math.Abs(FaceGeometry.LoopSignedArea(loopUv[0])) < 1e-12 * extent * extent)
            return null;

        var triangles = EarClip(uvAll, rings);
        if (triangles is null)
            return null;

        return AllVerticesUsed(triangles, uvAll.Count) ? triangles : null;
    }

    /// <summary>
    /// Every boundary sample must survive into the triangulation — a dropped vertex
    /// would leave a chord across the curved boundary that welding cannot close.
    /// </summary>
    private static bool AllVerticesUsed(List<(int A, int B, int C)> triangles, int vertexCount)
    {
        var used = new bool[vertexCount];
        foreach (var (a, b, c) in triangles)
        {
            used[a] = true;
            used[b] = true;
            used[c] = true;
        }
        return Array.IndexOf(used, false) < 0;
    }

    // ---- band-like regions: strip zipping ----

    /// <summary>
    /// Triangulates a band-like region whose loops wind the periodic direction: two
    /// opposite-winding ring chains are zipped into a triangle strip by a monotone
    /// merge walk (like the cylinder band path, but tolerating rings subdivided into
    /// arcs with unrelated sample phases); a single winding chain fans to the surface's
    /// pole. Each chain gains a closure duplicate (same exact 3D point, uv one period
    /// on), so the strip's first and last cross edges weld to each other; those two
    /// cross edges are marked as boundary so refinement can never subdivide the welded
    /// pair inconsistently.
    /// </summary>
    private static List<(int A, int B, int C)>? TriangulateBand(
        Surface surface,
        double period,
        List<List<Vector2d>> loopUv,
        List<List<Vector3d>> loopPoints,
        List<int> windings,
        List<Vector2d> uvAll,
        List<Vector3d> pointsAll,
        HashSet<(int, int)> boundaryEdges)
    {
        if (loopUv.Count is not (1 or 2))
            return null;

        var triangles = new List<(int, int, int)>();
        if (loopUv.Count == 2)
        {
            if (windings[0] != -windings[1])
                return null;
            double anchor = loopUv[0][0].X;
            var first = BuildChain(loopUv[0], loopPoints[0], windings[0], period, anchor, uvAll, pointsAll, boundaryEdges);
            var second = BuildChain(loopUv[1], loopPoints[1], windings[1], period, anchor, uvAll, pointsAll, boundaryEdges);

            // Bottom chain = lower mean v; triangles CCW in (u, v).
            bool firstIsBottom =
                loopUv[0].Average(p => p.Y) <= loopUv[1].Average(p => p.Y);
            var bottom = firstIsBottom ? first : second;
            var top = firstIsBottom ? second : first;
            boundaryEdges.Add(EdgeKey(bottom[0], top[0]));
            boundaryEdges.Add(EdgeKey(bottom[^1], top[^1]));

            // The unrolled band IS a u-monotone polygon, so the stack sweep is the correct
            // triangulation of it and the merge walk is only an approximation of one. The
            // difference bites whenever the two rings carry unequal sample counts: a merge
            // pairs by u, so a stretch where one chain has many samples between two of the
            // other's gets FANNED from a single far vertex, and where that stretch turns
            // back on itself consecutive fan triangles invert. Measured on
            // Box(20,20,20) - Sphere(12), whose cavity wall runs a 48-sample latitude
            // circle against a 240-sample scalloped rim: 102 226 triangles with 2 226
            // inverted (worst dot -0.9978) from the merge walk, against 1 508 with none
            // from the sweep. (The same lesson ZipSlabs records for bands with holes.)
            int emitted = triangles.Count;
            if (!NonDecreasingU(uvAll, bottom) || !NonDecreasingU(uvAll, top) ||
                !SweepMonotone(triangles, uvAll, bottom, top))
            {
                triangles.RemoveRange(emitted, triangles.Count - emitted);
                int i = 0, j = 0;
                while (i < bottom.Count - 1 || j < top.Count - 1)
                {
                    bool advanceBottom =
                        i < bottom.Count - 1 &&
                        (j >= top.Count - 1 || uvAll[bottom[i + 1]].X <= uvAll[top[j + 1]].X);
                    if (advanceBottom)
                    {
                        triangles.Add((bottom[i], bottom[i + 1], top[j]));
                        i++;
                    }
                    else
                    {
                        triangles.Add((bottom[i], top[j + 1], top[j]));
                        j++;
                    }
                }
            }
        }
        else
        {
            // A single winding loop bounds a pole cap: verify the far v row is
            // degenerate and fan the chain to the pole point.
            var domainV = surface.DomainV;
            double meanV = loopUv[0].Average(p => p.Y);
            double vFar = Math.Abs(meanV - domainV.Start) > Math.Abs(domainV.End - meanV)
                ? domainV.Start
                : domainV.End;
            if (!double.IsFinite(vFar))
                return null;
            var pole = surface.PointAt(surface.DomainU.Start, vFar);
            for (int k = 1; k <= 2; k++)
            {
                // (1e-9)²: squared weld tolerance — a true pole ring is exactly one point.
                if (surface.PointAt(surface.DomainU.Start + period * k / 3.0, vFar)
                    .DistanceSquaredTo(pole) > 1e-18)
                    return null;
            }

            var chain = BuildChain(loopUv[0], loopPoints[0], windings[0], period, loopUv[0][0].X, uvAll, pointsAll, boundaryEdges);
            int poleIndex = uvAll.Count;
            uvAll.Add(new Vector2d(uvAll[chain[0]].X + period / 2, vFar));
            pointsAll.Add(pole);
            boundaryEdges.Add(EdgeKey(chain[0], poleIndex));
            boundaryEdges.Add(EdgeKey(chain[^1], poleIndex));

            bool poleBelow = vFar < meanV;
            for (int i = 0; i + 1 < chain.Count; i++)
            {
                triangles.Add(poleBelow
                    ? (chain[i + 1], chain[i], poleIndex)
                    : (chain[i], chain[i + 1], poleIndex));
            }
        }
        return triangles;
    }

    /// <summary>
    /// Builds one ring chain oriented by increasing u, rotated to start at the vertex
    /// closest above <paramref name="alignToU"/>, with a closing duplicate vertex (same
    /// exact 3D point, uv one period on). Chain edges are marked as boundary.
    /// </summary>
    private static List<int> BuildChain(
        List<Vector2d> uv,
        List<Vector3d> pts,
        int winding,
        double period,
        double alignToU,
        List<Vector2d> uvAll,
        List<Vector3d> pointsAll,
        HashSet<(int, int)> boundaryEdges)
    {
        int n = uv.Count;
        var order = new int[n];
        for (int i = 0; i < n; i++)
            order[i] = winding > 0 ? i : n - 1 - i;

        // Rotate the chain to start at the vertex closest above the alignment phase.
        int startAt = 0;
        double bestOffset = double.PositiveInfinity;
        for (int i = 0; i < n; i++)
        {
            double offset = uv[order[i]].X - alignToU;
            offset -= period * Math.Floor(offset / period); // into [0, period)
            if (offset < bestOffset)
            {
                bestOffset = offset;
                startAt = i;
            }
        }

        var chain = new List<int>(n + 1);
        double previousU = alignToU + bestOffset;
        for (int i = 0; i < n; i++)
        {
            int src = order[(startAt + i) % n];
            double u = uv[src].X;
            u += period * Math.Round((previousU - u) / period);
            chain.Add(uvAll.Count);
            uvAll.Add(new Vector2d(u, uv[src].Y));
            pointsAll.Add(pts[src]);
            previousU = u;
        }
        // Closure duplicate: same exact 3D point, one period on.
        int first = chain[0];
        chain.Add(uvAll.Count);
        uvAll.Add(new Vector2d(uvAll[first].X + period, uvAll[first].Y));
        pointsAll.Add(pointsAll[first]);

        for (int i = 0; i + 1 < chain.Count; i++)
            boundaryEdges.Add(EdgeKey(chain[i], chain[i + 1]));
        return chain;
    }

    // ---- band regions with interior hole loops: seam cut + ear clipping ----

    /// <summary>
    /// Triangulates a two-ring band that carries extra zero-winding hole loops (e.g. a
    /// bore wall crossed by a smaller drill): the band is cut open along a seam placed
    /// in the largest u-gap left free by the holes, unrolling it into a simple
    /// rectangle-like outer polygon (bottom chain + reversed top chain, each with its
    /// closure duplicate). The unrolled region is then <see cref="ZipSlabs">slab-swept</see>
    /// if the holes decompose it into u-monotone sub-bands, and only otherwise ear-clipped
    /// with the holes bridged in. The two seam chords are exact uv-translates by one
    /// period with identical 3D endpoints, so they weld to each other; both are marked as
    /// boundary so refinement never splits the welded pair inconsistently. Pole-bounded
    /// single-chain bands with holes are not supported (returns null — the caller
    /// falls back).
    /// <para>The sweep is tried FIRST because ear clipping an unrolled band is
    /// structurally unable to do better than a fan here: both ring chains lie at a
    /// constant v (an extruded surface's rings are its v-domain ends, so consecutive
    /// samples are EXACTLY collinear in uv), and <see cref="IsEar"/> refuses exactly
    /// straight corners because they would be zero-area. The only clippable ears are
    /// therefore the unrolled rectangle's own four corners, so the clipper fans the whole
    /// band from a corner; <see cref="Refine"/> then bisects those long fan triangles into
    /// slivers whose normals are the boundary's binormal rather than the surface's — the
    /// crumpled bore wall a cross-drilled housing used to render as.</para>
    /// </summary>
    private static List<(int A, int B, int C)>? TriangulateBandWithHoles(
        double period,
        List<List<Vector2d>> loopUv,
        List<List<Vector3d>> loopPoints,
        List<int> windings,
        List<Vector2d> uvAll,
        List<Vector3d> pointsAll,
        HashSet<(int, int)> boundaryEdges)
    {
        var bandLoops = new List<int>();
        var holeLoops = new List<int>();
        for (int i = 0; i < windings.Count; i++)
            (windings[i] != 0 ? bandLoops : holeLoops).Add(i);
        if (bandLoops.Count != 2 || windings[bandLoops[0]] != -windings[bandLoops[1]])
            return null;

        if (!SeamAnchor(loopUv, holeLoops, period, out double anchor))
            return null;

        var chainA = BuildChain(
            loopUv[bandLoops[0]], loopPoints[bandLoops[0]], windings[bandLoops[0]],
            period, anchor, uvAll, pointsAll, boundaryEdges);
        var chainB = BuildChain(
            loopUv[bandLoops[1]], loopPoints[bandLoops[1]], windings[bandLoops[1]],
            period, anchor, uvAll, pointsAll, boundaryEdges);

        bool aIsBottom =
            loopUv[bandLoops[0]].Average(p => p.Y) <= loopUv[bandLoops[1]].Average(p => p.Y);
        var bottom = aIsBottom ? chainA : chainB;
        var top = aIsBottom ? chainB : chainA;

        // The two seam chords (left: closing ring edge top[0]→bottom[0]; right:
        // bottom[^1]→top[^1], its exact one-period translate) are boundary.
        boundaryEdges.Add(EdgeKey(bottom[0], top[0]));
        boundaryEdges.Add(EdgeKey(bottom[^1], top[^1]));

        // Outer ring, CCW in uv: bottom left→right along low v, then top right→left.
        var outer = new List<int>(bottom.Count + top.Count);
        outer.AddRange(bottom);
        for (int i = top.Count - 1; i >= 0; i--)
            outer.Add(top[i]);

        // Every hole must sit strictly between the seam chords' u-extents.
        double seamLow = Math.Max(uvAll[bottom[0]].X, uvAll[top[0]].X);
        double seamHigh = Math.Min(uvAll[bottom[0]].X, uvAll[top[0]].X) + period;

        var rings = new List<List<int>> { outer };
        foreach (int h in holeLoops)
        {
            var uv = loopUv[h];
            double mid = uv.Average(p => p.X);
            double shift = period * Math.Round((anchor + period / 2 - mid) / period);
            if (uv.Min(p => p.X) + shift <= seamLow || uv.Max(p => p.X) + shift >= seamHigh)
                return null; // hole straddles the seam — no clear cut exists

            int start = uvAll.Count;
            for (int j = 0; j < uv.Count; j++)
            {
                uvAll.Add(new Vector2d(uv[j].X + shift, uv[j].Y));
                pointsAll.Add(loopPoints[h][j]);
            }
            var ring = new List<int>(uv.Count);
            for (int j = 0; j < uv.Count; j++)
            {
                ring.Add(start + j);
                boundaryEdges.Add(EdgeKey(start + j, start + (j + 1) % uv.Count));
            }
            rings.Add(ring);
        }

        // Both paths consume exactly these vertices and boundary edges, so the sweep may
        // be attempted and abandoned without disturbing the fallback.
        var triangles = ZipSlabs(uvAll, bottom, top, rings) ?? EarClip(uvAll, rings);
        if (triangles is null)
            return null;
        return AllVerticesUsed(triangles, uvAll.Count) ? triangles : null;
    }

    /// <summary>
    /// Triangulates an unrolled band carrying interior hole loops WITHOUT ear clipping it:
    /// each hole is split at its extreme-u vertices into a lower and an upper u-monotone
    /// chain, which cuts the band into a run of u-monotone slabs — free slab, below-hole
    /// slab, above-hole slab, free slab, … — each triangulated by
    /// <see cref="SweepMonotone"/>. Away from the holes that reproduces the natural grid's
    /// zigzag: one column per ring sample, never a chord across many.
    /// <para>The cut at a hole's leftmost vertex L is the two-segment chord
    /// <c>bottom[k] → L → top[j]</c>, where k and j are the last ring samples at or before
    /// u(L); its halves are shared verbatim by the slabs on both sides (the free slab
    /// takes <c>bottom[k] → L</c> as its lower chain's last edge and <c>L → top[j]</c> as
    /// its closing rung, the below-hole slab takes the first as its opening rung and the
    /// above-hole slab the second), so the pieces are watertight by INDEX, never by
    /// tolerance. No new vertex is invented: the ring polylines are shared edge geometry
    /// and inserting a sample into one would crack the neighbouring cap face.</para>
    /// <para>Returns null — leaving the caller to ear-clip — whenever the decomposition
    /// does not exist: a hole whose two chains are not u-monotone, or holes crowding the
    /// ring samples so that a slab would be empty. The final guard is a global one: the
    /// emitted uv area must match the outer ring's less the holes', which neither a gap
    /// nor an overlap can satisfy.</para>
    /// </summary>
    private static List<(int A, int B, int C)>? ZipSlabs(
        List<Vector2d> uvAll, List<int> bottom, List<int> top, List<List<int>> rings)
    {
        double U(int i) => uvAll[i].X;

        // Split every hole ring at its extreme-u vertices into two chains running L → R.
        var holes = new List<(List<int> Lower, List<int> Upper)>(rings.Count - 1);
        foreach (var ring in rings.Skip(1))
        {
            if (ring.Count < 3)
                return null;
            int lo = 0, hi = 0;
            for (int i = 1; i < ring.Count; i++)
            {
                if (U(ring[i]) < U(ring[lo])) lo = i;
                if (U(ring[i]) > U(ring[hi])) hi = i;
            }
            // Exact comparison on purpose: a hole with no u-extent has no cut to make,
            // which is a structural refusal rather than a tolerance question.
            if (U(ring[lo]) >= U(ring[hi]))
                return null;

            var forward = WalkRing(ring, lo, hi, +1);
            var backward = WalkRing(ring, lo, hi, -1);
            if (!NonDecreasingU(uvAll, forward) || !NonDecreasingU(uvAll, backward))
                return null;
            holes.Add(forward.Average(i => uvAll[i].Y) <= backward.Average(i => uvAll[i].Y)
                ? (forward, backward)
                : (backward, forward));
        }
        holes.Sort((a, b) => U(a.Lower[0]).CompareTo(U(b.Lower[0])));

        var triangles = new List<(int A, int B, int C)>();
        int bAt = 0, tAt = 0;   // first ring sample not yet consumed
        int carried = -1;       // the previous cut's hole vertex, riding on the lower chain
        foreach (var (lower, upper) in holes)
        {
            int left = lower[0], right = lower[^1];
            int bLeft = LastAtOrBefore(uvAll, bottom, U(left), bAt);
            int tLeft = LastAtOrBefore(uvAll, top, U(left), tAt);
            int bRight = FirstAtOrAfter(uvAll, bottom, U(right), bLeft);
            int tRight = FirstAtOrAfter(uvAll, top, U(right), tLeft);
            if (bLeft < bAt || tLeft < tAt || bRight < 0 || tRight < 0)
                return null; // a slab would be empty — the holes crowd the ring samples

            // Free slab up to this hole. BOTH cut vertices ride on its lower chain and
            // neither on its upper one, so the free slab's two rungs are exactly the cuts'
            // upper halves and its lower chain's end edges are exactly their lower halves
            // — which is what makes every piece share whole segments with its neighbours.
            if (!SweepMonotone(triangles, uvAll,
                    Slice(bottom, bAt, bLeft, carried, left),
                    Slice(top, tAt, tLeft, -1, -1)))
                return null;
            // The hole splits its own slab in two.
            if (!SweepMonotone(triangles, uvAll, Slice(bottom, bLeft, bRight, -1, -1), lower) ||
                !SweepMonotone(triangles, uvAll, upper, Slice(top, tLeft, tRight, -1, -1)))
                return null;

            bAt = bRight;
            tAt = tRight;
            carried = right;
        }
        // Free slab from the last hole to the seam.
        if (!SweepMonotone(triangles, uvAll,
                Slice(bottom, bAt, bottom.Count - 1, carried, -1),
                Slice(top, tAt, top.Count - 1, -1, -1)))
            return null;

        // Global closure: a gap or an overlap cannot survive an area comparison, and the
        // sweep's own guards cannot see either (they are local to one slab). Relative,
        // because uv carries no model units; 1e-9 is decades above the round-off of
        // summing a few thousand shoelace terms and decades below any real defect.
        double target = Math.Abs(RingArea(uvAll, rings[0]));
        foreach (var ring in rings.Skip(1))
            target -= Math.Abs(RingArea(uvAll, ring));
        double sum = 0;
        foreach (var (a, b, c) in triangles)
            sum += (uvAll[b] - uvAll[a]).Cross(uvAll[c] - uvAll[a]) / 2;
        return Math.Abs(sum - target) <= 1e-9 * target ? triangles : null;

        static List<int> WalkRing(List<int> ring, int from, int to, int step)
        {
            var chain = new List<int>();
            for (int i = from; ; i = (i + step + ring.Count) % ring.Count)
            {
                chain.Add(ring[i]);
                if (i == to)
                    return chain;
            }
        }
    }

    /// <summary>
    /// Triangulates one slab — a u-monotone polygon given as its lower and upper chains,
    /// both running left to right and sharing no vertex — by the textbook stack sweep for
    /// monotone polygons, appending CCW triangles. False when the slab is degenerate.
    /// <para>The merge walk <see cref="ZipBand"/> uses is NOT enough here, and that is the
    /// whole reason this method exists: a merge pairs the chains by u, so wherever one
    /// chain carries many samples between two of the other's — a drilled breakout curve
    /// against a coarse ring — it fans them all from one far vertex, and as soon as that
    /// stretch turns back on itself (a breakout's right-hand end, where the curve climbs
    /// steeply) consecutive fan triangles invert. The stack sweep pops only at convex
    /// turns, so it is correct on ANY monotone slab; on the free slabs (both chains at a
    /// constant v, sampled at the same u's) it emits exactly the natural grid's zigzag.
    /// </para>
    /// </summary>
    private static bool SweepMonotone(
        List<(int A, int B, int C)> triangles, List<Vector2d> uv, List<int> lower, List<int> upper)
    {
        if (lower.Count + upper.Count < 3 || lower.Count == 0 || upper.Count == 0)
            return false;

        // Merge the chains by u; a tie keeps the lower chain's vertex first, so a vertical
        // rung is swept bottom to top.
        var order = new List<(int Index, bool Lower)>(lower.Count + upper.Count);
        for (int i = 0, j = 0; i < lower.Count || j < upper.Count;)
        {
            bool takeLower = j >= upper.Count ||
                (i < lower.Count && uv[lower[i]].X <= uv[upper[j]].X);
            order.Add(takeLower ? (lower[i++], true) : (upper[j++], false));
        }

        var stack = new List<(int Index, bool Lower)> { order[0], order[1] };
        for (int k = 2; k < order.Count; k++)
        {
            var v = order[k];
            // The rightmost vertex closes the funnel: it sees every vertex left on the
            // stack, whichever chain they came from.
            if (k == order.Count - 1 || v.Lower != stack[^1].Lower)
            {
                for (int s = stack.Count - 1; s > 0; s--)
                {
                    if (!AddOriented(triangles, uv, v.Index, stack[s].Index, stack[s - 1].Index))
                        return false;
                }
                var previous = order[k - 1];
                stack.Clear();
                stack.Add(previous);
                stack.Add(v);
                continue;
            }

            var last = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            while (stack.Count > 0 && TurnsIntoInterior(uv, stack[^1].Index, last.Index, v.Index, v.Lower))
            {
                if (!AddOriented(triangles, uv, stack[^1].Index, last.Index, v.Index))
                    return false;
                last = stack[^1];
                stack.RemoveAt(stack.Count - 1);
            }
            stack.Add(last);
            stack.Add(v);
        }
        return true;
    }

    /// <summary>
    /// Whether the diagonal closing (<paramref name="a"/>, <paramref name="b"/>,
    /// <paramref name="c"/>) lies inside the slab: a left turn below the interior (lower
    /// chain) and a right turn above it (upper chain). Exactly collinear is deliberately
    /// NOT a turn — a ring's samples are exactly collinear in uv, and popping there would
    /// emit the zero-area triangles this whole file exists to avoid.
    /// </summary>
    private static bool TurnsIntoInterior(List<Vector2d> uv, int a, int b, int c, bool lower)
    {
        double cross = (uv[b] - uv[a]).Cross(uv[c] - uv[a]);
        return lower ? cross > 0 : cross < 0;
    }

    /// <summary>
    /// Appends a triangle wound CCW in uv, taking the winding from its own signed area.
    /// The sweep proves the slab is covered exactly once, so each triangle's orientation
    /// is determinate and reading it off the sign is equivalent to — and shorter than —
    /// case-splitting on which chain the apex came from. Exactly zero area is refused: the
    /// sweep never produces one from valid input, so it means the chains crossed.
    /// </summary>
    private static bool AddOriented(
        List<(int A, int B, int C)> triangles, List<Vector2d> uv, int a, int b, int c)
    {
        double cross = (uv[b] - uv[a]).Cross(uv[c] - uv[a]);
        if (cross == 0)
            return false;
        triangles.Add(cross > 0 ? (a, b, c) : (a, c, b));
        return true;
    }

    /// <summary>
    /// Chain entries <paramref name="from"/>..<paramref name="to"/> inclusive, optionally
    /// bracketed by a <paramref name="prefix"/> and a <paramref name="suffix"/> vertex
    /// (−1 for neither) — the hole vertices a cut hangs off.
    /// </summary>
    private static List<int> Slice(List<int> chain, int from, int to, int prefix, int suffix)
    {
        var slice = new List<int>(to - from + 3);
        if (prefix >= 0)
            slice.Add(prefix);
        for (int i = from; i <= to; i++)
            slice.Add(chain[i]);
        if (suffix >= 0)
            slice.Add(suffix);
        return slice;
    }

    /// <summary>Last index in [<paramref name="from"/>, end] whose u is ≤ <paramref name="u"/>; <paramref name="from"/>−1 if none.</summary>
    private static int LastAtOrBefore(List<Vector2d> uv, List<int> chain, double u, int from)
    {
        int at = from - 1;
        for (int i = from; i < chain.Count && uv[chain[i]].X <= u; i++)
            at = i;
        return at;
    }

    /// <summary>First index in [<paramref name="from"/>, end] whose u is ≥ <paramref name="u"/>; −1 if none.</summary>
    private static int FirstAtOrAfter(List<Vector2d> uv, List<int> chain, double u, int from)
    {
        for (int i = Math.Max(from, 0); i < chain.Count; i++)
        {
            if (uv[chain[i]].X >= u)
                return i;
        }
        return -1;
    }

    private static bool NonDecreasingU(List<Vector2d> uv, List<int> chain)
    {
        for (int i = 0; i + 1 < chain.Count; i++)
        {
            if (uv[chain[i + 1]].X < uv[chain[i]].X)
                return false;
        }
        return true;
    }

    private static double RingArea(List<Vector2d> uv, List<int> ring)
    {
        double area = 0;
        for (int i = 0; i < ring.Count; i++)
            area += uv[ring[i]].Cross(uv[ring[(i + 1) % ring.Count]]);
        return area / 2;
    }

    /// <summary>
    /// Picks a seam u-phase for cutting a band open: the midpoint of the largest u-gap
    /// (mod the period) not covered by any hole loop, so the seam chords cannot cross a
    /// hole. False when a hole covers a full period (no clear seam exists).
    /// </summary>
    private static bool SeamAnchor(
        List<List<Vector2d>> loopUv, List<int> holeLoops, double period, out double anchor)
    {
        anchor = 0;
        var intervals = new List<(double Start, double End)>(holeLoops.Count);
        foreach (int h in holeLoops)
        {
            double min = loopUv[h].Min(p => p.X);
            double max = loopUv[h].Max(p => p.X);
            if (max - min >= period)
                return false;
            double start = min - period * Math.Floor(min / period); // into [0, period)
            intervals.Add((start, start + (max - min)));
        }
        if (intervals.Count == 0)
            return true;

        // Duplicate each interval one period on so every cyclic gap appears in full on
        // the doubled line [0, 2·period), then take the largest gap between merged runs.
        var doubled = intervals
            .SelectMany(i => (IEnumerable<(double Start, double End)>)[i, (i.Start + period, i.End + period)])
            .OrderBy(i => i.Start)
            .ToList();
        double coveredTo = doubled[0].End;
        double bestGap = 0;
        foreach (var (start, end) in doubled.Skip(1))
        {
            if (start > coveredTo && start - coveredTo > bestGap)
            {
                bestGap = start - coveredTo;
                anchor = (coveredTo + start) / 2 % period;
            }
            coveredTo = Math.Max(coveredTo, end);
        }
        return bestGap > 0;
    }

    // ---- exact ear clipping ----

    /// <summary>
    /// Ear clipping over index rings (outer first, holes after) with exact coordinates.
    /// Holes are bridged into the outer ring via mutually visible vertices (earcut's
    /// approach, with a conservative visibility test). Triangles come out CCW in uv
    /// regardless of the input winding. Returns null when no ear can be clipped
    /// (degenerate input) so the caller can fall back. Internal for direct unit testing.
    /// </summary>
    internal static List<(int A, int B, int C)>? EarClip(List<Vector2d> uv, List<List<int>> rings)
    {
        double RingArea(List<int> ring)
        {
            double area = 0;
            for (int i = 0; i < ring.Count; i++)
                area += uv[ring[i]].Cross(uv[ring[(i + 1) % ring.Count]]);
            return area / 2;
        }

        var outer = new List<int>(rings[0]);
        if (RingArea(outer) < 0)
            outer.Reverse();
        var holes = rings.Skip(1)
            .Select(r =>
            {
                var hole = new List<int>(r);
                if (RingArea(hole) > 0)
                    hole.Reverse();
                return hole;
            })
            .OrderBy(h => h.Min(i => uv[i].X))
            .ToList();

        // Inverse evaluation carries ~1e-9 jitter, so "on the boundary" needs a band:
        // a vertex a hair outside a candidate ear must still block it — otherwise the
        // ear's diagonal cuts that vertex off and the remaining polygon self-intersects.
        // The same band makes bridge visibility treat nearly-collinear contact as
        // touching (exact-zero cross products would miss it by one ulp).
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        foreach (var ring in rings)
        {
            foreach (int i in ring)
            {
                minX = Math.Min(minX, uv[i].X);
                maxX = Math.Max(maxX, uv[i].X);
                minY = Math.Min(minY, uv[i].Y);
                maxY = Math.Max(maxY, uv[i].Y);
            }
        }
        double blockBand = 1e-8 * Math.Max(maxX - minX, maxY - minY);

        foreach (var hole in holes)
        {
            if (!SpliceHole(uv, outer, hole, holes, blockBand))
                return null;
        }

        var polygon = outer;
        var triangles = new List<(int, int, int)>(polygon.Count);
        while (polygon.Count > 3)
        {
            // Shortest-diagonal-first: clipping the ear with the shortest cut keeps the
            // triangulation zigzagging along boundary runs instead of fanning from the
            // corners — fans breed long interior diagonals that the curvature
            // refinement would then have to subdivide quadratically.
            int best = -1;
            double bestDiagonal = double.PositiveInfinity;
            for (int ib = 0; ib < polygon.Count; ib++)
            {
                int ia = (ib + polygon.Count - 1) % polygon.Count;
                int ic = (ib + 1) % polygon.Count;
                var diagonal = uv[polygon[ic]] - uv[polygon[ia]];
                double length = diagonal.Dot(diagonal);
                if (length >= bestDiagonal || !IsEar(uv, polygon, ia, ib, ic, blockBand))
                    continue;
                best = ib;
                bestDiagonal = length;
            }
            if (best < 0)
                return null;
            int prev = (best + polygon.Count - 1) % polygon.Count;
            int next = (best + 1) % polygon.Count;
            triangles.Add((polygon[prev], polygon[best], polygon[next]));
            polygon.RemoveAt(best);
        }
        // The final triangle can be collinear when the leftover region has zero area
        // (everything real is already covered); emit only genuine area.
        var pa = uv[polygon[0]];
        var pb = uv[polygon[1]];
        var pc = uv[polygon[2]];
        if ((pb - pa).Cross(pc - pb) > 0)
            triangles.Add((polygon[0], polygon[1], polygon[2]));
        return triangles;
    }

    /// <summary>
    /// Strictly convex corner with no other polygon vertex inside or within
    /// <paramref name="blockBand"/> of the closed ear triangle. Points coincident with
    /// an ear corner (hole-bridge duplicates) do not block — the diagonal merely ends
    /// at their position.
    /// </summary>
    private static bool IsEar(List<Vector2d> uv, List<int> polygon, int ia, int ib, int ic, double blockBand)
    {
        var a = uv[polygon[ia]];
        var b = uv[polygon[ib]];
        var c = uv[polygon[ic]];
        if ((b - a).Cross(c - b) <= 0)
            return false; // reflex or exactly straight — never emit zero-area ears

        double ab = (b - a).Length, bc = (c - b).Length, ca = (a - c).Length;
        for (int j = 0; j < polygon.Count; j++)
        {
            if (j == ia || j == ib || j == ic)
                continue;
            var p = uv[polygon[j]];
            if (Coincident(p, a) || Coincident(p, b) || Coincident(p, c))
                continue;
            if ((b - a).Cross(p - a) >= -blockBand * ab &&
                (c - b).Cross(p - b) >= -blockBand * bc &&
                (a - c).Cross(p - c) >= -blockBand * ca)
                return false; // inside the ear, or within the jitter band of its edges
        }
        return true;
    }

    private static bool Coincident(in Vector2d p, in Vector2d q) => p.X == q.X && p.Y == q.Y;

    /// <summary>
    /// Connects a hole into the outer polygon through a mutually visible vertex pair,
    /// duplicating both bridge endpoints (the polygon becomes weakly simple). Pairs are
    /// tried closest-first; a pair is visible when its segment touches no ring edge
    /// (within <paramref name="tolerance"/>) and its midpoint lies inside the region.
    /// </summary>
    private static bool SpliceHole(
        List<Vector2d> uv, List<int> outer, List<int> hole, List<List<int>> allHoles, double tolerance)
    {
        var pairs = new List<(double DistanceSquared, int OuterAt, int HoleAt)>();
        for (int i = 0; i < outer.Count; i++)
        {
            for (int j = 0; j < hole.Count; j++)
            {
                var d = uv[outer[i]] - uv[hole[j]];
                pairs.Add((d.Dot(d), i, j));
            }
        }
        pairs.Sort((x, y) => x.DistanceSquared.CompareTo(y.DistanceSquared));

        foreach (var (_, outerAt, holeAt) in pairs)
        {
            var p = uv[outer[outerAt]];
            var q = uv[hole[holeAt]];
            if (Coincident(p, q))
                continue;
            bool blocked = false;
            foreach (var ring in allHoles.Append(outer))
            {
                for (int e = 0; e < ring.Count && !blocked; e++)
                {
                    var a = uv[ring[e]];
                    var b = uv[ring[(e + 1) % ring.Count]];
                    if (Coincident(a, p) || Coincident(b, p) || Coincident(a, q) || Coincident(b, q))
                        continue; // incident at an endpoint
                    if (SegmentsTouch(p, q, a, b, tolerance))
                        blocked = true;
                }
                if (blocked)
                    break;
            }
            if (blocked)
                continue;
            var mid = new Vector2d((p.X + q.X) / 2, (p.Y + q.Y) / 2);
            if (!InsideRegion(uv, outer, allHoles, hole, mid))
                continue;

            // outer: [..., o] + [h, hole walk..., h dup] + [o dup, ...]
            var insertion = new List<int>(hole.Count + 2);
            for (int k = 0; k <= hole.Count; k++)
                insertion.Add(hole[(holeAt + k) % hole.Count]);
            insertion.Add(outer[outerAt]);
            outer.InsertRange(outerAt + 1, insertion);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Conservative segment test: any contact (crossing, touching, collinear overlap)
    /// counts. An endpoint within <paramref name="tolerance"/> of the other segment's
    /// line counts as on it — exact-zero cross products would miss nearly-collinear
    /// contact by one ulp, letting a bridge overlap an edge and leaving the spliced
    /// polygon self-intersecting. The tolerance is an absolute uv distance, consistent
    /// with the ear clipper's jitter blocking band. Internal for direct unit testing.
    /// </summary>
    internal static bool SegmentsTouch(
        in Vector2d p, in Vector2d q, in Vector2d a, in Vector2d b, double tolerance)
    {
        var pq = q - p;
        var ab = b - a;
        double d1 = pq.Cross(a - p); // |pq| · signed distance of a from line pq
        double d2 = pq.Cross(b - p);
        double d3 = ab.Cross(p - a);
        double d4 = ab.Cross(q - a);
        double bandPq = tolerance * pq.Length;
        double bandAb = tolerance * ab.Length;
        if (((d1 > bandPq && d2 < -bandPq) || (d1 < -bandPq && d2 > bandPq)) &&
            ((d3 > bandAb && d4 < -bandAb) || (d3 < -bandAb && d4 > bandAb)))
            return true;
        bool OnSegment(in Vector2d s0, in Vector2d s1, in Vector2d x) =>
            Math.Min(s0.X, s1.X) - tolerance <= x.X && x.X <= Math.Max(s0.X, s1.X) + tolerance &&
            Math.Min(s0.Y, s1.Y) - tolerance <= x.Y && x.Y <= Math.Max(s0.Y, s1.Y) + tolerance;
        if (Math.Abs(d1) <= bandPq && OnSegment(p, q, a))
            return true;
        if (Math.Abs(d2) <= bandPq && OnSegment(p, q, b))
            return true;
        if (Math.Abs(d3) <= bandAb && OnSegment(a, b, p))
            return true;
        if (Math.Abs(d4) <= bandAb && OnSegment(a, b, q))
            return true;
        return false;
    }

    /// <summary>Point strictly inside the outer ring and outside every hole except <paramref name="beingSpliced"/>.</summary>
    private static bool InsideRegion(
        List<Vector2d> uv, List<int> outer, List<List<int>> holes, List<int> beingSpliced, Vector2d point)
    {
        bool Inside(List<int> ring)
        {
            int crossings = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = uv[ring[i]];
                var b = uv[ring[(i + 1) % ring.Count]];
                if (a.X <= point.X == b.X <= point.X)
                    continue;
                double t = (point.X - a.X) / (b.X - a.X);
                if (a.Y + t * (b.Y - a.Y) > point.Y)
                    crossings++;
            }
            return (crossings & 1) == 1;
        }
        if (!Inside(outer))
            return false;
        foreach (var hole in holes)
        {
            if (!ReferenceEquals(hole, beingSpliced) && Inside(hole))
                return false;
        }
        return true;
    }

    // ---- refinement ----

    /// <summary>
    /// Natural grid spacing per parameter direction, mirroring the grid path's sampling
    /// rules; infinite where the surface is ruled in that direction (chords are exact).
    /// </summary>
    private static (double U, double V) NaturalSteps(Surface surface, int segmentsPerCircle, int curveSamples)
    {
        double FromCurve(Curve3d c) => c.Underlying is Line3d
            ? double.PositiveInfinity
            : c.Domain.Length / (c.IsClosed && c.Underlying is Circle3d ? segmentsPerCircle : curveSamples);
        return surface switch
        {
            CylinderSurface => (2 * Math.PI / segmentsPerCircle, double.PositiveInfinity),
            ExtrudedSurface e => (FromCurve(e.Generator), double.PositiveInfinity),
            RevolvedSurface r => (
                r.DomainU.Length / (r.IsFullTurn ? segmentsPerCircle : curveSamples),
                FromCurve(r.Generator)),
            SweptSurface s => (FromCurve(s.Generator), s.DomainV.Length / curveSamples),
            _ => (double.PositiveInfinity, double.PositiveInfinity),
        };
    }

    /// <summary>
    /// Splits interior edges longer than one natural grid step (measured per-axis in
    /// step units) at their uv midpoints, lifting new vertices onto the exact surface.
    /// Boundary edges are never split — their chords are the shared seam geometry.
    /// Termination is enforced by a monotone-decrease rule: a newly created edge is
    /// only queued when it is strictly shorter than the edge whose split created it.
    /// Plain midpoint bisection lacks this property — the median to the opposite
    /// vertex can be as long as the split edge, and on the skinny triangles a band
    /// zip makes from a v-excursioned chain against a sparse ring that becomes a
    /// self-sustaining cascade (each split enqueues more same-length edges) whose
    /// ever-closer midpoints eventually weld non-manifold. Some interior edges may
    /// stay oversized where the rule stops a cascade — a fidelity trade, never a
    /// correctness one. Returns false if the safety guard still trips, so the caller
    /// abandons the face instead of emitting a partially refined set.
    /// </summary>
    private static bool Refine(
        Surface surface,
        double period,
        List<Vector2d> uv,
        List<Vector3d> points,
        List<(int A, int B, int C)> triangles,
        HashSet<(int, int)> boundaryEdges,
        double stepU,
        double stepV)
    {
        if (double.IsInfinity(stepU) && double.IsInfinity(stepV))
            return true;

        double MetricSquared((int, int) e)
        {
            double du = double.IsInfinity(stepU) ? 0 : (uv[e.Item2].X - uv[e.Item1].X) / stepU;
            double dv = double.IsInfinity(stepV) ? 0 : (uv[e.Item2].Y - uv[e.Item1].Y) / stepV;
            return du * du + dv * dv;
        }
        bool Oversized((int, int) e) => MetricSquared(e) > 1 + 1e-9;

        var edgeOwners = new Dictionary<(int, int), List<int>>();
        void Register(int triangle, (int, int) key)
        {
            if (!edgeOwners.TryGetValue(key, out var owners))
                edgeOwners[key] = owners = [];
            owners.Add(triangle);
        }
        for (int t = 0; t < triangles.Count; t++)
        {
            var (a, b, c) = triangles[t];
            Register(t, EdgeKey(a, b));
            Register(t, EdgeKey(b, c));
            Register(t, EdgeKey(c, a));
        }

        static (int A, int B, int C) Rotate((int A, int B, int C) triangle, (int, int) key)
        {
            var (a, b, c) = triangle;
            if (EdgeKey(a, b) == key)
                return (a, b, c);
            if (EdgeKey(b, c) == key)
                return (b, c, a);
            if (EdgeKey(c, a) == key)
                return (c, a, b);
            return (-1, -1, -1); // already rewritten by a previous split
        }

        var queue = new Queue<(int, int)>(
            edgeOwners.Keys.Where(k => !boundaryEdges.Contains(k) && Oversized(k)));
        int guard = 200000;
        while (queue.Count > 0)
        {
            if (guard-- <= 0)
                return false; // refinement did not converge — abandon the face
            var key = queue.Dequeue();
            if (!edgeOwners.TryGetValue(key, out var owners) || !Oversized(key))
                continue;
            double parentMetric = MetricSquared(key);

            var mid = new Vector2d(
                (uv[key.Item1].X + uv[key.Item2].X) / 2,
                (uv[key.Item1].Y + uv[key.Item2].Y) / 2);
            var midPoint = EvaluateAt(surface, period, mid);

            // Inverse-evaluation jitter (~1e-9) makes iso-parameter runs only almost
            // collinear, so a chord can skip a boundary vertex by a hair; its midpoint
            // then lands exactly on that vertex's 3D position, and creating a second
            // vertex there would weld the mesh non-manifold. Snap to the coincident
            // opposite vertex instead and drop the degenerate sliver.
            int midIndex = -1;
            foreach (int t in owners)
            {
                var (_, _, c) = Rotate(triangles[t], key);
                if (c >= 0 && points[c].DistanceSquaredTo(midPoint) <= 1e-16)
                {
                    midIndex = c;
                    break;
                }
            }
            if (midIndex < 0)
            {
                midIndex = uv.Count;
                uv.Add(mid);
                points.Add(midPoint);
            }

            edgeOwners.Remove(key);
            foreach (int t in owners.ToList())
            {
                var (a, b, c) = Rotate(triangles[t], key);
                if (a < 0)
                    continue;
                if (c == midIndex)
                {
                    // The chord ran exactly through c: the sliver between them vanishes.
                    edgeOwners.TryGetValue(EdgeKey(b, c), out var bcOwners);
                    bcOwners?.Remove(t);
                    edgeOwners.TryGetValue(EdgeKey(c, a), out var caOwners);
                    caOwners?.Remove(t);
                    triangles[t] = (-1, -1, -1);
                    continue;
                }
                int fresh = triangles.Count;
                triangles[t] = (a, midIndex, c);
                triangles.Add((midIndex, b, c));

                // (c, a) stays with t; (b, c) moves to the fresh triangle.
                var moved = EdgeKey(b, c);
                if (edgeOwners.TryGetValue(moved, out var movedOwners))
                {
                    movedOwners.Remove(t);
                    movedOwners.Add(fresh);
                }
                Register(t, EdgeKey(a, midIndex));
                Register(t, EdgeKey(midIndex, c));
                Register(fresh, EdgeKey(midIndex, b));
                Register(fresh, EdgeKey(c, midIndex));

                foreach (var candidate in (ReadOnlySpan<(int, int)>)
                    [EdgeKey(a, midIndex), EdgeKey(midIndex, b), EdgeKey(midIndex, c)])
                {
                    // Monotone decrease: only strictly shorter children may continue
                    // the refinement (see the method remarks on termination).
                    if (!boundaryEdges.Contains(candidate) && Oversized(candidate) &&
                        MetricSquared(candidate) <= 0.99 * parentMetric)
                        queue.Enqueue(candidate);
                }
            }
        }
        triangles.RemoveAll(t => t.A < 0);
        return true;
    }

    /// <summary>Evaluates the surface at an unwrapped uv (periodic u brought back into the domain).</summary>
    private static Vector3d EvaluateAt(Surface surface, double period, in Vector2d uv)
    {
        double u = uv.X;
        var domainU = surface.DomainU;
        if (period > 0)
            u = domainU.Start + (((u - domainU.Start) % period) + period) % period;
        else
            u = domainU.Clamp(u);
        return surface.PointAt(u, surface.DomainV.Clamp(uv.Y));
    }

    private static (int, int) EdgeKey(int a, int b) => a < b ? (a, b) : (b, a);
}
