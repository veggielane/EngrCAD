using EngrCAD.Core;

namespace EngrCAD.BRep;

// Modeling operations: extrude, revolve, sweep — with optional through-holes. All share
// the same construction shape: side faces per profile segment plus rail edges at
// junctions for every boundary profile (outer and holes), with multi-loop caps where the
// solid has open ends.

public static partial class SolidFactory
{
    /// <summary>
    /// Extrudes a planar profile along <paramref name="direction"/> (length = distance;
    /// shear extrusions where the direction is not normal to the profile are allowed).
    /// Optional hole profiles must be coplanar with the outer profile and inside it.
    /// </summary>
    public static BrepSolid Extrude(Profile profile, in Vector3d direction, IReadOnlyList<Profile>? holes = null)
    {
        if (direction.IsZero(Tolerance.Default))
            throw new ArgumentException("Extrude direction must be non-zero.", nameof(direction));
        double alignment = profile.Normal.Dot(direction);
        if (Math.Abs(alignment) < 1e-9 * direction.Length)
            throw new ArgumentException("Extrude direction lies in the profile plane.", nameof(direction));
        if (alignment < 0)
            profile = profile.Reversed();

        var profiles = new List<Profile> { profile };
        foreach (var hole in holes ?? [])
        {
            ValidateCoplanarHole(profile, hole);
            // Holes bound material on their outside: wind them opposite the outer profile.
            profiles.Add(hole.Normal.Dot(direction) > 0 ? hole.Reversed() : hole);
        }

        var translation = Matrix4d.CreateTranslation(direction);
        var dir = direction;

        return BuildSweptSolid(
            profiles,
            makeSideSurface: segment => new ExtrudedSurface(segment, dir),
            topTransform: translation,
            makeRailCurve: (bottom, top) => new Line3d(bottom, top),
            railDomain: Interval.Unit,
            bottomCap: new PlaneSurface(profile.Origin, profile.YAxis, profile.XAxis),
            topCap: new PlaneSurface(profile.Origin + direction, profile.XAxis, profile.YAxis));
    }

    /// <summary>
    /// Extrudes a planar profile with a TWIST and/or a per-axis taper — OpenSCAD's
    /// <c>linear_extrude(twist, scale)</c> as exact B-Rep geometry. Each side face is one
    /// <see cref="TwistedSurface"/> over its own profile segment; the two caps are planes.
    ///
    /// <para><paramref name="axis"/> states the twist axis and the scaling centre (its Z
    /// is the extrude direction, its X/Y the axes <paramref name="scale"/> is measured
    /// on) — a sketch plane, in the modelling layer. It is a parameter rather than
    /// something derived from the profile because <see cref="Profile.Origin"/> is an
    /// arbitrary point ON the boundary, so deriving it would twist about a corner.</para>
    ///
    /// <para>A zero twist is accepted and produces a ruled taper; the <c>Shape</c> layer
    /// routes that case to <see cref="Loft"/> instead, which reaches the same geometry
    /// through a two-section loft (both are exact — the taper is affine in v either
    /// way).</para>
    /// </summary>
    /// <param name="profile">The base section; wound automatically for outward normals.</param>
    /// <param name="axis">Twist axis and scaling centre (Z = extrude direction).</param>
    /// <param name="height">Rise along the axis Z (&gt; 0).</param>
    /// <param name="twist">Total twist over the height, radians, right-handed about Z.</param>
    /// <param name="scale">Per-axis scale of the top section (components &gt; 0).</param>
    /// <param name="holes">Coplanar hole profiles inside the outer one; each hole twists
    /// and scales about the SAME axis, so it stays a hole at every height.</param>
    public static BrepSolid TwistExtrude(
        Profile profile, in Frame3d axis, double height, double twist, Vector2d scale,
        IReadOnlyList<Profile>? holes = null)
    {
        if (!(height > 0) || !double.IsFinite(height))
            throw new ArgumentOutOfRangeException(nameof(height), height,
                "A twisted extrusion needs a positive finite height.");
        var direction = axis.Z;
        double alignment = profile.Normal.Dot(direction);
        if (Math.Abs(alignment) < 1e-9)
            throw new ArgumentException(
                "The extrude axis lies in the profile plane; a twisted extrusion sweeps along the axis.",
                nameof(axis));
        if (alignment < 0)
            profile = profile.Reversed();

        var profiles = new List<Profile> { profile };
        foreach (var hole in holes ?? [])
        {
            ValidateCoplanarHole(profile, hole);
            // Holes bound material on their outside: wind them opposite the outer profile.
            profiles.Add(hole.Normal.Dot(direction) > 0 ? hole.Reversed() : hole);
        }

        // One master surface supplies the section transform for every rail and both caps,
        // so a rail edge and the grid columns of the two faces it separates are the same
        // arithmetic (the Sweep arrangement). It IS the first side face's surface rather
        // than a fifth copy of the same numbers: the archive and BrepSolid.Transformed key
        // geometry on REFERENCE identity, so a value-identical twin would be written twice
        // and moved twice.
        var frame = axis;
        var sideSurfaces = new Dictionary<Curve3d, TwistedSurface>(ReferenceEqualityComparer.Instance);
        TwistedSurface SideSurface(Curve3d segment)
        {
            if (!sideSurfaces.TryGetValue(segment, out var surface))
                sideSurfaces[segment] = surface = new TwistedSurface(segment, frame, height, twist, scale);
            return surface;
        }
        var master = SideSurface(profile.Segments[0]);
        var topTransform = master.TransformTo(1);
        double cos = Math.Cos(twist), sin = Math.Sin(twist);
        var topX = frame.X * cos + frame.Y * sin;
        var topY = frame.Y * cos - frame.X * sin;
        var basePoint = profile.Origin;

        return BuildSweptSolid(
            profiles,
            makeSideSurface: SideSurface,
            topTransform: topTransform,
            makeRailCurve: (bottom, _) => new TwistedRailCurve(master, frame.ToLocal(bottom)),
            railDomain: Interval.Unit,
            // Bottom cap: axes swapped so its normal points back down the axis. The origin
            // is the PROFILE's, so the plane is the section's own however far along the
            // axis it sits (Extrude's convention).
            bottomCap: new PlaneSurface(basePoint, frame.Y, frame.X),
            // Top cap: the frame's axes ROTATED by the twist (orthonormal whatever the
            // per-axis scale does, since a scale never leaves the plane).
            topCap: new PlaneSurface(topTransform.TransformPoint(basePoint), topX, topY));
    }

