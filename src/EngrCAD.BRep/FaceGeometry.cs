using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// Parameter-space geometry on faces: pulling 3D curves back into a surface's (u, v)
/// space and classifying points against a face's loops. These are the building blocks
/// face splitting and (future) B-Rep booleans operate on.
/// </summary>
public static class FaceGeometry
{
    /// <summary>The u-period of surfaces that close on themselves in u; 0 when aperiodic.</summary>
    public static double PeriodU(Surface surface) => surface switch
    {
        CylinderSurface or SphereSurface => 2 * Math.PI,
        RevolvedSurface r when r.IsFullTurn => 2 * Math.PI,
        ExtrudedSurface e when e.Generator.IsClosed => e.DomainU.Length,
        SweptSurface s when s.Generator.IsClosed => s.DomainU.Length,
        _ => 0,
    };

    /// <summary>
    /// Samples a 3D curve lying on a surface and pulls it into parameter space, unwrapping
    /// the periodic u direction so the polyline is continuous (u may leave the primary
    /// period). Throws when a sample does not lie on the surface.
    /// </summary>
    public static List<Vector2d> PullCurve(Curve3d curve, Surface surface, int samples = 64)
    {
        var result = new List<Vector2d>(samples + 1);
        double period = PeriodU(surface);
        int count = curve.IsClosed ? samples : samples + 1;
        for (int i = 0; i < count; i++)
        {
            var p = curve.PointAt(curve.Domain.ParameterAt((double)i / samples));
            if (!surface.TryProjectPoint(p, out var uv, 1e-6))
                throw new ArgumentException($"Curve point {p} does not lie on the surface.");
            if (period > 0 && result.Count > 0)
            {
                double previous = result[^1].X;
                double u = uv.X;
                u += period * Math.Round((previous - u) / period);
                uv = new Vector2d(u, uv.Y);
            }
            result.Add(uv);
        }
        return result;
    }

    /// <summary>Signed area of a pulled-back closed polyline; positive = counter-clockwise in (u, v).</summary>
    public static double LoopSignedArea(IReadOnlyList<Vector2d> loop)
    {
        double area = 0;
        for (int i = 0; i < loop.Count; i++)
        {
            var p = loop[i];
            var q = loop[(i + 1) % loop.Count];
            area += p.Cross(q);
        }
        return area * 0.5;
    }

    /// <summary>Pulls each loop of a face into parameter space (coedge senses respected).</summary>
    public static List<List<Vector2d>> PullLoops(BrepFace face, int samplesPerCurve = 32)
    {
        var loops = new List<List<Vector2d>>();
        double period = PeriodU(face.Surface);
        foreach (var loop in face.Loops)
        {
            var points = new List<Vector2d>();
            foreach (var coedge in loop.Coedges)
            {
                var domain = coedge.Edge.Domain;
                bool closedEdge = coedge.Edge.IsClosedEdge;
                int count = closedEdge ? samplesPerCurve : samplesPerCurve + 1;
                for (int i = 0; i < count; i++)
                {
                    double f = (double)i / samplesPerCurve;
                    if (!coedge.SameSense)
                        f = 1 - f;
                    var p = coedge.Edge.Curve.PointAt(domain.ParameterAt(f));
                    if (!face.Surface.TryProjectPoint(p, out var uv, 1e-6))
                        throw new InvalidOperationException($"Loop point {p} does not lie on the face surface.");
                    if (period > 0 && points.Count > 0)
                    {
                        double u = uv.X + period * Math.Round((points[^1].X - uv.X) / period);
                        uv = new Vector2d(u, uv.Y);
                    }
                    points.Add(uv);
                }
                if (!closedEdge && points.Count > 0)
                    points.RemoveAt(points.Count - 1); // junction shared with the next coedge
            }
            loops.Add(points);
        }
        return loops;
    }

    /// <summary>
    /// Whether a 3D point lies within a face (inside the outer loop, outside the holes),
    /// by parity of an upward-v ray against all pulled-back loops. Periodic u is handled
    /// by shifting each segment into the test point's period.
    /// </summary>
    public static bool Contains(BrepFace face, in Vector3d point, int samplesPerCurve = 32)
    {
        if (!face.Surface.TryProjectPoint(point, out var uv, 1e-6))
            return false;

        double period = PeriodU(face.Surface);
        int crossings = 0;
        foreach (var loop in PullLoops(face, samplesPerCurve))
        {
            for (int i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                if (period > 0)
                {
                    // The wrap-around segment connects points stored a period apart —
                    // first make the segment itself compact, then shift it into the test
                    // point's period.
                    b = new Vector2d(b.X + period * Math.Round((a.X - b.X) / period), b.Y);
                    double mid = (a.X + b.X) / 2;
                    double shift = period * Math.Round((uv.X - mid) / period);
                    a = new Vector2d(a.X + shift, a.Y);
                    b = new Vector2d(b.X + shift, b.Y);
                }
                bool straddles = a.X <= uv.X != b.X <= uv.X;
                if (!straddles)
                    continue;
                double t = (uv.X - a.X) / (b.X - a.X);
                if (a.Y + t * (b.Y - a.Y) > uv.Y)
                    crossings++;
            }
        }
        return (crossings & 1) == 1;
    }
}
