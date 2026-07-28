using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Which corner a polygon face is fanned from when it has to become triangles.
/// </summary>
/// <remarks>
/// <para>
/// A polygon mesh here stores n-gons, but almost everything downstream needs triangles:
/// volume and mass properties, GPU buffers, STL/3MF/AMF export,
/// <see cref="HalfEdgeMesh.Triangulated"/>. Each of those used to fan from vertex 0,
/// which means <b>the split of a quad was decided by where its half-edge cycle happened
/// to start</b> — an artifact of construction order, not of geometry.
/// </para>
/// <para>
/// For a planar quad that costs nothing (both diagonals cover the same region), but every
/// grid band of a curved or sheared surface is <em>non-planar</em>, and there the two
/// triangulations are genuinely different surfaces. Measured on a threaded rod, whose
/// helical band is a sheared grid with cell diagonal ratios up to 40:1: a left-hand rod
/// tessellates to the <em>identical</em> vertex set as the mirror of its right-hand twin —
/// 0 of 131 200 vertices differ at 1e-9 — yet carried a systematically <b>3× larger volume
/// deficit</b> at every density, purely because mirroring the cells swapped which diagonal
/// the corner-0 fan picked.
/// </para>
/// <para>
/// The fix is to read the geometry: fan a quad from whichever corner spans the
/// <b>shorter 3D diagonal</b>. That is the standard rule, and it is the right one for the
/// same reason it is standard — the shorter diagonal is the one closer to the bilinear
/// surface the four corners sample, and it gives the fatter pair of triangles. Larger
/// n-gons keep the corner-0 fan: this is about grid cells, and a general n-gon's fan is
/// the wrong shape to fix by moving its apex.
/// </para>
/// <para>
/// <b>The tie guard is load bearing, and it is relative.</b> A great many grid cells have
/// diagonals that are <em>mathematically equal</em> — every quad of a UV sphere is
/// mirror-symmetric about its own meridian, so its two diagonals are reflections of each
/// other — and their computed squares then differ only in the last ulps. Measured: 408 of
/// the 960 quads of <c>UvSphere(40, 26)</c> report the far diagonal as "shorter", by a
/// ratio that is 1.000000000000 to twelve digits, and the two triangulations are equal in
/// quality to twelve digits of the inscribed-volume deficit. An exact comparison there
/// would let an ulp decide the split — the very defect this rule exists to remove, in new
/// clothes, and it measurably perturbed decimation and remeshing downstream. So corner 0
/// is kept unless the other diagonal is shorter by more than <see cref="RelativeTie"/>,
/// the scale-free tier: four orders above round-off, and far below any difference that
/// could matter to the surface.
/// </para>
/// <para>
/// <b>Every consumer must use this, or they disagree about the geometry.</b>
/// <see cref="HalfEdgeMesh.SignedVolume"/> fans a face to measure it and
/// <see cref="RenderMesh"/> fans it to draw it; if those two picked different diagonals,
/// the reported volume would be of a solid nobody ever sees. This is the same lesson the
/// tessellation audit learned from the other direction — a quality audit must fan
/// polygons exactly as the consumer does.
/// </para>
/// </remarks>
public static class PolygonFan
{
    /// <summary>
    /// How much shorter the 1–3 diagonal has to measure before it wins, as a relative
    /// slack on the SQUARED lengths. A scale-free guard, not a length: it exists to stop
    /// round-off deciding the split of a cell whose diagonals are mathematically equal
    /// (see the type's remarks), so it is sized against relative machine epsilon rather
    /// than against any model tolerance, and it never enters a comparison between
    /// genuinely different diagonals — a sheared helical band's differ by ratios up to
    /// 40:1.
    /// </summary>
    public const double RelativeTie = 1e-12;

    /// <summary>
    /// The corner index a quad <c>(p0, p1, p2, p3)</c> should be fanned from: 0 unless the
    /// 1–3 diagonal is shorter than the 0–2 diagonal by more than <see cref="RelativeTie"/>.
    /// </summary>
    public static int QuadApex(in Vector3d p0, in Vector3d p1, in Vector3d p2, in Vector3d p3)
    {
        double across02 = (p2 - p0).LengthSquared;
        double across13 = (p3 - p1).LengthSquared;
        return across13 < across02 * (1 - RelativeTie) ? 1 : 0;
    }

    /// <summary>The corner index <paramref name="loop"/> should be fanned from.</summary>
    public static int Apex(ReadOnlySpan<Vector3d> loop) =>
        loop.Length == 4 ? QuadApex(loop[0], loop[1], loop[2], loop[3]) : 0;

    /// <inheritdoc cref="Apex(ReadOnlySpan{Vector3d})"/>
    public static int Apex(IReadOnlyList<Vector3d> loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        return loop.Count == 4 ? QuadApex(loop[0], loop[1], loop[2], loop[3]) : 0;
    }

    /// <summary>
    /// The corner index a loop given as indices into <paramref name="positions"/> should
    /// be fanned from.
    /// </summary>
    public static int Apex(ReadOnlySpan<int> loop, IReadOnlyList<Vector3d> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        return loop.Length == 4
            ? QuadApex(positions[loop[0]], positions[loop[1]], positions[loop[2]], positions[loop[3]])
            : 0;
    }

    /// <summary>
    /// The loop position of the <paramref name="offset"/>-th vertex of an
    /// <paramref name="degree"/>-gon fanned from <paramref name="apex"/> — i.e. the fan's
    /// triangles are <c>(Corner(a, n, 0), Corner(a, n, i), Corner(a, n, i + 1))</c> for
    /// <c>i</c> in <c>1 .. n − 2</c>.
    /// </summary>
    public static int Corner(int apex, int degree, int offset) => (apex + offset) % degree;
}
