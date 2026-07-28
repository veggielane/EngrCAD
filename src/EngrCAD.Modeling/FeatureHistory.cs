using System.Diagnostics;
using System.Text.Json;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

public enum FeatureOutcome
{
    Applied,
    Cached,
    Suppressed,
    Failed,
    Skipped,
}

public sealed record FeatureStatus(Feature Feature, string Name, FeatureOutcome Outcome, string? Error, TimeSpan Elapsed);

public sealed record RegenerationResult(IReadOnlyList<FeatureStatus> Statuses, Shape? Body)
{
    public bool Succeeded => Statuses.All(s => s.Outcome is not (FeatureOutcome.Failed or FeatureOutcome.Skipped));

    public override string ToString() => string.Join("\n",
        Statuses.Select(s => $"{s.Name}: {s.Outcome}"
            + (s.Error is null ? "" : $" — {s.Error}")
            + $" ({s.Elapsed.TotalMilliseconds:F0} ms)"));
}

/// <summary>
/// An ordered list of parametric features regenerated into a <see cref="Shape"/> —
/// the parametric model. Regeneration replays features with prefix caching (a feature
/// re-runs only when its parameters or anything upstream changed), validates
/// <c>[Param]</c> ranges first, stops at the first failure keeping the last good
/// body, and reports a per-feature status. Parameter values round-trip as JSON so a
/// design can be re-tuned without recompiling.
/// </summary>
public sealed class FeatureHistory
{
    private readonly List<Feature> _features = [];
    private readonly List<(string ChainKey, Shape Output)?> _cache = [];
    private RegenerationResult? _lastResult;

    public IReadOnlyList<Feature> Features => _features;

    public Shape? Result => _lastResult?.Body;

    public void Add(Feature feature)
    {
        _features.Add(feature);
        _cache.Add(null);
    }

    public void Insert(int index, Feature feature)
    {
        _features.Insert(index, feature);
        _cache.Insert(index, null);
    }

    public void RemoveAt(int index)
    {
        _features.RemoveAt(index);
        _cache.RemoveAt(index);
    }

    public void Replace(int index, Feature feature)
    {
        _features[index] = feature;
        _cache[index] = null;
    }

    /// <summary>Unique display name of a feature within this history ("Boss",
    /// "Boss.2", …) — the key used by the JSON parameter file.</summary>
    public string NameOf(Feature feature)
    {
        int duplicate = 0;
        foreach (var other in _features)
        {
            if (ReferenceEquals(other, feature))
                return duplicate == 0 ? feature.Name : $"{feature.Name}.{duplicate + 1}";
            if (other.Name == feature.Name)
                duplicate++;
        }
        throw new ArgumentException("The feature is not part of this history.", nameof(feature));
    }

