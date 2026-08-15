using EngrCAD.Core;
using EngrCAD.Modeling;
using EngrCAD.Viewer;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The follow-a-path pose track: arc-length parameterization (not waypoint index), exact
/// endpoints, bystanders untouched bit-for-bit, and the pose-track contract (count and
/// order never change).
/// </summary>
public class PathTrackTests
{
    private static Scene SceneWith(out Part tool)
    {
        var scene = new Scene();
        scene.Add(new Part("block", Shape.Box(10, 10, 10)));
        tool = new Part("tool", Shape.Cylinder(1, 8));
        scene.Add(tool);
        return scene;
    }

    [Fact]
    public void FollowsByArcLength_NotByWaypointIndex()
    {
        var scene = SceneWith(out _);
        // Segments of length 10 then 5: the halfway point by ARC LENGTH is (7.5, 0, 0) —
        // index interpolation would put it at the corner.
        var track = PathTracks.Follow(scene, "tool",
            [new Vector3d(0, 0, 0), new Vector3d(10, 0, 0), new Vector3d(10, 5, 0)]);

        Assert.Equal(new Vector3d(0, 0, 0), track.PointAt(0));
        Assert.Equal(new Vector3d(10, 5, 0), track.PointAt(1));
        var mid = track.PointAt(0.5);
        Assert.Equal(7.5, mid.X, 9);
        Assert.Equal(0, mid.Y, 12);
    }

    [Fact]
    public void PosesTheTargetAndLeavesBystandersBitIdentical()
    {
        var scene = SceneWith(out _);
        var template = scene.Instances().ToList();
        var track = PathTracks.Follow(scene, "tool",
            [new Vector3d(2, 3, 4), new Vector3d(6, 3, 4)]);

        var posed = track.PosesAt(0);
        Assert.Equal(template.Count, posed.Count);
        for (int i = 0; i < posed.Count; i++)
        {
            Assert.Equal(template[i].Path, posed[i].Path);
            if (posed[i].Path != "tool")
                Assert.Equal(template[i].World, posed[i].World);
        }
        var tool0 = posed.Single(p => p.Path == "tool");
        Assert.Equal(
            Matrix4d.CreateTranslation(new Vector3d(2, 3, 4)) * template.Single(p => p.Path == "tool").World,
            tool0.World);

        // Clamp semantics: past the ends the pose holds the boundary value.
        Assert.Equal(track.PosesAt(1).Single(p => p.Path == "tool").World,
            track.PosesAt(2).Single(p => p.Path == "tool").World);
    }

    [Fact]
    public void RidesTheAnimationTimeline()
    {
        var scene = SceneWith(out _);
        var track = PathTracks.Follow(scene, "tool",
            [new Vector3d(0, 0, 0), new Vector3d(8, 0, 0)]);
        var animation = new Animation(durationSeconds: 2).With(track);

        var sample = animation.At(0.5);
        Assert.NotNull(sample.Instances);
        var tool = sample.Instances!.Single(p => p.Path == "tool");
        Assert.Equal(4, tool.World.M14, 9); // translation x at the arc-length midpoint
    }

    [Fact]
    public void AWrongPath_FailsAtConstruction_NamingWhatExists()
    {
        var scene = SceneWith(out _);
        var e = Assert.Throws<ArgumentException>(() =>
            PathTracks.Follow(scene, "cutter", [new Vector3d(0, 0, 0)]));
        Assert.Contains("cutter", e.Message);
        Assert.Contains("tool", e.Message);
    }
}
