using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Ecad;

/// <summary>Small geometry helpers shared by the board and layout — polygon canonicalisation,
/// point-in-polygon, and the plate build/volume the oracle rests on. Kept in one place so the
/// board and layout cannot build a plate two ways.</summary>
internal static class PcbGeometry
{
    /// <summary>Cleans a polygon loop for <see cref="Sketch.Polygon"/>: drops a repeated
    /// closing point (IDF loops close explicitly) and any consecutive duplicates, so the
    /// sketch's own <c>% count</c> closing rule adds exactly one closing edge.</summary>
    internal static IReadOnlyList<Vector2d> CanonicalLoop(IEnumerable<Vector2d> points)
    {
        var raw = points as IReadOnlyList<Vector2d> ?? [.. points];
        var loop = new List<Vector2d>(raw.Count);
        foreach (var p in raw)
            if (loop.Count == 0 || !Same(loop[^1], p))
                loop.Add(p);
        // Drop the explicit closing point if the loop repeats its start.
        if (loop.Count > 1 && Same(loop[0], loop[^1]))
            loop.RemoveAt(loop.Count - 1);
        return loop;
    }

    private static bool Same(Vector2d a, Vector2d b) =>
        a.X.Equals(b.X) && a.Y.Equals(b.Y);

    /// <summary>Whether <paramref name="point"/> lies inside the closed polygon
    /// (even-odd ray casting; a point exactly on an edge is decided by the ray parity, which
    /// is fine for pad/via centres well inside or well outside).</summary>
    internal static bool PolygonContains(IReadOnlyList<Vector2d> polygon, Vector2d point)
    {
        bool inside = false;
        int n = polygon.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vector2d a = polygon[i], b = polygon[j];
            bool crosses = (a.Y > point.Y) != (b.Y > point.Y)
                && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X;
            if (crosses)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>Builds the plate: extrude <paramref name="outline"/> to
    /// <paramref name="thickness"/>, then drill each hole through (grouped by diameter). The
    /// drill plane sits on the top face and cuts down through the board with an overshoot, so
    /// each round hole removes exactly πr² × thickness of material.</summary>
    internal static Shape BuildPlate(
        IReadOnlyList<Vector2d> outline, double thickness,
        IEnumerable<(Vector2d Center, double Diameter)> holes)
    {
        var plate = Shape.Extrude(Sketch.Polygon([.. outline]), thickness);

        // Drill from the top face downward, one call per distinct diameter (Drill takes one
        // hole spec applied at many points). Depth passes fully through with an overshoot so
        // the tool bottom is never coplanar with the board's own bottom face.
        var top = SketchPlane.At(new Vector3d(0, 0, thickness), Vector3d.UnitX, Vector3d.UnitY);
        double depth = thickness * 1.5;
        foreach (var group in holes.GroupBy(h => h.Diameter))
        {
            var points = group.Select(h => h.Center).ToList();
            plate = plate.Drill(HoleSpec.Simple(group.Key), points, depth, top);
        }
        return plate;
    }

    /// <summary>The closed-form volume of the plate: outline area × thickness, less each
    /// round hole's cylinder πr² × thickness.</summary>
    internal static double ExpectedPlateVolume(
        double outlineArea, double thickness, IEnumerable<double> holeDiameters)
    {
        double holeArea = holeDiameters.Sum(d => Math.PI * (d / 2) * (d / 2));
        return (outlineArea - holeArea) * thickness;
    }
}
