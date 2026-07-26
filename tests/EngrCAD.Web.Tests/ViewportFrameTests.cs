using EngrCAD.Core;
using EngrCAD.Modeling;
using EngrCAD.Viewer;
using EngrCAD.Web;
using Xunit;

namespace EngrCAD.Web.Tests;

/// <summary>
/// What the browser will draw, asserted as a value. The desktop window and the offscreen
/// renderer can only be checked by looking at pixels, which is how they once drifted;
/// the web client's frame is a pure function of the scene and the camera, so it can be
/// pinned directly.
/// </summary>
public class ViewportFrameTests
{
    private static readonly Aabb Bounds = new((-10, -10, 0), (10, 10, 5));

    private static CameraState Camera => ViewportFrame.DefaultCamera(Bounds);

    private static ViewportInstance Instance(string key, in Vector3d at) =>
        new(key, Matrix4d.CreateTranslation(at), Palette.Brass, at);

    private static FrameDescription Build(
        IReadOnlyList<ViewportInstance> instances, ViewportFurniture? furniture = null) =>
        ViewportFrame.Build(instances, Camera, Bounds, aspect: 1.6, furniture);

    [Fact]
    public void EmptySceneStillDrawsTheBackground()
    {
        var frame = Build([]);

        var background = Assert.Single(frame.Draws);
        Assert.Equal(ViewportFrame.BackgroundProgram, background.Program);
        // Vertexless: the shader builds a fullscreen triangle from gl_VertexID, so there
        // is no buffer to bind and the JS must take the count from here.
        Assert.Null(background.Geometry);
        Assert.Equal(3, background.Count);
        Assert.False(background.DepthTest);
        Assert.False(background.DepthWrite);
    }

    [Fact]
    public void ClearColorMatchesTheDesktopViewport()
    {
        // ViewportControl.OnOpenGlRender and OffscreenRenderer.Draw both clear to this
        // before drawing the gradient over it.
        Assert.Equal([0.11f, 0.12f, 0.14f], Build([]).Clear);
    }

    [Fact]
    public void FurnitureDrawsTheGridThenTheThreeAxes()
    {
        var frame = Build([], new ViewportFurniture("@grid", 300, "@axes"));

        Assert.Equal(5, frame.Draws.Count);
        var grid = frame.Draws[1];
        Assert.Equal(ViewportFrame.LineProgram, grid.Program);
        Assert.Equal("@grid", grid.Geometry);
        Assert.Equal(0, grid.First);
        Assert.Equal(300, grid.Count);

        // The three axes are consecutive vertex pairs in ONE upload, drawn as three
        // ranges so each gets its own colour.
        for (int axis = 0; axis < 3; axis++)
        {
            var call = frame.Draws[2 + axis];
            Assert.Equal("@axes", call.Geometry);
            Assert.Equal(axis * 2, call.First);
            Assert.Equal(2, call.Count);
        }
        Assert.Equal([0.75f, 0.30f, 0.30f], (float[])frame.Draws[2].Uniforms!["uColor"]);
        Assert.Equal([0.33f, 0.66f, 0.33f], (float[])frame.Draws[3].Uniforms!["uColor"]);
        Assert.Equal([0.35f, 0.48f, 0.85f], (float[])frame.Draws[4].Uniforms!["uColor"]);
    }

    [Fact]
    public void NoFurnitureLeavesOnlyTheBackgroundAndTheFills()
    {
        var frame = Build([Instance("a", (0, 0, 0))]);

        Assert.Equal(2, frame.Draws.Count);
        Assert.Equal(ViewportFrame.MeshProgram, frame.Draws[1].Program);
    }

    [Fact]
    public void EachInstanceDrawsItsOwnMatrixAndColor()
    {
        var frame = Build([Instance("a", (1, 2, 3)), Instance("b", (-4, 0, 0))]);

        var first = frame.Draws[1];
        Assert.Equal("a", first.Geometry);
        Assert.Equal([Palette.Brass.R, Palette.Brass.G, Palette.Brass.B], (float[])first.Uniforms!["uColor"]);

        var expected = new float[16];
        CameraMath.WriteColumnMajor(Matrix4d.CreateTranslation((1, 2, 3)), expected);
        Assert.Equal(expected, (float[])first.Uniforms["uModel"]);

        Assert.Equal("b", frame.Draws[2].Geometry);
    }

    [Fact]
    public void InstancesOfOneMeshShareItsGeometryKey()
    {
        // The whole point of the instance model: N occurrences, one upload, N matrices.
        var frame = Build([Instance("bolt", (0, 0, 0)), Instance("bolt", (10, 0, 0))]);

        Assert.Equal("bolt", frame.Draws[1].Geometry);
        Assert.Equal("bolt", frame.Draws[2].Geometry);
        Assert.NotEqual(frame.Draws[1].Uniforms!["uModel"], frame.Draws[2].Uniforms!["uModel"]);
    }