    /// <summary>
    /// Revolves a planar profile about an axis lying in its plane, through
    /// <paramref name="angle"/> radians (default a full turn). The profile must lie
    /// strictly on one side of the axis. Full turns produce cap-less torus-topology
    /// solids and require a multi-segment profile without holes; partial revolves get
    /// planar caps and support single-closed-curve profiles (pipe elbows) and holes.
    /// </summary>
    public static BrepSolid Revolve(
        Profile profile, in Vector3d axisOrigin, in Vector3d axisDirection,
        double angle = 2 * Math.PI, IReadOnlyList<Profile>? holes = null)
    {
        if (angle <= 1e-9 || angle > 2 * Math.PI + 1e-9)
            throw new ArgumentOutOfRangeException(nameof(angle));
        bool fullTurn = Math.Abs(angle - 2 * Math.PI) < 1e-9;

        var axis = axisDirection.Normalized();
        var origin = axisOrigin;
        if (Math.Abs(axis.Dot(profile.Normal)) > 1e-6 ||
            Math.Abs((origin - profile.Origin).Dot(profile.Normal)) > 1e-6)
            throw new ArgumentException("The revolve axis must lie in the profile plane.");

        var holeList = (holes ?? []).ToList();
        if (fullTurn && holeList.Count > 0)
            throw new NotSupportedException(
                "A full revolve of a profile with holes produces multiple shells; revolve partially or model the shells separately.");
        if (fullTurn && profile.IsSingleClosedCurve)
            throw new NotSupportedException(
                "Fully revolving a single closed curve needs a face with no edges; split the profile into two or more segments.");
        foreach (var hole in holeList)
            ValidateCoplanarHole(profile, hole);

        // Radial direction of the half-plane, and one-sidedness of everything revolved.
        var samples = profile.SampleLoop();
        var allSamples = new List<Vector3d>(samples);
        foreach (var hole in holeList)
            allSamples.AddRange(hole.SampleLoop());
        Vector3d Radial(in Vector3d p)
        {
            var d = p - origin;
            return d - axis * d.Dot(axis);
        }
        var radialDir = Radial(allSamples.MaxBy(p => Radial(p).LengthSquared)).Normalized();
        // Full turns may touch the axis: on-axis stretches revolve to nothing and are
        // dropped, their endpoints becoming poles. Partial revolves need their cap and
        // rail geometry off-axis.
        double sideLimit = fullTurn ? -1e-9 : 1e-9;
        foreach (var p in allSamples)
        {
            if ((p - origin).Dot(radialDir) < sideLimit)
                throw new ArgumentException(fullTurn
                    ? "The profile must lie on one side of the revolve axis (touching is allowed)."
                    : "The profile must lie strictly on one side of the revolve axis.");
        }

        // Counter-clockwise in (radius, height) coordinates faces outward; holes clockwise.
        double SignedRzArea(Profile p)
        {
            var loop = p.SampleLoop();
            double area = 0;
            for (int i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                area += (a - origin).Dot(radialDir) * (b - origin).Dot(axis)
                      - (b - origin).Dot(radialDir) * (a - origin).Dot(axis);
            }
            return area;
        }
        if (SignedRzArea(profile) < 0)
            profile = profile.Reversed();

        if (fullTurn)
            return RevolveFullTurn(profile, origin, axis, radialDir);

        var profiles = new List<Profile> { profile };
        foreach (var hole in holeList)
            profiles.Add(SignedRzArea(hole) > 0 ? hole.Reversed() : hole);

        var rotation = Matrix4d.CreateTranslation(origin)
                     * Matrix4d.CreateFromAxisAngle(axis, angle)
                     * Matrix4d.CreateTranslation(-origin);
        var endRadial = Quaterniond.FromAxisAngle(axis, angle).Rotate(radialDir);

        return BuildSweptSolid(
            profiles,
            makeSideSurface: segment => new RevolvedSurface(segment, origin, axis, angle),
            topTransform: rotation,
            makeRailCurve: (bottom, _) =>
            {
                var center = origin + axis * (bottom - origin).Dot(axis);
                var radiusVector = bottom - center;
                double radius = radiusVector.Length;
                var radial = radiusVector / radius;
                return new Circle3d(center, radial, axis.Cross(radial), radius);
            },
            railDomain: new Interval(0, angle),
            bottomCap: new PlaneSurface(origin, radialDir, axis),
            topCap: new PlaneSurface(origin, axis, endRadial));
    }

