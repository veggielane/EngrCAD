using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EngrCAD.Modeling;

/// <summary>
/// One named parameter set for a part's <see cref="FeatureHistory"/> — an "M6" of an
/// M4…M12 family, a "short" of a length family.
///
/// <para><b>The parameters are the <see cref="FeatureHistory.SaveParameters"/> JSON,
/// verbatim</b>: <c>{ "featureName": { "Param": value } }</c>. That is the whole design.
/// The same seam already carries a saved parameter file, an MCP <c>set_param</c>, a
/// properties-panel edit, <c>DocumentEdits.SetParameter</c> and a design study's
/// <c>StudyResult.Edits</c>, so a configuration cannot
/// spell a value differently from any of them — and applying one is
/// <see cref="FeatureHistory.LoadParameters"/>, not a second way to write a value.</para>
///
/// <para><b>A set may be PARTIAL.</b> Nothing requires a configuration to name every
/// feature: an M4…M12 family states only the bolt size, and the plate's thickness stays
/// whatever the model currently says. <see cref="ConfigurationSet.Capture(string)"/>
/// snapshots everything; the narrowing overloads and the typed
/// <c>ConfigurationSet.Add(name, (feature, parameter, value)…)</c> state less on
/// purpose.</para>
///
/// <para><b>Values only — no suppression, no feature list.</b> A configuration cannot add,
/// remove or suppress a feature, which is what makes a switch cheap AND exact: the feature
/// INSTANCES never change, so <see cref="FeatureHistory"/>'s prefix cache (keyed on
/// instance identity plus the parameter snapshot) re-runs precisely the prefix a forward
/// edit would and switching away and back returns bit-identical geometry. Per-configuration
/// suppression is filed rather than smuggled in: it is not part of the
/// <c>SaveParameters</c> vocabulary, and a second spelling beside it is exactly the drift
/// the one-seam rule exists to prevent.</para>
/// </summary>
public sealed class Configuration
{
    private readonly JsonObject _values;

    /// <summary>
    /// Builds a configuration from a name and <see cref="FeatureHistory.SaveParameters"/>
    /// JSON. The JSON is parsed and re-serialized through the shared options at
    /// construction, so <see cref="Parameters"/> is CANONICAL however it was spelled — which
    /// is what lets two configurations be compared as strings and what keeps the document
    /// format a byte fixed point whatever indentation a file happened to carry.
    /// </summary>
    /// <exception cref="ArgumentException">The name is blank, or the JSON is not an object
    /// of per-feature parameter objects.</exception>
    public Configuration(string name, string parameters)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A configuration's name must be non-empty.", nameof(name));
        ArgumentNullException.ThrowIfNull(parameters);

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(parameters);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                $"Configuration '{name}': the parameters are not valid JSON ({exception.Message}).",
                nameof(parameters));
        }
        _values = parsed as JsonObject ?? throw new ArgumentException(
            $"Configuration '{name}': parameters are a JSON object of "
            + "{ \"feature\": { \"Param\": value } }, as FeatureHistory.SaveParameters writes.",
            nameof(parameters));
        foreach (var entry in _values)
        {
            if (entry.Value is not JsonObject)
                throw new ArgumentException(
                    $"Configuration '{name}': '{entry.Key}' must map to an object of parameter values.",
                    nameof(parameters));
        }

        Name = name;
        Parameters = _values.ToJsonString(FeatureHistory.JsonOptions);
    }

    /// <summary>The configuration's name, unique within its <see cref="ConfigurationSet"/>.</summary>
    public string Name { get; }

    /// <summary>The parameter values as canonical <see cref="FeatureHistory.SaveParameters"/>
    /// JSON — the one vocabulary (see the class notes).</summary>
    public string Parameters { get; }

    /// <summary>The feature names this configuration states values for, in file order. A
    /// configuration naming no features is legal and means "leave everything alone".</summary>
    public IReadOnlyList<string> Features => [.. _values.Select(entry => entry.Key)];

    /// <summary>How many individual <c>[Param]</c> values this configuration states.</summary>
    public int ValueCount => _values.Sum(entry => ((JsonObject)entry.Value!).Count);

    internal JsonObject Values => _values;

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({ValueCount} value{(ValueCount == 1 ? "" : "s")})";
}

