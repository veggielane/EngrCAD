using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Modeling;

/// <summary>Where a bounded extrusion or cut stops against the target body — the
/// build123d/CadQuery <c>until</c> vocabulary (<c>Until.NEXT</c> / <c>Until.LAST</c>).</summary>
public enum Until
{
    /// <summary>Stop at the FIRST surface the extrusion meets: a boss grows until it
    /// lands on the body; a cut punches through the first wall and stops in the void
    /// behind it.</summary>
    Next,

    /// <summary>Continue to the body's far boundary: a boss reaches flush with the far
    /// face; a cut goes through everything (with the standard never-coplanar overshoot
    /// past the far face).</summary>
    Last,
}

/// <summary>The distance resolution behind <see cref="Shape.ExtrudeUntil"/> /
/// <see cref="Shape.CutUntil"/>: probe rays from the profile's interior against the
/// target's mesh, clustered into an unambiguous stop plane or refused loudly.
/// Internal seam so tests can pin the distance math without lowering booleans.</summary>
internal static class UntilResolver
{
    internal readonly record struct Resolution(double Distance, double Overshoot, double TopClearance)
    {
        /// <summary>The extrusion depth below the sketch plane.</summary>
        public double Height => Distance + Overshoot;
    }

    /// <summary>
    /// Resolves the stop distance below <paramref name="plane"/> (measured along
    /// −normal) for an extrusion of <paramref name="sketch"/> against
    /// <paramref name="target"/>. Throws <see cref="InvalidOperationException"/> naming
    /// the failure when the stop is ambiguous: rays that miss the body (the profile
    /// overhangs it), non-alternating enter/exit sequences (tangent grazes), or hit
    /// distances that do not cluster into one plane perpendicular to the direction.
    /// </summary>
    internal static Resolution Resolve(
        Shape target, Sketch sketch, SketchPlane plane, Until until, bool cut,
        MeshQuality? quality)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(sketch);

        var mesh = target.ToMesh(quality).Triangulated();
        var bounds = ComputeBounds(mesh);
        double extent = Math.Max(bounds.Size.X, Math.Max(bounds.Size.Y, bounds.Size.Z));
        if (!(extent > 0))
            throw new InvalidOperationException("ExtrudeUntil: the target body is empty.");

        var triangles = CollectTriangles(mesh);
        var samples = SampleProfile(sketch);
        var direction = -plane.Normal;
        // Rays start a hair above the plane so a body face exactly ON the plane is still
        // a robust hit (a ray origin exactly on a triangle is decided by rounding).
        double lift = 1e-6 * extent;
        // Two independently-derived distances count as one plane at the seam-ish tier:
        // planar stop faces tessellate exactly, so genuine planes cluster far tighter
        // and genuinely curved stops spread far wider.
        double tolerance = 1e-6 * extent;

        int missed = 0;
        var firstEnter = new List<double>();
        var firstExit = new List<double>();
        var lastExit = new List<double>();
        double minSlab = double.PositiveInfinity;   // first enter -> its exit
        double minGapAfter = double.PositiveInfinity; // first exit -> next enter
        bool anyInsideStart = false;

        foreach (var point in samples)
        {
            var origin = plane.ToWorld(point) + plane.Normal * lift;
            var hits = CastRay(triangles, origin, direction, lift, tolerance);
            if (hits.Count == 0)
            {
                missed++;
                continue;
            }

            // Enter/exit must alternate; a broken sequence means the ray grazed a
            // silhouette, and no distance derived from it can be trusted.
            for (int i = 1; i < hits.Count; i++)
            {
                if (hits[i].Entering == hits[i - 1].Entering)
                    throw new InvalidOperationException(
                        $"ExtrudeUntil: the probe ray at ({point.X:g6}, {point.Y:g6}) grazes the body " +
                        "tangentially (consecutive same-direction surface crossings) - the stop " +
                        "surface is ambiguous there. Extrude to an explicit depth instead.");
            }

            bool startsInside = !hits[0].Entering;
            anyInsideStart |= startsInside;

            if (!startsInside)
            {
                double enter = hits[0].Distance;
                firstEnter.Add(enter);
                if (hits.Count > 1)
                    minSlab = Math.Min(minSlab, hits[1].Distance - enter);
            }
            int exitIndex = hits.FindIndex(h => !h.Entering);
            if (exitIndex >= 0)
            {
                double exit = hits[exitIndex].Distance;
                firstExit.Add(exit);
                if (exitIndex + 1 < hits.Count)
                    minGapAfter = Math.Min(minGapAfter, hits[exitIndex + 1].Distance - exit);
            }
            for (int i = hits.Count - 1; i >= 0; i--)
            {
                if (!hits[i].Entering)
                {
                    lastExit.Add(hits[i].Distance);
                    break;
                }
            }
        }

