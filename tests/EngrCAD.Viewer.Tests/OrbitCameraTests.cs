using EngrCAD.Core;
using EngrCAD.Viewer;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The orbit camera's state transitions, which now live in <see cref="CameraMath"/>
/// (EngrCAD.Viewer.Core) rather than inside the Avalonia control — the Blazor client
/// calls exactly these, so a drag has one implementation and cannot feel different in
/// the browser. Pure math: no GL, no Avalonia, no browser.
/// </summary>
public class OrbitCameraTests
{
    private const double Tol = 1e-12;

    private static readonly Aabb Scene = new((-10, -10, 0), (10, 10, 5));

    private static CameraState Start => new(0.7, 0.45, 30, (1, 2, 3));

    // ---- clamps ----

    [Fact]
    public void PitchStopsShortOfThePole()
    {
        // Straight up would make LookAt's up vector degenerate.
        Assert.Equal(CameraMath.PitchLimit, CameraMath.Orbit(Start, 0, 10).Pitch, Tol);
        Assert.Equal(-CameraMath.PitchLimit, CameraMath.Orbit(Start, 0, -10).Pitch, Tol);
    }

    [Fact]
    public void ViewCubeAndOrbitAgreeOnThePitchLimit() =>
        // Not a coincidence to be re-asserted: if the cube snapped to a pitch the orbit
        // clamp then pulled back, "go to Top" would visibly not go to top.
        Assert.Equal(ViewCubeMath.PitchLimit, CameraMath.PitchLimit);

    [Fact]
    public void YawIsFree()
    {
        // Yaw wraps naturally through the trigonometry; clamping it would put a wall in
        // the middle of a turntable drag.
        Assert.Equal(Start.Yaw + 100, CameraMath.Orbit(Start, 100, 0).Yaw, Tol);
    }

    [Fact]
    public void ZoomIsBoundedByTheSceneSize()
    {
        Assert.Equal(0.5, CameraMath.Zoom(Start, 1e-9, Scene).Distance, Tol);
        Assert.Equal(
            CameraMath.MaxOrbitDistance(Scene), CameraMath.Zoom(Start, 1e9, Scene).Distance, Tol);
    }

    [Fact]
    public void ClampedNormalizesAnExternallySuppliedPose()
    {
        var clamped = CameraMath.Clamped(new CameraState(0.3, 5, 1e6, (0, 0, 0)), Scene);

        Assert.Equal(0.3, clamped.Yaw, Tol);
        Assert.Equal(CameraMath.PitchLimit, clamped.Pitch, Tol);
        Assert.Equal(CameraMath.MaxOrbitDistance(Scene), clamped.Distance, Tol);
    }

    // ---- drag bindings (the numbers a viewport feels through) ----

    [Fact]
    public void DraggingRightTurnsTheModelRight() =>
        Assert.True(CameraMath.DragOrbit(Start, 10, 0).Yaw < Start.Yaw);

    [Fact]
    public void DraggingDownRaisesThePitch() =>
        Assert.True(CameraMath.DragOrbit(Start, 0, 10).Pitch > Start.Pitch);

    [Fact]
    public void DragOrbitUsesTheSharedPixelsToRadiansRate()
    {
        var moved = CameraMath.DragOrbit(Start, 100, 50);

        Assert.Equal(Start.Yaw - 1.0, moved.Yaw, Tol);
        Assert.Equal(Start.Pitch + 0.5, moved.Pitch, Tol);
    }

    [Fact]
    public void DraggingDownWithControlZoomsOut() =>
        Assert.True(CameraMath.DragZoom(Start, 100, Scene).Distance > Start.Distance);

    [Fact]
    public void ScrollingUpZoomsIn() =>
        // A wheel notch toward the viewer brings the model closer, as on the desktop.
        Assert.True(CameraMath.WheelZoom(Start, 1, Scene).Distance < Start.Distance);

    [Fact]
    public void ZeroDeltasChangeNothing()
    {
        Assert.Equal(Start, CameraMath.DragOrbit(Start, 0, 0));
        Assert.Equal(Start, CameraMath.DragPan(Start, 0, 0));
        Assert.Equal(Start, CameraMath.DragZoom(Start, 0, Scene));
        Assert.Equal(Start, CameraMath.WheelZoom(Start, 0, Scene));
    }

    // ---- pan ----

    [Fact]
    public void PanMovesTheTargetInTheScreenPlane()
    {
        var camera = Start;
        var eye = CameraMath.Eye(camera.Yaw, camera.Pitch, camera.Distance, camera.Target);
        var forward = (camera.Target - eye).Normalized();

        var panned = CameraMath.Pan(camera, 40, -25);
        var travel = panned.Target - camera.Target;

        Assert.True(travel.Length > 0);
        // The target slides across the view, never along it: a pan that crept forward
        // would zoom by stealth.
        Assert.Equal(0, travel.Dot(forward), 1e-9);
    }

    [Fact]
    public void PanScalesWithDistanceSoItFeelsTheSameWhenZoomedOut()
    {
        var near = Start with { Distance = 10 };
        var far = Start with { Distance = 100 };

        double nearTravel = (CameraMath.Pan(near, 50, 0).Target - near.Target).Length;
        double farTravel = (CameraMath.Pan(far, 50, 0).Target - far.Target).Length;

        Assert.Equal(10 * nearTravel, farTravel, 1e-9);
    }

    [Fact]
    public void PanLeavesTheOrbitPoseAlone()
    {
        var panned = CameraMath.Pan(Start, 30, 20);

        Assert.Equal(Start.Yaw, panned.Yaw, Tol);
        Assert.Equal(Start.Pitch, panned.Pitch, Tol);
        Assert.Equal(Start.Distance, panned.Distance, Tol);
    }
}
