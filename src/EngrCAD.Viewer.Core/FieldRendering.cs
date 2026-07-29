using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

// Turning a Part's simulation results into the buffers a render pass uploads. Pure --
// no GL, no viewport state -- so the window, the headless pass and the browser client
// build IDENTICAL vertex data from the same call, which is what the RenderCore split
// exists to guarantee.
//
// Two things ride here, and they behave differently on purpose:
//
//  * COLOUR is a vertex attribute (aFieldColor, slot 3) with a constant-when-absent
//    rule copied verbatim from baked occlusion: a mesh uploaded with no colour buffer
//    reads a context constant, and the shader's uFieldColor strength uniform is 0, so
//    a part with no results renders BYTE-IDENTICALLY to before this existed. That is
//    not a hope -- the docs PNGs are the oracle.
//
//  * DEFORMATION is new geometry. It cannot ride the pose path the exploded view and
//    the animation transport use (matrices only, no buffer touched), because a
//    displaced shape is a different mesh, not a different placement. So it re-uploads,
//    deliberately and explicitly, and it is kept off the animation path.

/// <summary>
/// The per-part vertex data a field display contributes: the colour buffer every render
/// pass uploads as attribute 3, and — when the display asks for a deformed shape — the
/// displaced mesh to upload instead of the original.
/// </summary>
/// <param name="Colors">RGB per render-mesh vertex (3 floats each), in the render
/// mesh's own vertex order.</param>
/// <param name="Deformed">The displaced render mesh, or null when the display shows the
/// undeformed shape. Same vertex order and index buffer as the source, so
/// <paramref name="Colors"/> applies to it unchanged.</param>
/// <param name="Display">The resolved display this was built from (the field, its
/// range and map — what a legend and a properties panel read).</param>
public readonly record struct FieldMeshData(
    float[] Colors, RenderMesh? Deformed, ResolvedFieldDisplay Display)
{
    /// <summary>Whether the undeformed shape should also be drawn, ghosted (only
    /// meaningful when <see cref="Deformed"/> is non-null).</summary>
    public bool ShowGhost => Deformed is not null && Display.ShowUndeformed;
}

/// <summary>
/// Builds the vertex data a field-coloured part draws with — shared by every front end.
/// </summary>
public static class FieldRendering
{
    /// <summary>
    /// The <c>uFieldColor</c> uniform when a part IS field-coloured. 1 replaces the
    /// part colour outright: a result plot is about the values, and blending the part's
    /// own colour in would shift every reading away from the legend.
    /// </summary>
    public const float Strength = 1f;

    /// <summary>
    /// Alpha the undeformed ghost draws at. Fainter than
    /// <c>DisplayMode.Translucent</c>'s 0.4 on purpose — the ghost is a reference
    /// outline behind the result, not a see-through part, and at 0.4 it reads as a
    /// second body.
    /// </summary>
    public const float GhostAlpha = 0.18f;

    /// <summary>The colour every mesh vertex reads when no field-colour buffer is
    /// attached. White, because the shader multiplies nothing by it — the strength
    /// uniform is 0 there, so this only has to be a defined finite value; it is white
    /// rather than black so a mistake shows as "no field" instead of "black part".</summary>
    public static readonly (float R, float G, float B) NeutralColor = (1f, 1f, 1f);

    /// <summary>
    /// Builds the field data for one part, or returns false when it shows no field.
    /// <para><paramref name="error"/> is non-null only for a display that was ASKED FOR
    /// and could not be honoured (a result an edit removed, a field of the wrong
    /// length) — a part with no <c>FieldDisplay</c> returns false with a null error,
    /// the same "nothing to show" versus "it went wrong" distinction
    /// <c>Part.TryGetSdf</c> draws.</para>
    /// </summary>
    /// <param name="part">The part (its <c>FieldDisplay</c> and <c>Results</c>).</param>
    /// <param name="render">The part's render mesh — the caller already built it, and
    /// its <see cref="RenderMesh.SourceVertices"/> is what maps a per-source-vertex
    /// field onto the flat mesh's duplicates.</param>
    /// <param name="vertexCount">The SOURCE mesh's vertex count
    /// (<c>part.GetMesh().VertexCount</c>) — what a result must cover.</param>
    /// <param name="data">The colour buffer and, when asked for, the deformed mesh.</param>
    /// <param name="error">Why a requested display could not be honoured.</param>
    public static bool TryBuild(
        Part part, RenderMesh render, int vertexCount,
        out FieldMeshData data, out string? error)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(render);
        data = default;
        if (!part.TryResolveFieldDisplay(out var display, out error))
            return false;

        if (display.Field.Count != vertexCount)
        {
            error = $"Part '{part.Name}': result '{display.Field.Name}' covers "
                + $"{display.Field.Count} vertices but the display mesh has {vertexCount}. "
                + "A result is indexed by the part's display-mesh vertices, in vertex order.";
            return false;
        }
        if (display.Deform is { } deform && deform.Count != vertexCount)
        {
            error = $"Part '{part.Name}': displacement result '{deform.Name}' covers "
                + $"{deform.Count} vertices but the display mesh has {vertexCount}.";
            return false;
        }
        if (render.SourceVertices.Length != render.VertexCount)
        {
            error = $"Part '{part.Name}': the render mesh carries no source-vertex map, "
                + "so a per-vertex result cannot be placed on it.";
            return false;
        }

