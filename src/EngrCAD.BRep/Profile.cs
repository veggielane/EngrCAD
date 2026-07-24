using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// A planar closed profile for modeling operations (extrude, revolve, sweep): either a
/// single closed curve (circle, closed NURBS) or a chain of ≥ 2 open segments joined
/// end-to-start, with the last closing back to the first. The winding of the chain
/// defines <see cref="Normal"/> (right-hand rule); operations reverse the profile
/// automatically when their conventions need the opposite winding.
/// </summary>
public sealed class Profile
{
    // Chain endpoints often come from independent constructions (arc ends, projected
    // sketch points) carrying ~1e-7 error — looser than the 1e-9 weld tolerance on
    // purpose; the solid factories rebuild junction vertices exactly.
    private const double JoinTolerance = 1e-7;

    public IReadOnlyList<Curve3d> Segments { get; }
    public Vector3d Origin { get; }
    public Vector3d Normal { get; }
    public Vector3d XAxis { get; }
    public Vector3d YAxis { get; }

    public bool IsSingleClosedCurve => Segments.Count == 1;

    public Profile(IReadOnlyList<Curve3d> segments)
    {
        if (segments.Count == 0)
            throw new ArgumentException("Profile needs at least one segment.");
        if (segments.Count == 1)
        {
            if (!segments[0].IsClosed)
                throw new ArgumentException("A single-segment profile must be a closed curve.");
        }
        else
        {
            foreach (var segment in segments)
            {
                if (segment.IsClosed)
                    throw new ArgumentException("Closed curves cannot be chained with other segments.");
            }
            for (int i = 0; i < segments.Count; i++)
            {
                var end = segments[i].PointAt(segments[i].Domain.End);
                var next = segments[(i + 1) % segments.Count];
                var start = next.PointAt(next.Domain.Start);
                if (end.DistanceTo(start) > JoinTolerance)
                    throw new ArgumentException(
                        $"Profile is not a closed chain: segment {i} ends at {end} but the next starts at {start}.");
            }
        }

        Segments = segments;
        Origin = segments[0].PointAt(segments[0].Domain.Start);

        var loop = SampleLoop();
        double nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < loop.Count; i++)
        {
            var p = loop[i];
            var q = loop[(i + 1) % loop.Count];
            nx += (p.Y - q.Y) * (p.Z + q.Z);
            ny += (p.Z - q.Z) * (p.X + q.X);
            nz += (p.X - q.X) * (p.Y + q.Y);
        }
        if (!new Vector3d(nx, ny, nz).TryNormalize(Tolerance.Default, out var normal))
            throw new ArgumentException("Profile is degenerate (zero enclosed area).");
        Normal = normal;

        foreach (var p in loop)
        {
            // Planarity validation at the 1e-6 inverse-evaluation scale (sampled curve
            // points carry projection-level noise; weld-exactness is not required here).
            if (Math.Abs((p - Origin).Dot(Normal)) > 1e-6)
                throw new ArgumentException("Profile is not planar.");
        }

        var tangent = segments[0].TangentAt(segments[0].Domain.Start);
        var x = tangent - Normal * tangent.Dot(Normal);
        XAxis = x.TryNormalize(Tolerance.Default, out var xn) ? xn : Normal.ArbitraryPerpendicular(Tolerance.Default);
        YAxis = Normal.Cross(XAxis);
    }

    public Profile Reversed() =>
        new([.. Segments.Reverse().Select(s => s.Reversed())]);

    /// <summary>Polyloop approximation used for winding/area computations.</summary>
    internal List<Vector3d> SampleLoop(int samplesPerSegment = 16)
    {
        var points = new List<Vector3d>();
        foreach (var segment in Segments)
        {
            var domain = segment.Domain;
            if (IsSingleClosedCurve)
            {
                int n = Math.Max(32, samplesPerSegment);
                for (int i = 0; i < n; i++)
                    points.Add(segment.PointAt(domain.ParameterAt((double)i / n)));
            }
            else if (segment.Underlying is Line3d)
            {
                points.Add(segment.PointAt(domain.Start));
            }
            else
            {
                for (int i = 0; i < samplesPerSegment; i++)
                    points.Add(segment.PointAt(domain.ParameterAt((double)i / samplesPerSegment)));
            }
        }
        return points;
    }

    /// <summary>Polygonal profile through the given corner points (closed automatically).</summary>
    public static Profile FromPoints(IReadOnlyList<Vector3d> corners)
    {
        if (corners.Count < 3)
            throw new ArgumentException("A polygonal profile needs at least 3 corners.");
        var segments = new List<Curve3d>(corners.Count);
        for (int i = 0; i < corners.Count; i++)
            segments.Add(new Line3d(corners[i], corners[(i + 1) % corners.Count]));
        return new Profile(segments);
    }

    public static Profile Circle(in Vector3d center, in Vector3d xDirection, in Vector3d yDirection, double radius) =>
        new([new Circle3d(center, xDirection, yDirection, radius)]);
}
