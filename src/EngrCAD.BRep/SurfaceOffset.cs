using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// Exact analytic offsets of a surface along its own normal — the geometric half of every
/// offset-and-re-intersect operation (<see cref="Shelling"/>, <see cref="Draft"/>).
///
/// <para><b>What is exact, and why it is worth naming.</b> The offset of a plane is a plane,
/// of a cylinder a cylinder, of a sphere a sphere, and of a surface of revolution the revolve
/// of the <i>offset generator</i> — which for the shapes this kernel builds (straight and
/// circular generators) is again a straight or circular curve, so a cone offsets to a cone and
/// a torus to a torus. Nothing here approximates: the result is a surface of the SAME family
/// with new parameters, which is what keeps the downstream machinery (analytic intersection,
/// domain-driven tessellation, STEP export, `BrepQueries` recognition) working on the output.
/// A generic <see cref="OffsetCurve3d"/> generator is the honest fallback for a generator that
/// is neither straight nor circular; it is still exact geometry, just without the family
/// simplification.</para>
///
/// <para><b>The parameterization is preserved deliberately.</b> Frames, generator spans and
/// sweep angles carry over verbatim, so the offset surface's u = 0 sits where the original's
/// did. That is the phase-alignment rule this kernel has paid for repeatedly: a rim circle
/// built on the offset surface's own frame lands on its tessellation grid only if the frame
/// did not rotate. Nothing here recomputes a frame from a solved point.</para>
/// </summary>
public static class SurfaceOffset
{
    /// <summary>
    /// The surface displaced by <paramref name="distance"/> along its own outward normal
    /// (∂u × ∂v). Positive grows an outward-oriented face, negative shrinks it.
    /// </summary>
    /// <returns>False, with <paramref name="reason"/> naming the obstacle, when no exact
    /// offset of the same family exists.</returns>
    public static bool TryOffset(Surface surface, double distance, out Surface offset, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(surface);
        offset = null!;
        reason = null;
        // Exact-zero semantic test: a zero offset is the identity, and returning the very
        // same object keeps "offset by 0" bit-identical for every consumer.
        if (distance == 0)
        {
            offset = surface;
            return true;
        }

        switch (surface)
        {
            case PlaneSurface plane:
                // Translation only: u and v map to the same coordinates on the offset plane.
                offset = new PlaneSurface(
                    plane.Origin + plane.Normal.Normalized() * distance, plane.XDirection, plane.YDirection);
                return true;

            case CylinderSurface cylinder:
            {
                double radius = cylinder.Radius + distance;
                if (radius <= Tolerance.Default.Linear)
                {
                    reason = $"the offset consumes a cylinder of radius {cylinder.Radius:g6} " +
                             $"(it would leave {radius:g6})";
                    return false;
                }
                offset = new CylinderSurface(
                    cylinder.Origin, cylinder.XDirection, cylinder.YDirection, radius);
                return true;
            }

            case SphereSurface sphere:
            {
                double radius = sphere.Radius + distance;
                if (radius <= Tolerance.Default.Linear)
                {
                    reason = $"the offset consumes a sphere of radius {sphere.Radius:g6}";
                    return false;
                }
                offset = new SphereSurface(sphere.Center, radius);
                return true;
            }

            case RevolvedSurface revolved:
            {
                if (!TryHalfPlaneNormal(revolved, out var halfPlaneNormal))
                {
                    reason = "the generator of a surface of revolution lies on its own axis, so the " +
                             "half-plane it spans is undefined";
                    return false;
                }
                // O = C + d·(n̂ × T̂) is exactly C + d·N for a revolve, because ∂u is
                // parallel to the half-plane normal and ∂v is the generator tangent.
                if (!TryOffsetPlanarCurve(revolved.Generator, halfPlaneNormal, distance, out var generator, out reason))
                    return false;
                offset = new RevolvedSurface(
                    generator, revolved.AxisOrigin, revolved.AxisDirection, revolved.Angle);
                return true;
            }

            case ExtrudedSurface extruded:
            {
                if (!extruded.Direction.TryNormalize(Tolerance.Default, out var direction))
                {
                    reason = "the extrusion direction is degenerate";
                    return false;
                }
                // N = T̂ × d̂ = −(d̂ × T̂), so the OffsetCurve3d sign flips. A CURVED generator
                // must lie in a plane perpendicular to the direction for that identity to hold
                // — a sheared curved extrusion's normal is not a pure in-plane rotation of the
                // tangent, and the curve offset refuses it. A sheared STRAIGHT generator is
                // fine: the surface is still exactly a plane.
                if (!TryOffsetPlanarCurve(extruded.Generator, direction, -distance, out var generator, out reason))
                {
                    reason = "no same-family offset of this extrusion: " + reason;
                    return false;
                }
                offset = new ExtrudedSurface(generator, extruded.Direction);
                return true;
            }

            default:
                reason = $"{surface.GetType().Name} has no exact analytic offset";
                return false;
        }
    }

