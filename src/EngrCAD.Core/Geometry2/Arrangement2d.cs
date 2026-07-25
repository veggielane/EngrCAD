namespace EngrCAD.Core.Geometry2;

/// <summary>
/// Planar arrangement of 2D line segments: inserted segments split existing edges (and are
/// themselves split) at every intersection, so the resulting graph has no two edges that
/// cross away from a shared vertex. Bounded cells are extracted with
/// <see cref="ExtractCells"/>.
///
/// <para><b>Coordinate model</b> (the standard snap-tolerance approach, as geometry3Sharp's
/// <c>Arrangement2d</c>): all INCIDENCE DECISIONS — which side of a line a point lies on,
/// whether an endpoint sits exactly on an edge, whether two segments cross — are made with
/// the adaptive-exact <see cref="Predicates2d.Orient2d"/> on the stored double coordinates,
/// so they are never contradictory. Intersection POINTS, however, are inevitably rounded
/// when computed in doubles; vertices closer than <see cref="VertexSnapTolerance"/> are
/// merged into one, and a rounded crossing may perturb an edge by up to the snap tolerance.
/// Exactness therefore applies to the decisions, not the constructed coordinates.</para>
///
/// <para>Insertion is O(existing edges) per segment (no acceleration structure yet — fine
/// for sketch-scale inputs). The class is a construction-time structure and allocates
/// freely; it is not a kernel hot path.</para>
/// </summary>
public sealed class Arrangement2d
{
    private readonly List<Vector2d> _vertices = [];
    private readonly List<(int A, int B)> _edges = [];
    private readonly List<List<int>> _vertexEdges = [];
    private readonly Dictionary<(long X, long Y), List<int>> _grid = [];
    private readonly double _gridCell;

    /// <summary>Vertices closer than this are merged; rounded intersection points may perturb edges by up to this distance.</summary>
    public double VertexSnapTolerance { get; }

    // Default matches Tolerance.Default.Linear (a default parameter must be a constant,
    // so it cannot reference the policy directly).
    public Arrangement2d(double vertexSnapTolerance = 1e-9)
    {
        if (!(vertexSnapTolerance > 0))
            throw new ArgumentOutOfRangeException(nameof(vertexSnapTolerance));
        VertexSnapTolerance = vertexSnapTolerance;
        _gridCell = vertexSnapTolerance * 512;
    }

    public int VertexCount => _vertices.Count;
    public int EdgeCount => _edges.Count;
    public Vector2d VertexAt(int index) => _vertices[index];
    public (int A, int B) EdgeAt(int index) => _edges[index];

    /// <summary>
    /// For every vertex, its incident edge ids sorted counter-clockwise from east, using
    /// exact comparisons only (half-plane by coordinate comparison + exact
    /// <see cref="Predicates2d.Orient2d"/>). Indexed by vertex id.
    ///
    /// <para>This fan order IS the arrangement's combinatorial embedding: walking it
    /// clockwise from the reverse of an incoming directed edge yields the next edge of the
    /// face on that edge's left. <see cref="ExtractCells"/> traces every face this way, and
    /// region-boolean boundary tracing re-walks the same order restricted to the kept
    /// boundary — they must agree, so the order lives here rather than in either caller.</para>
    /// </summary>
    public int[][] BuildCcwEdgeFans()
    {
        var fans = new int[_vertices.Count][];
        for (int v = 0; v < _vertices.Count; v++)
        {
            var list = _vertexEdges[v].ToArray();
            int vv = v;
            Array.Sort(list, (e1, e2) => CompareCcw(vv, OtherVertex(e1, vv), OtherVertex(e2, vv)));
            fans[v] = list;
        }
        return fans;
    }

    /// <summary>The endpoint of <paramref name="edge"/> that is not <paramref name="vertex"/>.</summary>
    public int OtherVertex(int edge, int vertex)
    {
        var (a, b) = _edges[edge];
        return a == vertex ? b : a;
    }

