using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Cam;

/// <summary>
/// The cutter's BOTTOM geometry — the shape that decides the cutter-location surface, kept
/// apart from <see cref="MillTool"/>'s process numbers: a ball-nose (corner radius = the tool
/// radius), a flat end mill (corner radius 0), or a bull-nose between the two. The bottom
/// profile's height above the tip at horizontal offset ρ is <c>0</c> on the flat disc
/// (ρ ≤ R − r), <c>r − √(r² − (ρ − (R − r))²)</c> on the corner torus, and the vertical flank
/// beyond — one function all three kinds share, with the ball and the flat as its endpoints.
/// </summary>
public sealed record MillCutter(double Diameter, double CornerRadius)
{
    /// <summary>The tool radius (mm).</summary>
    public double Radius => Diameter / 2;

    /// <summary>The flat bottom disc's radius, R − r (0 for a ball-nose).</summary>
    public double DiscRadius => Radius - CornerRadius;

    /// <summary>True when the corner radius IS the tool radius — the cutter whose location
    /// surface is the field's own r-offset, so it rides the exact implicit route.</summary>
    public bool IsBallNose => CornerRadius == Radius;

    /// <summary>A ball-nose cutter (corner radius = tool radius).</summary>
    public static MillCutter BallNose(double diameter) => new(diameter, diameter / 2);

    /// <summary>A flat end mill (sharp corner).</summary>
    public static MillCutter FlatEnd(double diameter) => new(diameter, 0);

    /// <summary>A bull-nose (corner-radius) cutter; the corner must be strictly between a
    /// flat end and a ball, or it IS one of those and should say so.</summary>
    public static MillCutter BullNose(double diameter, double cornerRadius)
    {
        if (!(cornerRadius > 0) || !(cornerRadius < diameter / 2))
            throw new ArgumentException(
                $"A bull-nose corner radius must lie strictly in (0, R); got {cornerRadius:0.###} "
                + $"against R = {diameter / 2:0.###}. Use FlatEnd or BallNose at the endpoints.");
        return new MillCutter(diameter, cornerRadius);
    }

    /// <summary>Refuses an unusable cutter by name.</summary>
    public void Validate()
    {
        if (!(Diameter > 0) || !double.IsFinite(Diameter))
            throw new ArgumentException($"Diameter must be finite and positive; got {Diameter:0.###}.");
        if (!(CornerRadius >= 0) || CornerRadius > Radius)
            throw new ArgumentException(
                $"CornerRadius must lie in [0, R]; got {CornerRadius:0.###} against R = {Radius:0.###}.");
    }

    /// <summary>The bottom profile: the cutting surface's height above the TIP at horizontal
    /// offset ρ from the axis — 0 on the flat disc, the torus rise on the corner,
    /// +∞ past the flank (no bottom contact there).</summary>
    internal double Profile(double rho)
    {
        double a = DiscRadius;
        if (rho <= a)
            return 0;
        if (rho <= Radius)
        {
            double s = rho - a;
            return CornerRadius - Math.Sqrt(CornerRadius * CornerRadius - s * s);
        }
        return double.PositiveInfinity;
    }
}

