using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// LINQ-friendly classification and adjacency queries over B-Rep topology — the
/// selection vocabulary for feature operations:
/// <c>solid.Faces.Where(f => f.IsPlanar(out _, out var n) &amp;&amp; n.IsParallelTo(Vector3d.UnitZ, tol))</c>.
/// </summary>
public static class BrepQueries
{
    // ---- face classification ----

    /// <summary>Planar face test — recognizes <see cref="PlaneSurface"/> and extruded
    /// straight lines (the sides of extrusions); <paramref name="normal"/> is outward
    /// (reversal applied).</summary>
    public static bool IsPlanar(this BrepFace face, out Vector3d origin, out Vector3d normal)
    {
        switch (face.Surface)
        {
            case PlaneSurface plane:
                origin = plane.Origin;
                normal = face.IsReversed ? -plane.Normal : plane.Normal;
                return true;

            case ExtrudedSurface extruded when extruded.Generator.Underlying is Line3d:
            {
                var start = extruded.Generator.PointAt(extruded.Generator.Domain.Start);
                var end = extruded.Generator.PointAt(extruded.Generator.Domain.End);
                var tangent = (end - start).Normalized();
                // ∂u × ∂v is the tessellator's outward convention for generated surfaces.
                var raw = tangent.Cross(extruded.Direction);
                if (!raw.TryNormalize(Tolerance.Default, out var n))
                    break;
                origin = start;
                normal = face.IsReversed ? -n : n;
                return true;
            }
        }
        origin = default;
        normal = default;
        return false;
    }

    /// <summary>
    /// Cylindrical face test — recognizes <see cref="CylinderSurface"/>, extruded
    /// circles, and axis-parallel revolved lines (the forms booleans produce).
    /// </summary>
    public static bool IsCylindrical(this BrepFace face, out Vector3d axisOrigin, out Vector3d axisDirection, out double radius)
    {
        switch (face.Surface)
        {
            case CylinderSurface cylinder:
                axisOrigin = cylinder.Origin;
                axisDirection = cylinder.Axis.Normalized();
                radius = cylinder.Radius;
                return true;

            case ExtrudedSurface extruded when extruded.Generator.Underlying is Circle3d circle
                    && extruded.Direction.IsParallelTo(circle.Axis, Tolerance.Default):
                axisOrigin = circle.Center;
                axisDirection = extruded.Direction.Normalized();
                radius = circle.Radius;
                return true;

            case RevolvedSurface revolved when revolved.Generator.Underlying is Line3d:
            {
                var axis = revolved.AxisDirection;
                var start = revolved.Generator.PointAt(revolved.Generator.Domain.Start);
                var end = revolved.Generator.PointAt(revolved.Generator.Domain.End);
                if (!(end - start).IsParallelTo(axis, Tolerance.Default))
                    break;
                var offset = start - revolved.AxisOrigin;
                axisOrigin = revolved.AxisOrigin;
                axisDirection = axis;
                radius = (offset - axis * offset.Dot(axis)).Length;
                return true;
            }
        }
        axisOrigin = default;
        axisDirection = default;
        radius = 0;
        return false;
    }

    /// <summary>Outward normal at a point on the face (reversal applied).</summary>
    public static Vector3d NormalAt(this BrepFace face, in Vector3d point)
    {
        if (!face.Surface.TryProjectPoint(point, out var uv, 1e-6))
            throw new ArgumentException("The point does not lie on the face's surface.", nameof(point));
        var normal = face.Surface.NormalAt(uv.X, uv.Y).Normalized();
        return face.IsReversed ? -normal : normal;
    }

    // ---- edge classification ----

    public static bool IsLinear(this BrepEdge edge, out Vector3d start, out Vector3d end)
    {
        start = edge.Curve.PointAt(edge.Domain.Start);
        end = edge.Curve.PointAt(edge.Domain.End);
        return edge.Curve.Underlying is Line3d;
    }

    public static bool IsCircular(this BrepEdge edge, out Vector3d center, out Vector3d normal, out double radius)
    {
        if (edge.Curve.Underlying is Circle3d circle)
        {
            center = circle.Center;
            normal = circle.Axis.Normalized();
            radius = circle.Radius;
            return true;
        }
        center = default;
        normal = default;
        radius = 0;
        return false;
    }

