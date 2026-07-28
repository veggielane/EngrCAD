using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// A symmetric 3×3 tensor, stored as its six independent entries. Used for both the
/// inertia tensor and the volume-weighted second-moment matrix
/// (<see cref="MassProperties.SecondMoment"/>) — the two differ only by the standard
/// identity I = tr(S)·Id − S, so one type serves both.
/// </summary>
public readonly struct SymmetricTensor3 : IEquatable<SymmetricTensor3>
{
    public double Xx { get; }
    public double Yy { get; }
    public double Zz { get; }
    public double Xy { get; }
    public double Xz { get; }
    public double Yz { get; }

    public SymmetricTensor3(double xx, double yy, double zz, double xy, double xz, double yz)
    {
        Xx = xx;
        Yy = yy;
        Zz = zz;
        Xy = xy;
        Xz = xz;
        Yz = yz;
    }

    public static readonly SymmetricTensor3 Zero = default;
    public static readonly SymmetricTensor3 Identity = new(1, 1, 1, 0, 0, 0);

    public double Trace => Xx + Yy + Zz;

    /// <summary>Row/column access, 0-based (symmetric, so order does not matter).</summary>
    public double this[int row, int column] => (row, column) switch
    {
        (0, 0) => Xx,
        (1, 1) => Yy,
        (2, 2) => Zz,
        (0, 1) or (1, 0) => Xy,
        (0, 2) or (2, 0) => Xz,
        (1, 2) or (2, 1) => Yz,
        _ => throw new ArgumentOutOfRangeException(nameof(row)),
    };

    /// <summary>The outer product v·vᵀ.</summary>
    public static SymmetricTensor3 OuterProduct(in Vector3d v) =>
        new(v.X * v.X, v.Y * v.Y, v.Z * v.Z, v.X * v.Y, v.X * v.Z, v.Y * v.Z);

    public Vector3d Multiply(in Vector3d v) => new(
        Xx * v.X + Xy * v.Y + Xz * v.Z,
        Xy * v.X + Yy * v.Y + Yz * v.Z,
        Xz * v.X + Yz * v.Y + Zz * v.Z);

    public static SymmetricTensor3 operator +(in SymmetricTensor3 a, in SymmetricTensor3 b) =>
        new(a.Xx + b.Xx, a.Yy + b.Yy, a.Zz + b.Zz, a.Xy + b.Xy, a.Xz + b.Xz, a.Yz + b.Yz);

    public static SymmetricTensor3 operator -(in SymmetricTensor3 a, in SymmetricTensor3 b) =>
        new(a.Xx - b.Xx, a.Yy - b.Yy, a.Zz - b.Zz, a.Xy - b.Xy, a.Xz - b.Xz, a.Yz - b.Yz);

    public static SymmetricTensor3 operator *(in SymmetricTensor3 t, double s) =>
        new(t.Xx * s, t.Yy * s, t.Zz * s, t.Xy * s, t.Xz * s, t.Yz * s);

    public static SymmetricTensor3 operator *(double s, in SymmetricTensor3 t) => t * s;

    /// <summary>tr(T)·Id − T — the map between a second-moment matrix and an inertia tensor
    /// (it is its own inverse in 3D up to the factor ½ on the trace, so it is spelled out
    /// at both call sites rather than reused blindly).</summary>
    internal SymmetricTensor3 TraceComplement()
    {
        double t = Trace;
        return new SymmetricTensor3(t - Xx, t - Yy, t - Zz, -Xy, -Xz, -Yz);
    }

    /// <summary>
    /// The congruence M·T·Mᵀ using the linear (upper-left 3×3) part of
    /// <paramref name="transform"/>. Symmetric by construction.
    /// </summary>
    public SymmetricTensor3 Congruence(in Matrix4d transform)
    {
        var r0 = new Vector3d(transform.M11, transform.M12, transform.M13);
        var r1 = new Vector3d(transform.M21, transform.M22, transform.M23);
        var r2 = new Vector3d(transform.M31, transform.M32, transform.M33);
        var t0 = Multiply(r0);
        var t1 = Multiply(r1);
        var t2 = Multiply(r2);
        return new SymmetricTensor3(
            r0.Dot(t0), r1.Dot(t1), r2.Dot(t2),
            r0.Dot(t1), r0.Dot(t2), r1.Dot(t2));
    }

    public bool Equals(SymmetricTensor3 other) =>
        Xx.Equals(other.Xx) && Yy.Equals(other.Yy) && Zz.Equals(other.Zz) &&
        Xy.Equals(other.Xy) && Xz.Equals(other.Xz) && Yz.Equals(other.Yz);

    public override bool Equals(object? obj) => obj is SymmetricTensor3 t && Equals(t);
    public override int GetHashCode() => HashCode.Combine(Xx, Yy, Zz, Xy, Xz, Yz);
    public static bool operator ==(in SymmetricTensor3 a, in SymmetricTensor3 b) => a.Equals(b);
    public static bool operator !=(in SymmetricTensor3 a, in SymmetricTensor3 b) => !a.Equals(b);

    public override string ToString() =>
        $"[{Xx:G6} {Xy:G6} {Xz:G6}; {Xy:G6} {Yy:G6} {Yz:G6}; {Xz:G6} {Yz:G6} {Zz:G6}]";
}