        var colors = Colors(display.Field, display.Range, display.ColorMap, render);
        var deformed = display.Deform is { } field && display.DeformScale != 0
            ? Deform(render, field, display.DeformScale)
            : null;
        data = new FieldMeshData(colors, deformed, display);
        error = null;
        return true;
    }

    /// <summary>
    /// The colour buffer for a render mesh: every render vertex takes the colour its
    /// SOURCE vertex maps to. Three floats per vertex, matching the <c>aFieldColor</c>
    /// attribute.
    /// </summary>
    public static float[] Colors(
        MeshField field, in FieldRange range, FieldColorMap map, RenderMesh render)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(render);
        var perSource = SourceColors(field, range, map);

        var colors = new float[render.VertexCount * 3];
        for (int v = 0; v < render.VertexCount; v++)
        {
            var (r, g, b) = perSource[render.SourceVertices[v]];
            colors[v * 3] = r;
            colors[v * 3 + 1] = g;
            colors[v * 3 + 2] = b;
        }
        return colors;
    }

    /// <summary>
    /// One colour per SOURCE mesh vertex — the map sampled once per value, before any
    /// render-mesh duplication.
    /// <para>The flat render mesh repeats each source vertex once per incident triangle,
    /// so sampling per RENDER vertex would evaluate the map several times for the same
    /// value and (worse) leave the copies free to disagree if the map ever became
    /// inexact. Consumers that place colours on something other than a render mesh — the
    /// glTF exporter, which does its own spreading — take this directly, so the two
    /// cannot compute different colours for one field.</para>
    /// </summary>
    public static (float R, float G, float B)[] SourceColors(
        MeshField field, in FieldRange range, FieldColorMap map)
    {
        ArgumentNullException.ThrowIfNull(field);
        var perSource = new (float R, float G, float B)[field.Count];
        for (int v = 0; v < perSource.Length; v++)
            perSource[v] = ColorMaps.Sample(map, range, field.ScalarAt(v));
        return perSource;
    }

    /// <summary>
    /// The render mesh with every vertex displaced by <paramref name="displacement"/>
    /// times <paramref name="scale"/>, and its facet normals recomputed from the moved
    /// positions.
    /// <para>Normals must be rebuilt, not carried over: a deformed shape lit by the
    /// original normals looks like the original, which defeats the entire point of a
    /// deformed-shape plot. The flat render mesh stores three consecutive vertices per
    /// triangle, so the facet normal is one cross product per triangle written to its
    /// own three vertices — no averaging, no smoothing-group question.</para>
    /// <para>The index buffer and <see cref="RenderMesh.SourceVertices"/> are carried
    /// over unchanged, so a colour buffer built for the source applies verbatim.</para>
    /// </summary>
    public static RenderMesh Deform(RenderMesh render, MeshField displacement, double scale)
    {
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(displacement);
        if (!displacement.IsVector)
            throw new ArgumentException(
                $"Field '{displacement.Name}' is a scalar field; a deformed shape needs a vector field.",
                nameof(displacement));

        var positions = new float[render.Positions.Length];
        for (int v = 0; v < render.VertexCount; v++)
        {
            var d = displacement.VectorAt(render.SourceVertices[v]) * scale;
            positions[v * 3] = render.Positions[v * 3] + (float)d.X;
            positions[v * 3 + 1] = render.Positions[v * 3 + 1] + (float)d.Y;
            positions[v * 3 + 2] = render.Positions[v * 3 + 2] + (float)d.Z;
        }

        var normals = new float[render.Normals.Length];
        for (int t = 0; t < render.TriangleCount; t++)
        {
            uint i0 = render.Indices[t * 3];
            uint i1 = render.Indices[t * 3 + 1];
            uint i2 = render.Indices[t * 3 + 2];
            var a = At(positions, i0);
            var n = (At(positions, i1) - a).Cross(At(positions, i2) - a);
            // Exact-zero guard, not a tolerance: a triangle a displacement collapsed
            // has no normal to compute, and keeping the source facet's is the honest
            // fallback (a degenerate facet contributes no visible area anyway).
            double length = n.Length;
            var unit = length == 0 ? Vector3d.Zero : n / length;
            foreach (uint i in (ReadOnlySpan<uint>)[i0, i1, i2])
            {
                if (length == 0)
                {
                    normals[i * 3] = render.Normals[i * 3];
                    normals[i * 3 + 1] = render.Normals[i * 3 + 1];
                    normals[i * 3 + 2] = render.Normals[i * 3 + 2];
                }
                else
                {
                    normals[i * 3] = (float)unit.X;
                    normals[i * 3 + 1] = (float)unit.Y;
                    normals[i * 3 + 2] = (float)unit.Z;
                }
            }
        }

        return new RenderMesh
        {
            Positions = positions,
            Normals = normals,
            Indices = render.Indices,
            SourceVertices = render.SourceVertices,
        };

        static Vector3d At(float[] positions, uint index) =>
            new(positions[index * 3], positions[index * 3 + 1], positions[index * 3 + 2]);
    }
}
