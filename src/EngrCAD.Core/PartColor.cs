namespace EngrCAD.Core;

/// <summary>
/// RGB color in [0, 1] for display purposes (UI-framework free).
///
/// <para>It lives here, rather than in the document model where it is mostly used, because
/// <see cref="Material"/> carries an optional display color and Core is the only assembly
/// both the document model and the simulation layer see. The type is a bare triple of floats
/// with no document semantics; the <i>policy</i> — the palette and the once-only assignment
/// rule a part's color follows — stays in <c>EngrCAD.Modeling</c>, which is where the
/// invariant lives.</para>
/// </summary>
public readonly record struct PartColor(float R, float G, float B);
