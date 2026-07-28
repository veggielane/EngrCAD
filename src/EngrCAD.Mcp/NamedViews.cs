using EngrCAD.Core;
using EngrCAD.Modeling;
using EngrCAD.Viewer;

namespace EngrCAD.Mcp;

/// <summary>
/// Named camera poses for the <c>screenshot</c> tool — the viewer toolbar's
/// Front/Back/Left/Right/Top/Bottom/Iso buttons, available headlessly.
/// <para>This is a NAME TABLE, not pose math: the poses come from
/// <see cref="ViewCubeMath.PoseFor"/> and the framing distance from
/// <see cref="CameraMath.FrameDistance"/> — the same functions the window's toolbar,
/// the view cube, and the browser client use, so all front ends agree about what
/// "Front" means by construction. (Its predecessor, <c>StandardViews</c>, duplicated
/// those formulas from the days they were internal to the viewer; the equivalence
/// tests that warranted its deletion live on in <c>NamedViewsTests</c>.)</para>
/// </summary>
internal static class NamedViews
{
    /// <summary>Yaw used for the Top and Bottom views, where yaw is unconstrained: the
    /// Iso yaw, so a top view shows the model's front edge at the bottom of the frame.
    /// (The window keeps the camera's current yaw instead; there is no current yaw in
    /// a one-shot headless render.)</summary>
    public const double PoleYaw = -Math.PI / 4;

    /// <summary>The view direction (target toward eye) of a named view, or null when
    /// the name is not one of the standard views — the shared name table.</summary>
    public static Vector3d? DirectionFor(string view) => ViewCubeMath.DirectionFor(view);

    /// <summary>The standard view names, for error messages and tool descriptions.</summary>
    public static IReadOnlyList<string> Names => ViewCubeMath.StandardViewNames;

    /// <summary>Orbit yaw/pitch looking along <paramref name="direction"/> — the shared
    /// pose function with <see cref="PoleYaw"/> standing in for the window's current
    /// yaw at the poles.</summary>
    public static (double Yaw, double Pitch) PoseFor(in Vector3d direction) =>
        ViewCubeMath.PoseFor(direction, currentYaw: PoleYaw);

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
            CameraMath.FrameDistance(bounds), bounds.IsEmpty ? Vector3d.Zero : bounds.Center);
    }
}
