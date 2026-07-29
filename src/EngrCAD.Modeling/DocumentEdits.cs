using System.Text.Json;
using System.Text.Json.Nodes;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>An edit could not be applied (or could not be undone) — the document is
/// untouched. Carries the <see cref="RegenerationResult"/> when a parametric edit was
/// rolled back because the model failed to rebuild.</summary>
public sealed class DocumentEditException(string message, RegenerationResult? regeneration = null)
    : InvalidOperationException(message)
{
    /// <summary>The failed regeneration, when that is why the edit was refused.</summary>
    public RegenerationResult? Regeneration { get; } = regeneration;
}

/// <summary>
/// One reversible change to a document — the <c>MeshChange</c> pattern at document
/// granularity. An edit holds its target by reference and, on <see cref="Apply"/>,
/// captures whatever it is about to overwrite, so <see cref="Revert"/> restores the
/// previous state exactly rather than recomputing it.
///
/// <para><b>The two contracts, both tested against the document SERIALIZER as the oracle
/// rather than field by field</b> (a state comparison written by hand agrees with a
/// broken revert as happily as with a correct one):</para>
/// <list type="number">
/// <item><description><b>Revert restores a state that serializes identically.</b> Not
/// "equivalent" — identical, which is what makes an undone edit indistinguishable from an
/// edit that never happened, down to list positions and occurrence names.</description></item>
/// <item><description><b>A failed <see cref="Apply"/> leaves the document untouched.</b>
/// Guards run before any mutation, and an edit that mutates several things (or whose
/// regeneration fails) rolls its own partial work back before throwing — the
/// guards-never-partially-mutate rule <c>EditableMesh</c>'s Euler operators follow.</description></item>
/// </list>
///
/// <para>Build them with the <see cref="DocumentEdits"/> factories and run them through an
/// <see cref="UndoStack"/>, which is what turns them into an undo history.</para>
/// </summary>
public abstract class DocumentEdit
{
    /// <summary>What this edit did, for an Edit menu ("Undo Set plate.Height").</summary>
    public abstract string Description { get; }

    /// <summary>Performs the edit. Throws <see cref="DocumentEditException"/> (leaving the
    /// document untouched) when it cannot.</summary>
    public abstract void Apply();

    /// <summary>Undoes the edit, restoring the state <see cref="Apply"/> captured.</summary>
    public abstract void Revert();
}

/// <summary>
/// Several edits as ONE user-visible step — what <see cref="UndoStack.Group"/> produces.
/// Apply runs them in order and, if any fails, reverts the ones that already succeeded
/// before rethrowing, so a compound edit is atomic in exactly the way its parts are;
/// Revert runs them backwards.
/// </summary>
public sealed class CompoundEdit(string description, IReadOnlyList<DocumentEdit> edits) : DocumentEdit
{
    private readonly DocumentEdit[] _edits = [.. edits];

    /// <inheritdoc />
    public override string Description { get; } = description;

    /// <summary>The parts, in order.</summary>
    public IReadOnlyList<DocumentEdit> Edits => _edits;

    /// <inheritdoc />
    public override void Apply()
    {
        for (int i = 0; i < _edits.Length; i++)
        {
            try
            {
                _edits[i].Apply();
            }
            catch
            {
                for (int j = i - 1; j >= 0; j--)
                    _edits[j].Revert();
                throw;
            }
        }
    }

    /// <inheritdoc />
    public override void Revert()
    {
        for (int i = _edits.Length - 1; i >= 0; i--)
            _edits[i].Revert();
    }
}

/// <summary>
/// The edits a CAD host actually performs, as reversible <see cref="DocumentEdit"/>s:
/// parameter changes and suppression through the parametric history, feature insertion and
/// removal, part properties, assembly occurrences, mates and annotations.
///
/// <para>Every parametric edit routes through the SAME JSON seam as
/// <see cref="FeatureHistory.SaveParameters"/> / <see cref="FeatureHistory.LoadParameters"/>
/// and the MCP server's <c>set_param</c>, so a value cannot mean one thing typed into a
/// properties panel and another loaded from a file; and every one ends in
/// <see cref="Part.Regenerate"/>, whose failure rolls the edit back rather than leaving a
/// bad parameter on a good body.</para>
/// </summary>
public static class DocumentEdits
{
    // ---- parametric edits ----------------------------------------------

