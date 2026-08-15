using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Modeling;

namespace EngrCAD.Cam;

/// <summary>
/// The sparse-infill pattern family. Every member is anchored to the GLOBAL grid (the
/// stage-1 phase rule — the pattern is a function of the stated spacing, never of where
/// the part sits), and every member holds the stated DENSITY by scaling its spacing to
/// its direction count: grid lays two directions at twice the spacing, triangles three at
/// three times, so a density means one thing across the family. Gyroid sections the TPMS
/// level set at each layer's own z (the implicit engine's surface, so the pattern is
/// genuinely three-dimensional and self-supporting); Hilbert rides the landed
/// <see cref="SpaceFillingInfill"/> machinery.
/// </summary>
internal static class FdmInfill
{
    internal static List<SlicePath> Sparse(
        Region2d core, double spacing, int layerIndex, double z, InfillPattern pattern)
    {
        switch (pattern)
        {
            case InfillPattern.Rectilinear:
                return FdmSlicer.RectilinearInfill(core, spacing,
                    layerIndex % 2 == 0 ? Math.PI / 4 : 3 * Math.PI / 4);

            case InfillPattern.Grid:
            {
                var paths = FdmSlicer.RectilinearInfill(core, spacing * 2, Math.PI / 4);
                paths.AddRange(FdmSlicer.RectilinearInfill(core, spacing * 2, 3 * Math.PI / 4));
                return paths;
            }

            case InfillPattern.Triangles:
            {
                var paths = FdmSlicer.RectilinearInfill(core, spacing * 3, 0);
                paths.AddRange(FdmSlicer.RectilinearInfill(core, spacing * 3, Math.PI / 3));
                paths.AddRange(FdmSlicer.RectilinearInfill(core, spacing * 3, 2 * Math.PI / 3));
                return paths;
            }

            case InfillPattern.Concentric:
            {
                var paths = new List<SlicePath>();
                for (int k = 0; ; k++)
                {
                    IReadOnlyList<Region2d> rings = k == 0
                        ? new[] { core }
                        : Region2dOffset.Offset(core, -k * spacing);
                    if (rings.Count == 0)
                        break;
                    foreach (var ring in rings)
                    {
                        paths.Add(new SlicePath(SlicePathRole.Infill, ring.Outer, IsClosed: true));
                        foreach (var hole in ring.Holes)
                            paths.Add(new SlicePath(SlicePathRole.Infill, hole, IsClosed: true));
                    }
                }
                return paths;
            }

            case InfillPattern.Gyroid:
                return GyroidFill(core, spacing, z);

            case InfillPattern.Hilbert:
            {
                var sketch = Sketch.Polygon(core.Outer);
                foreach (var hole in core.Holes)
                    sketch = sketch.WithHole(Sketch.Polygon(hole));
                var fill = SpaceFillingInfill.Fill(sketch, spacing, tiled: true);
                var paths = new List<SlicePath>();
                foreach (var run in fill.Runs)
                {
                    if (run.Count >= 2)
                        paths.Add(new SlicePath(SlicePathRole.Infill, run, IsClosed: false));
                }
                return paths;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(pattern), pattern,
                    "Unknown infill pattern.");
        }
    }

    /// <summary>The gyroid's own zero level sectioned at the layer's z: marching squares
    /// over the core's box, chained by exact endpoint equality, then clipped to the core
    /// by the even-odd rule at vertex granularity (the grid step is the honest resolution
    /// of the curve anyway).</summary>
    private static List<SlicePath> GyroidFill(Region2d core, double spacing, double z)
    {
        var bounds = core.Bounds;
        double period = 2 * spacing;
        double step = spacing / 4;
        double pad = step;
        double spanX = bounds.Max.X - bounds.Min.X + 2 * pad;
        double spanY = bounds.Max.Y - bounds.Min.Y + 2 * pad;
        int nx = Math.Max(2, (int)Math.Ceiling(spanX / step) + 1);
        int ny = Math.Max(2, (int)Math.Ceiling(spanY / step) + 1);

        var field = new GyroidLevelField(period, new Aabb(
            new Vector3d(bounds.Min.X - pad, bounds.Min.Y - pad, z - 1),
            new Vector3d(bounds.Max.X + pad, bounds.Max.Y + pad, z + 1)));
        var contours = SdfContours.OnPlane(
            field,
            new Vector3d(bounds.Min.X - pad, bounds.Min.Y - pad, z),
            new Vector3d(spanX, 0, 0), new Vector3d(0, spanY, 0),
            nx, ny, [0.0]);

        var paths = new List<SlicePath>();
        foreach (var chain in CncSurfacing.ChainSegments(contours[0].Segments))
        {
            var run = new List<Vector2d>();
            List<Vector3d> points = chain.IsClosed
                ? [.. chain.Points, chain.Points[0]]
                : chain.Points;
            foreach (var point in points)
            {
                var q = new Vector2d(point.X, point.Y);
                if (Contains(core, q))
                {
                    run.Add(q);
                }
                else if (run.Count >= 2)
                {
                    paths.Add(new SlicePath(SlicePathRole.Infill, run, IsClosed: false));
                    run = [];
                }
                else
                {
                    run.Clear();
                }
            }
            if (run.Count >= 2)
                paths.Add(new SlicePath(SlicePathRole.Infill, run, IsClosed: false));
        }
        return paths;
    }

    /// <summary>Even-odd containment (outer minus holes), the half-open vertex rule.</summary>
    internal static bool Contains(Region2d region, in Vector2d p)
    {
        if (!InsideLoop(region.Outer, p))
            return false;
        foreach (var hole in region.Holes)
        {
            if (InsideLoop(hole, p))
                return false;
        }
        return true;

        static bool InsideLoop(IReadOnlyList<Vector2d> loop, in Vector2d p)
        {
            bool inside = false;
            for (int i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                if (a.Y > p.Y == b.Y > p.Y)
                    continue;
                if (p.X < a.X + (p.Y - a.Y) / (b.Y - a.Y) * (b.X - a.X))
                    inside = !inside;
            }
            return inside;
        }
    }

    /// <summary>The gyroid LEVEL function (not a distance — marching squares only reads
    /// sign crossings): <c>sin x·cos y + sin y·cos z + sin z·cos x</c> over the stated
    /// period. Deliberately not <c>Sdf.Gyroid</c>, which is the thickened LATTICE solid;
    /// the infill wants the surface's own zero set.</summary>
    private sealed class GyroidLevelField(double period, Aabb bounds) : Sdf
    {
        public override Aabb Bounds { get; } = bounds;

        public override double Evaluate(in Vector3d p)
        {
            double k = 2 * Math.PI / period;
            double x = k * p.X, y = k * p.Y, z = k * p.Z;
            return Math.Sin(x) * Math.Cos(y) + Math.Sin(y) * Math.Cos(z)
                + Math.Sin(z) * Math.Cos(x);
        }
    }
}
