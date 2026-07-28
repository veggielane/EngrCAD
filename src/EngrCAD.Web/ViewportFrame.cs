using EngrCAD.Core;
using EngrCAD.Modeling;
using EngrCAD.Viewer;

namespace EngrCAD.Web;

/// <summary>
/// One instance ready to draw: already-uploaded geometry keys plus the pose, colour and
/// display mode it draws with. Instances of the same <see cref="Part"/> share the keys, so
/// N occurrences cost one upload and N matrices — the same rule the desktop viewer follows.
///
/// <para>All three geometry keys are filled in for every part, even though a given frame
/// uses at most two of them: the global <see cref="ViewStyle"/> is a dropdown, and a
/// viewport that had to re-upload to switch style would stall on every change. The
/// desktop's <c>ViewportControl.UploadShared</c> makes exactly the same trade (the
/// one-shot offscreen pass uploads only what its mode needs, because it has no dropdown).</para>
/// </summary>
/// <param name="GeometryKey">Key the part's mesh was uploaded under (fills and points).</param>
/// <param name="World">Instance transform (frame chain x part transform).</param>
/// <param name="Color">Fill colour.</param>
/// <param name="WorldCenter">Instance bounds centre, for depth-ordered passes.</param>
/// <param name="Mode">The part's own display mode; the global style decides the rest
/// (<see cref="RenderModes.Resolve"/>).</param>
/// <param name="EdgeKey">Key of the part's feature-edge line upload, or null when it has none.</param>
/// <param name="EdgeVertexCount">Vertices in that upload (segments x 2).</param>
/// <param name="WireKey">Key of the part's every-mesh-edge line upload, or null.</param>
/// <param name="WireVertexCount">Vertices in that upload (segments x 2).</param>
/// <param name="Visible">Whether the model tree's checkboxes leave this instance shown.
/// A hidden instance contributes no draw calls and <b>keeps its index</b> — which is
/// what lets the tree, the selection and the pick list all address instances by the
/// same number however many rows are unchecked.</param>
/// <param name="ClippedBySection">Mirrors <c>Part.ClippedBySection</c>: false renders
/// this instance whole inside a cutaway (its draws carry a per-draw
/// <c>uSectionEnabled = 0</c> override) — the drafting convention that shafts, bolts
/// and pins are drawn unsectioned. Picking already carries the same flag on
/// <c>PickInstance</c>, so the two cannot disagree.</param>
public readonly record struct ViewportInstance(
    string GeometryKey, Matrix4d World, PartColor Color, Vector3d WorldCenter,
    DisplayMode Mode = DisplayMode.Shaded,
    string? EdgeKey = null, int EdgeVertexCount = 0,
    string? WireKey = null, int WireVertexCount = 0,
    bool Visible = true,
    bool ClippedBySection = true);

/// <summary>Keys the scene furniture is uploaded under, and how many vertices the grid
/// has (the axes are always three consecutive pairs).</summary>
/// <param name="GridKey">Key of the ground-grid line upload.</param>
/// <param name="GridVertexCount">Vertices in the grid upload.</param>
/// <param name="AxesKey">Key of the world-axes line upload (6 vertices).</param>
public readonly record struct ViewportFurniture(string GridKey, int GridVertexCount, string AxesKey);

/// <summary>
/// Keys the section-plane SDF isolines are uploaded under — the three level families
/// <c>SectionContours.Build</c> produces, uploaded by the component and drawn by
/// <see cref="ViewportFrame.Build"/> whenever sections are active. The geometry
/// (including the lift below the plane that keeps the shader discard from eating the
/// lines) and the family colours all come from the shared <c>SectionContours</c>;
/// nothing about what an isoline looks like is decided in this project.
/// </summary>
/// <param name="ZeroKey">Upload key of the d = 0 family (the exact cross-section).</param>
/// <param name="ZeroVertexCount">Vertices in that upload.</param>
/// <param name="PositiveKey">Upload key of the positive (outside material) families.</param>
/// <param name="PositiveVertexCount">Vertices in that upload.</param>
/// <param name="NegativeKey">Upload key of the negative (inside material) families.</param>
/// <param name="NegativeVertexCount">Vertices in that upload.</param>
public readonly record struct ViewportContours(
    string ZeroKey, int ZeroVertexCount,
    string PositiveKey, int PositiveVertexCount,
    string NegativeKey, int NegativeVertexCount);

