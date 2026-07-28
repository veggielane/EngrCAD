namespace EngrCAD.Core.Geometry2;

/// <summary>
/// Planar arrangement whose edges are CURVES — straight segments and circular arcs
/// (<see cref="CurvedEdge2d"/>). Inserted edges split existing ones (and are themselves
/// split) at every contact, so the resulting graph has no two edges that cross away from a
/// shared vertex; bounded cells are extracted with <see cref="ExtractCells"/>.
///
/// <para><b>Why a parallel type and not an extension of <see cref="Arrangement2d"/>.</b>
/// The straight arrangement is boolean-critical: <c>Region2dBoolean</c>, <c>Region2dOffset</c>,
/// every planar section and silhouette, and the rendered docs images all sit on it, and its
/// output is pinned bit-for-bit. Teaching it curves would change its vertex fan comparator
/// (positions to tangents), its edge identity (a vertex pair to a vertex pair plus a
/// carrier) and its area rule — three changes at once inside the code with the widest
/// regression surface. This type shares the exact predicates and the algorithms' shape
/// instead, and the straight path stays untouched. It is the same call design.md §5 makes
/// for <c>FaceSplitter</c>: do not unify boolean-critical machinery.</para>
///
/// <para><b>Coordinate model.</b> As <see cref="Arrangement2d"/>: incidence DECISIONS are
/// made as exactly as the shapes allow — straight-versus-straight goes through
/// <see cref="Predicates2d.Orient2dSign"/> and is exact for all finite doubles, and the
/// vertex fan's angular order is an exact orientation sign on the departure directions —
/// while intersection POINTS are inevitably rounded. Everything involving a circle is posed
/// so its threshold is a LENGTH, and that length is <see cref="VertexSnapTolerance"/>: no
/// second epsilon exists in this type or in <see cref="CurveIntersection2d"/>.</para>
///
/// <para><b>The tangential case is decidable, and that is why the tier stops at arcs.</b>
/// The cell walk orders edges at a node by their departure TANGENT, with the departure
/// CURVATURE as the tie-break. For lines and circles, agreeing in both at a point means
/// having the same carrier — a line and a circle never osculate, and two circles that
/// osculate are one circle — so the tie-break is complete and the walk never guesses. A
/// third shape (a Bézier, a general NURBS) breaks that: two distinct curves can agree to
/// second order and separate only in the third derivative, so the rule would need a jet of
/// unbounded order. Béziers are therefore flattened before they reach here, and the
/// comparator REFUSES rather than guessing if it is ever handed a second-order tie between
/// different carriers.</para>
/// </summary>
public sealed class CurvedArrangement2d
{
    private readonly List<Vector2d> _vertices = [];
    private readonly List<Entry> _edges = [];
    private readonly List<List<int>> _vertexEdges = [];
    private readonly Dictionary<(long X, long Y), List<int>> _vertexGrid = [];
    private readonly double _vertexCell;

    // ---- edge broad phase (the scheme Arrangement2d proved: midpoint cell + overflow) ----
    private readonly Dictionary<(long X, long Y), List<int>> _edgeGrid = [];
    private readonly List<int> _oversizedEdges = [];
    private readonly List<int> _candidates = [];
    private int[] _edgeStamp = [];
    private int _stamp;
    private double _edgeCell;
    private int _rebuildAt = MinIndexedEdges;

    private const int MinIndexedEdges = 128;
    private const int MaxCellsPerEdge = 32;

    private readonly record struct Entry(int A, int B, CurvedEdge2d Geometry);

    /// <summary>Vertices closer than this merge; every curve decision in this type is posed
    /// against it as a distance. Defaults to the 1e-9 absolute weld tier.</summary>
    public double VertexSnapTolerance { get; }

    /// <param name="vertexSnapTolerance">Vertex merge distance; also the tangency and
    /// on-curve threshold (a default parameter must be a constant, so it cannot reference
    /// <c>Tolerance.Default.Linear</c> directly).</param>
    public CurvedArrangement2d(double vertexSnapTolerance = 1e-9)
    {
        if (!(vertexSnapTolerance > 0))
            throw new ArgumentOutOfRangeException(nameof(vertexSnapTolerance));
        VertexSnapTolerance = vertexSnapTolerance;
        _vertexCell = vertexSnapTolerance * 512;
    }

