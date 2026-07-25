using EngrCAD.Core;

namespace EngrCAD.Mesh;

// BSP-based CSG internals (csg.js approach): solids are BSP trees of polygons; booleans
// clip each solid's polygons against the other's tree. Robust enough for well-conditioned
// inputs; seam tessellation from the two sides can differ (T-junctions), so boolean
// results may carry hairline topological boundaries along intersection curves.

internal readonly struct CsgPlane
{
    /// <summary>
    /// Absolute classification epsilon; geometry is expected at roughly unit-to-thousands
    /// scale. Matches <see cref="Tolerance.Default"/>.Linear (a const cannot reference the
    /// policy) and is boolean-critical: seam zipping depends on this exact value.
    /// </summary>
    public const double Epsilon = 1e-9;

    public Vector3d Normal { get; }
    public double W { get; }

    public CsgPlane(in Vector3d normal, double w)
    {
        Normal = normal;
        W = w;
    }

    public static bool TryFromPoints(in Vector3d a, in Vector3d b, in Vector3d c, out CsgPlane plane)
    {
        if (!(b - a).Cross(c - a).TryNormalize(Tolerance.Default, out var n))
        {
            plane = default;
            return false;
        }
        plane = new CsgPlane(n, n.Dot(a));
        return true;
    }

    public CsgPlane Flipped => new(-Normal, -W);

    private const int Coplanar = 0;
    private const int Front = 1;
    private const int Back = 2;
    private const int Spanning = 3;

    public void Split(
        CsgPolygon polygon,
        List<CsgPolygon> coplanarFront,
        List<CsgPolygon> coplanarBack,
        List<CsgPolygon> front,
        List<CsgPolygon> back)
    {
        int polygonType = 0;
        Span<int> types = polygon.Vertices.Count <= 64 ? stackalloc int[polygon.Vertices.Count] : new int[polygon.Vertices.Count];
        for (int i = 0; i < polygon.Vertices.Count; i++)
        {
            double t = Normal.Dot(polygon.Vertices[i]) - W;
            int type = t < -Epsilon ? Back : t > Epsilon ? Front : Coplanar;
            polygonType |= type;
            types[i] = type;
        }

        switch (polygonType)
        {
            case Coplanar:
                (Normal.Dot(polygon.Plane.Normal) > 0 ? coplanarFront : coplanarBack).Add(polygon);
                break;
            case Front:
                front.Add(polygon);
                break;
            case Back:
                back.Add(polygon);
                break;
            case Spanning:
                var f = new List<Vector3d>();
                var b = new List<Vector3d>();
                for (int i = 0; i < polygon.Vertices.Count; i++)
                {
                    int j = (i + 1) % polygon.Vertices.Count;
                    int ti = types[i], tj = types[j];
                    var vi = polygon.Vertices[i];
                    var vj = polygon.Vertices[j];
                    if (ti != Back)
                        f.Add(vi);
                    if (ti != Front)
                        b.Add(vi);
                    if ((ti | tj) == Spanning)
                    {
                        double t = (W - Normal.Dot(vi)) / Normal.Dot(vj - vi);
                        var v = Vector3d.Lerp(vi, vj, t);
                        f.Add(v);
                        b.Add(v);
                    }
                }
                if (f.Count >= 3)
                    front.Add(new CsgPolygon(f, polygon.Plane));
                if (b.Count >= 3)
                    back.Add(new CsgPolygon(b, polygon.Plane));
                break;
        }
    }
}

internal sealed class CsgPolygon
{
    public List<Vector3d> Vertices { get; }
    public CsgPlane Plane { get; private set; }

    public CsgPolygon(List<Vector3d> vertices, in CsgPlane plane)
    {
        Vertices = vertices;
        Plane = plane;
    }

    public void Flip()
    {
        Vertices.Reverse();
        Plane = Plane.Flipped;
    }
}