/// <summary>
/// The principal axes of an inertia tensor: three moments with the frame whose X/Y/Z are
/// the matching axes, placed at the body's centre of mass. Moments are sorted
/// <b>ascending</b> (the classical convention — the first axis is the axis of least
/// inertia, i.e. the body's "long" direction).
/// </summary>
public readonly record struct PrincipalInertia(Vector3d Moments, Frame3d Axes);

/// <summary>
/// Volume, surface area, centre of mass and inertia of a solid body of uniform
/// <see cref="Density"/> — the kernel's answer to OCCT's <c>GProp_GProps</c>.
/// </summary>
/// <remarks>
/// <para><b>Units are the caller's.</b> Lengths are model units; <see cref="Density"/> is
/// mass per unit volume in whatever system the caller works in (for steel modelled in mm,
/// 7.85e-6 kg/mm³). <see cref="Mass"/> is then in those mass units and inertia in
/// mass·length². The default density of 1 makes mass numerically equal to volume.</para>
/// <para>The stored quantity is the <b>volume-weighted</b> second-moment matrix about the
/// centroid, S = ∫(r−c)(r−c)ᵀ dV (units length⁵, density-free). Everything else derives
/// from it: <see cref="Inertia"/> = ρ·(tr(S)·Id − S), and the parallel-axis theorem moves
/// it to any other point. Carrying S rather than the inertia tensor is what makes
/// <see cref="Transformed"/> and <see cref="Combine"/> exact one-liners.</para>
/// </remarks>
public readonly struct MassProperties
{
    /// <summary>Enclosed volume, in model units³.</summary>
    public double Volume { get; }

    /// <summary>Total boundary area, in model units².</summary>
    public double SurfaceArea { get; }

    /// <summary>Mass per unit volume, in the caller's units.</summary>
    public double Density { get; }

    /// <summary>Centre of mass (= the volume centroid, since the density is uniform).</summary>
    public Vector3d Centroid { get; }

    /// <summary>
    /// The volume-weighted second-moment matrix about the centroid, ∫(r−c)(r−c)ᵀ dV
    /// (length⁵). Density-free, so <see cref="WithDensity"/> costs nothing.
    /// </summary>
    public SymmetricTensor3 SecondMoment { get; }

    public MassProperties(
        double volume, double surfaceArea, double density, in Vector3d centroid, in SymmetricTensor3 secondMoment)
    {
        Density = density;
        Volume = volume;
        SurfaceArea = surfaceArea;
        Centroid = centroid;
        SecondMoment = secondMoment;
    }

    public static readonly MassProperties Zero = new(0, 0, 1, Vector3d.Zero, SymmetricTensor3.Zero);

    public double Mass => Density * Volume;

    /// <summary>The inertia tensor about the centre of mass, in world axes (mass·length²).</summary>
    public SymmetricTensor3 Inertia => Density * SecondMoment.TraceComplement();

    /// <summary>
    /// The inertia tensor about an arbitrary point, by the parallel-axis theorem:
    /// I_p = I_c + m(|d|²·Id − d·dᵀ) with d = point − centroid.
    /// </summary>
    public SymmetricTensor3 InertiaAbout(in Vector3d point)
    {
        var d = point - Centroid;
        double m = Mass;
        return Inertia + m * (d.LengthSquared * SymmetricTensor3.Identity - SymmetricTensor3.OuterProduct(d));
    }

    /// <summary>The inertia tensor about the world origin.</summary>
    public SymmetricTensor3 InertiaAboutOrigin => InertiaAbout(Vector3d.Zero);

    /// <summary>Same body, different material.</summary>
    public MassProperties WithDensity(double density) =>
        new(Volume, SurfaceArea, density, Centroid, SecondMoment);

    /// <summary>
    /// Principal moments (ascending) and axes, from an eigen-decomposition of
    /// <see cref="Inertia"/>. The returned frame sits at <see cref="Centroid"/> and is
    /// right-handed; eigenvector signs are otherwise arbitrary, as they must be.
    /// </summary>
    public PrincipalInertia Principal()
    {
        var i = Inertia;
        var (values, vectors) = SymmetricEigen3.SolveAscending(i.Xx, i.Xy, i.Xz, i.Yy, i.Yz, i.Zz);
        // Frame3d.FromOrthonormal defines Z = X × Y, so the frame is right-handed whatever
        // signs the eigensolver produced; the third moment belongs to that axis either way
        // (an eigenvector and its negation span the same principal direction).
        var frame = Frame3d.FromOrthonormal(Centroid, vectors[0], vectors[1]);
        return new PrincipalInertia(new Vector3d(values[0], values[1], values[2]), frame);
    }

    /// <summary>
    /// The same body posed by a <b>similarity</b> transform (rotation, uniform scale,
    /// translation, optionally mirrored). Volume scales by |det|, area by the squared
    /// scale, and the second moment by the congruence |det|·M·S·Mᵀ.
    /// </summary>
    /// <remarks>
    /// Shear and non-uniform scale are rejected: they are perfectly well-defined for the
    /// volume integrals, but <see cref="SurfaceArea"/> is <i>not</i> a function of the
    /// input properties under such a map (it depends on the actual boundary geometry), and
    /// silently returning a wrong area would be worse than refusing.
    /// </remarks>
    public MassProperties Transformed(in Matrix4d transform)
    {
        // Dimensionless slack on matrix entries; the residuals below are normalized by the
        // scale (and by its square for the orthogonality dot products, which have units of
        // scale²), so this is the scale-free algorithmic tier, not a length tolerance.
        const double eps = 1e-9;
        if (Math.Abs(transform.M41) > eps || Math.Abs(transform.M42) > eps ||
            Math.Abs(transform.M43) > eps || Math.Abs(transform.M44 - 1) > eps)
            throw new ArgumentException("Mass properties cannot be posed by a perspective transform.", nameof(transform));

        var c0 = new Vector3d(transform.M11, transform.M21, transform.M31);
        var c1 = new Vector3d(transform.M12, transform.M22, transform.M32);
        var c2 = new Vector3d(transform.M13, transform.M23, transform.M33);
        double s = (c0.Length + c1.Length + c2.Length) / 3;
        if (s <= 0)
            throw new ArgumentException("Transform collapses space; mass properties are undefined.", nameof(transform));
        bool similarity =
            Math.Abs(c0.Length - s) <= eps * s && Math.Abs(c1.Length - s) <= eps * s &&
            Math.Abs(c2.Length - s) <= eps * s &&
            Math.Abs(c0.Dot(c1)) <= eps * s * s && Math.Abs(c1.Dot(c2)) <= eps * s * s &&
            Math.Abs(c0.Dot(c2)) <= eps * s * s;
        if (!similarity)
            throw new ArgumentException(
                "Mass properties can only be posed by a similarity transform (rotation, uniform scale, translation, mirror); " +
                "shear or non-uniform scale changes the surface area in a way the properties alone cannot express.",
                nameof(transform));

        double absDet = Math.Abs(c0.Cross(c1).Dot(c2));
        return new MassProperties(
            absDet * Volume,
            s * s * SurfaceArea,
            Density,
            transform.TransformPoint(Centroid),
            absDet * SecondMoment.Congruence(transform));
    }

    /// <summary>
    /// Combines bodies (an assembly). Volumes, areas and masses add; the centre of mass is
    /// the mass-weighted average; second moments are moved to the combined centroid by the
    /// parallel-axis theorem. Mixed densities are handled correctly — the result's
    /// <see cref="Density"/> is the effective bulk density (total mass / total volume).
    /// </summary>
    public static MassProperties Combine(IEnumerable<MassProperties> bodies)
    {
        ArgumentNullException.ThrowIfNull(bodies);
        double volume = 0, area = 0, mass = 0;
        var weighted = Vector3d.Zero;
        var list = bodies as IReadOnlyList<MassProperties> ?? [.. bodies];
        foreach (var b in list)
        {
            volume += b.Volume;
            area += b.SurfaceArea;
            mass += b.Mass;
            weighted += b.Centroid * b.Mass;
        }
        // Exact-zero semantic test: no material at all has no centre of mass to report.
        if (mass == 0 || volume == 0)
            return new MassProperties(volume, area, 0, Vector3d.Zero, SymmetricTensor3.Zero);

        var centroid = weighted / mass;
        var massMoment = SymmetricTensor3.Zero;
        foreach (var b in list)
        {
            var d = b.Centroid - centroid;
            massMoment += b.Density * (b.SecondMoment + b.Volume * SymmetricTensor3.OuterProduct(d));
        }
        double effectiveDensity = mass / volume;
        return new MassProperties(volume, area, effectiveDensity, centroid, massMoment * (1 / effectiveDensity));
    }

    public override string ToString() =>
        $"V={Volume:G6} A={SurfaceArea:G6} m={Mass:G6} c={Centroid}";
}

