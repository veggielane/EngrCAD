using EngrCAD.Core;

namespace EngrCAD.Interop;

/// <summary>
/// The quadratic error function of a set of (point, normal) samples — the Hermite data
/// that turns Surface Nets into dual contouring: <c>E(x) = Σ (nᵢ·(x − pᵢ))²</c>, the
/// summed squared distance from x to the tangent planes the field reports at its own
/// surface crossings.
/// <para>
/// <b>Why this rather than the mean of the crossings.</b> A cell's crossings all lie ON
/// the surface, so their mean lies strictly inside any convex corner and strictly inside
/// any edge — the plain Surface Nets vertex is a chamfer of every sharp feature by
/// construction, not by discretization error, and no resolution removes it. The planes do
/// carry the feature: at a box corner the three crossings report three mutually
/// perpendicular normals, E has a unique minimiser, and that minimiser IS the corner,
/// exactly.
/// </para>
/// <para>
/// <b>The solve is a regularised normal-equations solve, and normal equations are the
/// right form here rather than a compromise.</b> Writing the samples as rows of a matrix
/// N, this accumulates A = NᵀN and b = Nᵀ(N·p) directly, so A is symmetric positive
/// semi-definite by construction and its SVD IS its eigendecomposition — which
/// <see cref="SymmetricEigen3"/> already computes, tested, with no second implementation
/// to keep in step. The usual objection to normal equations (they square the condition
/// number) is bounded and then removed here: the rows are UNIT normals and there are at
/// most twelve of them, so κ(A) is a function of the ANGLES between the sampled planes
/// alone — and the truncation below is exactly the mechanism that answers an ill-
/// conditioned angle, by declining to resolve it rather than by inverting it.
/// </para>
/// <para>
/// <b>Rank is the feature classification, and truncation is a stated ANGLE.</b> A flat
/// region gives rank 1 (the minimiser is a plane), an edge rank 2 (a line), a corner
/// rank 3 (a point). Directions the samples do not constrain must come from somewhere,
/// and the answer is the MASS POINT — the mean of the crossings, i.e. the incumbent
/// Surface Nets vertex: <c>x = m + A⁺(b − A·m)</c> moves m only within the row space of
/// A and leaves it alone along the null space. So the fallback in every unconstrained
/// direction is bit-for-bit what this polygonizer produced before, which is what makes
/// this a strict refinement rather than a different algorithm.
/// </para>
/// </summary>
internal struct SurfaceQef
{
    // A = Σ n nᵀ (symmetric; upper triangle), b = Σ n (n·p), c = Σ (n·p)².
    private double _axx, _axy, _axz, _ayy, _ayz, _azz;
    private double _bx, _by, _bz;
    private double _c;
    // The mass point's running sum, and how many crossings it covers.
    private double _mx, _my, _mz;
    private int _count;
    // How many of those crossings contributed a usable plane.
    private int _planes;

    /// <summary>Crossings accumulated, plane or not.</summary>
    public readonly int Count => _count;

    /// <summary>
    /// The mean of the crossings — the plain Surface Nets vertex, and the regularisation
    /// target. Zero crossings has no mass point and callers must not ask.
    /// </summary>
    public readonly Vector3d MassPoint => new(_mx / _count, _my / _count, _mz / _count);

    /// <summary>
    /// Adds one surface crossing and the field's unit normal there. A zero normal — the
    /// spelling <see cref="Sdf.Normals"/> has no reason to produce but which a caller may
    /// pass for "no gradient here" — contributes to the mass point and to nothing else,
    /// so the vertex degrades to the incumbent mean rather than to a wrong plane.
    /// </summary>
    public void Add(in Vector3d point, in Vector3d normal)
    {
        _mx += point.X;
        _my += point.Y;
        _mz += point.Z;
        _count++;

        double nx = normal.X, ny = normal.Y, nz = normal.Z;
        if (nx == 0 && ny == 0 && nz == 0)
            return;

        double d = nx * point.X + ny * point.Y + nz * point.Z;
        _axx += nx * nx;
        _axy += nx * ny;
        _axz += nx * nz;
        _ayy += ny * ny;
        _ayz += ny * nz;
        _azz += nz * nz;
        _bx += nx * d;
        _by += ny * d;
        _bz += nz * d;
        _c += d * d;
        _planes++;
    }

