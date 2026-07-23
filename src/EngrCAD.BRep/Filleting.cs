using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// Rim chamfering and filleting: the closed boundary rim of a planar face is replaced
/// by a bevel or blend band. Chamfer supports straight edges with sharp corners (they
/// miter exactly — planar trapezoid strips share miter edges) and full circular rims
/// (exact cone bands). Fillet supports full circular rims (exact quarter-torus) and
/// tangent-continuous line+arc rims (quarter-cylinder and quarter-torus-segment bands
/// sharing junction arcs — e.g. rounded-rectangle plate tops). Sharp-corner fillets
/// (ball/miter corner patches) are future work.
/// </summary>
public static class Filleting
{
    /// <summary>45° chamfer of the outer rim of a planar face.</summary>
    public static BrepSolid ChamferRim(BrepSolid solid, BrepFace face, double setback) =>
        ChamferRim(solid, face, setback, setback);

    /// <summary>Chamfers the outer rim of a planar face: the face shrinks by
    /// <paramref name="topSetback"/>, neighbors drop by <paramref name="sideSetback"/>,
    /// and planar strips (mitered at corners) or cone bands (circular rims) fill in.</summary>
    public static BrepSolid ChamferRim(BrepSolid solid, BrepFace face, double topSetback, double sideSetback)
    {
        if (topSetback <= 0 || sideSetback <= 0)
            throw new ArgumentOutOfRangeException(nameof(topSetback));
        return RimSurgeon.Apply(solid, face, topSetback, sideSetback, fillet: false);
    }

