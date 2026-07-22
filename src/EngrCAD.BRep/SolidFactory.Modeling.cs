using EngrCAD.Core;

namespace EngrCAD.BRep;

// Modeling operations: extrude, revolve, sweep. All three share the same construction
// shape — side faces from the profile segments plus rail edges at junctions, with caps
// where the solid has open ends — differing only in how the "top" copy of the profile
// and the rails are generated.

public static partial class SolidFactory
{
    /// <summary>
    /// Extrudes a planar profile along <paramref name="direction"/> (length = distance;
    /// shear extrusions where the direction is not normal to the profile are allowed).
    /// </summary>
    public static BrepSolid Extrude(Profile profile, in Vector3d direction)
    {
        if (direction.IsZero(Tolerance.Default))
            throw new ArgumentException("Extrude direction must be non-zero.", nameof(direction));
        double alignment = profile.Normal.Dot(direction);
        if (Math.Abs(alignment) < 1e-9 * direction.Length)
            throw new ArgumentException("Extrude direction lies in the profile plane.", nameof(direction));
        if (alignment < 0)
            profile = profile.Reversed();

        var translation = Matrix4d.CreateTranslation(direction);
        var dir = direction;

        return BuildSweptSolid(
            profile,
            makeSideSurface: segment => new ExtrudedSurface(segment, dir),
            topTransform: translation,
            makeRailCurve: (bottom, top) => new Line3d(bottom, top),
            railDomain: Interval.Unit,
            bottomCap: new PlaneSurface(profile.Origin, profile.YAxis, profile.XAxis),
            topCap: new PlaneSurface(profile.Origin + direction, profile.XAxis, profile.YAxis));
    }

    /// <summary>
    /// Revolves a planar profile a full turn about the axis. The profile plane must
    /// contain the axis and the profile must lie strictly on one side of it. The profile
    /// needs at least two segments (a torus-like solid gets one face per segment).
    /// </summary>
    public static BrepSolid Revolve(Profile profile, in Vector3d axisOrigin, in Vector3d axisDirection)
    {
        var axis = axisDirection.Normalized();
        if (Math.Abs(axis.Dot(profile.Normal)) > 1e-6)
            throw new ArgumentException("The revolve axis must lie in the profile plane.");
        if (Math.Abs((axisOrigin - profile.Origin).Dot(profile.Normal)) > 1e-6)
            throw new ArgumentException("The revolve axis must lie in the profile plane.");
        if (profile.IsSingleClosedCurve)
            throw new NotSupportedException(
                "Revolving a single closed curve needs a face with no edges; split the profile into two or more segments.");

        // Radial direction of the half-plane, and one-sidedness check.
        var samples = profile.SampleLoop();
        var origin = axisOrigin;
        Vector3d Radial(in Vector3d p)
        {
            var d = p - origin;
            return d - axis * d.Dot(axis);
        }
        var radialDir = Radial(samples.MaxBy(p => Radial(p).LengthSquared)).Normalized();
        foreach (var p in samples)
        {
            if ((p - origin).Dot(radialDir) < 1e-9)
                throw new ArgumentException("The profile must lie strictly on one side of the revolve axis.");
        }

        // Wind counter-clockwise in (radius, height) coordinates so revolution surfaces
        // face outward.
        double area = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            var p = samples[i];
            var q = samples[(i + 1) % samples.Count];
            area += (p - origin).Dot(radialDir) * (q - origin).Dot(axis)
                  - (q - origin).Dot(radialDir) * (p - origin).Dot(axis);
        }
        if (area < 0)
            profile = profile.Reversed();

        int n = profile.Segments.Count;
        var circleY = axis.Cross(radialDir);
        var junctionEdges = new BrepEdge[n];
        var fullTurn = new Interval(0, 2 * Math.PI);
        for (int i = 0; i < n; i++)
        {
            var q = profile.Segments[i].PointAt(profile.Segments[i].Domain.Start);
            var center = origin + axis * (q - origin).Dot(axis);
            double radius = (q - origin).Dot(radialDir);
            var seam = new BrepVertex(q);
            junctionEdges[i] = new BrepEdge(new Circle3d(center, radialDir, circleY, radius), fullTurn, seam, seam);
        }

