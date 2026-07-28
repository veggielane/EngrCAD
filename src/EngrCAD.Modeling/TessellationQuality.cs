using EngrCAD.BRep;

namespace EngrCAD.Modeling;

/// <summary>
/// Curvature-driven tessellation quality — the adaptive alternative to fixing
/// <see cref="MeshQuality.SegmentsPerCircle"/> by hand. Opt-in via
/// <see cref="MeshQuality.Tessellation"/>: when set, B-Rep display meshes resolve their
/// segment counts from the model's own curvature radii against at most two criteria —
/// a maximum angle per segment (OpenSCAD's <c>$fa</c>) and a maximum chord deviation
/// (sagitta; OCCT's linear deflection) — clamped to
/// [<see cref="MinSegments"/>, <see cref="MaxSegments"/>].
/// <para><b>One criterion drives the display mesh AND the feature-edge overlay.</b>
/// With fixed counts the exact edge overlay (sampled at display resolution, ≥ 96
/// segments per circle) is deliberately finer than the mesh — zoom onto a large rim and
/// the smooth edge visibly detaches from the faceted fill it outlines. Under an
/// adaptive quality both consumers resolve the SAME counts from the same criterion
/// (<see cref="Part.GetMesh(MeshQuality?)"/> and <c>Part.GetFeatureEdges</c>), so they
/// agree by construction at any radius.</para>
/// <para>The chord criterion binds at the model's <i>largest</i> curvature radius (a
/// fixed sagitta needs n ∝ √(r/d) segments), so a 400 mm flange automatically gets the
/// segments a 10 mm boss never needed; the angle criterion is radius-free and acts as
/// the floor for small features. Radii are collected from the solid's circular/elliptic
/// edges and its cylinder/sphere/revolved/extruded/swept surfaces; solids with no
/// curvature resolve to the criterion floor (segment counts are then moot). The SDF
/// route (<see cref="MeshQuality.SdfResolution"/>) is deliberately untouched — grid
/// resolution is not a per-radius quantity.</para>
/// </summary>
public sealed class TessellationQuality
{
    /// <summary>Maximum angle subtended by one segment of a circle, in degrees
    /// (OpenSCAD <c>$fa</c>). Radius-free: 360 / angle segments for every circle.
    /// Null disables the criterion; at least one criterion must be set.</summary>
    public double? MaxAngleDegrees { get; set; }

    /// <summary>Maximum chord deviation (sagitta) of a segment from the true circle, in
    /// model units (OCCT's linear deflection, OpenSCAD's <c>$fs</c> cousin). Binds at
    /// the largest radius: n ≥ π / acos(1 − d/r). Null disables the criterion; at
    /// least one criterion must be set.</summary>
    public double? MaxChordDeviation { get; set; }

    /// <summary>Lower clamp on the resolved segments per circle (default 8).</summary>
    public int MinSegments { get; set; } = 8;

    /// <summary>Upper clamp on the resolved segments per circle (default 512).</summary>
    public int MaxSegments { get; set; } = 512;

    /// <summary>
    /// The number of segments this quality asks of a full circle of the given
    /// <paramref name="radius"/> — THE criterion, shared by every consumer. The angle
    /// criterion contributes 360/angle regardless of radius; the chord criterion
    /// contributes π / acos(1 − d/r) when the deviation is smaller than the radius
    /// (otherwise any polygon satisfies it); the result is clamped to
    /// [<see cref="MinSegments"/>, <see cref="MaxSegments"/>]. A non-positive radius
    /// (no curvature found) resolves from the angle criterion alone.
    /// </summary>
    public int SegmentsFor(double radius)
    {
        Validate();
        double n = MinSegments;
        if (MaxAngleDegrees is { } angle)
            n = Math.Max(n, 360.0 / angle);
        if (MaxChordDeviation is { } deviation && radius > 0 && deviation < radius)
            n = Math.Max(n, Math.PI / Math.Acos(1 - deviation / radius));
        // Epsilon-guard the Ceiling (the helical-sampling lesson: ulp-noise above an
        // integer must not buy a whole extra segment). The count is dimensionless, so
        // the guard is relative — the scale-free tier, not a length tolerance.
        int segments = (int)Math.Ceiling(n * (1 - 1e-12));
        return Math.Clamp(segments, MinSegments, MaxSegments);
    }

