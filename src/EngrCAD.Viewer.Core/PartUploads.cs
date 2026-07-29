using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

// The CPU half of "draw this part": everything the window, the offscreen pass and the
// browser client each used to compute by hand before touching a GL binding.
//
// Three front ends built the same five things per part -- RenderMesh.CreateFlat, the
// FieldRendering.TryBuild result, the baked occlusion array, Part.GetFeatureEdges and
// WireframeEdges.Extract, plus a PickMesh -- and every one is a pure function of
// (Part, quality, which pieces are wanted, where occlusion comes from). So it extracts,
// and the ~40 lines each pass repeated become one call.
//
// TWO THINGS THIS DELIBERATELY DOES NOT DO, and both are why the extraction waited:
//
//  * It does NOT decide WHICH pieces to build. That is a genuine per-front-end policy,
//    not an accident: the one-shot offscreen pass skips what its resolved mode cannot
//    use (it has no dropdown to change its mind), while the window and the browser build
//    all of them precisely so a style dropdown never re-uploads. A shared "what to
//    build" rule would silently make one of those wrong. The CALLER states its policy in
//    a PartUploadRequest and this carries it out.
//
//  * It does NOT own a cache. Uploads are keyed by Part reference in all three, but the
//    LIFETIMES differ -- the browser releases on tab switch, the window on GL deinit,
//    the offscreen pass when its one context dies -- so each front end keeps its own
//    dictionary. (The lifecycle is also why the larger "ViewerModel" was assessed and
//    declined; see todo.md. The window streams through TabMeshLoader on two threads,
//    offscreen is one-shot and synchronous, and the browser interleaves awaited JS
//    uploads on one thread. That part must NOT look the same.)
//
// What DOES belong here is every rule about the CONTENT, and one of them had been
// written out three times: a part carrying a displacement draws NO feature-edge overlay
// at any factor. See BuildFeatureEdges.

/// <summary>
/// Which pieces of a part's upload a front end wants, and where baked occlusion comes
/// from. Every field is the caller's policy — see the file header for why this is not
/// decided centrally.
/// </summary>
public readonly record struct PartUploadRequest
{
    /// <summary>Resolve the part's <c>FieldDisplay</c> into colour and displacement
    /// buffers. False leaves <see cref="PartUpload.Field"/> null, which is how
    /// <c>RenderToImage(fields: false)</c> takes a geometry figure of a model that
    /// carries results.</summary>
    public bool Fields { get; init; }

    /// <summary>Collect <c>Part.GetFeatureEdges()</c> for the edge overlay. Note this is
    /// permission, not a guarantee: a deformed part gets none regardless (see
    /// <see cref="PartUpload.FeatureEdges"/>).</summary>
    public bool FeatureEdges { get; init; }

    /// <summary>Collect every unique mesh edge, for the wireframe display mode.</summary>
    public bool WireEdges { get; init; }

    /// <summary>Build the triangle BVH picking raycasts against.</summary>
    public bool Pick { get; init; }

    /// <summary>
    /// Where the per-vertex baked occlusion comes from, or null for none.
    /// <para>A delegate rather than a flag because the two desktop passes ask genuinely
    /// different questions: the window asks a <i>never-bake</i> cache read (so an upload
    /// can never stall the render thread — an unbaked part goes up with no buffer, reads
    /// the context constant 1.0, and is exactly the flat-lit shading until the background
    /// bake lands), while the one-shot offscreen pass bakes inline because it must be
    /// deterministic. The browser passes null: there is no bake there.</para>
    /// </summary>
    public Func<HalfEdgeMesh, RenderMesh, float[]?>? Occlusion { get; init; }

    /// <summary>Mesh quality, or null for the part's default. One value for both
    /// <c>GetMesh</c> and <c>GetFeatureEdges</c>, so an adaptive criterion cannot
    /// tessellate the fill and the exact edge overlay at different densities.</summary>
    public MeshQuality? Quality { get; init; }

    /// <summary>
    /// Every piece: fields, both edge sets and the pick BVH (occlusion still has to be
    /// supplied). What a front end with a live view-style dropdown wants — but that
    /// reasoning belongs to the caller; this is only the spelling.
    /// </summary>
    public static PartUploadRequest All { get; } = new()
    {
        Fields = true,
        FeatureEdges = true,
        WireEdges = true,
        Pick = true,
    };
}