        int total = samples.Count;
        if (missed > 0)
            throw new InvalidOperationException(
                $"ExtrudeUntil: {missed} of {total} probe rays never meet the body - the profile " +
                "overhangs it, so no stop face covers the whole extrusion. Shrink the profile or " +
                "extrude to an explicit depth.");

        if (!cut && anyInsideStart)
            throw new InvalidOperationException(
                "ExtrudeUntil: the sketch plane is inside the body - a boss has nowhere to grow. " +
                "Move the plane clear of the body (or use CutUntil).");

        List<double> stops = (cut, until) switch
        {
            (false, Until.Next) => firstEnter,
            (true, Until.Next) => firstExit,
            (_, Until.Last) => lastExit,
            _ => throw new ArgumentOutOfRangeException(nameof(until)),
        };
        if (stops.Count != total)
            throw new InvalidOperationException(
                "ExtrudeUntil: some probe rays found no surface crossing in the stop direction. " +
                "Extrude to an explicit depth instead.");

        double stop = ClusterOrThrow(stops, tolerance, until);
        double overshootCap = 0.02 * Math.Max(stop, extent);

        double overshoot = (cut, until) switch
        {
            // A boss overshoots INTO the material (half the thinnest slab, capped), so
            // the union never sees the boss's end face coplanar with the stop face.
            (false, Until.Next) => Math.Min(
                double.IsFinite(minSlab) ? 0.5 * minSlab : overshootCap, overshootCap),
            // Flush with the far face by definition; see the Shape doc for the
            // coplanar-union caveat.
            (false, Until.Last) => 0,
            // A cut overshoots into the void behind the wall (half the thinnest gap,
            // capped) - or past the far face when nothing lies beyond.
            (true, Until.Next) => Math.Min(
                double.IsFinite(minGapAfter) ? 0.5 * minGapAfter : overshootCap, overshootCap),
            // Through-all: the Drill rule - the tool must never end coplanar with the
            // far face.
            (true, Until.Last) => overshootCap,
            _ => 0,
        };

        if (!cut && until == Until.Next && stop <= tolerance)
            throw new InvalidOperationException(
                "ExtrudeUntil: the body already touches the sketch plane - a boss extruded " +
                "until the next face would have zero height.");

        // A cut tool must also clear the TOP: with the sketch plane exactly ON a body
        // face (the classic pocket-from-face), a tool starting at the plane leaves its
        // top face coplanar with the body's - the Drill overshoot lesson. A submerged
        // plane (some ray starts inside) gets NO top clearance: the tool's top face is
        // then interior, which the boolean handles, and extending it would wrongly cut
        // above the sketch plane.
        double topClearance = cut && !anyInsideStart ? overshootCap : 0;