    /// <summary>
    /// The union of two sample sets — used by the adaptive pass, where a cluster's error
    /// is the error of the merged quadric rather than a re-fit of anything. Addition is
    /// exact in the sense that matters: the merged quadric IS the quadric of the union of
    /// the two sample sets, so a cluster's reported error is the true summed squared
    /// distance to every plane it swallowed, at any depth.
    /// </summary>
    public static SurfaceQef operator +(in SurfaceQef a, in SurfaceQef b) => new()
    {
        _axx = a._axx + b._axx,
        _axy = a._axy + b._axy,
        _axz = a._axz + b._axz,
        _ayy = a._ayy + b._ayy,
        _ayz = a._ayz + b._ayz,
        _azz = a._azz + b._azz,
        _bx = a._bx + b._bx,
        _by = a._by + b._by,
        _bz = a._bz + b._bz,
        _c = a._c + b._c,
        _mx = a._mx + b._mx,
        _my = a._my + b._my,
        _mz = a._mz + b._mz,
        _count = a._count + b._count,
        _planes = a._planes + b._planes,
    };

    /// <summary>
    /// <c>E(x) = xᵀAx − 2b·x + c</c>, the summed squared distance from x to every sampled
    /// plane. Round-off can make a value that is mathematically zero come out slightly
    /// negative (it is a difference of large cancelling products at the minimiser), so it
    /// is clamped at zero — the caller compares it against a squared length.
    /// </summary>
    public readonly double Error(in Vector3d x)
    {
        double q =
            _axx * x.X * x.X + _ayy * x.Y * x.Y + _azz * x.Z * x.Z +
            2 * (_axy * x.X * x.Y + _axz * x.X * x.Z + _ayz * x.Y * x.Z);
        return Math.Max(0, q - 2 * (_bx * x.X + _by * x.Y + _bz * x.Z) + _c);
    }

    /// <summary>
    /// The minimiser of <see cref="Error"/> nearest the mass point, with directions the
    /// samples constrain by less than <paramref name="singularRatio"/> (a ratio of
    /// SINGULAR values of N, i.e. square roots of A's eigenvalues) left at the mass point.
    /// <para>
    /// <paramref name="singularRatio"/> is an angle in disguise and the caller states it
    /// as one: two unit normals separated by α give A the eigenvalues 1 ± |cos α| in their
    /// own plane, so the singular-value ratio is exactly <c>tan(α/2)</c>. A ratio of 0.1
    /// therefore says "a crease that deviates from flat by less than 11.4° is not a
    /// feature" — which is a geometric decision about the model, not a numerical one about
    /// the matrix, and it is safe in the direction that matters: declining to resolve a
    /// direction returns the incumbent answer there, where resolving a direction the
    /// samples barely constrain sends the vertex far away.
    /// </para>
    /// </summary>
    public readonly Vector3d Solve(double singularRatio)
    {
        var m = MassPoint;
        if (_planes == 0)
            return m;

        var (values, vectors) = SymmetricEigen3.SolveDescending(_axx, _axy, _axz, _ayy, _ayz, _azz);
        double largest = values[0];
        // A is PSD, so the largest eigenvalue is non-negative; exactly zero means every
        // sampled normal was zero-length, which Add already excluded, but a caller feeding
        // denormals could still reach it. Deliberate exact-zero test.
        if (largest <= 0)
            return m;

        // r = b − A·m, the residual gradient at the mass point (halved).
        double rx = _bx - (_axx * m.X + _axy * m.Y + _axz * m.Z);
        double ry = _by - (_axy * m.X + _ayy * m.Y + _ayz * m.Z);
        double rz = _bz - (_axz * m.X + _ayz * m.Y + _azz * m.Z);
        var r = new Vector3d(rx, ry, rz);

        double floor = singularRatio * singularRatio * largest; // eigenvalues are squared singular values
        var delta = Vector3d.Zero;
        for (int i = 0; i < 3; i++)
        {
            if (values[i] <= floor)
                continue;
            delta += vectors[i] * (vectors[i].Dot(r) / values[i]);
        }
        return m + delta;
    }
}
