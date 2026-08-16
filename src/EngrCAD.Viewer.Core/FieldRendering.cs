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
//  * DEFORMATION rides the SAME rule, and used not to. A displaced shape looks like new
//    geometry, so the first version built it on the CPU and re-uploaded — which put it
//    off the matrices-only animation path, because animating it would have re-uploaded
//    every frame. It does not have to be an exception: the displacement travels ONCE as
//    attributes 4-7 and the vertex shader applies `position + uDeformScale * offset`, so
//    a whole result animation is ONE float uniform per frame. The normal is exact rather
//    than carried over (see DeformationAttributes), and the same constant-when-absent
//    rule holds: no buffer means zero, so an unaffected part renders byte-identically
//    whatever uDeformScale says.
//
//    The CPU displacement survives for exactly one job — PICK GEOMETRY, which is a
//    spatial index and cannot be a uniform. See PickShape.

/// <summary>
/// The per-part vertex data a field display contributes: the colour buffer every render
/// pass uploads as attribute 3, and — when the display asks for a deformed shape — the
/// displacement attribute buffer it uploads as attributes 4-7.
/// </summary>
/// <param name="Colors">RGB per render-mesh vertex (3 floats each), in the render
/// mesh's own vertex order.</param>
/// <param name="Deformation">The interleaved displacement attributes (see
/// <see cref="FieldRendering.DeformationAttributes"/>), or null when the display shows
/// the undeformed shape. Same vertex order as the source mesh, so
/// <paramref name="Colors"/> and the index buffer apply to it unchanged.</param>
/// <param name="Display">The resolved display this was built from (the field, its
/// range and map — what a legend and a properties panel read).</param>
public readonly record struct FieldMeshData(
    float[] Colors, float[]? Deformation, ResolvedFieldDisplay Display)
{
    /// <summary>Whether this part draws displaced (it carries a displacement buffer AND
    /// a non-zero scale). A part with <c>DeformScale = 0</c> gets no buffer at all.</summary>
    public bool Deformed => Deformation is not null;

    /// <summary>The part's own exaggeration factor — the <c>uDeformScale</c> uniform
    /// before any animation multiplies it, and 0 when nothing is displaced.</summary>
    public double DeformScale => Deformed ? Display.DeformScale : 0;

    /// <summary>Whether the undeformed shape should also be drawn, ghosted (only
    /// meaningful when <see cref="Deformed"/> is true).</summary>
    public bool ShowGhost => Deformed && Display.ShowUndeformed;
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
        out FieldMeshData data, out string? error, int faceCount = -1)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(render);
        data = default;
        if (!part.TryResolveFieldDisplay(out var display, out error))
            return false;

        if (display.Field.Association == FieldAssociation.Cell)
        {
            // A cell field is indexed by the display mesh's FACES, so the caller must
            // say how many there are; the flat render mesh's face map places it.
            if (faceCount >= 0 && display.Field.Count != faceCount)
            {
                error = $"Part '{part.Name}': cell result '{display.Field.Name}' covers "
                    + $"{display.Field.Count} cells but the display mesh has {faceCount} "
                    + "faces. A cell result is indexed by the display mesh's faces.";
                return false;
            }
            if (render.SourceFaces.Length != render.VertexCount)
            {
                error = $"Part '{part.Name}': the render mesh carries no source-face map, "
                    + "so a cell result cannot be placed on it (a smooth mesh shares "
                    + "vertices between faces, so the question has no answer there).";
                return false;
            }
        }
        else if (display.Field.Count != vertexCount)
        {
            error = $"Part '{part.Name}': result '{display.Field.Name}' covers "
                + $"{display.Field.Count} vertices but the display mesh has {vertexCount}. "
                + "A result is indexed by the part's display-mesh vertices, in vertex order.";
            return false;
        }
        if (display.Deform is { } deform)
        {
            if (deform.Association == FieldAssociation.Cell)
            {
                error = $"Part '{part.Name}': displacement result '{deform.Name}' is "
                    + "CELL-associated, and a displacement moves vertices — a per-face "
                    + "displacement has no vertex to move.";
                return false;
            }
            if (deform.Count != vertexCount)
            {
                error = $"Part '{part.Name}': displacement result '{deform.Name}' covers "
                    + $"{deform.Count} vertices but the display mesh has {vertexCount}.";
                return false;
            }
        }
        if (render.SourceVertices.Length != render.VertexCount)
        {
            error = $"Part '{part.Name}': the render mesh carries no source-vertex map, "
                + "so a per-vertex result cannot be placed on it.";
            return false;
        }

        var colors = Colors(
            display.Field, display.Range, display.ColorMap, render, display.LogScale);
        // The buffer carries the displacement per UNIT scale: the exaggeration is the
        // uDeformScale uniform, which is what makes animating it free. A zero scale gets
        // no buffer at all, so "shows no deformed shape" stays one question.
        var deformation = display.Deform is { } field && display.DeformScale != 0
            ? DeformationAttributes(render, field)
            : null;
        data = new FieldMeshData(colors, deformation, display);
        error = null;
        return true;
    }

    /// <summary>Floats per vertex in the interleaved deformation buffer: the
    /// displacement offset and the three coefficients of the displaced facet normal,
    /// four vec3 attributes at slots 4-7.</summary>
    public const int DeformationStride = 12;

    /// <summary>
    /// The <c>uDeformScale</c> uniform for one part: its own exaggeration times the
    /// animation's current factor (1 when nothing is animating it).
    /// <para>One function because the byte-for-byte equality between an animated frame
    /// and a static render of the same configuration rests on it — the product is formed
    /// in double and narrowed once, so a part displayed at <c>s*f</c> and a part at
    /// <c>s</c> animated to factor <c>f</c> send the identical float.</para>
    /// </summary>
    public static float DeformUniform(in FieldMeshData data, double factor) =>
        (float)(data.DeformScale * factor);

    /// <summary>
    /// The resolved display a LEGEND should be built from at the given animation factor:
    /// the same display with its exaggeration multiplied. The legend's title states that
    /// number, so a bar reading "40X DEFORMED" over a frame drawn at 20X would be exactly
    /// the lie the title exists to prevent. Factor 1 returns the display unchanged.
    /// </summary>
    public static ResolvedFieldDisplay? AtFactor(ResolvedFieldDisplay? display, double factor) =>
        display is { } resolved
            ? resolved with { DeformScale = resolved.DeformScale * factor }
            : null;

    /// <summary>The displacement every mesh vertex reads when no deformation buffer is
    /// attached: exactly zero, so <c>aPos + s*0</c> is <c>aPos</c> for every finite
    /// scale and the normal expression takes its <c>aNormal</c> fallback. The
    /// constant-when-absent rule <c>aOcclusion</c> and <c>aFieldColor</c> already
    /// follow, and the reason a part with no results renders byte-identically however
    /// the deform uniform is set.</summary>
    public static readonly (float X, float Y, float Z) NeutralDeformation = (0f, 0f, 0f);

    /// <summary>
    /// The shape a pick BVH must index for this part: the CPU-displaced mesh when a
    /// deformed shape is shown, the source mesh otherwise.
    /// <para><b>This is the one place the CPU displacement survives, and it is not a
    /// second render path.</b> Picking is a spatial index, not a draw call, so it cannot
    /// ride a uniform — a click must be answered against triangles that exist somewhere.
    /// The index is therefore built at the part's OWN <c>DeformScale</c>, which is the
    /// animation's factor-1 configuration: a pick is exact on a static plot and at a load
    /// ramp's peak, and off by the difference in exaggeration at intermediate frames.
    /// Rebuilding a BVH per frame is precisely the cost this design exists to avoid, so
    /// the mismatch is stated rather than paid for.</para>
    /// </summary>
    public static RenderMesh PickShape(RenderMesh render, in FieldMeshData data)
    {
        ArgumentNullException.ThrowIfNull(render);
        return data.Deformed && data.Display.Deform is { } field
            ? Deform(render, field, data.Display.DeformScale)
            : render;
    }

    /// <summary>
    /// The colour buffer for a render mesh: every render vertex takes the colour its
    /// SOURCE vertex maps to. Three floats per vertex, matching the <c>aFieldColor</c>
    /// attribute.
    /// </summary>
    public static float[] Colors(
        MeshField field, in FieldRange range, FieldColorMap map, RenderMesh render,
        bool logScale = false)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(render);
        // A cell field looks up by the render vertex's source FACE — every duplicate of
        // a face's corners takes that face's value, so the cell renders flat with no
        // shader change; a vertex field looks up by source vertex, as ever.
        var lookup = field.Association == FieldAssociation.Cell
            ? render.SourceFaces
            : render.SourceVertices;
        return Colors(field, range, map, lookup, logScale);
    }

    /// <summary>
    /// The retained-lookup form of <see cref="Colors(MeshField, in FieldRange,
    /// FieldColorMap, RenderMesh, bool)"/> — for a consumer that kept only the
    /// source-index map after upload (the transient-playback path re-uploads colours
    /// per step without holding the whole render mesh). The caller supplies the lookup
    /// its field's ASSOCIATION wants: source vertices for a vertex field, source faces
    /// for a cell field.
    /// </summary>
    public static float[] Colors(
        MeshField field, in FieldRange range, FieldColorMap map, int[] lookup,
        bool logScale = false)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(lookup);
        var perSource = SourceColors(field, range, map, logScale);
        var colors = new float[lookup.Length * 3];
        for (int v = 0; v < lookup.Length; v++)
        {
            var (r, g, b) = perSource[lookup[v]];
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
        MeshField field, in FieldRange range, FieldColorMap map, bool logScale = false)
    {
        ArgumentNullException.ThrowIfNull(field);
        var perSource = new (float R, float G, float B)[field.Count];
        if (logScale)
        {
            // Log colour position: (log v − log min)/(log max − log min). The display
            // resolution has already required a strictly positive range; a non-positive
            // VALUE has no log position and maps to NaN — the colour map's bottom stop,
            // its own "no value here" convention.
            double logMin = Math.Log10(range.Min);
            double logSpan = Math.Log10(range.Max) - logMin;
            for (int v = 0; v < perSource.Length; v++)
            {
                double value = field.ScalarAt(v);
                double t = value > 0 && logSpan > 0
                    ? (Math.Log10(value) - logMin) / logSpan
                    : double.NaN;
                perSource[v] = ColorMaps.Sample(map, t);
            }
            return perSource;
        }
        for (int v = 0; v < perSource.Length; v++)
            perSource[v] = ColorMaps.Sample(map, range, field.ScalarAt(v));
        return perSource;
    }

    /// <summary>
    /// The interleaved deformation attributes for a render mesh: 12 floats per vertex —
    /// the displacement offset (attribute 4) followed by the three coefficients of the
    /// displaced facet normal (attributes 5, 6, 7), which the vertex shader evaluates as
    /// <c>n0 + s*n1 + s*s*n2</c>.
    /// <para><b>Why three coefficients and not a re-upload.</b> A triangle whose vertices
    /// move linearly in the scale s has edges <c>a + s*alpha</c> and <c>b + s*beta</c>,
    /// so its facet normal is
    /// <c>(a x b) + s*(a x beta + alpha x b) + s^2*(alpha x beta)</c> — a polynomial of
    /// degree exactly two. The displaced shading is therefore EXACT at every scale from
    /// data sent once, which is what let the CPU deformation path retire instead of
    /// living on beside the shader one. It reproduces what that path computed: the
    /// per-TRIANGLE normal of the displaced facet, not the source face's normal (a
    /// non-planar quad's two halves genuinely have different normals once bent).</para>
    /// <para>Only the DIRECTION matters — the fragment shader normalizes — so the three
    /// coefficients are divided by the largest of their magnitudes purely to keep float32
    /// well conditioned at any model scale. When all three vanish (a triangle that is
    /// degenerate at every scale) they are left at zero, which is the shader's signal to
    /// fall back to the source normal.</para>
    /// </summary>
    /// <param name="render">The undeformed render mesh (flat: three consecutive vertices
    /// per triangle, so a facet normal is one cross product written to three vertices).</param>
    /// <param name="displacement">Vector result, indexed by source vertex.</param>
    public static float[] DeformationAttributes(RenderMesh render, MeshField displacement)
    {
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(displacement);
        if (!displacement.IsVector)
            throw new ArgumentException(
                $"Field '{displacement.Name}' is a scalar field; a deformed shape needs a vector field.",
                nameof(displacement));

        var attributes = new float[render.VertexCount * DeformationStride];
        var offsets = new Vector3d[render.VertexCount];
        for (int v = 0; v < render.VertexCount; v++)
        {
            var d = displacement.VectorAt(render.SourceVertices[v]);
            offsets[v] = d;
            attributes[v * DeformationStride] = (float)d.X;
            attributes[v * DeformationStride + 1] = (float)d.Y;
            attributes[v * DeformationStride + 2] = (float)d.Z;
        }

        for (int t = 0; t < render.TriangleCount; t++)
        {
            uint i0 = render.Indices[t * 3];
            uint i1 = render.Indices[t * 3 + 1];
            uint i2 = render.Indices[t * 3 + 2];
            var p0 = At(render.Positions, i0);
            var a = At(render.Positions, i1) - p0;
            var b = At(render.Positions, i2) - p0;
            var alpha = offsets[i1] - offsets[i0];
            var beta = offsets[i2] - offsets[i0];

            var n0 = a.Cross(b);
            var n1 = a.Cross(beta) + alpha.Cross(b);
            var n2 = alpha.Cross(beta);
            // Exact-zero guard, not a tolerance: a scale factor of zero would be a
            // division by zero, and every other magnitude is a legitimate conditioning
            // choice rather than a decision about geometry.
            double largest = Math.Max(n0.Length, Math.Max(n1.Length, n2.Length));
            if (largest != 0)
            {
                n0 /= largest;
                n1 /= largest;
                n2 /= largest;
            }

            foreach (uint i in (ReadOnlySpan<uint>)[i0, i1, i2])
            {
                Write(attributes, i, 3, n0);
                Write(attributes, i, 6, n1);
                Write(attributes, i, 9, n2);
            }
        }
        return attributes;

        static void Write(float[] attributes, uint vertex, int slot, in Vector3d value)
        {
            int at = (int)vertex * DeformationStride + slot;
            attributes[at] = (float)value.X;
            attributes[at + 1] = (float)value.Y;
            attributes[at + 2] = (float)value.Z;
        }
    }

    /// <summary>
    /// The render mesh with every vertex displaced by <paramref name="displacement"/>
    /// times <paramref name="scale"/>, and its facet normals recomputed from the moved
    /// positions.
    /// <para><b>Not the render path any more</b> — the vertex shader displaces from
    /// <see cref="DeformationAttributes"/>, which is what makes a result animation one
    /// uniform per frame. This builds the geometry a PICK BVH indexes (see
    /// <see cref="PickShape"/>), and remains the readable statement of what the shader's
    /// quadratic normal coefficients reproduce.</para>
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
    }

    private static Vector3d At(float[] positions, uint index) =>
        new(positions[index * 3], positions[index * 3 + 1], positions[index * 3 + 2]);
}
