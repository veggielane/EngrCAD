using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// Rigid re-placement of geometry: one curve or surface moved by a proper rigid motion,
/// with every type answered in its OWN family (a moved cylinder is a cylinder, a moved
/// revolve a revolve) rather than behind a wrapper.
///
/// <para><b>Why rigid, and only rigid.</b> The scope is not a caution, it is the exact
/// condition under which this is a re-placement rather than a re-derivation: every
/// parameterization in this kernel is built out of LENGTHS and ANGLES, and an isometry
/// preserves both — so a moved solid's edge domains, seam phases and grid samples are the
/// ones it already had, and nothing downstream has to be re-derived. Widen the map and that
/// stops being true one type at a time, which is why the two natural widenings are refused
/// by name instead of half-supported:</para>
/// <list type="bullet">
/// <item><b>A shear or non-uniform scale</b> changes the surface FAMILY — a sheared cylinder
/// is an elliptic cylinder and a sheared sphere an ellipsoid, neither of which this kernel
/// carries — so there is no answer to give.</item>
/// <item><b>A uniform scale</b> looks admissible and is not, for a reason that lives outside
/// the geometry: <see cref="PolylineCurve3d"/> is parameterized by CUMULATIVE CHORD LENGTH,
/// so scaling its points scales its DOMAIN — while a <see cref="BrepEdge"/> stores its trim
/// domain separately from its curve. Scaling the curve without scaling every domain that
/// refers to it (including the base parameters a <see cref="CurveSegment"/> holds) would
/// desynchronize the two silently, which is exactly the failure mode that is not worth a
/// convenience. Every tracer-produced edge is polyline-backed, so this is the common case
/// rather than an exotic one.</item>
/// <item><b>A reflection</b> is refused because it is not merely a placement: it reverses
/// orientation, so every loop would have to be re-wound to keep outward normals outward, and
/// the types that carry handedness in their arithmetic (<see cref="HelicalSurface"/>, and a
/// <see cref="RevolvedSurface"/>'s axis through the conjugation identity F·Rot(d,φ)·F =
/// Rot(−F·d, φ)) each need their own rule. <c>Shape.Mirror</c> already does all of that
/// correctly one layer up by baking the reflection into construction inputs, so a second
/// route here would be a riskier way to reach an answer the kernel already has.</item>
/// </list>
///
/// <para><b>Curves never refuse.</b> A type with a same-family answer gets it exactly; every
/// other type falls back to <see cref="TransformedCurve"/>, which is exact in position AND
/// in parameterization for any affine map (it maps the evaluated point, so the same t gives
/// the moved point) and whose <see cref="Curve3d.Underlying"/> still reports the base type,
/// so the tessellator's sampling rules are unaffected. Surfaces have no such wrapper, so an
/// unknown surface type is a refusal by name rather than a silent approximation.</para>
/// </summary>
public static class GeometryTransform
{
    /// <summary>
    /// Validates that <paramref name="transform"/> is a proper rigid motion, throwing with
    /// the reason it is not. The gate IS the correctness condition rather than a proxy for
    /// it: <see cref="Matrix4d.TryDecomposeRigidUniformScale"/> already refuses shear,
    /// non-uniform scale and reflection (its handedness test is the triple product of the
    /// columns), so the only thing left to check is that the uniform scale it reports is 1.
    /// </summary>
    /// <param name="transform">The candidate placement.</param>
    /// <param name="what">Named in the exception, so the caller's operation is identifiable.</param>
    public static void RequireRigid(in Matrix4d transform, string what)
    {
        if (!transform.TryDecomposeRigidUniformScale(out _, out _, out double scale))
            throw new NotSupportedException(
                $"{what} needs a proper rigid motion (rotation and translation). This transform is a " +
                "shear, a non-uniform scale or a reflection: a shear changes the surface family (a " +
                "sheared cylinder is an elliptic cylinder, a sheared sphere an ellipsoid), and a " +
                "reflection reverses orientation, so every loop would need re-winding and the " +
                "handedness-carrying types their own rule. Mirror through Shape.Mirror, which bakes " +
                "the reflection into construction inputs instead.");

        // Scale-free comparison: the decomposition's own slack is dimensionless, and so is a
        // ratio against 1.
        if (Math.Abs(scale - 1) > 1e-9)
            throw new NotSupportedException(
                $"{what} needs a rigid motion with no scaling; this one scales by {scale:g6}. A uniform " +
                "scale is refused because PolylineCurve3d is parameterized by cumulative chord length, " +
                "so scaling its points scales its DOMAIN — while a BrepEdge stores its trim domain " +
                "separately from its curve, and a CurveSegment stores base parameters. Scaling the " +
                "curve alone would desynchronize them silently. Scale through the Shape API, which " +
                "bakes the factor into construction inputs.");
    }

