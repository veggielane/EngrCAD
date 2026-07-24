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
        foreach (var p in _polygons)
            p.Flip();
        _plane = _plane?.Flipped;
        _front?.Invert();
        _back?.Invert();
        (_front, _back) = (_back, _front);
    }

    /// <summary>Returns the parts of <paramref name="polygons"/> outside this BSP's solid.</summary>
    private List<CsgPolygon> ClipPolygons(List<CsgPolygon> polygons)
    {
        if (_plane is null)
            return [.. polygons];

        var front = new List<CsgPolygon>();
        var back = new List<CsgPolygon>();
        foreach (var p in polygons)
            _plane.Value.Split(p, front, back, front, back);

        var result = _front is not null ? _front.ClipPolygons(front) : front;
        if (_back is not null)
            result.AddRange(_back.ClipPolygons(back));
        // With no back subtree, back-side polygons are inside the solid and are discarded.
        return result;
    }

    /// <summary>Removes the parts of this tree's polygons inside <paramref name="bsp"/>'s solid.</summary>
    public void ClipTo(CsgNode bsp)
    {
        _polygons = bsp.ClipPolygons(_polygons);
        _front?.ClipTo(bsp);
        _back?.ClipTo(bsp);
    }

    public List<CsgPolygon> AllPolygons()
    {
        var result = new List<CsgPolygon>(_polygons);
        if (_front is not null)
            result.AddRange(_front.AllPolygons());
        if (_back is not null)
            result.AddRange(_back.AllPolygons());
        return result;
    }

    public void Build(List<CsgPolygon> polygons)
    {
        if (polygons.Count == 0)
            return;
        _plane ??= polygons[0].Plane;

        var front = new List<CsgPolygon>();
        var back = new List<CsgPolygon>();
        foreach (var p in polygons)
            _plane.Value.Split(p, _polygons, _polygons, front, back);

        if (front.Count > 0)
        {
            _front ??= new CsgNode();
            _front.Build(front);
        }
        if (back.Count > 0)
        {
            _back ??= new CsgNode();
            _back.Build(back);
        }
    }
}
