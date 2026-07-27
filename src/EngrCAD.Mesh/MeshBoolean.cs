using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>Which combination of the two solids to keep.</summary>
internal enum BooleanOperation
{
    Union,
    Difference,
    Intersection,
}

/// <summary>
/// Boolean operations on closed meshes. Inputs are triangulated internally; both must be
/// closed with outward winding.
/// <para>
/// The algorithm is the imprint boolean: cut both meshes along their exact common curve
/// (<see cref="MeshMeshCut"/>), classify each surface patch by the other mesh's generalized
/// winding number — or, where the two solids share boundary instead of crossing it, by
/// normal agreement (<see cref="CoincidentSurface"/>) — keep the halves the operation calls
/// for, and weld by exact coordinate equality. Every guard is relative to the operands'
/// extent, so it is scale-free and survives near-tangency.
/// </para>
/// <para>
/// A BSP-tree clipper (csg.js) used to sit behind a <c>BooleanMethod</c> selector as the
/// alternative. It was retired once coincident (coplanar-overlapping) surface was
/// classified rather than refused, which was the last thing it did that the imprint path
/// could not: every constant in it was absolute, so it discarded every polygon of a model
/// at ~1e-5 scale and left boundary edges wherever two surfaces came within 1e-9 of
/// tangency, and on a 32k+32k sphere union it took 74.9 s to return an *open* 347k-face
/// shell against 0.71 s closed here. The cases it got wrong are still locked by tests in
/// <c>ExactBooleanTests</c>; the lessons its absolute epsilons taught live on in the
/// scale-free guards throughout this engine.
/// </para>
/// </summary>
public static class MeshBoolean
{
    /// <summary>
    /// Everything in either solid. <paramref name="progress"/> reports and cancels
    /// cooperatively.
    /// </summary>
    public static HalfEdgeMesh Union(HalfEdgeMesh a, HalfEdgeMesh b, ProgressCancel? progress = null) =>
        MeshBooleanExact.Combine(a, b, BooleanOperation.Union, progress);

    /// <summary>Everything in <paramref name="a"/> but not in <paramref name="b"/>.</summary>
    public static HalfEdgeMesh Difference(HalfEdgeMesh a, HalfEdgeMesh b, ProgressCancel? progress = null) =>
        MeshBooleanExact.Combine(a, b, BooleanOperation.Difference, progress);

    /// <summary>Everything in both solids.</summary>
    public static HalfEdgeMesh Intersection(HalfEdgeMesh a, HalfEdgeMesh b, ProgressCancel? progress = null) =>
        MeshBooleanExact.Combine(a, b, BooleanOperation.Intersection, progress);
}
