using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

/// <summary>
/// The turntable: orbits about world Z at fixed pitch, distance and target — the
/// camera move everyone wants first. Yaw runs <c>turns</c> full revolutions across the
/// track, so with the animation's default <see cref="AnimationEasing.Linear"/> easing
/// and whole <paramref name="turns"/>, frame t = 1 lands exactly on frame t = 0 and the
/// export loops seamlessly.
/// </summary>
public sealed class TurntableTrack : CameraTrack
{
    private readonly CameraState _base;
    private readonly double _turns;

    /// <param name="baseCamera">The pose the turntable starts from (its yaw is the
    /// start yaw; pitch/distance/target are held).</param>
    /// <param name="turns">Full revolutions across the track (negative = clockwise).</param>
    public TurntableTrack(CameraState baseCamera, double turns = 1)
    {
        ArgumentNullException.ThrowIfNull(baseCamera);
        if (turns == 0)   // exact-zero semantic test: a zero-turn turntable is a request error
            throw new ArgumentOutOfRangeException(nameof(turns), "A turntable needs a nonzero turn count.");
        _base = baseCamera;
        _turns = turns;
    }

    /// <summary>A turntable framed on <paramref name="scene"/>'s bounds with the
    /// viewer's first-visit pose (<see cref="CameraMath.DefaultCamera"/>) — reads the
    /// parts' bounds, so call off the render thread, like <c>Scene.PreMesh</c>.</summary>
    public static TurntableTrack Around(Scene scene, double turns = 1)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var bounds = Aabb.Empty;
        foreach (var instance in scene.AllInstances)
            bounds = bounds.Union(instance.Bounds());
        return new TurntableTrack(CameraMath.DefaultCamera(bounds), turns);
    }

    public override CameraState CameraAt(double t) =>
        _base with { Yaw = _base.Yaw + t * _turns * 2 * Math.PI };
}

/// <summary>A camera keyframe: a timeline position (track-local fraction) and the pose
/// to be at there.</summary>
public sealed record CameraKeyframe(double T, CameraState Camera);

/// <summary>
/// Keyframed camera poses with the view cube's transition feel between them: each
/// segment eases with <see cref="ViewCubeMath.Ease"/> (smoothstep, C1 at both ends) and
/// yaw takes the SHORTEST angular path (<see cref="ViewCubeMath.ShortestYawTarget"/> —
/// the primitive the cube's 250 ms moves already use; interpolating yaw naively sends
/// the camera the long way round). Pitch, distance and target interpolate under the
/// same per-segment ease. Before the first keyframe the track holds the first pose,
/// after the last it holds the last.
/// </summary>
public sealed class KeyframedCameraTrack : CameraTrack
{
    private readonly IReadOnlyList<CameraKeyframe> _keys;

    public KeyframedCameraTrack(IEnumerable<CameraKeyframe> keyframes)
    {
        ArgumentNullException.ThrowIfNull(keyframes);
        _keys = keyframes.ToList();
        if (_keys.Count < 2)
            throw new ArgumentException("A keyframed camera track needs at least two keyframes.",
                nameof(keyframes));
        for (int i = 0; i < _keys.Count; i++)
        {
            if (_keys[i].T is < 0 or > 1)
                throw new ArgumentException(
                    $"Keyframe {i} sits at t = {_keys[i].T:g6}, outside [0, 1].", nameof(keyframes));
            if (i > 0 && !(_keys[i].T > _keys[i - 1].T))
                throw new ArgumentException(
                    $"Keyframe times must strictly ascend (keyframe {i} at t = {_keys[i].T:g6} does not " +
                    $"follow {_keys[i - 1].T:g6}).", nameof(keyframes));
        }
    }

    /// <summary>The keyframes, in timeline order.</summary>
    public IReadOnlyList<CameraKeyframe> Keyframes => _keys;

    public override CameraState CameraAt(double t)
    {
        if (t <= _keys[0].T)
            return _keys[0].Camera;
        if (t >= _keys[^1].T)
            return _keys[^1].Camera;

        int i = 0;
        while (t > _keys[i + 1].T)
            i++;
        var a = _keys[i];
        var b = _keys[i + 1];
        double e = ViewCubeMath.Ease((t - a.T) / (b.T - a.T));

        double yawTarget = ViewCubeMath.ShortestYawTarget(a.Camera.Yaw, b.Camera.Yaw);
        return new CameraState(
            a.Camera.Yaw + (yawTarget - a.Camera.Yaw) * e,
            a.Camera.Pitch + (b.Camera.Pitch - a.Camera.Pitch) * e,
            a.Camera.Distance + (b.Camera.Distance - a.Camera.Distance) * e,
            a.Camera.Target + (b.Camera.Target - a.Camera.Target) * e);
    }
}

/// <summary>
/// A fly-through: the eye travels along a <see cref="Curve3d"/> (any curve — a
/// <c>NurbsCurve.InterpolatePoints</c> through waypoints is the usual spelling),
/// looking along the tangent, or at a fixed point when <c>lookAt</c> is given.
/// <para><b>The orbit camera is Z-up, so the path frame's roll is dropped.</b> A full
/// RMF frame (the one <c>SweptSurface</c> sweeps with) carries a roll component the
/// (yaw, pitch, distance, target) pose cannot represent; what survives is the frame's
/// tangent, which is exactly what "look where you are flying" needs. Near-vertical
/// tangents clamp to the orbit pitch limit via <see cref="ViewCubeMath.PoseFor"/>, the
/// same rule every other camera move obeys.</para>
/// </summary>
public sealed class FlyThroughTrack : CameraTrack
{
    private readonly Curve3d _path;
    private readonly Vector3d? _lookAt;
    private readonly double _lookAhead;

    /// <param name="path">The eye's path; t sweeps its whole domain.</param>
    /// <param name="lookAhead">Orbit distance of the synthesized pose — how far ahead
    /// of the eye the camera target sits. Also the pan/zoom pivot if a viewer takes
    /// over after playback.</param>
    /// <param name="lookAt">Fixed world point to keep looking at (a walk-around);
    /// null looks along the flight direction.</param>
    public FlyThroughTrack(Curve3d path, double lookAhead = 1, Vector3d? lookAt = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!(lookAhead > 0))
            throw new ArgumentOutOfRangeException(nameof(lookAhead), "lookAhead must be positive.");
        _path = path;
        _lookAhead = lookAhead;
        _lookAt = lookAt;
    }

    public override CameraState CameraAt(double t)
    {
        var domain = _path.Domain;
        double u = domain.Start + Math.Clamp(t, 0, 1) * (domain.End - domain.Start);
        var eye = _path.PointAt(u);
        var forward = _lookAt is { } target ? (target - eye).Normalized() : _path.TangentAt(u);
        // The orbit pose's direction runs target → eye; PoseFor handles the vertical
        // degeneracy (yaw free at the poles) and the pitch clamp in one place.
        var (yaw, pitch) = ViewCubeMath.PoseFor(-forward, currentYaw: 0);
        return new CameraState(yaw, pitch, _lookAhead, eye + forward * _lookAhead);
    }
}