    public RegenerationResult Regenerate()
    {
        var statuses = new List<FeatureStatus>(_features.Count);
        Shape? body = null;
        string chain = "";
        bool failed = false;

        for (int i = 0; i < _features.Count; i++)
        {
            var feature = _features[i];
            var name = NameOf(feature);
            if (failed)
            {
                statuses.Add(new FeatureStatus(feature, name, FeatureOutcome.Skipped, null, TimeSpan.Zero));
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            string key = chain + "||" + feature.Snapshot();

            if (feature.Suppressed)
            {
                chain = key;
                _cache[i] = null;
                statuses.Add(new FeatureStatus(feature, name, FeatureOutcome.Suppressed, null, stopwatch.Elapsed));
                continue;
            }

            if (_cache[i] is { } cached && cached.ChainKey == key)
            {
                body = cached.Output;
                chain = key;
                statuses.Add(new FeatureStatus(feature, name, FeatureOutcome.Cached, null, stopwatch.Elapsed));
                continue;
            }

            var violations = feature.Validate();
            if (violations.Count > 0)
            {
                _cache[i] = null;
                statuses.Add(new FeatureStatus(feature, name, FeatureOutcome.Failed,
                    string.Join("; ", violations), stopwatch.Elapsed));
                failed = true;
                continue;
            }

            // Declared geometry inputs resolve BEFORE Apply, against the same context, so
            // a broken reference is a named validation failure rather than an exception
            // from deep inside the operation — and Apply reuses the resolution.
            var context = new FeatureContext(body);
            IReadOnlyList<string> inputFailures;
            try
            {
                inputFailures = feature.ValidateInputs(context);
            }
            catch (Exception exception)
            {
                inputFailures = [$"{exception.GetType().Name}: {exception.Message}"];
            }
            if (inputFailures.Count > 0)
            {
                _cache[i] = null;
                statuses.Add(new FeatureStatus(feature, name, FeatureOutcome.Failed,
                    string.Join("; ", inputFailures), stopwatch.Elapsed));
                failed = true;
                continue;
            }

            try
            {
                var output = feature.Apply(context);
                _cache[i] = (key, output);
                body = output;
                chain = key;
                statuses.Add(new FeatureStatus(feature, name, FeatureOutcome.Applied, null, stopwatch.Elapsed));
            }
            catch (Exception exception)
            {
                _cache[i] = null;
                statuses.Add(new FeatureStatus(feature, name, FeatureOutcome.Failed,
                    $"{exception.GetType().Name}: {exception.Message}", stopwatch.Elapsed));
                failed = true;
            }
        }

        _lastResult = new RegenerationResult(statuses, body);
        return _lastResult;
    }

    /// <summary>
    /// The body as of feature <paramref name="index"/> — the shape the history had
    /// produced after applying features 0..index — or null when nothing has been
    /// regenerated that far. This is the rollback view the construction tree previews:
    /// a suppressed (or not-yet-run) step reports the last body that WAS produced,
    /// which is exactly the body it passed through untouched.
    /// </summary>
    public Shape? BodyAfter(int index)
    {
        if (index < 0 || index >= _cache.Count)
            return null;
        for (int i = index; i >= 0; i--)
        {
            if (_cache[i] is { } cached)
                return cached.Output;
        }
        return null;
    }

    /// <summary>Regenerates and wraps the result as a document-model part. The part
    /// keeps a reference to this history, so viewers can show the feature list
    /// (<see cref="Part.ConstructionTree"/>).</summary>
    public Part ToPart(string name, PartColor? color = null, Matrix4d? transform = null)
    {
        var result = Regenerate();
        if (result.Body is null)
            throw new InvalidOperationException($"The history produced no body:\n{result}");
        return new Part(name, result.Body, this, color, transform);
    }

    // ---- JSON parameter overrides ----

    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Parameter values as JSON: <c>{ "featureName": { "Param": value } }</c>.</summary>
    public string SaveParameters()
    {
        var document = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var feature in _features)
        {
            var values = new Dictionary<string, object?>();
            foreach (var parameter in feature.Parameters)
                values[parameter.Name] = SerializeValue(parameter.Value);
            if (values.Count > 0)
                document[NameOf(feature)] = values;
        }
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    /// <summary>A parameter value in its JSON form — one vocabulary shared by
    /// <see cref="SaveParameters"/> and <see cref="SaveHistory"/> so the two files
    /// cannot spell a value differently.</summary>
    internal static object? SerializeValue(object? value) => value switch
    {
        Vector2d v => new[] { v.X, v.Y },
        Vector3d v => new[] { v.X, v.Y, v.Z },
        Enum e => e.ToString(),
        // Descriptor in, descriptor out: a declarative reference round-trips,
        // and a lambda-backed one writes its opaque marker (which LoadParameters
        // then declines with a warning rather than rebuilding something wrong).
        GeometryRef reference => reference.Descriptor,
        var other => other,
    };

    /// <summary>Applies JSON parameter overrides (reflection handles init-only
    /// setters). Returns warnings for unknown features/parameters or bad values
    /// instead of throwing; changed values invalidate the affected cache prefix
    /// automatically via the snapshot keys.</summary>
    public IReadOnlyList<string> LoadParameters(string json)
    {
        var warnings = new List<string>();
        using var document = JsonDocument.Parse(json);
        foreach (var featureEntry in document.RootElement.EnumerateObject())
        {
            var feature = _features.FirstOrDefault(f => NameOf(f) == featureEntry.Name);
            if (feature is null)
            {
                warnings.Add($"unknown feature '{featureEntry.Name}'");
                continue;
            }
            ApplyParameters(feature, featureEntry.Value, featureEntry.Name, warnings);
        }
        return warnings;
    }

    /// <summary>Sets <c>[Param]</c> properties from a JSON object, warning per entry it
    /// cannot apply — shared by <see cref="LoadParameters"/> and
    /// <see cref="LoadHistory"/>.</summary>
    internal static void ApplyParameters(
        Feature feature, JsonElement values, string featureName, List<string> warnings)
    {
        var properties = Feature.PropertiesOf(feature.GetType());
        foreach (var valueEntry in values.EnumerateObject())
        {
            var property = properties.FirstOrDefault(p => p.Name == valueEntry.Name);
            if (property is null)
            {
                warnings.Add($"unknown parameter '{featureName}.{valueEntry.Name}'");
                continue;
            }
            try
            {
                property.SetValue(feature, Convert(valueEntry.Value, property.PropertyType));
            }
            catch (Exception exception)
            {
                warnings.Add($"could not set '{featureName}.{valueEntry.Name}': {exception.Message}");
            }
        }
    }

    private static object? Convert(JsonElement element, Type type)
    {
        if (type == typeof(double))
            return element.GetDouble();
        if (type == typeof(float))
            return (float)element.GetDouble();
        if (type == typeof(int))
            return element.GetInt32();
        if (type == typeof(long))
            return element.GetInt64();
        if (type == typeof(bool))
            return element.GetBoolean();
        if (type == typeof(string))
            return element.GetString();
        if (type.IsEnum)
            return Enum.Parse(type, element.GetString()
                ?? throw new FormatException("enum values are strings"), ignoreCase: true);
        if (type == typeof(Vector2d))
            return new Vector2d(element[0].GetDouble(), element[1].GetDouble());
        if (type == typeof(Vector3d))
            return new Vector3d(element[0].GetDouble(), element[1].GetDouble(), element[2].GetDouble());
        if (typeof(GeometryRef).IsAssignableFrom(type))
            return GeometryRef.Parse(element.GetString()
                ?? throw new FormatException("geometry references are descriptor strings"), type);
        throw new FormatException($"unsupported parameter type {type.Name}");
    }

    // ---- whole-history persistence (the FeatureRegistry side) ----

    /// <summary>
    /// The WHOLE history as JSON — ordered feature records with type, name, suppression,
    /// serialized constructor inputs (<see cref="Feature.SaveInputs"/>) and
    /// <c>[Param]</c> values (the same value vocabulary as <see cref="SaveParameters"/>).
    /// <see cref="LoadHistory"/> reconstructs it through a <see cref="FeatureRegistry"/>.
    /// Features whose inputs have no serialized form (lambdas, <see cref="Shape"/>
    /// tools, catalogue components) are still WRITTEN — type, name and parameters — so
    /// the file is an honest record; they come back only through the load hook.
    /// </summary>
    public string SaveHistory()
    {
        var features = new System.Text.Json.Nodes.JsonArray();
        foreach (var feature in _features)
        {
            var record = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = feature.GetType().Name,
                ["name"] = feature.Name,
            };
            if (feature.Suppressed)
                record["suppressed"] = true;
            if (feature.SaveInputs() is { } inputs)
                record["inputs"] = inputs;
            var parameters = new System.Text.Json.Nodes.JsonObject();
            foreach (var parameter in feature.Parameters)
                parameters[parameter.Name] =
                    JsonSerializer.SerializeToNode(SerializeValue(parameter.Value), JsonOptions);
            if (parameters.Count > 0)
                record["parameters"] = parameters;
            features.Add(record);
        }
        return new System.Text.Json.Nodes.JsonObject { ["features"] = features }
            .ToJsonString(JsonOptions);
    }

