using EngrCAD.Core;

namespace EngrCAD.Fea;

/// <summary>
/// Per-element geometric measures for tetrahedra: circumsphere, edge lengths, dihedral
/// angles and the shape ratios a quality report is made of.
///
/// <para>Everything here is ordinary floating-point arithmetic, deliberately. These are
/// MEASUREMENTS, not decisions — the topology is settled by <see cref="Predicates3d"/>, and
/// no combinatorial branch in the mesher reads a number from this file. That separation is
/// what lets the quality report be approximate without ever making the mesh wrong.</para>
/// </summary>
public static class TetGeometry
{
    /// <summary>
    /// Circumcentre and circumradius of a tetrahedron. Returns false when the four points
    /// are degenerate relative to their own extent — the scale-free tier, never an absolute
    /// epsilon, because a determinant is a VOLUME and an absolute threshold on one fails
    /// cubically with model scale (the lesson `MeshDecimator` paid for at 1e-5 scale).
    /// </summary>
    public static bool TryCircumcentre(
        in Vector3d a, in Vector3d b, in Vector3d c, in Vector3d d,
        out Vector3d centre, out double radius)
    {
        var ba = b - a;
        var ca = c - a;
        var da = d - a;

        double determinant = ba.Cross(ca).Dot(da);
        double extent = Math.Max(ba.Length, Math.Max(ca.Length, da.Length));
        if (Math.Abs(determinant) <= 1e-13 * extent * extent * extent)
        {
            centre = default;
            radius = 0;
            return false;
        }

        var offset = (ca.Cross(da) * ba.LengthSquared
                    + da.Cross(ba) * ca.LengthSquared
                    + ba.Cross(ca) * da.LengthSquared) / (2.0 * determinant);
        centre = a + offset;
        radius = offset.Length;
        return true;
    }

    /// <summary>Length of the shortest of a tetrahedron's six edges.</summary>
    public static double ShortestEdge(in Vector3d a, in Vector3d b, in Vector3d c, in Vector3d d)
    {
        double shortest = (b - a).Length;
        shortest = Math.Min(shortest, (c - a).Length);
        shortest = Math.Min(shortest, (d - a).Length);
        shortest = Math.Min(shortest, (c - b).Length);
        shortest = Math.Min(shortest, (d - b).Length);
        return Math.Min(shortest, (d - c).Length);
    }

    /// <summary>
    /// Circumradius of a triangle, <c>abc/4A</c>. Returns +infinity for a degenerate one
    /// (relative to its own extent — an area threshold must scale quadratically).
    /// </summary>
    public static double TriangleCircumradius(in Vector3d a, in Vector3d b, in Vector3d c)
    {
        double ab = (b - a).Length, bc = (c - b).Length, ca = (a - c).Length;
        double twiceArea = (b - a).Cross(c - a).Length;
        double extent = Math.Max(ab, Math.Max(bc, ca));
        return twiceArea <= 1e-13 * extent * extent
            ? double.PositiveInfinity
            : ab * bc * ca / (2.0 * twiceArea);
    }

    /// <summary>
    /// Circumcentre and circumradius of a triangle in 3D. The sphere centred there with that
    /// radius is the triangle's <em>diametral ball</em> — the smallest ball containing it —
    /// which is what Delaunay refinement's encroachment rule tests points against. Returns
    /// false for a degenerate triangle (relative to its own extent).
    /// </summary>
    public static bool TryTriangleCircumcentre(
        in Vector3d a, in Vector3d b, in Vector3d c, out Vector3d centre, out double radius)
    {
        var ab = b - a;
        var ac = c - a;
        var normal = ab.Cross(ac);
        double normalSquared = normal.LengthSquared;
        double extent = Math.Max(ab.Length, ac.Length);
        if (normalSquared <= 1e-26 * extent * extent * extent * extent)
        {
            centre = default;
            radius = 0;
            return false;
        }

        var offset = (ac.Cross(normal) * ab.LengthSquared + normal.Cross(ab) * ac.LengthSquared)
                   / (2.0 * normalSquared);
        centre = a + offset;
        radius = offset.Length;
        return true;
    }