/// <summary>
/// The annotation overlay's upload: billboarded line segments built by the shared
/// <c>AnnotationGeometry.Build</c> (the same dimension anatomy, stroke-font text and
/// screen-constant sizing the desktop layer draws), re-uploaded by the component only
/// when the <c>AnnotationCamera</c> value or the item set changes.
/// </summary>
/// <param name="Key">Upload key of the annotation line vertices.</param>
/// <param name="VertexCount">Vertices in that upload (segments x 2).</param>
public readonly record struct ViewportAnnotations(string Key, int VertexCount);

/// <summary>
/// The view cube's frame inputs: the canvas size (CSS pixels — the region maths is in
/// DIPs, scaled to framebuffer pixels by the frame's <c>pixelScale</c>) and the hovered
/// direction, if any. The geometry itself is the shared <c>ViewCubeGeometry</c> arrays,
/// uploaded once under <see cref="ViewportFrame.CubeFillsKey"/> and friends.
/// </summary>
/// <param name="CanvasWidth">Canvas width in CSS pixels.</param>
/// <param name="CanvasHeight">Canvas height in CSS pixels.</param>
/// <param name="Hover">Hovered cube direction (components in {-1,0,1}), or null. Every
/// face contributing a component brightens — the shared hover rule.</param>
public readonly record struct ViewportCube(
    double CanvasWidth, double CanvasHeight, Vector3d? Hover = null);

/// <summary>
/// Turns a posed instance list into the <see cref="FrameDescription"/> the WebGL2 client
/// draws — the browser's counterpart of <c>ViewportControl.OnOpenGlRender</c> and
/// <c>OffscreenRenderer.Draw</c>, and the reason those two do not gain a third divergent
/// sibling: every value here comes from the shared render core in EngrCAD.Viewer.Core
/// (<see cref="ViewerShaders"/> for the programs, <see cref="CameraMath"/> for the
/// matrices, <see cref="RenderModes"/> for mode precedence and translucency ordering,
/// <see cref="RenderGeometry"/> for the furniture), and the JavaScript that executes the
/// result contains no decisions at all.
///
/// <para>It is deliberately a pure function of its arguments — no GL handle, no JS
/// runtime, no component state — so what the browser will draw is a unit-testable value.
/// The desktop passes cannot be tested that way (their output is pixels), which is
/// exactly why they drifted.</para>
///
/// <para><b>The pass order is the desktop's, transcribed.</b> Background, furniture,
/// opaque fills, line overlay, points, then translucency last: blended fills
/// back-to-front with depth writes off, then their feature edges opaque on top for a
/// readable silhouette. Getting that order from a value rather than from a sequence of
/// GL state changes is the point — blend state, depth-mask state and the sort are all
/// *in the returned frame*, so a test asserts them instead of a screenshot.</para>
///
/// <para><b>Visibility, selection and hover are inputs, not side effects.</b> A hidden
/// instance keeps its index and contributes no draws; a selected or hovered one changes
/// the uniforms of the draws it does contribute. Both are therefore observable in the
/// returned value, so "hiding a part removes its edges too" and "selecting one tints its
/// fill and golds its edges" are assertions over a frame rather than over a screenshot.</para>
///
/// <para><b>Scope, stated rather than implied.</b> Section planes and baked occlusion are
/// later rungs; their shared rules already exist (<see cref="SectionClip"/>) and are
/// deliberately not half-applied here, because a mode resolved and then ignored looks
/// like support and is not.</para>
/// </summary>
public static class ViewportFrame
{
    /// <summary>Program names, as passed to <see cref="WebGlContext.CreateProgramAsync"/>.</summary>
    public const string MeshProgram = "mesh";

    /// <inheritdoc cref="MeshProgram"/>
    public const string LineProgram = "line";

    /// <inheritdoc cref="MeshProgram"/>
    public const string PointProgram = "point";

    /// <inheritdoc cref="MeshProgram"/>
    public const string BackgroundProgram = "background";

    /// <summary>Upload key of the view cube's face fills (<c>ViewCubeGeometry.BuildFillVertices</c>,
    /// drawn per face through the line program in mode "triangles").</summary>
    public const string CubeFillsKey = "@cube.fills";

    /// <summary>Upload key of the view cube's 12 edges.</summary>
    public const string CubeEdgesKey = "@cube.edges";

