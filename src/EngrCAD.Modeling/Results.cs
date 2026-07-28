using EngrCAD.Mesh;

namespace EngrCAD.Modeling;

// Simulation results in the DOCUMENT model. A MeshField (EngrCAD.Mesh) is the data; the
// three types here are how a Part carries it and says what should be drawn.
//
// The split follows DisplayMode's precedent exactly: the enum and the intent live in
// EngrCAD.Modeling (so a design, a script or an MCP tool can express them with no viewer
// reference), and the colour TABLES that turn them into pixels live in
// EngrCAD.Viewer.Core beside the shaders. Neither half can drift, because one is a
// choice and the other is the only implementation of it.

/// <summary>
/// Which colour map a scalar field is drawn through.
/// <para>The tables are in <c>ColorMaps</c> (EngrCAD.Viewer.Core); this is the
/// document-model choice, so a design can pick one without referencing a renderer.</para>
/// </summary>
public enum FieldColorMap
{
    /// <summary>
    /// The perceptual default: dark blue-purple through teal and green to yellow.
    /// Monotone in lightness, so it reads correctly in greyscale and for the common
    /// colour-vision deficiencies — the right choice for a magnitude (stress,
    /// temperature, displacement) where only "more" and "less" are meaningful.
    /// </summary>
    Viridis,

    /// <summary>
    /// A diverging blue-grey-red map for signed quantities, where the neutral midpoint
    /// is the interesting value. It only means what it looks like over a range centred
    /// on zero — see <c>FieldRange.SymmetricAboutZero</c>, which is deliberately
    /// something the caller asks for rather than something choosing this map does
    /// silently.
    /// </summary>
    Diverging,
}

/// <summary>
/// What a viewer should draw for a part that carries results: which result colours it,
/// through which map and over what range, and whether the shape is displaced by a
/// displacement result.
/// <para>Attached to a <see cref="Part"/> (<see cref="Part.FieldDisplay"/>), so the
/// choice belongs to the document rather than to a viewport — a headless render, the
/// desktop window and the browser client all show the same thing, and a script can set
/// it up with no viewer reference.</para>
/// </summary>
public sealed record FieldDisplay
{
    /// <summary>Name of the result (<c>MeshField.Name</c>) that colours the part.</summary>
    public required string Field { get; init; }

    /// <summary>Which colour map (default <see cref="FieldColorMap.Viridis"/>).</summary>
    public FieldColorMap ColorMap { get; init; } = FieldColorMap.Viridis;

    /// <summary>
    /// The value range the map spans; null uses the field's own min/max. An explicit
    /// range is what makes several parts — or several load cases — comparable, since
    /// each field's own range would give each one its own scale.
    /// </summary>
    public FieldRange? Range { get; init; }

    /// <summary>
    /// Name of a VECTOR result to displace the vertices by (a displacement field), or
    /// null for the undeformed shape. This is genuinely new geometry per result, not a
    /// pose: a viewer re-uploads its buffers for it, deliberately, and it is kept off
    /// the animation path for exactly that reason.
    /// </summary>
    public string? Deform { get; init; }

    /// <summary>Exaggeration applied to <see cref="Deform"/> (1 = true scale). Real
    /// deflections are usually invisible, so a scale factor is the norm rather than a
    /// debugging aid — which is why it is stated in the document and appears on the
    /// legend rather than living in a viewport slider.</summary>
    public double DeformScale { get; init; } = 1;

    /// <summary>Whether the UNDEFORMED shape is also drawn, ghosted behind the deformed
    /// one (default true; ignored without <see cref="Deform"/>). The comparison is the
    /// point of a deformed-shape plot.</summary>
    public bool ShowUndeformed { get; init; } = true;
}

/// <summary>
/// A <see cref="FieldDisplay"/> with its names resolved against a part's results and
/// its range settled — what a renderer consumes, so no render path re-implements the
/// lookup or the "null range means the field's own" rule.
/// </summary>
/// <param name="Field">The result that colours the part.</param>
/// <param name="Range">The range the colour map spans (never empty).</param>
/// <param name="ColorMap">Which map.</param>
/// <param name="Deform">The displacement result, or null.</param>
/// <param name="DeformScale">Exaggeration for <paramref name="Deform"/>.</param>
/// <param name="ShowUndeformed">Whether to ghost the undeformed shape.</param>
public readonly record struct ResolvedFieldDisplay(
    MeshField Field,
    FieldRange Range,
    FieldColorMap ColorMap,
    MeshField? Deform,
    double DeformScale,
    bool ShowUndeformed)
{
    /// <summary>Legend title: the field's name and units.</summary>
    public string Label => Field.Label;
}