    /// <summary>
    /// Resolves (segmentsPerCircle, curveSamples) for one solid: the largest curvature
    /// radius found anywhere in it through <see cref="SegmentsFor"/>. Curve samples
    /// (generic non-circular curves) keep the default quality's 3:4 ratio to the circle
    /// count, floored at 8. Pure — same solid and settings always resolve the same
    /// counts, which is what lets the mesh and the edge overlay call it independently
    /// and still agree.
    /// </summary>
    public (int SegmentsPerCircle, int CurveSamples) ResolveFor(BrepSolid solid)
    {
        ArgumentNullException.ThrowIfNull(solid);
        double maxRadius = 0;
        foreach (var face in solid.Faces)
        {
            Accumulate(SurfaceRadius(face.Surface), ref maxRadius);
            foreach (var loop in face.Loops)
            {
                foreach (var coedge in loop.Coedges)
                    Accumulate(CurveRadius(coedge.Edge.Curve), ref maxRadius);
            }
        }
        int segments = Math.Max(3, SegmentsFor(maxRadius));
        int curveSamples = Math.Max(8, (segments * 3 + 3) / 4);
        return (segments, curveSamples);
    }

    private void Validate()
    {
        if (MaxAngleDegrees is null && MaxChordDeviation is null)
            throw new InvalidOperationException(
                "TessellationQuality needs at least one criterion: set MaxAngleDegrees " +
                "(OpenSCAD $fa) and/or MaxChordDeviation (OCCT linear deflection).");
        if (MaxAngleDegrees is { } angle && (!double.IsFinite(angle) || angle <= 0))
            throw new InvalidOperationException($"MaxAngleDegrees must be positive (got {MaxAngleDegrees}).");
        if (MaxChordDeviation is { } deviation && (!double.IsFinite(deviation) || deviation <= 0))
            throw new InvalidOperationException($"MaxChordDeviation must be positive (got {MaxChordDeviation}).");
        if (MinSegments < 3)
            throw new InvalidOperationException($"MinSegments must be at least 3 (got {MinSegments}).");
        if (MaxSegments < MinSegments)
            throw new InvalidOperationException(
                $"MaxSegments ({MaxSegments}) must be at least MinSegments ({MinSegments}).");
    }

    private static void Accumulate(double? radius, ref double maxRadius)
    {
        if (radius is { } r && r > maxRadius)
            maxRadius = r;
    }

    /// <summary>Curvature radius of an edge curve, when the type states one. Wrappers
    /// recurse; lowering bakes transforms into construction inputs, so a scaled
    /// transformed wrapper is not expected in a lowered solid.</summary>
    private static double? CurveRadius(Curve3d curve) => curve switch
    {
        Circle3d circle => circle.Radius,
        Ellipse3d ellipse => Math.Max(ellipse.SemiAxisX.Length, ellipse.SemiAxisY.Length),
        CurveSegment segment => CurveRadius(segment.Base),
        ReversedCurve reversed => CurveRadius(reversed.Base),
        TransformedCurve transformed => CurveRadius(transformed.Base),
        _ => null,
    };

    /// <summary>Curvature radius a surface's u-grid follows, when the type states one:
    /// a revolve's largest generator-to-axis distance (its rims are circles of that
    /// radius), a cylinder/sphere's radius, an extrusion/sweep's generator radius.</summary>
    private static double? SurfaceRadius(Surface surface) => surface switch
    {
        CylinderSurface cylinder => cylinder.Radius,
        SphereSurface sphere => sphere.Radius,
        RevolvedSurface revolved => Math.Max(
            RevolveRadius(revolved), CurveRadius(revolved.Generator) ?? 0),
        ExtrudedSurface extruded => CurveRadius(extruded.Generator),
        SweptSurface swept => CurveRadius(swept.Generator),
        _ => null,
    };

    private static double RevolveRadius(RevolvedSurface revolved)
    {
        var axisDirection = revolved.AxisDirection.Normalized();
        double max = 0;
        for (int i = 0; i <= 16; i++)
        {
            double t = revolved.Generator.Domain.ParameterAt(i / 16.0);
            var offset = revolved.Generator.PointAt(t) - revolved.AxisOrigin;
            max = Math.Max(max, (offset - axisDirection * offset.Dot(axisDirection)).Length);
        }
        return max;
    }
}