    /// <summary>
    /// Rebuilds a history from <see cref="SaveHistory"/> JSON. Each record is
    /// constructed through <paramref name="registry"/> (default:
    /// <see cref="FeatureRegistry.Default"/>), its name and suppression restored, and
    /// its <c>[Param]</c> values applied through the same conversion vocabulary as
    /// <see cref="LoadParameters"/> — so geometry references re-resolve per
    /// regeneration exactly as saved ones do.
    /// <para>A record the registry cannot construct (see
    /// <see cref="FeatureTypeInfo.Reason"/>) is offered to
    /// <paramref name="resolveOpaque"/> — the caller's chance to supply the instance
    /// (rebuild the lambda, reference the catalogue component). A null return SKIPS the
    /// feature with a warning naming it, and the result's
    /// <see cref="HistoryLoadResult.Complete"/> goes false: downstream features then
    /// see a different upstream body than the saved model did, which the caller must
    /// judge.</para>
    /// </summary>
    public static HistoryLoadResult LoadHistory(
        string json,
        FeatureRegistry? registry = null,
        Func<FeatureRecord, Feature?>? resolveOpaque = null)
    {
        registry ??= FeatureRegistry.Default;
        var history = new FeatureHistory();
        var warnings = new List<string>();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("features", out var array)
            || array.ValueKind != JsonValueKind.Array)
            throw new FormatException("A saved history is an object with a 'features' array.");