    private static BrepSolid RevolveFullTurn(Profile profile, in Vector3d origin, in Vector3d axis, in Vector3d radialDir)
    {
        var circleY = axis.Cross(radialDir);
        var fullTurn = new Interval(0, 2 * Math.PI);
        var axisOrigin = origin;
        var axisDir = axis;
        var radial = radialDir;
        double RadiusOf(in Vector3d p) => (p - axisOrigin).Dot(radial);

        // On-axis stretches revolve to nothing — drop them; their endpoints are poles
        // (no junction edge there, like MakeSphere's pole).
        var kept = new List<Curve3d>();
        foreach (var segment in profile.Segments)
        {
            bool onAxis = true;
            for (int i = 0; i <= 8 && onAxis; i++)
                onAxis = RadiusOf(segment.PointAt(segment.Domain.ParameterAt(i / 8.0))) <= 1e-9;
            if (!onAxis)
                kept.Add(segment);
        }
        if (kept.Count == 0)
            throw new ArgumentException("The profile lies entirely on the revolve axis.");

        // A pole-to-pole generator would make a face with no boundary edges (the
        // sphere case); split it at its midpoint so an equator junction exists.
        var generators = new List<Curve3d>();
        foreach (var segment in kept)
        {
            var domain = segment.Domain;
            bool startPole = RadiusOf(segment.PointAt(domain.Start)) <= 1e-9;
            bool endPole = RadiusOf(segment.PointAt(domain.End)) <= 1e-9;
            if (startPole && endPole)
            {
                generators.Add(new CurveSegment(segment, domain.Start, domain.Mid));
                generators.Add(new CurveSegment(segment, domain.Mid, domain.End));
            }
            else
            {
                generators.Add(segment);
            }
        }

        // Junction circle at each generator's start (null at poles). Cyclic adjacency
        // still holds after dropping axis segments: wherever two kept generators are
        // not contiguous, both meet the axis, so both sides are poles.
        int n = generators.Count;
        var junctionEdges = new BrepEdge?[n];
        for (int i = 0; i < n; i++)
        {
            var q = generators[i].PointAt(generators[i].Domain.Start);
            double radius = RadiusOf(q);
            if (radius <= 1e-9)
                continue;
            var center = origin + axis * (q - origin).Dot(axis);
            var seam = new BrepVertex(q);
            junctionEdges[i] = new BrepEdge(new Circle3d(center, radialDir, circleY, radius), fullTurn, seam, seam);
        }

        var faces = new List<BrepFace>(n);
        for (int i = 0; i < n; i++)
        {
            var loops = new List<BrepLoop>(2);
            if (junctionEdges[i] is { } startEdge)
                loops.Add(new BrepLoop([new BrepCoedge(startEdge, sameSense: true)]));
            if (junctionEdges[(i + 1) % n] is { } endEdge)
                loops.Add(new BrepLoop([new BrepCoedge(endEdge, sameSense: false)]));
            faces.Add(new BrepFace(new RevolvedSurface(generators[i], origin, axis), loops));
        }
        return new BrepSolid([new BrepShell(faces)]);
    }