    /// <summary>Number of vertices in the arrangement.</summary>
    public int VertexCount => _vertices.Count;

    /// <summary>Number of edges in the arrangement.</summary>
    public int EdgeCount => _edges.Count;

    /// <summary>Position of a vertex.</summary>
    public Vector2d VertexAt(int index) => _vertices[index];

    /// <summary>The endpoint vertex indices of an edge.</summary>
    public (int A, int B) EdgeVerticesAt(int index) => (_edges[index].A, _edges[index].B);

    /// <summary>The geometry of an edge, oriented from its A vertex to its B vertex.</summary>
    public CurvedEdge2d EdgeAt(int index) => _edges[index].Geometry;

    /// <summary>The geometry of a DIRECTED edge id (2·edge for A→B, 2·edge+1 for B→A).</summary>
    public CurvedEdge2d DirectedEdgeAt(int directed) =>
        (directed & 1) == 0 ? _edges[directed >> 1].Geometry : _edges[directed >> 1].Geometry.Reversed();

    /// <summary>The directed-edge id that LEAVES <paramref name="vertex"/> along
    /// <paramref name="edge"/>.</summary>
    public int DirectedFrom(int edge, int vertex) =>
        _edges[edge].A == vertex ? edge << 1 : (edge << 1) | 1;

    /// <summary>The endpoint of <paramref name="edge"/> that is not <paramref name="vertex"/>.</summary>
    public int OtherVertex(int edge, int vertex)
    {
        var entry = _edges[edge];
        return entry.A == vertex ? entry.B : entry.A;
    }

    /// <summary>Inserts a closed chain of edges (each edge's end is the next one's start).</summary>
    public void InsertLoop(IReadOnlyList<CurvedEdge2d> loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        foreach (var edge in loop)
            Insert(edge);
    }

    /// <summary>Inserts every loop of a curved region.</summary>
    public void Insert(CurvedRegion2d region)
    {
        ArgumentNullException.ThrowIfNull(region);
        foreach (var loop in region.AllLoops())
            InsertLoop(loop);
    }

    /// <summary>
    /// Inserts one edge, splitting existing edges (and the new edge) at every contact.
    /// Arcs spanning more than half a turn are halved first, so no edge of the graph ever
    /// closes on itself — the fan order and the cell walk both assume an edge meets each of
    /// its endpoints once.
    /// </summary>
    public void Insert(CurvedEdge2d edge)
    {
        if (edge.IsArc && Math.Abs(edge.SweepAngle) > Math.PI)
        {
            Insert(edge.Sub(0, 0.5));
            Insert(edge.Sub(0.5, 1));
            return;
        }

        int aIdx = FindVertex(edge.Start);
        int bIdx = FindVertex(edge.End);
        var start = aIdx >= 0 ? _vertices[aIdx] : edge.Start;
        var end = bIdx >= 0 ? _vertices[bIdx] : edge.End;
        if (aIdx >= 0 && aIdx == bIdx)
            return;
        if (start.DistanceTo(end) <= VertexSnapTolerance)
            return;   // degenerate after snapping
        if (aIdx < 0) aIdx = AddVertex(start);
        if (bIdx < 0) bIdx = AddVertex(end);
        edge = edge.WithEndpoints(_vertices[aIdx], _vertices[bIdx]);

        if (_edges.Count >= _rebuildAt)
            RebuildEdgeIndex();

        // Contacts are collected before anything is mutated, exactly as Arrangement2d does:
        // splits per existing edge, and the new edge's own subdivision parameters.
        var splits = new Dictionary<int, List<Split>>();
        var onNew = new List<(double T, int Vid)> { (0, aIdx), (1, bIdx) };
        var contacts = new List<CurveIntersection2d.Contact>();

        int edgeCount = _edges.Count;
        var scan = CandidateEdges(edge, edgeCount);
        for (int c = 0; c < scan.Count; c++)
        {
            int eid = scan[c];
            var other = _edges[eid];
            contacts.Clear();
            CurveIntersection2d.Intersect(edge, other.Geometry, VertexSnapTolerance, contacts);
            foreach (var contact in contacts)
            {
                var point = contact.Point;
                bool atOtherStart = _vertices[other.A].DistanceTo(point) <= VertexSnapTolerance;
                bool atOtherEnd = _vertices[other.B].DistanceTo(point) <= VertexSnapTolerance;
                if (atOtherStart || atOtherEnd)
                {
                    // The contact is an existing VERTEX: it subdivides the new edge only.
                    int vid = atOtherStart ? other.A : other.B;
                    if (vid != aIdx && vid != bIdx)
                        onNew.Add((edge.ParameterOf(_vertices[vid]), vid));
                    continue;
                }
                // Interior to the existing edge: it must split there, and (unless the point
                // is one of the new edge's own endpoints) subdivide the new edge too.
                bool onNewInterior =
                    edge.Start.DistanceTo(point) > VertexSnapTolerance
                    && edge.End.DistanceTo(point) > VertexSnapTolerance;
                AddSplit(splits, eid, new Split(contact.Tb, point, onNewInterior ? contact.Ta : double.NaN));
            }
        }

        foreach (var (eid, list) in splits)
        {
            list.Sort(static (p, q) => p.T.CompareTo(q.T));
            int currentEdge = eid;
            foreach (var split in list)
            {
                int vid = ResolveSplitVertex(currentEdge, split.Position);
                var entry = _edges[currentEdge];
                if (vid != entry.A && vid != entry.B)
                    currentEdge = SplitEdge(currentEdge, vid);
                if (!double.IsNaN(split.TOnNew))
                    onNew.Add((edge.ParameterOf(_vertices[vid]), vid));
            }
        }

        // Connect the new edge's subdivision, skipping pieces an identical edge already
        // covers (which is how collinear and cocircular overlaps dedupe: both sides were
        // split at every shared endpoint above, so the overlapping pieces now match).
        onNew.Sort(static (p, q) => p.T.CompareTo(q.T));
        for (int i = 0; i + 1 < onNew.Count; i++)
        {
            int v0 = onNew[i].Vid;
            int v1 = onNew[i + 1].Vid;
            if (v0 == v1)
                continue;
            var piece = edge.Sub(onNew[i].T, onNew[i + 1].T, _vertices[v0], _vertices[v1]);
            if (FindEdge(v0, v1, piece) < 0)
                AddEdge(v0, v1, piece);
        }
    }