    /// <summary>Throwing form of <see cref="TryOffset"/>.</summary>
    public static Surface Offset(Surface surface, double distance) =>
        TryOffset(surface, distance, out var offset, out var reason)
            ? offset
            : throw new NotSupportedException($"Cannot offset this surface exactly: {reason}.");

    /// <summary>
    /// The planar offset O = C + <paramref name="distance"/>·(n̂ × T̂), simplified back to the
    /// curve's own family where one exists: a straight curve offsets to a
    /// <see cref="Line3d"/>, a circular one to a concentric circle or arc of the SAME spelling
    /// (<see cref="Circle3d"/> / <see cref="CurveSegment"/> / rational <see cref="NurbsCurve"/>
    /// arc), and anything else to an <see cref="OffsetCurve3d"/>.
    /// </summary>
    /// <remarks>
    /// Straightness and circularity are decided by SAMPLING the actual curve, never by reading
    /// <see cref="Curve3d.Underlying"/>: a wrapper's underlying geometry may sit somewhere else
    /// entirely, and a <c>CurveSegment</c> over a circle reports the untrimmed circle.
    /// Preserving the SPELLING matters as much as the shape — a corner patch samples an arc at
    /// even angles, so an arc respelled as a rational NURBS (or the reverse) samples to
    /// different points and stops welding.
    /// </remarks>
    public static bool TryOffsetPlanarCurve(
        Curve3d curve, in Vector3d planeNormal, double distance, out Curve3d offset, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(curve);
        offset = null!;
        reason = null;
        if (!planeNormal.TryNormalize(Tolerance.Default, out var normal))
        {
            reason = "the offset plane normal is degenerate";
            return false;
        }
        // Exact-zero semantic test: identity, and the very same object keeps consumers
        // bit-identical.
        if (distance == 0)
        {
            offset = curve;
            return true;
        }

        var domain = curve.Domain;
        if (!double.IsFinite(domain.Length))
        {
            reason = "the curve has an unbounded domain";
            return false;
        }

        if (CircularArc.TryFit(curve, out var fit))
        {
            if (Math.Abs(fit.Circle.Axis.Dot(normal)) < 1 - 1e-9)
            {
                reason = "a circular generator does not lie in the offset plane";
                return false;
            }
            // n̂ × T̂ on a circle points along ∓ the radial, depending on the circle's own
            // winding about n̂: a CCW circle's offset SHRINKS for a positive distance.
            double sign = fit.Circle.Axis.Dot(normal) > 0 ? -1 : 1;
            double radius = fit.Circle.Radius + sign * distance;
            if (radius <= Tolerance.Default.Linear)
            {
                reason = $"the offset consumes a circular generator of radius {fit.Circle.Radius:g6}";
                return false;
            }
            offset = fit.Rebuild(radius);
            return true;
        }

        if (TryStraight(curve, out var start, out var end, out var tangent))
        {
            // n̂ × T̂ is already unit whenever n̂ is the curve's own plane normal, so
            // normalizing is a no-op in the planar case and the RIGHT answer in the one case
            // that is not: an extrusion of a straight generator SHEARED against its direction
            // is still exactly a plane, and its offset is still exactly a translation — but
            // by the unit normal, not by the shortened cross product.
            if (!normal.Cross(tangent).TryNormalize(Tolerance.Default, out var outward))
            {
                reason = "the curve runs along the offset plane's normal";
                return false;
            }
            var shift = outward * distance;
            offset = new Line3d(start + shift, end + shift);
            return true;
        }

        // Exact, but without the family simplification: the generic offset carries the base
        // curve's parameterization and its exact derivative law.
        try
        {
            offset = new OffsetCurve3d(curve, normal, distance);
            return true;
        }
        catch (ArgumentException exception)
        {
            reason = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// The unit normal of the half-plane a revolve's generator spans with its axis — the
    /// direction ∂u points at u = 0, and therefore the plane normal the generator offset must
    /// use. Sampled off the axis, since a generator that touches the axis (a pole) has no
    /// radial direction there.
    /// </summary>
    private static bool TryHalfPlaneNormal(RevolvedSurface revolved, out Vector3d normal)
    {
        normal = default;
        var axis = revolved.AxisDirection;
        var domain = revolved.Generator.Domain;
        const int samples = 16;
        double bestRadius = 0;
        for (int i = 0; i <= samples; i++)
        {
            var offset = revolved.Generator.PointAt(domain.ParameterAt((double)i / samples)) - revolved.AxisOrigin;
            var radial = offset - axis * offset.Dot(axis);
            if (radial.Length > bestRadius)
            {
                bestRadius = radial.Length;
                normal = axis.Cross(radial);
            }
        }
        return normal.TryNormalize(Tolerance.Default, out normal);
    }

    /// <summary>
    /// Whether the curve is straight at the weld tier, with its ACTUAL endpoints and unit
    /// tangent (a wrapper's underlying line may run somewhere else entirely).
    /// </summary>
    private static bool TryStraight(Curve3d curve, out Vector3d start, out Vector3d end, out Vector3d tangent)
    {
        var domain = curve.Domain;
        start = curve.PointAt(domain.Start);
        end = curve.PointAt(domain.End);
        tangent = default;
        var chord = end - start;
        if (!chord.TryNormalize(Tolerance.Default, out tangent))
            return false;
        double length = chord.Length;
        const int samples = 16;
        for (int i = 1; i < samples; i++)
        {
            var point = curve.PointAt(domain.ParameterAt((double)i / samples)) - start;
            // Weld tier, scaled by the chord: "straight enough to be built as a line".
            if ((point - tangent * point.Dot(tangent)).Length > Tolerance.Default.Linear * Math.Max(1, length))
                return false;
        }
        return true;
    }
}

/// <summary>
/// A circular curve recognized by SAMPLING — the carrier circle, the traversal-ordered angular
/// span, and how to rebuild the same curve at a different radius <i>in its original
/// spelling</i>.
/// </summary>
/// <remarks>
/// The spelling is the load-bearing part. This kernel writes circular geometry three ways —
/// <see cref="Circle3d"/>, <see cref="CurveSegment"/> over one, and a rational
/// <see cref="NurbsCurve"/> arc — and they agree on the point set but NOT on the parameter
/// mapping. A revolve samples its generator at even parameter, so respelling an arc silently
/// moves every sample and the surface stops welding to the edges built beside it.
/// </remarks>
internal readonly struct CircularArc
{
    public required Circle3d Circle { get; init; }

    /// <summary>Angle of the curve's start point in the circle's own frame.</summary>
    public required double StartAngle { get; init; }

    /// <summary>Signed angular span from <see cref="StartAngle"/> to the curve's end.</summary>
    public required double Sweep { get; init; }

    internal required Curve3d Source { private get; init; }

    /// <summary>
    /// True when the carrier frame was taken VERBATIM from a <see cref="Circle3d"/> the source
    /// curve already carries (after verifying it against the sampled points), rather than
    /// rebuilt from the circumcentre. Only then may a trimmed spelling reuse its base
    /// parameters, which are angles in that exact frame.
    /// </summary>
    internal required bool FrameAdopted { private get; init; }

    /// <summary>The same curve at a new radius, in the source's own spelling.</summary>
    public Curve3d Rebuild(double radius)
    {
        var circle = new Circle3d(Circle.Center, Circle.XDirection, Circle.YDirection, radius);
        bool wholeTurn = Math.Abs(Math.Abs(Sweep) - 2 * Math.PI) < 1e-9;
        switch (Source)
        {
            // A whole circle: the closed carrier itself, domain and all.
            case Circle3d when FrameAdopted && wholeTurn:
                return circle;
            // A trimmed circle keeps its base parameters, which ARE angles — the rule every
            // corner patch and every revolve generator depends on.
            case CurveSegment segment when FrameAdopted && segment.Base is Circle3d:
                return new CurveSegment(circle, segment.BaseStart, segment.BaseEnd);
            // A rational arc's parameterization depends only on its SPAN and its start
            // direction, and the fitted frame's X points at the start — so this reproduces
            // the source's mapping exactly, rotated frame or not.
            case NurbsCurve or CurveSegment { Base: NurbsCurve }:
                return NurbsCurve.Arc(
                    Circle.Center, Circle.XDirection, Circle.YDirection, radius,
                    StartAngle, StartAngle + Sweep);
            default:
                return new CurveSegment(circle, StartAngle, StartAngle + Sweep);
        }
    }

    /// <summary>
    /// Fits a circle to the curve's own sampled points (circumcentre of three, then every
    /// sample verified at the weld tier). Where the source already carries a
    /// <see cref="Circle3d"/> that the samples confirm, that circle's exact frame is adopted
    /// so the rebuilt curve keeps the original's phase bit for bit; otherwise the frame's X
    /// points at the curve's START, which is what makes a rebuilt rational arc reproduce the
    /// source's parameterization.
    /// </summary>
    public static bool TryFit(Curve3d curve, out CircularArc arc) =>
        TryFit(curve, curve.Domain, out arc);

    /// <summary>
    /// The fit over an explicit parameter range. An EDGE carries its own domain and it is
    /// routinely narrower than its carrier's: a partial revolve's rail is a whole
    /// <see cref="Circle3d"/> used over a quarter turn, and fitting the carrier's domain
    /// instead would report a full-turn sweep and rebuild the rim as a closed circle.
    /// </summary>
    public static bool TryFit(Curve3d curve, in Interval range, out CircularArc arc)
    {
        arc = default;
        var domain = range;
        if (!double.IsFinite(domain.Length) || domain.Length <= 0)
            return false;

        var p0 = curve.PointAt(domain.Start);
        var p1 = curve.PointAt(domain.ParameterAt(1.0 / 3));
        var p2 = curve.PointAt(domain.ParameterAt(2.0 / 3));
        var u = p1 - p0;
        var v = p2 - p0;
        var plane = u.Cross(v);
        double planeLengthSquared = plane.LengthSquared;
        // Scale-free collinearity guard in area² units (the guard `Filleting.ActualCircle`
        // uses), not a model-unit tolerance.
        double scale = Math.Max(u.LengthSquared, v.LengthSquared);
        if (planeLengthSquared < 1e-24 * scale * scale)
            return false;

        var centre = p0 + (plane.Cross(u) * v.LengthSquared + v.Cross(plane) * u.LengthSquared)
            / (2 * planeLengthSquared);
        double radius = centre.DistanceTo(p0);
        if (radius <= Tolerance.Default.Linear)
            return false;

        var axis = plane.Normalized(); // p0 → p1 → p2 winds CCW about this
        double tolerance = Tolerance.Default.Linear * Math.Max(1, radius);

        // Adopt the source's own circle when the samples confirm it: the frame is then EXACT
        // rather than reconstructed, so a rebuilt trimmed arc's base parameters still mean
        // what they meant. Verification is what makes reading `Underlying` legitimate here.
        var circle = new Circle3d(centre, (p0 - centre).Normalized(), axis.Cross((p0 - centre).Normalized()), radius);
        bool adopted = false;
        if (curve.Underlying is Circle3d carrier
            && carrier.Center.DistanceTo(centre) <= tolerance
            && Math.Abs(carrier.Radius - radius) <= tolerance
            && Math.Abs(Math.Abs(carrier.Axis.Dot(axis)) - 1) <= Tolerance.Default.Angular)
        {
            circle = carrier;
            adopted = true;
        }

        // Every sample must sit on the circle at the weld tier, or this is some other curve
        // that merely passes through three co-circular points.
        const int samples = 16;
        double startAngle = Angle(p0);
        double previous = startAngle, sweep = 0;
        for (int i = 1; i <= samples; i++)
        {
            var point = curve.PointAt(domain.ParameterAt((double)i / samples));
            var offset = point - circle.Center;
            double height = offset.Dot(circle.Axis);
            if (Math.Abs(height) > tolerance
                || Math.Abs((offset - circle.Axis * height).Length - circle.Radius) > tolerance)
                return false;
            double angle = Angle(point);
            double step = angle - previous;
            // Unwrap: a sampled step can never exceed half a turn at 16 samples per curve.
            if (step > Math.PI)
                step -= 2 * Math.PI;
            else if (step < -Math.PI)
                step += 2 * Math.PI;
            sweep += step;
            previous = angle;
        }

        arc = new CircularArc
        {
            Circle = circle,
            StartAngle = startAngle,
            Sweep = sweep,
            Source = curve,
            FrameAdopted = adopted,
        };
        return true;

        double Angle(in Vector3d point)
        {
            var offset = point - circle.Center;
            return Math.Atan2(offset.Dot(circle.YDirection), offset.Dot(circle.XDirection));
        }
    }
}
