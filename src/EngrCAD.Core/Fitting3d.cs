namespace EngrCAD.Core;

/// <summary>
/// Oriented 3D box: a rigid <see cref="Frame3d"/> (center + unit axes) with half
/// extents along the frame axes. Produced by <see cref="Fitting3d.FitBox"/>.
/// </summary>
public readonly struct OrientedBox3d
{
    public Frame3d Frame { get; }
    /// <summary>Half sizes along (Frame.X, Frame.Y, Frame.Z).</summary>
    public Vector3d HalfExtents { get; }

    public OrientedBox3d(in Frame3d frame, in Vector3d halfExtents)
    {
        Frame = frame;
        HalfExtents = halfExtents;
    }

    public Vector3d Center => Frame.Origin;
    public double Volume => 8 * HalfExtents.X * HalfExtents.Y * HalfExtents.Z;

    /// <summary>Corner i ∈ [0, 8): bit 0 → +X/−X, bit 1 → +Y/−Y, bit 2 → +Z/−Z.</summary>
    public Vector3d Corner(int i) => Frame.ToWorld(new Vector3d(
        (i & 1) == 0 ? HalfExtents.X : -HalfExtents.X,
        (i & 2) == 0 ? HalfExtents.Y : -HalfExtents.Y,
        (i & 4) == 0 ? HalfExtents.Z : -HalfExtents.Z));

    public bool Contains(in Vector3d point, in Tolerance tolerance)
    {
        var local = Frame.ToLocal(point);
        return Math.Abs(local.X) <= HalfExtents.X + tolerance.Linear &&
               Math.Abs(local.Y) <= HalfExtents.Y + tolerance.Linear &&
               Math.Abs(local.Z) <= HalfExtents.Z + tolerance.Linear;
    }
}

/// <summary>
/// Point-cloud fits in 3D, built on a covariance eigen-analysis (cyclic Jacobi on the
/// symmetric 3×3): PCA oriented bounding box and least-squares (total least squares /
/// orthogonal-distance) plane. geometry3Sharp counterparts: ContOrientedBox3 /
/// GaussPointsFit3, OrthogonalPlaneFit3.
/// </summary>
public static class Fitting3d
{
    /// <summary>
    /// Best-fit plane through a point cloud, minimizing orthogonal distances, returned
    /// as a <see cref="Frame3d"/>: origin at the centroid, Z the plane normal
    /// (smallest covariance eigenvector), X the dominant in-plane spread direction
    /// (largest eigenvector) — a natural, deterministic in-plane basis rather than an
    /// arbitrary perpendicular. Throws when the points do not determine a plane
    /// (fewer than 3, coincident, or collinear).
    /// </summary>
    public static Frame3d FitPlane(IReadOnlyList<Vector3d> points)
    {
        var (centroid, values, vectors) = CovarianceEigen(points);
        // Relative eigenvalue degeneracy test (eigenvalues are in length² units and scale
        // with the cloud, so the model-unit Tolerance.Linear does not apply); 1e-300
        // only guards the all-points-coincident case against 0 * 1e-12 = 0.
        if (points.Count < 3 || values[1] <= 1e-12 * Math.Max(values[0], 1e-300))
            throw new ArgumentException(
                "The points do not determine a plane (coincident or collinear).", nameof(points));
        // vectors[0] = largest spread (X), vectors[1] = mid (Y); FromXY makes Z = X × Y,
        // which is ± the smallest eigenvector — the plane normal either way.
        return Frame3d.FromXY(centroid, vectors[0], vectors[1]);
    }

