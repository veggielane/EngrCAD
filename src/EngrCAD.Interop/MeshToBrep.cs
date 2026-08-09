using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Interop;

/// <summary>
/// The one edge of the conversion triangle that puts information BACK rather than throwing
/// it away: a triangle <see cref="HalfEdgeMesh"/> re-recognised as a parametric
/// <see cref="BrepSolid"/> of analytic faces. Implicit→mesh, B-Rep→mesh and mesh→implicit
/// are all controlled discretisations; this direction reconstructs the surfaces the
/// tessellation came from, so a drilled plate comes back as about seven faces — not five
/// thousand planar facets wearing a <c>.step</c> extension.
///
/// <para>
/// <b>v1 is the TESSELLATED-CAD case, said out loud.</b> A tessellation of exact geometry
/// has vertices lying EXACTLY on the original surface (an inscribed n-gon's corners are ON
/// the cylinder), so a fit's residual is the chord error and nothing else, and a cylinder's
/// radius is recovered essentially exactly at every tessellation density — where a fit that
/// reported the inscribed radius <c>r·cos(π/n)</c> would be measurably wrong. A 3D SCAN is a
/// different product (noise, outliers, missing regions) and is not attempted: a region whose
/// best primitive fit exceeds the tolerance is reported UNFITTED by name rather than forced
/// onto a surface it is not.
/// </para>
///
/// <para><b>Five stages.</b>
/// <list type="number">
///   <item><description><b>Segmentation.</b> Region-grow triangles across every edge that
///     is not a sharp crease (<see cref="MeshToBrepOptions.FeatureAngleDegrees"/>), so a
///     smoothly-tessellated cylinder wall stays one region while the sharp edge to its cap
///     splits it off. The feature angle reads the MESH it is given, not the surface it means
///     (a coarse octagon's 45° facet dihedral would over-split), so the default admits a
///     tessellation of ≥ 12 segments per circle and the face count is the honest check that
///     no over-segmentation happened.</description></item>
///   <item><description><b>Primitive fitting</b> — plane / cylinder / sphere per region,
///     with the worst residual REPORTED (the <c>BiArcFit.MaxDeviation</c> convention). Cone,
///     torus and freeform are refused by name for v1.</description></item>
///   <item><description><b>Edge recovery</b> — the stage that decides whether the result
///     CLOSES: a region boundary becomes the EXACT intersection of the two fitted surfaces
///     (a line, a circle), never the chordal polyline the mesh happened to carry, and a
///     triple-point corner is snapped to the exact meeting of its three surfaces
///     (<see cref="SurfaceCorner"/>).</description></item>
///   <item><description><b>Assembly</b> — the trimmed faces are welded into a
///     <see cref="BrepSolid"/> and <see cref="ShapeHealing.Heal"/> sews the soup (its stated
///     case), after which <see cref="BrepSolid.Validate"/> must pass.</description></item>
///   <item><description><b>Freeform fallback</b> — a NURBS surface fit for a region that
///     fits no analytic primitive. Not in v1 (a surface fitter is the genuinely new
///     numerical work); such a region is reported unfitted instead.</description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>The verification bar needs no external data</b>, which is what makes the ambition
/// affordable: tessellate a <see cref="BrepSolid"/> this kernel built, reconstruct it, and
/// require the same analytic TYPES with the same parameters, the same FACE COUNT, and a
/// volume agreeing to the tessellation's own convergence. The cylinder-radius-across-densities
/// test is the one that separates a real fit from an inscribed-radius impostor.
/// </para>
/// </summary>
public static class MeshToBrep
{
    /// <summary>
    /// Reconstructs a parametric <see cref="BrepSolid"/> from a triangle mesh that is a
    /// tessellation of analytic CAD geometry. The mesh must be CLOSED and manifold (run
    /// <c>MeshRepair.AutoRepair</c> first on dirty geometry — it is not invoked silently,
    /// because closing holes invents surface a fit would then be recovering from thin air).
    /// </summary>
    /// <returns>A <see cref="MeshToBrepResult"/> whose <see cref="MeshToBrepReport"/> always
    /// describes the regions and their fits, and whose <see cref="MeshToBrepResult.Solid"/>
    /// is the reconstructed B-Rep when every region fitted and the assembly validated.</returns>
    public static MeshToBrepResult Reconstruct(HalfEdgeMesh mesh, MeshToBrepOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        options ??= new MeshToBrepOptions();

        var notes = new List<string>();

        if (mesh.FaceCount == 0)
            return Fail("The mesh is empty.", notes);
        if (!mesh.IsClosed)
            return Fail(
                "The mesh is not closed — it has boundary edges. Reconstruction needs a watertight " +
                "surface; run MeshRepair.AutoRepair first (closing holes invents geometry, so it is " +
                "not done here without being asked).", notes);
        var nonManifold = mesh.NonManifoldVertices();
        if (nonManifold.Count > 0)
            return Fail(
                $"The mesh is non-manifold ({nonManifold.Count} pinched vertices). Reconstruction needs " +
                "a manifold surface; run MeshRepair.AutoRepair first.", notes);

        var tris = mesh.Triangulated();
        double diagonal = MeshDiagonal(tris);
        double absTolerance = options.FitTolerance * diagonal;

        var faceArr = tris.Faces.ToArray();
        var region = Segment(tris, faceArr, options.FeatureAngleDegrees);
        int regionCount = region.Length == 0 ? 0 : region.Max() + 1;

        var regions = new List<ReconstructedRegion>(regionCount);
        var surfaces = new SurfaceFit[regionCount];
        bool allFitted = true;
        for (int r = 0; r < regionCount; r++)
        {
            var fit = FitRegion(faceArr, region, r, absTolerance);
            surfaces[r] = fit;
            regions.Add(new ReconstructedRegion(
                r, fit.Kind, fit.Residual, CountTriangles(region, r), fit.Surface));
            if (fit.Kind == ReconstructedSurfaceKind.Unfitted)
                allFitted = false;
        }

        var report = new MeshToBrepReport(regionCount, regionCount, regions, notes);

        if (!allFitted)
        {
            int unfitted = regions.Count(x => x.Kind == ReconstructedSurfaceKind.Unfitted);
            return new MeshToBrepResult(
                null, report, Succeeded: false,
                $"{unfitted} of {regionCount} regions fit no plane, cylinder or sphere within tolerance " +
                $"({options.FitTolerance:G3} of the model). v1 reconstructs tessellated CAD geometry " +
                "(plane/cylinder/sphere); cone, torus, freeform and noisy scan data are out of scope.");
        }

        // Phase 2: assemble the trimmed faces into a solid.
        var assembled = SolidAssembler.Assemble(tris, region, regionCount, surfaces, notes);
        return new MeshToBrepResult(
            assembled.Solid, report with { Notes = notes }, assembled.Solid is not null, assembled.Reason);
    }

