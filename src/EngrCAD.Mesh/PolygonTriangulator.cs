using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Polygon triangulation with hole support — a faithful C# port of mapbox's earcut
/// (2.2.x, minus the z-order curve acceleration): ear clipping over a doubly linked list
/// with earcut's full recovery ladder (filter → cure local intersections → split), and
/// hole elimination via bridges to mutually visible vertices. Output triangles are
/// normalized counter-clockwise in the 2D plane regardless of input orientation.
/// Exactly-collinear input vertices are filtered by the algorithm and may not appear in
/// any triangle.
/// </summary>
public static class PolygonTriangulator
{
    public static List<(int A, int B, int C)> Triangulate(IReadOnlyList<Vector2d> polygon)
    {
        if (polygon.Count < 3)
            throw new ArgumentException("Polygon needs at least 3 vertices.", nameof(polygon));
        return Run(polygon, [polygon.Count]);
    }

    /// <summary>
    /// Triangulates an outer polygon with holes. Returned indices refer to the
    /// concatenation [outer..., holes[0]..., holes[1]...] in the given order.
    /// </summary>
    public static List<(int A, int B, int C)> TriangulateWithHoles(
        IReadOnlyList<Vector2d> outer, IReadOnlyList<IReadOnlyList<Vector2d>> holes)
    {
        if (outer.Count < 3)
            throw new ArgumentException("Polygon needs at least 3 vertices.", nameof(outer));
        var points = new List<Vector2d>(outer);
        var ringEnds = new List<int> { outer.Count };
        foreach (var hole in holes)
        {
            points.AddRange(hole);
            ringEnds.Add(points.Count);
        }
        return Run(points, ringEnds);
    }