    /// <summary>Sets one <c>[Param]</c> value on a feature and regenerates the part. The
    /// value goes through the JSON vocabulary, so a <see cref="Vector3d"/>, an enum and a
    /// <see cref="GeometryRef"/> descriptor all spell themselves exactly as they do in a
    /// saved file.</summary>
    public static DocumentEdit SetParameter(Part part, Feature feature, string parameter, object? value)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        var values = new JsonObject
        {
            [parameter] = JsonSerializer.SerializeToNode(
                FeatureHistory.SerializeValue(value), FeatureHistory.JsonOptions),
        };
        return new ParameterEdit(part, feature, values, $"Set {Label(part, feature)}.{parameter}");
    }

    /// <summary>Sets several <c>[Param]</c> values at once from a JSON object
    /// (<c>{ "Height": 12, "Plane": "topPlane" }</c>) — the raw form of
    /// <see cref="SetParameter"/>, and the one a properties panel or an MCP tool already
    /// has in hand. One regeneration for the lot.</summary>
    public static DocumentEdit SetParameters(Part part, Feature feature, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var values = JsonNode.Parse(json) as JsonObject
            ?? throw new ArgumentException("Parameter values are a JSON object.", nameof(json));
        return new ParameterEdit(part, feature, values, $"Edit {Label(part, feature)}");
    }

    /// <summary>Suppresses or unsuppresses a feature and regenerates.</summary>
    public static DocumentEdit Suppress(Part part, Feature feature, bool suppressed) =>
        new SuppressEdit(part, feature, suppressed);

    /// <summary>Inserts a feature into a part's history (appended when
    /// <paramref name="index"/> is null) and regenerates.</summary>
    public static DocumentEdit AddFeature(Part part, Feature feature, int? index = null) =>
        new FeatureListEdit(part, feature, index, remove: false);

    /// <summary>Removes a feature from a part's history and regenerates. Undo puts it back
    /// at the same position, which matters: a history is ordered.</summary>
    public static DocumentEdit RemoveFeature(Part part, Feature feature) =>
        new FeatureListEdit(part, feature, null, remove: true);

    // ---- part properties -----------------------------------------------

    /// <summary>Renames a part (see <see cref="Scene.Rename"/> for the uniqueness rule the
    /// scene enforces; a colliding name refuses before anything changes).</summary>
    public static DocumentEdit Rename(Scene scene, Part part, string name)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(part);
        return new ValueEdit<string>(
            $"Rename {part.Name}", () => part.Name, n => scene.Rename(part, n), name);
    }

    /// <summary>Recolours a part.</summary>
    public static DocumentEdit SetColor(Part part, PartColor color)
    {
        ArgumentNullException.ThrowIfNull(part);
        return new ValueEdit<PartColor?>(
            $"Recolour {part.Name}", () => part.Color, c => part.Color = c, color);
    }

    /// <summary>Re-places a part (its own transform, not an occurrence pose).</summary>
    public static DocumentEdit SetTransform(Part part, Matrix4d transform)
    {
        ArgumentNullException.ThrowIfNull(part);
        return new ValueEdit<Matrix4d>(
            $"Move {part.Name}", () => part.Transform, t => part.Transform = t, transform);
    }

    /// <summary>Changes how a part is drawn.</summary>
    public static DocumentEdit SetDisplayMode(Part part, DisplayMode mode)
    {
        ArgumentNullException.ThrowIfNull(part);
        return new ValueEdit<DisplayMode>(
            $"Display {part.Name} as {mode}", () => part.DisplayMode, m => part.DisplayMode = m, mode);
    }

    /// <summary>Sets whether section planes cut a part (false is the drafting convention
    /// for shafts, fasteners, keys and pins).</summary>
    public static DocumentEdit SetClippedBySection(Part part, bool clipped)
    {
        ArgumentNullException.ThrowIfNull(part);
        return new ValueEdit<bool>(
            $"{(clipped ? "Cut" : "Keep whole")} {part.Name} in section",
            () => part.ClippedBySection, c => part.ClippedBySection = c, clipped);
    }

    // ---- assemblies -----------------------------------------------------

    /// <summary>Places a part in an assembly. Undo removes the occurrence; redo puts back
    /// the SAME occurrence at the same index with the same derived name.</summary>
    public static DocumentEdit AddOccurrence(
        Assembly assembly, Part part, Frame3d? frame = null, string? name = null) =>
        new AddOccurrenceEdit(assembly, part, null, frame, name);

    /// <summary>Places a sub-assembly in an assembly.</summary>
    public static DocumentEdit AddOccurrence(
        Assembly assembly, Assembly subAssembly, Frame3d? frame = null, string? name = null) =>
        new AddOccurrenceEdit(assembly, null, subAssembly, frame, name);

    /// <summary>Removes an occurrence; undo restores it at its old index.</summary>
    public static DocumentEdit RemoveOccurrence(Assembly assembly, Occurrence occurrence) =>
        new RemoveOccurrenceEdit(assembly, occurrence);

    /// <summary>Re-poses an occurrence (a drag in the viewport, or a mate solve committed
    /// as one step).</summary>
    public static DocumentEdit Repose(Occurrence occurrence, Frame3d frame)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        return new ValueEdit<Frame3d>(
            $"Move {occurrence.Name}", () => occurrence.Frame, f => occurrence.Frame = f, frame);
    }

    /// <summary>Sets (or clears) an occurrence's exploded-view displacement.</summary>
    public static DocumentEdit SetExplodeOffset(Occurrence occurrence, Vector3d? offset)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        return new ValueEdit<Vector3d?>(
            $"Explode {occurrence.Name}",
            () => occurrence.ExplodeOffset, o => occurrence.ExplodeOffset = o, offset);
    }

    // ---- mates and annotations ------------------------------------------

    /// <summary>Adds a mate to a set (validated by <see cref="MateSet.Add"/> before
    /// anything changes). Does not solve — that is a separate, and separately undoable,
    /// step.</summary>
    public static DocumentEdit AddMate(MateSet set, Mate mate) => new MateEdit(set, mate, remove: false);

    /// <summary>Removes a mate; undo restores it at its old index.</summary>
    public static DocumentEdit RemoveMate(MateSet set, Mate mate) => new MateEdit(set, mate, remove: true);

    /// <summary>Attaches an annotation to a part.</summary>
    public static DocumentEdit AddAnnotation(Part part, Annotation annotation) =>
        new AnnotationEdit(part, annotation, remove: false);

    /// <summary>Detaches an annotation; undo restores it at its old index.</summary>
    public static DocumentEdit RemoveAnnotation(Part part, Annotation annotation) =>
        new AnnotationEdit(part, annotation, remove: true);

    // ---- implementations ------------------------------------------------

    private static string Label(Part part, Feature feature) =>
        $"{part.Name}.{(part.History is { } history ? history.NameOf(feature) : feature.Name)}";

    /// <summary>The general "one property, old value captured" edit — every part, occurrence
    /// and display property rides it, so there is one implementation of capture-and-restore
    /// instead of a dozen near-identical classes.</summary>
    private sealed class ValueEdit<T>(
        string description, Func<T> read, Action<T> write, T value) : DocumentEdit
    {
        private T _old = default!;
        private bool _applied;

        public override string Description => description;

        public override void Apply()
        {
            var old = read();
            write(value);   // a refusal (a colliding name) throws before _old is trusted
            _old = old;
            _applied = true;
        }

        public override void Revert()
        {
            if (!_applied)
                throw new DocumentEditException($"'{description}' was never applied.");
            write(_old);
        }
    }

    /// <summary>
    /// A <c>[Param]</c> edit: capture the feature's WHOLE parameter object, apply the new
    /// values through the JSON seam, regenerate. Capturing all of them rather than only the
    /// ones being written is the <c>MeshChange</c> instinct — the complete journal is what
    /// makes revert exact — and it costs nothing at these sizes.
    /// </summary>
    private sealed class ParameterEdit(
        Part part, Feature feature, JsonObject values, string description) : DocumentEdit
    {
        private JsonObject? _old;

        public override string Description => description;

        public override void Apply()
        {
            RequireHistory(part, feature);
            // Guard BEFORE mutating: an unknown parameter name must refuse the whole edit
            // rather than apply the entries it happened to recognize.
            var known = Feature.PropertiesOf(feature.GetType()).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var entry in values)
            {
                if (!known.Contains(entry.Key))
                    throw new DocumentEditException(
                        $"{feature.GetType().Name} has no [Param] named '{entry.Key}' " +
                        $"(it has {(known.Count == 0 ? "none" : string.Join(", ", known.Order(StringComparer.Ordinal)))}).");
            }

            var old = Capture(feature);
            Write(feature, values, description);
            RegenerateOrRollback(part, description, () => Write(feature, old, description));
            _old = old;
        }

        public override void Revert()
        {
            if (_old is null)
                throw new DocumentEditException($"'{description}' was never applied.");
            Write(feature, _old, description);
            // The previous state rebuilt once; it must rebuild again. A failure here is a
            // bug, not a user error, so it is reported as such rather than swallowed.
            RegenerateOrThrow(part, $"undo of '{description}'");
        }

        private static JsonObject Capture(Feature feature)
        {
            var json = new JsonObject();
            foreach (var parameter in feature.Parameters)
            {
                json[parameter.Name] = JsonSerializer.SerializeToNode(
                    FeatureHistory.SerializeValue(parameter.Value), FeatureHistory.JsonOptions);
            }
            return json;
        }

        private static void Write(Feature feature, JsonObject values, string description)
        {
            var warnings = new List<string>();
            using var document = JsonDocument.Parse(values.ToJsonString());
            FeatureHistory.ApplyParameters(feature, document.RootElement, feature.Name, warnings);
            if (warnings.Count > 0)
                throw new DocumentEditException($"{description}: {string.Join("; ", warnings)}");
        }
    }

    private sealed class SuppressEdit(Part part, Feature feature, bool suppressed) : DocumentEdit
    {
        private bool _old;
        private bool _applied;

        public override string Description =>
            $"{(suppressed ? "Suppress" : "Unsuppress")} {Label(part, feature)}";

        public override void Apply()
        {
            RequireHistory(part, feature);
            _old = feature.Suppressed;
            feature.Suppressed = suppressed;
            RegenerateOrRollback(part, Description, () => feature.Suppressed = _old);
            _applied = true;
        }

        public override void Revert()
        {
            if (!_applied)
                throw new DocumentEditException($"'{Description}' was never applied.");
            feature.Suppressed = _old;
            RegenerateOrThrow(part, $"undo of '{Description}'");
        }
    }

    private sealed class FeatureListEdit(Part part, Feature feature, int? index, bool remove) : DocumentEdit
    {
        private int _index = -1;

        public override string Description =>
            $"{(remove ? "Delete" : "Add")} {feature.Name} in {part.Name}";

        public override void Apply()
        {
            var history = part.History ?? throw new DocumentEditException(
                $"Part '{part.Name}' has no feature history to edit.");
            if (remove)
            {
                _index = IndexOf(history, feature);
                if (_index < 0)
                    throw new DocumentEditException(
                        $"Feature '{feature.Name}' is not in part '{part.Name}'s history.");
                history.RemoveAt(_index);
                RegenerateOrRollback(part, Description, () => history.Insert(_index, feature));
            }
            else
            {
                if (IndexOf(history, feature) >= 0)
                    throw new DocumentEditException(
                        $"Feature '{feature.Name}' is already in part '{part.Name}'s history.");
                _index = index ?? history.Features.Count;
                if (_index < 0 || _index > history.Features.Count)
                    throw new DocumentEditException(
                        $"Cannot insert at {_index}: the history has {history.Features.Count} features.");
                history.Insert(_index, feature);
                RegenerateOrRollback(part, Description, () => history.RemoveAt(_index));
            }
        }

        public override void Revert()
        {
            var history = part.History!;
            if (_index < 0)
                throw new DocumentEditException($"'{Description}' was never applied.");
            if (remove)
                history.Insert(_index, feature);
            else
                history.RemoveAt(_index);
            RegenerateOrThrow(part, $"undo of '{Description}'");
        }

        private static int IndexOf(FeatureHistory history, Feature feature)
        {
            for (int i = 0; i < history.Features.Count; i++)
            {
                if (ReferenceEquals(history.Features[i], feature))
                    return i;
            }
            return -1;
        }
    }

    private sealed class AddOccurrenceEdit(
        Assembly assembly, Part? part, Assembly? subAssembly, Frame3d? frame, string? name) : DocumentEdit
    {
        private Occurrence? _occurrence;
        private int _index = -1;

        public override string Description =>
            $"Place {part?.Name ?? subAssembly!.Name} in {assembly.Name}";

        public override void Apply()
        {
            if (_occurrence is null)
            {
                // First run: Assembly.Add derives the name and validates the cycle rule.
                _occurrence = part is not null
                    ? assembly.Add(part, frame, name)
                    : assembly.Add(subAssembly!, frame, name);
                _index = assembly.IndexOf(_occurrence);
            }
            else
            {
                // Redo: the SAME occurrence, at the same index, with the same name.
                assembly.Insert(_index, _occurrence);
            }
        }

        public override void Revert()
        {
            if (_occurrence is null)
                throw new DocumentEditException($"'{Description}' was never applied.");
            if (!assembly.Remove(_occurrence))
                throw new DocumentEditException(
                    $"'{Description}' cannot be undone: the occurrence is no longer in '{assembly.Name}'.");
        }
    }

    private sealed class RemoveOccurrenceEdit(Assembly assembly, Occurrence occurrence) : DocumentEdit
    {
        private int _index = -1;

        public override string Description => $"Remove {occurrence.Name} from {assembly.Name}";

        public override void Apply()
        {
            int index = assembly.IndexOf(occurrence);
            if (index < 0)
                throw new DocumentEditException(
                    $"Occurrence '{occurrence.Name}' is not in assembly '{assembly.Name}'.");
            assembly.Remove(occurrence);
            _index = index;
        }

        public override void Revert()
        {
            if (_index < 0)
                throw new DocumentEditException($"'{Description}' was never applied.");
            assembly.Insert(_index, occurrence);
        }
    }

    private sealed class MateEdit(MateSet set, Mate mate, bool remove) : DocumentEdit
    {
        private int _index = -1;

        public override string Description =>
            $"{(remove ? "Remove" : "Add")} mate {mate.Name} on {set.Assembly.Name}";

        public override void Apply()
        {
            if (remove)
            {
                _index = set.IndexOf(mate);
                if (_index < 0)
                    throw new DocumentEditException($"Mate '{mate.Name}' is not in this set.");
                set.Remove(mate);
            }
            else
            {
                if (set.IndexOf(mate) >= 0)
                    throw new DocumentEditException($"Mate '{mate.Name}' is already in this set.");
                set.Add(mate);   // validates the references before anything changes
                _index = set.IndexOf(mate);
            }
        }

        public override void Revert()
        {
            if (_index < 0)
                throw new DocumentEditException($"'{Description}' was never applied.");
            if (remove)
                set.Insert(_index, mate);
            else
                set.Remove(mate);
        }
    }

    private sealed class AnnotationEdit(Part part, Annotation annotation, bool remove) : DocumentEdit
    {
        private int _index = -1;

        public override string Description =>
            $"{(remove ? "Remove" : "Add")} {annotation.GetType().Name} on {part.Name}";

        public override void Apply()
        {
            if (remove)
            {
                _index = part.IndexOfAnnotation(annotation);
                if (_index < 0)
                    throw new DocumentEditException(
                        $"That annotation is not attached to part '{part.Name}'.");
                part.RemoveAnnotation(annotation);
            }
            else
            {
                if (part.IndexOfAnnotation(annotation) >= 0)
                    throw new DocumentEditException(
                        $"That annotation is already attached to part '{part.Name}'.");
                part.Annotate(annotation);
                _index = part.IndexOfAnnotation(annotation);
            }
        }

        public override void Revert()
        {
            if (_index < 0)
                throw new DocumentEditException($"'{Description}' was never applied.");
            if (remove)
                part.InsertAnnotation(_index, annotation);
            else
                part.RemoveAnnotation(annotation);
        }
    }

    // ---- shared helpers -------------------------------------------------

    private static void RequireHistory(Part part, Feature feature)
    {
        var history = part.History ?? throw new DocumentEditException(
            $"Part '{part.Name}' has no feature history — it was built directly from geometry.");
        if (!history.Features.Any(f => ReferenceEquals(f, feature)))
            throw new DocumentEditException(
                $"Feature '{feature.Name}' is not in part '{part.Name}'s history.");
    }

    /// <summary>
    /// Regenerates after a parametric mutation and, when the model fails to rebuild, undoes
    /// that mutation and rebuilds again before throwing — so a refused edit leaves the
    /// document exactly as it found it, parameters included.
    /// <para><see cref="Part.Regenerate"/> already keeps the previous complete BODY on
    /// failure, which is what stops a half-built solid reaching the screen; what it cannot
    /// do is know that the bad parameter was ours to take back.</para>
    /// </summary>
    private static void RegenerateOrRollback(Part part, string description, Action rollback)
    {
        var result = part.Regenerate();
        if (result.Succeeded)
            return;
        rollback();
        part.Regenerate();
        throw new DocumentEditException(
            $"{description} was refused: the model did not rebuild.\n{result}", result);
    }

    private static void RegenerateOrThrow(Part part, string what)
    {
        var result = part.Regenerate();
        if (!result.Succeeded)
            throw new DocumentEditException(
                $"The {what} left the model unable to rebuild, which should be impossible " +
                $"(this state rebuilt once already).\n{result}", result);
    }
}
