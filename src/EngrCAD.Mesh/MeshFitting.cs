using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Shape fitting that needs a mesh to start from — the bridge between
/// <see cref="ConvexHull"/> and <see cref="Fitting3d"/>.
///
/// <para><b>Why the bridge lives here and not in Core.</b> <see cref="Fitting3d.MinVolumeBox"/>
/// deliberately takes its hull as plain vertices + index triples: EngrCAD.Core owns no
/// polyhedron type, and the 3D quickhull speaks <see cref="HalfEdgeMesh"/>, so pulling it
/// down would drag half-edge topology into the dependency-free foundation. The cost of
/// that (right) decision is that every caller repeats the same
/// <c>Compute(...).Triangulated().ToIndexed()</c> flattening dance; these overloads are
/// that dance, written once on the side of the boundary that already knows about meshes.</para>
/// </summary>
public static class MeshFitting
{
    /// <summary>
    /// The exact minimum-volume oriented box of a CONVEX HULL mesh (O'Rourke's search —
    /// see <see cref="Fitting3d.MinVolumeBox"/> for the algorithm, its cost, and why the
    /// 2D calipers theorem does not lift to 3D).
    /// <para>The mesh is taken to be a hull already: it is triangulated and flattened, not
    /// re-hulled. Feeding a non-convex mesh is not an error and the box still contains
    /// every vertex, but the candidate families come from a shape that is not the hull, so
    /// the result is only a fit, not the minimum. Use
    /// <see cref="MinVolumeBoxOf(IReadOnlyList{Vector3d})"/> when the input is a raw
    /// cloud.</para>
    /// </summary>
    public static OrientedBox3d MinVolumeBox(HalfEdgeMesh hull)
    {
        ArgumentNullException.ThrowIfNull(hull);
        var (positions, faces) = hull.Triangulated().ToIndexed();
        var triangles = new int[faces.Count * 3];
        for (int i = 0; i < faces.Count; i++)
        {
            var face = faces[i];
            // Triangulated() guarantees triangles; a longer face here would mean the mesh
            // changed under us, and silently fanning it would hide that.
            if (face.Length != 3)
                throw new ArgumentException(
                    $"A triangulated hull face has {face.Length} vertices.", nameof(hull));
            triangles[3 * i] = face[0];
            triangles[3 * i + 1] = face[1];
            triangles[3 * i + 2] = face[2];
        }
        return Fitting3d.MinVolumeBox(positions, triangles);
    }

    /// <summary>
    /// The exact minimum-volume oriented box of a point cloud: its convex hull, then
    /// <see cref="MinVolumeBox(HalfEdgeMesh)"/>. The hull step is what makes the cost
    /// tolerable — the search is quadratic in the number of distinct hull edge directions,
    /// so it must never see the raw cloud.
    /// </summary>
    public static OrientedBox3d MinVolumeBoxOf(IReadOnlyList<Vector3d> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        return MinVolumeBox(ConvexHull.Compute(points));
    }
}
