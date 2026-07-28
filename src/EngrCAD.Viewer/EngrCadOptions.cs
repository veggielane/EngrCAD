using EngrCAD.Modeling;
using Microsoft.Extensions.Logging;

namespace EngrCAD.Viewer;

/// <summary>
/// Host-level defaults for the viewer entry points — a plain POCO so it binds
/// directly as <c>IOptions&lt;EngrCadOptions&gt;</c> in a generic-host app
/// (delegate/interface properties, <see cref="Logger"/> among them, are simply left
/// unbound by configuration and set in code). Build one by hand, from DI, or fluently
/// via <see cref="EngrCad.Configure()"/>.
/// </summary>
public sealed class EngrCadOptions
{
    /// <summary>Window title (also keys the persisted live-reload camera pose).</summary>
    public string Title { get; set; } = "EngrCAD";

    /// <summary>
    /// Default mesh quality for display and mesh export. Precedence: a
    /// <see cref="Scene"/> constructed with explicit options always wins; otherwise
    /// this quality; otherwise <c>MeshQuality</c>'s defaults
    /// (<see cref="Scene.ResolveQuality"/> implements the rule).
    /// </summary>
    public MeshQuality? Quality { get; set; }

    /// <summary>Image width in pixels for <c>--render</c> /
    /// <see cref="EngrCadBuilder.RenderToImage"/>.</summary>
    public int RenderWidth { get; set; } = 1280;

    /// <summary>Image height in pixels for <c>--render</c> /
    /// <see cref="EngrCadBuilder.RenderToImage"/>.</summary>
    public int RenderHeight { get; set; } = 800;

    /// <summary>
    /// Where status/error reporting goes (exports, headless renders, the live-reload
    /// messages that also appear in the overlay). Null means
    /// <see cref="EngrCadLoggers.Console"/> — see that type for why the default is a
    /// console sink rather than <c>NullLogger.Instance</c>, and pass
    /// <c>NullLogger.Instance</c> when you want silence.
    /// </summary>
    public ILogger? Logger { get; set; }

    /// <summary>Callback invoked once the GL viewport exists — custom hosts capture
    /// the <see cref="ViewportControl"/> here.</summary>
    public Action<ViewportControl>? OnViewportReady { get; set; }

    /// <summary>Global view style for headless renders (<c>--render</c> /
    /// <see cref="EngrCadBuilder.RenderToImage"/>); default
    /// <see cref="ViewStyle.ShadedWithEdges"/>. Parts with an explicitly non-default
    /// <c>Part.DisplayMode</c> still override it, exactly as in the window.</summary>
    public ViewStyle RenderStyle { get; set; } = ViewStyle.ShadedWithEdges;

    /// <summary>Which world axis the headless-render section plane is perpendicular
    /// to (default Z). Only meaningful when <see cref="SectionOffset"/> is set.</summary>
    public SectionAxis SectionAxis { get; set; } = SectionAxis.Z;

    /// <summary>Non-null enables an axis-aligned section plane in headless renders:
    /// geometry beyond this offset along <see cref="SectionAxis"/> is clipped and
    /// exposed interiors shade as cut material (the viewport's Section toggle,
    /// headless). Null (the default) renders unsectioned.</summary>
    public double? SectionOffset { get; set; }

    /// <summary>
    /// Exploded-view factor for headless renders and for the window's initial state:
    /// 0 (the default) assembled, 1 fully exploded, anything between interpolates.
    /// Non-zero derives the occurrence offsets through <c>Assembly.AutoExplode</c> unless
    /// the design set them itself. Assembly-free scenes are unaffected — a loose part
    /// belongs to no assembly and so has nothing to explode away from.
    /// </summary>
    public double Explode { get; set; }

    /// <summary>
    /// Builds the window's <see cref="Viewer.Animation"/> for a scene — played by the
    /// toolbar transport (play/pause/loop/scrub) and re-invoked per live reload with
    /// the fresh scene, so tracks always reference current occurrences. A factory
    /// rather than an instance because tracks capture the scene they pose, and the
    /// scene is remade by every hot reload; null (or a factory returning null) shows
    /// no transport. Invoked OFF the UI thread — track construction may read bounds
    /// (mesh parts), the same rule <c>Scene.PreMesh</c> follows.
    /// </summary>
    public Func<Scene, Animation?>? Animation { get; set; }