    private static MeshToBrepResult Fail(string reason, List<string> notes) =>
        new(null, new MeshToBrepReport(0, 0, [], notes), Succeeded: false, reason);

    // ---- Segmentation -----------------------------------------------------------------

    /// <summary>
    /// Region id per triangle: flood across every edge whose two faces meet within the
    /// feature angle (a smooth surface), stopping at sharp creases. Uses face-normal angle
    /// directly rather than <see cref="HalfEdge.DihedralAngle"/> so a degenerate face's
    /// unreliable dihedral does not silently merge two surfaces.
    /// </summary>
    internal static int[] Segment(HalfEdgeMesh mesh, Face[] faces, double featureAngleDegrees)
    {
        int nf = mesh.FaceCount;
        var region = new int[nf];
        Array.Fill(region, -1);
        var normals = new Vector3d[nf];
        for (int f = 0; f < nf; f++)
            normals[f] = faces[f].Normal();

        double cosLimit = Math.Cos(featureAngleDegrees * Math.PI / 180.0);
        int next = 0;
        var stack = new Stack<int>();
        for (int seed = 0; seed < nf; seed++)
        {
            if (region[seed] >= 0)
                continue;
            int r = next++;
            region[seed] = r;
            stack.Push(seed);
            while (stack.Count > 0)
            {
                int f = stack.Pop();
                foreach (var h in faces[f].HalfEdges())
                {
                    var twin = h.Twin;
                    if (twin.IsBoundary)
                        continue;
                    int g = twin.Face.Index;
                    if (region[g] >= 0)
                        continue;
                    // Same region iff the two facet normals agree within the feature angle.
                    if (normals[f].Dot(normals[g]) >= cosLimit)
                    {
                        region[g] = r;
                        stack.Push(g);
                    }
                }
            }
        }
        return region;
    }

