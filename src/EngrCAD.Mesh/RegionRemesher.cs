using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>Outcome of <see cref="RegionRemesher.Remesh"/>.</summary>
/// <param name="Mesh">The whole model, with the region replaced.</param>
/// <param name="Region">The reinserted faces, ready for another edit.</param>
/// <param name="Patch">What the remesh of the extracted patch itself did.</param>
public sealed record RegionRemeshResult(
    HalfEdgeMesh Mesh, MeshFaceSelection Region, RemeshResult Patch);

/// <summary>
/// Isotropic remeshing of one face selection, in place (geometry3Sharp's
/// <c>RegionRemesher</c>): pull the region out with <see cref="MeshRegionOperator"/>, remesh
/// it with its seam pinned, stitch it back. The rest of the model is untouched — which is
/// what makes remeshing usable on a real part, where one bore wall wants 0.2 mm triangles and
/// the rest of the plate does not want to be touched at all.
/// <code>
/// var result = RegionRemesher.Remesh(mesh, selection, new RemeshOptions(0.5) { Iterations = 10 });
/// </code>
/// <para>
/// <b>Two things are decided for the caller, and both follow from the seam contract.</b>
/// <see cref="RemeshOptions.PreserveBoundary"/> is forced on — the region's rim is what the
/// surrounding mesh is welded to, so it may gain vertices but never move — and when
/// <see cref="RemeshOptions.ProjectionTarget"/> is left null, one is built over the region as
/// extracted. Without a target, smoothing is curvature flow and the patch sinks away from the
/// surface it belongs to, leaving a visible dent bounded by an unmoved rim; a target of the
/// original patch is the answer that changes the tessellation and leaves the shape.
/// </para>
/// <para>
/// The rim may be <b>refined</b>: with <see cref="RemeshOptions.SplitFixedEdges"/> left on
/// (the default), splits along the seam are carried into the neighbouring faces by
/// <see cref="MeshRegionOperator.Reinsert"/>, so a fine region can meet a coarse model without
/// a T-junction. Set it false to have the rim come back vertex for vertex.
/// </para>
/// <para>
/// <see cref="HoleFiller.FillSmoothed"/> does something that looks similar and is deliberately
/// not this: it builds a patch that does not exist yet, remeshes it standalone and stitches it
/// in, where this one edits geometry the model already has. The shared machinery is the pinned
/// rim; the difference is whether there is anything to extract.
/// </para>
/// </summary>
public static class RegionRemesher
{
    /// <summary>
    /// Remeshes <paramref name="region"/> of <paramref name="mesh"/> and returns the whole
    /// model with the result stitched back in.
    /// </summary>
    /// <param name="mesh">The model. Never modified.</param>
    /// <param name="region">The faces to remesh; must extract to a manifold patch.</param>
    /// <param name="options">
    /// Remesh settings. <see cref="RemeshOptions.PreserveBoundary"/> is forced on, and
    /// <see cref="RemeshOptions.ProjectionTarget"/> defaults to the extracted region.
    /// <see cref="RemeshOptions.FixedVertices"/> are indices into <paramref name="mesh"/>;
    /// ones outside the region are ignored.
    /// </param>
    /// <param name="progress">Optional cooperative progress/cancellation, polled per pass.</param>
    /// <exception cref="ArgumentException">
    /// The selection does not extract to a manifold patch, or the remeshed patch cannot be
    /// stitched back (which, with the boundary pinned, means the region itself came apart).
    /// </exception>
    public static RegionRemeshResult Remesh(
        HalfEdgeMesh mesh, MeshFaceSelection region, RemeshOptions options, ProgressCancel? progress = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(options);

        var session = MeshRegionOperator.Extract(mesh, region);
        var patchOptions = options with
        {
            PreserveBoundary = true,
            ProjectionTarget = options.ProjectionTarget ?? new MeshProjectionTarget(session.Region),
            FixedVertices = MapFixedVertices(session, options.FixedVertices),
        };

        var patch = Remesher.Remesh(session.Region, patchOptions, progress);
        var reinserted = session.Reinsert(patch.Mesh);
        return new RegionRemeshResult(reinserted.Base, reinserted.Selection, patch);
    }

    /// <summary>Remeshes the faces with the given indices.</summary>
    public static RegionRemeshResult Remesh(
        HalfEdgeMesh mesh, IEnumerable<int> faceIndices, RemeshOptions options, ProgressCancel? progress = null) =>
        Remesh(mesh, MeshFaceSelection.FromIndices(mesh, faceIndices), options, progress);

    /// <summary>
    /// Translates caller-supplied pins from base indices to region indices. The map runs the
    /// other way, so it is inverted here rather than asking the caller to speak in indices of
    /// a patch they have not seen.
    /// </summary>
    private static IReadOnlyCollection<int>? MapFixedVertices(
        MeshRegionOperator session, IReadOnlyCollection<int>? baseVertices)
    {
        if (baseVertices is null || baseVertices.Count == 0)
            return null;

        var wanted = new HashSet<int>(baseVertices);
        var mapped = new List<int>();
        for (int v = 0; v < session.RegionToBaseVertex.Count; v++)
        {
            if (wanted.Contains(session.RegionToBaseVertex[v]))
                mapped.Add(v);
        }
        return mapped;
    }
}
