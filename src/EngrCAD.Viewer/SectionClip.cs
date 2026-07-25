using EngrCAD.Core;

namespace EngrCAD.Viewer;

/// <summary>
/// The section plane's clip rule, in ONE place: a world point is hidden when
/// <c>dot(world, axis) &gt; offset</c> — the same test the mesh, line, and point
/// fragment shaders discard on (<c>ViewerShaders</c> in RenderCore.cs). Picking and
/// hover apply it on the CPU so a click cannot select a part through the cut-away
/// half; if the shader rule ever changes, both sides must change here together.
/// Pure math, no GL — unit-tested directly.
/// </summary>
internal static class SectionClip
{
    /// <summary>Whether the section plane hides <paramref name="world"/>.</summary>
    public static bool Hides(bool enabled, in Vector3d world, SectionAxis axis, double offset) =>
        enabled && world.Dot(axis.Direction()) > offset;
}
