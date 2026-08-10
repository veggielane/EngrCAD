using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Ecad;

/// <summary>
/// Recognises a copper <see cref="CurvedRegion2d"/> as a standard Gerber aperture (so a pad becomes a
/// proper flash rather than a region fill), and builds the region an aperture flash covers (so the
/// round-trip decoder reconstructs the same copper). The two directions are one file so a shape the
/// writer flashes is exactly the shape the reader rebuilds.
///
/// <para>Recognition is geometric — it reads the region's own loop structure, so a pad's placement
/// rotation never has to be threaded through: an axis-aligned rectangle or obround is flashed, a
/// rotated one (whose edges are no longer axis-aligned) falls to a faithful region fill. Correctness
/// never depends on recognition: an unrecognised feature is region-filled, which is exact for any
/// shape, so recognition only decides flash vs fill.</para>
/// </summary>
internal static class GerberShapes
{
    // A relative tolerance for "is this edge axis-aligned" / "is this the right radius". Scale-free:
    // an absolute epsilon on a length is wrong away from unit scale.
    private const double Relative = 1e-9;

    /// <summary>A round pad — a single full-circle outer loop, no holes.</summary>
    internal static bool TryDisc(CurvedRegion2d region, out Vector2d center, out double diameter)
    {
        center = default;
        diameter = 0;
        if (region.Holes.Count != 0 || region.Outer.Count != 1)
            return false;
        var edge = region.Outer[0];
        if (!edge.IsFullCircle)
            return false;
        center = edge.Center;
        diameter = 2 * edge.Radius;
        return true;
    }

    /// <summary>An axis-aligned rectangular pad — four straight edges, each horizontal or vertical,
    /// enclosing the exact area of its bounding box.</summary>
    internal static bool TryAxisAlignedRect(
        CurvedRegion2d region, out Vector2d center, out double width, out double height)
    {
        center = default;
        width = height = 0;
        if (region.Holes.Count != 0 || region.Outer.Count != 4)
            return false;
        foreach (var edge in region.Outer)
            if (edge.Kind != CurvedEdgeKind.Line)
                return false;

        var b = region.Bounds;
        width = b.Max.X - b.Min.X;
        height = b.Max.Y - b.Min.Y;
        center = new Vector2d((b.Min.X + b.Max.X) / 2, (b.Min.Y + b.Max.Y) / 2);
        if (!(width > 0) || !(height > 0))
            return false;

        double tol = Relative * Math.Max(width, height);
        foreach (var edge in region.Outer)
            if (Math.Abs(edge.Start.X - edge.End.X) > tol && Math.Abs(edge.Start.Y - edge.End.Y) > tol)
                return false;   // an edge that is neither horizontal nor vertical: a rotated rect

        // The area equalling the box confirms a full rectangle (not an L or a sliver).
        return Math.Abs(region.Area - width * height) <= 1e-6 * width * height;
    }

    /// <summary>An axis-aligned obround (stadium) pad — straight sides axis-aligned, its corner arcs
    /// of radius half the short side, enclosing the obround's own area.</summary>
    internal static bool TryAxisAlignedObround(
        CurvedRegion2d region, out Vector2d center, out double width, out double height)
    {
        center = default;
        width = height = 0;
        if (region.Holes.Count != 0)
            return false;

        bool hasArc = false;
        foreach (var edge in region.Outer)
        {
            if (edge.Kind == CurvedEdgeKind.Bezier)
                return false;
            if (edge.IsArc)
                hasArc = true;
        }
        if (!hasArc)
            return false;

        var b = region.Bounds;
        width = b.Max.X - b.Min.X;
        height = b.Max.Y - b.Min.Y;
        center = new Vector2d((b.Min.X + b.Max.X) / 2, (b.Min.Y + b.Max.Y) / 2);
        if (!(width > 0) || !(height > 0))
            return false;

        double r = Math.Min(width, height) / 2;
        double tol = Relative * Math.Max(width, height);
        foreach (var edge in region.Outer)
        {
            if (edge.Kind == CurvedEdgeKind.Line)
            {
                if (Math.Abs(edge.Start.X - edge.End.X) > tol && Math.Abs(edge.Start.Y - edge.End.Y) > tol)
                    return false;
            }
            else if (Math.Abs(edge.Radius - r) > 1e-6 * r)
            {
                return false;
            }
        }

        // Obround area = w·h − (4 − π)·r² (the four corners the rounding cut away).
        double expected = width * height - (4 - Math.PI) * r * r;
        return Math.Abs(region.Area - expected) <= 1e-6 * Math.Max(expected, 1e-30);
    }

