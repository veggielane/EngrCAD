using EngrCAD.Core;
using EngrCAD.Modeling;
using EngrCAD.Viewer;
using EngrCAD.Web;
using Xunit;

namespace EngrCAD.Web.Tests;

/// <summary>
/// Per-part display modes and the global view style, asserted as a frame value.
///
/// <para>These are the assertions that matter for this rung, and they are deliberately
/// about <b>relationships between modes</b> rather than about one mode in isolation: that
/// wireframe draws lines and no fill, that shaded-with-edges draws strictly more than
/// shaded, that translucency lands last and back-to-front. A single "it drew something"
/// check passes just as well when every mode quietly draws the same thing.</para>
///
/// <para>Blend state, depth-mask state and the sort are all IN the frame, which is why
/// they can be pinned here at all — the desktop passes decide them through a sequence of
/// GL state changes that only pixels can witness.</para>
/// </summary>
public class DisplayModeFrameTests
{
    private static readonly Aabb Bounds = new((-10, -10, 0), (10, 10, 5));

    private static CameraState Camera => ViewportFrame.DefaultCamera(Bounds);

    /// <summary>An instance with all three buffers uploaded, as the component always
    /// uploads them.</summary>
    private static ViewportInstance Instance(
        string name, DisplayMode mode = DisplayMode.Shaded, Vector3d at = default) =>
        new($"{name}", Matrix4d.CreateTranslation(at), Palette.Brass, at,
            mode, $"{name}.edges", 40, $"{name}.wire", 300);

    private static FrameDescription Build(
        IReadOnlyList<ViewportInstance> instances, ViewStyle style = ViewStyle.ShadedWithEdges,
        double pixelScale = 1.0) =>
        ViewportFrame.Build(instances, Camera, Bounds, aspect: 1.6, furniture: null, style, pixelScale);

    /// <summary>The draws after the background gradient (which every frame opens with).</summary>
    private static List<DrawCall> Model(FrameDescription frame) => [.. frame.Draws.Skip(1)];

    /// <summary>The geometry keys drawn, in order — the shape of a frame in one line.</summary>
    private static string?[] Order(FrameDescription frame) => [.. Model(frame).Select(c => c.Geometry)];

    // ---- precedence: one rule, not re-implemented here ----

    [Theory]
    [InlineData(ViewStyle.Points, DisplayMode.Shaded)]
    [InlineData(ViewStyle.Wireframe, DisplayMode.Shaded)]
    [InlineData(ViewStyle.Shaded, DisplayMode.Shaded)]
    [InlineData(ViewStyle.ShadedWithEdges, DisplayMode.Shaded)]
    [InlineData(ViewStyle.Points, DisplayMode.Wireframe)]
    [InlineData(ViewStyle.ShadedWithEdges, DisplayMode.Wireframe)]
    [InlineData(ViewStyle.Points, DisplayMode.Translucent)]
    [InlineData(ViewStyle.Wireframe, DisplayMode.Translucent)]
    public void EveryStyleAndModePairDrawsWhatTheSharedRuleResolves(ViewStyle style, DisplayMode mode)
    {
        // The frame must agree with RenderModes.Resolve for every combination, and the
        // expectation is READ FROM IT rather than restated: a second copy of the
        // precedence rule in a test is the same drift the shared rule exists to stop, and
        // it would agree with a broken implementation.
        var frame = Build([Instance("a", mode)], style);
        var model = Model(frame);

        switch (RenderModes.Resolve(style, mode))
        {
            case EffectiveMode.Points:
                Assert.Equal(ViewportFrame.PointProgram, Assert.Single(model).Program);
                break;
            case EffectiveMode.Wireframe:
                var wire = Assert.Single(model);
                Assert.Equal(ViewportFrame.LineProgram, wire.Program);
                Assert.Equal("a.wire", wire.Geometry);
                break;
            case EffectiveMode.Shaded:
                Assert.Equal(ViewportFrame.MeshProgram, Assert.Single(model).Program);
                break;
            case EffectiveMode.ShadedWithEdges:
                Assert.Equal(2, model.Count);
                Assert.Equal(ViewportFrame.MeshProgram, model[0].Program);
                Assert.Equal("a.edges", model[1].Geometry);
                break;
            case EffectiveMode.Translucent:
                Assert.Equal(2, model.Count);
                Assert.True(model[0].Blend);
                Assert.Equal("a.edges", model[1].Geometry);
                break;
        }
    }

    [Fact]
    public void AnExplicitPartModeOverridesTheGlobalStyleAndADefaultOneFollowsIt()
    {
        // The precedence rule in one sentence, drawn: with the style on Shaded, the
        // wireframe part still draws lines and the default part still follows the style.
        var frame = Build(
            [Instance("plain"), Instance("wire", DisplayMode.Wireframe)], ViewStyle.Shaded);
        var model = Model(frame);

        Assert.Equal(2, model.Count);
        Assert.Equal(ViewportFrame.MeshProgram, model[0].Program);
        Assert.Equal("plain", model[0].Geometry);
        Assert.Equal("wire.wire", model[1].Geometry);
    }