/// <summary>The outcome of <see cref="ConfigurationSet.Activate"/>: what was applied, the
/// regeneration it triggered, and one warning per value the history could not take.</summary>
/// <param name="Name">The configuration that was activated.</param>
/// <param name="Succeeded">True when the model rebuilt completely (see
/// <paramref name="Regeneration"/>). Note the values are applied EITHER WAY — a failed
/// regeneration keeps the previous complete body, exactly as <see cref="Part.Regenerate"/>
/// does for any other parameter edit.</param>
/// <param name="Regeneration">The regeneration, verbatim.</param>
/// <param name="Warnings">Values the history declined — an unknown feature name, an unknown
/// parameter, a value of the wrong shape. Reported, never thrown and never silently
/// dropped, exactly as <see cref="FeatureHistory.LoadParameters"/> reports them.</param>
public sealed record ConfigurationResult(
    string Name, bool Succeeded, RegenerationResult Regeneration, IReadOnlyList<string> Warnings);

/// <summary>
/// A part's named parameter sets — <b>one <see cref="FeatureHistory"/>, N configurations</b>
/// — plus which one is active.
///
/// <code>
/// var configurations = bracket.Configurations!;
/// foreach (double size in new[] { 4.0, 5.0, 6.0, 8.0, 10.0, 12.0 })
///     configurations.Add($"M{size:0}", (holes, nameof(BoltHoles.Size), size));
/// configurations.Activate("M8");
/// </code>
///
/// <para><b>It lives on the <see cref="Part"/>, not on the history</b>, and the reason is
/// the rebuild: a configuration is only meaningful once something can regenerate from it,
/// and <see cref="Part.Regenerate"/> is the one call that swaps the fresh body in AND clears
/// every derived cache (mesh, B-Rep and SDF lowerings, feature edges, resolved annotations,
/// construction tree). A set living on the history could load values and leave every
/// consumer looking at stale geometry. The history still owns the parameter VOCABULARY,
/// which is exactly the seam this writes through.</para>
///
/// <para><b>The active configuration is DOCUMENT state.</b> An undo stack is session state
/// because it records how the document got here; the active configuration records where it
/// IS — it names the parameter values the model currently carries, and those are saved with
/// the history. Dropping the name on save would leave a reloaded document whose values
/// exactly match "M6" unable to say so.</para>
///
/// <para><b>Activating does not write back.</b> Editing a parameter while "M6" is active
/// does NOT update "M6"; it leaves the model MODIFIED against it
/// (<see cref="ActiveIsModified"/>), and <see cref="Capture(string)"/> is the deliberate act
/// of storing the current values. That is what keeps a configuration's values a function of
/// the document rather than of the order in which someone clicked.</para>
/// </summary>
public sealed class ConfigurationSet : IReadOnlyList<Configuration>
{
    private readonly Part _part;
    private readonly List<Configuration> _items = [];
    private string? _active;

    internal ConfigurationSet(Part part) => _part = part;

    /// <summary>The part these configurations drive.</summary>
    public Part Part => _part;

    private FeatureHistory History => _part.History
        ?? throw new InvalidOperationException(
            $"Part '{_part.Name}' has no feature history to configure.");

    /// <inheritdoc />
    public int Count => _items.Count;

    /// <inheritdoc />
    public Configuration this[int index] => _items[index];

    /// <inheritdoc />
    public IEnumerator<Configuration> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The configuration names, in order.</summary>
    public IReadOnlyList<string> Names => [.. _items.Select(item => item.Name)];

    /// <summary>The active configuration's NAME, or null when none has been activated.</summary>
    public string? Active => _active;

    /// <summary>The active configuration, or null.</summary>
    public Configuration? ActiveConfiguration => _active is null ? null : Find(_active);

    /// <summary>
    /// True when a configuration is active and the model no longer agrees with it — someone
    /// edited a parameter it states since it was applied.
    /// <para>The comparison is EXACT and needs no tolerance, because both sides come from the
    /// same serializer: the model's current values through
    /// <see cref="FeatureHistory.SaveParameters"/>, the configuration's as stored.</para>
    /// </summary>
    public bool ActiveIsModified => ActiveConfiguration is { } configuration && !Matches(configuration);

