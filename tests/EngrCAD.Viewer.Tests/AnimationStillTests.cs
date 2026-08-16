using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// Two halves of the same claim, that there is ONE answer to "what does the model look
/// like at t":
/// <list type="bullet">
/// <item><see cref="OffscreenRenderer.RenderSequence"/> — the batched export path that
/// holds one EGL context, one set of programs and one set of uploaded buffers across
/// every frame — must be <b>byte-identical</b> to calling the single-frame
/// <see cref="OffscreenRenderer.Render(IReadOnlyList{PartInstance}, int, int, CameraState?, bool, ViewStyle, SectionAxis, double?, bool, IReadOnlyList{SectionPlane}?, SectionCombine, IReadOnlyList{ValueTuple{Core.Vector3d, Core.Vector3d}}?, Core.Matrix4d?, bool)"/>
/// once per frame. A "several times faster" claim about a render path is worthless
/// without the pixel oracle beside it.</item>
/// <item><c>EngrCad.RenderToImage(scene, animation, t, ...)</c> — a still of one instant
/// — must equal frame ⌊t·N⌋ of the export, because both evaluate the same pure
/// <c>Animation.At(t)</c>.</item>
/// </list>
/// </summary>
[Collection("offscreen-gl")]
public class AnimationStillTests
{
    private static string? SkipReason =>
        OffscreenRenderer.IsAvailable ? null
        : $"no offscreen GL context on this machine: {OffscreenRenderer.UnavailableReason}";

    /// <summary>A two-part assembly with explode offsets, so poses genuinely move.</summary>
    private static Scene ExplodableScene()
    {
        var scene = new Scene();
        var body = new Part("body", Shape.Box(6, 4, 2));
        var lid = new Part("lid", Shape.Box(6, 4, 1).Translate(0, 0, 2));
        var assembly = new Assembly("stack");
        assembly.Add(body);
        assembly.Add(lid).ExplodeOffset = new Core.Vector3d(0, 0, 6);
        scene.AddTab("stack").Add(assembly);
        return scene;
    }

    [SkippableFact]
    public void BatchedSequenceIsByteIdenticalToOneRenderPerFrame()
    {
        Skip.If(SkipReason is not null, SkipReason);

        var scene = ExplodableScene();
        scene.PreMesh();
        var camera = CameraMath.DefaultCamera(scene.Instances().Select(i => i.Bounds())
            .Aggregate(Core.Aabb.Empty, (a, b) => a.Union(b)));

        // Three genuinely different poses of the SAME parts — the animation contract.
        var frames =
            new List<(IReadOnlyList<PartInstance> Instances, CameraState Camera, double DeformFactor)>();
        foreach (double factor in new[] { 0.0, 0.5, 1.0 })
            frames.Add(([.. scene.Instances(factor)], camera, 1.0));

        var batched = OffscreenRenderer.RenderSequence(frames, 128, 96);
        Assert.Equal(3, batched.Count);
        for (int i = 0; i < frames.Count; i++)
        {
            var single = OffscreenRenderer.Render(frames[i].Instances, 128, 96, frames[i].Camera);
            Assert.Equal(Convert.ToHexString(single), Convert.ToHexString(batched[i]));
        }

        // ... and the frames are not all the same picture, or the comparison proves nothing.
        Assert.NotEqual(Convert.ToHexString(batched[0]), Convert.ToHexString(batched[2]));
    }

    [SkippableFact]
    public void EmptySequenceRendersNothingRatherThanCreatingAContext()
    {
        Skip.If(SkipReason is not null, SkipReason);
        // Explicitly typed: the per-frame-sections overload makes a bare [] ambiguous.
        IReadOnlyList<(IReadOnlyList<PartInstance> Instances, CameraState Camera, double DeformFactor)>
            empty = [];
        Assert.Empty(OffscreenRenderer.RenderSequence(empty, 64, 64));
    }