    /// <summary>Upload key of the view cube's stroke-font labels.</summary>
    public const string CubeLabelsKey = "@cube.labels";

    // Vertex counts of the cube's shared uploads, derived from the geometry builders
    // themselves so a glyph change cannot silently truncate a draw.
    private static readonly int CubeEdgeVertexCount = ViewCubeGeometry.BuildEdgeVertices().Length / 3;
    private static readonly int CubeLabelVertexCount = ViewCubeGeometry.BuildLabelVertices().Length / 3;

    /// <summary>Clear colour behind the gradient — the viewport's, to the byte
    /// (<c>ViewportControl.OnOpenGlRender</c> and <c>OffscreenRenderer.Draw</c> both
    /// clear to this before drawing the gradient over it).</summary>
    public static readonly float[] ClearColor = [0.11f, 0.12f, 0.14f];

    /// <summary>Feature-edge / translucent-silhouette colour, matching both desktop passes.</summary>
    public static readonly float[] EdgeColor = [0.09f, 0.10f, 0.11f];

    /// <summary>Fill alpha for <c>DisplayMode.Translucent</c> parts, matching both
    /// desktop passes.</summary>
    public const float TranslucentAlpha = 0.4f;

    /// <summary>Point-sprite diameter in device-independent pixels, before the display's
    /// pixel scale. The desktop window multiplies the same 4 by its DPI scaling and the
    /// offscreen pass by its supersample factor, for the same reason: the sprite is sized
    /// in the framebuffer, so it must follow the framebuffer's resolution to keep a
    /// constant apparent size.</summary>
    public const float PointSize = 4f;

    /// <summary>Directional light, shared with both desktop passes.</summary>
    private static readonly Vector3d LightDirection = new Vector3d(-0.5, -0.7, -0.9).Normalized();

    private static readonly float[] GridColor = [0.24f, 0.26f, 0.29f];
    private static readonly float[] AxisXColor = [0.75f, 0.30f, 0.30f];
    private static readonly float[] AxisYColor = [0.33f, 0.66f, 0.33f];
    private static readonly float[] AxisZColor = [0.35f, 0.48f, 0.85f];

    /// <summary>
    /// Builds the frame for one draw: background gradient, ground grid and world axes,
    /// then each instance in whichever mode <see cref="RenderModes.Resolve"/> puts it in.
    /// </summary>
    /// <param name="instances">Posed instances referencing already-uploaded geometry.</param>
    /// <param name="camera">Orbit pose (see <see cref="CameraMath"/>).</param>
    /// <param name="sceneBounds">World bounds of everything drawn; sizes the frustum.</param>
    /// <param name="aspect">Viewport width / height.</param>
    /// <param name="furniture">Grid and axes keys, or null to draw neither.</param>
    /// <param name="style">Global view style; a part whose <see cref="ViewportInstance.Mode"/>
    /// is explicitly non-default overrides it (the precedence rule lives in
    /// <see cref="RenderModes.Resolve"/> and is not restated here).</param>
    /// <param name="pixelScale">Device pixels per CSS pixel, so point sprites keep a
    /// constant apparent size on a high-DPI display.</param>
    /// <param name="selected">Index of the selected instance, or -1. Drawn with the
    /// selection highlight (<see cref="Highlight"/>, shared with the desktop viewer).</param>
    /// <param name="hovered">Index of the hovered instance, or -1: the fainter
    /// pre-selection tint. A hovered selected instance shows selection only.</param>
    /// <param name="sectionPlanes">Active section planes (null or empty = sections off).
    /// The clip itself runs in the shared shaders (<see cref="ViewerShaders.SectionClip"/>);
    /// this only carries the uniforms, and scene furniture plus
    /// <see cref="ViewportInstance.ClippedBySection"/>-exempt instances carry a per-draw
    /// <c>uSectionEnabled = 0</c> override exactly as the desktop passes flip the same
    /// uniform per draw group.</param>
    /// <param name="sectionCombine">How several planes combine — the shared
    /// <see cref="SectionCombine"/> rule, carried as <c>uSectionUnion</c>.</param>
    /// <param name="contours">SDF isolines on the cut, or null. Drawn only while
    /// sections are active, after everything else so they read as an overlay: signed
    /// families first, then d = 0 last so the gold cross-section wins any overdraw —
    /// the desktop renderer's order, with the shared <c>SectionContours</c> palette.</param>
    /// <param name="cube">The view cube, or null. Drawn last of all (the desktop rule:
    /// the cube is window chrome sitting on top of everything), into its own top-right
    /// sub-viewport with the depth buffer cleared first.</param>
    /// <param name="annotations">The annotation overlay, or null. Drawn depth-off
    /// (always-on-top v1) in the shared <c>AnnotationGeometry.Color</c>, never
    /// section-clipped — annotations are documentation, not model geometry — after the
    /// isolines and before the cube, the desktop pass order.</param>
    public static FrameDescription Build(
        IReadOnlyList<ViewportInstance> instances,
        CameraState camera,
        in Aabb sceneBounds,
        double aspect,
        ViewportFurniture? furniture = null,
        ViewStyle style = ViewStyle.ShadedWithEdges,
        double pixelScale = 1.0,
        int selected = -1,
        int hovered = -1,
        IReadOnlyList<SectionPlane>? sectionPlanes = null,
        SectionCombine sectionCombine = SectionCombine.Intersection,
        ViewportContours? contours = null,
        ViewportCube? cube = null,
        ViewportAnnotations? annotations = null)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(aspect, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pixelScale, 0);

