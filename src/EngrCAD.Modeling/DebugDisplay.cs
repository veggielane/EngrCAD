namespace EngrCAD.Modeling;

/// <summary>
/// The shared rules for the part-level debug modifiers (<see cref="Part.Hidden"/> /
/// <see cref="Part.Ghost"/> / <see cref="Part.Isolated"/> — the OpenSCAD
/// <c>*</c>/<c>%</c>/<c>!</c> analog), in ONE place so the window, the offscreen
/// renderer and every exporter cannot disagree about what a flag means:
/// <list type="bullet">
/// <item><b>Hidden</b> — not shown, not exported.</item>
/// <item><b>Ghost</b> — shown (translucent, via
/// <see cref="Part.EffectiveDisplayMode"/>), NOT exported: reference geometry you
/// want on screen but never in a print file.</item>
/// <item><b>Isolated</b> — when any part in scope is isolated, only isolated parts
/// are shown/exported. Scope is whatever part collection the caller is operating on
/// (a viewer tab, a whole scene for headless render/export).</item>
/// </list>
/// </summary>
public static class DebugFilter
{
    /// <summary>Whether any part in scope carries <see cref="Part.Isolated"/> —
    /// compute once per pass, then feed <see cref="IsShown"/>/<see cref="IsExported"/>.</summary>
    public static bool AnyIsolated(IEnumerable<Part> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return parts.Any(p => p.Isolated);
    }

    /// <summary>Whether a part is rendered (Hidden and the isolate rule; Ghost parts
    /// ARE shown — that is their point).</summary>
    public static bool IsShown(Part part, bool anyIsolated) =>
        !part.Hidden && (!anyIsolated || part.Isolated);

    /// <summary>Whether a part belongs in exported geometry — <see cref="IsShown"/>
    /// minus ghosts.</summary>
    public static bool IsExported(Part part, bool anyIsolated) =>
        IsShown(part, anyIsolated) && !part.Ghost;

    /// <summary>The instances a renderer should draw, order preserved. With no flags
    /// set anywhere this returns the input contents unchanged.</summary>
    public static IReadOnlyList<PartInstance> Shown(IReadOnlyList<PartInstance> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);
        bool anyIsolated = AnyIsolated(instances.Select(i => i.Part).Distinct());
        return [.. instances.Where(i => IsShown(i.Part, anyIsolated))];
    }

    /// <summary>The instances an exporter should write, order preserved.</summary>
    public static IReadOnlyList<PartInstance> Exported(IReadOnlyList<PartInstance> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);
        bool anyIsolated = AnyIsolated(instances.Select(i => i.Part).Distinct());
        return [.. instances.Where(i => IsExported(i.Part, anyIsolated))];
    }
}