    [Fact]
    public void FillsDoNotCull()
    {
        // Both desktop passes leave face culling OFF, and it is load-bearing rather than
        // incidental: a section plane exposes a solid's interior as BACKfaces, which the
        // shared fragment shader shades as cut material via gl_FrontFacing. Culling here
        // would look fine today and silently break that rung.
        Assert.False(Build([Instance("a", (0, 0, 0))]).Draws[1].Cull);
    }

    [Fact]
    public void FillsArePushedBackForAnEdgeOverlay()
    {
        Assert.Equal([1f, 1f], Build([Instance("a", (0, 0, 0))]).Draws[1].PolygonOffset!);
    }

    [Fact]
    public void CameraUniformsComeFromTheSharedCameraMath()
    {
        var camera = Camera;
        var frame = ViewportFrame.Build([], camera, Bounds, aspect: 1.6);

        var eye = CameraMath.Eye(camera.Yaw, camera.Pitch, camera.Distance, camera.Target);
        var (near, far) = CameraMath.FrustumPlanes(camera.Distance, Bounds);

        var view = new float[16];
        CameraMath.WriteColumnMajor(CameraMath.LookAt(eye, camera.Target, Vector3d.UnitZ), view);
        var projection = new float[16];
        CameraMath.WriteColumnMajor(CameraMath.Perspective(Math.PI / 4, 1.6, near, far), projection);

        Assert.Equal(view, (float[])frame.Shared!["uView"]);
        Assert.Equal(projection, (float[])frame.Shared["uProj"]);
        Assert.Equal([(float)eye.X, (float)eye.Y, (float)eye.Z], (float[])frame.Shared["uEyePos"]);
    }

    [Fact]
    public void FrustumScalesWithTheScene()
    {
        // The scene-scaled frustum is why a large model frames fully; a fixed 0.1/200
        // pair cropped anything wider than about 100 units in both desktop passes.
        var far = new Aabb((-500, -500, -500), (500, 500, 500));
        var near = new Aabb((-1, -1, -1), (1, 1, 1));

        static float FarPlaneRow(FrameDescription frame) => ((float[])frame.Shared!["uProj"])[14];

        Assert.NotEqual(
            FarPlaneRow(ViewportFrame.Build([], ViewportFrame.DefaultCamera(far), far, 1.6)),
            FarPlaneRow(ViewportFrame.Build([], ViewportFrame.DefaultCamera(near), near, 1.6)));
    }

    [Fact]
    public void NeutralShaderStateLeavesTheSectionRuleOff()
    {
        var shared = Build([]).Shared!;

        Assert.Equal(0f, shared["uSectionEnabled"]);
        Assert.Equal(0f, shared["uHighlight"]);
        Assert.Equal(1f, shared["uAlpha"]);
        // 0 makes the occlusion factor exactly 1.0, which IS the AO-off shading rather
        // than an approximation of it.
        Assert.Equal(0f, shared["uAmbientOcclusion"]);

        // uSectionCount is an `int` uniform and the interop sends every JSON number
        // through uniform1f, which GL rejects on an int. An unset int uniform is already
        // 0, and the clip rule short-circuits on uSectionEnabled, so the neutral state
        // must say nothing about it. Deleting this assertion is how the section rung
        // starts with an invisible GL error.
        Assert.DoesNotContain("uSectionCount", shared.Keys);
    }

    [Fact]
    public void EveryProgramNamedIsOneTheViewportCompiles()
    {
        string[] compiled =
            [ViewportFrame.MeshProgram, ViewportFrame.LineProgram, ViewportFrame.BackgroundProgram];
        var frame = Build([Instance("a", (0, 0, 0))], new ViewportFurniture("@grid", 12, "@axes"));

        Assert.All(frame.Draws, call => Assert.Contains(call.Program, compiled));
    }

    [Fact]
    public void DegenerateAspectIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ViewportFrame.Build([], Camera, Bounds, aspect: 0));

    [Fact]
    public void DefaultCameraFramesTheScene()
    {
        var camera = ViewportFrame.DefaultCamera(Bounds);

        // The viewer's first-visit iso view, matching OffscreenRenderer.DefaultCamera.
        Assert.Equal(0.7, camera.Yaw);
        Assert.Equal(0.45, camera.Pitch);
        Assert.Equal(CameraMath.FrameDistance(Bounds), camera.Distance);
        Assert.Equal(Bounds.Center, camera.Target);
    }

    [Fact]
    public void DefaultCameraOnAnEmptySceneStillLooksAtSomething()
    {
        var camera = ViewportFrame.DefaultCamera(Aabb.Empty);

        Assert.Equal(15.0, camera.Distance);
        Assert.Equal(Vector3d.Zero, camera.Target);
    }
}