/// <summary>
/// The mesh drop-cutter — flat and bull-nose raster finishing, and the place the FILED
/// prediction was overturned: "the rounded-cone distance the SDF vocabulary already spells"
/// does not survive contact with the arithmetic, because a flat-bottomed tool's cutter-location
/// condition is a MIN OVER ITS DISC of the field, and certifying a minimum to ε through a
/// 1-Lipschitz oracle is Ω((a/ε)²) evaluations wherever the field is horizontally flat — which
/// is not the hostile case but the COMMON one (every plateau the flat cutter exists to finish).
/// The ball is special precisely because its disc is a point. So flat and bull-nose ride the
/// TESSELLATION with per-mode contact arithmetic — the textbook drop-cutter — and the fidelity
/// statement is the mesh's own (the <c>Remeshed</c> honesty: faithful to the mesh it ran on,
/// carrying its chord error), while the ball keeps the exact implicit route.
///
/// <para><b>Contact is the max over three modes</b>, each against the tool's bottom profile
/// f(ρ): a VERTEX lifts the tip to <c>v.z − f(ρ_v)</c>; an EDGE's contact maximizes
/// <c>e(t).z − f(ρ(t))</c> over the segment — refined numerically on a bracketed 1D scan,
/// because a torus–line tangency is a quartic (the sharp-corner fillet lesson) and the scan is
/// the honest spelling; a FACE's contact is CLOSED FORM — the bottom's slope matches the
/// plane's at <c>ρ* = a + r·s/√(1+s²)</c>, rise <c>r − r/√(1+s²)</c>, taken only when the
/// contact point lands inside the triangle (else the rim modes govern). A vertex under the
/// flat disc reads f = 0 exactly, which is what makes the flat-spot oracle an equality rather
/// than a band.</para>
/// </summary>
internal static class DropCutter
{
    /// <summary>Raster finishing over the tessellation: the same serpentine grid-anchored
    /// rows as the implicit route, each sample's tip the max contact height over the nearby
    /// triangles (a 2D bucket grid at one tool radius). A sample over no triangle clamps to
    /// the part's own bottom.</summary>
    public static MillOperation Raster(
        Shape shape, MillTool tool, MillCutter cutter, double? sampleStep, string name,
        double angleDegrees = 0)
    {
        var mesh = shape.ToMesh().Triangulated();
        var (positions, faces) = mesh.ToIndexed();
        var bounds = shape.Bounds();
        double floor = bounds.Min.Z;
        double reach = cutter.Radius;

        // Bucket triangles by XY bounding box on a tool-radius grid; a query collects the
        // cells the tool's footprint overlaps.
        double cell = Math.Max(reach, 1e-6);
        var buckets = new Dictionary<(int, int), List<int>>();
        for (int f = 0; f < faces.Count; f++)
        {
            var tri = faces[f];
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            foreach (int i in tri)
            {
                var p = positions[i];
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
            for (int gx = (int)Math.Floor(minX / cell); gx <= (int)Math.Floor(maxX / cell); gx++)
                for (int gy = (int)Math.Floor(minY / cell); gy <= (int)Math.Floor(maxY / cell); gy++)
                {
                    if (!buckets.TryGetValue((gx, gy), out var list))
                        buckets[(gx, gy)] = list = [];
                    list.Add(f);
                }
        }

        var seen = new int[faces.Count];
        int stamp = 0;
        double TipAt(double x, double y)
        {
            double best = floor;
            stamp++;
            for (int gx = (int)Math.Floor((x - reach) / cell); gx <= (int)Math.Floor((x + reach) / cell); gx++)
                for (int gy = (int)Math.Floor((y - reach) / cell); gy <= (int)Math.Floor((y + reach) / cell); gy++)
                {
                    if (!buckets.TryGetValue((gx, gy), out var list))
                        continue;
                    foreach (int f in list)
                    {
                        if (seen[f] == stamp)
                            continue;
                        seen[f] = stamp;
                        var tri = faces[f];
                        double z = TriangleDrop(
                            positions[tri[0]], positions[tri[1]], positions[tri[2]],
                            x, y, cutter);
                        if (z > best)
                            best = z;
                    }
                }
            return best;
        }

        return CncSurfacing.SerpentineRaster(tool, bounds, sampleStep, name, TipAt,
            angleDegrees);
    }

    /// <summary>The tip height at which the cutter, axis at (x, y), first touches this
    /// triangle from above — the max over the vertex, edge and face contact modes, or −∞ when
    /// the triangle is out of the tool's reach.</summary>
    internal static double TriangleDrop(
        in Vector3d va, in Vector3d vb, in Vector3d vc, double x, double y, MillCutter cutter)
    {
        double best = double.NegativeInfinity;
        double reach = cutter.Radius;

        // Vertex mode: the vertex on the bottom profile.
        Span<Vector3d> v = [va, vb, vc];
        foreach (var p in v)
        {
            double rho = Hypot(p.X - x, p.Y - y);
            if (rho <= reach)
                best = Math.Max(best, p.Z - cutter.Profile(rho));
        }

        // Edge mode: maximize e(t).z − f(ρ(t)) over each segment. A torus–line tangency is a
        // quartic, so the honest spelling is a bracketed scan (the closest-approach parameter
        // seeded alongside the uniform samples) refined by golden section.
        best = Math.Max(best, EdgeDrop(va, vb, x, y, cutter));
        best = Math.Max(best, EdgeDrop(vb, vc, x, y, cutter));
        best = Math.Max(best, EdgeDrop(vc, va, x, y, cutter));

        // Face mode: closed form — the bottom's slope matches the plane's at ρ*, and the
        // contact counts only when it lands inside the triangle (else the rim modes govern).
        var n = (vb - va).Cross(vc - va);
        if (n.Z > 1e-12 * n.Length)
        {
            double gx = -n.X / n.Z, gy = -n.Y / n.Z;      // the plane's uphill gradient
            double s = Hypot(gx, gy);
            double invSec = 1 / Math.Sqrt(1 + s * s);
            double rhoStar = cutter.DiscRadius + cutter.CornerRadius * s * invSec;
            double rise = cutter.CornerRadius * (1 - invSec);
            double cx = x, cy = y;
            if (s > 0)
            {
                cx += rhoStar * gx / s;
                cy += rhoStar * gy / s;
            }
            if (InTriangleXy(va, vb, vc, cx, cy))
            {
                double planeZ = va.Z + gx * (cx - va.X) + gy * (cy - va.Y);
                best = Math.Max(best, planeZ - rise);
            }
        }
        return best;
    }

    private static double EdgeDrop(
        Vector3d a, Vector3d b, double x, double y, MillCutter cutter)
    {
        double ex = b.X - a.X, ey = b.Y - a.Y;
        double len2 = ex * ex + ey * ey;
        double tNear = len2 > 0
            ? Math.Clamp(((x - a.X) * ex + (y - a.Y) * ey) / len2, 0, 1)
            : 0;

        double At(double t)
        {
            double px = a.X + t * (b.X - a.X);
            double py = a.Y + t * (b.Y - a.Y);
            double rho = Hypot(px - x, py - y);
            if (rho > cutter.Radius)
                return double.NegativeInfinity;
            return a.Z + t * (b.Z - a.Z) - cutter.Profile(rho);
        }

        // Bracketed scan: nine uniform samples plus the closest-approach parameter, then
        // golden section around the best.
        double bestT = tNear, best = At(tNear);
        for (int i = 0; i <= 8; i++)
        {
            double t = i / 8.0;
            double z = At(t);
            if (z > best)
            {
                best = z;
                bestT = t;
            }
        }
        if (double.IsNegativeInfinity(best))
            return best;
        double lo = Math.Max(0, bestT - 0.125), hi = Math.Min(1, bestT + 0.125);
        const double phi = 0.6180339887498949;
        double t1 = hi - phi * (hi - lo), t2 = lo + phi * (hi - lo);
        double f1 = At(t1), f2 = At(t2);
        for (int i = 0; i < 48; i++)
        {
            if (f1 < f2)
            {
                lo = t1; t1 = t2; f1 = f2;
                t2 = lo + phi * (hi - lo); f2 = At(t2);
            }
            else
            {
                hi = t2; t2 = t1; f2 = f1;
                t1 = hi - phi * (hi - lo); f1 = At(t1);
            }
        }
        return Math.Max(best, Math.Max(f1, f2));
    }

    private static bool InTriangleXy(
        in Vector3d a, in Vector3d b, in Vector3d c, double px, double py)
    {
        double d1 = Cross(a, b, px, py);
        double d2 = Cross(b, c, px, py);
        double d3 = Cross(c, a, px, py);
        bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNeg && hasPos);

        static double Cross(in Vector3d p, in Vector3d q, double px, double py) =>
            (q.X - p.X) * (py - p.Y) - (q.Y - p.Y) * (px - p.X);
    }

    private static double Hypot(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);
}