        // The shader's uniform array is fixed-size; the desktop's SectionUniforms.Write
        // clamps to the same limit rather than failing, and so does this.
        int sectionCount = Math.Min(sectionPlanes?.Count ?? 0, ViewerShaders.MaxSectionPlanes);
        bool sectionActive = sectionCount > 0;

        var eye = CameraMath.Eye(camera.Yaw, camera.Pitch, camera.Distance, camera.Target);
        var view = CameraMath.LookAt(eye, camera.Target, Vector3d.UnitZ);
        var (near, far) = CameraMath.FrustumPlanes(camera.Distance, sceneBounds);
        var projection = CameraMath.Perspective(Math.PI / 4, aspect, near, far);

        // Frame-constant uniforms travel once instead of once per draw call. Names absent
        // from a program are skipped, so one set serves the mesh, line and point programs
        // and is simply ignored by the background gradient.
        //
        // uSectionCount is an `int` uniform, so it travels as an IntUniform marker: the
        // interop marshals a plain JSON number through uniform1f, which GL rejects on an
        // int — the typed-marker path in engrcad-gl.js exists for exactly this uniform.
        var shared = new Dictionary<string, object>
        {
            ["uView"] = ColumnMajor(view),
            ["uProj"] = ColumnMajor(projection),
            ["uLightDir"] = Vec3(LightDirection),
            ["uEyePos"] = Vec3(eye),
            // Neutral by default; a selected or hovered instance's own fill draw
            // overrides it, so an ordinary fill needs to say nothing (the same
            // discipline uAlpha follows for the translucent pass).
            ["uHighlight"] = 0f,
            ["uAlpha"] = 1f,              // the translucent pass overrides this per draw
            ["uAmbientOcclusion"] = 0f,   // no bake in the browser; 0 leaves the factor exactly 1
            ["uSectionEnabled"] = sectionActive ? 1f : 0f,
            ["uSectionCount"] = new IntUniform(sectionCount),
            ["uPointSize"] = PointSize * (float)pixelScale,
        };
        if (sectionActive)
        {
            // Packed exactly as the desktop's SectionUniforms.Write packs them:
            // xyz = normal, w = offset, count * 4 floats. The Vec4ArrayUniform marker
            // exists because four packed planes are 16 floats, which the interop's
            // shape dispatch would otherwise send as a mat4.
            var packed = new float[sectionCount * 4];
            for (int i = 0; i < sectionCount; i++)
            {
                var plane = sectionPlanes![i];
                packed[i * 4] = (float)plane.Normal.X;
                packed[i * 4 + 1] = (float)plane.Normal.Y;
                packed[i * 4 + 2] = (float)plane.Normal.Z;
                packed[i * 4 + 3] = (float)plane.Offset;
            }
            shared["uSectionPlanes"] = new Vec4ArrayUniform(packed);
            shared["uSectionUnion"] = sectionCombine == SectionCombine.Union ? 1f : 0f;
        }

        var draws = new List<DrawCall>(instances.Count + 4);