    // ---- what each mode actually draws ----

    [Fact]
    public void ShadedWithEdgesDrawsStrictlyMoreThanShaded()
    {
        var shaded = Model(Build([Instance("a")], ViewStyle.Shaded));
        var withEdges = Model(Build([Instance("a")], ViewStyle.ShadedWithEdges));

        // Not "both drew something": the edge overlay is an ADDITION to the same fill.
        Assert.Single(shaded);
        Assert.Equal(2, withEdges.Count);
        Assert.Equal(shaded[0].Geometry, withEdges[0].Geometry);
        Assert.Equal("a.edges", withEdges[1].Geometry);
        Assert.Equal(ViewportFrame.EdgeColor, (float[])withEdges[1].Uniforms!["uColor"]);
    }

    [Fact]
    public void WireframeDrawsLinesAndNoFillAtAll()
    {
        var model = Model(Build([Instance("a")], ViewStyle.Wireframe));

        var wire = Assert.Single(model);
        Assert.Equal(ViewportFrame.LineProgram, wire.Program);
        Assert.Equal("a.wire", wire.Geometry);
        Assert.Equal(300, wire.Count);
        // Every mesh edge, not the feature edges: a wireframe shows the tessellation.
        Assert.DoesNotContain(model, call => call.Geometry == "a.edges");
        Assert.DoesNotContain(model, call => call.Program == ViewportFrame.MeshProgram);
    }

    [Fact]
    public void WireframeKeepsThePartColourWhereTheEdgeOverlayGoesDark()
    {
        // With no fill behind them, edge-coloured lines would be nearly invisible against
        // the background — and colour is all that is left to tell parts apart.
        var wire = Assert.Single(Model(Build([Instance("a")], ViewStyle.Wireframe)));
        var edges = Model(Build([Instance("a")], ViewStyle.ShadedWithEdges))[1];

        Assert.Equal([Palette.Brass.R, Palette.Brass.G, Palette.Brass.B], (float[])wire.Uniforms!["uColor"]);
        Assert.Equal(ViewportFrame.EdgeColor, (float[])edges.Uniforms!["uColor"]);
    }

    [Fact]
    public void PointsDrawTheMeshBufferAsSprites()
    {
        var points = Assert.Single(Model(Build([Instance("a")], ViewStyle.Points)));

        Assert.Equal(ViewportFrame.PointProgram, points.Program);
        Assert.Equal("a", points.Geometry);          // the mesh buffer, not a line buffer
        Assert.Equal("points", points.Mode);
        Assert.Equal([Palette.Brass.R, Palette.Brass.G, Palette.Brass.B],
            (float[])points.Uniforms!["uColor"]);
    }

    [Fact]
    public void PointSizeFollowsTheDevicePixelRatio()
    {
        // gl_PointSize is measured in FRAMEBUFFER pixels, so a 2x display needs twice the
        // value to look the same. The desktop window multiplies by its DPI scaling and the
        // offscreen pass by its supersample factor for exactly this reason.
        Assert.Equal(ViewportFrame.PointSize, Build([], pixelScale: 1.0).Shared!["uPointSize"]);
        Assert.Equal(ViewportFrame.PointSize * 2f, Build([], pixelScale: 2.0).Shared!["uPointSize"]);
    }

    [Fact]
    public void PointSizeIsAFloatNotAnInt()
    {
        // uPointSize is a float uniform and the interop dispatches on the runtime type:
        // an int would take uniform1i and GL would reject it on a float, silently leaving
        // the sprite at 1 pixel.
        Assert.IsType<float>(Build([]).Shared!["uPointSize"]);
    }

    // ---- translucency: ordering and state ----

    [Fact]
    public void TranslucentFillsBlendWithDepthWritesOff()
    {
        var fill = Model(Build([Instance("a", DisplayMode.Translucent)]))[0];

        Assert.True(fill.Blend);
        Assert.False(fill.DepthWrite);
        Assert.True(fill.DepthTest);          // still occluded by opaque geometry in front
        Assert.Equal(ViewportFrame.TranslucentAlpha, fill.Uniforms!["uAlpha"]);
    }

    [Fact]
    public void TranslucentFillsCarryNoPolygonOffset()
    {
        // The fills write no depth, so their edges have nothing to z-fight with — and the
        // desktop disables polygon offset before this pass for the same reason.
        var frame = Model(Build([Instance("a", DisplayMode.Translucent)]));

        Assert.Null(frame[0].PolygonOffset);
        Assert.NotNull(Model(Build([Instance("b")]))[0].PolygonOffset);   // opaque fills do
    }

