using EngrCAD.Core;

namespace EngrCAD.Viewer;

/// <summary>
/// The section planes' clip rule, in ONE place: a world point is excluded by a plane
/// when <c>dot(world, normal) &gt; offset</c>, and several planes combine per
/// <see cref="SectionCombine"/> — <see cref="SectionCombine.Intersection"/> hides only
/// where EVERY plane excludes (the quarter/octant cutaway),
/// <see cref="SectionCombine.Union"/> where ANY does. This mirrors
/// <c>ViewerShaders.SectionClip</c> (RenderCore.cs) statement for statement, because
/// picking and hover apply the same rule on the CPU so a click cannot select a part
/// through the cut-away half. If the shader rule ever changes, both sides must change
/// here together. Pure math, no GL — unit-tested directly.
/// </summary>
internal static class SectionClip
{
    /// <summary>Whether the active section planes hide <paramref name="world"/>.</summary>
    public static bool Hides(
        bool enabled, in Vector3d world, IReadOnlyList<SectionPlane> planes, SectionCombine combine)
    {
        if (!enabled || planes.Count == 0)
            return false;

        bool any = false;
        bool all = true;
        for (int i = 0; i < planes.Count; i++)
        {
            bool excluded = world.Dot(planes[i].Normal) > planes[i].Offset;
            any |= excluded;
            all &= excluded;
        }
        return combine == SectionCombine.Union ? any : all;
    }

    /// <summary>Single axis-aligned plane — one plane makes the two combine rules
    /// coincide, so the mode does not matter here.</summary>
    public static bool Hides(bool enabled, in Vector3d world, SectionAxis axis, double offset) =>
        Hides(enabled, world, [SectionPlane.On(axis, offset)], SectionCombine.Intersection);
}
