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

    /// <summary>
    /// The clip planes that bound plane <paramref name="index"/>'s own <em>cut face</em>
    /// — the overlay rule for anything drawn ON a section plane (today the SDF
    /// isolines). Written into <paramref name="into"/> and always applied with
    /// <see cref="SectionCombine.Union"/>, whatever <paramref name="combine"/> the model
    /// itself uses; an empty result means "no clipping" (the single-plane case).
    /// <para>
    /// The rule it implements, stated exactly: a point <c>p</c> lying on plane
    /// <c>index</c> is on the VISIBLE cut face iff <c>p</c> itself is not hidden AND the
    /// material immediately past the plane IS hidden —
    /// <c>!Hides(p) &amp;&amp; Hides(p + eps * normal)</c>. That single sentence gives
    /// both modes without a special case:
    /// </para>
    /// <list type="bullet">
    /// <item><description><see cref="SectionCombine.Intersection"/> (the quarter/octant
    /// cutaway): <c>p</c> on the plane is never hidden (the plane's own test is strict,
    /// so it does not exclude its own points, and Intersection needs EVERY plane to
    /// exclude), while <c>p + eps * normal</c> is hidden exactly where every OTHER plane
    /// excludes. So the face is exposed where all siblings exclude — clip where any
    /// sibling does not, which is the siblings FLIPPED.</description></item>
    /// <item><description><see cref="SectionCombine.Union"/>: <c>p + eps * normal</c> is
    /// always hidden (this plane alone suffices), so the only question is whether
    /// <c>p</c> survives — clip where any sibling excludes it, i.e. the siblings
    /// unflipped.</description></item>
    /// </list>
    /// Without this, a quarter cut draws each plane's contours across its full extent,
    /// including the half buried inside the remaining material.
    /// </summary>
    public static void Siblings(
        IReadOnlyList<SectionPlane> planes, int index, SectionCombine combine, List<SectionPlane> into)
    {
        into.Clear();
        for (int j = 0; j < planes.Count; j++)
        {
            if (j != index)
                into.Add(combine == SectionCombine.Union ? planes[j] : planes[j].Flipped());
        }
    }
}