    /// <summary>
    /// Several section planes for headless renders — two perpendicular planes are the
    /// classic quarter cut, three an octant. When set it wins over
    /// <see cref="SectionAxis"/>/<see cref="SectionOffset"/>; null (the default) keeps
    /// the single-plane behavior.
    /// </summary>
    public IReadOnlyList<SectionPlane>? SectionPlanes { get; set; }

    /// <summary>How <see cref="SectionPlanes"/> combine (default
    /// <see cref="Viewer.SectionCombine.Intersection"/> — the quarter/octant cutaway).
    /// Irrelevant with a single plane.</summary>
    public SectionCombine SectionCombine { get; set; } = SectionCombine.Intersection;

    /// <summary>
    /// The shipped default for ambient occlusion (window and headless): ON. Baked AO is
    /// a one-off CPU cost at scene load and costs nothing per frame, and the crevice
    /// shading it adds is what separates a CAD view from a flat-lit one.
    /// </summary>
    public const bool AmbientOcclusionDefault = true;

    /// <summary>
    /// Baked per-vertex ambient occlusion — pockets, bores, ribs and fillet roots
    /// darken where the geometry occludes itself (default
    /// <see cref="AmbientOcclusionDefault"/>). Applies to the viewport
    /// (<see cref="ViewportControl.AmbientOcclusion"/>, which the toolbar's AO toggle
    /// also drives) and to headless renders. Off reproduces the pre-AO look exactly and
    /// skips the bake.
    /// </summary>
    public bool AmbientOcclusion { get; set; } = AmbientOcclusionDefault;

    /// <summary>
    /// The shipped default for on-demand tab meshing: ON. A document's tabs are meshed
    /// when they are first VIEWED, so the window opens immediately instead of waiting
    /// for tabs the user may never open (measured on the demo scene: 54 s to a window,
    /// down to under 2 s).
    /// </summary>
    public const bool LazyTabMeshingDefault = true;

    /// <summary>
    /// Mesh each tab when it is first shown, on a background task with a progress bar,
    /// instead of meshing the whole scene before the window opens (default
    /// <see cref="LazyTabMeshingDefault"/> = on). Meshes are cached per part, so a tab
    /// is meshed once however often it is revisited.
    /// <para><b>The escape hatch:</b> set this false (or
    /// <see cref="EngrCadBuilder.WithLazyTabMeshing"/>(false), or <c>--mesh all</c> on
    /// the command line) to restore the eager behavior — <c>Scene.PreMesh</c> for the
    /// whole document before the first frame, no progress UI, every tab instant once
    /// the window appears.</para>
    /// <para>Headless paths are unaffected: <c>--export</c>, <c>--render</c> and
    /// <see cref="EngrCad.RenderToImage"/> mesh exactly what they need, eagerly. A
    /// custom host that drives <see cref="ViewportControl.SetParts"/> itself must
    /// prepare its parts off the render thread (<c>Part.Prepare</c>/<c>Tab.PreMesh</c>/
    /// <c>Scene.PreMesh</c>) — the contract <see cref="ViewportControl.SetInstances"/>
    /// always documented — or set this false.</para>
    /// </summary>
    public bool LazyTabMeshing { get; set; } = LazyTabMeshingDefault;

    /// <summary>
    /// Non-null enables the viewer's remote-control endpoint: newline-delimited
    /// JSON-RPC 2.0 on a <b>loopback-only</b> TCP port (see <c>RemoteControl.cs</c> for
    /// the vocabulary — set_view/fit/set_section/set_display_mode/set_view_style/
    /// select_part/get_selection/measure/screenshot). <b>Off by default, deliberately</b>:
    /// this endpoint moves the camera and writes screenshot files, so it must be asked
    /// for (<see cref="EngrCadBuilder.WithRemoteControl"/> or <c>--rpc [port]</c>); an
    /// optional token gates every request. Windowed modes only — headless paths have
    /// the MCP server.
    /// </summary>
    public RemoteControlOptions? RemoteControl { get; set; }
}