/// <summary>
/// Accumulates the exact polynomial volume moments of a closed, outward-oriented boundary
/// given as triangles or planar polygons — the divergence-theorem sum behind
/// <see cref="MeshMassProperties"/> (Mirtich / Eberly), exposed so triangle-soup callers
/// (an STL stream, a tessellation in flight) can integrate without building a mesh.
/// </summary>
/// <remarks>
/// <para>Exact for a polyhedron: each facet contributes the signed tetrahedron it spans
/// with the reference point, and a simplex's monomial moments are closed-form
/// (∫λ_aλ_b dV = V·(1+δ_ab)/20 in barycentric coordinates).</para>
/// <para><b>Always give a reference point near the body</b> (the constructor takes one;
/// <see cref="MeshMassProperties"/> passes the bounding-box centre). The sum is over terms
/// of size |r|³ that cancel down to the volume, so a body far from the origin loses
/// digits catastrophically — a 10 mm cube at (10⁶, 10⁶, 10⁶) computes its volume from
/// ~10¹⁸-sized terms and comes out ~10% wrong. Re-centring costs one subtraction per
/// vertex and restores full precision; the moments are then shifted back exactly, and the
/// centroid-relative second moment is translation-invariant so it needs no shift at all.
/// </para>
/// <para>Mutable struct: it is an accumulator, not a math value. It allocates nothing.</para>
/// </remarks>
public struct MassPropertyIntegrator
{
    private readonly Vector3d _reference;
    private double _v6;                                     // Σ det[a b c]  = 6·volume
    private double _mx, _my, _mz;                           // Σ det·(a+b+c) = 24·∫r dV
    private double _xx, _yy, _zz, _xy, _xz, _yz;            // 120·∫r rᵀ dV
    private double _area;