    // ---- the region an aperture flash covers (the reader's side) --------------

    /// <summary>The disc a circle aperture flashes.</summary>
    internal static CurvedRegion2d Circle(in Vector2d center, double diameter) =>
        CurvedRegion2d.Disc(center, diameter / 2);

    /// <summary>The axis-aligned rectangle a rectangle aperture flashes.</summary>
    internal static CurvedRegion2d Rectangle(in Vector2d center, double width, double height)
    {
        double hw = width / 2, hh = height / 2;
        var bl = new Vector2d(center.X - hw, center.Y - hh);
        var br = new Vector2d(center.X + hw, center.Y - hh);
        var tr = new Vector2d(center.X + hw, center.Y + hh);
        var tl = new Vector2d(center.X - hw, center.Y + hh);
        return new CurvedRegion2d(
        [
            CurvedEdge2d.Line(bl, br), CurvedEdge2d.Line(br, tr),
            CurvedEdge2d.Line(tr, tl), CurvedEdge2d.Line(tl, bl),
        ]);
    }

    /// <summary>The axis-aligned stadium an obround aperture flashes (the same set the copper model's
    /// oval pad encloses, so their symmetric difference is empty).</summary>
    internal static CurvedRegion2d Obround(in Vector2d center, double width, double height)
    {
        double r = Math.Min(width, height) / 2;
        var edges = new List<CurvedEdge2d>(4);
        if (width >= height)
        {
            double x = width / 2 - r;                    // the two arc centres sit at ±x
            var rightC = new Vector2d(center.X + x, center.Y);
            var leftC = new Vector2d(center.X - x, center.Y);
            var bl = new Vector2d(center.X - x, center.Y - r);
            var br = new Vector2d(center.X + x, center.Y - r);
            var tr = new Vector2d(center.X + x, center.Y + r);
            var tl = new Vector2d(center.X - x, center.Y + r);
            if (x > 0)
                edges.Add(CurvedEdge2d.Line(bl, br));
            edges.Add(CurvedEdge2d.ArcBetween(br, tr, rightC, r, turn: 1));
            if (x > 0)
                edges.Add(CurvedEdge2d.Line(tr, tl));
            edges.Add(CurvedEdge2d.ArcBetween(tl, bl, leftC, r, turn: 1));
        }
        else
        {
            double y = height / 2 - r;
            var topC = new Vector2d(center.X, center.Y + y);
            var bottomC = new Vector2d(center.X, center.Y - y);
            var br = new Vector2d(center.X + r, center.Y - y);
            var tr = new Vector2d(center.X + r, center.Y + y);
            var tl = new Vector2d(center.X - r, center.Y + y);
            var bl = new Vector2d(center.X - r, center.Y - y);
            edges.Add(CurvedEdge2d.Line(br, tr));
            edges.Add(CurvedEdge2d.ArcBetween(tr, tl, topC, r, turn: 1));
            edges.Add(CurvedEdge2d.Line(tl, bl));
            edges.Add(CurvedEdge2d.ArcBetween(bl, br, bottomC, r, turn: 1));
        }
        return new CurvedRegion2d(edges);
    }

    /// <summary>The regular polygon a polygon aperture flashes (vertex 0 on +x, rotated by
    /// <paramref name="rotationDegrees"/>).</summary>
    internal static CurvedRegion2d Polygon(
        in Vector2d center, double diameter, int vertices, double rotationDegrees)
    {
        if (vertices < 3)
            throw new FormatException($"A polygon aperture needs at least 3 vertices (got {vertices}).");
        double radius = diameter / 2;
        double phase = rotationDegrees * Math.PI / 180.0;
        var pts = new Vector2d[vertices];
        for (int k = 0; k < vertices; k++)
        {
            double a = phase + 2 * Math.PI * k / vertices;
            pts[k] = new Vector2d(center.X + radius * Math.Cos(a), center.Y + radius * Math.Sin(a));
        }
        var edges = new CurvedEdge2d[vertices];
        for (int k = 0; k < vertices; k++)
            edges[k] = CurvedEdge2d.Line(pts[k], pts[(k + 1) % vertices]);
        return new CurvedRegion2d(edges);
    }
}