/// <summary>
/// Fluent configuration for the viewer entry points:
/// <code>
/// return EngrCad.Configure()
///     .WithTitle("bracket")
///     .WithQuality(new MeshQuality { SegmentsPerCircle = 48 })
///     .WithRenderSize(1920, 1080)
///     .Run(args, BuildScene);
/// </code>
/// Terminal methods (<see cref="Run"/>, <see cref="Show"/>, <see cref="ShowLive"/>,
/// <see cref="RenderToImage"/>) mirror the static <see cref="EngrCad"/> methods with
/// the accumulated <see cref="Options"/> applied.
/// </summary>
public sealed class EngrCadBuilder
{
    /// <summary>The options being accumulated — the same instance passed to
    /// <see cref="EngrCad.Configure(EngrCadOptions)"/>, so DI-provided options flow
    /// through unchanged.</summary>
    public EngrCadOptions Options { get; }

    internal EngrCadBuilder(EngrCadOptions options) => Options = options;

    /// <summary>Sets the window title.</summary>
    public EngrCadBuilder WithTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title must be non-empty.", nameof(title));
        Options.Title = title;
        return this;
    }

    /// <summary>Sets the default mesh quality (scenes constructed with their own
    /// explicit options still win — see <see cref="Scene.ResolveQuality"/>).</summary>
    public EngrCadBuilder WithQuality(MeshQuality quality)
    {
        Options.Quality = quality ?? throw new ArgumentNullException(nameof(quality));
        return this;
    }

    /// <summary>Sets the headless render image size in pixels.</summary>
    public EngrCadBuilder WithRenderSize(int width, int height)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "Render width must be positive.");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), height, "Render height must be positive.");
        Options.RenderWidth = width;
        Options.RenderHeight = height;
        return this;
    }

    /// <summary>Routes status/error reporting through <paramref name="logger"/> instead
    /// of the console. Pass <c>NullLogger.Instance</c> for silence.</summary>
    public EngrCadBuilder WithLogger(ILogger logger)
    {
        Options.Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        return this;
    }

    /// <summary>Routes status/error reporting through a logger from
    /// <paramref name="factory"/>, under the <c>EngrCAD</c> category.</summary>
    public EngrCadBuilder WithLoggerFactory(ILoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return WithLogger(factory.CreateLogger("EngrCAD"));
    }

    /// <summary>
    /// Enables the loopback-only remote-control endpoint when a window opens (see
    /// <see cref="EngrCadOptions.RemoteControl"/>). <paramref name="port"/> 0 picks an
    /// ephemeral port, reported in the log and status bar; <paramref name="token"/>,
    /// when set, must accompany every request.
    /// </summary>
    public EngrCadBuilder WithRemoteControl(int port = 0, string? token = null)
    {
        if (port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be 0..65535.");
        Options.RemoteControl = new RemoteControlOptions { Port = port, Token = token };
        return this;
    }

    /// <summary>Registers a callback invoked once the GL viewport exists.</summary>
    public EngrCadBuilder WithViewportReady(Action<ViewportControl> callback)
    {
        Options.OnViewportReady = callback ?? throw new ArgumentNullException(nameof(callback));
        return this;
    }

    /// <summary>Sets the global view style for headless renders (<c>--render</c> also
    /// accepts <c>--render-style points|wireframe|shaded|shaded-edges</c>, which wins
    /// over this default).</summary>
    public EngrCadBuilder WithViewStyle(ViewStyle style)
    {
        Options.RenderStyle = style;
        return this;
    }

    /// <summary>Enables the axis-aligned section plane for headless renders: clip
    /// beyond <paramref name="offset"/> along <paramref name="axis"/> (<c>--render</c>
    /// also accepts <c>--section x|y|z &lt;offset&gt;</c>, which wins over this
    /// default).</summary>
    public EngrCadBuilder WithSection(SectionAxis axis, double offset)
    {
        Options.SectionAxis = axis;
        Options.SectionOffset = offset;
        Options.SectionPlanes = null;
        return this;
    }

    /// <summary>Enables several section planes at once for headless renders — two
    /// perpendicular planes give the quarter cut, three an octant. The combine rule
    /// defaults to <see cref="SectionCombine.Intersection"/> (the CAD cutaway);
    /// <c>--section</c> with several axis/offset pairs is the CLI equivalent and wins
    /// over this default.</summary>
    public EngrCadBuilder WithSection(params SectionPlane[] planes) =>
        WithSection(SectionCombine.Intersection, planes);

    /// <summary>Several section planes with an explicit combine rule — see
    /// <see cref="WithSection(SectionPlane[])"/>.</summary>
    public EngrCadBuilder WithSection(SectionCombine combine, params SectionPlane[] planes)
    {
        ArgumentNullException.ThrowIfNull(planes);
        if (planes.Length == 0)
            throw new ArgumentException("At least one section plane is required.", nameof(planes));
        Options.SectionPlanes = planes;
        Options.SectionCombine = combine;
        Options.SectionOffset ??= planes[0].Offset;   // keeps "sectioning is on" readable
        return this;
    }

    /// <summary>Sets the exploded-view factor (0 assembled → 1 fully exploded) for
    /// headless renders and the window's initial state. <c>--explode &lt;factor&gt;</c>
    /// wins over this default. See <see cref="EngrCadOptions.Explode"/>.</summary>
    public EngrCadBuilder WithExplode(double factor)
    {
        if (factor < 0)
            throw new ArgumentOutOfRangeException(nameof(factor), "An explode factor cannot be negative.");
        Options.Explode = factor;
        return this;
    }

    /// <summary>Gives the window an animation to play (toolbar transport:
    /// play/pause/loop/scrub). The factory runs per scene — including per live reload —
    /// off the UI thread. See <see cref="EngrCadOptions.Animation"/>.</summary>
    public EngrCadBuilder WithAnimation(Func<Scene, Animation?> animation)
    {
        ArgumentNullException.ThrowIfNull(animation);
        Options.Animation = animation;
        return this;
    }

    /// <summary>Enables or disables baked ambient occlusion for the viewport and for
    /// headless renders (<c>--ao on|off</c> wins over this default). On by default —
    /// see <see cref="EngrCadOptions.AmbientOcclusion"/>.</summary>
    public EngrCadBuilder WithAmbientOcclusion(bool enabled = true)
    {
        Options.AmbientOcclusion = enabled;
        return this;
    }

    /// <summary>
    /// Meshes each tab when it is first viewed (the default) rather than meshing the
    /// whole document before the window opens; pass <c>false</c> for the eager
    /// behavior. <c>--mesh lazy|all</c> wins over this default. See
    /// <see cref="EngrCadOptions.LazyTabMeshing"/>.
    /// </summary>
    public EngrCadBuilder WithLazyTabMeshing(bool enabled = true)
    {
        Options.LazyTabMeshing = enabled;
        return this;
    }

    /// <summary>Standard main-method wrapper — <see cref="EngrCad.Run"/> with these
    /// options (no args → live, <c>--view</c>, <c>--export</c>, <c>--render</c>).</summary>
    public int Run(string[] args, Func<Scene> sceneFactory) =>
        EngrCad.RunCore(args, sceneFactory, Options);

    /// <summary>Opens the viewer on <paramref name="scene"/> and blocks —
    /// <see cref="EngrCad.Show"/> with these options.</summary>
    public void Show(Scene scene) => EngrCad.ShowCore(scene, Options);

    /// <summary>The live-modeling loop — <see cref="EngrCad.ShowLive"/> with these
    /// options.</summary>
    public void ShowLive(Func<Scene> sceneFactory) => EngrCad.ShowLiveCore(sceneFactory, Options);

    /// <summary>Headless PNG render — <see cref="EngrCad.RenderToImage"/> at the
    /// configured size and quality. The optional <paramref name="style"/>/
    /// <paramref name="sectionAxis"/>/<paramref name="sectionOffset"/> mirror the
    /// static overload's parameters; when omitted the accumulated options
    /// (<see cref="WithViewStyle"/>/<see cref="WithSection"/>) apply.</summary>
    public void RenderToImage(
        Scene scene, string path, CameraState? camera = null,
        ViewStyle? style = null, SectionAxis? sectionAxis = null, double? sectionOffset = null,
        bool? ambientOcclusion = null, IReadOnlyList<SectionPlane>? sectionPlanes = null,
        double? explode = null)
    {
        scene.PreMesh(Options.Quality); // meshes cache, so the inner PreMesh is a no-op
        EngrCad.RenderToImage(scene, path, Options.RenderWidth, Options.RenderHeight, camera,
            style ?? Options.RenderStyle,
            sectionAxis ?? Options.SectionAxis,
            sectionOffset ?? Options.SectionOffset,
            ambientOcclusion ?? Options.AmbientOcclusion,
            sectionPlanes ?? Options.SectionPlanes,
            Options.SectionCombine,
            preview: null,
            explode ?? Options.Explode);
    }
}