    private static int CountTriangles(int[] region, int r)
    {
        int count = 0;
        foreach (int x in region)
            if (x == r) count++;
        return count;
    }

    // ---- Fitting ----------------------------------------------------------------------

    /// <summary>The result of trying to recognise one region's surface.</summary>
    internal readonly record struct SurfaceFit(
        ReconstructedSurfaceKind Kind, Surface? Surface, double Residual, bool Reversed);

    private static SurfaceFit FitRegion(Face[] allFaces, int[] region, int r, double tolerance)
    {
        var (vertices, faces) = RegionGeometry(allFaces, region, r);
        if (vertices.Count < 3)
            return new SurfaceFit(ReconstructedSurfaceKind.Unfitted, null, double.PositiveInfinity, false);

        var outward = RegionOutwardNormal(allFaces, faces);

        if (TryFitPlane(vertices, outward, tolerance, out var plane, out double planeRes, out bool planeRev))
            return new SurfaceFit(ReconstructedSurfaceKind.Plane, plane, planeRes, planeRev);
        if (TryFitCylinder(allFaces, vertices, faces, outward, tolerance, out var cyl, out double cylRes, out bool cylRev))
            return new SurfaceFit(ReconstructedSurfaceKind.Cylinder, cyl, cylRes, cylRev);
        if (TryFitSphere(vertices, outward, tolerance, out var sph, out double sphRes, out bool sphRev))
            return new SurfaceFit(ReconstructedSurfaceKind.Sphere, sph, sphRes, sphRev);

        // Report the smallest residual achieved so the caller can see how close it came.
        return new SurfaceFit(ReconstructedSurfaceKind.Unfitted, null, planeRes, false);
    }

    private static bool TryFitPlane(
        List<Vector3d> vertices, in Vector3d outward, double tolerance,
        out Surface? surface, out double residual, out bool reversed)
    {
        surface = null;
        residual = double.PositiveInfinity;
        reversed = false;
        Frame3d frame;
        try { frame = Fitting3d.FitPlane(vertices); }
        catch (ArgumentException) { return false; }

        var normal = frame.Z;
        double worst = 0;
        foreach (var p in vertices)
            worst = Math.Max(worst, Math.Abs((p - frame.Origin).Dot(normal)));
        residual = worst;
        if (worst > tolerance)
            return false;

        // Orient the surface so its own normal points OUT of the solid; then IsReversed is
        // false and the mesh-walk loops (CCW around the outward normal) are CCW around the
        // surface normal, which is what BrepFace wants.
        surface = normal.Dot(outward) >= 0
            ? new PlaneSurface(frame.Origin, frame.X, frame.Y)   // normal = X×Y = +Z
            : new PlaneSurface(frame.Origin, frame.Y, frame.X);  // normal = Y×X = -Z
        return true;
    }

