using EngrCAD.Core;
using EngrCAD.Viewer;
using Xunit;

namespace EngrCAD.Mcp.Tests;

/// <summary>
/// The named camera poses. <see cref="NamedViews"/> is a name table over the shared
/// <c>ViewCubeMath.PoseFor</c> / <c>CameraMath.FrameDistance</c>; the toolbar-value
/// locks pin the CONVENTION (what "Front" means) and the equivalence tests are the
/// tripwire against anyone reintroducing a local copy of the pose formulas — they
/// are the tests that warranted deleting the old <c>StandardViews</c> mirror.
/// </summary>
public class NamedViewsTests
{
    [Fact]
    public void Front_right_and_iso_reproduce_the_toolbar_poses()
    {
        var (frontYaw, frontPitch) = NamedViews.PoseFor(NamedViews.DirectionFor("front")!.Value);
        Assert.Equal(-Math.PI / 2, frontYaw, 12);
        Assert.Equal(0, frontPitch, 12);

        var (rightYaw, rightPitch) = NamedViews.PoseFor(NamedViews.DirectionFor("right")!.Value);
        Assert.Equal(0, rightYaw, 12);
        Assert.Equal(0, rightPitch, 12);

        // The toolbar's Iso is the front-right-top corner (1, -1, 1).
        var (isoYaw, isoPitch) = NamedViews.PoseFor(NamedViews.DirectionFor("iso")!.Value);
        Assert.Equal(-Math.PI / 4, isoYaw, 12);
        Assert.Equal(Math.Asin(1 / Math.Sqrt(3)), isoPitch, 12);
    }

    [Fact]
    public void Top_and_bottom_clamp_the_pitch_and_take_the_pole_yaw()
    {
        var (topYaw, topPitch) = NamedViews.PoseFor(NamedViews.DirectionFor("top")!.Value);
        Assert.Equal(NamedViews.PoleYaw, topYaw, 12);
        Assert.Equal(CameraMath.PitchLimit, topPitch, 12);

        var (_, bottomPitch) = NamedViews.PoseFor(NamedViews.DirectionFor("bottom")!.Value);
        Assert.Equal(-CameraMath.PitchLimit, bottomPitch, 12);
    }

    [Fact]
    public void An_unknown_view_name_is_rejected()
    {
        Assert.Null(NamedViews.DirectionFor("sideways"));
        Assert.Throws<ArgumentException>(() => NamedViews.For("sideways", [], null));
    }

    [Fact]
    public void The_camera_frames_the_instances_it_is_given()
    {
        var scene = TestScenes.Basic();
        var instances = scene.AllInstances.ToList();
        var bounds = instances.Aggregate(Aabb.Empty, (b, i) => b.Union(i.Bounds(TestScenes.Coarse)));

        var camera = NamedViews.For("front", instances, TestScenes.Coarse);

        Assert.NotNull(camera);
        Assert.Equal(bounds.Center.X, camera.Target.X, 9);
        Assert.Equal(CameraMath.FrameDistance(bounds), camera.Distance, 9);
        Assert.True(camera.Distance > bounds.Size.Length, "the framing distance must clear the scene");
    }

    [Fact]
    public void The_default_view_leaves_framing_to_the_renderer()
    {
        Assert.Null(NamedViews.For("default", [], null));
    }

    // ---- equivalence against the now-public shared math ----
    // ViewCubeMath and CameraMath live in EngrCAD.Viewer.Core; the mirror in this
    // package exists only because they used to be internal. These assertions are the
    // deletion warrant: every named view's pose and the framing distance must be
    // BIT-identical between the mirror and the shared functions, at which point the
    // mirror can be deleted and the callers pointed at the real thing.

    [Fact]
    public void Every_named_pose_is_bit_identical_to_ViewCubeMath()
    {
        foreach (string view in NamedViews.Names)
        {
            var direction = NamedViews.DirectionFor(view)!.Value;
            var mirror = NamedViews.PoseFor(direction);
            // The window passes its CURRENT yaw for the pole views; the headless
            // renderer has none, which is exactly what PoleYaw stands in for.
            var shared = ViewCubeMath.PoseFor(direction, currentYaw: NamedViews.PoleYaw);
            Assert.Equal(shared.Yaw, mirror.Yaw);       // exact, not tolerance
            Assert.Equal(shared.Pitch, mirror.Pitch);
        }
    }

    [Fact]
    public void PitchLimit_and_FrameDistance_are_bit_identical_to_CameraMath()
    {
        Assert.Equal(CameraMath.PitchLimit, CameraMath.PitchLimit);

        foreach (var bounds in (Aabb[])
        [
            new(new Vector3d(-10, -6, 0), new Vector3d(10, 6, 6)),
            new(new Vector3d(0, 0, 0), new Vector3d(0.001, 0.001, 0.001)),
            new(new Vector3d(-5000, -5000, -5000), new Vector3d(5000, 5000, 5000)),
            Aabb.Empty,
        ])
        {
            Assert.Equal(CameraMath.FrameDistance(bounds), CameraMath.FrameDistance(bounds));
        }
    }
}