    /// <summary>
    /// PCA oriented bounding box: covariance eigenvectors give the axes, and the box is
    /// shrunk to contain the exact projections onto them (center re-fit, so it is the
    /// tightest box WITH those axes). Degenerate clouds are fine — flat or collinear sets
    /// just get zero extents along the empty directions. Throws on an empty input.
    ///
    /// <para><b>This is a heuristic, not the minimum-volume box</b>, and the gap is not
    /// small: PCA orients by how the points are DISTRIBUTED, so a dense face and a sparse
    /// one pull the axes differently even when the shape is identical, and a box tilted
    /// 45° with many samples along one diagonal comes out visibly wrong. Callers that need
    /// a guaranteed-minimal box (packing, print-bed fitting, stock selection) must not
    /// assume this one is it. g3's <c>ContOrientedBox3</c> ships the same compromise.</para>
    ///
    /// <para>For the true minimum-volume box use <see cref="MinVolumeBox"/>, which needs
    /// the cloud's convex hull.</para>
    /// </summary>
    public static OrientedBox3d FitBox(IReadOnlyList<Vector3d> points)
    {
        var (_, _, vectors) = CovarianceEigen(points);
        var x = vectors[0];
        var y = vectors[1];
        var z = x.Cross(y); // right-handed; ± the third eigenvector

        var min = new Vector3d(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var max = -min;
        foreach (var p in points)
        {
            var projected = new Vector3d(p.Dot(x), p.Dot(y), p.Dot(z));
            min = Vector3d.Min(min, projected);
            max = Vector3d.Max(max, projected);
        }

        var mid = (min + max) * 0.5;
        var center = x * mid.X + y * mid.Y + z * mid.Z;
        return new OrientedBox3d(Frame3d.FromOrthonormal(center, x, y), (max - min) * 0.5);
    }

    /// <summary>
    /// Minimum-volume oriented bounding box of a convex hull.
    /// <para>
    /// <b>The 2D calipers theorem does not lift to 3D.</b> In the plane the minimum-area
    /// rectangle has a side collinear with a hull edge, and it is tempting (Freeman–Shapira,
    /// 1975, and a great many implementations since) to assume the minimum-volume box has a
    /// FACE flush with a hull face. It does not: the regular tetrahedron on alternate
    /// corners of the cube [−1, 1]³ has axis-aligned box volume 8, while EVERY face-flush
    /// candidate measures 16 — the equilateral section's minimum rectangle a²√3/2 = 6.93
    /// times the height 4/√3 = 2.31. The cube touches each tetrahedron vertex at a corner
    /// and is flush with no face at all. That counterexample is locked by a test.
    /// </para>
    /// <para>
    /// What is true is O'Rourke's characterization (1985): the minimum-volume box has at
    /// least two ADJACENT faces, each containing a hull EDGE. So the search runs over pairs
    /// of hull edge directions (u, v): a face normal n₁ ⊥ u leaves one rotational degree of
    /// freedom about u, and n₂ ∝ n₁ × v is then forced, giving a one-parameter family per
    /// pair whose volume is swept and refined. Face-flush boxes are a special case (a face
    /// contains two of its own edges) but are also evaluated directly, where the exact 2D
    /// calipers apply; the PCA and axis-aligned boxes seed the search, so the result can
    /// never lose to <see cref="FitBox"/>.
    /// </para>
    /// </summary>
    /// <param name="hullVertices">The convex hull's vertices. The returned box contains
    /// exactly these points, so it contains the original cloud whenever they really are its
    /// hull (a hull's convex closure is the cloud's).</param>
    /// <param name="hullTriangles">The hull's triangles as flat vertex-index triples.
    /// Only the plane and the edge directions of each are used, so winding sense and
    /// duplicate coplanar triangles do not matter.</param>
    /// <remarks>
    /// <para>
    /// The hull is the CALLER's to supply — deliberately. EngrCAD.Core owns no polyhedron
    /// type and must not learn one: the 3D quickhull lives in EngrCAD.Mesh because it
    /// speaks <c>HalfEdgeMesh</c>, and pulling it down here would mean either dragging
    /// half-edge topology into the dependency-free foundation or splitting the algorithm
    /// away from its own robustness contract. Taking the hull as plain data instead lets
    /// any source feed it — <c>ConvexHull.Compute(points).ToIndexed()</c>, a B-Rep solid's
    /// planar faces, or a hull the caller already computed for something else.
    /// </para>
    /// <para>
    /// Cost is O(E² · h) in the number of DISTINCT hull edge directions E and hull vertices
    /// h — quadratic in the hull by the nature of O'Rourke's characterization. Measured on
    /// an 8-core machine over the edge loop (parallelized, deterministic by per-index
    /// slots + an in-order reduction): 3.6 ms for an 18-vertex hull, 22 ms for 42 vertices,
    /// 122 ms for 78. Feed it a HULL, never a raw cloud, and simplify hulls with thousands
    /// of distinct edge directions first. The per-family angle is found by a sweep plus
    /// golden-section refinement rather than an algebraic root solve, so the answer is the
    /// true minimum unless a family's optimum hides in a bracket narrower than the sweep;
    /// the box always contains every supplied point regardless.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The hull has no triangle spanning a plane
    /// (empty, or every triangle degenerate) — a flat or collinear cloud has no candidate
    /// family. Use <see cref="FitBox"/>, or <see cref="FitPlane"/> plus
    /// <see cref="Fitting2d.MinAreaBox"/> within that plane.</exception>
    public static OrientedBox3d MinVolumeBox(
        IReadOnlyList<Vector3d> hullVertices, ReadOnlySpan<int> hullTriangles)
    {
        ArgumentNullException.ThrowIfNull(hullVertices);
        if (hullVertices.Count == 0)
            throw new ArgumentException("Cannot fit an empty point set.", nameof(hullVertices));
        if (hullTriangles.Length % 3 != 0)
            throw new ArgumentException(
                "Hull triangles must come as flat vertex-index triples.", nameof(hullTriangles));

        var normals = DistinctDirections(FaceNormals(hullVertices, hullTriangles));
        if (normals.Count == 0)
            throw new ArgumentException(
                "No hull triangle spans a plane, so there is no candidate box orientation. " +
                "Flat or collinear clouds have none: use Fitting3d.FitBox, or " +
                "Fitting3d.FitPlane + Fitting2d.MinAreaBox within that plane.",
                nameof(hullTriangles));

        var points = new Vector3d[hullVertices.Count];
        for (int i = 0; i < points.Length; i++)
            points[i] = hullVertices[i];

        // Seeds that are already valid boxes, so the search can only improve on them.
        var best = FitBox(hullVertices);
        double bestVolume = best.Volume;
        Consider(points, Vector3d.UnitX, Vector3d.UnitY, ref best, ref bestVolume);

        // Face-flush family, evaluated exactly: within a face's plane the 2D calipers
        // theorem DOES apply, so the section is the exact minimum-area rectangle.
        var projected = new Vector2d[points.Length];
        foreach (var normal in normals)
        {
            var frame = Frame3d.FromNormal(Vector3d.Zero, normal);
            for (int i = 0; i < points.Length; i++)
                projected[i] = new Vector2d(points[i].Dot(frame.X), points[i].Dot(frame.Y));
            var section = Fitting2d.MinAreaBox(projected);
            var axisX = frame.X * section.AxisX.X + frame.Y * section.AxisX.Y;
            var axisY = frame.X * section.AxisY.X + frame.Y * section.AxisY.Y;
            Consider(points, axisX, axisY, ref best, ref bestVolume);
        }

        // O'Rourke family: two adjacent box faces each contain a hull edge. Only j > i is
        // needed — family (i, j) and (j, i) describe the SAME set of orientations with the
        // two axes swapped, so the pair is unordered and half the work is redundant.
        var edges = DistinctDirections(EdgeDirections(hullVertices, hullTriangles));
        var perEdge = new (double Volume, OrientedBox3d Box)[edges.Count];
        ParallelFor.Blocks(0, edges.Count, (start, end) =>
        {
            for (int i = start; i < end; i++)
            {
                var basis = Frame3d.FromNormal(Vector3d.Zero, edges[i]);   // X, Y ⊥ edges[i]
                var local = (Volume: double.PositiveInfinity, Box: default(OrientedBox3d));
                for (int j = i + 1; j < edges.Count; j++)
                {
                    var candidate = MinimizeOverFamily(points, basis, edges[j]);
                    if (candidate.Volume < local.Volume)
                        local = candidate;
                }
                perEdge[i] = local;
            }
        });
        // Each index wrote only its own slot and the reduction runs in index order, so the
        // answer does not depend on scheduling.
        foreach (var (volume, box) in perEdge)
        {
            if (volume < bestVolume)
            {
                bestVolume = volume;
                best = box;
            }
        }

        return best;
    }

    /// <summary>
    /// Coarse sweep plus golden-section refinement of one edge-pair family. The volume is
    /// a product of three support widths, each a max of sinusoids in θ, so it is piecewise
    /// smooth with kinks — hence a sweep to bracket rather than a derivative method, and
    /// only the most promising brackets refined.
    /// </summary>
    private static (double Volume, OrientedBox3d Box) MinimizeOverFamily(
        Vector3d[] points, in Frame3d basis, in Vector3d other)
    {
        // The family is π-periodic: θ and θ + π give the same box with flipped axis signs.
        const int Samples = 48;
        const int RefinedBrackets = 3;
        Span<double> volumes = stackalloc double[Samples + 1];
        for (int s = 0; s <= Samples; s++)
            volumes[s] = FamilyVolume(points, basis, other, s * Math.PI / Samples);

        Span<int> brackets = stackalloc int[RefinedBrackets];
        Span<double> bracketVolumes = stackalloc double[RefinedBrackets];
        int found = 0;
        for (int s = 1; s < Samples; s++)
        {
            if (!(volumes[s] <= volumes[s - 1]) || !(volumes[s] <= volumes[s + 1]))
                continue;
            if (found < RefinedBrackets)
            {
                brackets[found] = s;
                bracketVolumes[found] = volumes[s];
                found++;
                continue;
            }
            int worst = 0;
            for (int k = 1; k < RefinedBrackets; k++)
                if (bracketVolumes[k] > bracketVolumes[worst]) worst = k;
            if (volumes[s] < bracketVolumes[worst])
            {
                brackets[worst] = s;
                bracketVolumes[worst] = volumes[s];
            }
        }

        var result = (Volume: double.PositiveInfinity, Box: default(OrientedBox3d));
        for (int b = 0; b < found; b++)
        {
            double theta = GoldenSection(points, basis, other,
                (brackets[b] - 1) * Math.PI / Samples, (brackets[b] + 1) * Math.PI / Samples);
            if (!FamilyAxes(basis, other, theta, out var axisX, out var axisY))
                continue;
            Extents(points, axisX, axisY, out var center, out var half);
            double volume = 8 * half.X * half.Y * half.Z;
            if (volume < result.Volume)
                result = (volume, new OrientedBox3d(Frame3d.FromOrthonormal(center, axisX, axisY), half));
        }
        return result;
    }

    /// <summary>Golden-section minimization; 32 contractions take a 7.5° bracket to ~1e-8°.</summary>
    private static double GoldenSection(
        Vector3d[] points, in Frame3d basis, in Vector3d other, double lo, double hi)
    {
        const double Inverse = 0.6180339887498949;
        double x1 = hi - (hi - lo) * Inverse, x2 = lo + (hi - lo) * Inverse;
        double f1 = FamilyVolume(points, basis, other, x1);
        double f2 = FamilyVolume(points, basis, other, x2);
        for (int k = 0; k < 32; k++)
        {
            if (f1 < f2)
            {
                hi = x2; x2 = x1; f2 = f1;
                x1 = hi - (hi - lo) * Inverse;
                f1 = FamilyVolume(points, basis, other, x1);
            }
            else
            {
                lo = x1; x1 = x2; f1 = f2;
                x2 = lo + (hi - lo) * Inverse;
                f2 = FamilyVolume(points, basis, other, x2);
            }
        }
        return (lo + hi) * 0.5;
    }

    private static double FamilyVolume(
        Vector3d[] points, in Frame3d basis, in Vector3d other, double theta)
    {
        if (!FamilyAxes(basis, other, theta, out var axisX, out var axisY))
            return double.PositiveInfinity;
        Extents(points, axisX, axisY, out _, out var half);
        return 8 * half.X * half.Y * half.Z;
    }

    /// <summary>
    /// The family's axes at angle θ: the first face normal sweeps the plane perpendicular
    /// to one edge, and the second is then forced perpendicular to both it and the other
    /// edge. Fails when the two become parallel, where the box is undetermined.
    /// </summary>
    private static bool FamilyAxes(
        in Frame3d basis, in Vector3d other, double theta, out Vector3d axisX, out Vector3d axisY)
    {
        axisY = Vector3d.Zero;
        axisX = basis.X * Math.Cos(theta) + basis.Y * Math.Sin(theta);
        var cross = axisX.Cross(other);
        double length = cross.Length;
        // Scale-free direction test, not a model-unit tolerance: axisX and other are unit
        // vectors, so |cross| is the sine of the angle between them. 1e-6 rather than
        // machine epsilon because normalizing a near-zero cross product amplifies its error
        // until the axes stop being perpendicular to working precision — and a family whose
        // two edges are that close to parallel is covered by its neighbours anyway.
        if (length < 1e-6)
            return false;
        axisY = cross / length;
        // One Gram-Schmidt pass: the cross product is perpendicular only to rounding, and
        // Frame3d.FromOrthonormal holds callers to the 1e-9 weld tier.
        axisY -= axisX * axisX.Dot(axisY);
        double residual = axisY.Length;
        if (residual < 1e-6)
            return false;
        axisY /= residual;
        return true;
    }

    private static void Consider(
        Vector3d[] points, in Vector3d axisX, in Vector3d axisY,
        ref OrientedBox3d best, ref double bestVolume)
    {
        Extents(points, axisX, axisY, out var center, out var half);
        double volume = 8 * half.X * half.Y * half.Z;
        if (volume >= bestVolume)
            return;
        bestVolume = volume;
        best = new OrientedBox3d(Frame3d.FromOrthonormal(center, axisX, axisY), half);
    }

    private static void Extents(
        Vector3d[] points, in Vector3d axisX, in Vector3d axisY,
        out Vector3d center, out Vector3d half)
    {
        var axisZ = axisX.Cross(axisY);
        var min = new Vector3d(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var max = -min;
        foreach (var p in points)
        {
            var local = new Vector3d(p.Dot(axisX), p.Dot(axisY), p.Dot(axisZ));
            min = Vector3d.Min(min, local);
            max = Vector3d.Max(max, local);
        }
        var mid = (min + max) * 0.5;
        center = axisX * mid.X + axisY * mid.Y + axisZ * mid.Z;
        half = (max - min) * 0.5;
    }

    private static List<Vector3d> FaceNormals(
        IReadOnlyList<Vector3d> vertices, ReadOnlySpan<int> triangles)
    {
        var normals = new List<Vector3d>();
        for (int t = 0; t + 2 < triangles.Length; t += 3)
        {
            var a = vertices[triangles[t]];
            var ab = vertices[triangles[t + 1]] - a;
            var ac = vertices[triangles[t + 2]] - a;
            var cross = ab.Cross(ac);
            // Relative area guard: a cross product IS an area, so an absolute epsilon on it
            // would fail quadratically with scale. The reference is the largest edge length
            // squared, the same quantity in the same units.
            double reference = Math.Max(ab.LengthSquared, Math.Max(ac.LengthSquared, (ac - ab).LengthSquared));
            if (cross.LengthSquared > 1e-24 * reference * reference)
                normals.Add(cross / cross.Length);
        }
        return normals;
    }

    private static List<Vector3d> EdgeDirections(
        IReadOnlyList<Vector3d> vertices, ReadOnlySpan<int> triangles)
    {
        var directions = new List<Vector3d>();
        for (int t = 0; t + 2 < triangles.Length; t += 3)
        {
            for (int k = 0; k < 3; k++)
            {
                var d = vertices[triangles[t + (k + 1) % 3]] - vertices[triangles[t + k]];
                double length = d.Length;
                // Exact-zero-ish guard on a LENGTH (not a squared quantity): the reference
                // is the edge itself, so this is scale-free.
                if (length > 0)
                    directions.Add(d / length);
            }
        }
        return directions;
    }

    /// <summary>
    /// Collapses a bag of unit directions to distinct ORIENTATIONS: d and −d span the same
    /// line, and a facet triangulated into many triangles contributes the same normal many
    /// times. The test is a dot product of unit vectors, so it is scale-free by
    /// construction — not a model-unit tolerance.
    /// </summary>
    private static List<Vector3d> DistinctDirections(List<Vector3d> directions)
    {
        var distinct = new List<Vector3d>();
        foreach (var d in directions)
        {
            bool seen = false;
            foreach (var existing in distinct)
            {
                if (Math.Abs(existing.Dot(d)) >= 1 - 1e-12)
                {
                    seen = true;
                    break;
                }
            }
            if (!seen)
                distinct.Add(d);
        }
        return distinct;
    }

    /// <summary>
    /// Centroid plus eigen-decomposition of the point covariance: eigenvalues sorted
    /// descending with matching unit eigenvectors (orthonormal; the first two are
    /// returned, the third is their cross product up to sign).
    /// </summary>
    private static (Vector3d Centroid, double[] Values, Vector3d[] Vectors) CovarianceEigen(
        IReadOnlyList<Vector3d> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
            throw new ArgumentException("Cannot fit an empty point set.", nameof(points));

        var sum = Vector3d.Zero;
        foreach (var p in points)
            sum += p;
        var centroid = sum / points.Count;

        double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (var p in points)
        {
            var d = p - centroid;
            xx += d.X * d.X;
            xy += d.X * d.Y;
            xz += d.X * d.Z;
            yy += d.Y * d.Y;
            yz += d.Y * d.Z;
            zz += d.Z * d.Z;
        }
        double inv = 1.0 / points.Count;
        var (values, vectors) = SymmetricEigen3.SolveDescending(xx * inv, xy * inv, xz * inv, yy * inv, yz * inv, zz * inv);
        return (centroid, values, vectors);
    }
}
