using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>A node of a <see cref="StraightSkeleton"/>: where it sits, and the offset
/// distance at which the shrinking wavefront reached it. The polygon's own corners are
/// nodes at time zero.</summary>
public readonly record struct SkeletonNode(Vector2d Position, double Time);

/// <summary>
/// An arc of a <see cref="StraightSkeleton"/> — a straight segment between two nodes, shared
/// by exactly the two skeleton faces named by <paramref name="FaceA"/> and
/// <paramref name="FaceB"/> (indices into the polygon's edge list).
/// </summary>
public readonly record struct SkeletonArc(int Start, int End, int FaceA, int FaceB);

/// <summary>Thrown when a straight skeleton cannot be computed for the polygon given.</summary>
public sealed class StraightSkeletonException : Exception
{
    public StraightSkeletonException(string message) : base(message) { }
}

/// <summary>
/// The <b>straight skeleton</b> of a simple polygon: the trace of its vertices as every edge
/// sweeps inward at unit speed, keeping its own direction. It is what a hip roof's ridges and
/// valleys are, which is why <see cref="Shape.Roof(Sketch, double, SketchPlane?)"/> is built
/// on it — see <c>docs/examples/roof.md</c>.
///
/// <para><b>Two event kinds, and both are load-bearing.</b> An <i>edge event</i> is an edge
/// shrinking to zero, after which its two neighbours become adjacent; a <i>split event</i> is
/// a REFLEX vertex reaching a non-adjacent edge and dividing the wavefront in two. A convex
/// polygon has only edge events, so an implementation that stops there is a well-verified
/// subset that silently returns nonsense for the first L-shape it meets.</para>
///
/// <para><b>The simulation is exact and the verification is structural.</b> Every vertex
/// moves on a straight line at constant velocity (the solution of <c>v·n₁ = v·n₂ = 1</c> for
/// its two edges' inward normals), so every event time is one division and every node is a
/// point, not a fitted one. What is checked afterwards is the one property the whole
/// construction stands on: the skeleton faces are simple, positively wound, and their areas
/// sum to the polygon's own. A degenerate configuration that the event simulation cannot
/// decide therefore <b>refuses by name</b> rather than returning a plausible skeleton.</para>
///
/// <para><b>Holes are refused by name.</b> A hole's wavefront GROWS while the outer one
/// shrinks, so the two meet in a merge event whose first contact is, for every rectilinear
/// input, an edge-against-edge SEGMENT rather than the vertex-against-edge point this
/// simulation is built from — see the class remarks on <see cref="Shape.Roof(Sketch, double,
/// SketchPlane?)"/>.</para>
/// </summary>
public sealed class StraightSkeleton
{
    private StraightSkeleton(
        IReadOnlyList<Vector2d> polygon,
        IReadOnlyList<SkeletonNode> nodes,
        IReadOnlyList<SkeletonArc> arcs,
        IReadOnlyList<IReadOnlyList<int>> faces,
        int edgeEvents,
        int splitEvents)
    {
        Polygon = polygon;
        Nodes = nodes;
        Arcs = arcs;
        Faces = faces;
        EdgeEvents = edgeEvents;
        SplitEvents = splitEvents;
        double max = 0;
        foreach (var node in nodes)
            max = Math.Max(max, node.Time);
        MaxTime = max;
    }

    /// <summary>The polygon the skeleton was computed for, counter-clockwise.</summary>
    public IReadOnlyList<Vector2d> Polygon { get; }

    /// <summary>Every skeleton node. Indices <c>0 .. Polygon.Count - 1</c> are the polygon's
    /// own corners, at time zero; the rest are interior nodes.</summary>
    public IReadOnlyList<SkeletonNode> Nodes { get; }

    /// <summary>Every skeleton arc, each naming the two faces it separates.</summary>
    public IReadOnlyList<SkeletonArc> Arcs { get; }