    [Fact]
    public void TranslucentSilhouetteEdgesDrawOpaqueOnTop()
    {
        var model = Model(Build([Instance("a", DisplayMode.Translucent)]));

        var edges = model[1];
        Assert.Equal("a.edges", edges.Geometry);
        Assert.False(edges.Blend);
        Assert.True(edges.DepthWrite);
        Assert.Equal(ViewportFrame.EdgeColor, (float[])edges.Uniforms!["uColor"]);
    }

    [Fact]
    public void TranslucentPartsDrawBackToFront()
    {
        // Alpha blending is not commutative, so the far part must be drawn first whichever
        // order the instances arrive in. The camera looks from +X+Y+Z at the origin, so
        // the part at -20 is the far one.
        var near = Instance("near", DisplayMode.Translucent, (8, 8, 0));
        var far = Instance("far", DisplayMode.Translucent, (-20, -20, 0));
        ViewportInstance[][] orders = [[near, far], [far, near]];

        foreach (var supplied in orders)
        {
            var fills = Model(Build(supplied)).Where(c => c.Blend).ToList();
            Assert.Equal(2, fills.Count);
            Assert.Equal("far", fills[0].Geometry);
            Assert.Equal("near", fills[1].Geometry);
        }
    }

    [Fact]
    public void TranslucentPartsDrawAfterEveryOpaqueOne()
    {
        // Blending reads the colour already in the buffer, so anything opaque behind a
        // see-through part has to be there before it is drawn — including opaque parts
        // that come LATER in the instance list.
        var model = Model(Build(
            [Instance("glass", DisplayMode.Translucent), Instance("solid"), Instance("mesh", DisplayMode.Wireframe)]));

        int lastOpaque = model.FindLastIndex(c => !c.Blend && c.Geometry?.StartsWith("glass") != true);
        int firstBlended = model.FindIndex(c => c.Blend);
        Assert.True(firstBlended > lastOpaque,
            $"a translucent fill at {firstBlended} draws before opaque geometry at {lastOpaque}");
    }

    // ---- pass structure ----

    [Fact]
    public void AllFillsPrecedeAllEdges()
    {
        // One part's fill must not be able to hide another part's edges, which is why the
        // overlay is its own pass rather than a fill-then-edge pair per instance.
        string?[] expected = ["a", "b", "a.edges", "b.edges"];

        Assert.Equal(expected, Order(Build([Instance("a", at: (0, 0, 0)), Instance("b", at: (10, 0, 0))])));
    }

    [Fact]
    public void APartWithNoUploadedEdgesSimplyDrawsItsFill()
    {
        // GetFeatureEdges can legitimately return nothing (a part whose lowering failed
        // and whose mesh has no sharp dihedrals). That is a missing overlay, not a
        // missing part.
        var bare = new ViewportInstance("a", Matrix4d.Identity, Palette.Steel, Vector3d.Zero);

        var fill = Assert.Single(Model(Build([bare])));
        Assert.Equal(ViewportFrame.MeshProgram, fill.Program);
    }

    [Fact]
    public void APartWithNoUploadedWireEdgesDrawsNothingInWireframe()
    {
        var bare = new ViewportInstance("a", Matrix4d.Identity, Palette.Steel, Vector3d.Zero);

        Assert.Empty(Model(Build([bare], ViewStyle.Wireframe)));
    }

    [Fact]
    public void ModesMixInOneFrameWithoutInterfering()
    {
        // Fills, then the whole line overlay in instance order, then translucency last.
        string?[] expected = ["solid", "solid.edges", "wire.wire", "glass", "glass.edges"];

        Assert.Equal(expected, Order(Build(
        [
            Instance("solid"),
            Instance("wire", DisplayMode.Wireframe),
            Instance("glass", DisplayMode.Translucent),
        ])));
    }

    [Fact]
    public void EveryProgramNamedIsOneTheViewportCompiles()
    {
        string[] compiled =
        [
            ViewportFrame.MeshProgram, ViewportFrame.LineProgram,
            ViewportFrame.PointProgram, ViewportFrame.BackgroundProgram,
        ];

        foreach (var style in Enum.GetValues<ViewStyle>())
        {
            foreach (var mode in Enum.GetValues<DisplayMode>())
            {
                var frame = Build([Instance("a", mode)], style);
                Assert.All(frame.Draws, call => Assert.Contains(call.Program, compiled));
            }
        }
    }

    [Fact]
    public void NoModelDrawCullsFaces()
    {
        // Culling would look right until the section rung: a clipped solid exposes its
        // interior as BACKfaces, which the shared fragment shader shades as cut material.
        foreach (var style in Enum.GetValues<ViewStyle>())
        {
            foreach (var mode in Enum.GetValues<DisplayMode>())
                Assert.All(Build([Instance("a", mode)], style).Draws, call => Assert.False(call.Cull));
        }
    }

    [Fact]
    public void ADegeneratePixelScaleIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ViewportFrame.Build([], Camera, Bounds, aspect: 1.6, furniture: null,
                ViewStyle.Shaded, pixelScale: 0));
}
