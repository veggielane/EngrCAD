using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mcp.Tests;

/// <summary>
/// The named camera poses. <see cref="StandardViews"/> mirrors the viewer's internal
/// <c>ViewCubeMath.PoseFor</c> / <c>CameraMath.FrameDistance</c>, so these assertions
/// are against the values documented there — if the viewer's convention ever changes,
/// these fail rather than the screenshots quietly drifting.
/// </summary>
public class StandardViewsTests
{
    [Fact]
    public void Front_right_and_iso_reproduce_the_toolbar_poses()
    {
        var (frontYaw, frontPitch) = StandardViews.PoseFor(StandardViews.DirectionFor("front")!.Value);
        Assert.Equal(-Math.PI / 2, frontYaw, 12);
        Assert.Equal(0, frontPitch, 12);

        var (rightYaw, rightPitch) = StandardViews.PoseFor(StandardViews.DirectionFor("right")!.Value);
        Assert.Equal(0, rightYaw, 12);
        Assert.Equal(0, rightPitch, 12);

        // The toolbar's Iso is the front-right-top corner (1, -1, 1).
        var (isoYaw, isoPitch) = StandardViews.PoseFor(StandardViews.DirectionFor("iso")!.Value);
        Assert.Equal(-Math.PI / 4, isoYaw, 12);
        Assert.Equal(Math.Asin(1 / Math.Sqrt(3)), isoPitch, 12);
    }

    [Fact]
    public void Top_and_bottom_clamp_the_pitch_and_take_the_pole_yaw()
    {
        var (topYaw, topPitch) = StandardViews.PoseFor(StandardViews.DirectionFor("top")!.Value);
        Assert.Equal(StandardViews.PoleYaw, topYaw, 12);
        Assert.Equal(StandardViews.PitchLimit, topPitch, 12);

        var (_, bottomPitch) = StandardViews.PoseFor(StandardViews.DirectionFor("bottom")!.Value);
        Assert.Equal(-StandardViews.PitchLimit, bottomPitch, 12);
    }

    [Fact]
    public void An_unknown_view_name_is_rejected()
    {
        Assert.Null(StandardViews.DirectionFor("sideways"));
        Assert.Throws<ArgumentException>(() => StandardViews.For("sideways", [], null));
    }

    [Fact]
    public void The_camera_frames_the_instances_it_is_given()
    {
        var scene = TestScenes.Basic();
        var instances = scene.AllInstances.ToList();
        var bounds = instances.Aggregate(Aabb.Empty, (b, i) => b.Union(i.Bounds(TestScenes.Coarse)));

        var camera = StandardViews.For("front", instances, TestScenes.Coarse);

        Assert.NotNull(camera);
        Assert.Equal(bounds.Center.X, camera.Target.X, 9);
        Assert.Equal(StandardViews.FrameDistance(bounds), camera.Distance, 9);
        Assert.True(camera.Distance > bounds.Size.Length, "the framing distance must clear the scene");
    }

    [Fact]
    public void The_default_view_leaves_framing_to_the_renderer()
    {
        Assert.Null(StandardViews.For("default", [], null));
    }
}