    /// <summary>An integrator whose moments are accumulated relative to
    /// <paramref name="reference"/> and shifted back on completion.</summary>
    public MassPropertyIntegrator(in Vector3d reference) => _reference = reference;

    /// <summary>Boundary area accumulated so far.</summary>
    public readonly double Area => _area;

    /// <summary>Signed enclosed volume accumulated so far (negative means inward winding).</summary>
    public readonly double SignedVolume => _v6 / 6.0;

    /// <summary>Adds one outward-wound triangle: its area and its volume moments.</summary>
    public void AddTriangle(in Vector3d p0, in Vector3d p1, in Vector3d p2)
    {
        _area += (p1 - p0).Cross(p2 - p0).Length * 0.5;
        AddTriangleMoments(p0, p1, p2);
    }

    /// <summary>
    /// Adds one outward-wound planar polygon (n ≥ 3): Newell's area — correct for concave
    /// polygons, where summing fan-triangle areas would over-count — plus the fan's volume
    /// moments, which telescope exactly whatever the polygon's shape.
    /// </summary>
    public void AddPolygon(ReadOnlySpan<Vector3d> polygon)
    {
        if (polygon.Length < 3)
            throw new ArgumentException("A polygon needs at least 3 vertices.", nameof(polygon));
        double nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < polygon.Length; i++)
        {
            var p = polygon[i];
            var q = polygon[(i + 1) % polygon.Length];
            nx += (p.Y - q.Y) * (p.Z + q.Z);
            ny += (p.Z - q.Z) * (p.X + q.X);
            nz += (p.X - q.X) * (p.Y + q.Y);
        }
        _area += new Vector3d(nx, ny, nz).Length * 0.5;
        for (int i = 1; i + 1 < polygon.Length; i++)
            AddTriangleMoments(polygon[0], polygon[i], polygon[i + 1]);
    }

    /// <summary>Volume moments of one facet without touching the area sum (the polygon
    /// path measures area by Newell instead of per fan triangle).</summary>
    internal void AddTriangleMoments(in Vector3d p0, in Vector3d p1, in Vector3d p2)
    {
        var a = p0 - _reference;
        var b = p1 - _reference;
        var c = p2 - _reference;

        // Six times the signed volume of tetrahedron (reference, a, b, c). No epsilon here
        // by design: a degenerate facet contributes exactly zero and needs no guard, and
        // thresholding a determinant would be the classic scale-quadratic mistake.
        double det = a.Dot(b.Cross(c));
        _v6 += det;

        double sx = a.X + b.X + c.X, sy = a.Y + b.Y + c.Y, sz = a.Z + b.Z + c.Z;
        _mx += det * sx;
        _my += det * sy;
        _mz += det * sz;

        // ∫x_i x_j over the tetrahedron = (V/20)·(Σ_k p_k,i p_k,j + S_i S_j) with the
        // fourth vertex at the origin of the shifted frame; V = det/6, so the factor is
        // det/120 and the division is deferred to Complete().
        _xx += det * (a.X * a.X + b.X * b.X + c.X * c.X + sx * sx);
        _yy += det * (a.Y * a.Y + b.Y * b.Y + c.Y * c.Y + sy * sy);
        _zz += det * (a.Z * a.Z + b.Z * b.Z + c.Z * c.Z + sz * sz);
        _xy += det * (a.X * a.Y + b.X * b.Y + c.X * c.Y + sx * sy);
        _xz += det * (a.X * a.Z + b.X * b.Z + c.X * c.Z + sx * sz);
        _yz += det * (a.Y * a.Z + b.Y * b.Z + c.Y * c.Z + sy * sz);
    }

    /// <summary>Adds boundary area not covered by the facets fed in (rarely needed; the
    /// tessellation paths measure everything they integrate).</summary>
    public void AddArea(double area) => _area += area;

    /// <summary>
    /// Finishes the sums into a <see cref="MassProperties"/>. Throws when the accumulated
    /// signed volume is not positive — an empty or inward-wound boundary, for which mass
    /// and inertia are meaningless.
    /// </summary>
    public readonly MassProperties Complete(double density = 1.0)
    {
        double volume = _v6 / 6.0;
        if (!(volume > 0))
            throw new InvalidOperationException(
                $"Signed volume is {volume:G6}; mass properties need a closed boundary wound outward " +
                "(a negative volume means the facets face inward).");

        // First moments → centroid, still relative to the reference point.
        var relativeCentroid = new Vector3d(_mx, _my, _mz) / (24.0 * volume);

        // Second moments about the reference, then shifted to the centroid. The shift is
        // benign because the reference is near the body, so |relativeCentroid| is at most
        // the body's half-extent: no cancellation of the kind the reference point exists
        // to prevent.
        double f = 1.0 / 120.0;
        var aboutReference = new SymmetricTensor3(_xx * f, _yy * f, _zz * f, _xy * f, _xz * f, _yz * f);
        var aboutCentroid = aboutReference - volume * SymmetricTensor3.OuterProduct(relativeCentroid);

        return new MassProperties(volume, _area, density, _reference + relativeCentroid, aboutCentroid);
    }
}