        var faces = new List<BrepFace>(n);
        for (int i = 0; i < n; i++)
        {
            faces.Add(new BrepFace(
                new RevolvedSurface(profile.Segments[i], origin, axis),
                [
                    new BrepLoop([new BrepCoedge(junctionEdges[i], sameSense: true)]),
                    new BrepLoop([new BrepCoedge(junctionEdges[(i + 1) % n], sameSense: false)]),
                ]));
        }
        return new BrepSolid([new BrepShell(faces)]);
    }

    /// <summary>
    /// Sweeps a planar profile along an open path using rotation-minimizing frames.
    /// The profile must lie in the plane through the path's start point, perpendicular to
    /// its start tangent.
    /// </summary>
    public static BrepSolid Sweep(Profile profile, Curve3d path)
    {
        if (path.IsClosed)
            throw new NotSupportedException("Closed sweep paths are not supported yet.");
        var start = path.PointAt(path.Domain.Start);
        var startTangent = path.TangentAt(path.Domain.Start);
        if (!profile.Normal.IsParallelTo(startTangent, new Tolerance(1e-9, 1e-6)))
            throw new ArgumentException("The profile plane must be perpendicular to the path's start tangent.");
        if (Math.Abs((profile.Origin - start).Dot(startTangent)) > 1e-6)
            throw new ArgumentException("The profile must lie in the plane through the path's start point.");
        if (profile.Normal.Dot(startTangent) < 0)
            profile = profile.Reversed();

        // One frame master defines rails and the end transform; per-segment surfaces are
        // built with the same (path, startX), so their frames agree exactly.
        var master = new SweptSurface(profile.Segments[0], path, profile.XAxis);
        var (frameOrigin, frameX, frameY) = master.FrameAt(path.Domain.Start);
        var endTransform = master.TransformTo(path.Domain.End);
        var (endOrigin, endX, endY) = master.FrameAt(path.Domain.End);

        Vector2d LocalOffset(in Vector3d p)
        {
            var d = p - frameOrigin;
            return new Vector2d(d.Dot(frameX), d.Dot(frameY));
        }

        return BuildSweptSolid(
            profile,
            makeSideSurface: segment => new SweptSurface(segment, path, profile.XAxis),
            topTransform: endTransform,
            makeRailCurve: (bottom, _) => new SweptRailCurve(master, LocalOffset(bottom)),
            railDomain: path.Domain,
            bottomCap: new PlaneSurface(frameOrigin, frameY, frameX),
            topCap: new PlaneSurface(endOrigin, endX, endY));
    }

    /// <summary>
    /// Shared topology construction for extrude and sweep: N side faces, rails at
    /// junctions, and two caps. The profile is already wound counter-clockwise about the
    /// travel direction.
    /// </summary>
    private static BrepSolid BuildSweptSolid(
        Profile profile,
        Func<Curve3d, Surface> makeSideSurface,
        in Matrix4d topTransform,
        Func<Vector3d, Vector3d, Curve3d> makeRailCurve,
        in Interval railDomain,
        PlaneSurface bottomCap,
        PlaneSurface topCap)
    {
        var faces = new List<BrepFace>();

        if (profile.IsSingleClosedCurve)
        {
            var generator = profile.Segments[0];
            var domain = generator.Domain;
            var seamBottom = new BrepVertex(generator.PointAt(domain.Start));
            var seamTop = new BrepVertex(topTransform.TransformPoint(seamBottom.Position));
            var bottomEdge = new BrepEdge(generator, domain, seamBottom, seamBottom);
            var topEdge = new BrepEdge(generator.Transformed(topTransform), domain, seamTop, seamTop);

            faces.Add(new BrepFace(makeSideSurface(generator),
            [
                new BrepLoop([new BrepCoedge(bottomEdge, sameSense: true)]),
                new BrepLoop([new BrepCoedge(topEdge, sameSense: false)]),
            ]));
            faces.Add(new BrepFace(bottomCap, [new BrepLoop([new BrepCoedge(bottomEdge, sameSense: false)])]));
            faces.Add(new BrepFace(topCap, [new BrepLoop([new BrepCoedge(topEdge, sameSense: true)])]));
            return new BrepSolid([new BrepShell(faces)]);
        }

        int n = profile.Segments.Count;
        var bottomVertices = new BrepVertex[n];
        var topVertices = new BrepVertex[n];
        for (int i = 0; i < n; i++)
        {
            var q = profile.Segments[i].PointAt(profile.Segments[i].Domain.Start);
            bottomVertices[i] = new BrepVertex(q);
            topVertices[i] = new BrepVertex(topTransform.TransformPoint(q));
        }

        var bottomEdges = new BrepEdge[n];
        var topEdges = new BrepEdge[n];
        var railEdges = new BrepEdge[n];
        for (int i = 0; i < n; i++)
        {
            var segment = profile.Segments[i];
            int next = (i + 1) % n;
            bottomEdges[i] = new BrepEdge(segment, segment.Domain, bottomVertices[i], bottomVertices[next]);
            topEdges[i] = new BrepEdge(segment.Transformed(topTransform), segment.Domain, topVertices[i], topVertices[next]);
            railEdges[i] = new BrepEdge(
                makeRailCurve(bottomVertices[i].Position, topVertices[i].Position),
                railDomain, bottomVertices[i], topVertices[i]);
        }

        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            faces.Add(new BrepFace(makeSideSurface(profile.Segments[i]),
            [
                new BrepLoop(
                [
                    new BrepCoedge(bottomEdges[i], sameSense: true),
                    new BrepCoedge(railEdges[next], sameSense: true),
                    new BrepCoedge(topEdges[i], sameSense: false),
                    new BrepCoedge(railEdges[i], sameSense: false),
                ]),
            ]));
        }

        var bottomLoop = new List<BrepCoedge>(n);
        for (int i = n - 1; i >= 0; i--)
            bottomLoop.Add(new BrepCoedge(bottomEdges[i], sameSense: false));
        faces.Add(new BrepFace(bottomCap, [new BrepLoop(bottomLoop)]));

        var topLoop = new List<BrepCoedge>(n);
        for (int i = 0; i < n; i++)
            topLoop.Add(new BrepCoedge(topEdges[i], sameSense: true));
        faces.Add(new BrepFace(topCap, [new BrepLoop(topLoop)]));

        return new BrepSolid([new BrepShell(faces)]);
    }
}