    private static bool TryFitCylinder(
        Face[] allFaces, List<Vector3d> vertices, List<int> faces, in Vector3d outward,
        double tolerance, out Surface? surface, out double residual, out bool reversed)
    {
        surface = null;
        residual = double.PositiveInfinity;
        reversed = false;
        if (vertices.Count < 6)
            return false;

        // Axis = the direction all facet normals are perpendicular to. Facet normals of a
        // cylinder span a great circle in the plane ⊥ axis, so the covariance of the
        // (area-weighted) normals is rank 2 and its SMALLEST eigenvector is the axis.
        double nxx = 0, nxy = 0, nxz = 0, nyy = 0, nyz = 0, nzz = 0;
        foreach (int f in faces)
        {
            var n = allFaces[f].NormalRaw; // area-weighted
            nxx += n.X * n.X; nxy += n.X * n.Y; nxz += n.X * n.Z;
            nyy += n.Y * n.Y; nyz += n.Y * n.Z; nzz += n.Z * n.Z;
        }
        var (_, vectors) = SymmetricEigen3.SolveDescending(nxx, nxy, nxz, nyy, nyz, nzz);
        var axis = vectors[2];
        if (!axis.TryNormalize(Tolerance.Default, out axis))
            return false;

        // Circle fit in the plane ⊥ axis. Build a frame; project; algebraic (Kåsa) fit,
        // which is exact for points lying ON a circle — the tessellated-CAD case.
        var frame = Frame3d.FromNormal(vertices[0], axis);
        var x = frame.X;
        var y = frame.Y;
        int n2 = vertices.Count;
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0, sz = 0;
        var proj = new Vector2d[n2];
        for (int i = 0; i < n2; i++)
        {
            var d = vertices[i] - frame.Origin;
            double u = d.Dot(x), v = d.Dot(y);
            proj[i] = new Vector2d(u, v);
            double zz = u * u + v * v;
            sx += u; sy += v; sz += zz;
            sxx += u * u; syy += v * v; sxy += u * v;
            sxz += u * zz; syz += v * zz;
        }
        // Solve [sxx sxy sx; sxy syy sy; sx sy N] [A;B;C] = [sxz; syz; sz]  → centre, radius.
        if (!Solve3(sxx, sxy, sx, sxy, syy, sy, sx, sy, n2, sxz, syz, sz, out double aC, out double bC, out double cC))
            return false;
        double cu = aC / 2, cv = bC / 2;
        double radius2 = cC + cu * cu + cv * cv;
        if (radius2 <= 0)
            return false;
        double radius = Math.Sqrt(radius2);

        var centre = frame.Origin + x * cu + y * cv;
        double worst = 0;
        for (int i = 0; i < n2; i++)
        {
            double du = proj[i].X - cu, dv = proj[i].Y - cv;
            worst = Math.Max(worst, Math.Abs(Math.Sqrt(du * du + dv * dv) - radius));
        }
        residual = worst;
        if (worst > tolerance)
            return false;

        surface = new CylinderSurface(centre, x, y, radius);
        // The cylinder surface normal is radial-OUT. A solid wall's material is on the
        // inside (outward = radial-out); a bore's material is on the outside (outward =
        // radial-in) → IsReversed.
        var sample = allFaces[faces[0]].Centroid();
        var radial = (sample - centre);
        radial -= axis * radial.Dot(axis);
        reversed = radial.Dot(outward) < 0;
        return true;
    }

    private static bool TryFitSphere(
        List<Vector3d> vertices, in Vector3d outward, double tolerance,
        out Surface? surface, out double residual, out bool reversed)
    {
        surface = null;
        residual = double.PositiveInfinity;
        reversed = false;
        if (vertices.Count < 4)
            return false;

        // Algebraic sphere fit: minimise Σ(x²+y²+z² + Dx+Ey+Fz+G)², linear in D,E,F,G.
        double sx = 0, sy = 0, sz = 0, sxx = 0, syy = 0, szz = 0, sxy = 0, sxz = 0, syz = 0;
        double sxr = 0, syr = 0, szr = 0, sr = 0;
        int n = vertices.Count;
        foreach (var p in vertices)
        {
            double rr = p.X * p.X + p.Y * p.Y + p.Z * p.Z;
            sx += p.X; sy += p.Y; sz += p.Z;
            sxx += p.X * p.X; syy += p.Y * p.Y; szz += p.Z * p.Z;
            sxy += p.X * p.Y; sxz += p.X * p.Z; syz += p.Y * p.Z;
            sxr += p.X * rr; syr += p.Y * rr; szr += p.Z * rr; sr += rr;
        }
        // Normal equations for [D E F G]; centre = -(D,E,F)/2. Solve the 4×4 by elimination
        // against the centred moments (subtract the mean to condition it).
        if (!Solve4(
            sxx, sxy, sxz, sx, sxy, syy, syz, sy, sxz, syz, szz, sz, sx, sy, sz, n,
            -sxr, -syr, -szr, -sr, out double d, out double e, out double fc, out double g))
            return false;
        var centre = new Vector3d(-d / 2, -e / 2, -fc / 2);
        double radius2 = centre.LengthSquared - g;
        if (radius2 <= 0)
            return false;
        double radius = Math.Sqrt(radius2);

        double worst = 0;
        foreach (var p in vertices)
            worst = Math.Max(worst, Math.Abs((p - centre).Length - radius));
        residual = worst;
        if (worst > tolerance)
            return false;

        surface = new SphereSurface(centre, radius);
        reversed = outward.Dot((vertices[0] - centre)) < 0;
        return true;
    }