    /// <summary>Length of the longest of a tetrahedron's six edges.</summary>
    public static double LongestEdge(in Vector3d a, in Vector3d b, in Vector3d c, in Vector3d d)
    {
        double longest = (b - a).Length;
        longest = Math.Max(longest, (c - a).Length);
        longest = Math.Max(longest, (d - a).Length);
        longest = Math.Max(longest, (c - b).Length);
        longest = Math.Max(longest, (d - b).Length);
        return Math.Max(longest, (d - c).Length);
    }

    /// <summary>
    /// Radius-edge ratio: circumradius over shortest edge. The standard Delaunay-refinement
    /// quality measure — bounded above, it excludes every badly shaped tetrahedron EXCEPT
    /// slivers, which is why a mesher must report minimum dihedral angle as well and never
    /// claim this number alone is quality.
    /// </summary>
    public static double RadiusEdgeRatio(in Vector3d a, in Vector3d b, in Vector3d c, in Vector3d d) =>
        TryCircumcentre(a, b, c, d, out _, out double radius)
            ? radius / ShortestEdge(a, b, c, d)
            : double.PositiveInfinity;

    /// <summary>
    /// The six dihedral angles in radians, in the fixed edge order
    /// (a-b, a-c, a-d, b-c, b-d, c-d).
    ///
    /// <para>Each is computed as <c>atan2(|n1 x n2|, n1 . n2)</c> on RAW (unnormalized) face
    /// normals — exact at any magnitude, with no normalization and no epsilon anywhere. That
    /// is the same measure <c>HoleFiller</c>'s minimum-weight triangulation uses, and it is
    /// chosen for the same reason: a normalized form needs a degeneracy guard, and a
    /// degeneracy guard on a cross product is an AREA threshold.</para>
    /// </summary>
    public static void DihedralAngles(
        in Vector3d a, in Vector3d b, in Vector3d c, in Vector3d d, Span<double> angles)
    {
        if (angles.Length < 6)
            throw new ArgumentException("Need room for six dihedral angles.", nameof(angles));

        // Inward face normals, one per vertex (the face opposite it).
        var na = (c - b).Cross(d - b);   // face bcd
        var nb = (d - a).Cross(c - a);   // face acd
        var nc = (b - a).Cross(d - a);   // face abd
        var nd = (c - a).Cross(b - a);   // face abc

        // The dihedral along an edge is the angle between the two faces sharing it; those
        // are the faces opposite the edge's two NON-endpoints.
        angles[0] = Angle(nc, nd); // edge a-b: faces abd and abc
        angles[1] = Angle(nb, nd); // edge a-c: faces acd and abc
        angles[2] = Angle(nb, nc); // edge a-d: faces acd and abd
        angles[3] = Angle(na, nd); // edge b-c: faces bcd and abc
        angles[4] = Angle(na, nc); // edge b-d: faces bcd and abd
        angles[5] = Angle(na, nb); // edge c-d: faces bcd and acd
    }

    private static double Angle(in Vector3d n1, in Vector3d n2) =>
        Math.PI - Math.Atan2(n1.Cross(n2).Length, n1.Dot(n2));

    /// <summary>The smallest of the six dihedral angles, in radians.</summary>
    public static double MinDihedral(in Vector3d a, in Vector3d b, in Vector3d c, in Vector3d d)
    {
        Span<double> angles = stackalloc double[6];
        DihedralAngles(a, b, c, d, angles);
        double min = angles[0];
        for (int i = 1; i < 6; i++)
            min = Math.Min(min, angles[i]);
        return min;
    }