    /// <summary>
    /// For every vertex, its incident edge ids sorted counter-clockwise from east by
    /// DEPARTURE TANGENT, with departure CURVATURE as the tie-break (see the class remarks
    /// for why that is complete for lines and arcs). This fan order IS the arrangement's
    /// combinatorial embedding, and both <see cref="ExtractCells"/> and
    /// <see cref="CurvedRegion2dBoolean"/> walk it, so it lives here.
    /// </summary>
    public int[][] BuildCcwEdgeFans()
    {
        var fans = new int[_vertices.Count][];
        for (int v = 0; v < _vertices.Count; v++)
        {
            var list = _vertexEdges[v].ToArray();
            int vv = v;
            Array.Sort(list, (e1, e2) => CompareCcw(vv, e1, e2));
            OrderTangentialRuns(v, list);
            fans[v] = list;
        }
        return fans;
    }

    /// <summary>
    /// Extracts the bounded cells of the arrangement: each has a counter-clockwise outer
    /// chain and clockwise hole chains (holes arise from island components floating inside a
    /// cell). Loops are traced with the tightest-turn walk over
    /// <see cref="BuildCcwEdgeFans"/>; the unbounded outside and zero-area spur loops are
    /// dropped, and a hole must come from a DIFFERENT connected component than the cell it
    /// fills (the union-find rule <see cref="Arrangement2d"/> learned the hard way — a
    /// cell's own reversed perimeter has an area equal to the cell's up to ulps).
    /// </summary>
    public IReadOnlyList<CurvedCell2d> ExtractCells()
    {
        var fans = BuildCcwEdgeFans();
        var visited = new bool[_edges.Count * 2];
        var loops = new List<(int[] Directed, double Area)>();

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
                loop.Add(current);
                int edge = current >> 1;
                var entry = _edges[edge];
                int to = (current & 1) == 0 ? entry.B : entry.A;

                var fan = fans[to];
                int reverseIndex = Array.IndexOf(fan, edge);
                if (reverseIndex < 0)
                    throw new InvalidOperationException("Curved arrangement cell tracing lost its reverse edge.");
                int nextEdge = fan[(reverseIndex - 1 + fan.Length) % fan.Length];
                current = _edges[nextEdge].A == to ? nextEdge << 1 : (nextEdge << 1) | 1;

                if (current == start)
                    break;
                if (--guard < 0)
                    throw new InvalidOperationException("Curved arrangement cell tracing did not close.");
            }
            loops.Add((loop.ToArray(), LoopSignedArea(loop)));
        }

        var component = ConnectedComponents();
        var cellLoops = loops.Where(l => l.Area > 0).ToList();
        var holeAssignments = cellLoops.Select(_ => new List<int[]>()).ToList();
        foreach (var (holeDirected, holeArea) in loops.Where(l => l.Area < 0))
        {
            var probe = DirectedEdgeAt(holeDirected[0]).Start;
            int holeComponent = component[FromVertex(holeDirected[0])];
            int best = -1;
            double bestArea = double.PositiveInfinity;
            for (int i = 0; i < cellLoops.Count; i++)
            {
                double area = cellLoops[i].Area;
                if (component[FromVertex(cellLoops[i].Directed[0])] == holeComponent)
                    continue;
                if (area > -holeArea && area < bestArea && LoopContains(cellLoops[i].Directed, probe))
                {
                    best = i;
                    bestArea = area;
                }
            }
            if (best >= 0)
                holeAssignments[best].Add(holeDirected);
        }

        var cells = new List<CurvedCell2d>(cellLoops.Count);
        for (int i = 0; i < cellLoops.Count; i++)
        {
            double area = cellLoops[i].Area;
            foreach (var hole in holeAssignments[i])
                area += LoopSignedArea(hole);
            cells.Add(new CurvedCell2d(
                cellLoops[i].Directed,
                EdgesOf(cellLoops[i].Directed),
                [.. holeAssignments[i]],
                [.. holeAssignments[i].Select(EdgesOf)],
                area));
        }
        return cells;
    }

    /// <summary>Materializes a directed-edge loop into its curve chain.</summary>
    public IReadOnlyList<CurvedEdge2d> EdgesOf(IReadOnlyList<int> directedLoop)
    {
        var chain = new CurvedEdge2d[directedLoop.Count];
        for (int i = 0; i < directedLoop.Count; i++)
            chain[i] = DirectedEdgeAt(directedLoop[i]);
        return chain;
    }

    /// <summary>Signed area of a directed-edge loop (exact for lines and arcs).</summary>
    public double LoopSignedArea(IReadOnlyList<int> directedLoop)
    {
        if (directedLoop.Count == 0)
            return 0;
        var anchor = DirectedEdgeAt(directedLoop[0]).Start;
        double sum = 0;
        foreach (int directed in directedLoop)
            sum += DirectedEdgeAt(directed).SignedAreaTerm(anchor);
        return sum;
    }

    /// <summary>Even–odd containment of a point in a directed-edge loop.</summary>
    public bool LoopContains(IReadOnlyList<int> directedLoop, in Vector2d point)
    {
        int crossings = 0;
        foreach (int directed in directedLoop)
            crossings += DirectedEdgeAt(directed).RayCrossings(point);
        return (crossings & 1) != 0;
    }

    /// <summary>
    /// The id of the edge between two vertices carrying the same geometry as
    /// <paramref name="piece"/>, or −1. Identity is the vertex pair PLUS the carrier and the
    /// turn direction: two points on one circle are joined by two different arcs, and a
    /// chord and its arc join the same pair as well.
    /// </summary>
    public int FindEdge(int v0, int v1, in CurvedEdge2d piece)
    {
        foreach (int eid in _vertexEdges[v0])
        {
            var entry = _edges[eid];
            if (entry.A != v1 && entry.B != v1)
                continue;
            var geometry = entry.A == v0 ? entry.Geometry : entry.Geometry.Reversed();
            if (!CurveIntersection2d.SameCarrier(geometry, piece, VertexSnapTolerance))
                continue;
            if (geometry.IsArc && Math.Sign(geometry.SweepAngle) != Math.Sign(piece.SweepAngle))
                continue;
            return eid;
        }
        return -1;
    }

    // ---- vertices and edges ----

    private int FromVertex(int directed) =>
        (directed & 1) == 0 ? _edges[directed >> 1].A : _edges[directed >> 1].B;

    private int AddVertex(in Vector2d position)
    {
        int vid = _vertices.Count;
        _vertices.Add(position);
        _vertexEdges.Add([]);
        var key = GridKey(position);
        if (!_vertexGrid.TryGetValue(key, out var bucket))
            _vertexGrid[key] = bucket = [];
        bucket.Add(vid);
        return vid;
    }

    private void AddEdge(int v0, int v1, in CurvedEdge2d geometry)
    {
        int eid = _edges.Count;
        _edges.Add(new Entry(v0, v1, geometry));
        _vertexEdges[v0].Add(eid);
        _vertexEdges[v1].Add(eid);
        IndexEdge(eid);
    }

    /// <summary>Splits an edge at an existing vertex; returns the id of the far half.</summary>
    private int SplitEdge(int eid, int vid)
    {
        var entry = _edges[eid];
        var position = _vertices[vid];
        double t = entry.Geometry.ParameterOf(position);
        var near = entry.Geometry.Sub(0, t, entry.Geometry.Start, position);
        var far = entry.Geometry.Sub(t, 1, position, entry.Geometry.End);

        _edges[eid] = new Entry(entry.A, vid, near);
        _vertexEdges[entry.B].Remove(eid);
        _vertexEdges[vid].Add(eid);

        int newEid = _edges.Count;
        _edges.Add(new Entry(vid, entry.B, far));
        _vertexEdges[vid].Add(newEid);
        _vertexEdges[entry.B].Add(newEid);
        // eid's index entry now over-approximates its shortened footprint, which is safe;
        // the far half needs its own.
        IndexEdge(newEid);
        return newEid;
    }

    private int ResolveSplitVertex(int currentEdge, in Vector2d position)
    {
        var entry = _edges[currentEdge];
        if (_vertices[entry.A].DistanceTo(position) <= VertexSnapTolerance)
            return entry.A;
        if (_vertices[entry.B].DistanceTo(position) <= VertexSnapTolerance)
            return entry.B;
        int found = FindVertex(position);
        return found >= 0 ? found : AddVertex(position);
    }

    private int FindVertex(in Vector2d position)
    {
        long x0 = GridCoordinate((position.X - VertexSnapTolerance) / _vertexCell);
        long x1 = GridCoordinate((position.X + VertexSnapTolerance) / _vertexCell);
        long y0 = GridCoordinate((position.Y - VertexSnapTolerance) / _vertexCell);
        long y1 = GridCoordinate((position.Y + VertexSnapTolerance) / _vertexCell);
        int best = -1;
        double bestDistance = double.PositiveInfinity;
        for (long gx = x0; gx <= x1; gx++)
        for (long gy = y0; gy <= y1; gy++)
        {
            if (!_vertexGrid.TryGetValue((gx, gy), out var bucket))
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

    private (long, long) GridKey(in Vector2d position) =>
        (GridCoordinate(position.X / _vertexCell), GridCoordinate(position.Y / _vertexCell));

    private static long GridCoordinate(double scaled)
    {
        double floor = Math.Floor(scaled);
        return floor >= long.MaxValue ? long.MaxValue - 1
             : floor <= long.MinValue ? long.MinValue + 1
             : (long)floor;
    }

    // ---- edge broad phase ----

    private void IndexEdge(int eid)
    {
        if (_edgeCell <= 0)
            return;
        var box = _edges[eid].Geometry.Bounds();
        double pad = VertexSnapTolerance;
        double extent = Math.Max(box.Max.X - box.Min.X, box.Max.Y - box.Min.Y) + 2 * pad;
        if (!(extent <= _edgeCell))
        {
            _oversizedEdges.Add(eid);
            return;
        }
        var key = (GridCoordinate(0.5 * (box.Min.X + box.Max.X) / _edgeCell),
                   GridCoordinate(0.5 * (box.Min.Y + box.Max.Y) / _edgeCell));
        if (!_edgeGrid.TryGetValue(key, out var bucket))
            _edgeGrid[key] = bucket = [];
        bucket.Add(eid);
    }

    private void RebuildEdgeIndex()
    {
        _edgeGrid.Clear();
        _oversizedEdges.Clear();
        _rebuildAt = Math.Max(MinIndexedEdges, _edges.Count * 2);

        double total = 0;
        foreach (var entry in _edges)
        {
            var box = entry.Geometry.Bounds();
            total += Math.Max(box.Max.X - box.Min.X, box.Max.Y - box.Min.Y);
        }
        double mean = _edges.Count > 0 ? total / _edges.Count : 0;
        _edgeCell = Math.Max(4 * mean, VertexSnapTolerance);

        for (int eid = 0; eid < _edges.Count; eid++)
            IndexEdge(eid);
    }

    private List<int> CandidateEdges(in CurvedEdge2d edge, int edgeCount)
    {
        _candidates.Clear();
        var box = edge.Bounds();
        if (_edgeCell <= 0
            || !TryQueryCellRange(box, edgeCount, out long x0, out long y0, out long x1, out long y1))
        {
            for (int eid = 0; eid < edgeCount; eid++)
                _candidates.Add(eid);
            return _candidates;
        }

        if (_edgeStamp.Length < _edges.Count)
            Array.Resize(ref _edgeStamp, Math.Max(_edges.Count, 2 * _edgeStamp.Length + 16));
        int stamp = ++_stamp;

        foreach (int eid in _oversizedEdges)
        {
            if (eid < edgeCount && _edgeStamp[eid] != stamp)
            {
                _edgeStamp[eid] = stamp;
                _candidates.Add(eid);
            }
        }
        for (long gx = x0; gx <= x1; gx++)
        for (long gy = y0; gy <= y1; gy++)
        {
            if (!_edgeGrid.TryGetValue((gx, gy), out var bucket))
                continue;
            foreach (int eid in bucket)
            {
                if (eid < edgeCount && _edgeStamp[eid] != stamp)
                {
                    _edgeStamp[eid] = stamp;
                    _candidates.Add(eid);
                }
            }
        }
        return _candidates;
    }

    private bool TryQueryCellRange(
        in Aabb box, long edgeCount, out long x0, out long y0, out long x1, out long y1)
    {
        double pad = VertexSnapTolerance;
        x0 = GridCoordinate((box.Min.X - pad) / _edgeCell) - 1;
        x1 = GridCoordinate((box.Max.X + pad) / _edgeCell) + 1;
        y0 = GridCoordinate((box.Min.Y - pad) / _edgeCell) - 1;
        y1 = GridCoordinate((box.Max.Y + pad) / _edgeCell) + 1;
        if (x1 < x0 || y1 < y0)
            return false;
        long budget = Math.Max(MaxCellsPerEdge, edgeCount);
        long columns = x1 - x0 + 1;
        long rows = y1 - y0 + 1;
        return columns <= budget && rows <= budget && columns * rows <= budget;
    }

    // ---- the fan comparator ----

    /// <summary>
    /// Counter-clockwise-from-east order of two edges leaving <paramref name="v"/>: the
    /// half-plane by coordinate comparison, then the EXACT orientation sign of the two
    /// departure directions, then the departure curvature, then the edge id so the order is
    /// always total and consistent (an inconsistent comparator makes <c>Array.Sort</c>
    /// produce garbage, which is worse than any ordering choice).
    ///
    /// <para>This is only the FIRST pass. Where two edges are tangent at the node their
    /// direction sign is round-off, not information, and
    /// <see cref="OrderTangentialRuns"/> re-orders those runs by curvature afterwards.</para>
    /// </summary>
    private int CompareCcw(int v, int e1, int e2)
    {
        if (e1 == e2)
            return 0;
        var d1 = Departure(v, e1, out double k1);
        var d2 = Departure(v, e2, out double k2);
        int h1 = HalfPlane(d1);
        int h2 = HalfPlane(d2);
        if (h1 != h2)
            return h1 - h2;
        int turn = -Predicates2d.Orient2dSign(Vector2d.Zero, d1, d2);
        if (turn != 0)
            return turn;
        int curvature = k1.CompareTo(k2);
        return curvature != 0 ? curvature : e1.CompareTo(e2);
    }

    /// <summary>
    /// Re-orders each cyclic run of TANGENTIALLY TIED departures by curvature.
    ///
    /// <para><b>Why this pass exists — the tangency lesson.</b> Where two edges leave a node
    /// with the same tangent, their computed directions differ only by round-off, and an
    /// exact predicate on those numbers therefore makes a CONFIDENT WRONG decision. Measured:
    /// a disc tangent to a plate's edge from outside gave the arc a departure of
    /// (−1.22e-16, −1) — the sign of the x component is nothing but the error in
    /// <c>sin(π)</c> — which put it on the wrong side of the plate's exactly vertical edge,
    /// the tightest-turn walk closed no face at all, and the union came back EMPTY. Exactness
    /// buys nothing when the coordinates it decides are noise; only the curvature carries
    /// information there. (design.md §5 records the same shape of finding for
    /// <c>FaceSplitter.DepartureAngle</c>.)</para>
    ///
    /// <para><b>The tie band is DERIVED, not chosen.</b> A vertex may sit up to
    /// <see cref="VertexSnapTolerance"/> from the true tangency point, and displacing a point
    /// by δ along a circle of radius r rotates its radial — and so its tangent — by δ/r =
    /// δ·|κ|. So two departures are indistinguishable in direction whenever their unit
    /// vectors' cross product falls below <c>snap·max(|κ₁|, |κ₂|)</c> plus an arithmetic
    /// floor of a few ulps. For two straight edges the curvature term vanishes and only the
    /// floor remains, so genuinely distinct line directions are still decided exactly — a
    /// near-parallel pair of segments keeps the orientation sign's answer, since equal
    /// curvature makes the re-order a stable no-op.</para>
    /// </summary>
    private void OrderTangentialRuns(int v, int[] fan)
    {
        int n = fan.Length;
        if (n < 2)
            return;

        var directions = new Vector2d[n];
        var curvatures = new double[n];
        for (int i = 0; i < n; i++)
            directions[i] = Departure(v, fan[i], out curvatures[i]);

        var tied = new bool[n];
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            tied[i] = IsTangentiallyTied(directions[i], curvatures[i], directions[j], curvatures[j]);
            any |= tied[i];
        }
        if (!any)
            return;

        // Runs are CYCLIC: a tangent along the +x axis puts one departure at the very start
        // of the fan and its partner at the very end, so the wrap has to be walked.
        int start = -1;
        for (int i = 0; i < n; i++)
        {
            if (!tied[(i - 1 + n) % n])
            {
                start = i;
                break;
            }
        }
        if (start < 0)
        {
            SortRun(v, fan, curvatures, 0, n, n);
            return;
        }

        int index = 0;
        while (index < n)
        {
            int length = 1;
            while (length < n && tied[(start + index + length - 1) % n])
                length++;
            if (length > 1)
                SortRun(v, fan, curvatures, start + index, length, n);
            index += length;
        }
    }

    /// <summary>Stable sort of one cyclic run of the fan by ascending departure curvature —
    /// the larger signed curvature sits further counter-clockwise, from
    /// p(s) = v + s·d + ½s²κ·n̂.</summary>
    private void SortRun(int v, int[] fan, double[] curvatures, int from, int length, int n)
    {
        var order = new int[length];
        for (int k = 0; k < length; k++)
            order[k] = k;
        // Stable by construction: equal curvature falls back to the incoming position, so a
        // near-parallel pair of STRAIGHT edges keeps the exact orientation sign's answer.
        Array.Sort(order, (p, q) =>
        {
            int byCurvature = curvatures[(from + p) % n].CompareTo(curvatures[(from + q) % n]);
            return byCurvature != 0 ? byCurvature : p.CompareTo(q);
        });

        var ids = new int[length];
        var values = new double[length];
        for (int k = 0; k < length; k++)
        {
            ids[k] = fan[(from + order[k]) % n];
            values[k] = curvatures[(from + order[k]) % n];
        }
        for (int k = 0; k + 1 < length; k++)
        {
            // Equal tangent AND equal curvature means the two carriers osculate. For lines
            // and circles that implies one carrier, which insertion already deduped — so
            // reaching this state means the input is outside the tier, and the walk refuses
            // rather than picking one. (Two straight edges tie at curvature 0 legitimately;
            // they are ordered by the exact sign above and never reach here.)
            if (values[k] == values[k + 1] && (EdgeAt(ids[k]).IsArc || EdgeAt(ids[k + 1]).IsArc))
            {
                throw new InvalidOperationException(
                    $"Curved arrangement cannot order two edges leaving {_vertices[v]}: they share a "
                    + "tangent and a curvature but not a carrier (osculating curves are outside the "
                    + "line/arc tier).");
            }
        }
        for (int k = 0; k < length; k++)
        {
            fan[(from + k) % n] = ids[k];
            curvatures[(from + k) % n] = values[k];
        }
    }

    /// <summary>Are two departures parallel to within the uncertainty their own geometry
    /// carries? See <see cref="OrderTangentialRuns"/> for where the band comes from.</summary>
    private bool IsTangentiallyTied(
        in Vector2d d1, double k1, in Vector2d d2, double k2)
    {
        if (d1.Dot(d2) <= 0)
            return false;   // opposite directions are never one run
        // Arithmetic floor (a few ulps on unit vectors) plus the rotation a snap-tolerance
        // displacement induces on a radial of curvature k.
        double band = 1e-15 + VertexSnapTolerance * Math.Max(Math.Abs(k1), Math.Abs(k2));
        return Math.Abs(d1.Cross(d2)) <= band;
    }

    /// <summary>The direction and signed curvature with which an edge leaves a vertex.</summary>
    private Vector2d Departure(int v, int edge, out double curvature)
    {
        var entry = _edges[edge];
        if (entry.A == v)
        {
            curvature = entry.Geometry.SignedCurvature;
            return entry.Geometry.TangentAt(0);
        }
        // Travelling backwards negates the signed curvature along with the tangent.
        curvature = -entry.Geometry.SignedCurvature;
        return -entry.Geometry.TangentAt(1);
    }

    /// <summary>0 for directions with angle in [0, π) (east axis inclusive), 1 for [π, 2π).</summary>
    private static int HalfPlane(in Vector2d direction) =>
        direction.Y > 0 || (direction.Y == 0 && direction.X > 0) ? 0 : 1;

    private int[] ConnectedComponents()
    {
        var parent = new int[_vertices.Count];
        for (int v = 0; v < parent.Length; v++)
            parent[v] = v;

        int Find(int v)
        {
            while (parent[v] != v)
                v = parent[v] = parent[parent[v]];
            return v;
        }

        foreach (var entry in _edges)
        {
            int ra = Find(entry.A), rb = Find(entry.B);
            if (ra != rb)
                parent[ra] = rb;
        }
        for (int v = 0; v < parent.Length; v++)
            parent[v] = Find(v);
        return parent;
    }

    private static void AddSplit(Dictionary<int, List<Split>> splits, int eid, in Split split)
    {
        if (!splits.TryGetValue(eid, out var list))
            splits[eid] = list = [];
        list.Add(split);
    }

    private readonly record struct Split(double T, Vector2d Position, double TOnNew);
}

