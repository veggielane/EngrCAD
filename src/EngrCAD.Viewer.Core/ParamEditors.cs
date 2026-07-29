using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

/// <summary>Which affordance a <c>[Param]</c> deserves in a properties panel.</summary>
public enum ParamEditorKind
{
    /// <summary>A text field: the fallback, and the right answer for a string, an
    /// unbounded number, a vector, or anything the panel has no better idea about.</summary>
    Text,

    /// <summary>A checkbox — a <see cref="bool"/> parameter.</summary>
    Toggle,

    /// <summary>A dropdown of the enum's members.</summary>
    Choice,

    /// <summary>A slider (beside a text field), for a numeric parameter whose
    /// <c>[Param(Min=, Max=)]</c> range is finite and non-degenerate.</summary>
    Slider,
}

/// <summary>
/// Which editor a feature parameter gets, decided from metadata the registry has carried
/// since features landed. Free text was the placeholder, not the design: the
/// <c>[Param]</c> attribute already states a type and a <c>Min</c>/<c>Max</c> range, so a
/// panel that makes a user type <c>"Counterbore"</c> and learn the spelling from an error
/// message is throwing that away.
///
/// <para><b>Here, and pure, for the reason every other shared render decision is</b>: the
/// desktop properties panel and any future browser one must agree about what a parameter
/// looks like, and a rule restated in two front ends is two rules. It is also the only
/// way to assert the decision as a VALUE rather than by looking at a window.</para>
///
/// <para>The load-bearing constraint is one level down and not expressed here: whatever
/// editor is chosen, it writes through the SAME JSON seam as
/// <c>FeatureHistory.SaveParameters</c> and the MCP <c>set_param</c> tool. A typed editor
/// is a better way to SAY a value, never a second way to apply one.</para>
/// </summary>
public static class ParamEditors
{
    /// <summary>The editor <paramref name="parameter"/> deserves.</summary>
    public static ParamEditorKind KindFor(ParamInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        var type = Nullable.GetUnderlyingType(parameter.Type) ?? parameter.Type;
        if (type == typeof(bool))
            return ParamEditorKind.Toggle;
        if (type.IsEnum)
            return ParamEditorKind.Choice;
        return IsNumeric(type) && HasRange(parameter) ? ParamEditorKind.Slider : ParamEditorKind.Text;
    }

    /// <summary>True when the parameter's range is genuinely bounded and has travel — an
    /// unbounded <c>[Param]</c> (the default is ±infinity) has nothing to map a slider
    /// onto, and Min == Max has nothing to choose.</summary>
    public static bool HasRange(ParamInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        return double.IsFinite(parameter.Min) && double.IsFinite(parameter.Max)
            && parameter.Max > parameter.Min;
    }

    /// <summary>True when the parameter steps in whole numbers, so a slider should snap
    /// and its text should not grow a decimal point.</summary>
    public static bool IsWhole(ParamInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        var type = Nullable.GetUnderlyingType(parameter.Type) ?? parameter.Type;
        return type == typeof(int) || type == typeof(long);
    }

    /// <summary>The parameter's value as a double for a slider position (0 when it is
    /// not a number — a slider is only offered for numerics, so this is the guard rather
    /// than the answer).</summary>
    public static double Position(ParamInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        double value = parameter.Value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            _ => 0,
        };
        return HasRange(parameter) ? Math.Clamp(value, parameter.Min, parameter.Max) : value;
    }

    /// <summary>
    /// What a MATERIAL dropdown offers for a part currently made of
    /// <paramref name="current"/>: <c>null</c> (the "(none)" row — unstated is a legal and
    /// common answer, so it must be reachable) first, then
    /// <see cref="Materials.All"/> in catalogue order.
    ///
    /// <para><b>A material the catalogue does not carry is appended</b> — one a design
    /// built with <c>new Material(...)</c>, or a <c>FastenerMaterials</c> grade a
    /// catalogue component brought with it. Without that row the dropdown would show
    /// nothing selected for a part that plainly states a material, which reads as "not
    /// set", and one idle click would discard it. Comparison is the record's value
    /// equality, so a re-derived copy of a catalogue entry matches its row rather than
    /// producing a duplicate.</para>
    ///
    /// <para>Pure and here for the same reason <see cref="KindFor"/> is: the desktop panel
    /// and any browser one must offer the same list, and the rule is then asserted as a
    /// value instead of by looking at a window.</para>
    /// </summary>
    public static IReadOnlyList<Material?> MaterialChoices(Material? current)
    {
        var choices = new List<Material?>(Materials.All.Count + 2) { null };
        choices.AddRange(Materials.All);
        if (current is not null && !choices.Contains(current))
            choices.Add(current);
        return choices;
    }

    /// <summary>The label a <see cref="MaterialChoices"/> row shows.</summary>
    public static string MaterialLabel(Material? material) => material?.Name ?? "(none)";

    private static bool IsNumeric(Type type) =>
        type == typeof(double) || type == typeof(float) || type == typeof(int) || type == typeof(long);
}