        return new Resolution(stop, overshoot, topClearance);
    }

    private static double ClusterOrThrow(List<double> stops, double tolerance, Until until)
    {
        var sorted = stops.OrderBy(d => d).ToList();
        if (sorted[^1] - sorted[0] <= tolerance)
            return sorted[^1];

        // Not one plane: report the candidate clusters so the refusal is actionable.
        var clusters = new List<(double Distance, int Count)>();
        int start = 0;
        for (int i = 1; i <= sorted.Count; i++)
        {
            if (i == sorted.Count || sorted[i] - sorted[i - 1] > tolerance)
            {
                clusters.Add((sorted[start], i - start));
                start = i;
            }
        }
        string listing = string.Join(", ",
            clusters.Take(6).Select(c => $"{c.Distance:g6} ({c.Count} rays)"));
        if (clusters.Count > 6)
            listing += $", ... {clusters.Count - 6} more";
        throw new InvalidOperationException(
            $"ExtrudeUntil: Until.{until} found no single stop plane perpendicular to the " +
            $"extrusion - probe hits cluster at {listing}. The surface the extrusion meets is " +
            "curved or slanted; extrude to an explicit depth instead.");
    }

    // ---- probing machinery ----

    private readonly record struct Hit(double Distance, bool Entering);

    private readonly record struct Triangle(Vector3d A, Vector3d B, Vector3d C);

    private static List<Triangle> CollectTriangles(HalfEdgeMesh mesh)
    {
        var triangles = new List<Triangle>(mesh.FaceCount);
        Span<Vector3d> corners = stackalloc Vector3d[3];
        foreach (var face in mesh.Faces)
        {
            int k = 0;
            foreach (var vertex in face.Vertices())
            {
                if (k < 3)
                    corners[k] = vertex.Position;
                k++;
            }
            if (k == 3)
                triangles.Add(new Triangle(corners[0], corners[1], corners[2]));
        }
        return triangles;
    }

    private static Aabb ComputeBounds(HalfEdgeMesh mesh)
    {
        var bounds = Aabb.Empty;
        foreach (var vertex in mesh.Vertices)
            bounds = bounds.Union(vertex.Position);
        return bounds;
    }

    /// <summary>All surface crossings along the ray, sorted by distance FROM THE PLANE
    /// (the lift is subtracted), deduplicated at the weld-ish tier (a ray through a
    /// shared mesh edge reports both triangles).</summary>
    private static List<Hit> CastRay(
        List<Triangle> triangles, in Vector3d origin, in Vector3d direction,
        double lift, double tolerance)
    {
        var hits = new List<Hit>();
        foreach (var triangle in triangles)
        {
            // Moller-Trumbore. Near-parallel triangles are skipped (their crossing
            // distance is decided by rounding); the enter/exit alternation check
            // downstream catches the geometry they would have described.
            var e1 = triangle.B - triangle.A;
            var e2 = triangle.C - triangle.A;
            var p = direction.Cross(e2);
            double det = e1.Dot(p);
            // Scale-free guard: det is a volume-rate; compare against the edges' scale.
            double scale = e1.Length * e2.Length;
            if (Math.Abs(det) <= 1e-12 * scale)
                continue;
            double inv = 1.0 / det;
            var s = origin - triangle.A;
            double u = s.Dot(p) * inv;
            // Inclusive bounds (barycentric is dimensionless, so 1e-9 is relative): a
            // crossing exactly on a shared edge must register on at least one of the
            // two triangles - exclusive tests can drop it from BOTH, and a ray that
            // enters but never exits poisons the whole resolution. The dedupe below
            // absorbs the double count when both triangles report it.
            if (u < -1e-9 || u > 1 + 1e-9)
                continue;
            var q = s.Cross(e1);
            double v = direction.Dot(q) * inv;
            if (v < -1e-9 || u + v > 1 + 1e-9)
                continue;
            double t = e2.Dot(q) * inv;
            if (t <= 1e-9)
                continue;
            // det > 0 means the triangle's outward normal opposes the ray: entering.
            hits.Add(new Hit(t - lift, det > 0));
        }
        hits.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        // Dedupe: a crossing through a shared edge or vertex reports several triangles
        // at (bit-near) the same distance with the same orientation.
        var deduped = new List<Hit>(hits.Count);
        foreach (var hit in hits)
        {
            if (deduped.Count > 0 &&
                deduped[^1].Entering == hit.Entering &&
                hit.Distance - deduped[^1].Distance <= tolerance)
                continue;
            deduped.Add(hit);
        }
        return deduped;
    }

    /// <summary>Strictly interior sample points of the sketch region: grid midpoints of
    /// the sketch bounds kept where the exact signed distance is clear of the boundary
    /// (probe rays must not run along the extrusion's own side walls).</summary>
    private static List<Vector2d> SampleProfile(Sketch sketch)
    {
        var region = new SketchRegion(sketch);
        var bounds = region.Bounds;
        double sizeX = bounds.Size.X, sizeY = bounds.Size.Y;
        double extent = Math.Max(sizeX, sizeY);
        if (!(extent > 0))
            throw new InvalidOperationException("ExtrudeUntil: the sketch encloses no area.");

        const int grid = 16;
        // Prefer points clearly inside; a thin profile falls back to any interior point.
        foreach (double inset in new[] { extent * 1e-2, extent * 1e-6 })
        {
            var samples = new List<Vector2d>();
            for (int j = 0; j < grid; j++)
            {
                double y = bounds.Min.Y + (j + 0.5) * sizeY / grid;
                for (int i = 0; i < grid; i++)
                {
                    double x = bounds.Min.X + (i + 0.5) * sizeX / grid;
                    var point = new Vector2d(x, y);
                    if (region.SignedDistance(point) < -inset)
                        samples.Add(point);
                }
            }
            if (samples.Count > 0)
                return samples;
        }
        throw new InvalidOperationException(
            "ExtrudeUntil: no probe point lies inside the sketch region - the profile is too " +
            "thin to probe at the sampling resolution.");
    }
}
