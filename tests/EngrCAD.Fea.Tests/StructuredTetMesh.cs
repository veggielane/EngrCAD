using EngrCAD.Core;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Structured tetrahedral meshes for solver VERIFICATION — a grid of boxes, each split
/// into six tetrahedra by Kuhn's subdivision.
///
/// <para><b>Why a fixture rather than <see cref="TetMesher"/>.</b> A verification test
/// must isolate what it is verifying. Kuhn's subdivision gives elements of bounded quality
/// (every one is a corner of the same reference simplex, so the shape is identical up to
/// the grid's own anisotropy), a refinement sequence that is exactly geometrically similar
/// — which is what makes a measured convergence ORDER mean anything — and box-face tags a
/// boundary condition can name. Measured on a 100 x 10 x 10 beam, the Delaunay mesher at
/// a 5.0 size target returns 31 214 elements of which 9 954 are slivers below 10 degrees,
/// two with a non-positive floating-point volume, and a minimum dihedral of 0.000 degrees;
/// a stiffness matrix assembled from that is not numerically positive definite and the
/// factorization fails. That is the sliver-removal gap the mesher's own README and
/// todo.md already name, and it belongs in the mesher's tests, not in the middle of a
/// convergence table.</para>
///
/// <para><b>Kuhn's subdivision is conforming.</b> The six tetrahedra are the regions
/// <c>0 &lt;= x_p(1) &lt;= x_p(2) &lt;= x_p(3) &lt;= 1</c> over the six permutations p, so
/// each face of the cube is split by a diagonal decided by coordinate ORDER alone. Two
/// neighbouring cubes are translates of one another and therefore agree on the shared
/// face's diagonal with nothing to negotiate — the same reason the tessellator's shared
/// edge polylines weld.</para>
/// </summary>
internal static class StructuredTetMesh
{
    /// <summary>Tag of the face at the low end of X.</summary>
    public const int XMin = 0;

    /// <summary>Tag of the face at the high end of X.</summary>
    public const int XMax = 1;

    /// <summary>Tag of the face at the low end of Y.</summary>
    public const int YMin = 2;

    /// <summary>Tag of the face at the high end of Y.</summary>
    public const int YMax = 3;

    /// <summary>Tag of the face at the low end of Z.</summary>
    public const int ZMin = 4;

    /// <summary>Tag of the face at the high end of Z.</summary>
    public const int ZMax = 5;

    /// <summary>Faces of a positively oriented tetrahedron, wound OUTWARD; face i is
    /// opposite vertex i (the same table <see cref="TetMesh"/> uses internally).</summary>
    private static readonly int[][] FaceTable =
    [
        [1, 2, 3],
        [0, 3, 2],
        [0, 1, 3],
        [0, 2, 1],
    ];

    /// <summary>The six Kuhn tetrahedra of a unit cube, by corner bit-index
    /// (bit 0 = x, bit 1 = y, bit 2 = z).</summary>
    private static readonly int[][] KuhnTets =
    [
        [0, 1, 3, 7],
        [0, 1, 5, 7],
        [0, 2, 3, 7],
        [0, 2, 6, 7],
        [0, 4, 5, 7],
        [0, 4, 6, 7],
    ];

    /// <summary>A box from <paramref name="origin"/> spanning <paramref name="size"/>,
    /// divided into <paramref name="nx"/> x <paramref name="ny"/> x <paramref name="nz"/>
    /// cells of six tetrahedra each. Boundary facets carry the XMin..ZMax tags.</summary>
    public static TetMesh Box(Vector3d origin, Vector3d size, int nx, int ny, int nz) =>
        Box(origin, size, nx, ny, nz, null);