    public static double SignedArea(IReadOnlyList<Vector2d> polygon)
    {
        double area = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            var p = polygon[i];
            var q = polygon[(i + 1) % polygon.Count];
            area += p.Cross(q);
        }
        return area * 0.5;
    }

    // ---- earcut port ----

    private sealed class Node(int i, double x, double y)
    {
        public readonly int I = i;
        public readonly double X = x;
        public readonly double Y = y;
        public Node Prev = null!;
        public Node Next = null!;
        public bool Steiner;
    }

    private static List<(int, int, int)> Run(IReadOnlyList<Vector2d> points, List<int> ringEnds)
    {
        var triangles = new List<(int, int, int)>();
        var outerNode = LinkRing(points, 0, ringEnds[0], clockwise: true);
        if (outerNode is null || outerNode.Next == outerNode.Prev)
            return triangles;

        if (ringEnds.Count > 1)
            outerNode = EliminateHoles(points, ringEnds, outerNode);

        EarcutLinked(outerNode, triangles, 0);

        // Normalize to CCW in standard orientation for downstream consumers.
        for (int t = 0; t < triangles.Count; t++)
        {
            var (a, b, c) = triangles[t];
            if ((points[b] - points[a]).Cross(points[c] - points[a]) < 0)
                triangles[t] = (a, c, b);
        }
        triangles.RemoveAll(t =>
            Math.Abs((points[t.Item2] - points[t.Item1]).Cross(points[t.Item3] - points[t.Item1])) <= 0);
        return triangles;
    }

    /// <summary>Links a ring [start, end) into a circular list, oriented per <paramref name="clockwise"/> (earcut's convention).</summary>
    private static Node? LinkRing(IReadOnlyList<Vector2d> points, int start, int end, bool clockwise)
    {
        Node? last = null;
        if (clockwise == (RingSignedArea(points, start, end) > 0))
        {
            for (int i = start; i < end; i++)
                last = InsertNode(i, points[i], last);
        }
        else
        {
            for (int i = end - 1; i >= start; i--)
                last = InsertNode(i, points[i], last);
        }

        if (last is not null && CoordsEqual(last, last.Next))
        {
            RemoveNode(last);
            last = last.Next;
        }
        return last;
    }

    /// <summary>Earcut's shoelace variant: positive for one orientation, negative for the other.</summary>
    private static double RingSignedArea(IReadOnlyList<Vector2d> points, int start, int end)
    {
        double sum = 0;
        for (int i = start, j = end - 1; i < end; j = i++)
            sum += (points[j].X - points[i].X) * (points[i].Y + points[j].Y);
        return sum;
    }

    private static Node InsertNode(int i, in Vector2d p, Node? last)
    {
        var node = new Node(i, p.X, p.Y);
        if (last is null)
        {
            node.Prev = node;
            node.Next = node;
        }
        else
        {
            node.Next = last.Next;
            node.Prev = last;
            last.Next.Prev = node;
            last.Next = node;
        }
        return node;
    }

    private static void RemoveNode(Node p)
    {
        p.Next.Prev = p.Prev;
        p.Prev.Next = p.Next;
    }

    private static bool CoordsEqual(Node a, Node b) => a.X == b.X && a.Y == b.Y;

    /// <summary>Earcut's signed area of a corner; negative means a convex (ear-candidate) turn.</summary>
    private static double Area(Node p, Node q, Node r) =>
        (q.Y - p.Y) * (r.X - q.X) - (q.X - p.X) * (r.Y - q.Y);

    /// <summary>Removes duplicate and exactly-collinear nodes.</summary>
    private static Node? FilterPoints(Node? start, Node? end = null)
    {
        if (start is null)
            return null;
        end ??= start;

        var p = start;
        bool again;
        do
        {
            again = false;
            if (!p.Steiner && (CoordsEqual(p, p.Next) || Area(p.Prev, p, p.Next) == 0))
            {
                RemoveNode(p);
                p = end = p.Prev;
                if (p == p.Next)
                    break;
                again = true;
            }
            else
            {
                p = p.Next;
            }
        } while (again || p != end);

        return end;
    }

    private static void EarcutLinked(Node? ear, List<(int, int, int)> triangles, int pass)
    {
        if (ear is null)
            return;

        var stop = ear;
        while (ear!.Prev != ear.Next)
        {
            var prev = ear.Prev;
            var next = ear.Next;

            if (IsEar(ear))
            {
                triangles.Add((prev.I, ear.I, next.I));
                RemoveNode(ear);
                ear = next.Next;
                stop = next.Next;
                continue;
            }

            ear = next;
            if (ear == stop)
            {
                // No ear in a full loop: escalate through earcut's recovery ladder.
                if (pass == 0)
                {
                    EarcutLinked(FilterPoints(ear), triangles, 1);
                }
                else if (pass == 1)
                {
                    ear = CureLocalIntersections(FilterPoints(ear)!, triangles);
                    EarcutLinked(ear, triangles, 2);
                }
                else if (pass == 2)
                {
                    SplitEarcut(ear, triangles);
                }
                break;
            }
        }
    }

    private static bool IsEar(Node ear)
    {
        var a = ear.Prev;
        var b = ear;
        var c = ear.Next;

        if (Area(a, b, c) >= 0)
            return false; // reflex

        double x0 = Math.Min(a.X, Math.Min(b.X, c.X));
        double y0 = Math.Min(a.Y, Math.Min(b.Y, c.Y));
        double x1 = Math.Max(a.X, Math.Max(b.X, c.X));
        double y1 = Math.Max(a.Y, Math.Max(b.Y, c.Y));

        var p = c.Next;
        while (p != a)
        {
            if (p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1 &&
                PointInTriangleExceptFirst(a, b, c, p.X, p.Y) &&
                Area(p.Prev, p, p.Next) >= 0)
                return false;
            p = p.Next;
        }
        return true;
    }

    private static bool PointInTriangle(double ax, double ay, double bx, double by, double cx, double cy, double px, double py) =>
        (cx - px) * (ay - py) >= (ax - px) * (cy - py) &&
        (ax - px) * (by - py) >= (bx - px) * (ay - py) &&
        (bx - px) * (cy - py) >= (cx - px) * (by - py);

    private static bool PointInTriangleExceptFirst(Node a, Node b, Node c, double px, double py) =>
        !(a.X == px && a.Y == py) && PointInTriangle(a.X, a.Y, b.X, b.Y, c.X, c.Y, px, py);

    /// <summary>Fixes cases where two adjacent edges intersect by clipping the offending pair.</summary>
    private static Node? CureLocalIntersections(Node start, List<(int, int, int)> triangles)
    {
        var p = start;
        do
        {
            var a = p.Prev;
            var b = p.Next.Next;

            if (!CoordsEqual(a, b) && Intersects(a, p, p.Next, b) && LocallyInside(a, b) && LocallyInside(b, a))
            {
                triangles.Add((a.I, p.I, b.I));
                RemoveNode(p);
                RemoveNode(p.Next);
                p = start = b;
            }
            p = p.Next;
        } while (p != start);

        return FilterPoints(p);
    }

    /// <summary>Last resort: split the remaining polygon along a valid diagonal and recurse.</summary>
    private static void SplitEarcut(Node start, List<(int, int, int)> triangles)
    {
        var a = start;
        do
        {
            var b = a.Next.Next;
            while (b != a.Prev)
            {
                if (a.I != b.I && IsValidDiagonal(a, b))
                {
                    var c = SplitPolygon(a, b);
                    a = FilterPoints(a, a.Next)!;
                    c = FilterPoints(c, c.Next)!;
                    EarcutLinked(a, triangles, 0);
                    EarcutLinked(c, triangles, 0);
                    return;
                }
                b = b.Next;
            }
            a = a.Next;
        } while (a != start);
    }

    private static bool IsValidDiagonal(Node a, Node b) =>
        a.Next.I != b.I && a.Prev.I != b.I && !IntersectsPolygon(a, b) &&
        (LocallyInside(a, b) && LocallyInside(b, a) && MiddleInside(a, b) &&
            (Area(a.Prev, a, b.Prev) != 0 || Area(a, b.Prev, b) != 0) ||
         CoordsEqual(a, b) && Area(a.Prev, a, a.Next) > 0 && Area(b.Prev, b, b.Next) > 0);

    private static bool Intersects(Node p1, Node q1, Node p2, Node q2)
    {
        int o1 = Math.Sign(Area(p1, q1, p2));
        int o2 = Math.Sign(Area(p1, q1, q2));
        int o3 = Math.Sign(Area(p2, q2, p1));
        int o4 = Math.Sign(Area(p2, q2, q1));

        if (o1 != o2 && o3 != o4)
            return true;
        if (o1 == 0 && OnSegment(p1, p2, q1))
            return true;
        if (o2 == 0 && OnSegment(p1, q2, q1))
            return true;
        if (o3 == 0 && OnSegment(p2, p1, q2))
            return true;
        if (o4 == 0 && OnSegment(p2, q1, q2))
            return true;
        return false;
    }

    private static bool OnSegment(Node p, Node q, Node r) =>
        q.X <= Math.Max(p.X, r.X) && q.X >= Math.Min(p.X, r.X) &&
        q.Y <= Math.Max(p.Y, r.Y) && q.Y >= Math.Min(p.Y, r.Y);

    private static bool IntersectsPolygon(Node a, Node b)
    {
        var p = a;
        do
        {
            if (p.I != a.I && p.Next.I != a.I && p.I != b.I && p.Next.I != b.I &&
                Intersects(p, p.Next, a, b))
                return true;
            p = p.Next;
        } while (p != a);
        return false;
    }

    private static bool LocallyInside(Node a, Node b) =>
        Area(a.Prev, a, a.Next) < 0
            ? Area(a, b, a.Next) >= 0 && Area(a, a.Prev, b) >= 0
            : Area(a, b, a.Prev) < 0 || Area(a, a.Next, b) < 0;

    private static bool MiddleInside(Node a, Node b)
    {
        var p = a;
        bool inside = false;
        double px = (a.X + b.X) / 2;
        double py = (a.Y + b.Y) / 2;
        do
        {
            if (p.Y > py != p.Next.Y > py && p.Next.Y != p.Y &&
                px < (p.Next.X - p.X) * (py - p.Y) / (p.Next.Y - p.Y) + p.X)
                inside = !inside;
            p = p.Next;
        } while (p != a);
        return inside;
    }

    /// <summary>
    /// Links two polygon vertices with a bridge, splitting the ring into two (the bridge
    /// edge appears in both, traversed oppositely, via duplicate nodes).
    /// </summary>
    private static Node SplitPolygon(Node a, Node b)
    {
        var a2 = new Node(a.I, a.X, a.Y);
        var b2 = new Node(b.I, b.X, b.Y);
        var an = a.Next;
        var bp = b.Prev;

        a.Next = b;
        b.Prev = a;

        a2.Next = an;
        an.Prev = a2;

        b2.Next = a2;
        a2.Prev = b2;

        bp.Next = b2;
        b2.Prev = bp;

        return b2;
    }

    private static Node EliminateHoles(IReadOnlyList<Vector2d> points, List<int> ringEnds, Node outerNode)
    {
        var queue = new List<Node>();
        for (int r = 1; r < ringEnds.Count; r++)
        {
            var ring = LinkRing(points, ringEnds[r - 1], ringEnds[r], clockwise: false);
            if (ring is null)
                continue;
            if (ring == ring.Next)
                ring.Steiner = true;
            queue.Add(GetLeftmost(ring));
        }
        queue.Sort((x, y) => x.X.CompareTo(y.X));

        foreach (var hole in queue)
            outerNode = EliminateHole(hole, outerNode);

        return outerNode;
    }

    private static Node GetLeftmost(Node start)
    {
        var p = start;
        var leftmost = start;
        do
        {
            if (p.X < leftmost.X || (p.X == leftmost.X && p.Y < leftmost.Y))
                leftmost = p;
            p = p.Next;
        } while (p != start);
        return leftmost;
    }

    private static Node EliminateHole(Node hole, Node outerNode)
    {
        var bridge = FindHoleBridge(hole, outerNode);
        if (bridge is null)
            return outerNode;

        var bridgeReverse = SplitPolygon(bridge, hole);
        FilterPoints(bridgeReverse, bridgeReverse.Next);
        return FilterPoints(bridge, bridge.Next)!;
    }

    /// <summary>David Eberly's algorithm: a hole's leftmost vertex bridged to a visible outer vertex.</summary>
    private static Node? FindHoleBridge(Node hole, Node outerNode)
    {
        var p = outerNode;
        double hx = hole.X;
        double hy = hole.Y;
        double qx = double.NegativeInfinity;
        Node? m = null;

        // Nearest outer edge intersected by a -x ray from the hole's leftmost point.
        do
        {
            if (hy <= p.Y && hy >= p.Next.Y && p.Next.Y != p.Y)
            {
                double x = p.X + (hy - p.Y) * (p.Next.X - p.X) / (p.Next.Y - p.Y);
                if (x <= hx && x > qx)
                {
                    qx = x;
                    m = p.X < p.Next.X ? p : p.Next;
                    if (x == hx)
                        return m; // the ray hit a vertex: directly visible
                }
            }
            p = p.Next;
        } while (p != outerNode);

        if (m is null)
            return null;

        // Among outer points inside the triangle (ray endpoint, intersection, candidate),
        // pick the reflex-visible one minimizing the angle to the ray.
        var stop = m;
        double mx = m.X;
        double my = m.Y;
        double tanMin = double.PositiveInfinity;

        p = m;
        do
        {
            if (hx >= p.X && p.X >= mx && hx != p.X &&
                PointInTriangle(hy < my ? hx : qx, hy, mx, my, hy < my ? qx : hx, hy, p.X, p.Y))
            {
                double tan = Math.Abs(hy - p.Y) / (hx - p.X);
                if (LocallyInside(p, hole) &&
                    (tan < tanMin || (tan == tanMin && (p.X > m.X || (p.X == m.X && SectorContainsSector(m, p))))))
                {
                    m = p;
                    tanMin = tan;
                }
            }
            p = p.Next;
        } while (p != stop);

        return m;
    }

    /// <summary>Whether m's angular sector fully contains p's (tie-break for equally-angled bridge candidates).</summary>
    private static bool SectorContainsSector(Node m, Node p) =>
        Area(m.Prev, m, p.Prev) < 0 && Area(p.Next, m, m.Next) < 0;
}