        // Background gradient: a fullscreen triangle the vertex shader builds from
        // gl_VertexID, so it needs no buffer and must not be depth-tested.
        draws.Add(new DrawCall
        {
            Program = BackgroundProgram,
            Count = 3,
            DepthTest = false,
            DepthWrite = false,
            Cull = false,
        });

        if (furniture is { } f)
        {
            // Scene furniture is never section-clipped (the desktop passes flip the
            // same uniform off for their furniture draw group), so when sections are
            // active these draws override the shared uSectionEnabled per draw.
            var identity = ColumnMajor(Matrix4d.Identity);
            draws.Add(Line(f.GridKey, identity, GridColor, first: 0, count: f.GridVertexCount, unclip: sectionActive));
            draws.Add(Line(f.AxesKey, identity, AxisXColor, first: 0, count: 2, unclip: sectionActive));
            draws.Add(Line(f.AxesKey, identity, AxisYColor, first: 2, count: 2, unclip: sectionActive));
            draws.Add(Line(f.AxesKey, identity, AxisZColor, first: 4, count: 2, unclip: sectionActive));
        }

        // Resolve once per instance; every pass below reads the answer rather than
        // re-deciding, so a part cannot be shaded in one pass and wireframe in the next.
        var modes = new EffectiveMode[instances.Count];
        for (int i = 0; i < instances.Count; i++)
            modes[i] = RenderModes.Resolve(style, instances[i].Mode);

        // Pass 1: opaque fills, pushed back slightly so the edge overlay wins the depth
        // test. Translucent fills are NOT here — they come last, after everything opaque
        // has established the depth buffer they blend against.
        for (int i = 0; i < instances.Count; i++)
        {
            if (modes[i] is not (EffectiveMode.Shaded or EffectiveMode.ShadedWithEdges))
                continue;
            var instance = instances[i];
            if (!instance.Visible)
                continue;
            var uniforms = new Dictionary<string, object>
            {
                ["uModel"] = ColumnMajor(instance.World),
                ["uColor"] = Rgb(instance.Color),
            };
            Highlighted(uniforms, i, selected, hovered);
            Unclipped(uniforms, sectionActive, instance);
            draws.Add(new DrawCall
            {
                Program = MeshProgram,
                Geometry = instance.GeometryKey,
                Uniforms = uniforms,
                // Face culling stays OFF, matching both desktop passes: clipping a solid
                // with a section plane exposes its interior as backfaces, which the shared
                // fragment shader shades as cut material via gl_FrontFacing. Enabling
                // culling here would work today and silently break that rung.
                Cull = false,
                PolygonOffset = [1f, 1f],
            });
        }

        // Pass 2: the line overlay — feature edges over shaded-with-edges fills, every
        // mesh edge for wireframe parts. Both go through the one line program, and both
        // are drawn AFTER all the fills so a part cannot hide a neighbour's edges.
        for (int i = 0; i < instances.Count; i++)
        {
            var instance = instances[i];
            if (!instance.Visible)
                continue;
            switch (modes[i])
            {
                // The selected part's feature edges draw in selection gold rather than
                // the near-black overlay colour, exactly as ViewportControl.DrawFeatureEdges
                // does — hover deliberately does NOT tint edges there, so it does not here.
                case EffectiveMode.ShadedWithEdges when instance.EdgeKey is not null && instance.EdgeVertexCount > 0:
                    draws.Add(Line(instance.EdgeKey, ColumnMajor(instance.World),
                        i == selected ? Rgb(Highlight.Selection) : EdgeColor,
                        first: 0, count: instance.EdgeVertexCount,
                        unclip: sectionActive && !instance.ClippedBySection));
                    break;
                // Wireframe draws in the part's OWN colour, not the edge colour: with no
                // fill behind them the dark edge colour would be nearly invisible against
                // the background, and colour is the only thing left to tell parts apart.
                // With no fill there is also no uHighlight to act on, which is why
                // selection and hover reach a wireframe part through its LINE colour.
                case EffectiveMode.Wireframe when instance.WireKey is not null && instance.WireVertexCount > 0:
                    draws.Add(Line(instance.WireKey, ColumnMajor(instance.World),
                        Rgb(Highlight.LineColor(i, selected, hovered, Tuple(instance.Color))),
                        first: 0, count: instance.WireVertexCount,
                        unclip: sectionActive && !instance.ClippedBySection));
                    break;
            }
        }

