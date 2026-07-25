using EngrCAD.Core;
using EngrCAD.Modeling;
using EngrCAD.Viewer;

namespace EngrCAD.Mcp;

/// <summary>
/// Named camera poses for the <c>screenshot</c> tool — the viewer toolbar's
/// Front/Back/Left/Right/Top/Bottom/Iso buttons, available headlessly.
/// <para>The pose formulas mirror <c>ViewCubeMath.PoseFor</c> and
/// <c>CameraMath.FrameDistance</c> in EngrCAD.Viewer, which are internal to that
/// assembly. They are three lines of trigonometry each and are locked by tests here;
/// if the viewer ever makes them public, delete this file and call them directly.</para>
/// </summary>
public static class StandardViews
{
    /// <summary>Pitch clamp shared with the orbit camera (0.01 shy of the poles keeps
    /// the LookAt up-vector non-degenerate) — <c>ViewCubeMath.PitchLimit</c>.</summary>
    public const double PitchLimit = Math.PI / 2 - 0.01;

    /// <summary>Yaw used for the Top and Bottom views, where yaw is unconstrained: the
    /// Iso yaw, so a top view shows the model's front edge at the bottom of the frame.
    /// (The window keeps the camera's current yaw instead; there is no current yaw in
    /// a one-shot headless render.)</summary>
    public const double PoleYaw = -Math.PI / 4;

    /// <summary>The view direction (target toward eye) of a named view, or null when
    /// the name is not one of the standard views.</summary>
    public static Vector3d? DirectionFor(string view) => view.ToLowerInvariant() switch
    {
        "front" => new Vector3d(0, -1, 0),
        "back" => new Vector3d(0, 1, 0),
        "left" => new Vector3d(-1, 0, 0),
        "right" => new Vector3d(1, 0, 0),
        "top" => new Vector3d(0, 0, 1),
        "bottom" => new Vector3d(0, 0, -1),
        "iso" => new Vector3d(1, -1, 1),
        _ => null,
    };

    /// <summary>The standard view names, for error messages and tool descriptions.</summary>
    public static IReadOnlyList<string> Names { get; } =
        ["iso", "front", "back", "left", "right", "top", "bottom"];

    /// <summary>Orbit yaw/pitch looking along <paramref name="direction"/> — the
    /// mirror of <c>ViewCubeMath.PoseFor</c>.</summary>
    public static (double Yaw, double Pitch) PoseFor(in Vector3d direction)
    {
        var d = direction.Normalized();
        double pitch = Math.Clamp(Math.Asin(Math.Clamp(d.Z, -1, 1)), -PitchLimit, PitchLimit);
        // Exact-zero horizontal component test (semantic: the +-Z views), squared scale.
        double yaw = d.X * d.X + d.Y * d.Y < 1e-18 ? PoleYaw : Math.Atan2(d.Y, d.X);
        return (yaw, pitch);
    }

    /// <summary>Auto-framing orbit distance for a scene of the given bounds — the
    /// mirror of <c>CameraMath.FrameDistance</c>.</summary>
    public static double FrameDistance(in Aabb bounds) => Math.Max(bounds.Size.Length * 1.25 + 1, 2);

    /// <summary>
    /// The camera for a named view over the given instances: the standard pose at the
    /// auto-framing distance, targeted at the instances' centre. Returns null for
    /// "default", which lets the renderer use its own auto-framed iso view.
    /// </summary>
    public static CameraState? For(string view, IReadOnlyList<PartInstance> instances, MeshQuality? quality)
    {
        if (view.Equals("default", StringComparison.OrdinalIgnoreCase))
            return null;
        if (DirectionFor(view) is not { } direction)
            throw new ArgumentException(
                $"Unknown view '{view}' — use one of: {string.Join(", ", Names)}.", nameof(view));

        var bounds = Aabb.Empty;
        foreach (var instance in instances)
            bounds = bounds.Union(instance.Bounds(quality));
        var (yaw, pitch) = PoseFor(direction);
        return new CameraState(yaw, pitch,
            FrameDistance(bounds), bounds.IsEmpty ? Vector3d.Zero : bounds.Center);
    }
}