/// <summary>Mass properties of a <see cref="HalfEdgeMesh"/> — exact for any polyhedron.</summary>
public static class MeshMassProperties
{
    /// <summary>
    /// Volume, area, centre of mass and inertia of the solid bounded by
    /// <paramref name="mesh"/>. <b>Exact</b>: every facet contributes a closed-form
    /// tetrahedral moment, so a box, a tetrahedron or any other polyhedron comes out to
    /// round-off. A tessellated curved solid is exact for the polyhedron it actually is —
    /// see <c>BrepMassProperties</c> in EngrCAD.Interop for the convergence to the smooth
    /// body.
    /// </summary>
    /// <param name="mesh">Closed, outward-wound boundary.</param>
    /// <param name="density">Mass per unit volume; 1 makes mass equal volume.</param>
    /// <param name="requireClosed">When false, skips the topological closedness check —
    /// for surfaces that are geometrically watertight but carry T-junction seams (boolean
    /// output), exactly as <see cref="HalfEdgeMesh.SignedVolume"/> allows.</param>
    public static MassProperties Compute(HalfEdgeMesh mesh, double density = 1.0, bool requireClosed = true)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (requireClosed && !mesh.IsClosed)
            throw new InvalidOperationException("Mass properties are only defined for a closed mesh.");