/// <summary>
/// One distinct part's CPU-side render data — what a front end turns into GL buffers.
/// Built by <see cref="PartUploads.Build"/>; a piece the request did not ask for is null
/// or empty.
/// </summary>
/// <param name="Part">The part this was built from (the reference every front end's
/// upload cache keys on).</param>
/// <param name="Mesh">The part's cached display mesh.</param>
/// <param name="Render">The flat render mesh — three vertices per triangle, carrying
/// <see cref="RenderMesh.SourceVertices"/> so a per-source-vertex result maps onto the
/// duplicates exactly.</param>
/// <param name="Field">The resolved field display's colour and displacement buffers, or
/// null when the part shows no field (or the request declined to resolve one).</param>
/// <param name="FieldError">Why a display that WAS asked for could not be honoured — a
/// result an edit removed, a field of the wrong length. Null both when nothing was asked
/// for and when everything worked, the same "nothing to show" versus "it went wrong"
/// distinction <c>FieldRendering.TryBuild</c> draws. A front end with somewhere to say it
/// (a status bar) reports it; the headless passes discard it.</param>
/// <param name="Occlusion">Baked per-vertex ambient occlusion, or null — in which case
/// the attribute reads the context constant 1.0, which is exactly the AO-off shading.</param>
/// <param name="FeatureEdges">The edge overlay's segments: a B-Rep-backed part's ACTUAL
/// B-Rep edges (so a bore rim stays a smooth circle at any tessellation), else mesh
/// dihedrals. <b>Empty for a part carrying a displacement</b> — see
/// <see cref="PartUploads"/>.</param>
/// <param name="WireEdges">Every unique mesh edge, for the wireframe display mode.</param>
/// <param name="Pick">The triangle BVH a raycast tests, or null when none was asked
/// for. Built at the part's OWN deformation scale (<c>FieldRendering.PickShape</c>).</param>
public sealed record PartUpload(
    Part Part,
    HalfEdgeMesh Mesh,
    RenderMesh Render,
    FieldMeshData? Field,
    string? FieldError,
    float[]? Occlusion,
    IReadOnlyList<(Vector3d A, Vector3d B)> FeatureEdges,
    IReadOnlyList<(Vector3d A, Vector3d B)> WireEdges,
    PickMesh? Pick)
{
    /// <summary>Indices in the mesh's element buffer.</summary>
    public int IndexCount => Render.Indices.Length;

    /// <summary>Vertices the feature-edge line draw covers (two per segment).</summary>
    public int FeatureEdgeVertexCount => FeatureEdges.Count * 2;

    /// <summary>Vertices the wireframe line draw covers (two per segment).</summary>
    public int WireEdgeVertexCount => WireEdges.Count * 2;

    /// <summary>Whether this part's fill takes its colour from a result rather than from
    /// <c>Part.Color</c> (the <c>uFieldColor</c> strength uniform).</summary>
    public bool FieldColored => Field is not null;

    /// <summary>The part's own exaggeration — the <c>uDeformScale</c> uniform before any
    /// animation factor multiplies it, and 0 when nothing is displaced.</summary>
    public double DeformScale => Field?.DeformScale ?? 0;

    /// <summary>Whether the undeformed shape should also be drawn, ghosted behind the
    /// deformed one.</summary>
    public bool ShowGhost => Field is { ShowGhost: true };

    /// <summary>
    /// <see cref="Pick"/> for a caller that asked for one. Throws rather than letting a
    /// missing BVH travel: a part with no pick data is not a visible defect, it is a
    /// model that quietly cannot be clicked, which is exactly the kind of bug that ships.
    /// </summary>
    public PickMesh RequirePick => Pick ?? throw new InvalidOperationException(
        $"Part '{Part.Name}': no pick geometry was built (PartUploadRequest.Pick was false).");
}

/// <summary>
/// Builds the CPU-side render data for one part — the call all three front ends make
/// before they touch a GL binding.
/// </summary>
public static class PartUploads
{
    /// <summary>
    /// Meshes the part (through its own cache), builds the flat render mesh, and then
    /// builds exactly the pieces <paramref name="request"/> asked for.
    /// <para>Pure and thread-safe with respect to GL: nothing here binds a context, so a
    /// front end may call it on a worker and upload on its render thread.</para>
    /// </summary>
    public static PartUpload Build(Part part, in PartUploadRequest request)
    {
        ArgumentNullException.ThrowIfNull(part);
        var mesh = part.GetMesh(request.Quality);
        var render = RenderMesh.CreateFlat(mesh);

        // Simulation results: a colour buffer for attribute 3 and, for a deformed-shape
        // display, the displacement attributes for 4-7. The geometry is the UNDEFORMED
        // mesh either way -- the vertex shader displaces it from a uniform, which is what
        // keeps an animated result off the re-upload path.
        FieldMeshData? field = null;
        string? fieldError = null;
        if (request.Fields)
        {
            if (FieldRendering.TryBuild(part, render, mesh.VertexCount, out var built, out string? error))
                field = built;
            else
                fieldError = error;   // null when the part simply shows no field
        }

        var occlusion = request.Occlusion?.Invoke(mesh, render);

        return new PartUpload(
            part, mesh, render, field, fieldError, occlusion,
            BuildFeatureEdges(part, request, field),
            request.WireEdges ? WireframeEdges.Extract(mesh) : [],
            // Picking follows what is DRAWN at the part's own exaggeration: a BVH is a
            // spatial index, so unlike the shading it cannot be a uniform, and it is built
            // once over the displaced triangles (FieldRendering.PickShape states what that
            // costs while an animation is running).
            request.Pick
                ? PickMesh.Build(field is { } f ? FieldRendering.PickShape(render, f) : render)
                : null);
    }

    /// <summary>
    /// The edge overlay's segments — and the one content rule that had been written out
    /// once per front end.
    /// <para><b>A part carrying a displacement gets NO overlay, at any factor.</b> Those
    /// edges describe geometry that has moved, so drawing them over the displaced shape
    /// would be a wrong outline rather than a coarse one. The test is whether the part
    /// CARRIES a displacement, never what the scale happens to be at this instant: the
    /// draw list must not depend on an animation's t, which is exactly what lets a whole
    /// clip reuse one upload.</para>
    /// </summary>
    private static IReadOnlyList<(Vector3d A, Vector3d B)> BuildFeatureEdges(
        Part part, in PartUploadRequest request, FieldMeshData? field) =>
        request.FeatureEdges && field is not { Deformed: true }
            ? part.GetFeatureEdges(request.Quality)
            : [];
}