    [SkippableFact]
    public void StillAtTMatchesTheSameFrameOfTheExport()
    {
        Skip.If(SkipReason is not null, SkipReason);

        var scene = ExplodableScene();
        var animation = new Animation(durationSeconds: 1).With(new ExplodeTrack(scene));

        string directory = Path.Combine(Path.GetTempPath(), $"engrcad-{Guid.NewGuid():N}");
        string still = Path.Combine(Path.GetTempPath(), $"engrcad-{Guid.NewGuid():N}.png");
        try
        {
            // A 4-frame loop samples t = 0, 0.25, 0.5, 0.75; the still asks for 0.5.
            var paths = animation.RenderFrames(scene, directory, frames: 4, width: 128, height: 96);
            EngrCad.RenderToImage(scene, animation, 0.5, still, 128, 96);
            Assert.Equal(
                Convert.ToHexString(File.ReadAllBytes(paths[2])),
                Convert.ToHexString(File.ReadAllBytes(still)));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
            File.Delete(still);
        }
    }

    [Fact]
    public void PoseAtIsTheSharedPosingSeamAndNeedsNoGl()
    {
        var scene = ExplodableScene();
        var animation = new Animation(durationSeconds: 1).With(new ExplodeTrack(scene));

        var assembled = EngrCad.PoseAt(scene, animation, 0);
        var exploded = EngrCad.PoseAt(scene, animation, 1);

        // The instance COUNT and ORDER never depend on t (that is what lets the viewport
        // animate with matrices alone), only the poses.
        Assert.Equal(assembled.Count, exploded.Count);
        Assert.Equal(assembled.Select(i => i.Path), exploded.Select(i => i.Path));
        Assert.NotEqual(assembled[1].World, exploded[1].World);

        // Factor exactly 0 leaves the flatten bit-identical (the exploded-view rule).
        var plain = scene.Instances().ToList();
        for (int i = 0; i < plain.Count; i++)
            Assert.Equal(plain[i].World, assembled[i].World);
    }

    [Fact]
    public void PoseAtHonorsDebugModifiers()
    {
        var scene = ExplodableScene();
        var animation = new Animation(durationSeconds: 1).With(new ExplodeTrack(scene));
        int all = EngrCad.PoseAt(scene, animation, 0.4).Count;

        scene.AllParts.First(p => p.Name == "lid").Hidden = true;
        Assert.Equal(all - 1, EngrCad.PoseAt(scene, animation, 0.4).Count);
    }

    // ---- transient playback stills ----

