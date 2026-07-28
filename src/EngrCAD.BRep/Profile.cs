using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

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
    // sketch points), so joining them is the ladder's seam tier — looser than the 1e-9
    // weld tolerance on purpose; the solid factories rebuild junction vertices exactly.
    private const double JoinTolerance = FaceGeometry.SeamTolerance;

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

    /// <summary>
    /// A 2D <see cref="Region2d"/> placed on a plane, as the (outer, holes) pair the
    /// modeling operations take: <c>SolidFactory.Extrude(outer, direction, holes)</c>,
    /// <c>Revolve</c>, <c>Sweep</c>. The region's 2D x/y become <paramref name="plane"/>'s
    /// X/Y (default: the world XY plane).
    ///
    /// <para>Regions are polygonal, so the profiles are polygonal — a region produced by 2D
    /// booleans from curved sketch input carries that flattening (see
    /// <c>Sketch.ToRegions</c>); a sketch handed straight to a modeling operation keeps its
    /// exact arcs and NURBS instead.</para>
    /// </summary>
    public static (Profile Outer, IReadOnlyList<Profile> Holes) FromRegion(Region2d region, Frame3d? plane = null)
    {
        ArgumentNullException.ThrowIfNull(region);
        var frame = plane ?? Frame3d.WorldXY;
        // No simplicity re-check: Region2d validated every loop when it was constructed.
        return (
            Place(region.Outer, frame),
            [.. region.Holes.Select(hole => Place(hole, frame))]);
    }

    /// <summary>One closed 2D loop as a polygonal profile on a plane (the region's 2D x/y
    /// become the frame's X/Y). The loop closes implicitly — do not repeat the first point.
    ///
    /// <para>The loop must be SIMPLE. A profile is the boundary of a face, and a boundary
    /// that crosses itself has no interior to extrude or revolve — the solid factories would
    /// build a self-overlapping shell that still passes <c>Validate()</c>, so the refusal
    /// belongs here (<see cref="Region2dValidation"/>). Loops arriving via
    /// <see cref="FromRegion"/> were already checked when the region was built.</para></summary>
    public static Profile FromLoop(IReadOnlyList<Vector2d> loop, in Frame3d plane)
    {
        ArgumentNullException.ThrowIfNull(loop);
        Region2dValidation.Require([loop], _ => "The profile loop", nameof(loop));
        return Place(loop, plane);
    }

    private static Profile Place(IReadOnlyList<Vector2d> loop, in Frame3d plane)
    {
        var corners = new Vector3d[loop.Count];
        for (int i = 0; i < loop.Count; i++)
            corners[i] = plane.ToWorld(new Vector3d(loop[i].X, loop[i].Y, 0));
        return FromPoints(corners);
    }
}