    /// <summary>One face per polygon edge, as a counter-clockwise cycle of node indices
    /// starting at the edge's own two corners. The faces partition the polygon exactly.</summary>
    public IReadOnlyList<IReadOnlyList<int>> Faces { get; }

    /// <summary>How many edge events the simulation ran.</summary>
    public int EdgeEvents { get; }

    /// <summary>How many split events the simulation ran. Zero for every convex polygon.</summary>
    public int SplitEvents { get; }

    /// <summary>The largest node time — the offset distance at which the wavefront vanished,
    /// i.e. the roof's own apex height at unit slope.</summary>
    public double MaxTime { get; }

    /// <summary>The number of interior (non-corner) nodes.</summary>
    public int InteriorNodeCount => Nodes.Count - Polygon.Count;

    /// <summary>
    /// The straight skeleton of a simple polygon. The winding is normalised to
    /// counter-clockwise, so <see cref="Polygon"/> may be the reverse of what was passed.
    /// </summary>
    /// <exception cref="ArgumentException">Fewer than three distinct corners.</exception>
    /// <exception cref="StraightSkeletonException">The simulation could not close the polygon.</exception>
    public static StraightSkeleton Of(IReadOnlyList<Vector2d> polygon)
        => Of(polygon, allowSplitEvents: true);