/// <summary>
/// One bounded cell of a <see cref="CurvedArrangement2d"/>: a counter-clockwise outer chain
/// plus zero or more clockwise hole chains. Loops are available both as directed-edge ids
/// into the owning arrangement (2·edge for A→B, 2·edge+1 for B→A) and as materialized
/// curve chains.
/// </summary>
public sealed class CurvedCell2d
{
    /// <summary>Counter-clockwise outer boundary, as directed-edge ids.</summary>
    public IReadOnlyList<int> OuterDirected { get; }

    /// <summary>Counter-clockwise outer boundary as a curve chain.</summary>
    public IReadOnlyList<CurvedEdge2d> Outer { get; }

    /// <summary>Clockwise hole loops, as directed-edge ids.</summary>
    public IReadOnlyList<IReadOnlyList<int>> HolesDirected { get; }

    /// <summary>Clockwise hole loops as curve chains.</summary>
    public IReadOnlyList<IReadOnlyList<CurvedEdge2d>> Holes { get; }

    /// <summary>Net area of the cell: outer chain minus the holes. Exact for lines and arcs.</summary>
    public double Area { get; }

    internal CurvedCell2d(
        IReadOnlyList<int> outerDirected,
        IReadOnlyList<CurvedEdge2d> outer,
        IReadOnlyList<IReadOnlyList<int>> holesDirected,
        IReadOnlyList<IReadOnlyList<CurvedEdge2d>> holes,
        double area)
    {
        OuterDirected = outerDirected;
        Outer = outer;
        HolesDirected = holesDirected;
        Holes = holes;
        Area = area;
    }
}
