using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// A surface's closed-form signed distance and its gradient — the residual a corner solve
/// Newtons on (<see cref="SurfaceCorner"/>), and the ruler a constructed curve is verified
/// against.
///
/// <para><b>Why implicit rather than closest-point.</b> A corner is the root of "be on all of
/// these", which needs a residual and a gradient, not a foot. Every carrier this kernel builds
/// curved solids from has both in closed form: a plane's is a dot product, a cylinder's and a
/// sphere's a radius comparison, and a revolve's or an extrusion's reduces to a TWO-dimensional
/// line or circle in its own profile coordinates. Going through inverse evaluation instead
/// would put a Gauss–Newton solve inside every Newton step, and — worse — the surfaces'
/// <c>TryProjectPoint</c> overrides answer "is this point ON me", which is exactly false for
/// every iterate of a corner solve.</para>
///
/// <para><b>The field is the CARRIER's, not the face's.</b> Trims do not enter: a corner may
/// legitimately sit past the end of the generator that produced it (the eroded apex of a
/// shelled cone is the canonical case), and clamping to the trim would put a spurious kink in
/// the residual exactly where Newton needs it smooth.</para>
/// </summary>
internal readonly struct ImplicitSurface
{
    private enum Shape
    {
        /// <summary>Signed distance to a plane: (p − origin)·normal.</summary>
        Plane,

        /// <summary>Radial distance to an axis, minus a radius.</summary>
        Cylinder,

        /// <summary>Distance to a centre, minus a radius.</summary>
        Sphere,

        /// <summary>A 2D line in profile coordinates (a cone, or a straight extrusion).</summary>
        ProfileLine,

        /// <summary>A 2D circle in profile coordinates (a torus, or a circular extrusion).</summary>
        ProfileCircle,
    }

    /// <summary>How a 3D point maps to the 2D profile plane of a swept carrier.</summary>
    private enum Sweep
    {
        None,

        /// <summary>(radius about the axis, height along it) — a surface of revolution.</summary>
        Revolved,

        /// <summary>Coordinates in the plane perpendicular to the extrusion direction.</summary>
        Extruded,
    }

    private Shape Kind { get; init; }
    private Sweep Mapping { get; init; }

    /// <summary>Axis origin (revolve), plane origin, cylinder origin, or sphere centre.</summary>
    private Vector3d Origin { get; init; }

    /// <summary>Plane normal, cylinder/revolve axis, or the extrusion direction.</summary>
    private Vector3d Axis { get; init; }

    /// <summary>First in-plane basis vector of an extruded profile.</summary>
    private Vector3d BasisX { get; init; }

    /// <summary>Second in-plane basis vector of an extruded profile.</summary>
    private Vector3d BasisY { get; init; }

    /// <summary>Profile-line outward normal, or the profile circle's centre.</summary>
    private Vector2d Profile { get; init; }

    /// <summary>Profile-line offset along its normal, or the profile circle's radius.</summary>
    private double Value { get; init; }

    /// <summary>Radius (cylinder, sphere), or ±1 orienting a profile circle.</summary>
    private double Radius { get; init; }

    /// <summary>
    /// The signed distance at <paramref name="point"/> — negative inside an outward-oriented
    /// carrier — with the unit outward <paramref name="gradient"/> there.
    /// </summary>
    public double Evaluate(in Vector3d point, out Vector3d gradient)
    {
        switch (Kind)
        {
            case Shape.Plane:
                gradient = Axis;
                return (point - Origin).Dot(Axis);

            case Shape.Sphere:
            {
                var offset = point - Origin;
                double length = offset.Length;
                // Exact-zero division guard: at the centre every direction is equally
                // outward, so the axis is as good an answer as any.
                gradient = length > 0 ? offset / length : Axis;
                return length - Radius;
            }

            case Shape.Cylinder:
            {
                var offset = point - Origin;
                var radial = offset - Axis * offset.Dot(Axis);
                double length = radial.Length;
                gradient = length > 0 ? radial / length : Axis.ArbitraryPerpendicular(Tolerance.Default);
                return length - Radius;
            }

            default:
            {
                var (q, radial) = ToProfile(point);
                if (Kind == Shape.ProfileLine)
                {
                    gradient = Lift(Profile, radial);
                    return q.Dot(Profile) - Value;
                }
                var toCentre = q - Profile;
                double distance = toCentre.Length;
                var normal2 = distance > 0 ? toCentre / distance : new Vector2d(1, 0);
                gradient = Lift(normal2 * Radius, radial);
                return Radius * (distance - Value);
            }
        }
    }

    /// <summary>Profile coordinates of a point, plus the unit radial the lift needs.</summary>
    private (Vector2d Coordinates, Vector3d Radial) ToProfile(in Vector3d point)
    {
        var offset = point - Origin;
        if (Mapping == Sweep.Revolved)
        {
            double height = offset.Dot(Axis);
            var radial = offset - Axis * height;
            double length = radial.Length;
            var unit = length > 0 ? radial / length : Axis.ArbitraryPerpendicular(Tolerance.Default);
            return (new Vector2d(length, height), unit);
        }
        return (new Vector2d(offset.Dot(BasisX), offset.Dot(BasisY)), default);
    }

    /// <summary>A profile-plane vector back in 3D.</summary>
    private Vector3d Lift(in Vector2d value, in Vector3d radial) =>
        Mapping == Sweep.Revolved ? radial * value.X + Axis * value.Y : BasisX * value.X + BasisY * value.Y;

    /// <summary>
    /// The implicit form of a carrier, or false with the reason it has none. Swept carriers
    /// are recognized by SAMPLING their generators — a wrapper's underlying curve says nothing
    /// about where the generator actually goes — and their outward sense is read off the
    /// surface's own normal rather than derived, so no orientation convention has to be
    /// re-proved here.
    /// </summary>
    public static bool TryBuild(Surface surface, out ImplicitSurface field, out string? reason)
    {
        field = default;
        reason = null;
        switch (surface)
        {
            case PlaneSurface plane:
                field = new ImplicitSurface
                {
                    Kind = Shape.Plane,
                    Origin = plane.Origin,
                    Axis = plane.Normal.Normalized(),
                };
                return true;

            case CylinderSurface cylinder:
                field = new ImplicitSurface
                {
                    Kind = Shape.Cylinder,
                    Origin = cylinder.Origin,
                    Axis = cylinder.Axis.Normalized(),
                    Radius = cylinder.Radius,
                };
                return true;

            case SphereSurface sphere:
                field = new ImplicitSurface
                {
                    Kind = Shape.Sphere,
                    Origin = sphere.Center,
                    Axis = Vector3d.UnitZ,
                    Radius = sphere.Radius,
                };
                return true;

            case RevolvedSurface revolved:
                return TryBuildSwept(
                    revolved, revolved.Generator, Sweep.Revolved,
                    revolved.AxisOrigin, revolved.AxisDirection, default, default, out field, out reason);

            case ExtrudedSurface extruded:
            {
                if (!extruded.Direction.TryNormalize(Tolerance.Default, out var direction))
                {
                    reason = "the extrusion direction is degenerate";
                    return false;
                }
                var basisX = direction.ArbitraryPerpendicular(Tolerance.Default);
                var basisY = direction.Cross(basisX);
                return TryBuildSwept(
                    extruded, extruded.Generator, Sweep.Extruded,
                    extruded.Generator.PointAt(extruded.Generator.Domain.Start), direction,
                    basisX, basisY, out field, out reason);
            }

            default:
                reason = $"{surface.GetType().Name} is not one of the analytic carriers " +
                         "(plane, cylinder, sphere, straight or circular revolve or extrusion)";
                return false;
        }
    }

    private static bool TryBuildSwept(
        Surface surface, Curve3d generator, Sweep mapping,
        in Vector3d origin, in Vector3d axisDirection, in Vector3d basisX, in Vector3d basisY,
        out ImplicitSurface field, out string? reason)
    {
        field = default;
        reason = null;
        if (!axisDirection.TryNormalize(Tolerance.Default, out var axis))
        {
            reason = "the sweep axis is degenerate";
            return false;
        }

        var template = new ImplicitSurface
        {
            Mapping = mapping,
            Origin = origin,
            Axis = axis,
            BasisX = basisX,
            BasisY = basisY,
        };

        var domain = generator.Domain;
        if (!double.IsFinite(domain.Length))
        {
            reason = "the generator has an unbounded domain";
            return false;
        }

        const int samples = 16;
        Span<Vector2d> profile = stackalloc Vector2d[samples + 1];
        for (int i = 0; i <= samples; i++)
            profile[i] = template.ToProfile(generator.PointAt(domain.ParameterAt((double)i / samples))).Coordinates;

        // Extent of the sampled profile: every degeneracy guard below is relative to it,
        // because a "distance" here is quadratic in scale exactly as an area is.
        double extent = 0;
        for (int i = 1; i <= samples; i++)
            extent = Math.Max(extent, (profile[i] - profile[0]).Length);
        if (extent <= Tolerance.Default.Linear)
        {
            reason = "the generator collapses to a point in its own profile plane";
            return false;
        }

        var candidate = TryFitProfileLine(template, profile, extent)
            ?? TryFitProfileCircle(template, profile, extent);
        if (candidate is null)
        {
            reason = "the generator is neither straight nor circular in its profile plane, so the " +
                     "carrier has no closed-form implicit distance";
            return false;
        }

        // Orient against the surface's OWN normal at a mid-generator sample, so no outward
        // convention has to be re-derived (and none can drift from the surface's).
        var built = candidate.Value;
        double v = surface.DomainV.Mid;
        double u = surface.DomainU.Mid;
        if (!double.IsFinite(u))
            u = 0;
        if (!double.IsFinite(v))
            v = 0;
        var point = surface.PointAt(u, v);
        var normal = surface.NormalAt(u, v);
        built.Evaluate(point, out var gradient);
        if (gradient.Dot(normal) < 0)
        {
            built = built.Kind == Shape.ProfileLine
                ? built with { Profile = -built.Profile, Value = -built.Value }
                : built with { Radius = -built.Radius };
        }
        field = built;
        return true;
    }

    private static ImplicitSurface? TryFitProfileLine(
        in ImplicitSurface template, ReadOnlySpan<Vector2d> profile, double extent)
    {
        // The FARTHEST sample, not the last one: a CLOSED generator (a full circle extruded
        // into a cylinder, a torus's minor circle) returns to its start, so the end-to-end
        // chord is zero and both fits below would report degeneracy on the commonest input
        // there is.
        var direction = Vector2d.Zero;
        for (int i = 1; i < profile.Length; i++)
        {
            if ((profile[i] - profile[0]).Length > direction.Length)
                direction = profile[i] - profile[0];
        }
        if (direction.Length <= 1e-13 * extent)
            return null;
        direction /= direction.Length;
        var normal = new Vector2d(direction.Y, -direction.X);
        double offset = profile[0].Dot(normal);
        for (int i = 1; i < profile.Length; i++)
        {
            // Weld tier, relative to the profile's own extent: "straight enough to BE a line".
            if (Math.Abs(profile[i].Dot(normal) - offset) > Tolerance.Default.Linear * Math.Max(1, extent))
                return null;
        }
        return template with { Kind = Shape.ProfileLine, Profile = normal, Value = offset };
    }

    private static ImplicitSurface? TryFitProfileCircle(
        in ImplicitSurface template, ReadOnlySpan<Vector2d> profile, double extent)
    {
        // Thirds, not (first, middle, last): a closed generator's last sample IS its first,
        // which makes the circumcircle degenerate on every cylinder and every torus.
        var a = profile[0];
        var b = profile[profile.Length / 3];
        var c = profile[2 * profile.Length / 3];
        double area2 = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        // Scale-free collinearity guard: twice an AREA, so the threshold is quadratic in the
        // profile's own extent.
        if (Math.Abs(area2) < 1e-13 * extent * extent)
            return null;
        var ab = b - a;
        var ac = c - a;
        double centreX = a.X + (ac.Y * ab.LengthSquared - ab.Y * ac.LengthSquared) / (2 * area2);
        double centreY = a.Y + (ab.X * ac.LengthSquared - ac.X * ab.LengthSquared) / (2 * area2);
        var centre = new Vector2d(centreX, centreY);
        double radius = (a - centre).Length;
        if (radius <= Tolerance.Default.Linear)
            return null;
        for (int i = 0; i < profile.Length; i++)
        {
            if (Math.Abs((profile[i] - centre).Length - radius) > Tolerance.Default.Linear * Math.Max(1, radius))
                return null;
        }
        return template with { Kind = Shape.ProfileCircle, Profile = centre, Value = radius, Radius = 1 };
    }
}