    /// <summary>The id of the edge joining two vertices, or -1 when they are not adjacent
    /// (the arrangement never holds two edges between the same vertex pair).</summary>
    public int FindEdge(int v0, int v1)
    {
        foreach (int eid in _vertexEdges[v0])
        {
            var (a, b) = _edges[eid];
            if (a == v1 || b == v1)
                return eid;
        }
        return -1;
    }

    /// <summary>Materializes a loop of vertex indices (as produced by <see cref="ExtractCells"/>) into points.</summary>
    public IReadOnlyList<Vector2d> PointsOf(IReadOnlyList<int> loop)
    {
        var points = new Vector2d[loop.Count];
        for (int i = 0; i < loop.Count; i++)
            points[i] = _vertices[loop[i]];
        return points;
    }

    /// <summary>Inserts the segments of a polyline (closing it back to the first point when <paramref name="closed"/>).</summary>
    public void InsertPolyline(IReadOnlyList<Vector2d> points, bool closed = false)
    {
        for (int i = 0; i + 1 < points.Count; i++)
            Insert(points[i], points[i + 1]);
        if (closed && points.Count > 2)
            Insert(points[^1], points[0]);
    }

    /// <summary>
    /// Inserts segment [a, b], splitting existing edges (and the new segment) at every
    /// intersection, T-junction, and collinear-overlap endpoint.
    /// </summary>
    public void Insert(Vector2d a, Vector2d b)
    {
        // Snap the endpoints to existing vertices and use their CANONICAL positions for
        // every predicate below, so incidence decisions agree with the stored graph.
        int aIdx = FindVertex(a);
        int bIdx = FindVertex(b);
        if (aIdx >= 0) a = _vertices[aIdx];
        if (bIdx >= 0) b = _vertices[bIdx];
        if (aIdx >= 0 && aIdx == bIdx)
            return;
        if (a.DistanceTo(b) <= VertexSnapTolerance)
            return; // degenerate segment
        if (aIdx < 0) aIdx = AddVertex(a);
        if (bIdx < 0) bIdx = AddVertex(b);

        // Scan existing edges with exact predicates; collect events before mutating.
        // splits: (position, existing vid or -1, whether the point also lies on [a,b]).
        var splits = new Dictionary<int, List<(double S, Vector2d Position, int Vid, bool OnAb)>>();
        var onSegment = new List<(double T, int Vid)> { (0.0, aIdx), (1.0, bIdx) };
        var abDir = b - a;
        double abLen2 = abDir.LengthSquared;

        int edgeCount = _edges.Count;
        for (int eid = 0; eid < edgeCount; eid++)
        {
            var (xi, yi) = _edges[eid];
            if ((xi == aIdx && yi == bIdx) || (xi == bIdx && yi == aIdx))
                continue;
            var x = _vertices[xi];
            var y = _vertices[yi];

            int ox = Predicates2d.Orient2dSign(a, b, x);
            int oy = Predicates2d.Orient2dSign(a, b, y);
            if (ox == oy && ox != 0)
                continue; // both endpoints strictly on one side: no intersection
            int oa = Predicates2d.Orient2dSign(x, y, a);
            int ob = Predicates2d.Orient2dSign(x, y, b);

            // Existing vertex exactly on the open segment (a, b): a point of the new
            // segment's subdivision (T-junction into the new segment, or collinear overlap).
            if (ox == 0 && xi != aIdx && xi != bIdx && StrictlyBetween(a, b, x))
                onSegment.Add((Parameter(a, abDir, abLen2, x), xi));
            if (oy == 0 && yi != aIdx && yi != bIdx && StrictlyBetween(a, b, y))
                onSegment.Add((Parameter(a, abDir, abLen2, y), yi));

            // New endpoint exactly interior to an existing edge: split that edge there.
            if (oa == 0 && aIdx != xi && aIdx != yi && StrictlyBetween(x, y, a))
                AddSplit(splits, eid, Parameter(x, y - x, (y - x).LengthSquared, a), a, aIdx, onAb: false);
            if (ob == 0 && bIdx != xi && bIdx != yi && StrictlyBetween(x, y, b))
                AddSplit(splits, eid, Parameter(x, y - x, (y - x).LengthSquared, b), b, bIdx, onAb: false);

            // Proper transversal crossing: both pairs strictly straddle. The crossing
            // POINT is computed in doubles (rounded — see the coordinate model above).
            if (ox * oy < 0 && oa * ob < 0)
            {
                var d2 = y - x;
                double denom = abDir.Cross(d2);
                double t = (x - a).Cross(d2) / denom;
                var p = a + abDir * t;
                AddSplit(splits, eid, Parameter(x, d2, d2.LengthSquared, p), p, -1, onAb: true);
            }
        }

        // Apply the collected splits, walking each edge's pieces in order.
        foreach (var (eid, list) in splits)
        {
            list.Sort((p, q) => p.S.CompareTo(q.S));
            int currentEdge = eid;
            foreach (var (_, position, existingVid, onAb) in list)
            {
                int vid = ResolveSplitVertex(currentEdge, position, existingVid);
                var (ca, cb) = _edges[currentEdge];
                if (vid != ca && vid != cb)
                    currentEdge = SplitEdge(currentEdge, vid);
                if (onAb)
                    onSegment.Add((Parameter(a, abDir, abLen2, _vertices[vid]), vid));
            }
        }

        // Connect the subdivision of [a, b], skipping duplicates and existing edges
        // (collinear overlaps dedupe here naturally).
        onSegment.Sort((p, q) => p.T.CompareTo(q.T));
        for (int i = 0; i + 1 < onSegment.Count; i++)
        {
            int v0 = onSegment[i].Vid;
            int v1 = onSegment[i + 1].Vid;
            if (v0 != v1 && FindEdge(v0, v1) < 0)
                AddEdge(v0, v1);
        }
    }