    /// <summary><see cref="Box(Vector3d, Vector3d, int, int, int)"/> with a material region
    /// id chosen per element from its centroid.</summary>
    public static TetMesh Box(
        Vector3d origin, Vector3d size, int nx, int ny, int nz, Func<Vector3d, int>? regionOf)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(nx, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(ny, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(nz, 1);

        int strideY = nx + 1;
        int strideZ = strideY * (ny + 1);
        var positions = new Vector3d[strideZ * (nz + 1)];
        for (int k = 0; k <= nz; k++)
        {
            for (int j = 0; j <= ny; j++)
            {
                for (int i = 0; i <= nx; i++)
                {
                    positions[i + j * strideY + k * strideZ] = origin + new Vector3d(
                        size.X * i / nx, size.Y * j / ny, size.Z * k / nz);
                }
            }
        }

        var tets = new List<int>(nx * ny * nz * 24);
        Span<int> corner = stackalloc int[8];
        for (int k = 0; k < nz; k++)
        {
            for (int j = 0; j < ny; j++)
            {
                for (int i = 0; i < nx; i++)
                {
                    for (int c = 0; c < 8; c++)
                    {
                        corner[c] = (i + (c & 1))
                                  + (j + ((c >> 1) & 1)) * strideY
                                  + (k + ((c >> 2) & 1)) * strideZ;
                    }
                    foreach (var kuhn in KuhnTets)
                    {
                        int a = corner[kuhn[0]], b = corner[kuhn[1]];
                        int c2 = corner[kuhn[2]], d = corner[kuhn[3]];
                        if (Predicates3d.SignedVolume6Sign(
                                positions[a], positions[b], positions[c2], positions[d]) < 0)
                            (c2, d) = (d, c2);
                        tets.Add(a);
                        tets.Add(b);
                        tets.Add(c2);
                        tets.Add(d);
                    }
                }
            }
        }

        var boundary = BoundaryFacets(positions, tets, origin, size);
        var regions = new int[tets.Count / 4];
        if (regionOf is not null)
        {
            for (int t = 0; t < regions.Length; t++)
            {
                var centroid = (positions[tets[4 * t]] + positions[tets[4 * t + 1]]
                              + positions[tets[4 * t + 2]] + positions[tets[4 * t + 3]]) * 0.25;
                regions[t] = regionOf(centroid);
            }
        }
        return new TetMesh([.. positions], [.. tets], regions, boundary);
    }

    /// <summary>The same box centred on the origin.</summary>
    public static TetMesh CenteredBox(Vector3d size, int nx, int ny, int nz) =>
        Box(-size * 0.5, size, nx, ny, nz);

    /// <summary>Tag of the hole's cylindrical surface in <see cref="PlateWithHole"/>.</summary>
    public const int Hole = 6;

    /// <summary>A one-element mesh from four corners, each face its own tag — the fixture
    /// for element-level refusals.</summary>
    public static TetMesh SingleTet(Vector3d[] corners)
    {
        var facets = new TetFacet[4];
        for (int f = 0; f < 4; f++)
            facets[f] = new TetFacet(FaceTable[f][0], FaceTable[f][1], FaceTable[f][2], 0, f);
        return new TetMesh([.. corners], [0, 1, 2, 3], [0], facets);
    }

    /// <summary>
    /// A rectangular plate, centred on the origin in x and y and spanning
    /// [0, thickness] in z, with a central circular hole — an O-grid: every node sits on a
    /// ray from the axis, the inner ring is the hole and the outer ring is the rectangle's
    /// own boundary.
    ///
    /// <para><b>The four corner angles are inserted into the angular list</b>, replacing
    /// the nearest uniform angle. Without them the outer ring is a polygon INSCRIBED in the
    /// rectangle and each corner is chopped off — measured at 64 uniform angles, a triangle
    /// 11.7 by 1.8 units at each corner of a 120 x 40 plate. The insertion costs a little
    /// local non-uniformity exactly where the stress is uniform and nothing depends on
    /// it.</para>
    ///
    /// <para>Radial spacing is graded as <c>(j/n)^grading</c>, so cells cluster at the
    /// hole where the gradient is.</para>
    /// </summary>
    public static TetMesh PlateWithHole(
        double halfLength, double halfWidth, double thickness, double holeRadius,
        int nTheta, int nRadial, int nZ, double grading = 1.6)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(nTheta, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(nRadial, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(nZ, 1);
        if (!(holeRadius > 0 && holeRadius < halfWidth))
            throw new ArgumentOutOfRangeException(nameof(holeRadius));

        var angles = Angles(halfLength, halfWidth, nTheta);
        int nt = angles.Length;
        int ringStride = nt;
        int layerStride = nt * (nRadial + 1);

        var positions = new Vector3d[layerStride * (nZ + 1)];
        for (int k = 0; k <= nZ; k++)
        {
            double z = thickness * k / nZ;
            for (int j = 0; j <= nRadial; j++)
            {
                double s = Math.Pow((double)j / nRadial, grading);
                for (int i = 0; i < nt; i++)
                {
                    double c = Math.Cos(angles[i]), sn = Math.Sin(angles[i]);
                    double outer = RayToRectangle(c, sn, halfLength, halfWidth);
                    double r = holeRadius + (outer - holeRadius) * s;
                    positions[i + j * ringStride + k * layerStride] = new Vector3d(r * c, r * sn, z);
                }
            }
        }

        var tets = new List<int>(nt * nRadial * nZ * 24);
        Span<int> corner = stackalloc int[8];
        for (int k = 0; k < nZ; k++)
        {
            for (int j = 0; j < nRadial; j++)
            {
                for (int i = 0; i < nt; i++)
                {
                    int iNext = (i + 1) % nt;
                    for (int c = 0; c < 8; c++)
                    {
                        int ii = (c & 1) == 0 ? i : iNext;
                        int jj = j + ((c >> 1) & 1);
                        int kk = k + ((c >> 2) & 1);
                        corner[c] = ii + jj * ringStride + kk * layerStride;
                    }
                    foreach (var kuhn in KuhnTets)
                    {
                        int a = corner[kuhn[0]], b = corner[kuhn[1]];
                        int c2 = corner[kuhn[2]], d = corner[kuhn[3]];
                        if (Predicates3d.SignedVolume6Sign(
                                positions[a], positions[b], positions[c2], positions[d]) < 0)
                            (c2, d) = (d, c2);
                        tets.Add(a);
                        tets.Add(b);
                        tets.Add(c2);
                        tets.Add(d);
                    }
                }
            }
        }

        var boundary = PlateBoundary(positions, tets, halfLength, halfWidth, thickness, holeRadius);
        return new TetMesh([.. positions], [.. tets], new int[tets.Count / 4], boundary);
    }

    /// <summary>Uniform angles with the rectangle's four corner angles substituted in.</summary>
    private static double[] Angles(double halfLength, double halfWidth, int nTheta)
    {
        var list = new List<double>(nTheta + 4);
        for (int i = 0; i < nTheta; i++)
            list.Add(2.0 * Math.PI * i / nTheta);

        double corner = Math.Atan2(halfWidth, halfLength);
        double[] corners = [corner, Math.PI - corner, Math.PI + corner, 2 * Math.PI - corner];
        foreach (double c in corners)
        {
            int nearest = 0;
            double best = double.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                double d = Math.Abs(list[i] - c);
                if (d < best)
                {
                    best = d;
                    nearest = i;
                }
            }
            list[nearest] = c;
        }
        list.Sort();
        return [.. list];
    }

    /// <summary>Distance from the origin to the rectangle's boundary along (c, s).</summary>
    private static double RayToRectangle(double c, double s, double halfLength, double halfWidth)
    {
        double byX = Math.Abs(c) > 0 ? halfLength / Math.Abs(c) : double.MaxValue;
        double byY = Math.Abs(s) > 0 ? halfWidth / Math.Abs(s) : double.MaxValue;
        return Math.Min(byX, byY);
    }

    private static TetFacet[] PlateBoundary(
        Vector3d[] positions, List<int> tets,
        double halfLength, double halfWidth, double thickness, double holeRadius)
    {
        int count = tets.Count / 4;
        var uses = new Dictionary<(int, int, int), int>();
        for (int t = 0; t < count; t++)
        {
            for (int f = 0; f < 4; f++)
            {
                var key = SortedKey(
                    tets[4 * t + FaceTable[f][0]],
                    tets[4 * t + FaceTable[f][1]],
                    tets[4 * t + FaceTable[f][2]]);
                uses[key] = uses.GetValueOrDefault(key) + 1;
            }
        }

        double tolerance = (halfLength + halfWidth) * 1e-9;
        var facets = new List<TetFacet>();
        for (int t = 0; t < count; t++)
        {
            for (int f = 0; f < 4; f++)
            {
                int v0 = tets[4 * t + FaceTable[f][0]];
                int v1 = tets[4 * t + FaceTable[f][1]];
                int v2 = tets[4 * t + FaceTable[f][2]];
                if (uses[SortedKey(v0, v1, v2)] != 1)
                    continue;
                var p = (positions[v0] + positions[v1] + positions[v2]) / 3.0;
                int tag;
                if (Math.Abs(p.Z) <= tolerance) tag = ZMin;
                else if (Math.Abs(p.Z - thickness) <= tolerance) tag = ZMax;
                else if (Math.Abs(p.X + halfLength) <= tolerance) tag = XMin;
                else if (Math.Abs(p.X - halfLength) <= tolerance) tag = XMax;
                else if (Math.Abs(Math.Abs(p.Y) - halfWidth) <= tolerance) tag = p.Y > 0 ? YMax : YMin;
                else if (Math.Abs(Math.Sqrt(p.X * p.X + p.Y * p.Y) - holeRadius)
                         <= holeRadius * 0.2) tag = Hole;
                else throw new InvalidOperationException($"boundary facet at {p} matches no face");
                facets.Add(new TetFacet(v0, v1, v2, t, tag));
            }
        }
        return [.. facets];
    }

    private static TetFacet[] BoundaryFacets(
        Vector3d[] positions, List<int> tets, Vector3d origin, Vector3d size)
    {
        int count = tets.Count / 4;
        var usage = new Dictionary<(int, int, int), (int Tet, int Face, int Uses)>();
        for (int t = 0; t < count; t++)
        {
            for (int f = 0; f < 4; f++)
            {
                var key = SortedKey(
                    tets[4 * t + FaceTable[f][0]],
                    tets[4 * t + FaceTable[f][1]],
                    tets[4 * t + FaceTable[f][2]]);
                usage[key] = usage.TryGetValue(key, out var existing)
                    ? (existing.Tet, existing.Face, existing.Uses + 1)
                    : (t, f, 1);
            }
        }

        var facets = new List<TetFacet>();
        for (int t = 0; t < count; t++)
        {
            for (int f = 0; f < 4; f++)
            {
                int v0 = tets[4 * t + FaceTable[f][0]];
                int v1 = tets[4 * t + FaceTable[f][1]];
                int v2 = tets[4 * t + FaceTable[f][2]];
                if (usage[SortedKey(v0, v1, v2)].Uses != 1)
                    continue;
                var centroid = (positions[v0] + positions[v1] + positions[v2]) / 3.0;
                facets.Add(new TetFacet(v0, v1, v2, t, TagOf(centroid, origin, size)));
            }
        }
        return [.. facets];
    }

    private static int TagOf(Vector3d p, Vector3d origin, Vector3d size)
    {
        double tolerance = size.Length * 1e-12;
        if (Math.Abs(p.X - origin.X) <= tolerance) return XMin;
        if (Math.Abs(p.X - origin.X - size.X) <= tolerance) return XMax;
        if (Math.Abs(p.Y - origin.Y) <= tolerance) return YMin;
        if (Math.Abs(p.Y - origin.Y - size.Y) <= tolerance) return YMax;
        if (Math.Abs(p.Z - origin.Z) <= tolerance) return ZMin;
        if (Math.Abs(p.Z - origin.Z - size.Z) <= tolerance) return ZMax;
        throw new InvalidOperationException($"boundary facet at {p} is on no box face");
    }

    private static (int, int, int) SortedKey(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }
}
