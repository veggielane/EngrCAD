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
public readonly record struct ViewportInstance(
    string GeometryKey, Matrix4d World, PartColor Color, Vector3d WorldCenter,
    DisplayMode Mode = DisplayMode.Shaded,
    string? EdgeKey = null, int EdgeVertexCount = 0,
    string? WireKey = null, int WireVertexCount = 0,
    bool Visible = true);

/// <summary>Keys the scene furniture is uploaded under, and how many vertices the grid
/// has (the axes are always three consecutive pairs).</summary>
/// <param name="GridKey">Key of the ground-grid line upload.</param>
/// <param name="GridVertexCount">Vertices in the grid upload.</param>
/// <param name="AxesKey">Key of the world-axes line upload (6 vertices).</param>
public readonly record struct ViewportFurniture(string GridKey, int GridVertexCount, string AxesKey);

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
    public static FrameDescription Build(
        IReadOnlyList<ViewportInstance> instances,
        CameraState camera,
        in Aabb sceneBounds,
        double aspect,
        ViewportFurniture? furniture = null,
        ViewStyle style = ViewStyle.ShadedWithEdges,
        double pixelScale = 1.0,
        int selected = -1,
        int hovered = -1)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(aspect, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pixelScale, 0);

        var eye = CameraMath.Eye(camera.Yaw, camera.Pitch, camera.Distance, camera.Target);
        var view = CameraMath.LookAt(eye, camera.Target, Vector3d.UnitZ);
        var (near, far) = CameraMath.FrustumPlanes(camera.Distance, sceneBounds);
        var projection = CameraMath.Perspective(Math.PI / 4, aspect, near, far);

        // Frame-constant uniforms travel once instead of once per draw call. Names absent
        // from a program are skipped, so one set serves the mesh, line and point programs
        // and is simply ignored by the background gradient.
        //
        // uSectionCount is deliberately NOT here: it is an `int` uniform and the interop
        // marshals every JSON number through uniform1f, which GL rejects on an int. The
        // section rule short-circuits on uSectionEnabled, and an unset int uniform is
        // already 0, so the neutral state needs nothing said about it.
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
            ["uSectionEnabled"] = 0f,
            ["uPointSize"] = PointSize * (float)pixelScale,
        };

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
            var identity = ColumnMajor(Matrix4d.Identity);
            draws.Add(Line(f.GridKey, identity, GridColor, first: 0, count: f.GridVertexCount));
            draws.Add(Line(f.AxesKey, identity, AxisXColor, first: 0, count: 2));
            draws.Add(Line(f.AxesKey, identity, AxisYColor, first: 2, count: 2));
            draws.Add(Line(f.AxesKey, identity, AxisZColor, first: 4, count: 2));
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
                        first: 0, count: instance.EdgeVertexCount));
                    break;
                // Wireframe draws in the part's OWN colour, not the edge colour: with no
                // fill behind them the dark edge colour would be nearly invisible against
                // the background, and colour is the only thing left to tell parts apart.
                // With no fill there is also no uHighlight to act on, which is why
                // selection and hover reach a wireframe part through its LINE colour.
                case EffectiveMode.Wireframe when instance.WireKey is not null && instance.WireVertexCount > 0:
                    draws.Add(Line(instance.WireKey, ColumnMajor(instance.World),
                        Rgb(Highlight.LineColor(i, selected, hovered, Tuple(instance.Color))),
                        first: 0, count: instance.WireVertexCount));
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
            draws.Add(new DrawCall
            {
                Program = PointProgram,
                Geometry = instance.GeometryKey,
                Mode = "points",
                Cull = false,
                Uniforms = new Dictionary<string, object>
                {
                    ["uModel"] = ColumnMajor(instance.World),
                    // Point sprites are lines' cousins here: no fill, so the highlight
                    // must reach them through the colour.
                    ["uColor"] = Rgb(Highlight.LineColor(i, selected, hovered, Tuple(instance.Color))),
                },
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
        AppendTranslucent(draws, instances, modes, eye, selected, hovered);

        return new FrameDescription { Clear = ClearColor, Shared = shared, Draws = draws };
    }

    private static void AppendTranslucent(
        List<DrawCall> draws, IReadOnlyList<ViewportInstance> instances,
        EffectiveMode[] modes, in Vector3d eye, int selected, int hovered)
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
                first: 0, count: instance.EdgeVertexCount));
        }
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

    private static DrawCall Line(string key, float[] model, float[] color, int first, int count) =>
        new()
        {
            Program = LineProgram,
            Geometry = key,
            First = first,
            Count = count,
            Cull = false,
            Uniforms = new Dictionary<string, object> { ["uModel"] = model, ["uColor"] = color },
        };

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