        foreach (var element in array.EnumerateArray())
        {
            string typeName = element.GetProperty("type").GetString()
                ?? throw new FormatException("feature records carry a 'type' string");
            string name = element.GetProperty("name").GetString() ?? typeName;
            bool suppressed = element.TryGetProperty("suppressed", out var s) && s.GetBoolean();
            JsonElement? inputs = element.TryGetProperty("inputs", out var i) ? i.Clone() : null;
            JsonElement? parameters = element.TryGetProperty("parameters", out var p) ? p.Clone() : null;
            var record = new FeatureRecord(typeName, name, suppressed, inputs, parameters);

            Feature? feature;
            if (!registry.TryCreate(typeName, inputs, out feature, out string? error))
            {
                feature = resolveOpaque?.Invoke(record);
                if (feature is null)
                {
                    warnings.Add($"skipped '{name}' ({typeName}): {error}");
                    continue;
                }
            }

            NameProperty.SetValue(feature, name);
            feature!.Suppressed = suppressed;
            if (parameters is { } values)
                ApplyParameters(feature, values, name, warnings);
            history.Add(feature);
        }
        return new HistoryLoadResult(history, warnings);
    }

    // Name is init-only; reflection writes it the same way LoadParameters writes
    // init-only [Param] setters.
    private static readonly System.Reflection.PropertyInfo NameProperty =
        typeof(Feature).GetProperty(nameof(Feature.Name))!;
}

/// <summary>One saved feature as raw data — what <see cref="FeatureHistory.LoadHistory"/>
/// hands to the caller's resolve hook when the registry cannot construct the type.</summary>
public sealed record FeatureRecord(
    string TypeName, string Name, bool Suppressed, JsonElement? Inputs, JsonElement? Parameters);

/// <summary>Result of <see cref="FeatureHistory.LoadHistory"/>: the rebuilt history plus
/// one warning per record that could not be fully restored.</summary>
public sealed record HistoryLoadResult(FeatureHistory History, IReadOnlyList<string> Warnings)
{
    /// <summary>True when every saved feature was reconstructed.</summary>
    public bool Complete => Warnings.Count == 0;
}
