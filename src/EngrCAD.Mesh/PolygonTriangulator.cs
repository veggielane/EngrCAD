using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Ear-clipping triangulation of simple (non-self-intersecting, hole-free) 2D polygons.
/// O(n²); fine for boundary loops from tessellation. Output triangles are counter-clockwise
/// in the 2D plane regardless of input orientation.
/// </summary>
public static class PolygonTriangulator
{
    public static List<(int A, int B, int C)> Triangulate(IReadOnlyList<Vector2d> polygon)
    {
        int n = polygon.Count;
        if (n < 3)
            throw new ArgumentException("Polygon needs at least 3 vertices.", nameof(polygon));

        // Work in counter-clockwise order.
        var indices = new List<int>(n);
        for (int i = 0; i < n; i++)
            indices.Add(i);
        if (SignedArea(polygon) < 0)
            indices.Reverse();

        var triangles = new List<(int, int, int)>(n - 2);
        int guard = 0;
        while (indices.Count > 3)
        {
            bool clipped = false;
            for (int i = 0; i < indices.Count; i++)
            {
                int prev = indices[(i - 1 + indices.Count) % indices.Count];
                int curr = indices[i];
                int next = indices[(i + 1) % indices.Count];

                var a = polygon[prev];
                var b = polygon[curr];
                var c = polygon[next];
                if ((b - a).Cross(c - b) <= 0)
                    continue; // reflex or degenerate corner

                bool containsOther = false;
                foreach (int other in indices)
                {
                    if (other == prev || other == curr || other == next)
                        continue;
                    if (PointInTriangle(polygon[other], a, b, c))
                    {
                        containsOther = true;
                        break;
                    }
                }
                if (containsOther)
                    continue;

                triangles.Add((prev, curr, next));
                indices.RemoveAt(i);
                clipped = true;
                break;
            }

            if (!clipped)
            {
                // Degenerate (collinear runs, numeric trouble): fall back to a fan so we
                // always terminate with full coverage.
                for (int i = 1; i < indices.Count - 1; i++)
                    triangles.Add((indices[0], indices[i], indices[i + 1]));
                return triangles;
            }
            if (++guard > n * n)
                throw new InvalidOperationException("Ear clipping failed to terminate.");
        }

        triangles.Add((indices[0], indices[1], indices[2]));
        return triangles;
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

    private static bool PointInTriangle(in Vector2d p, in Vector2d a, in Vector2d b, in Vector2d c)
    {
        const double eps = 1e-12;
        double d1 = (b - a).Cross(p - a);
        double d2 = (c - b).Cross(p - b);
        double d3 = (a - c).Cross(p - c);
        return d1 >= -eps && d2 >= -eps && d3 >= -eps;
    }
}