    /// <summary>The field-sequence verification bar: a still of the animation at a
    /// step is BYTE-IDENTICAL to a static render of the same scene whose part displays
    /// that step's field over the run's one range explicitly — both roads reach one
    /// configuration, which is what makes the track a selection rather than a second
    /// rendering path.</summary>
    [SkippableFact]
    public void AFieldSequenceStillEqualsTheStaticRenderOfTheSameStep()
    {
        Skip.If(SkipReason is not null, SkipReason);

        Scene SceneWith(Action<Part>? display = null)
        {
            var scene = new Scene();
            var part = new Part("plate", Shape.Box(20, 10, 2));
            var mesh = part.GetMesh();
            part.AddResult(Mesh.MeshField.Sample(mesh, "T@0", "K", p => 300 + p.X));
            part.AddResult(Mesh.MeshField.Sample(mesh, "T@5", "K", p => 300 + 4 * p.X));
            part.FieldDisplay = new FieldDisplay { Field = "T@0" };
            display?.Invoke(part);
            scene.Add(part);
            return scene;
        }

        var track = new FieldSequenceTrack([("T@0", 0), ("T@5", 5)]);
        var animated = SceneWith();
        var animation = new Animation(durationSeconds: 2).With(track);

        string dir = Path.Combine(Path.GetTempPath(), "engrcad-fieldseq-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            // t = 1: the last step. The static twin states the step field and the RUN
            // range (the union of both steps' own ranges) explicitly.
            string stillPath = Path.Combine(dir, "still.png");
            EngrCad.RenderToImage(animated, animation, t: 1, stillPath, width: 320, height: 240);

            var part = animated.Tabs[0].Parts[0];
            var runRange = track.RunRange(part);
            var staticScene = SceneWith(p => p.FieldDisplay = new FieldDisplay
            {
                Field = "T@5",
                Range = runRange,
            });
            string staticPath = Path.Combine(dir, "static.png");
            EngrCad.RenderToImage(staticScene, staticPath, width: 320, height: 240);

            Assert.Equal(File.ReadAllBytes(staticPath), File.ReadAllBytes(stillPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The batched export's transient playback: a sequence whose frames select DIFFERENT
    /// steps must be byte-identical to one fresh <c>Render</c> per frame with the same
    /// selection. The batch's second frame is the case with teeth — a warm cache whose
    /// colour buffers show the previous step, so only the colours-only re-upload path
    /// can make it match the fresh render.
    /// </summary>
    [SkippableFact]
    public void ABatchedFieldSequenceIsByteIdenticalToOneRenderPerFrame()
    {
        Skip.If(SkipReason is not null, SkipReason);

        var scene = new Scene();
        var part = new Part("plate", Shape.Box(20, 10, 2));
        var mesh = part.GetMesh();
        part.AddResult(Mesh.MeshField.Sample(mesh, "T@0", "K", p => 300 + p.X));
        part.AddResult(Mesh.MeshField.Sample(mesh, "T@5", "K", p => 300 + 4 * p.X));
        part.FieldDisplay = new FieldDisplay { Field = "T@0" };
        scene.Add(part);
        scene.PreMesh();

        var instances = (IReadOnlyList<PartInstance>)[.. scene.Instances()];
        var camera = CameraMath.DefaultCamera(instances.Select(i => i.Bounds())
            .Aggregate(Core.Aabb.Empty, (a, b) => a.Union(b)));
        var track = new FieldSequenceTrack([("T@0", 0), ("T@5", 5)]);

        // Three frames: step 0, step 1, and step 0 AGAIN — the third frame proves the
        // re-upload is not one-way (the cache must come BACK from the later step too).
        string[] steps = ["T@0", "T@5", "T@0"];
        var frames = new List<(IReadOnlyList<PartInstance> Instances, CameraState Camera,
            double DeformFactor, IReadOnlyList<SectionPlane>? Sections)>();
        var fieldSteps = new List<(FieldSequenceTrack Track, string FieldName)?>();
        foreach (var step in steps)
        {
            frames.Add((instances, camera, 1.0, null));
            fieldSteps.Add((track, step));
        }

        var batched = OffscreenRenderer.RenderSequence(frames, 128, 96, fieldSteps: fieldSteps);
        Assert.Equal(3, batched.Count);
        for (int i = 0; i < steps.Length; i++)
        {
            var single = OffscreenRenderer.Render(instances, 128, 96, camera,
                fieldStep: (track, steps[i]));
            Assert.Equal(Convert.ToHexString(single), Convert.ToHexString(batched[i]));
        }

        // ... and the two steps are genuinely different pictures, or the comparison
        // proves nothing about the re-upload.
        Assert.NotEqual(Convert.ToHexString(batched[0]), Convert.ToHexString(batched[1]));
        // Frame 2's warm-cache return to step 0 equals frame 0's picture exactly.
        Assert.Equal(Convert.ToHexString(batched[0]), Convert.ToHexString(batched[2]));
    }

    [SkippableFact]
    public void MismatchedFieldStepCountIsRefusedByName()
    {
        Skip.If(SkipReason is not null, SkipReason);

        var scene = ExplodableScene();
        scene.PreMesh();
        var camera = CameraMath.DefaultCamera(scene.Instances().Select(i => i.Bounds())
            .Aggregate(Core.Aabb.Empty, (a, b) => a.Union(b)));
        var frames = new List<(IReadOnlyList<PartInstance> Instances, CameraState Camera,
            double DeformFactor, IReadOnlyList<SectionPlane>? Sections)>
        {
            ([.. scene.Instances()], camera, 1.0, null),
            ([.. scene.Instances()], camera, 1.0, null),
        };
        var oneStep = new List<(FieldSequenceTrack Track, string FieldName)?> { null };
        var ex = Assert.Throws<ArgumentException>(() =>
            OffscreenRenderer.RenderSequence(frames, 64, 64, fieldSteps: oneStep));
        Assert.Contains("parallel to frames", ex.Message);
    }
}