    /// <summary>
    /// Sweeps a planar profile along an open path using rotation-minimizing frames.
    /// The profile (and any holes) must lie in the plane through the path's start point,
    /// perpendicular to its start tangent.
    /// </summary>
    public static BrepSolid Sweep(Profile profile, Curve3d path, IReadOnlyList<Profile>? holes = null)
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

        var profiles = new List<Profile> { profile };
        foreach (var hole in holes ?? [])
        {
            ValidateCoplanarHole(profile, hole);
            profiles.Add(hole.Normal.Dot(startTangent) > 0 ? hole.Reversed() : hole);
        }

        // One frame master defines rails and the end transform; per-segment surfaces are
        // built with the same (path, startX), so their frames agree exactly.
        var master = new SweptSurface(profile.Segments[0], path, profile.XAxis);
        var startFrame = master.Frame(path.Domain.Start);
        var endTransform = master.TransformTo(path.Domain.End);
        var endFrame = master.Frame(path.Domain.End);

        Vector2d LocalOffset(in Vector3d p)
        {
            var local = startFrame.ToLocal(p);
            return new Vector2d(local.X, local.Y);
        }

        return BuildSweptSolid(
            profiles,
            makeSideSurface: segment => new SweptSurface(segment, path, profile.XAxis),
            topTransform: endTransform,
            makeRailCurve: (bottom, _) => new SweptRailCurve(master, LocalOffset(bottom)),
            railDomain: path.Domain,
            // Bottom cap: axes swapped so the cap normal points backward along the path.
            bottomCap: new PlaneSurface(startFrame.Origin, startFrame.Y, startFrame.X),
            topCap: new PlaneSurface(endFrame.Origin, endFrame.X, endFrame.Y));
    }

    private static void ValidateCoplanarHole(Profile outer, Profile hole)
    {
        if (!hole.Normal.IsParallelTo(outer.Normal, new Tolerance(1e-9, 1e-6)) ||
            Math.Abs((hole.Origin - outer.Origin).Dot(outer.Normal)) > 1e-6)
            throw new ArgumentException("Hole profiles must be coplanar with the outer profile.");
    }

    /// <summary>
    /// Shared topology construction for extrude, sweep and partial revolve: side faces
    /// and junction rails for every boundary profile (profiles[0] = outer, wound for
    /// outward side normals; the rest are holes, wound oppositely), plus two multi-loop
    /// planar caps.
    /// </summary>
    private static BrepSolid BuildSweptSolid(
        IReadOnlyList<Profile> profiles,
        Func<Curve3d, Surface> makeSideSurface,
        in Matrix4d topTransform,
        Func<Vector3d, Vector3d, Curve3d> makeRailCurve,
        in Interval railDomain,
        PlaneSurface bottomCap,
        PlaneSurface topCap)
    {
        var faces = new List<BrepFace>();
        var bottomCapLoops = new List<BrepLoop>();
        var topCapLoops = new List<BrepLoop>();

        foreach (var profile in profiles)
        {
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
                bottomCapLoops.Add(new BrepLoop([new BrepCoedge(bottomEdge, sameSense: false)]));
                topCapLoops.Add(new BrepLoop([new BrepCoedge(topEdge, sameSense: true)]));
                continue;
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
            bottomCapLoops.Add(new BrepLoop(bottomLoop));

            var topLoop = new List<BrepCoedge>(n);
            for (int i = 0; i < n; i++)
                topLoop.Add(new BrepCoedge(topEdges[i], sameSense: true));
            topCapLoops.Add(new BrepLoop(topLoop));
        }

        faces.Add(new BrepFace(bottomCap, bottomCapLoops));
        faces.Add(new BrepFace(topCap, topCapLoops));
        return new BrepSolid([new BrepShell(faces)]);
    }
}