        // Reference point: the bounding-box centre. See MassPropertyIntegrator's remarks —
        // this is the difference between full precision and losing every digit on a body
        // modelled far from the origin.
        // (An empty mesh has an inverted "empty" box whose centre is NaN; it fails in
        // Complete() with the honest "no enclosed volume" message instead.)
        var integrator = new MassPropertyIntegrator(
            mesh.VertexCount > 0 ? mesh.ComputeBounds().Center : Vector3d.Zero);

        for (int f = 0; f < mesh.FaceCount; f++)
        {
            // Newell area (correct for concave faces) + fan moments, walked straight over
            // the half-edge arrays so no polygon buffer is materialized.
            integrator.AddArea(mesh.FaceNormalRaw(f).Length);

            int start = mesh.FaceAnyHalfEdge(f);
            var p0 = mesh.GetPosition(mesh.HeOrigin(start));
            int he = mesh.HeNext(start);
            while (mesh.HeNext(he) != start)
            {
                integrator.AddTriangleMoments(
                    p0,
                    mesh.GetPosition(mesh.HeOrigin(he)),
                    mesh.GetPosition(mesh.HeOrigin(mesh.HeNext(he))));
                he = mesh.HeNext(he);
            }
        }

        return integrator.Complete(density);
    }

    /// <inheritdoc cref="Compute(HalfEdgeMesh, double, bool)"/>
    public static MassProperties MassProperties(this HalfEdgeMesh mesh, double density = 1.0, bool requireClosed = true) =>
        Compute(mesh, density, requireClosed);
}
