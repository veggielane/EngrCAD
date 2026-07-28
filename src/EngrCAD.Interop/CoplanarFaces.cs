using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Interop;

/// <summary>How a face sits against the other solid's coincident surface.</summary>
internal enum Coincidence
{
    /// <summary>Not on the other solid's boundary; classify it by the usual inside/outside probe.</summary>
    None,

    /// <summary>Flush, outward normals agreeing — both solids occupy the same side of this surface.</summary>
    SameNormal,

    /// <summary>Flush, outward normals opposing — the solids mate here, each on its own side.</summary>
    OppositeNormal,
}

/// <summary>
/// The planar part of one solid's boundary that the other solid's boundary lies on top of —
/// the B-Rep counterpart of the mesh boolean's <c>CoincidentSurface</c>, and deliberately the
/// same model: the ordinary transversal path imprints the shared region's RIM (a coplanar
/// patch ends where the other solid's surface leaves the shared plane, and that pair is
/// transverse and does produce a curve), so after splitting every fragment is wholly
/// coincident or wholly not, and one probe decides for the whole fragment.
///
/// <para><b>Scope: coplanar PLANES.</b> Flush embossing, stacked plates, blocks butted
/// together and pockets whose floor is the host's own face are the overwhelmingly common
/// coincident inputs, and a plane is the one carrier whose overlap this can decide without
/// surface–surface re-intersection. Coincident CURVED surface — a shaft in a bore of the same
/// diameter, a flange band tangent to a sheet — is refused by name in
/// <see cref="BrepBoolean"/> rather than approximated.</para>
///
/// <para><b>Why the probe is legal exactly where the SDF probe is not.</b> A fragment lying on
/// the other solid's boundary reads distance ~0 from that solid's mesh SDF, so the
/// inside/outside test decides by rounding — the B-Rep twin of the mesh boolean's winding
/// number being ½ there. Normal agreement is the set algebra of the shared surface and needs
/// no distance at all.</para>
/// </summary>
internal sealed class CoplanarFaces
{
    private readonly record struct PlanarFace(BrepFace Face, Vector3d Origin, Vector3d Normal);

    private readonly List<PlanarFace> _faces;

    private CoplanarFaces(List<PlanarFace> faces) => _faces = faces;

    /// <summary>
    /// Indexes a solid's planar faces (<see cref="BrepQueries.IsPlanar"/> recognition, so
    /// extruded straight generators count too — a sketch pocket's wall is a plane). Normals
    /// are OUTWARD, reversal applied.
    /// </summary>
    public static CoplanarFaces For(BrepSolid solid)
    {
        var faces = new List<PlanarFace>();
        foreach (var face in solid.Faces)
        {
            if (face.IsPlanar(out var origin, out var normal) &&
                normal.TryNormalize(Tolerance.Default, out var unit))
                faces.Add(new PlanarFace(face, origin, unit));
        }
        return new CoplanarFaces(faces);
    }

    /// <summary>Outward unit normal of a face, or null when it is not planar.</summary>
    public static Vector3d? OutwardNormal(BrepFace face) =>
        face.IsPlanar(out _, out var normal) && normal.TryNormalize(Tolerance.Default, out var unit)
            ? unit
            : null;

    /// <summary>
    /// Whether two planar faces carry the SAME plane. Normals may oppose — a plane has no
    /// preferred side, and the mating case (an emboss's underside against its host's top) is
    /// exactly the opposing one.
    /// <para>The 1e-7 seam tier is the right rung: the two faces were built independently, by
    /// different construction chains, and coincide only to however exactly the caller placed
    /// them. The offset test is a LENGTH (a point's distance to the other plane), so it is not
    /// scale-limited the way an area or determinant test would be.</para>
    /// </summary>
    public static bool SamePlane(
        in Vector3d originA, in Vector3d normalA, in Vector3d originB, in Vector3d normalB)
    {
        double alignment = normalA.Dot(normalB);
        if (Math.Abs(Math.Abs(alignment) - 1) > Tolerance.Default.Angular)
            return false;
        return Math.Abs(normalA.Dot(originB - originA)) <= FaceGeometry.SeamTolerance;
    }

    /// <summary>
    /// Whether <paramref name="probe"/> lies on a plane this solid carries and inside that
    /// face's trim, and if so how the two outward normals relate. Only the SIGN of the dot
    /// product is read: the caller has already established the normals are parallel, so there
    /// is no magnitude and no tolerance. An exactly-zero dot cannot occur for unit normals
    /// that passed <see cref="SamePlane"/>.
    /// </summary>
    public Coincidence Classify(in Vector3d probe, in Vector3d outwardNormal)
    {
        foreach (var (face, origin, normal) in _faces)
        {
            if (Math.Abs(Math.Abs(normal.Dot(outwardNormal)) - 1) > Tolerance.Default.Angular)
                continue;
            if (Math.Abs(normal.Dot(probe - origin)) > FaceGeometry.SeamTolerance)
                continue;
            if (!FaceGeometry.Contains(face, probe))
                continue;
            return normal.Dot(outwardNormal) > 0 ? Coincidence.SameNormal : Coincidence.OppositeNormal;
        }
        return Coincidence.None;
    }

    /// <summary>
    /// The set algebra of the surface two solids share, transcribed from the mesh boolean's
    /// <c>KeepCoincident</c> so the two engines cannot disagree about what a flush mate means.
    /// With normals AGREEING both solids lie on the same side, so the surface bounds the union
    /// and the intersection (one copy) and vanishes from the difference — locally A minus B
    /// removes all of A's material. With normals OPPOSING the solids mate back to back: union
    /// and intersection bury the surface inside the result, while the difference leaves the
    /// first solid untouched and keeps its copy.
    /// <para><b>The asymmetry is deliberate.</b> Both solids cover the region, so exactly one
    /// copy can ever survive and it is always the FIRST solid's: the second's is redundant when
    /// they agree and back-to-front when they oppose. (A difference reverses the second solid's
    /// faces, so keeping ITS copy would also be geometrically right — but keeping A's needs no
    /// special case, and it keeps the surviving face's edges the ones the first solid's
    /// neighbours already reference.)</para>
    /// </summary>
    public static bool Keep(Coincidence coincidence, bool difference, bool fromA)
    {
        if (!fromA)
            return false;
        return coincidence == Coincidence.SameNormal ? !difference : difference;
    }
}
