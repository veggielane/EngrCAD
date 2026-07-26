using EngrCAD.Core;
using EngrCAD.Modeling;
using EngrCAD.Viewer;

namespace EngrCAD.Web;

/// <summary>
/// One instance ready to draw: an already-uploaded geometry key plus the pose and colour
/// it draws with. Instances of the same <see cref="Part"/> share a key, so N occurrences
/// cost one upload and N matrices — the same rule the desktop viewer follows.
/// </summary>
/// <param name="GeometryKey">Key the part's mesh was uploaded under.</param>
/// <param name="World">Instance transform (frame chain x part transform).</param>
/// <param name="Color">Fill colour.</param>
/// <param name="WorldCenter">Instance bounds centre, for depth-ordered passes.</param>
public readonly record struct ViewportInstance(
    string GeometryKey, Matrix4d World, PartColor Color, Vector3d WorldCenter);

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
/// matrices, <see cref="RenderGeometry"/> for the furniture), and the JavaScript that
/// executes the result contains no decisions at all.
///
/// <para>It is deliberately a pure function of its arguments — no GL handle, no JS
/// runtime, no component state — so what the browser will draw is a unit-testable value.
/// The desktop passes cannot be tested that way (their output is pixels), which is
/// exactly why they drifted.</para>
///
/// <para><b>Scope, stated rather than implied.</b> This rung draws shaded fills and the
/// scene furniture. Feature edges, per-part display modes, the global
/// <see cref="ViewStyle"/>, section planes and baked occlusion are later rungs: the
/// shared rules for all of them already exist (<see cref="RenderModes.Resolve"/>,
/// <see cref="SectionClip"/>) and are deliberately not half-applied here, because a
/// mode resolved and then ignored looks like support and is not.</para>
/// </summary>
public static class ViewportFrame
{
    /// <summary>Program names, as passed to <see cref="WebGlContext.CreateProgramAsync"/>.</summary>
    public const string MeshProgram = "mesh";

    /// <inheritdoc cref="MeshProgram"/>
    public const string LineProgram = "line";

    /// <inheritdoc cref="MeshProgram"/>
    public const string BackgroundProgram = "background";

    /// <summary>Clear colour behind the gradient — the viewport's, to the byte
    /// (<c>ViewportControl.OnOpenGlRender</c> and <c>OffscreenRenderer.Draw</c> both
    /// clear to this before drawing the gradient over it).</summary>
    public static readonly float[] ClearColor = [0.11f, 0.12f, 0.14f];

    /// <summary>Directional light, shared with both desktop passes.</summary>
    private static readonly Vector3d LightDirection = new Vector3d(-0.5, -0.7, -0.9).Normalized();

    private static readonly float[] GridColor = [0.24f, 0.26f, 0.29f];
    private static readonly float[] AxisXColor = [0.75f, 0.30f, 0.30f];
    private static readonly float[] AxisYColor = [0.33f, 0.66f, 0.33f];
    private static readonly float[] AxisZColor = [0.35f, 0.48f, 0.85f];

    /// <summary>
    /// Builds the frame for one draw: background gradient, ground grid and world axes,
    /// then shaded fills in instance order.
    /// </summary>
    /// <param name="instances">Posed instances referencing already-uploaded geometry.</param>
    /// <param name="camera">Orbit pose (see <see cref="CameraMath"/>).</param>
    /// <param name="sceneBounds">World bounds of everything drawn; sizes the frustum.</param>
    /// <param name="aspect">Viewport width / height.</param>
    /// <param name="furniture">Grid and axes keys, or null to draw neither.</param>
    public static FrameDescription Build(
        IReadOnlyList<ViewportInstance> instances,
        CameraState camera,
        in Aabb sceneBounds,
        double aspect,
        ViewportFurniture? furniture = null)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(aspect, 0);

        var eye = CameraMath.Eye(camera.Yaw, camera.Pitch, camera.Distance, camera.Target);
        var view = CameraMath.LookAt(eye, camera.Target, Vector3d.UnitZ);
        var (near, far) = CameraMath.FrustumPlanes(camera.Distance, sceneBounds);
        var projection = CameraMath.Perspective(Math.PI / 4, aspect, near, far);

        // Frame-constant uniforms travel once instead of once per draw call. Names absent
        // from a program are skipped, so one set serves the mesh and line programs and is
        // simply ignored by the background gradient.
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
            ["uHighlight"] = 0f,          // selection is a later rung
            ["uAlpha"] = 1f,
            ["uAmbientOcclusion"] = 0f,   // no bake in the browser; 0 leaves the factor exactly 1
            ["uSectionEnabled"] = 0f,
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

        foreach (var instance in instances)
        {
            draws.Add(new DrawCall
            {
                Program = MeshProgram,
                Geometry = instance.GeometryKey,
                Uniforms = new Dictionary<string, object>
                {
                    ["uModel"] = ColumnMajor(instance.World),
                    ["uColor"] = new[] { instance.Color.R, instance.Color.G, instance.Color.B },
                },
                // Face culling stays OFF, matching both desktop passes: clipping a solid
                // with a section plane exposes its interior as backfaces, which the shared
                // fragment shader shades as cut material via gl_FrontFacing. Enabling
                // culling here would work today and silently break that rung.
                Cull = false,
                // Fills sit back a touch so a feature-edge overlay wins the depth test.
                // Kept now so adding the overlay is a new draw call, not a state change.
                PolygonOffset = [1f, 1f],
            });
        }

        return new FrameDescription { Clear = ClearColor, Shared = shared, Draws = draws };
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

    private static float[] ColumnMajor(in Matrix4d m)
    {
        var values = new float[16];
        CameraMath.WriteColumnMajor(m, values);
        return values;
    }

    private static float[] Vec3(in Vector3d v) => [(float)v.X, (float)v.Y, (float)v.Z];
}
