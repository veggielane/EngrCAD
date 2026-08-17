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

    // ---- configurations --------------------------------------------------

    /// <summary>
    /// Activates one of a part's <see cref="ConfigurationSet">configurations</see> — ONE
    /// edit and ONE rebuild, however many parameters it states (composing it out of
    /// per-feature <see cref="SetParameter"/>s would regenerate once per feature).
    ///
    /// <para>What undo restores is the model's LIVE parameter values, captured here, not the
    /// previously active configuration's stored ones — the <c>MeshChange</c>
    /// complete-journal rule. The two differ exactly when someone had edited a parameter
    /// without capturing it, and restoring the stored values there would silently discard
    /// that edit.</para>
    ///
    /// <para>Values the history declines (a configuration naming a feature that has since
    /// been removed) are WARNINGS, not failures, and a <see cref="DocumentEdit"/> has no
    /// channel for them: call <see cref="ConfigurationSet.Validate"/> first, or
    /// <see cref="ConfigurationSet.Activate"/> directly when the messages matter.</para>
    /// </summary>
    public static DocumentEdit SetConfiguration(Part part, string name) =>
        new ConfigurationEdit(part, name);

    /// <summary>Adds a configuration to a part (no rebuild — a stored parameter set changes
    /// no geometry until it is activated).</summary>
    public static DocumentEdit AddConfiguration(Part part, Configuration configuration) =>
        new ConfigurationListEdit(part, configuration, null, remove: false);

    /// <summary>Removes a configuration; undo puts it back at its old index, and restores it
    /// as ACTIVE when it was.</summary>
    public static DocumentEdit RemoveConfiguration(Part part, string name) =>
        new ConfigurationListEdit(part, null, name, remove: true);

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

    /// <summary>
    /// Assigns (or clears) a part's material. It is a plain value edit — the material feeds
    /// mass properties and the BOM, neither of which is cached on the part, so nothing has to
    /// be invalidated. The part's <b>colour is untouched</b>, deliberately: a material only
    /// ever supplied the DEFAULT at add time, and silently recolouring a part someone has
    /// already coloured would be a second, hidden edit inside this one.
    /// </summary>
    public static DocumentEdit SetMaterial(Part part, Material? material)
    {
        ArgumentNullException.ThrowIfNull(part);
        return new ValueEdit<Material?>(
            material is null ? $"Clear {part.Name}'s material" : $"{part.Name}: {material.Name}",
            () => part.Material, m => part.Material = m, material);
    }

    /// <summary>Changes which simulation result a part displays (or clears the display
    /// with null) — the load-step/frequency selector in its honest discrete form. A
    /// display naming a result the part does not carry is legal document state:
    /// resolution reports it rather than this edit refusing it, exactly as a saved
    /// document whose result was removed reloads with the display intact.</summary>
    public static DocumentEdit SetFieldDisplay(Part part, FieldDisplay? display)
    {
        ArgumentNullException.ThrowIfNull(part);
        return new ValueEdit<FieldDisplay?>(
            display is null ? $"Clear {part.Name}'s result display" : $"{part.Name}: show {display.Field}",
            () => part.FieldDisplay, d => part.FieldDisplay = d, display);
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

    /// <summary>Adds a loose part to a tab. Redo re-adds the SAME part object, whose
    /// palette colour was assigned on the first Apply and sticks (the colour-stability
    /// rule), so undo→redo is byte-identical through the serializer.</summary>
    public static DocumentEdit AddPart(Tab tab, Part part)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(part);
        return new AddPartEdit(tab, part);
    }

    /// <summary>
    /// Removes a loose part from a tab — REFUSING BY NAME when any assembly in the
    /// scene still places it: removing a placed part must be deliberate, so the
    /// occurrences come out first (each its own undoable edit; a UI offering the
    /// cascade groups them). Undo re-inserts at the captured index, since the
    /// serializer writes parts in list order and position is part of the document's
    /// identity.
    /// </summary>
    public static DocumentEdit RemovePart(Scene scene, Tab tab, Part part)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(part);
        return new RemovePartEdit(scene, tab, part);
    }

    /// <summary>Adds a tab. The edit creates its <see cref="Tab"/> ONCE and re-inserts
    /// the same object on redo, so references into it survive an undo/redo cycle.</summary>
    public static DocumentEdit AddTab(Scene scene, string name)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return new AddTabEdit(scene, name);
    }

    /// <summary>Removes a tab (its parts are untouched — other tabs referencing them
    /// keep them); undo re-inserts the same tab object, contents intact, at the
    /// captured index.</summary>
    public static DocumentEdit RemoveTab(Scene scene, Tab tab)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(tab);
        return new RemoveTabEdit(scene, tab);
    }

    private sealed class AddPartEdit(Tab tab, Part part) : DocumentEdit
    {
        public override string Description => $"Add {part.Name}";

        public override void Apply()
        {
            try
            {
                tab.Add(part);
            }
            catch (ArgumentException exception)
            {
                throw new DocumentEditException(exception.Message);
            }
        }

        public override void Revert() => tab.Remove(part);
    }

    private sealed class RemovePartEdit(Scene scene, Tab tab, Part part) : DocumentEdit
    {
        private int _index = -1;

        public override string Description => $"Remove {part.Name}";

        public override void Apply()
        {
            var placements = new List<string>();
            foreach (var t in scene.Tabs)
            {
                foreach (var assembly in t.Assemblies)
                    CollectPlacements(assembly, assembly.Name, placements);
            }
            if (placements.Count > 0)
            {
                throw new DocumentEditException(
                    $"Part '{part.Name}' is still placed by {placements.Count} occurrence(s) "
                    + $"({string.Join(", ", placements)}). Remove the occurrences first — each is "
                    + "its own undoable edit, and a UI offering the cascade groups them.");
            }
            _index = tab.IndexOf(part);
            if (_index < 0)
                throw new DocumentEditException($"Part '{part.Name}' is not a loose part of tab '{tab.Name}'.");
            tab.Remove(part);
        }

        private void CollectPlacements(Assembly assembly, string path, List<string> placements)
        {
            foreach (var occurrence in assembly.Occurrences)
            {
                if (ReferenceEquals(occurrence.Part, part))
                    placements.Add($"{path}/{occurrence.Name}");
                if (occurrence.SubAssembly is { } nested)
                    CollectPlacements(nested, $"{path}/{occurrence.Name}", placements);
            }
        }

        public override void Revert() => tab.Insert(_index, part);
    }

    private sealed class AddTabEdit(Scene scene, string name) : DocumentEdit
    {
        private Tab? _tab;

        public override string Description => $"Add tab {name}";

        public override void Apply()
        {
            try
            {
                if (_tab is null)
                {
                    _tab = scene.AddTab(name);
                }
                else
                {
                    // Redo: the SAME tab object comes back (references survive), at the
                    // end — where AddTab put it and where undo removed it from.
                    scene.InsertTab(scene.Tabs.Count, _tab);
                }
            }
            catch (ArgumentException exception)
            {
                throw new DocumentEditException(exception.Message);
            }
        }

        public override void Revert() => scene.RemoveTab(_tab!);
    }

    private sealed class RemoveTabEdit(Scene scene, Tab tab) : DocumentEdit
    {
        private int _index = -1;

        public override string Description => $"Remove tab {tab.Name}";

        public override void Apply()
        {
            _index = scene.IndexOf(tab);
            if (_index < 0)
                throw new DocumentEditException($"Tab '{tab.Name}' is not in the scene.");
            scene.RemoveTab(tab);
        }

        public override void Revert()
        {
            try
            {
                scene.InsertTab(_index, tab);
            }
            catch (ArgumentException exception)
            {
                throw new DocumentEditException(exception.Message);
            }
        }
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

    /// <summary>
    /// Commits a mate solve as ONE undoable step. Apply captures every occurrence frame
    /// in the set's assembly TREE first (the safe superset — a deep mate's unknown is a
    /// nested occurrence's frame, and an untouched frame restores to itself exactly),
    /// then runs <see cref="MateSet.Solve"/>; Revert restores the captured frames, so
    /// undo takes the whole repose back in one Ctrl+Z. A solve that does not converge
    /// writes NOTHING (the solver's own refuse-loudly contract), so the exception
    /// propagates with the document untouched and the edit is never pushed — the
    /// refused-edit rule. Redo re-solves from the restored drawn poses, which is
    /// bit-identical because the solver is deterministic from its seed.
    /// </summary>
    public static DocumentEdit SolveMates(MateSet set, MateSolverSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        return new SolveMatesEdit(set, settings);
    }

    private sealed class SolveMatesEdit(MateSet set, MateSolverSettings? settings) : DocumentEdit
    {
        private readonly List<(Occurrence Occurrence, Frame3d Frame)> _before = [];

        public override string Description => "Solve mates";

        public override void Apply()
        {
            _before.Clear();
            Capture(set.Assembly);
            try
            {
                set.Solve(settings);
            }
            catch
            {
                _before.Clear(); // nothing was written; hold no stale capture
                throw;
            }
        }

        private void Capture(Assembly assembly)
        {
            foreach (var occurrence in assembly.Occurrences)
            {
                _before.Add((occurrence, occurrence.Frame));
                if (occurrence.SubAssembly is { } nested)
                    Capture(nested);
            }
        }

        public override void Revert()
        {
            foreach (var (occurrence, frame) in _before)
                occurrence.Frame = frame;
        }
    }

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

    /// <summary>
    /// Activating a configuration: capture the model's whole live parameter set AND the
    /// active name, apply, regenerate once, and roll both back if the model refuses to
    /// rebuild.
    /// </summary>
    private sealed class ConfigurationEdit(Part part, string name) : DocumentEdit
    {
        private string? _oldActive;
        private string? _oldParameters;
        private bool _applied;

        public override string Description => $"Configuration {part.Name}: {name}";

        public override void Apply()
        {
            var set = RequireConfigurations(part);
            if (set.Find(name) is null)
            {
                throw new DocumentEditException(
                    $"Part '{part.Name}' has no configuration named '{name}'"
                    + $" (it has {(set.Count == 0 ? "none" : string.Join(", ", set.Names))}).");
            }

            string? oldActive = set.Active;
            string oldParameters = part.History!.SaveParameters();
            var result = set.Activate(name);
            if (!result.Succeeded)
            {
                Restore(set, oldParameters, oldActive);
                throw new DocumentEditException(
                    $"{Description} was refused: the model did not rebuild.\n{result.Regeneration}",
                    result.Regeneration);
            }
            _oldActive = oldActive;
            _oldParameters = oldParameters;
            _applied = true;
        }

        public override void Revert()
        {
            if (!_applied)
                throw new DocumentEditException($"'{Description}' was never applied.");
            var set = part.Configurations!;
            set.SetActiveName(_oldActive);
            part.History!.LoadParameters(_oldParameters!);
            RegenerateOrThrow(part, $"undo of '{Description}'");
        }

        private static void Restore(ConfigurationSet set, string parameters, string? active)
        {
            set.SetActiveName(active);
            set.Part.History!.LoadParameters(parameters);
            set.Part.Regenerate();
        }
    }

    private sealed class ConfigurationListEdit(
        Part part, Configuration? added, string? name, bool remove) : DocumentEdit
    {
        private Configuration? _configuration = added;
        private int _index = -1;
        private bool _wasActive;

        public override string Description =>
            $"{(remove ? "Remove" : "Add")} configuration {name ?? _configuration!.Name} on {part.Name}";

        public override void Apply()
        {
            var set = RequireConfigurations(part);
            if (remove)
            {
                _index = set.IndexOf(name!);
                if (_index < 0)
                    throw new DocumentEditException(
                        $"Part '{part.Name}' has no configuration named '{name}'.");
                _configuration = set[_index];
                _wasActive = string.Equals(set.Active, name, StringComparison.Ordinal);
                set.Remove(name!);
            }
            else
            {
                set.Add(_configuration!);   // refuses a duplicate name before anything changes
                _index = set.IndexOf(_configuration!.Name);
            }
        }

        public override void Revert()
        {
            if (_index < 0)
                throw new DocumentEditException($"'{Description}' was never applied.");
            var set = part.Configurations!;
            if (remove)
            {
                set.Insert(_index, _configuration!);
                if (_wasActive)
                    set.SetActiveName(_configuration!.Name);
            }
            else
            {
                set.Remove(_configuration!.Name);
            }
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

    private static ConfigurationSet RequireConfigurations(Part part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return part.Configurations ?? throw new DocumentEditException(
            $"Part '{part.Name}' has no feature history, so it has no configurations.");
    }

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