    // ---- Small linear solves ----------------------------------------------------------

    private static bool Solve3(
        double a11, double a12, double a13, double a21, double a22, double a23,
        double a31, double a32, double a33, double b1, double b2, double b3,
        out double x, out double y, out double z)
    {
        double det =
            a11 * (a22 * a33 - a23 * a32) -
            a12 * (a21 * a33 - a23 * a31) +
            a13 * (a21 * a32 - a22 * a31);
        x = y = z = 0;
        if (Math.Abs(det) < 1e-300)
            return false;
        double inv = 1.0 / det;
        x = (b1 * (a22 * a33 - a23 * a32) - a12 * (b2 * a33 - a23 * b3) + a13 * (b2 * a32 - a22 * b3)) * inv;
        y = (a11 * (b2 * a33 - a23 * b3) - b1 * (a21 * a33 - a23 * a31) + a13 * (a21 * b3 - b2 * a31)) * inv;
        z = (a11 * (a22 * b3 - b2 * a32) - a12 * (a21 * b3 - b2 * a31) + b1 * (a21 * a32 - a22 * a31)) * inv;
        return double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z);
    }

    private static bool Solve4(
        double a11, double a12, double a13, double a14,
        double a21, double a22, double a23, double a24,
        double a31, double a32, double a33, double a34,
        double a41, double a42, double a43, double a44,
        double b1, double b2, double b3, double b4,
        out double x1, out double x2, out double x3, out double x4)
    {
        Span<double> m = stackalloc double[20]
        {
            a11, a12, a13, a14, b1,
            a21, a22, a23, a24, b2,
            a31, a32, a33, a34, b3,
            a41, a42, a43, a44, b4,
        };
        x1 = x2 = x3 = x4 = 0;
        for (int col = 0; col < 4; col++)
        {
            int pivot = col;
            for (int row = col + 1; row < 4; row++)
                if (Math.Abs(m[row * 5 + col]) > Math.Abs(m[pivot * 5 + col]))
                    pivot = row;
            if (Math.Abs(m[pivot * 5 + col]) < 1e-300)
                return false;
            if (pivot != col)
                for (int k = 0; k < 5; k++)
                    (m[col * 5 + k], m[pivot * 5 + k]) = (m[pivot * 5 + k], m[col * 5 + k]);
            for (int row = 0; row < 4; row++)
            {
                if (row == col) continue;
                double factor = m[row * 5 + col] / m[col * 5 + col];
                for (int k = col; k < 5; k++)
                    m[row * 5 + k] -= factor * m[col * 5 + k];
            }
        }
        x1 = m[4] / m[0];
        x2 = m[9] / m[6];
        x3 = m[14] / m[12];
        x4 = m[19] / m[18];
        return double.IsFinite(x1) && double.IsFinite(x2) && double.IsFinite(x3) && double.IsFinite(x4);
    }

    // ---- Region geometry helpers ------------------------------------------------------

    /// <summary>Distinct vertex positions and face indices of one region.</summary>
    private static (List<Vector3d> Vertices, List<int> Faces) RegionGeometry(
        Face[] allFaces, int[] region, int r)
    {
        var faces = new List<int>();
        var seen = new HashSet<int>();
        var vertices = new List<Vector3d>();
        for (int f = 0; f < region.Length; f++)
        {
            if (region[f] != r)
                continue;
            faces.Add(f);
            foreach (var v in allFaces[f].Vertices())
                if (seen.Add(v.Index))
                    vertices.Add(v.Position);
        }
        return (vertices, faces);
    }

    private static Vector3d RegionOutwardNormal(Face[] allFaces, List<int> faces)
    {
        var sum = Vector3d.Zero;
        foreach (int f in faces)
            sum += allFaces[f].NormalRaw; // area-weighted
        return sum.TryNormalize(Tolerance.Default, out var n) ? n : Vector3d.UnitZ;
    }

    private static double MeshDiagonal(HalfEdgeMesh mesh)
    {
        var min = new Vector3d(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = -min;
        foreach (var v in mesh.Vertices)
        {
            min = Vector3d.Min(min, v.Position);
            max = Vector3d.Max(max, v.Position);
        }
        double d = (max - min).Length;
        return d > 0 ? d : 1;
    }
}
