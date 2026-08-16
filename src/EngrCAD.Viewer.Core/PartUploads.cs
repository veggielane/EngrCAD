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
// written out three times: a part carrying a displacement used to draw NO feature-edge
// overlay at any factor. That rule is RETIRED — the edges now carry their own
// displacement attribute and follow the same uDeformScale the fills do, so they are
// drawn at every factor and correct at every factor. See BuildFeatureEdgeDeformation.

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

    /// <summary>Collect <c>Part.GetFeatureEdges()</c> for the edge overlay. A deformed
    /// part's edges come with <see cref="PartUpload.FeatureEdgeDeformation"/> so the
    /// overlay follows the displaced shape.</summary>
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
/// <param name="WireColors">Per-line-vertex field colours for the wireframe (RGB per
/// endpoint, two endpoints per segment, parallel to <paramref name="WireEdges"/>) — or
/// null when the part shows no field, or shows a CELL-associated one (a mesh edge
/// borders two faces, so "which cell's colour" has no answer at an endpoint; the
/// wireframe then keeps the part colour, honestly).</param>
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
    float[]? FeatureEdgeDeformation,
    IReadOnlyList<(Vector3d A, Vector3d B)> WireEdges,
    float[]? WireColors,
    float[]? WireDeformation,
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
            if (FieldRendering.TryBuild(
                part, render, mesh.VertexCount, out var built, out string? error,
                mesh.FaceCount))
                field = built;
            else
                fieldError = error;   // null when the part simply shows no field
        }

        var occlusion = request.Occlusion?.Invoke(mesh, render);

        var featureEdges = BuildFeatureEdges(part, request);
        return new PartUpload(
            part, mesh, render, field, fieldError, occlusion,
            featureEdges,
            BuildFeatureEdgeDeformation(mesh, featureEdges, field),
            request.WireEdges ? WireframeEdges.Extract(mesh) : [],
            request.WireEdges ? BuildWireColors(mesh, field) : null,
            request.WireEdges ? BuildWireDeformation(mesh, field) : null,
            // Picking follows what is DRAWN: the BVH is built once over the triangles
            // displaced at the part's own exaggeration, and the raw displacement rides
            // along so ScenePick answers EXACTLY at any animation factor (the
            // deformed-ray correction) with the index never rebuilt.
            request.Pick
                ? field is { } f ? PickMesh.Build(render, f) : PickMesh.Build(render)
                : null);
    }

    /// <summary>The edge overlay's segments. A displaced part's edges are drawn too —
    /// they carry <see cref="PartUpload.FeatureEdgeDeformation"/> and follow the
    /// displacement through the line program's own attribute, so the draw list still
    /// never depends on an animation's t (the rule that lets a clip reuse one
    /// upload).</summary>
    private static IReadOnlyList<(Vector3d A, Vector3d B)> BuildFeatureEdges(
        Part part, in PartUploadRequest request) =>
        request.FeatureEdges ? part.GetFeatureEdges(request.Quality) : [];

    /// <summary>
    /// Per-endpoint displacement vectors for the feature-edge overlay (3 floats each,
    /// segment order — the line program's <c>aDeformOffset</c>), or null for a part
    /// with no displacement, which keeps the incumbent upload bit-identical.
    /// <para>An edge sample is an exact B-Rep curve point, NOT a mesh vertex, so its
    /// displacement is interpolated: the nearest source-mesh triangle's corners weighted
    /// barycentrically (<see cref="MeshProjectionTarget.TryInterpolate"/>). Exact for
    /// any affine displacement field, and within the fill's own facet interpolation
    /// otherwise — the edge sits on the displaced surface to the same order the shaded
    /// facets do.</para>
    /// </summary>
    private static float[]? BuildFeatureEdgeDeformation(
        HalfEdgeMesh mesh, IReadOnlyList<(Vector3d A, Vector3d B)> edges, FieldMeshData? field)
    {
        if (edges.Count == 0 || field is not { Deformed: true } f
            || f.Display.Deform is not { } displacement)
            return null;
        var target = new MeshProjectionTarget(mesh);
        var result = new float[edges.Count * 6];
        for (int i = 0; i < edges.Count; i++)
        {
            var (a, b) = edges[i];
            WriteOffset(result, i * 6, InterpolatedOffset(target, displacement, a));
            WriteOffset(result, i * 6 + 3, InterpolatedOffset(target, displacement, b));
        }
        return result;
    }

    private static Vector3d InterpolatedOffset(
        MeshProjectionTarget target, MeshField displacement, in Vector3d point)
    {
        if (!target.TryInterpolate(point, out var corners, out var weights))
            return Vector3d.Zero;
        return displacement.VectorAt(corners.A) * weights.A
             + displacement.VectorAt(corners.B) * weights.B
             + displacement.VectorAt(corners.C) * weights.C;
    }

    private static void WriteOffset(float[] buffer, int at, in Vector3d offset)
    {
        buffer[at] = (float)offset.X;
        buffer[at + 1] = (float)offset.Y;
        buffer[at + 2] = (float)offset.Z;
    }

    /// <summary>
    /// The wireframe's per-endpoint displacement vectors — the exact twin of
    /// <see cref="BuildWireColors"/>, and simpler than the feature edges' builder
    /// because a wireframe endpoint IS a source vertex: no interpolation, just
    /// <c>VectorAt</c> through the same <c>ExtractIndexed</c> pairs the colours use.
    /// Null for a part with no displacement.
    /// </summary>
    private static float[]? BuildWireDeformation(HalfEdgeMesh mesh, FieldMeshData? field)
    {
        if (field is not { Deformed: true } f || f.Display.Deform is not { } displacement)
            return null;
        var indexed = WireframeEdges.ExtractIndexed(mesh);
        var result = new float[indexed.Count * 6];
        for (int i = 0; i < indexed.Count; i++)
        {
            var (a, b) = indexed[i];
            WriteOffset(result, i * 6, displacement.VectorAt(a));
            WriteOffset(result, i * 6 + 3, displacement.VectorAt(b));
        }
        return result;
    }

    /// <summary>
    /// The wireframe's per-endpoint field colours: the segments walk the SOURCE
    /// half-edge mesh, so each endpoint takes its source vertex's colour from the same
    /// <c>FieldRendering.SourceColors</c> the fills are built from — a wireframe reading
    /// of a result cannot disagree with the shaded one. Null for a part with no field,
    /// and for a CELL-associated one (an edge borders two faces; no endpoint colour is
    /// well-defined, so the wireframe keeps the part colour).
    /// </summary>
    private static float[]? BuildWireColors(HalfEdgeMesh mesh, FieldMeshData? field)
    {
        if (field is not { } f || f.Display.Field.Association != FieldAssociation.Vertex)
            return null;
        var display = f.Display;
        var perSource = FieldRendering.SourceColors(
            display.Field, display.Range, display.ColorMap, display.LogScale);
        var indexed = WireframeEdges.ExtractIndexed(mesh);
        var colors = new float[indexed.Count * 6];
        for (int i = 0; i < indexed.Count; i++)
        {
            var (a, b) = indexed[i];
            var (ra, ga, ba) = perSource[a];
            var (rb, gb, bb) = perSource[b];
            int at = i * 6;
            colors[at + 0] = ra; colors[at + 1] = ga; colors[at + 2] = ba;
            colors[at + 3] = rb; colors[at + 4] = gb; colors[at + 5] = bb;
        }
        return colors;
    }
}