    /// <summary>The configuration of that name, or null (ordinal comparison).</summary>
    public Configuration? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var item in _items)
        {
            if (string.Equals(item.Name, name, StringComparison.Ordinal))
                return item;
        }
        return null;
    }

    /// <summary>The index of the configuration of that name, or -1.</summary>
    public int IndexOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (int i = 0; i < _items.Count; i++)
        {
            if (string.Equals(_items[i].Name, name, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    // ---- authoring ------------------------------------------------------

    /// <summary>Adds an already-built configuration.</summary>
    /// <exception cref="ArgumentException">A configuration of that name already exists.</exception>
    public Configuration Add(Configuration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (Find(configuration.Name) is not null)
            throw new ArgumentException(
                $"Part '{_part.Name}' already has a configuration named '{configuration.Name}'.",
                nameof(configuration));
        _items.Add(configuration);
        return configuration;
    }

    /// <summary>
    /// Adds a configuration from raw <see cref="FeatureHistory.SaveParameters"/> JSON — the
    /// form a file, an MCP client or another document already has in hand.
    /// <para>Feature and parameter NAMES are not checked here, deliberately: this overload
    /// names them by string, which is exactly the position
    /// <see cref="FeatureHistory.LoadParameters"/> is in, so a name this history does not
    /// know is reported as a warning when the configuration is applied (or up front by
    /// <see cref="Validate"/>) rather than thrown at a caller holding a file. The typed
    /// <c>Add(name, (feature, parameter, value)…)</c> overload, whose
    /// caller holds the feature OBJECT, refuses instead.</para>
    /// </summary>
    public Configuration Add(string name, string parameters) => Add(new Configuration(name, parameters));

    /// <summary>
    /// Adds a configuration stating individual values —
    /// <c>set.Add("M8", (holes, "Size", 8.0), (holes, "Depth", 14.0))</c>.
    /// <para>Values are serialized through <see cref="FeatureHistory.SerializeValue"/>, so a
    /// <c>Vector3d</c>, an enum and a <c>GeometryRef</c> descriptor spell themselves exactly
    /// as they do in a saved file. The feature must be in this part's history and the
    /// parameter must exist on it — both are refused BY NAME, because a caller holding the
    /// object has made a mistake rather than read a stale file.</para>
    /// </summary>
    public Configuration Add(string name, params (Feature Feature, string Parameter, object? Value)[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var json = new JsonObject();
        foreach (var (feature, parameter, value) in values)
        {
            ArgumentNullException.ThrowIfNull(feature);
            ArgumentNullException.ThrowIfNull(parameter);
            string featureName = History.NameOf(feature);   // refuses a feature not in this history
            var known = Feature.PropertiesOf(feature.GetType()).Select(p => p.Name).ToList();
            if (!known.Contains(parameter, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    $"{feature.GetType().Name} has no [Param] named '{parameter}' "
                    + $"(it has {(known.Count == 0 ? "none" : string.Join(", ", known.Order(StringComparer.Ordinal)))}).",
                    nameof(values));
            }
            if (json[featureName] is not JsonObject entry)
                json[featureName] = entry = [];
            entry[parameter] = JsonSerializer.SerializeToNode(
                FeatureHistory.SerializeValue(value), FeatureHistory.JsonOptions);
        }
        return Add(new Configuration(name, json.ToJsonString(FeatureHistory.JsonOptions)));
    }

    /// <summary>
    /// Stores the model's CURRENT parameter values under <paramref name="name"/> — the whole
    /// history, through <see cref="FeatureHistory.SaveParameters"/>.
    /// </summary>
    public Configuration Capture(string name) => Add(new Configuration(name, History.SaveParameters()));

    /// <summary>
    /// Stores the current values of the named features ONLY — a partial set, so a
    /// configuration that means "the M8 variant" states the bolt size and says nothing about
    /// the plate thickness a designer may still be tuning.
    /// </summary>
    public Configuration Capture(string name, IEnumerable<Feature> features)
    {
        ArgumentNullException.ThrowIfNull(features);
        var wanted = features.Select(History.NameOf).ToHashSet(StringComparer.Ordinal);
        var all = JsonNode.Parse(History.SaveParameters())!.AsObject();
        var json = new JsonObject();
        foreach (var entry in all)
        {
            if (wanted.Contains(entry.Key))
                json[entry.Key] = entry.Value?.DeepClone();
        }
        return Add(new Configuration(name, json.ToJsonString(FeatureHistory.JsonOptions)));
    }

    /// <summary>Removes a configuration by name. Removing the ACTIVE one clears
    /// <see cref="Active"/> — the model keeps its values, it just no longer claims to be
    /// anything.</summary>
    public bool Remove(string name)
    {
        int index = IndexOf(name);
        if (index < 0)
            return false;
        _items.RemoveAt(index);
        if (string.Equals(_active, name, StringComparison.Ordinal))
            _active = null;
        return true;
    }

    internal void Insert(int index, Configuration configuration) => _items.Insert(index, configuration);

    /// <summary>Sets the active NAME without applying anything — what the document loader
    /// does (the history already carries the values it was saved with, so re-applying would
    /// be a no-op that costs a regeneration) and what an undo does after restoring the
    /// previous values itself.</summary>
    internal void SetActiveName(string? name) => _active = name;

    // ---- applying -------------------------------------------------------

    /// <summary>
    /// Applies a configuration's values through
    /// <see cref="FeatureHistory.LoadParameters"/> and regenerates the part ONCE, then
    /// records it as <see cref="Active"/>.
    ///
    /// <para>One regeneration for the whole set, deliberately: composing this out of
    /// per-feature <c>DocumentEdits.SetParameter</c>s would rebuild once per feature, which
    /// is the same measurement that keeps a design study's <c>StudyResult.Edits</c> writing
    /// through this seam internally. <c>DocumentEdits.SetConfiguration</c> is the undoable wrapper —
    /// ONE edit, ONE rebuild.</para>
    ///
    /// <para>Warnings (an unknown feature, an unknown parameter) are REPORTED on the result,
    /// never thrown: a configuration that has outlived a feature is a data condition, and
    /// <see cref="Validate"/> is the pre-flight that says so without applying anything.</para>
    /// </summary>
    /// <exception cref="ArgumentException">No configuration of that name.</exception>
    public ConfigurationResult Activate(string name)
    {
        var configuration = Find(name) ?? throw new ArgumentException(
            $"Part '{_part.Name}' has no configuration named '{name}'"
            + $" (it has {(_items.Count == 0 ? "none" : string.Join(", ", Names))}).",
            nameof(name));
        var warnings = History.LoadParameters(configuration.Parameters);
        var regeneration = _part.Regenerate();
        _active = configuration.Name;
        return new ConfigurationResult(configuration.Name, regeneration.Succeeded, regeneration, warnings);
    }

    /// <summary>
    /// True when the model currently agrees with every value this configuration STATES. A
    /// partial set says nothing about the parameters it omits, so those are not compared.
    /// </summary>
    public bool Matches(Configuration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var current = JsonNode.Parse(History.SaveParameters())!.AsObject();
        foreach (var feature in configuration.Values)
        {
            if (current[feature.Key] is not JsonObject actual)
                return false;
            foreach (var parameter in (JsonObject)feature.Value!)
            {
                if (!actual.TryGetPropertyValue(parameter.Key, out var value)
                    || !JsonNode.DeepEquals(value, parameter.Value))
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// One message per value in this set that the history could not take — an unknown
    /// feature name, or a parameter the named feature does not have. The pre-flight for
    /// <see cref="Activate"/>, so a host can show a stale configuration WITHOUT applying it;
    /// empty means every configuration resolves.
    /// <para>A stale configuration is kept rather than dropped, deliberately: the feature it
    /// names may come back (an undone <c>RemoveFeature</c>), and a file that quietly loses a
    /// variant is worse than one that reports it.</para>
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var warnings = new List<string>();
        var features = History.Features.ToDictionary(History.NameOf, feature => feature, StringComparer.Ordinal);
        foreach (var configuration in _items)
        {
            foreach (var entry in configuration.Values)
            {
                if (!features.TryGetValue(entry.Key, out var feature))
                {
                    warnings.Add($"configuration '{configuration.Name}': unknown feature '{entry.Key}'");
                    continue;
                }
                var known = Feature.PropertiesOf(feature.GetType()).Select(p => p.Name).ToList();
                foreach (var parameter in (JsonObject)entry.Value!)
                {
                    if (!known.Contains(parameter.Key, StringComparer.Ordinal))
                    {
                        warnings.Add(
                            $"configuration '{configuration.Name}': "
                            + $"'{entry.Key}' has no [Param] named '{parameter.Key}'");
                    }
                }
            }
        }
        return warnings;
    }

    /// <inheritdoc />
    public override string ToString() =>
        _items.Count == 0
            ? "(no configurations)"
            : string.Join(", ", _items.Select(item =>
                string.Equals(item.Name, _active, StringComparison.Ordinal)
                    ? $"[{item.Name}]{(ActiveIsModified ? "*" : "")}"
                    : item.Name));
}