    /// <summary>The mutation seam: running with <paramref name="allowSplitEvents"/> false is
    /// the convex-only implementation, and every non-convex fixture must fail against it.</summary>
    internal static StraightSkeleton Of(IReadOnlyList<Vector2d> polygon, bool allowSplitEvents)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        var corners = Normalise(polygon, out double extent);
        return new Simulation(corners, extent, allowSplitEvents).Run();
    }

    // ---- input conditioning ----

    private static Vector2d[] Normalise(IReadOnlyList<Vector2d> polygon, out double extent)
    {
        var kept = new List<Vector2d>(polygon.Count);
        foreach (var p in polygon)
        {
            if (kept.Count > 0 && kept[^1].AreEqual(p, Tolerance.Default))
                continue;
            kept.Add(p);
        }
        while (kept.Count > 1 && kept[0].AreEqual(kept[^1], Tolerance.Default))
            kept.RemoveAt(kept.Count - 1);
        if (kept.Count < 3)
            throw new ArgumentException("A straight skeleton needs at least three distinct corners.", nameof(polygon));

        var min = kept[0];
        var max = kept[0];
        foreach (var p in kept)
        {
            min = Vector2d.Min(min, p);
            max = Vector2d.Max(max, p);
        }
        extent = (max - min).Length;
        if (!(extent > 0))
            throw new ArgumentException("The polygon is degenerate: every corner is the same point.", nameof(polygon));

        double twiceArea = 0;
        for (int i = 0; i < kept.Count; i++)
        {
            var a = kept[i];
            var b = kept[(i + 1) % kept.Count];
            twiceArea += a.Cross(b);
        }
        if (Math.Abs(twiceArea) <= 1e-13 * extent * extent)
            throw new ArgumentException("The polygon encloses no area.", nameof(polygon));
        if (twiceArea < 0)
            kept.Reverse();
        return kept.ToArray();
    }

    // ---- the wavefront simulation ----

    private sealed class Edge
    {
        public Vector2d A;
        public Vector2d B;
        public Vector2d Direction;  // unit, along the traversal
        public Vector2d Normal;     // unit, inward
        public double Offset;       // A·Normal, so the offset line at time t is p·Normal = Offset + t
    }

    private sealed class Wave
    {
        public int Id;
        public Vector2d Origin;
        public double BirthTime;
        public int BirthNode;
        public Vector2d Velocity;
        public bool Moving;
        public int EdgeIn;
        public int EdgeOut;
        public Wave? Prev;
        public Wave? Next;
        public bool Dead;

        public Vector2d At(double t) => Origin + Velocity * (t - BirthTime);
        public Vector2d Base => Origin - Velocity * BirthTime;
    }

    private enum EventKind { Edge, Split }

    private readonly record struct Event(EventKind Kind, double Time, Wave Wave, int Edge);

    private sealed class Simulation
    {
        private readonly Vector2d[] _corners;
        private readonly Edge[] _edges;
        private readonly bool[] _reflex;
        private readonly bool _allowSplits;
        private readonly double _extent;
        private readonly double _eps;
        private readonly List<SkeletonNode> _nodes = [];
        private readonly List<SkeletonArc> _arcs = [];
        private readonly List<Wave> _waves = [];
        private int _edgeEvents;
        private int _splitEvents;
        private int _nextId;

        public Simulation(Vector2d[] corners, double extent, bool allowSplits)
        {
            _corners = corners;
            _extent = extent;
            _eps = 1e-9 * extent;
            _allowSplits = allowSplits;

            int n = corners.Length;
            _edges = new Edge[n];
            for (int i = 0; i < n; i++)
            {
                var a = corners[i];
                var b = corners[(i + 1) % n];
                var d = (b - a).Normalized();
                _edges[i] = new Edge { A = a, B = b, Direction = d, Normal = d.Perpendicular, Offset = 0 };
                _edges[i].Offset = a.Dot(_edges[i].Normal);
            }

            _reflex = new bool[n];
            for (int i = 0; i < n; i++)
            {
                int inEdge = (i + n - 1) % n;
                _reflex[i] = _edges[inEdge].Direction.Cross(_edges[i].Direction) < 0;
            }
        }

        public StraightSkeleton Run()
        {
            int n = _corners.Length;
            for (int i = 0; i < n; i++)
                _nodes.Add(new SkeletonNode(_corners[i], 0));

            // One wave per corner; the corner IS its birth node.
            var ring = new Wave[n];
            for (int i = 0; i < n; i++)
            {
                int inEdge = (i + n - 1) % n;
                ring[i] = NewWave(_corners[i], 0, i, inEdge, i);
            }
            for (int i = 0; i < n; i++)
            {
                ring[i].Prev = ring[(i + n - 1) % n];
                ring[i].Next = ring[(i + 1) % n];
            }

            double now = 0;
            int budget = 8 * (n + 2) * (n + 2);
            for (int guard = 0; guard <= budget; guard++)
            {
                ResolveDegenerateLoops(now);
                if (!TryNextEvent(now, out var ev))
                    break;
                now = ev.Time;
                if (ev.Kind == EventKind.Edge)
                    ProcessEdgeEvent(ev.Wave, now);
                else
                    ProcessSplitEvent(ev.Wave, ev.Edge, now);
                if (guard == budget)
                    throw new StraightSkeletonException(
                        "The straight skeleton did not terminate within its event budget; the polygon is likely "
                        + "self-intersecting or carries a degeneracy the wavefront simulation cannot decide.");
            }

            ResolveDegenerateLoops(now);
            foreach (var wave in _waves)
            {
                if (!wave.Dead)
                    throw new StraightSkeletonException(
                        "The straight skeleton stalled with an unresolved wavefront; the polygon carries a "
                        + "degeneracy this simulation cannot decide (several events at one instant along a "
                        + "whole edge, rather than at a point).");
            }

            var faces = AssembleFaces();
            Verify(faces);
            return new StraightSkeleton(_corners, _nodes, _arcs, faces, _edgeEvents, _splitEvents);
        }

        // ---- wave bookkeeping ----

        private Wave NewWave(Vector2d origin, double time, int node, int edgeIn, int edgeOut)
        {
            var wave = new Wave
            {
                Id = _nextId++,
                Origin = origin,
                BirthTime = time,
                BirthNode = node,
                EdgeIn = edgeIn,
                EdgeOut = edgeOut,
            };
            var n1 = _edges[edgeIn].Normal;
            var n2 = _edges[edgeOut].Normal;
            double det = n1.Cross(n2);
            if (Math.Abs(det) <= 1e-9)
            {
                // Collinear edges (the vertex simply translates) or anti-parallel ones (the
                // wavefront there has closed head-on, so the vertex has no finite velocity).
                if (n1.Dot(n2) > 0)
                {
                    wave.Velocity = n1;
                    wave.Moving = true;
                }
                else
                {
                    wave.Velocity = Vector2d.Zero;
                    wave.Moving = false;
                }
            }
            else
            {
                wave.Velocity = new Vector2d((n2.Y - n1.Y) / det, (n1.X - n2.X) / det);
                wave.Moving = true;
            }
            _waves.Add(wave);
            return wave;
        }

        private int AddNode(Vector2d p, double time)
        {
            for (int i = 0; i < _nodes.Count; i++)
            {
                if ((_nodes[i].Position - p).Length <= _eps)
                    return i;
            }
            _nodes.Add(new SkeletonNode(p, time));
            return _nodes.Count - 1;
        }

        private void Kill(Wave wave, int node)
        {
            wave.Dead = true;
            if (wave.BirthNode != node)
                _arcs.Add(new SkeletonArc(wave.BirthNode, node, wave.EdgeIn, wave.EdgeOut));
        }

        // ---- events ----

        private bool TryNextEvent(double now, out Event best)
        {
            best = default;
            bool found = false;
            foreach (var wave in _waves)
            {
                if (wave.Dead || !wave.Moving)
                    continue;
                if (TryEdgeEvent(wave, now, out double te) && Better(te, EventKind.Edge, wave.Id, found, best))
                {
                    best = new Event(EventKind.Edge, te, wave, wave.EdgeOut);
                    found = true;
                }
                if (!_allowSplits || !IsReflex(wave))
                    continue;
                for (int e = 0; e < _edges.Length; e++)
                {
                    if (e == wave.EdgeIn || e == wave.EdgeOut)
                        continue;
                    if (!TrySplitTime(wave, e, now, out double ts))
                        continue;
                    if (!SplitIsReachable(wave, e, ts))
                        continue;
                    if (Better(ts, EventKind.Split, wave.Id, found, best))
                    {
                        best = new Event(EventKind.Split, ts, wave, e);
                        found = true;
                    }
                }
            }
            return found;
        }

        /// <summary>Reflexivity is a property of the wave's own two EDGES, never of the corner
        /// it descends from — a wave created by an event joins two edges that were never
        /// adjacent, so an index into the polygon's corner table would answer for the wrong
        /// pair.</summary>
        private bool IsReflex(Wave wave)
            => _edges[wave.EdgeIn].Direction.Cross(_edges[wave.EdgeOut].Direction) < 0;

        private static bool Better(double time, EventKind kind, int id, bool haveBest, in Event best)
        {
            if (!haveBest)
                return true;
            if (time < best.Time)
                return true;
            if (time > best.Time)
                return false;
            // A deterministic tie-break so simultaneous events are processed in one order.
            if (kind != best.Kind)
                return kind == EventKind.Edge;
            return id < best.Wave.Id;
        }

        private bool TryEdgeEvent(Wave u, double now, out double time)
        {
            time = 0;
            var w = u.Next!;
            if (w.Dead || !w.Moving || ReferenceEquals(w, u))
                return false;
            var dir = _edges[u.EdgeOut].Direction;
            double a = (w.Base - u.Base).Dot(dir);
            double b = (w.Velocity - u.Velocity).Dot(dir);
            if (b >= -1e-12)
                return false;   // not shrinking
            time = -a / b;
            return time >= now - _eps && time >= Math.Max(u.BirthTime, w.BirthTime) - _eps;
        }

        private bool TrySplitTime(Wave v, int e, double now, out double time)
        {
            time = 0;
            var edge = _edges[e];
            double denom = v.Velocity.Dot(edge.Normal) - 1;
            if (denom >= -1e-12)
                return false;   // the vertex never catches the receding offset line
            time = (edge.Offset - v.Base.Dot(edge.Normal)) / denom;
            // Deliberately inclusive of the CURRENT instant: an L-shape's split lands at the
            // same time as the arm's own edge event, so a strictly-later test would miss the
            // one event kind the shape exists to exercise.
            return time >= now - _eps && time >= v.BirthTime - _eps;
        }

        /// <summary>
        /// A split is real only if the reflex vertex lands strictly INSIDE the stretch of that
        /// edge still carried by its own loop at that instant — which is why the test is made
        /// against the CURRENT bounding waves rather than against the original corners.
        /// </summary>
        private bool SplitIsReachable(Wave v, int e, double time)
        {
            if (!TryFindEdgeInLoop(v, e, out var left, out var right))
                return false;
            // A non-moving bound is NOT a reason to refuse: it is a wave whose two edges have
            // closed head-on, so it stands where it was born — which is exactly the L-shape's
            // ridge end, the configuration a split event most needs to reach.
            var q = v.At(time);
            var l = left.At(time);
            var r = right.At(time);
            var span = r - l;
            double len2 = span.LengthSquared;
            if (len2 <= _eps * _eps)
                return false;
            double s = (q - l).Dot(span) / len2;
            double margin = _eps / Math.Sqrt(len2);
            return s > margin && s < 1 - margin;
        }

        private static bool TryFindEdgeInLoop(Wave v, int e, out Wave left, out Wave right)
        {
            left = v;
            right = v;
            var w = v;
            do
            {
                if (w.EdgeOut == e && !ReferenceEquals(w, v))
                {
                    left = w;
                    right = w.Next!;
                    return true;
                }
                w = w.Next!;
            } while (!ReferenceEquals(w, v));
            return false;
        }

        private void ProcessEdgeEvent(Wave u, double time)
        {
            _edgeEvents++;
            var w = u.Next!;
            int node = AddNode(u.At(time), time);
            var prev = u.Prev!;
            var next = w.Next!;
            bool pair = ReferenceEquals(prev, w);
            Kill(u, node);
            Kill(w, node);
            if (pair)
                return;     // the loop had only these two waves

            var merged = NewWave(_nodes[node].Position, time, node, u.EdgeIn, w.EdgeOut);
            merged.Prev = prev;
            merged.Next = next;
            prev.Next = merged;
            next.Prev = merged;
        }

        private void ProcessSplitEvent(Wave v, int e, double time)
        {
            if (!TryFindEdgeInLoop(v, e, out var left, out var right))
                return;
            _splitEvents++;
            int node = AddNode(v.At(time), time);
            var prev = v.Prev!;
            var next = v.Next!;
            Kill(v, node);

            // The reflex vertex's two sides attach to the two halves of the edge it hit. The
            // same relinking SPLITS one loop in two and MERGES two loops into one, which is
            // why a hole would need no new event code — only the degeneracies it brings.
            var lower = NewWave(_nodes[node].Position, time, node, v.EdgeIn, e);
            var upper = NewWave(_nodes[node].Position, time, node, e, v.EdgeOut);
            lower.Prev = prev;
            lower.Next = right;
            prev.Next = lower;
            right.Prev = lower;
            upper.Prev = left;
            upper.Next = next;
            left.Next = upper;
            next.Prev = upper;
        }

        /// <summary>
        /// A wavefront loop that has collapsed onto a SEGMENT — zero area, every wave collinear
        /// — is a RIDGE, and the arcs it contributes are the 1-D OVERLAY of its own two chains:
        /// the loop runs out along the segment carrying one edge's face and back carrying
        /// another's, so each elementary stretch is covered exactly twice and those two faces
        /// are what the arc separates. A two-wave loop is the smallest member (an L-shape's arm),
        /// a collinear run's rectangle the next (three waves, two arcs), and a loop whose waves
        /// have all met at one point contributes no arc at all.
        /// </summary>
        private void ResolveDegenerateLoops(double now)
        {
            var seen = new HashSet<Wave>();
            foreach (var start in _waves)
            {
                if (start.Dead || !seen.Add(start))
                    continue;
                var members = new List<Wave>();
                var w = start;
                do
                {
                    members.Add(w);
                    seen.Add(w);
                    w = w.Next!;
                } while (!ReferenceEquals(w, start) && members.Count <= _waves.Count);

                if (members.Count == 1)
                {
                    Kill(members[0], AddNode(members[0].At(now), now));
                    continue;
                }
                if (!IsCollapsed(members, now, out var origin, out var direction))
                    continue;
                if (StillShrinking(members))
                    continue;
                CollapseLoop(members, now, origin, direction);
            }
        }

        /// <summary>Every wave on one segment, enclosing no area. The direction is the loop's
        /// longest chord, or false when the waves are not collinear (which a wavefront that
        /// closed head-on cannot be, so it is refused rather than guessed at).</summary>
        private bool IsCollapsed(List<Wave> members, double now, out Vector2d origin, out Vector2d direction)
        {
            origin = members[0].At(now);
            direction = Vector2d.UnitX;

            double twiceArea = 0;
            for (int i = 0; i < members.Count; i++)
            {
                var a = members[i].At(now);
                var b = members[(i + 1) % members.Count].At(now);
                twiceArea += a.Cross(b);
            }
            if (Math.Abs(twiceArea) > _eps * _extent)
                return false;

            double best = 0;
            foreach (var member in members)
            {
                var d = member.At(now) - origin;
                if (d.LengthSquared > best)
                {
                    best = d.LengthSquared;
                    direction = d;
                }
            }
            if (best <= _eps * _eps)
                return true;    // every wave at one point
            direction = direction / Math.Sqrt(best);
            foreach (var member in members)
            {
                if (Math.Abs((member.At(now) - origin).Cross(direction)) > _eps)
                    return false;
            }
            return true;
        }

        /// <summary>A pair that can still shrink is a real wavefront, not a ridge — let its
        /// edge event fire.</summary>
        private bool StillShrinking(List<Wave> members)
        {
            foreach (var member in members)
            {
                var next = member.Next!;
                if (!member.Moving || !next.Moving)
                    continue;
                if ((next.Velocity - member.Velocity).Dot(_edges[member.EdgeOut].Direction) < -1e-12)
                    return true;
            }
            return false;
        }

        private void CollapseLoop(List<Wave> members, double now, Vector2d origin, Vector2d direction)
        {
            int n = members.Count;
            var at = new double[n];
            var nodes = new int[n];
            for (int i = 0; i < n; i++)
            {
                // Parameters come from the INTERNED node, never from the raw trajectory: the
                // interning is what already decided which of these waves are at one point, and
                // reading the raw positions instead splits a fully-converged loop into
                // elementary intervals 1e-29 wide that every chain then covers (measured on a
                // regular five-pointed star, whose five waves all arrive at the centre).
                nodes[i] = AddNode(members[i].At(now), now);
                at[i] = (_nodes[nodes[i]].Position - origin).Dot(direction);
            }

            var cuts = at.Distinct().OrderBy(v => v).ToArray();
            for (int k = 0; k + 1 < cuts.Length; k++)
            {
                double lo = cuts[k], hi = cuts[k + 1];
                double mid = 0.5 * (lo + hi);
                int faceA = -1, faceB = -1, extra = 0;
                for (int i = 0; i < n; i++)
                {
                    double s = at[i];
                    double e = at[(i + 1) % n];
                    if (Math.Min(s, e) > mid || Math.Max(s, e) < mid)
                        continue;
                    if (faceA < 0) faceA = members[i].EdgeOut;
                    else if (faceB < 0) faceB = members[i].EdgeOut;
                    else extra++;
                }
                if (faceB < 0)
                    continue;
                if (extra > 0)
                    throw new StraightSkeletonException(
                        "A collapsed wavefront covers one stretch more than twice; the polygon carries a "
                        + "degeneracy this simulation cannot decide.");

                int a = NodeAt(at, nodes, lo);
                int b = NodeAt(at, nodes, hi);
                _arcs.Add(new SkeletonArc(a, b, faceA, faceB));
            }

            int last = AddNode(members[0].At(now), now);
            for (int i = 0; i < n; i++)
                Kill(members[i], nodes[i] >= 0 ? nodes[i] : last);
        }

        private static int NodeAt(double[] at, int[] nodes, double value)
        {
            for (int i = 0; i < at.Length; i++)
            {
                if (at[i] == value)
                    return nodes[i];
            }
            return nodes[0];
        }

        // ---- faces ----

        private IReadOnlyList<IReadOnlyList<int>> AssembleFaces()
        {
            int n = _corners.Length;
            var faces = new IReadOnlyList<int>[n];
            for (int e = 0; e < n; e++)
            {
                var adjacency = new Dictionary<int, List<int>>();
                void Link(int a, int b)
                {
                    if (a == b)
                        return;
                    if (!adjacency.TryGetValue(a, out var la))
                        adjacency[a] = la = [];
                    if (!adjacency.TryGetValue(b, out var lb))
                        adjacency[b] = lb = [];
                    la.Add(b);
                    lb.Add(a);
                }

                int start = e;
                int end = (e + 1) % n;
                Link(start, end);
                foreach (var arc in _arcs)
                {
                    if (arc.FaceA == e || arc.FaceB == e)
                        Link(arc.Start, arc.End);
                }

                foreach (var (node, neighbours) in adjacency)
                {
                    if (neighbours.Count != 2)
                        throw new StraightSkeletonException(
                            $"The straight skeleton's face for edge {e} does not close: node {node} at "
                            + $"{_nodes[node].Position} is used by {neighbours.Count} arcs rather than 2.");
                }

                var cycle = new List<int> { start, end };
                int previous = start;
                int current = end;
                while (true)
                {
                    var options = adjacency[current];
                    int step = options[0] == previous ? options[1] : options[0];
                    if (step == start)
                        break;
                    cycle.Add(step);
                    previous = current;
                    current = step;
                    if (cycle.Count > _nodes.Count + 1)
                        throw new StraightSkeletonException(
                            $"The straight skeleton's face for edge {e} does not close into a simple cycle.");
                }
                faces[e] = cycle;
            }
            return faces;
        }

        private void Verify(IReadOnlyList<IReadOnlyList<int>> faces)
        {
            double total = 0;
            for (int e = 0; e < faces.Count; e++)
            {
                double area = SignedArea(faces[e]);
                if (area <= 1e-12 * _extent * _extent)
                    throw new StraightSkeletonException(
                        $"The straight skeleton's face for edge {e} came out wound the wrong way or empty "
                        + $"(signed area {area:R}); the wavefront simulation hit a degeneracy it could not decide.");
                total += area;
            }
            double expected = SignedArea(Enumerable.Range(0, _corners.Length).ToArray());
            if (Math.Abs(total - expected) > 1e-9 * expected)
                throw new StraightSkeletonException(
                    $"The straight skeleton's faces cover {total:R} against the polygon's own {expected:R}; "
                    + "they must partition it exactly.");
        }

        private double SignedArea(IReadOnlyList<int> cycle)
        {
            double twice = 0;
            for (int i = 0; i < cycle.Count; i++)
            {
                var a = _nodes[cycle[i]].Position;
                var b = _nodes[cycle[(i + 1) % cycle.Count]].Position;
                twice += a.Cross(b);
            }
            return twice * 0.5;
        }
    }
}
