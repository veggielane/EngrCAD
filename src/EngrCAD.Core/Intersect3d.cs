namespace EngrCAD.Core;

/// <summary>
/// Ray/primitive intersection in terms of plain points — the companion to
/// <see cref="Distance3d"/>, and here for the same reason: ray-versus-triangle is what
/// every BVH leaf test in this codebase ends up calling (viewport picking, the ambient
/// occlusion bake, hidden-line removal), and it had been written twice already with the
/// two copies differing only in which <c>t</c> range each accepted.
///
/// <para>The split that keeps them honest: the <b>predicate</b> — is the ray's line
/// through the triangle's interior, and at what parameter — belongs here, while the
/// <b>acceptance range</b> belongs to the caller, because that is where the copies
/// legitimately differed (picking rejects self-hits at the origin; an occlusion probe
/// only cares about hits inside its search radius). Returning the raw parameter makes
/// both spellings one line at the call site instead of one algorithm each.</para>
/// </summary>
public static class Intersect3d
{
    /// <summary>
    /// Möller–Trumbore: does the ray's line pass through triangle <c>abc</c>, and at
    /// what parameter? <paramref name="t"/> is in units of the (unnormalized) ray
    /// direction, so a hit at the direction's tip is exactly t = 1 — which is what lets
    /// a caller express a search radius by scaling the direction rather than
    /// normalizing.
    ///
    /// <para><b>t may be negative</b> (a hit behind the origin) and is NOT filtered
    /// here: callers apply their own range. The one thing this does reject is a ray
    /// parallel to the triangle's plane, by a round-off-scale guard on the determinant
    /// — a degeneracy test on a scalar triple product, not a geometric tolerance, and
    /// deliberately not routed through <see cref="Tolerance"/>: an edge-on triangle
    /// missed here costs a click or a shading sample, never a weld.</para>
    /// </summary>
    /// <param name="ray">The ray; direction need not be unit length.</param>
    /// <param name="a">First triangle corner.</param>
    /// <param name="b">Second triangle corner.</param>
    /// <param name="c">Third triangle corner.</param>
    /// <param name="t">Parameter along the ray direction of the hit; 0 on a miss.</param>
    /// <returns>True when the ray's line crosses the triangle's interior or boundary.</returns>
    public static bool RayTriangle(
        in Ray3d ray, in Vector3d a, in Vector3d b, in Vector3d c, out double t)
    {
        t = 0;
        var e1 = b - a;
        var e2 = c - a;
        var p = ray.Direction.Cross(e2);
        double determinant = e1.Dot(p);
        // Round-off-scale parallel-ray guard (see the remarks): an exactly-zero test
        // would leave 0/0 on a degenerate triangle.
        if (Math.Abs(determinant) < 1e-15)
            return false;
        double inverse = 1.0 / determinant;
        var s = ray.Origin - a;
        double u = s.Dot(p) * inverse;
        if (u < 0 || u > 1)
            return false;
        var q = s.Cross(e1);
        double v = ray.Direction.Dot(q) * inverse;
        if (v < 0 || u + v > 1)
            return false;
        t = e2.Dot(q) * inverse;
        return true;
    }
}
