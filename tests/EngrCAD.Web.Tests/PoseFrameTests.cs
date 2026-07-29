using EngrCAD.Core;
using EngrCAD.Modeling;
using EngrCAD.Viewer;
using Xunit;

namespace EngrCAD.Web.Tests;

/// <summary>
/// Exploded views and animation playback in the browser: both are POSES over buffers
/// that are already uploaded, which is why they cost nothing and why picking keeps
/// working. <c>ViewportFrame.PoseByPath</c> is the whole of the rule and is pure, so it
/// is pinned here as values.
/// <para>The rule that matters is matching by occurrence PATH rather than by index. A
/// whole-scene pose track legitimately carries instances the current tab does not draw,
/// and an instance the track says nothing about must keep its DOCUMENT pose — index
/// matching gets both wrong the moment a tab shows a subset, and the symptom is a part
/// wearing its neighbour's transform, which looks like a modelling error.</para>
/// </summary>
public class PoseFrameTests
{
    private static Part Box(string name) => new(name, Shape.Box(2, 2, 2));

    private static PartInstance At(Part part, string path, double z) =>
        new(part, Matrix4d.CreateTranslation(new Vector3d(0, 0, z)), path);

    [Fact]
    public void NoPosesReturnsTheDocumentsOwnMatrices()
    {
        var a = Box("a");
        IReadOnlyList<PartInstance> drawn = [At(a, "rig/a", 1), At(a, "rig/a.2", 2)];

        var world = ViewportFrame.PoseByPath(drawn, null);

        Assert.Equal(drawn.Count, world.Length);
        for (int i = 0; i < drawn.Count; i++)
            Assert.Equal(drawn[i].World, world[i]);
    }

    [Fact]
    public void PosesAreMatchedByPathNotByIndex()
    {
        var a = Box("a");
        var b = Box("b");
        IReadOnlyList<PartInstance> drawn = [At(a, "rig/a", 1), At(b, "rig/b", 2)];
        // Deliberately in the OTHER order, which is what separates path matching from
        // index matching: an index match would swap the two parts' poses.
        IReadOnlyList<PartInstance> posed = [At(b, "rig/b", 20), At(a, "rig/a", 10)];

        var world = ViewportFrame.PoseByPath(drawn, posed);

        Assert.Equal(Matrix4d.CreateTranslation(new Vector3d(0, 0, 10)), world[0]);
        Assert.Equal(Matrix4d.CreateTranslation(new Vector3d(0, 0, 20)), world[1]);
    }

    [Fact]
    public void AnInstanceThePoseTrackSaysNothingAboutKeepsItsDocumentPose()
    {
        var a = Box("a");
        var bystander = Box("fixture");
        IReadOnlyList<PartInstance> drawn = [At(a, "rig/a", 1), At(bystander, "rig/fixture", 3)];
        IReadOnlyList<PartInstance> posed = [At(a, "rig/a", 10)];

        var world = ViewportFrame.PoseByPath(drawn, posed);

        Assert.Equal(Matrix4d.CreateTranslation(new Vector3d(0, 0, 10)), world[0]);
        Assert.Equal(drawn[1].World, world[1]);
    }

    [Fact]
    public void PosesForInstancesThisTabDoesNotDrawAreIgnored()
    {
        var a = Box("a");
        var elsewhere = Box("other");
        IReadOnlyList<PartInstance> drawn = [At(a, "rig/a", 1)];
        IReadOnlyList<PartInstance> posed = [At(a, "rig/a", 10), At(elsewhere, "second-tab/other", 99)];

        var world = ViewportFrame.PoseByPath(drawn, posed);

        Assert.Single(world);
        Assert.Equal(Matrix4d.CreateTranslation(new Vector3d(0, 0, 10)), world[0]);
    }

    [Fact]
    public void AnExplodeTrackAndTheScalarFactorAgreeThroughThisSeam()
    {
        // The two front-end affordances (a slider and an animation) reach the same
        // flatten walk, so at factor 1 they must produce the same matrices — otherwise
        // the transport and the slider would be two different exploded views.
        var scene = new Scene();
        var body = new Part("body", Shape.Box(6, 4, 2));
        var lid = new Part("lid", Shape.Box(6, 4, 1).Translate(0, 0, 2));
        var stack = new Assembly("stack");
        stack.Add(body);
        stack.Add(lid).ExplodeOffset = new Vector3d(0, 0, 8);
        scene.AddTab("stack").Add(stack);

        var drawn = scene.Instances().ToList();
        var slider = ViewportFrame.PoseByPath(drawn, [.. scene.Tabs[0].Instances(1)]);
        var track = ViewportFrame.PoseByPath(
            drawn, new Animation(durationSeconds: 1).With(new ExplodeTrack(scene)).At(1).Instances);

        Assert.Equal(slider, track);
        // ... and factor 0 is the document, bit for bit (the exploded view's own rule).
        var assembled = ViewportFrame.PoseByPath(drawn, [.. scene.Tabs[0].Instances(0)]);
        for (int i = 0; i < drawn.Count; i++)
            Assert.Equal(drawn[i].World, assembled[i]);
        Assert.NotEqual(slider[1], assembled[1]);
    }

    [Fact]
    public void DebugModifiersRemoveInstancesBeforeAnythingIsPosed()
    {
        // The viewport resolves its instances through DebugFilter.Shown, so a Hidden
        // part never reaches the pose seam at all — it must not even influence framing.
        var scene = new Scene();
        var shown = new Part("shown", Shape.Box(2, 2, 2));
        var hidden = new Part("hidden", Shape.Box(2, 2, 2)) { Hidden = true };
        scene.Add(shown);
        scene.Add(hidden);

        var visible = DebugFilter.Shown([.. scene.Tabs[0].Instances()]);
        Assert.Single(visible);
        Assert.Equal("shown", visible[0].Part.Name);

        // With no flags set the filter is the identity, which is why it could be added
        // to the browser front end without moving a pixel.
        hidden.Hidden = false;
        Assert.Equal(2, DebugFilter.Shown([.. scene.Tabs[0].Instances()]).Count);
    }
}
