using EngrCAD.Core;

namespace EngrCAD.Modeling;

// Callout generators: standard drawing-note text from the same specs that cut the
// geometry (HoleSpec / ThreadSpec), packaged as LeaderNote annotations. Symbols per
// ASME Y14.5 style: \u2300 diameter, \u21A7 depth, \u2334 counterbore,
// \u2335 countersink, \u00D7 times, \u00B0 degrees (source files stay pure ASCII —
// escapes only).

/// <summary>
/// Generates standard hole-callout text ("&#x2300;5.5 &#x21A7;14", with
/// counterbore/countersink continuations) from a <see cref="HoleSpec"/>, so drilled
/// parts can label themselves from the spec that cut them.
/// </summary>
public static class HoleCallout
{
    /// <summary>
    /// The callout text for a hole drilled with <paramref name="spec"/> to
    /// <paramref name="depth"/>: "&#x2300;D &#x21A7;depth", followed by
    /// "&#x2334;&#x2300;D &#x21A7;d" for counterbores or
    /// "&#x2335;&#x2300;D &#x00D7;angle&#x00B0;" for countersinks, and
    /// " &#x00D7;angle&#x00B0; TIP" when the hole carries a drill point.
    /// </summary>
    /// <remarks>
    /// The depth is quoted as given, i.e. to the SHOULDER, which is what the tip
    /// annotation means on a drawing: the point reaches further and is not dimensioned
    /// (see <see cref="HoleSpec.WithTipAngle"/>).
    /// </remarks>
    public static string Text(HoleSpec spec, double depth)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (depth <= 0)
            throw new ArgumentOutOfRangeException(nameof(depth));
        string callout = "\u2300" + Annotation.Format(spec.Diameter)
            + " \u21A7" + Annotation.Format(depth);
        if (spec.TipAngleDegrees is { } tip)
            callout += " \u00D7" + Annotation.Format(tip) + "\u00B0 TIP";
        if (spec.IsCounterbore)
            callout += " \u2334\u2300" + Annotation.Format(spec.FeatureDiameter)
                + " \u21A7" + Annotation.Format(spec.CounterboreDepth);
        else if (spec.IsCountersink)
            callout += " \u2335\u2300" + Annotation.Format(spec.FeatureDiameter)
                + " \u00D7" + Annotation.Format(spec.CountersinkAngleDegrees) + "\u00B0";
        return callout;
    }

    /// <summary>A <see cref="LeaderNote"/> carrying the callout, anchored at
    /// <paramref name="anchor"/> (part-local — typically a point on the hole's rim).</summary>
    public static LeaderNote From(HoleSpec spec, Vector3d anchor, double depth) =>
        new(anchor, Text(spec, depth));
}

/// <summary>
/// Generates standard thread-callout text ("M6&#x00D7;1 &#x21A7;12") from a
/// <see cref="ThreadSpec"/> — the designation plus an optional depth.
/// </summary>
public static class ThreadCallout
{
    /// <summary>The callout text: the spec's designation ("M6&#x00D7;1"), with
    /// " &#x21A7;depth" appended for blind threads (null depth = through/rod).</summary>
    public static string Text(ThreadSpec spec, double? depth = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (depth is <= 0)
            throw new ArgumentOutOfRangeException(nameof(depth));
        return depth is { } d
            ? spec.Designation + " \u21A7" + Annotation.Format(d)
            : spec.Designation;
    }

    /// <summary>A <see cref="LeaderNote"/> carrying the callout, anchored at
    /// <paramref name="anchor"/> (part-local — typically a point on the thread's rim).</summary>
    public static LeaderNote From(ThreadSpec spec, Vector3d anchor, double? depth = null) =>
        new(anchor, Text(spec, depth));
}
