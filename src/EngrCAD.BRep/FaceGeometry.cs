using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// Parameter-space geometry on faces: pulling 3D curves back into a surface's (u, v)
/// space and classifying points against a face's loops. These are the building blocks
/// face splitting and (future) B-Rep booleans operate on.
/// </summary>
public static class FaceGeometry
{
    /// <summary>
    /// Distance tolerance for inverse evaluation (<see cref="Surface.TryProjectPoint"/>)
    /// when pulling on-surface points back to parameters. Deliberately three decades
    /// looser than the 1e-9 weld tolerance: Gauss-Newton projection on curved surfaces
    /// carries ~1e-7 residual, and tracer polylines are on-surface only at their vertices
    /// (see the numerical notes in CLAUDE.md). Do not tighten toward the weld tolerance.
    /// </summary>
    public const double InverseEvaluationTolerance = 1e-6;

    /// <summary>The u-period of surfaces that close on themselves in u; 0 when aperiodic.</summary>
    public static double PeriodU(Surface surface) => surface switch
    {
        CylinderSurface or SphereSurface => 2 * Math.PI,
        RevolvedSurface r when r.IsFullTurn => 2 * Math.PI,
        ExtrudedSurface e when e.Generator.IsClosed => e.DomainU.Length,
        SweptSurface s when s.Generator.IsClosed => s.DomainU.Length,
        LoftedSurface l when l.IsClosedU => l.DomainU.Length,
        _ => 0,
    };

    /// <summary>
    /// Samples a 3D curve lying on a surface and pulls it into parameter space, unwrapping
    /// the periodic u direction so the polyline is continuous (u may leave the primary
    /// period). Throws when a sample does not lie on the surface. Marching-tracer
    /// polylines (<see cref="PolylineCurve3d"/>) lie on the surface only at their
    /// vertices (chordal between), so they are sampled at exactly those.
    /// </summary>
    public static List<Vector2d> PullCurve(Curve3d curve, Surface surface, int samples = 64)
    {
        List<Vector3d> samplePoints;
        if (curve is PolylineCurve3d polyline)
        {
            var vertices = polyline.Points;
            int vertexCount = polyline.IsClosed ? vertices.Count - 1 : vertices.Count;
            samplePoints = [.. vertices.Take(vertexCount)];
        }
        else
        {
            int count = curve.IsClosed ? samples : samples + 1;
            samplePoints = new List<Vector3d>(count);
            for (int i = 0; i < count; i++)
                samplePoints.Add(curve.PointAt(curve.Domain.ParameterAt((double)i / samples)));
        }

        var result = new List<Vector2d>(samplePoints.Count);
        double period = PeriodU(surface);
        foreach (var p in samplePoints)
        {
            if (!surface.TryProjectPoint(p, out var uv, InverseEvaluationTolerance))
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

    /// <summary>
    /// Curve parameters (ascending, spanning [start, end] inclusive) at which the curve
    /// evaluates exactly on its carrier surface. Marching-tracer polylines are exact only
    /// at their VERTICES — between them the polyline is chordal, a sagitta off the
    /// surface, so inverse evaluation rejects mid-chord samples — hence polyline-backed
    /// curves (raw, or wrapped in a reparameterizing <see cref="CurveSegment"/>) sample
    /// at vertex parameters; everything else samples uniformly.
    /// </summary>
    internal static List<double> ExactSampleParameters(Curve3d curve, double start, double end, int uniformSamples)
    {
        var result = new List<double> { start };
        double interiorEpsilon = Math.Max(1e-12, (end - start) * 1e-9);
        void AddInterior(double t)
        {
            if (t > start + interiorEpsilon && t < end - interiorEpsilon)
                result.Add(t);
        }
        switch (curve)
        {
            case PolylineCurve3d polyline:
                foreach (double t in polyline.VertexParameters)
                    AddInterior(t);
                break;
            case CurveSegment { Base: PolylineCurve3d basePolyline } segment:
            {
                // Segment parameter t maps to base parameter s = s0 + (s1 − s0)·t, with
                // s wrapping past a closed base's domain end (CurveSegment wraps too).
                double s0 = segment.BaseStart;
                double length = segment.BaseEnd - segment.BaseStart;
                double baseLength = basePolyline.Domain.Length;
                foreach (double s in basePolyline.VertexParameters)
                {
                    AddInterior((s - s0) / length);
                    if (basePolyline.IsClosed)
                        AddInterior((s + baseLength - s0) / length);
                }
                result.Sort();
                break;
            }
            default:
                for (int i = 1; i < uniformSamples; i++)
                    result.Add(start + (end - start) * i / uniformSamples);
                break;
        }
        result.Add(end);

        // Near-duplicate parameters (a vertex within epsilon of an endpoint) would make
        // degenerate sampling segments downstream.
        for (int i = result.Count - 1; i > 0; i--)
        {
            if (result[i] - result[i - 1] < interiorEpsilon)
                result.RemoveAt(i == result.Count - 1 ? i - 1 : i);
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
                var parameters = ExactSampleParameters(coedge.Edge.Curve, domain.Start, domain.End, samplesPerCurve);
                // The traversal-final sample is skipped: for open edges it is the
                // junction shared with the next coedge, for closed edges the duplicate
                // of the traversal start.
                for (int i = 0; i < parameters.Count - 1; i++)
                {
                    double t = coedge.SameSense ? parameters[i] : parameters[parameters.Count - 1 - i];
                    var p = coedge.Edge.Curve.PointAt(t);
                    if (!face.Surface.TryProjectPoint(p, out var uv, InverseEvaluationTolerance))
                        throw new InvalidOperationException($"Loop point {p} does not lie on the face surface.");
                    if (period > 0 && points.Count > 0)
                    {
                        double u = uv.X + period * Math.Round((points[^1].X - uv.X) / period);
                        uv = new Vector2d(u, uv.Y);
                    }
                    points.Add(uv);
                }
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
        if (!face.Surface.TryProjectPoint(point, out var uv, InverseEvaluationTolerance))
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