    /// <summary>
    /// Extracts the bounded cells of the arrangement as polygons: each cell has a
    /// counter-clockwise outer loop and clockwise hole loops (holes arise from island
    /// components floating inside a cell). Loops are traced with the tightest-turn
    /// (next-clockwise-from-reverse) walk — the same dance <c>FaceSplitter.SplitByCurve</c>
    /// performs in UV space — using exact angular comparisons; the unbounded outside and
    /// zero-area spur loops are dropped. Edges dangling into a cell appear in its outer
    /// loop as a doubled-back slit, as in geometry3Sharp's <c>GraphCells2d</c>.
    /// </summary>
    public IReadOnlyList<ArrangementCell2d> ExtractCells()
    {
        var outgoing = BuildCcwEdgeFans();

        // Trace every directed edge once. Directed edge id: 2*edge + (0: A→B, 1: B→A).
        var visited = new bool[_edges.Count * 2];
        var loops = new List<(int[] Vertices, double Area)>();
        for (int start = 0; start < visited.Length; start++)
        {
            if (visited[start])
                continue;
            var loop = new List<int>();
            int current = start;
            int guard = visited.Length + 4;
            while (true)
            {
                visited[current] = true;
                int edge = current >> 1;
                var (ea, eb) = _edges[edge];
                int from = (current & 1) == 0 ? ea : eb;
                int to = (current & 1) == 0 ? eb : ea;
                loop.Add(from);

                // Next directed edge: at 'to', take the edge clockwise-next from the
                // reverse direction (to → from) — bounded cells come out CCW.
                var fan = outgoing[to];
                int reverseIndex = Array.IndexOf(fan, edge);
                if (reverseIndex < 0)
                    throw new InvalidOperationException("Arrangement cell tracing lost its reverse edge.");
                int nextEdge = fan[(reverseIndex - 1 + fan.Length) % fan.Length];
                var (na, nb) = _edges[nextEdge];
                current = na == to ? nextEdge << 1 : (nextEdge << 1) | 1;

                if (current == start)
                    break;
                if (--guard < 0)
                    throw new InvalidOperationException("Arrangement cell tracing did not close.");
            }
            loops.Add((loop.ToArray(), LoopSignedArea(loop)));
        }

        // Positive loops bound cells; negative loops are island perimeters — holes of the
        // smallest strictly-larger positive loop containing them, or the unbounded outside
        // (dropped) when uncontained. Zero-area loops (pure spurs) are dropped.
        var cellLoops = loops.Where(l => l.Area > 0).ToList();
        var holeAssignments = cellLoops.Select(_ => new List<int[]>()).ToList();
        foreach (var (holeVertices, holeArea) in loops.Where(l => l.Area < 0))
        {
            var probe = _vertices[holeVertices[0]];
            int best = -1;
            double bestArea = double.PositiveInfinity;
            for (int i = 0; i < cellLoops.Count; i++)
            {
                double area = cellLoops[i].Area;
                if (area > -holeArea && area < bestArea && LoopContains(cellLoops[i].Vertices, probe))
                {
                    best = i;
                    bestArea = area;
                }
            }
            if (best >= 0)
                holeAssignments[best].Add(holeVertices);
        }

        var cells = new List<ArrangementCell2d>(cellLoops.Count);
        for (int i = 0; i < cellLoops.Count; i++)
        {
            double area = cellLoops[i].Area;
            foreach (var hole in holeAssignments[i])
                area += LoopSignedArea(hole); // hole areas are negative
            cells.Add(new ArrangementCell2d(
                cellLoops[i].Vertices,
                PointsOf(cellLoops[i].Vertices),
                holeAssignments[i].Select(h => (IReadOnlyList<int>)h).ToList(),
                holeAssignments[i].Select(h => PointsOf(h)).ToList(),
                area));
        }
        return cells;
    }