    /// <summary>The largest of the six dihedral angles, in radians.</summary>
    public static double MaxDihedral(in Vector3d a, in Vector3d b, in Vector3d c, in Vector3d d)
    {
        Span<double> angles = stackalloc double[6];
        DihedralAngles(a, b, c, d, angles);
        double max = angles[0];
        for (int i = 1; i < 6; i++)
            max = Math.Max(max, angles[i]);
        return max;
    }

    /// <summary>
    /// The smallest dihedral angle, in DEGREES, of the element after it has been un-stretched
    /// along its own thinnest principal axis — "given that this element is deliberately thin,
    /// is it otherwise well shaped?".
    ///
    /// <para>The element's second moment about its centroid,
    /// <c>M = sum (p - c)(p - c)^T</c>, has eigenvalues lambda1 &gt;= lambda2 &gt;= lambda3;
    /// the third eigenvector is the thin direction and the element is scaled along it by
    /// <c>sqrt(lambda2 / lambda3)</c>, bringing that extent up to the middle one. <b>The
    /// middle one, not the largest, and not a full whitening.</b> A full whitening
    /// (<c>M^-1/2</c>) maps EVERY non-degenerate tetrahedron to a well-shaped one, so the
    /// number it produces carries no signal at all; scaling only the thin axis restores a
    /// boundary-layer element — which is thin in one direction and healthy in the other two —
    /// while leaving a NEEDLE (long in one direction, thin in two) exactly as bad as it was,
    /// which is the distinction worth keeping.</para>
    ///
    /// <para>Returns 0 for an element whose thinnest extent is zero relative to its largest
    /// (the scale-free tier — an eigenvalue is a squared length, so the guard is squared too).
    /// </para>
    /// </summary>
    public static double UnstretchedMinDihedralDegrees(
        in Vector3d a, in Vector3d b, in Vector3d c, in Vector3d d)
    {
        var centroid = (a + b + c + d) * 0.25;
        Span<Vector3d> offsets = [a - centroid, b - centroid, c - centroid, d - centroid];

        double mxx = 0, myy = 0, mzz = 0, mxy = 0, mxz = 0, myz = 0;
        foreach (var o in offsets)
        {
            mxx += o.X * o.X; myy += o.Y * o.Y; mzz += o.Z * o.Z;
            mxy += o.X * o.Y; mxz += o.X * o.Z; myz += o.Y * o.Z;
        }

        var (values, vectors) = SymmetricEigen3.SolveDescending(mxx, mxy, mxz, myy, myz, mzz);
        double largest = values[0];
        if (!(largest > 0) || values[2] <= 1e-26 * largest)
            return 0;

        double scale = Math.Sqrt(values[1] / values[2]);
        var axis = vectors[2];
        Vector3d Stretch(in Vector3d p)
        {
            var o = p - centroid;
            double along = o.Dot(axis);
            return centroid + o + axis * (along * (scale - 1));
        }

        return MinDihedral(Stretch(a), Stretch(b), Stretch(c), Stretch(d)) * 180.0 / Math.PI;
    }

    /// <summary>
    /// Normalized aspect measure in (0, 1]: <c>(3 * inradius) / circumradius</c>, which is
    /// exactly 1 for a regular tetrahedron and tends to 0 as the element degenerates.
    /// Returns 0 for a flat element.
    /// </summary>
    public static double AspectRatio(in Vector3d a, in Vector3d b, in Vector3d c, in Vector3d d)
    {
        if (!TryCircumcentre(a, b, c, d, out _, out double circumradius) || circumradius <= 0)
            return 0;

        double volume = Math.Abs((b - a).Cross(c - a).Dot(d - a)) / 6.0;
        double area = 0.5 * ((c - b).Cross(d - b).Length
                           + (d - a).Cross(c - a).Length
                           + (b - a).Cross(d - a).Length
                           + (c - a).Cross(b - a).Length);
        if (area <= 0)
            return 0;

        double inradius = 3.0 * volume / area;
        return 3.0 * inradius / circumradius;
    }
}
