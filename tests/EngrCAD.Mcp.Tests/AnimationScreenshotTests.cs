using EngrCAD.Core;
using EngrCAD.Modeling;
using EngrCAD.Viewer;
using ModelContextProtocol.Protocol;
using Xunit;

namespace EngrCAD.Mcp.Tests;

/// <summary>
/// The <c>screenshot</c> tool's <c>t</c> parameter — "show me the mechanism at t = 0.3".
/// The posing goes through <c>EngrCad.PoseAt</c>, the same seam the still overload and
/// every export use, so this file's job is the tool's own contract: the range check, the
/// honest refusal when the model declares no timeline, the scope interaction, and that
/// two different instants genuinely render differently.
/// </summary>
public class AnimationScreenshotValidationTests
{
    private static Scene Assembled()
    {
        var scene = new Scene(TestScenes.Coarse);
        var body = new Part("body", Shape.Box(10, 8, 3));
        var lid = new Part("lid", Shape.Box(10, 8, 2).Translate(0, 0, 3));
        var stack = new Assembly("stack");
        stack.Add(body);
        stack.Add(lid).ExplodeOffset = new Vector3d(0, 0, 12);
        scene.AddTab("stack").Add(stack);
        return scene;
    }

    private static SceneTools Animated() =>
        new(new SceneSession(Assembled(), TestScenes.Coarse,
            scene => new Animation(durationSeconds: 1).With(new ExplodeTrack(scene))));

    private static string ErrorText(CallToolResult result)
    {
        Assert.True(result.IsError == true, "expected an error result");
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
    }

    [Fact]
    public void A_model_with_no_animation_says_so_and_names_the_builder_call()
    {
        var tools = new SceneTools(new SceneSession(TestScenes.Basic()));
        string error = ErrorText(tools.Screenshot(t: 0.5));
        Assert.Contains("declares no animation", error);
        Assert.Contains("WithAnimation", error);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void T_must_be_a_timeline_fraction(double t)
    {
        Assert.Contains("between 0 and 1", ErrorText(Animated().Screenshot(t: t)));
    }

    [Fact]
    public void The_animation_is_built_lazily_and_only_when_t_is_asked_for()
    {
        int built = 0;
        var session = new SceneSession(Assembled(), TestScenes.Coarse,
            scene => { built++; return new Animation(durationSeconds: 1).With(new ExplodeTrack(scene)); });
        var tools = new SceneTools(session);

        tools.ListParts();                       // the listing tools evaluate nothing
        Assert.Equal(0, built);
        _ = session.Animation;
        Assert.Equal(1, built);
        _ = session.Animation;                   // and it is built ONCE
        Assert.Equal(1, built);
    }
}

/// <inheritdoc cref="AnimationScreenshotValidationTests"/>
[Collection("offscreen-gl")]
public class AnimationScreenshotRenderTests
{
    private static Scene Assembled()
    {
        var scene = new Scene(TestScenes.Coarse);
        var body = new Part("body", Shape.Box(10, 8, 3));
        var lid = new Part("lid", Shape.Box(10, 8, 2).Translate(0, 0, 3));
        var stack = new Assembly("stack");
        stack.Add(body);
        stack.Add(lid).ExplodeOffset = new Vector3d(0, 0, 12);
        scene.AddTab("stack").Add(stack);
        return scene;
    }

    private static SceneTools Animated(Scene scene) =>
        new(new SceneSession(scene, TestScenes.Coarse,
            s => new Animation(durationSeconds: 1).With(new ExplodeTrack(s))));

    private static byte[] Png(CallToolResult result)
    {
        Assert.False(result.IsError == true,
            $"expected an image, got: {string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text))}");
        return Assert.Single(result.Content.OfType<ImageContentBlock>()).Data.ToArray();
    }

    [SkippableFact]
    public void Two_instants_render_differently_and_t_zero_matches_the_untimed_shot()
    {
        Skip.IfNot(EngrCad.CanRenderToImage,
            $"offscreen GL unavailable: {OffscreenRenderer.UnavailableReason}");
        var tools = Animated(Assembled());

        // A named view keeps the camera fixed, so any difference IS the pose. Explode
        // factor exactly 0 leaves the flatten bit-identical, which is what makes the
        // untimed shot the right reference.
        byte[] untimed = Png(tools.Screenshot(view: "front", width: 200, height: 160));
        byte[] start = Png(tools.Screenshot(view: "front", width: 200, height: 160, t: 0));
        byte[] end = Png(tools.Screenshot(view: "front", width: 200, height: 160, t: 1));

        Assert.Equal(untimed, start);
        Assert.NotEqual(start, end);
    }

    [SkippableFact]
    public void A_deformation_track_reaches_the_render()
    {
        Skip.IfNot(EngrCad.CanRenderToImage,
            $"offscreen GL unavailable: {OffscreenRenderer.UnavailableReason}");

        // A deformation track is the one kind of track that does NOT travel as poses — it
        // is a scalar the render pass applies as a uniform — so posing the instances is
        // not enough and a still at t would otherwise silently show the wrong
        // exaggeration. Two instants of a load ramp must differ.
        var scene = new Scene(TestScenes.Coarse);
        var plate = new Part("plate", Shape.Box(20, 8, 2));
        scene.Add(plate);
        scene.PreMesh();
        var mesh = plate.GetMesh();
        plate.AddResult(Mesh.MeshField.SampleVector(mesh, "u", "mm",
            p => new Vector3d(0, 0, 0.05 * (p.X + 10) * (p.X + 10))));
        plate.FieldDisplay = new FieldDisplay { Field = "u", Deform = "u", DeformScale = 4 };

        var tools = new SceneTools(new SceneSession(scene, TestScenes.Coarse,
            _ => new Animation(durationSeconds: 1).With(DeformationTracks.LoadRamp())));

        byte[] flat = Png(tools.Screenshot(view: "front", width: 200, height: 160, t: 0));
        byte[] peak = Png(tools.Screenshot(view: "front", width: 200, height: 160, t: 0.5));
        Assert.NotEqual(flat, peak);
    }

    [SkippableFact]
    public void A_part_scope_still_narrows_a_posed_render()
    {
        Skip.IfNot(EngrCad.CanRenderToImage,
            $"offscreen GL unavailable: {OffscreenRenderer.UnavailableReason}");
        var tools = Animated(Assembled());

        byte[] whole = Png(tools.Screenshot(view: "front", width: 200, height: 160, t: 0.6));
        byte[] lidOnly = Png(tools.Screenshot(view: "front", width: 200, height: 160, t: 0.6, part: "lid"));
        Assert.NotEqual(whole, lidOnly);
    }
}