    // ---- vertices, edges, snapping ----

    private int AddVertex(Vector2d position)
    {
        int vid = _vertices.Count;
        _vertices.Add(position);
        _vertexEdges.Add([]);
        var key = GridKey(position);
        if (!_grid.TryGetValue(key, out var bucket))
            _grid[key] = bucket = [];
        bucket.Add(vid);
        return vid;
    }

    private void AddEdge(int v0, int v1)
    {
        int eid = _edges.Count;
        _edges.Add((v0, v1));
        _vertexEdges[v0].Add(eid);
        _vertexEdges[v1].Add(eid);
    }

    /// <summary>Splits edge <paramref name="eid"/> at existing vertex <paramref name="vid"/>; returns the id of the far half (vid → old B).</summary>
    private int SplitEdge(int eid, int vid)
    {
        var (va, vb) = _edges[eid];
        _edges[eid] = (va, vid);
        _vertexEdges[vb].Remove(eid);
        _vertexEdges[vid].Add(eid);
        int newEid = _edges.Count;
        _edges.Add((vid, vb));
        _vertexEdges[vid].Add(newEid);
        _vertexEdges[vb].Add(newEid);
        return newEid;
    }

    private int ResolveSplitVertex(int currentEdge, Vector2d position, int existingVid)
    {
        if (existingVid >= 0)
            return existingVid;
        // Prefer the current piece's endpoints (a crossing within snap tolerance of an
        // endpoint reuses it), then any existing vertex, then a new one.
        var (va, vb) = _edges[currentEdge];
        if (_vertices[va].DistanceTo(position) <= VertexSnapTolerance)
            return va;
        if (_vertices[vb].DistanceTo(position) <= VertexSnapTolerance)
            return vb;
        int found = FindVertex(position);
        return found >= 0 ? found : AddVertex(position);
    }

    private int FindVertex(Vector2d position)
    {
        long x0 = GridCoordinate((position.X - VertexSnapTolerance) / _gridCell);
        long x1 = GridCoordinate((position.X + VertexSnapTolerance) / _gridCell);
        long y0 = GridCoordinate((position.Y - VertexSnapTolerance) / _gridCell);
        long y1 = GridCoordinate((position.Y + VertexSnapTolerance) / _gridCell);
        int best = -1;
        double bestDistance = double.PositiveInfinity;
        for (long gx = x0; gx <= x1; gx++)
        for (long gy = y0; gy <= y1; gy++)
        {
            if (!_grid.TryGetValue((gx, gy), out var bucket))
                continue;
            foreach (int vid in bucket)
            {
                double distance = _vertices[vid].DistanceTo(position);
                if (distance <= VertexSnapTolerance && distance < bestDistance)
                {
                    best = vid;
                    bestDistance = distance;
                }
            }
        }
        return best;
    }