/// <summary>
/// A BSP node. Every tree walk here is written with an <b>explicit stack</b>, never with
/// recursion: the splitting plane is the first polygon's plane, so a convex body (a sphere
/// above all) produces an essentially degenerate chain whose depth is O(polygons) — two
/// 32k-triangle spheres overflowed the CLR stack in <c>Invert</c> and crashed the process
/// rather than failing. Depth is a property of the input, so no stack size is "enough";
/// the walks are iterative instead. Each walk keeps the recursive visit order exactly
/// (own polygons, then front subtree, then back subtree — LIFO stack, back pushed first),
/// because the polygon order feeds the next <see cref="Build"/> and therefore decides how
/// the result is subdivided.
/// </summary>
internal sealed class CsgNode
{
    private CsgPlane? _plane;
    private CsgNode? _front;
    private CsgNode? _back;
    private List<CsgPolygon> _polygons = [];

    public CsgNode()
    {
    }

    public CsgNode(List<CsgPolygon> polygons) => Build(polygons);

    /// <summary>Convert solid space to empty space and vice versa.</summary>
    public void Invert()
    {
        var stack = new Stack<CsgNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            foreach (var p in node._polygons)
                p.Flip();
            node._plane = node._plane?.Flipped;
            if (node._front is not null)
                stack.Push(node._front);
            if (node._back is not null)
                stack.Push(node._back);
            (node._front, node._back) = (node._back, node._front);
        }
    }

    /// <summary>Returns the parts of <paramref name="polygons"/> outside this BSP's solid.</summary>
    private List<CsgPolygon> ClipPolygons(List<CsgPolygon> polygons)
    {
        var result = new List<CsgPolygon>();
        var stack = new Stack<(CsgNode Node, List<CsgPolygon> Polygons)>();
        stack.Push((this, polygons));
        while (stack.Count > 0)
        {
            var (node, incoming) = stack.Pop();
            if (node._plane is null)
            {
                result.AddRange(incoming);
                continue;
            }

            var front = new List<CsgPolygon>();
            var back = new List<CsgPolygon>();
            foreach (var p in incoming)
                node._plane.Value.Split(p, front, back, front, back);

            // Push back first so the front subtree is fully consumed first — the visit
            // order of the recursive form, on which the output polygon order depends.
            // With no back subtree, back-side polygons are inside the solid: discarded.
            if (node._back is not null)
                stack.Push((node._back, back));
            if (node._front is not null)
                stack.Push((node._front, front));
            else
                result.AddRange(front);
        }
        return result;
    }

    /// <summary>Removes the parts of this tree's polygons inside <paramref name="bsp"/>'s solid.</summary>
    public void ClipTo(CsgNode bsp)
    {
        var stack = new Stack<CsgNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            node._polygons = bsp.ClipPolygons(node._polygons);
            if (node._back is not null)
                stack.Push(node._back);
            if (node._front is not null)
                stack.Push(node._front);
        }
    }

    public List<CsgPolygon> AllPolygons()
    {
        var result = new List<CsgPolygon>();
        var stack = new Stack<CsgNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            result.AddRange(node._polygons);
            if (node._back is not null)
                stack.Push(node._back);
            if (node._front is not null)
                stack.Push(node._front);
        }
        return result;
    }

    public void Build(List<CsgPolygon> polygons)
    {
        var stack = new Stack<(CsgNode Node, List<CsgPolygon> Polygons)>();
        stack.Push((this, polygons));
        while (stack.Count > 0)
        {
            var (node, incoming) = stack.Pop();
            if (incoming.Count == 0)
                continue;
            node._plane ??= incoming[0].Plane;

            var front = new List<CsgPolygon>();
            var back = new List<CsgPolygon>();
            foreach (var p in incoming)
                node._plane.Value.Split(p, node._polygons, node._polygons, front, back);

            if (back.Count > 0)
            {
                node._back ??= new CsgNode();
                stack.Push((node._back, back));
            }
            if (front.Count > 0)
            {
                node._front ??= new CsgNode();
                stack.Push((node._front, front));
            }
        }
    }
}