    /// <summary>
    /// Fillets the outer rim of a planar face with the given radius. The rim must be a
    /// full circle or a tangent-continuous chain of lines and arcs (round sharp sketch
    /// corners first — chamfer handles sharp corners).
    /// </summary>
    public static BrepSolid FilletRim(BrepSolid solid, BrepFace face, double radius)
    {
        if (radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius));
        return RimSurgeon.Apply(solid, face, radius, radius, fillet: true);
    }

    /// <summary>
    /// Fillets a closed circular edge between a planar cap and a cylindrical band with
    /// the given radius. The cap shrinks, the band shortens, and an exact quarter-torus
    /// (a revolved circular arc) joins them. Returns the new solid; untouched faces are
    /// reused (the input solid is consumed).
    /// </summary>
    public static BrepSolid FilletEdge(BrepSolid solid, BrepEdge edge, double radius)
    {
        if (radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius));
        if (!edge.IsClosedEdge || edge.Curve.Underlying is not Circle3d)
            throw new NotSupportedException("Only closed circular edges can be filleted yet.");
        if (edge.Uses.Count != 2)
            throw new ArgumentException("The edge must be interior to a solid.", nameof(edge));
        // Geometry from the edge itself — wrapped curves (translated extrusion tops)
        // have Underlying circles at the wrong height.
        var rim = ActualCircle(edge);

        var capUse = edge.Uses.FirstOrDefault(u => u.Loop.Face.Surface is PlaneSurface);
        var bandUse = edge.Uses.FirstOrDefault(u => u.Loop.Face.IsCylindrical(out _, out _, out _));
        if (capUse is null || bandUse is null)
            throw new NotSupportedException("Filleting expects the edge to join a planar cap and a cylindrical band.");

        var cap = (PlaneSurface)capUse.Loop.Face.Surface;
        var bandFace = bandUse.Loop.Face;
        bandFace.IsCylindrical(out _, out var bandAxis, out _);

        var axis = bandAxis.Normalized();
        var capNormal = cap.Normal.Normalized();
        if (!capNormal.IsParallelTo(axis, new Tolerance(1e-9, 1e-6)))
            throw new NotSupportedException("The cap must be perpendicular to the band's axis.");
        var outward = capNormal; // outward normal of the cap, ±axis

        double bigRadius = rim.Radius;
        if (radius >= bigRadius)
            throw new ArgumentOutOfRangeException(nameof(radius), "Fillet radius must be smaller than the rim radius.");

        // Band length check: distance to the band's far ring.
        var farLoop = bandFace.Loops.First(l => !l.Coedges.Contains(bandUse));
        if (farLoop.Coedges is not [{ Edge: { Curve.Underlying: Circle3d } farEdge }])
            throw new NotSupportedException("The band must be bounded by two circular rings.");
        var farCircle = ActualCircle(farEdge);
        double bandLength = Math.Abs((rim.Center - farCircle.Center).Dot(axis));
        if (radius >= bandLength)
            throw new ArgumentOutOfRangeException(nameof(radius), "Fillet radius exceeds the band length.");

        // Geometry: arc center sits radius inward both radially and axially.
        var radial = rim.XDirection;
        var around = axis.Cross(radial);
        var arcCircle = new Circle3d(
            rim.Center + radial * (bigRadius - radius) - outward * radius,
            radial, outward, radius);
        var arc = new CurveSegment(arcCircle, 0, Math.PI / 2); // band tangent → cap tangent

        var bandRing = new Circle3d(rim.Center - outward * radius, radial, around, bigRadius);
        var capRing = new Circle3d(rim.Center, radial, around, bigRadius - radius);
        var bandSeam = new BrepVertex(bandRing.PointAt(0));
        var capSeam = new BrepVertex(capRing.PointAt(0));
        var bandRingEdge = new BrepEdge(bandRing, new Interval(0, 2 * Math.PI), bandSeam, bandSeam);
        var capRingEdge = new BrepEdge(capRing, new Interval(0, 2 * Math.PI), capSeam, capSeam);

        // Sense bookkeeping: the new rings wind about +axis; the old rim wound about
        // ±axis. Preserve each face's traversal orientation, and give the torus the
        // opposite uses.
        int oldWinding = Math.Sign(rim.Axis.Dot(axis));
        bool bandSense = oldWinding > 0 ? bandUse.SameSense : !bandUse.SameSense;
        bool capSense = oldWinding > 0 ? capUse.SameSense : !capUse.SameSense;

        bandUse.Loop.ReplaceCoedge(bandUse, [new BrepCoedge(bandRingEdge, bandSense)]);
        capUse.Loop.ReplaceCoedge(capUse, [new BrepCoedge(capRingEdge, capSense)]);

        var torus = new BrepFace(
            new RevolvedSurface(arc, rim.Center, axis),
            [
                new BrepLoop([new BrepCoedge(bandRingEdge, !bandSense)]),
                new BrepLoop([new BrepCoedge(capRingEdge, !capSense)]),
            ]);

        // Domain-driven band surfaces (extruded/revolved) must be trimmed to the
        // shortened extent — their grids ignore the loops.
        var trimmedBand = TrimNeighborBand(bandFace, bandRing.PointAt(0));
        var faces = solid.Faces
            .Select(f => ReferenceEquals(f, bandFace) && trimmedBand is not null ? trimmedBand : f)
            .ToList();
        faces.Add(torus);
        return new BrepSolid([new BrepShell(faces)]);
    }

    /// <summary>Circle geometry recovered from an edge's actual points (circumcenter of
    /// three samples), immune to wrapper curves whose Underlying sits elsewhere. The
    /// frame starts at the edge's start point; the axis follows increasing parameter.</summary>
    private static Circle3d ActualCircle(BrepEdge edge)
    {
        var domain = edge.Domain;
        var p0 = edge.Curve.PointAt(domain.Start);
        var p1 = edge.Curve.PointAt(domain.ParameterAt(1.0 / 3));
        var p2 = edge.Curve.PointAt(domain.ParameterAt(2.0 / 3));

        var u = p1 - p0;
        var v = p2 - p0;
        var plane = u.Cross(v);
        double planeLengthSq = plane.LengthSquared;
        if (planeLengthSq < 1e-24)
            throw new NotSupportedException("Degenerate circular edge.");
        // Circumcenter: p0 + (|v|²(plane×u) + |u|²(v×plane)) / (2|plane|²).
        var center = p0 + (plane.Cross(u) * v.LengthSquared + v.Cross(plane) * u.LengthSquared) / (2 * planeLengthSq);
        double radius = center.DistanceTo(p0);
        var axis = plane.Normalized(); // winding of p0→p1→p2 = increasing parameter
        var x = (p0 - center).Normalized();
        return new Circle3d(center, x, axis.Cross(x), radius);
    }

    /// <summary>
    /// Rebuilds a neighbor band face whose surface is domain-driven in the tessellator
    /// (extruded/revolved) so its extent ends at the lowered rim. Loop-driven surfaces
    /// (planes, cylinders) return null — no trim needed.
    /// </summary>
    private static BrepFace? TrimNeighborBand(BrepFace neighbor, in Vector3d loweredRimPoint)
    {
        switch (neighbor.Surface)
        {
            case ExtrudedSurface extruded:
            {
                if (!extruded.TryProjectPoint(loweredRimPoint, out var uv, 1e-6))
                    throw new NotSupportedException("Could not locate the lowered rim on a neighbor band.");
                double vLow = uv.Y;
                Surface trimmed = vLow > 0.5
                    ? new ExtrudedSurface(extruded.Generator, extruded.Direction * vLow)
                    : new ExtrudedSurface(
                        extruded.Generator.Transformed(Matrix4d.CreateTranslation(extruded.Direction * vLow)),
                        extruded.Direction * (1 - vLow));
                return new BrepFace(trimmed, [.. neighbor.Loops], neighbor.IsReversed);
            }
            case RevolvedSurface revolved when revolved.Generator.Underlying is Line3d:
            {
                if (!revolved.TryProjectPoint(loweredRimPoint, out var uv, 1e-6))
                    throw new NotSupportedException("Could not locate the lowered rim on a neighbor band.");
                var domain = revolved.Generator.Domain;
                bool rimAtEnd = Math.Abs(uv.Y - domain.End) < Math.Abs(uv.Y - domain.Start);
                var trimmedGenerator = rimAtEnd
                    ? new CurveSegment(revolved.Generator, domain.Start, uv.Y)
                    : new CurveSegment(revolved.Generator, uv.Y, domain.End);
                return new BrepFace(
                    new RevolvedSurface(trimmedGenerator, revolved.AxisOrigin, revolved.AxisDirection, revolved.Angle),
                    [.. neighbor.Loops], neighbor.IsReversed);
            }
            default:
                return null;
        }
    }

    /// <summary>Shared machinery for rim chamfer/fillet topology surgery. All new rim
    /// edges are built in the top face's traversal direction, which fixes every sense:
    /// the shrunk top loop uses them forward, neighbors backward, and each band is
    /// [top backward, corner forward, bottom forward, next-corner backward].</summary>
    private static class RimSurgeon
    {
        public static BrepSolid Apply(BrepSolid solid, BrepFace face, double top, double side, bool fillet)
        {
            if (!face.IsPlanar(out _, out var normal))
                throw new NotSupportedException("Rim features apply to planar faces.");
            if (face.IsReversed)
                throw new NotSupportedException("Rim features on reversed faces are not supported yet.");

            var outer = OuterLoop(face);
            if (outer.Coedges is [{ Edge: { IsClosedEdge: true } rimEdge }]
                && rimEdge.Curve.Underlying is Circle3d)
            {
                return fillet
                    ? FilletEdge(solid, rimEdge, top)
                    : ChamferCircularRim(solid, face, rimEdge, top, side, normal);
            }
            return PolygonRim(solid, face, outer, top, side, fillet, normal);
        }

        private static BrepLoop OuterLoop(BrepFace face)
        {
            BrepLoop? best = null;
            double bestArea = -1;
            foreach (var loop in face.Loops)
            {
                var points = new List<Vector3d>();
                foreach (var coedge in loop.Coedges)
                {
                    var domain = coedge.Edge.Domain;
                    for (int i = 0; i < 16; i++)
                    {
                        double f = coedge.SameSense ? i / 16.0 : 1 - i / 16.0;
                        points.Add(coedge.Edge.Curve.PointAt(domain.ParameterAt(f)));
                    }
                }
                double nx = 0, ny = 0, nz = 0;
                for (int i = 0; i < points.Count; i++)
                {
                    var p = points[i];
                    var q = points[(i + 1) % points.Count];
                    nx += (p.Y - q.Y) * (p.Z + q.Z);
                    ny += (p.Z - q.Z) * (p.X + q.X);
                    nz += (p.X - q.X) * (p.Y + q.Y);
                }
                double area = new Vector3d(nx, ny, nz).Length;
                if (area > bestArea)
                {
                    bestArea = area;
                    best = loop;
                }
            }
            return best ?? throw new InvalidOperationException("Face has no loops.");
        }

        // ---- circular rims (chamfer): exact cone band ----

        private static BrepSolid ChamferCircularRim(
            BrepSolid solid, BrepFace capFace, BrepEdge rimEdge, double top, double side, in Vector3d capNormal)
        {
            var rim = ActualCircle(rimEdge); // wrapper-immune (translated extrusion tops)
            var capUse = rimEdge.Uses.First(u => ReferenceEquals(u.Loop.Face, capFace));
            var bandUse = rimEdge.Uses.First(u => !ReferenceEquals(u.Loop.Face, capFace));
            if (!bandUse.Loop.Face.IsCylindrical(out _, out var bandAxis, out _)
                || !bandAxis.IsParallelTo(capNormal, Tolerance.Default))
                throw new NotSupportedException("A circular rim's neighbor must be a coaxial cylindrical band.");
            if (top >= rim.Radius)
                throw new ArgumentOutOfRangeException(nameof(top), "Chamfer exceeds the rim radius.");

            var outward = capNormal;
            var radial = rim.XDirection;
            var around = outward.Cross(radial);

            var capRing = new Circle3d(rim.Center, radial, around, rim.Radius - top);
            var bandRing = new Circle3d(rim.Center - outward * side, radial, around, rim.Radius);
            var capSeam = new BrepVertex(capRing.PointAt(0));
            var bandSeam = new BrepVertex(bandRing.PointAt(0));
            var capRingEdge = new BrepEdge(capRing, new Interval(0, 2 * Math.PI), capSeam, capSeam);
            var bandRingEdge = new BrepEdge(bandRing, new Interval(0, 2 * Math.PI), bandSeam, bandSeam);

            int oldWinding = Math.Sign(rim.Axis.Dot(outward));
            bool capSense = oldWinding > 0 ? capUse.SameSense : !capUse.SameSense;
            bool bandSense = oldWinding > 0 ? bandUse.SameSense : !bandUse.SameSense;
            capUse.Loop.ReplaceCoedge(capUse, [new BrepCoedge(capRingEdge, capSense)]);
            bandUse.Loop.ReplaceCoedge(bandUse, [new BrepCoedge(bandRingEdge, bandSense)]);

            // Generator runs band → cap (bottom → top) so ∂u × ∂v points outward,
            // matching FilletEdge's quarter-torus convention.
            var slant = new Line3d(bandRing.PointAt(0), capRing.PointAt(0));
            var cone = new BrepFace(
                new RevolvedSurface(slant, rim.Center, outward),
                [
                    new BrepLoop([new BrepCoedge(capRingEdge, !capSense)]),
                    new BrepLoop([new BrepCoedge(bandRingEdge, !bandSense)]),
                ]);

            var bandFace = bandUse.Loop.Face;
            var trimmedBand = TrimNeighborBand(bandFace, bandRing.PointAt(0));
            var faces = solid.Faces
                .Select(f => ReferenceEquals(f, bandFace) && trimmedBand is not null ? trimmedBand : f)
                .ToList();
            faces.Add(cone);
            return new BrepSolid([new BrepShell(faces)]);
        }

        // ---- polygonal / tangent-continuous rims ----

        private sealed record RimEdgeInfo(
            BrepCoedge Use, BrepEdge Edge, BrepFace Neighbor, BrepCoedge NeighborUse,
            Vector3d Start, Vector3d End, Vector3d DownDir, Circle3d? Arc, double ArcStart, double ArcSweep);

        private static BrepSolid PolygonRim(
            BrepSolid solid, BrepFace face, BrepLoop outer,
            double top, double side, bool fillet, in Vector3d normal)
        {
            var up = normal;
            int n = outer.Coedges.Count;
            if (n < 2)
                throw new NotSupportedException("Rim features need a multi-edge rim or a full circle.");

            var edges = new List<RimEdgeInfo>(n);
            foreach (var use in outer.Coedges)
            {
                var edge = use.Edge;
                if (edge.Uses.Count != 2)
                    throw new NotSupportedException("Rim edges must be interior (two uses).");
                var neighborUse = edge.Uses.First(u => !ReferenceEquals(u, use));
                var neighbor = neighborUse.Loop.Face;
                var start = edge.Curve.PointAt(use.SameSense ? edge.Domain.Start : edge.Domain.End);
                var end = edge.Curve.PointAt(use.SameSense ? edge.Domain.End : edge.Domain.Start);

                Vector3d downDir;
                Circle3d? arc = null;
                double arcStart = 0, arcSweep = 0;
                if (edge.Curve.Underlying is Line3d)
                {
                    if (!neighbor.IsPlanar(out _, out var sideNormal))
                        throw new NotSupportedException("Straight rim edges need planar neighbor faces.");
                    if (fillet && Math.Abs(sideNormal.Dot(up)) > 1e-6)
                        throw new NotSupportedException("Fillet rims need neighbors perpendicular to the face.");
                    var raw = -up + sideNormal * up.Dot(sideNormal);
                    if (!raw.TryNormalize(Tolerance.Default, out downDir))
                        throw new NotSupportedException("A rim neighbor is parallel to the face.");
                }
                else if (edge.Curve.Underlying is Circle3d)
                {
                    if (!neighbor.IsCylindrical(out _, out var axis, out _)
                        || !axis.IsParallelTo(up, Tolerance.Default))
                        throw new NotSupportedException("Arc rim edges need coaxial cylindrical neighbor faces.");
                    downDir = -up;
                    var actual = ActualCircle(edge); // wrapper-immune geometry
                    arc = actual;
                    // start/end are already traversal-ordered and the midpoint is
                    // direction-agnostic, so this span follows the loop directly.
                    (arcStart, arcSweep) = ArcSpan(actual, start, end,
                        edge.Curve.PointAt(edge.Domain.Mid));
                }
                else
                {
                    throw new NotSupportedException(
                        "Rim edges must be lines or circular arcs (straighten or round other curves first).");
                }

                edges.Add(new RimEdgeInfo(use, edge, neighbor, neighborUse, start, end, downDir, arc, arcStart, arcSweep));
            }

            if (fillet)
                ValidateTangentContinuity(edges);

            var topPoints = new Vector3d[n];
            var bottomPoints = new Vector3d[n];
            for (int i = 0; i < n; i++)
            {
                var previous = edges[(i + n - 1) % n];
                var current = edges[i];
                var corner = current.Start;
                topPoints[i] = TopOffsetCorner(previous, current, corner, top, up, fillet);

                var dropA = corner + previous.DownDir * side;
                var dropB = corner + current.DownDir * side;
                if (dropA.DistanceTo(dropB) > 1e-9)
                    throw new NotSupportedException(
                        "Rim corners must descend consistently on both neighbors (uniform side geometry).");
                bottomPoints[i] = dropB;
            }

            var topVertices = topPoints.Select(p => new BrepVertex(p)).ToArray();
            var bottomVertices = bottomPoints.Select(p => new BrepVertex(p)).ToArray();
            var topEdges = new BrepEdge[n];
            var bottomEdges = new BrepEdge[n];
            var cornerEdges = new BrepEdge[n];
            for (int i = 0; i < n; i++)
            {
                int next = (i + 1) % n;
                var info = edges[i];
                topEdges[i] = OffsetEdge(info, topPoints[i], topPoints[next], top, up, atTop: true,
                    topVertices[i], topVertices[next]);
                bottomEdges[i] = OffsetEdge(info, bottomPoints[i], bottomPoints[next], side, up, atTop: false,
                    bottomVertices[i], bottomVertices[next]);
            }
            for (int i = 0; i < n; i++)
            {
                cornerEdges[i] = fillet
                    ? JunctionArc(topPoints[i], bottomPoints[i], top, up, topVertices[i], bottomVertices[i])
                    : new BrepEdge(new Line3d(topPoints[i], bottomPoints[i]), Interval.Unit,
                        topVertices[i], bottomVertices[i]);
            }

            // Shrunk top face (holes untouched).
            var newOuter = new BrepLoop([.. Enumerable.Range(0, n).Select(i => new BrepCoedge(topEdges[i], true))]);
            var newFace = new BrepFace(face.Surface,
                [newOuter, .. face.Loops.Where(l => !ReferenceEquals(l, outer))]);

            // Lower the neighbors' rim edges (they traverse opposite the top face).
            for (int i = 0; i < n; i++)
                edges[i].NeighborUse.Loop.ReplaceCoedge(edges[i].NeighborUse,
                    [new BrepCoedge(bottomEdges[i], false)]);

            // Shorten the side edges descending from the old corners.
            var shortened = new Dictionary<BrepEdge, BrepEdge>();
            for (int i = 0; i < n; i++)
            {
                var oldCorner = edges[i].Start;
                foreach (var use in edges[i].Neighbor.Loops.SelectMany(l => l.Coedges))
                {
                    var e = use.Edge;
                    if (shortened.ContainsKey(e) || bottomEdges.Contains(e))
                        continue;
                    bool atStart = e.Curve.PointAt(e.Domain.Start).DistanceTo(oldCorner) < 1e-9;
                    bool atEnd = e.Curve.PointAt(e.Domain.End).DistanceTo(oldCorner) < 1e-9;
                    if (!atStart && !atEnd)
                        continue;
                    if (e.Curve.Underlying is not Line3d)
                        throw new NotSupportedException("Side edges at rim corners must be straight.");
                    var farPoint = e.Curve.PointAt(atStart ? e.Domain.End : e.Domain.Start);
                    var farVertex = atStart ? e.EndVertex : e.StartVertex;
                    if (farPoint.DistanceTo(bottomPoints[i]) < 1e-9)
                        throw new NotSupportedException("The side setback consumes an entire neighbor edge.");
                    shortened[e] = atStart
                        ? new BrepEdge(new Line3d(bottomPoints[i], farPoint), Interval.Unit, bottomVertices[i], farVertex)
                        : new BrepEdge(new Line3d(farPoint, bottomPoints[i]), Interval.Unit, farVertex, bottomVertices[i]);
                }
            }
            foreach (var anyFace in solid.Faces)
            {
                foreach (var loop in anyFace.Loops)
                {
                    foreach (var use in loop.Coedges.ToList())
                    {
                        if (shortened.TryGetValue(use.Edge, out var replacement))
                            loop.ReplaceCoedge(use, [new BrepCoedge(replacement, use.SameSense)]);
                    }
                }
            }

            // Band faces.
            var bands = new List<BrepFace>(n);
            for (int i = 0; i < n; i++)
            {
                int next = (i + 1) % n;
                var loop = new BrepLoop(
                [
                    new BrepCoedge(topEdges[i], false),
                    new BrepCoedge(cornerEdges[i], true),
                    new BrepCoedge(bottomEdges[i], true),
                    new BrepCoedge(cornerEdges[next], false),
                ]);
                bands.Add(new BrepFace(
                    BandSurface(edges[i], topPoints[i], bottomPoints[i], top, up, fillet), [loop]));
            }

            // Domain-driven neighbor surfaces must be trimmed to the lowered extent.
            var trims = new Dictionary<BrepFace, BrepFace>();
            for (int i = 0; i < n; i++)
            {
                if (!trims.ContainsKey(edges[i].Neighbor)
                    && TrimNeighborBand(edges[i].Neighbor, bottomPoints[i]) is { } trimmedNeighbor)
                    trims[edges[i].Neighbor] = trimmedNeighbor;
            }

            var faces = solid.Faces
                .Where(f => !ReferenceEquals(f, face))
                .Select(f => trims.GetValueOrDefault(f, f))
                .ToList();
            faces.Add(newFace);
            faces.AddRange(bands);
            return new BrepSolid([new BrepShell(faces)]);
        }

        // ---- helpers ----

        /// <summary>Signed angular span of an arc edge measured in the circle's own
        /// frame, resolved through the edge's midpoint (handles &gt; π sweeps).</summary>
        private static (double Start, double Sweep) ArcSpan(
            Circle3d circle, in Vector3d start, in Vector3d end, in Vector3d mid)
        {
            double Angle(in Vector3d p)
            {
                var offset = p - circle.Center;
                return Math.Atan2(offset.Dot(circle.YDirection), offset.Dot(circle.XDirection));
            }
            double s = Angle(start);
            double ccwToEnd = Wrap(Angle(end) - s);
            double ccwToMid = Wrap(Angle(mid) - s);
            double sweep = ccwToMid <= ccwToEnd + 1e-9 ? ccwToEnd : ccwToEnd - 2 * Math.PI;
            return (s, sweep);

            static double Wrap(double a) => a - 2 * Math.PI * Math.Floor(a / (2 * Math.PI));
        }

        private static Vector3d TangentAtStart(RimEdgeInfo info)
        {
            var t = info.Edge.Curve.TangentAt(info.Use.SameSense ? info.Edge.Domain.Start : info.Edge.Domain.End);
            return info.Use.SameSense ? t : -t;
        }

        private static Vector3d TangentAtEnd(RimEdgeInfo info)
        {
            var t = info.Edge.Curve.TangentAt(info.Use.SameSense ? info.Edge.Domain.End : info.Edge.Domain.Start);
            return info.Use.SameSense ? t : -t;
        }

        private static void ValidateTangentContinuity(List<RimEdgeInfo> edges)
        {
            for (int i = 0; i < edges.Count; i++)
            {
                var previous = edges[(i + edges.Count - 1) % edges.Count];
                if (TangentAtEnd(previous).Cross(TangentAtStart(edges[i])).Length > 1e-6)
                    throw new NotSupportedException(
                        "Fillet rims must be tangent-continuous (round the sketch corners); chamfer handles sharp corners.");
            }
        }

        private static Vector3d TopOffsetCorner(
            RimEdgeInfo previous, RimEdgeInfo current, in Vector3d corner, double amount, in Vector3d up, bool fillet)
        {
            var inPrev = up.Cross(TangentAtEnd(previous)).Normalized();   // interior is left of travel
            var inCurrent = up.Cross(TangentAtStart(current)).Normalized();
            // Tangent-continuous corners (finite-difference tangents agree to ~1e-9)
            // share the offset point; anything sharper miters.
            if (fillet || inPrev.Cross(inCurrent).Length < 1e-6)
                return corner + inCurrent * amount;

            // Miter: intersect the offset lines of the two edges within the top plane.
            var d1 = TangentAtEnd(previous);
            var p1 = corner + inPrev * amount;
            var d2 = TangentAtStart(current);
            var p2 = corner + inCurrent * amount;
            var w = p2 - p1;
            double a = d1.Dot(d1), b = d1.Dot(d2), c = d2.Dot(d2);
            double d = d1.Dot(w), e = d2.Dot(w);
            double denominator = a * c - b * b;
            if (Math.Abs(denominator) < 1e-15)
                throw new NotSupportedException("Degenerate rim corner (parallel edges).");
            return p1 + d1 * ((d * c - b * e) / denominator);
        }

        /// <summary>New rim edge (top: inset; bottom: dropped), built in traversal
        /// direction — lines directly, arcs as concentric trimmed circles.</summary>
        private static BrepEdge OffsetEdge(
            RimEdgeInfo info, in Vector3d from, in Vector3d to, double amount, in Vector3d up, bool atTop,
            BrepVertex fromVertex, BrepVertex toVertex)
        {
            if (info.Arc is Circle3d arc)
            {
                bool convexAboutUp = arc.Axis.Dot(up) * Math.Sign(info.ArcSweep) > 0;
                double newRadius = atTop ? arc.Radius + (convexAboutUp ? -amount : amount) : arc.Radius;
                if (newRadius <= 1e-9)
                    throw new ArgumentOutOfRangeException(nameof(amount), "The offset consumes an arc radius.");
                var center = atTop ? arc.Center : arc.Center - up * amount;
                var concentric = new Circle3d(center, arc.XDirection, arc.YDirection, newRadius);
                var curve = new CurveSegment(concentric, info.ArcStart, info.ArcStart + info.ArcSweep);
                return new BrepEdge(curve, Interval.Unit, fromVertex, toVertex);
            }
            return new BrepEdge(new Line3d(from, to), Interval.Unit, fromVertex, toVertex);
        }

        /// <summary>Fillet band cross-section at a corner: a quarter arc from the top
        /// tangency down to the side tangency, stored top → bottom.</summary>
        private static BrepEdge JunctionArc(
            in Vector3d topPoint, in Vector3d bottomPoint, double radius, in Vector3d up,
            BrepVertex topVertex, BrepVertex bottomVertex)
        {
            // Built top → bottom directly (x = up), matching the loop convention and
            // the extruded band generators' sampling.
            var center = topPoint - up * radius;
            var yDir = (bottomPoint - center).Normalized();
            var arc = NurbsCurve.Arc(center, up, yDir, radius, 0, Math.PI / 2);
            return new BrepEdge(arc, arc.Domain, topVertex, bottomVertex);
        }

        private static Surface BandSurface(
            RimEdgeInfo info, in Vector3d topPoint, in Vector3d bottomPoint, double amount, in Vector3d up, bool fillet)
        {
            // Orientation notes: the tessellator's outward convention is ∂u × ∂v.
            // Revolved bands (∂u tangential along traversal) need bottom → top
            // generators; extruded bands (∂u = generator tangent, ∂v = edge) need
            // top → bottom cross-sections.
            if (info.Arc is Circle3d arc)
            {
                var axis = info.ArcSweep * arc.Axis.Dot(up) > 0 ? up : -up;
                if (!fillet)
                    return new RevolvedSurface(new Line3d(bottomPoint, topPoint), arc.Center, axis, Math.Abs(info.ArcSweep));
                var torusCenter = topPoint - up * amount;
                var torusX = (bottomPoint - torusCenter).Normalized();
                var generator = NurbsCurve.Arc(torusCenter, torusX, up, amount, 0, Math.PI / 2);
                return new RevolvedSurface(generator, arc.Center, axis, Math.Abs(info.ArcSweep));
            }

            if (!fillet)
            {
                var x = (info.End - info.Start).Normalized();
                var toBottom = bottomPoint - topPoint;
                var y = (toBottom - x * toBottom.Dot(x)).Normalized();
                var strip = new PlaneSurface(topPoint, x, y);
                // Flip the frame if the normal came out inward (rim strips face upward).
                return strip.Normal.Dot(up) < 0 ? new PlaneSurface(topPoint, x, -y) : strip;
            }

            var arcCenter = topPoint - up * amount;
            var arcY = (bottomPoint - arcCenter).Normalized();
            var crossSection = NurbsCurve.Arc(arcCenter, up, arcY, amount, 0, Math.PI / 2);
            return new ExtrudedSurface(crossSection, info.End - info.Start);
        }
    }
}