    /// <summary>Edge length: exact for lines and circular arcs, sampled otherwise.</summary>
    public static double Length(this BrepEdge edge)
    {
        var domain = edge.Domain;
        switch (edge.Curve.Underlying)
        {
            case Line3d:
                return edge.Curve.PointAt(domain.Start).DistanceTo(edge.Curve.PointAt(domain.End));
            case Circle3d circle when ReferenceEquals(edge.Curve.Underlying, edge.Curve):
                return circle.Radius * domain.Length;
        }
        double length = 0;
        const int samples = 64;
        var previous = edge.Curve.PointAt(domain.Start);
        for (int i = 1; i <= samples; i++)
        {
            var point = edge.Curve.PointAt(domain.ParameterAt((double)i / samples));
            length += previous.DistanceTo(point);
            previous = point;
        }
        return length;
    }

    public static Aabb Bounds(this BrepEdge edge)
    {
        var bounds = Aabb.Empty;
        const int samples = 32;
        for (int i = 0; i <= samples; i++)
            bounds = bounds.Union(edge.Curve.PointAt(edge.Domain.ParameterAt((double)i / samples)));
        return bounds;
    }

    public static Aabb Bounds(this BrepFace face)
    {
        var bounds = Aabb.Empty;
        foreach (var loop in face.Loops)
        {
            foreach (var coedge in loop.Coedges)
                bounds = bounds.Union(coedge.Edge.Bounds());
        }

        // Edge samples alone miss the interior of curved faces: a pole-bounded
        // hemisphere's only edge is its equator, whose bounds are a flat disk — the
        // dome would be invisible (and boolean face-pair prefiltering would silently
        // skip real intersections). Generated surfaces tessellate domain-driven, so a
        // finite surface domain is the face's coverage; sample it too. Planar faces
        // need no interior samples (the boundary's hull contains the face), and
        // infinite carrier domains (planes, cylinders) cannot be sampled — their
        // faces are bounded by their edges anyway.
        var surface = face.Surface;
        if (surface is not PlaneSurface &&
            double.IsFinite(surface.DomainU.Length) && double.IsFinite(surface.DomainV.Length))
        {
            const int samples = 16;
            for (int i = 0; i <= samples; i++)
            {
                double u = surface.DomainU.ParameterAt((double)i / samples);
                for (int j = 0; j <= samples; j++)
                    bounds = bounds.Union(surface.PointAt(u, surface.DomainV.ParameterAt((double)j / samples)));
            }
        }
        return bounds;
    }

    // ---- topology ----

    /// <summary>The distinct edges bounding a face (all loops).</summary>
    public static IEnumerable<BrepEdge> Edges(this BrepFace face) =>
        face.Loops.SelectMany(l => l.Coedges).Select(c => c.Edge).Distinct();

    /// <summary>The faces sharing an edge (2 on a manifold interior edge; possibly one
    /// face twice for seam edges).</summary>
    public static IReadOnlyList<BrepFace> FacesOf(this BrepSolid solid, BrepEdge edge)
    {
        var faces = new List<BrepFace>(2);
        foreach (var face in solid.Faces)
        {
            foreach (var loop in face.Loops)
            {
                if (loop.Coedges.Any(c => ReferenceEquals(c.Edge, edge)))
                {
                    faces.Add(face);
                    break;
                }
            }
        }
        return faces;
    }

    /// <summary>
    /// True when the material angle across the edge is less than π (a corner sticking
    /// out, like every edge of a box); false for concave (grooves) or smooth edges.
    /// </summary>
    public static bool IsConvex(this BrepSolid solid, BrepEdge edge, double tolerance = 1e-9)
    {
        var faces = solid.FacesOf(edge);
        if (faces.Count != 2 || ReferenceEquals(faces[0], faces[1]))
            return false;

        var midpoint = edge.Curve.PointAt(edge.Domain.Mid);
        var tangent = edge.Curve.TangentAt(edge.Domain.Mid);
        var n0 = faces[0].NormalAt(midpoint);
        var n1 = faces[1].NormalAt(midpoint);

        // Orient the tangent along face 0's traversal of the edge.
        var use = faces[0].Loops.SelectMany(l => l.Coedges).First(c => ReferenceEquals(c.Edge, edge));
        if (!use.SameSense)
            tangent = -tangent;

        return n0.Cross(n1).Dot(tangent) > tolerance;
    }

    // ---- selector sugar ----

    /// <summary>Planar faces whose outward normal points along <paramref name="direction"/>.</summary>
    public static IEnumerable<BrepFace> PlanarFacesWithNormal(
        this BrepSolid solid, Vector3d direction, double angularTolerance = 1e-6)
    {
        var wanted = direction.Normalized();
        foreach (var face in solid.Faces)
        {
            if (face.IsPlanar(out _, out var normal) && normal.Dot(wanted) > 1 - angularTolerance)
                yield return face;
        }
    }
}