    /// <summary>Whether a map is a proper rigid motion — the silent half of
    /// <see cref="RequireRigid"/>, for the places that choose a path rather than refuse.</summary>
    private static bool IsRigid(in Matrix4d transform) =>
        transform.TryDecomposeRigidUniformScale(out _, out _, out double scale)
        && Math.Abs(scale - 1) <= 1e-9;

    /// <summary>
    /// One curve moved. Types with a same-family answer are rebuilt exactly; everything else
    /// is wrapped in a <see cref="TransformedCurve"/>, which is exact in position and
    /// parameterization and keeps its <see cref="Curve3d.Underlying"/> type.
    /// </summary>
    /// <remarks>
    /// The transform is assumed rigid — call <see cref="RequireRigid"/> first. That is what
    /// lets a <see cref="Circle3d"/> keep its radius and pass its transformed axes straight
    /// through: the class's <c>ArcLength</c> is <c>(to − from) · radius</c>, which is only
    /// true while the stored axes are UNIT, and an isometry is exactly the condition that
    /// keeps them so.
    /// </remarks>
    public static Curve3d Apply(Curve3d curve, in Matrix4d transform)
    {
        ArgumentNullException.ThrowIfNull(curve);
        var m = transform;
        return curve switch
        {
            Line3d line => new Line3d(m.TransformPoint(line.Start), m.TransformPoint(line.End)),

            Circle3d circle => new Circle3d(
                m.TransformPoint(circle.Center),
                m.TransformVector(circle.XDirection),
                m.TransformVector(circle.YDirection),
                circle.Radius),

            Ellipse3d ellipse => new Ellipse3d(
                m.TransformPoint(ellipse.Center),
                m.TransformVector(ellipse.SemiAxisX),
                m.TransformVector(ellipse.SemiAxisY)),

            // Exact for ANY affine map: a rational curve is an affine combination of its
            // control points at every parameter (Σ wN(MP + b) / Σ wN = M·C + b), so moving
            // the control points with weights and knots untouched IS the moved curve.
            NurbsCurve nurbs => new NurbsCurve(
                nurbs.Degree,
                [.. nurbs.ControlPoints.Select(p => m.TransformPoint(p))],
                nurbs.Weights,
                nurbs.Knots),

            // Chord lengths are preserved by an isometry, so the cumulative parameterization
            // — and therefore every edge domain that refers to it — is unchanged. The tracer
            // carriers move with the points: they are either face surfaces (whose own
            // transform succeeds, or the solid walk has already refused) or promoted
            // cylinders/spheres, all of which Apply answers in-family.
            PolylineCurve3d polyline => new PolylineCurve3d(
                [.. polyline.Points.Select(p => m.TransformPoint(p))], polyline.IsClosed,
                polyline.Carriers is { } pair ? (Apply(pair.A, m), Apply(pair.B, m)) : null),

            ReversedCurve reversed => Apply(reversed.Base, m).Reversed(),

            // Fold rather than nest: the moved curve of a moved curve is one placement, and
            // the order matters (the inner transform runs first). But the fold is only
            // legitimate while the COMBINED map is still rigid — a wrapper may itself carry
            // a scale, and folding that into a Circle3d would hand it non-unit axes beside
            // an unscaled radius, which evaluates correctly and reports the wrong arc
            // length. So the gate IS the correctness condition, checked on the product
            // rather than assumed from the outer map; when it fails the wrapper simply
            // nests, which stays exact.
            TransformedCurve wrapped when IsRigid(m * wrapped.Transform) =>
                Apply(wrapped.Base, m * wrapped.Transform),

            // The trim is stated in the BASE curve's parameters, which an isometry leaves
            // alone, so the segment's own mapping carries over verbatim.
            CurveSegment segment => new CurveSegment(
                Apply(segment.Base, m), segment.BaseStart, segment.BaseEnd),

            _ => curve.Transformed(m),
        };
    }

