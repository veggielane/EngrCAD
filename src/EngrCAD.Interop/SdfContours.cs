using System.Buffers;
using EngrCAD.Core;
using EngrCAD.Implicit;

namespace EngrCAD.Interop;

/// <summary>
/// One extracted iso level: the requested distance value and its contour segments,
/// with 3D endpoints in the SDF's own coordinate space, lying exactly on the sampled
/// plane. Endpoints shared between adjacent grid cells are interpolated from the same
/// two samples with the same expression, so touching segments meet bit-identically —
/// consumers may chain segments into loops by exact endpoint equality. (A contour
/// passing exactly through a sample node is one endpoint of a segment in each of the
/// four surrounding cells, so such a point can have multiplicity above two.)
/// </summary>
public sealed record SdfContourLevel(double Level, IReadOnlyList<(Vector3d A, Vector3d B)> Segments);

/// <summary>
/// Marching-squares iso-contour extraction of an SDF restricted to a planar sample
/// grid — the geometry behind section-plane isolines. The d = 0 contour is the exact
/// (to sampling) surface cross-section; other levels visualize the field itself
/// (wall thickness at a glance, blend/offset debugging). UI-free and deterministic:
/// rendering lives in the viewer, and identical inputs produce identical output.
/// </summary>
public static class SdfContours
{
    /// <summary>
    /// Samples <paramref name="sdf"/> on a planar grid and extracts the iso-contours
    /// of each requested level. The grid spans the parallelogram
    /// <c>origin + u·uSide + v·vSide</c> for u, v in [0, 1], with
    /// <paramref name="uSamples"/> × <paramref name="vSamples"/> sample points
    /// (≥ 2 each); the plane is arbitrary — pass the section plane mapped into the
    /// SDF's own space (an affine instance transform maps a rectangle to a
    /// parallelogram, which this parameterization represents exactly). Sampling goes
    /// through the batch <see cref="Sdf.Evaluate(ReadOnlySpan{Vector3d}, Span{double})"/>
    /// seam once for the whole grid; each level is then a marching-squares pass with
    /// linear interpolation along cell edges (crossing-position error is
    /// O(h² · field curvature) for grid step h). Ambiguous saddle cells are resolved
    /// by the cell-center average. Returns one entry per requested level, in order;
    /// levels that do not cross the grid yield empty segment lists.
    /// </summary>
    public static IReadOnlyList<SdfContourLevel> OnPlane(
        Sdf sdf,
        in Vector3d origin, in Vector3d uSide, in Vector3d vSide,
        int uSamples, int vSamples,
        ReadOnlySpan<double> levels)
    {
        if (uSamples < 2 || vSamples < 2)
            throw new ArgumentOutOfRangeException(
                uSamples < 2 ? nameof(uSamples) : nameof(vSamples),
                "Contour extraction needs at least a 2x2 sample grid.");

        int count = uSamples * vSamples;
        var points = ArrayPool<Vector3d>.Shared.Rent(count);
        var values = ArrayPool<double>.Shared.Rent(count);
        try
        {
            // Sample points: shared between all levels, evaluated in one batch call.
            double uStep = 1.0 / (uSamples - 1);
            double vStep = 1.0 / (vSamples - 1);
            for (int j = 0; j < vSamples; j++)
            {
                var rowStart = origin + vSide * (j * vStep);
                for (int i = 0; i < uSamples; i++)
                    points[j * uSamples + i] = rowStart + uSide * (i * uStep);
            }
            sdf.Evaluate(points.AsSpan(0, count), values.AsSpan(0, count));

            var result = new SdfContourLevel[levels.Length];
            for (int l = 0; l < levels.Length; l++)
                result[l] = new SdfContourLevel(levels[l], March(points, values, uSamples, vSamples, levels[l]));
            return result;
        }
        finally
        {
            ArrayPool<Vector3d>.Shared.Return(points);
            ArrayPool<double>.Shared.Return(values);
        }
    }