        // Pass 3: point sprites over the mesh buffers. A flat RenderMesh references each
        // vertex exactly once, so drawing the vertex array (what the interop does) and
        // drawing through the index buffer (what the desktop does) visit the same points.
        for (int i = 0; i < instances.Count; i++)
        {
            if (modes[i] != EffectiveMode.Points)
                continue;
            var instance = instances[i];
            if (!instance.Visible)
                continue;
            var pointUniforms = new Dictionary<string, object>
            {
                ["uModel"] = ColumnMajor(instance.World),
                // Point sprites are lines' cousins here: no fill, so the highlight
                // must reach them through the colour.
                ["uColor"] = Rgb(Highlight.LineColor(i, selected, hovered, Tuple(instance.Color))),
            };
            Unclipped(pointUniforms, sectionActive, instance);
            draws.Add(new DrawCall
            {
                Program = PointProgram,
                Geometry = instance.GeometryKey,
                Mode = "points",
                Cull = false,
                Uniforms = pointUniforms,
            });
        }

        // Pass 4: translucency, last and in its own order. Alpha blending is not
        // commutative, so the blended fills go back-to-front by distance from the eye
        // (RenderModes.SortBackToFront — the shared rule, so the browser cannot order
        // them differently from the desktop), with depth writes off so they do not
        // occlude each other. Their feature edges then draw opaque on top, which is what
        // keeps a see-through part readable rather than a coloured haze.
        //
        // Note what is absent: no polygon offset. The fills wrote no depth, so there is
        // nothing for the edges to z-fight with, and the desktop disables it here too.
        AppendTranslucent(draws, instances, modes, eye, selected, hovered, sectionActive);

        // Pass 5: SDF isolines on the cut — after everything else so they read as an
        // overlay (the desktop draws them last for the same reason). Depth-tested like
        // feature edges; the geometry already sits 1% of a spacing below the plane so
        // the fragment discard cannot eat it, but a SINGLE plane has no siblings to
        // clip against (SectionClip.Siblings returns empty), so the draws carry the
        // unclip override.
        if (sectionActive && contours is { } iso)
        {
            var identity = ColumnMajor(Matrix4d.Identity);
            if (iso.PositiveVertexCount > 0)
                draws.Add(Line(iso.PositiveKey, identity, Rgb(SectionContours.PositiveColor),
                    first: 0, count: iso.PositiveVertexCount, unclip: true));
            if (iso.NegativeVertexCount > 0)
                draws.Add(Line(iso.NegativeKey, identity, Rgb(SectionContours.NegativeColor),
                    first: 0, count: iso.NegativeVertexCount, unclip: true));
            if (iso.ZeroVertexCount > 0)
                draws.Add(Line(iso.ZeroKey, identity, Rgb(SectionContours.ZeroColor),
                    first: 0, count: iso.ZeroVertexCount, unclip: true));
        }

        // Pass 6: the annotation overlay — depth test OFF (always-on-top v1, so
        // dimensions read over the model from any angle) and never section-clipped
        // (documentation, not model geometry). The desktop draws it at the same point:
        // after the scene and its overlays, before the view cube.
        if (annotations is { VertexCount: > 0 } pmi)
        {
            var uniforms = new Dictionary<string, object>
            {
                ["uModel"] = ColumnMajor(Matrix4d.Identity),
                ["uColor"] = Rgb(AnnotationGeometry.Color),
            };
            if (sectionActive)
                uniforms["uSectionEnabled"] = 0f;
            draws.Add(new DrawCall
            {
                Program = LineProgram,
                Geometry = pmi.Key,
                First = 0,
                Count = pmi.VertexCount,
                DepthTest = false,
                Cull = false,
                Uniforms = uniforms,
            });
        }

        // Pass 7: the view cube, last of all — it is window chrome and sits on top of
        // everything, exactly as the desktop draws it at the end of its render pass.
        if (cube is { } widget)
            AppendViewCube(draws, widget, camera, pixelScale, sectionActive);

