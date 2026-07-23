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

            try
            {
                var output = feature.Apply(new FeatureContext(body));
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

    /// <summary>Regenerates and wraps the result as a document-model part.</summary>
    public Part ToPart(string name, PartColor? color = null, Matrix4d? transform = null)
    {
        var result = Regenerate();
        if (result.Body is null)
            throw new InvalidOperationException($"The history produced no body:\n{result}");
        return new Part(name, result.Body, color, transform);
    }

    // ---- JSON parameter overrides ----

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Parameter values as JSON: <c>{ "featureName": { "Param": value } }</c>.</summary>
    public string SaveParameters()
    {
        var document = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var feature in _features)
        {
            var values = new Dictionary<string, object?>();
            foreach (var parameter in feature.Parameters)
            {
                values[parameter.Name] = parameter.Value switch
                {
                    Vector2d v => (object)new[] { v.X, v.Y },
                    Vector3d v => new[] { v.X, v.Y, v.Z },
                    Enum e => e.ToString(),
                    var other => other,
                };
            }
            if (values.Count > 0)
                document[NameOf(feature)] = values;
        }
        return JsonSerializer.Serialize(document, JsonOptions);
    }

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
            var properties = Feature.PropertiesOf(feature.GetType());
            foreach (var valueEntry in featureEntry.Value.EnumerateObject())
            {
                var property = properties.FirstOrDefault(p => p.Name == valueEntry.Name);
                if (property is null)
                {
                    warnings.Add($"unknown parameter '{featureEntry.Name}.{valueEntry.Name}'");
                    continue;
                }
                try
                {
                    property.SetValue(feature, Convert(valueEntry.Value, property.PropertyType));
                }
                catch (Exception exception)
                {
                    warnings.Add($"could not set '{featureEntry.Name}.{valueEntry.Name}': {exception.Message}");
                }
            }
        }
        return warnings;
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
        throw new FormatException($"unsupported parameter type {type.Name}");
    }
}