    /// <summary>Marching squares over the sampled grid for one iso level.</summary>
    private static List<(Vector3d A, Vector3d B)> March(
        Vector3d[] points, double[] values, int uSamples, int vSamples, double level)
    {
        var segments = new List<(Vector3d A, Vector3d B)>();
        for (int j = 0; j < vSamples - 1; j++)
        {
            for (int i = 0; i < uSamples - 1; i++)
            {
                // Corner sample indices: c0 bottom-left, c1 bottom-right,
                // c2 top-right, c3 top-left (u is "x", v is "y" of the grid).
                int c0 = j * uSamples + i;
                int c1 = c0 + 1;
                int c3 = c0 + uSamples;
                int c2 = c3 + 1;

                // Inside = strictly below the level. Strictness matters for the
                // interpolation guard: a crossing edge always has one strict side,
                // so the denominator in Crossing() is never exactly zero.
                int mask = (values[c0] < level ? 1 : 0)
                         | (values[c1] < level ? 2 : 0)
                         | (values[c2] < level ? 4 : 0)
                         | (values[c3] < level ? 8 : 0);
                if (mask == 0 || mask == 15)
                    continue;

                // Cell edges (each expression is identical to the neighboring cell's
                // view of the same edge, so shared endpoints are bit-identical):
                // e0 bottom c0->c1, e1 right c1->c2, e2 top c3->c2, e3 left c0->c3.
                switch (mask)
                {
                    case 1:
                    case 14:
                        segments.Add((Crossing(points, values, c0, c3, level), Crossing(points, values, c0, c1, level)));
                        break;
                    case 2:
                    case 13:
                        segments.Add((Crossing(points, values, c0, c1, level), Crossing(points, values, c1, c2, level)));
                        break;
                    case 3:
                    case 12:
                        segments.Add((Crossing(points, values, c0, c3, level), Crossing(points, values, c1, c2, level)));
                        break;
                    case 4:
                    case 11:
                        segments.Add((Crossing(points, values, c1, c2, level), Crossing(points, values, c3, c2, level)));
                        break;
                    case 6:
                    case 9:
                        segments.Add((Crossing(points, values, c0, c1, level), Crossing(points, values, c3, c2, level)));
                        break;
                    case 7:
                    case 8:
                        segments.Add((Crossing(points, values, c0, c3, level), Crossing(points, values, c3, c2, level)));
                        break;
                    case 5:
                        // Ambiguous saddle (c0 and c2 inside): the cell-center average
                        // decides whether the two inside corners connect diagonally.
                        if (Center(values, c0, c1, c2, c3) < level)
                        {
                            segments.Add((Crossing(points, values, c0, c3, level), Crossing(points, values, c3, c2, level)));
                            segments.Add((Crossing(points, values, c0, c1, level), Crossing(points, values, c1, c2, level)));
                        }
                        else
                        {
                            segments.Add((Crossing(points, values, c0, c3, level), Crossing(points, values, c0, c1, level)));
                            segments.Add((Crossing(points, values, c1, c2, level), Crossing(points, values, c3, c2, level)));
                        }
                        break;
                    case 10:
                        // Ambiguous saddle (c1 and c3 inside), same resolution rule.
                        if (Center(values, c0, c1, c2, c3) < level)
                        {
                            segments.Add((Crossing(points, values, c0, c3, level), Crossing(points, values, c0, c1, level)));
                            segments.Add((Crossing(points, values, c1, c2, level), Crossing(points, values, c3, c2, level)));
                        }
                        else
                        {
                            segments.Add((Crossing(points, values, c0, c1, level), Crossing(points, values, c1, c2, level)));
                            segments.Add((Crossing(points, values, c3, c2, level), Crossing(points, values, c0, c3, level)));
                        }
                        break;
                }
            }
        }
        return segments;
    }

    private static double Center(double[] values, int c0, int c1, int c2, int c3) =>
        (values[c0] + values[c1] + values[c2] + values[c3]) * 0.25;

    /// <summary>
    /// Linear interpolation of the crossing point on the edge between samples
    /// <paramref name="a"/> and <paramref name="b"/>. Only called for crossing edges,
    /// where the strict inside test guarantees values[a] != values[b]; the clamp is a
    /// pure roundoff guard keeping the point on the edge (scale-free, not a Tolerance
    /// decision — the classification already decided the topology).
    /// </summary>
    private static Vector3d Crossing(Vector3d[] points, double[] values, int a, int b, double level)
    {
        double t = (level - values[a]) / (values[b] - values[a]);
        t = Math.Clamp(t, 0.0, 1.0);
        return points[a] + (points[b] - points[a]) * t;
    }
}