    /// <summary>
    /// One surface moved, answered in its own family. Refuses an unrecognized surface type
    /// by name — there is no surface wrapper to fall back on, and a silent approximation
    /// here would be geometry that is merely near the right place.
    /// </summary>
    /// <remarks>
    /// The transform is assumed rigid (call <see cref="RequireRigid"/> first), which is what
    /// makes each of these a re-placement: a plane's frame stays orthonormal, a revolve's
    /// axis stays unit, and a sweep's rotation-minimizing frames are intrinsic to the path
    /// and so come out of the recomputation already moved.
    /// </remarks>
    public static Surface Apply(Surface surface, in Matrix4d transform)
    {
        ArgumentNullException.ThrowIfNull(surface);
        var m = transform;
        switch (surface)
        {
            case PlaneSurface plane:
                return new PlaneSurface(
                    m.TransformPoint(plane.Origin),
                    m.TransformVector(plane.XDirection),
                    m.TransformVector(plane.YDirection));

            case CylinderSurface cylinder:
                return new CylinderSurface(
                    m.TransformPoint(cylinder.Origin),
                    m.TransformVector(cylinder.XDirection),
                    m.TransformVector(cylinder.YDirection),
                    cylinder.Radius);

            // A sphere carries no frame — its u/v are measured against the WORLD axes — so a
            // moved sphere is the same class of object at a new centre, re-parameterized.
            // That is sound because a face's trim loops are pulled back from its 3D edge
            // curves at use, never stored in uv.
            case SphereSurface sphere:
                return new SphereSurface(m.TransformPoint(sphere.Center), sphere.Radius);

            case NurbsSurface nurbs:
            {
                int countU = nurbs.ControlPoints.GetLength(0);
                int countV = nurbs.ControlPoints.GetLength(1);
                var points = new Vector3d[countU, countV];
                for (int i = 0; i < countU; i++)
                {
                    for (int j = 0; j < countV; j++)
                        points[i, j] = m.TransformPoint(nurbs.ControlPoints[i, j]);
                }
                return new NurbsSurface(
                    nurbs.DegreeU, nurbs.DegreeV, points, nurbs.Weights, nurbs.KnotsU, nurbs.KnotsV);
            }

            case ExtrudedSurface extruded:
                return new ExtrudedSurface(
                    Apply(extruded.Generator, m), m.TransformVector(extruded.Direction));

            case RevolvedSurface revolved:
                return new RevolvedSurface(
                    Apply(revolved.Generator, m),
                    m.TransformPoint(revolved.AxisOrigin),
                    m.TransformVector(revolved.AxisDirection),
                    revolved.Angle);

            // Rotation-minimizing transport is intrinsic to the path, so sweeping the moved
            // profile along the moved path IS the moved sweep — the frames need no fixing,
            // they simply come out moved. The frame COUNT is part of the surface's identity
            // (every interior frame is interpolated between the computed ones), so it is
            // carried rather than defaulted, and StartX is fed back from what the original
            // STORED, where re-projecting an already-perpendicular unit vector is a no-op.
            case SweptSurface swept:
                return new SweptSurface(
                    Apply(swept.Generator, m),
                    Apply(swept.Path, m),
                    m.TransformVector(swept.StartX),
                    swept.FrameCount);

            // A loft is an affine combination of its section points at every (u, v) — the
            // cardinal basis is a partition of unity — so moving the sections moves the
            // surface exactly. The section PARAMETERS are passed through rather than
            // recomputed: they are derived from mean chord length, which an isometry
            // preserves, and re-deriving them would reach the same numbers by a different
            // route and so only in the last bits.
            case LoftedSurface lofted:
                return new LoftedSurface(
                    [.. lofted.Sections.Select(s => Apply(s, m))], lofted.SectionParameters);

            // The frame is orthonormal and stays so under an isometry, so it is rebuilt
            // through the one factory that stores its axes VERBATIM — re-deriving them would
            // move them by an ulp, the AxisRef rule. Profile and pitch are lengths measured
            // in that frame and are untouched.
            case HelicalSurface helical:
                return new HelicalSurface(
                    Frame3d.FromOrthonormal(
                        m.TransformPoint(helical.Frame.Origin),
                        m.TransformVector(helical.Frame.X),
                        m.TransformVector(helical.Frame.Y)),
                    helical.ProfileStart,
                    helical.ProfileEnd,
                    helical.Pitch,
                    helical.DomainU);

            default:
                throw new NotSupportedException(
                    $"A {surface.GetType().Name} face cannot be re-placed: there is no rule for moving " +
                    "that surface type in its own family, and unlike a curve there is no exact wrapper " +
                    "to fall back on. Add a case to GeometryTransform.Apply rather than approximating.");
        }
    }
}