    private (long, long) GridKey(Vector2d position) =>
        (GridCoordinate(position.X / _gridCell), GridCoordinate(position.Y / _gridCell));

    private static long GridCoordinate(double scaled)
    {
        double floor = Math.Floor(scaled);
        // Saturate huge coordinates into boundary buckets: lookups stay correct because
        // every candidate is distance-checked; only bucketing granularity degrades.
        return floor >= long.MaxValue ? long.MaxValue - 1
             : floor <= long.MinValue ? long.MinValue + 1
             : (long)floor;
    }

    // ---- geometric helpers ----

    private static void AddSplit(
        Dictionary<int, List<(double S, Vector2d Position, int Vid, bool OnAb)>> splits,
        int eid, double s, Vector2d position, int vid, bool onAb)
    {
        if (!splits.TryGetValue(eid, out var list))
            splits[eid] = list = [];
        list.Add((s, position, vid, onAb));
    }

    /// <summary>Projection parameter of p along origin+direction (rounded; used only for ordering).</summary>
    private static double Parameter(Vector2d origin, Vector2d direction, double lengthSquared, Vector2d p) =>
        (p - origin).Dot(direction) / lengthSquared;

    /// <summary>
    /// For p exactly on the line through a and b (Orient2d == 0): is p strictly between
    /// them? Decided by exact coordinate comparison on the dominant axis.
    /// </summary>
    private static bool StrictlyBetween(Vector2d a, Vector2d b, Vector2d p)
    {
        if (Math.Abs(b.X - a.X) >= Math.Abs(b.Y - a.Y))
            return a.X < b.X ? a.X < p.X && p.X < b.X : b.X < p.X && p.X < a.X;
        return a.Y < b.Y ? a.Y < p.Y && p.Y < b.Y : b.Y < p.Y && p.Y < a.Y;
    }

    /// <summary>
    /// Exact counter-clockwise-from-east angular order of neighbors w1, w2 around v:
    /// half-plane split by coordinate comparison, then the exact orientation sign.
    /// </summary>
    private int CompareCcw(int v, int w1, int w2)
    {
        if (w1 == w2)
            return 0;
        var pv = _vertices[v];
        var p1 = _vertices[w1];
        var p2 = _vertices[w2];
        int h1 = HalfPlane(pv, p1);
        int h2 = HalfPlane(pv, p2);
        if (h1 != h2)
            return h1 - h2;
        return -Predicates2d.Orient2dSign(pv, p1, p2);
    }

    /// <summary>0 for directions with angle in [0, π) (east axis inclusive), 1 for [π, 2π).</summary>
    private static int HalfPlane(Vector2d from, Vector2d to) =>
        to.Y > from.Y || (to.Y == from.Y && to.X > from.X) ? 0 : 1;

    private double LoopSignedArea(IReadOnlyList<int> loop)
    {
        // Shoelace anchored at the first vertex for stability far from the origin.
        var p0 = _vertices[loop[0]];
        double sum = 0;
        for (int i = 1; i + 1 < loop.Count; i++)
            sum += (_vertices[loop[i]] - p0).Cross(_vertices[loop[i + 1]] - p0);
        return 0.5 * sum;
    }

    /// <summary>Even-odd parity containment of a point in a vertex-index loop (half-open upward crossings).</summary>
    private bool LoopContains(IReadOnlyList<int> loop, Vector2d point)
    {
        bool inside = false;
        for (int i = 0; i < loop.Count; i++)
        {
            var p = _vertices[loop[i]];
            var q = _vertices[loop[(i + 1) % loop.Count]];
            if (p.Y > point.Y == q.Y > point.Y)
                continue;
            double xCross = p.X + (point.Y - p.Y) / (q.Y - p.Y) * (q.X - p.X);
            if (point.X < xCross)
                inside = !inside;
        }
        return inside;
    }
}