        return new FrameDescription { Clear = ClearColor, Shared = shared, Draws = draws };
    }

    /// <summary>
    /// The view cube's draws: face fills (per face, for their colours, with the shared
    /// hover-brighten rule), the 12 edges, and the stroke-font labels — all through the
    /// line program into a top-right sub-viewport, with the depth buffer cleared before
    /// the first draw so the cube overlays the finished scene. Region maths, the ortho
    /// mini-projection and the palette are all the shared <see cref="ViewCubeMath"/> /
    /// <see cref="ViewCubeGeometry"/> — the region rect here is the desktop's
    /// <c>ViewCube.Draw</c> transcribed, including its too-small-to-draw guard.
    /// </summary>
    private static void AppendViewCube(
        List<DrawCall> draws, in ViewportCube cube, CameraState camera,
        double pixelScale, bool sectionActive)
    {
        int sizePx = (int)Math.Round(ViewCubeMath.RegionSizeDip * pixelScale);
        int marginPx = (int)Math.Round(ViewCubeMath.RegionMarginDip * pixelScale);
        int x = (int)Math.Round(cube.CanvasWidth * pixelScale) - sizePx - marginPx;
        int y = (int)Math.Round(cube.CanvasHeight * pixelScale) - sizePx - marginPx;
        if (sizePx < 16 || x < 0 || y < 0)
            return; // viewport too small for the widget (the desktop's guard)

        int[] viewport = [x, y, sizePx, sizePx];
        var identity = ColumnMajor(Matrix4d.Identity);
        var eye = CameraMath.Eye(camera.Yaw, camera.Pitch, ViewCubeMath.EyeDistance, Vector3d.Zero);
        var view = ColumnMajor(CameraMath.LookAt(eye, Vector3d.Zero, Vector3d.UnitZ));
        // Always orthographic, independent of the main projection — standard for
        // orientation widgets, and it matches ViewCubeMath.TryHit's ortho pick ray.
        var projection = ColumnMajor(
            CameraMath.Orthographic(ViewCubeMath.OrthoHalfExtent, 1, 0.5, 8));

        for (int face = 0; face < ViewCubeGeometry.Faces.Count; face++)
        {
            var color = ViewCubeGeometry.Faces[face].Color;
            if (cube.Hover is { } hover && hover.Dot(ViewCubeGeometry.Faces[face].Normal) > 0.5)
                color = ViewCubeGeometry.Brightened(color);
            draws.Add(new DrawCall
            {
                Program = LineProgram,
                Geometry = CubeFillsKey,
                Mode = "triangles",
                First = face * ViewCubeGeometry.VerticesPerFace,
                Count = ViewCubeGeometry.VerticesPerFace,
                Cull = false,
                // Fills pushed back slightly so edges and labels win the depth test
                // cleanly — the same trick as the scene's feature-edge overlay.
                PolygonOffset = [1f, 1f],
                // The depth clear that makes the cube an overlay, once, before its
                // first draw (glClear ignores the viewport rect).
                ClearDepth = face == 0,
                Viewport = viewport,
                Uniforms = CubeUniforms(identity, view, projection, Rgb(color), sectionActive),
            });
        }

        draws.Add(new DrawCall
        {
            Program = LineProgram,
            Geometry = CubeEdgesKey,
            First = 0,
            Count = CubeEdgeVertexCount,
            Cull = false,
            Viewport = viewport,
            Uniforms = CubeUniforms(identity, view, projection, Rgb(ViewCubeGeometry.EdgeColor), sectionActive),
        });
        draws.Add(new DrawCall
        {
            Program = LineProgram,
            Geometry = CubeLabelsKey,
            First = 0,
            Count = CubeLabelVertexCount,
            Cull = false,
            Viewport = viewport,
            Uniforms = CubeUniforms(identity, view, projection, Rgb(ViewCubeGeometry.LabelColor), sectionActive),
        });
    }

    /// <summary>A cube draw's uniforms: its own view/projection over an identity model,
    /// never section-clipped (widget chrome — the desktop flips the same uniform).</summary>
    private static Dictionary<string, object> CubeUniforms(
        float[] model, float[] view, float[] projection, float[] color, bool sectionActive)
    {
        var uniforms = new Dictionary<string, object>
        {
            ["uModel"] = model,
            ["uView"] = view,
            ["uProj"] = projection,
            ["uColor"] = color,
        };
        if (sectionActive)
            uniforms["uSectionEnabled"] = 0f;
        return uniforms;
    }

    private static void AppendTranslucent(
        List<DrawCall> draws, IReadOnlyList<ViewportInstance> instances,
        EffectiveMode[] modes, in Vector3d eye, int selected, int hovered, bool sectionActive)
    {
        int count = 0;
        for (int i = 0; i < instances.Count; i++)
        {
            if (modes[i] == EffectiveMode.Translucent && instances[i].Visible)
                count++;
        }
        if (count == 0)
            return;

        var order = new int[count];
        var depth = new double[count];
        int at = 0;
        for (int i = 0; i < instances.Count; i++)
        {
            if (modes[i] != EffectiveMode.Translucent || !instances[i].Visible)
                continue;
            order[at] = i;
            depth[at] = (instances[i].WorldCenter - eye).LengthSquared;
            at++;
        }
        RenderModes.SortBackToFront(order, depth, count);

        for (int k = 0; k < count; k++)
        {
            int index = order[k];
            var instance = instances[index];
            var uniforms = new Dictionary<string, object>
            {
                ["uModel"] = ColumnMajor(instance.World),
                ["uColor"] = Rgb(instance.Color),
                ["uAlpha"] = TranslucentAlpha,
            };
            Highlighted(uniforms, index, selected, hovered);
            Unclipped(uniforms, sectionActive, instance);
            draws.Add(new DrawCall
            {
                Program = MeshProgram,
                Geometry = instance.GeometryKey,
                Blend = true,
                DepthWrite = false,
                Cull = false,
                Uniforms = uniforms,
            });
        }

        for (int k = 0; k < count; k++)
        {
            int index = order[k];
            var instance = instances[index];
            if (instance.EdgeKey is null || instance.EdgeVertexCount == 0)
                continue;
            draws.Add(Line(instance.EdgeKey, ColumnMajor(instance.World),
                index == selected ? Rgb(Highlight.Selection) : EdgeColor,
                first: 0, count: instance.EdgeVertexCount,
                unclip: sectionActive && !instance.ClippedBySection));
        }
    }

    /// <summary>Adds the per-draw <c>uSectionEnabled = 0</c> override for an instance
    /// exempt from sectioning (<see cref="ViewportInstance.ClippedBySection"/> false),
    /// and only while sections are active — the neutral state says nothing, the same
    /// discipline <see cref="Highlighted"/> follows.</summary>
    private static void Unclipped(
        Dictionary<string, object> uniforms, bool sectionActive, in ViewportInstance instance)
    {
        if (sectionActive && !instance.ClippedBySection)
            uniforms["uSectionEnabled"] = 0f;
    }

    /// <summary>Adds the selection/hover uniform to a fill draw, and only when it is
    /// not neutral: the frame's shared uniforms already carry 0, so an ordinary fill
    /// says nothing and a highlighted one says exactly what changed.</summary>
    private static void Highlighted(
        Dictionary<string, object> uniforms, int index, int selected, int hovered)
    {
        float strength = Highlight.Strength(index, selected, hovered);
        if (strength != 0f)
            uniforms["uHighlight"] = strength;
    }

    /// <summary>Auto-framing pose for a scene: the viewer's first-visit iso view.</summary>
    public static CameraState DefaultCamera(in Aabb bounds) => bounds.IsEmpty
        ? new CameraState(0.7, 0.45, 15.0, Vector3d.Zero)
        : new CameraState(0.7, 0.45, CameraMath.FrameDistance(bounds), bounds.Center);

    private static DrawCall Line(
        string key, float[] model, float[] color, int first, int count, bool unclip = false)
    {
        var uniforms = new Dictionary<string, object> { ["uModel"] = model, ["uColor"] = color };
        if (unclip)
            uniforms["uSectionEnabled"] = 0f;
        return new DrawCall
        {
            Program = LineProgram,
            Geometry = key,
            First = first,
            Count = count,
            Cull = false,
            Uniforms = uniforms,
        };
    }

    private static float[] Rgb(in PartColor color) => [color.R, color.G, color.B];

    private static float[] Rgb(in (float R, float G, float B) color) => [color.R, color.G, color.B];

    private static (float R, float G, float B) Tuple(in PartColor color) =>
        (color.R, color.G, color.B);

    private static float[] ColumnMajor(in Matrix4d m)
    {
        var values = new float[16];
        CameraMath.WriteColumnMajor(m, values);
        return values;
    }

    private static float[] Vec3(in Vector3d v) => [(float)v.X, (float)v.Y, (float)v.Z];
}
