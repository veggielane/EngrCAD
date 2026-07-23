using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>Marks a property as a feature parameter: reflectable metadata for
/// validation, caching, JSON overrides, and (eventually) property-panel editing.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ParamAttribute : Attribute
{
    public double Min { get; set; } = double.NegativeInfinity;
    public double Max { get; set; } = double.PositiveInfinity;
    public string? Units { get; set; }
    public string? Description { get; set; }
}

/// <summary>Metadata + current value of one feature parameter.</summary>
public sealed record ParamInfo(
    string Name, Type Type, object? Value,
    double Min, double Max, string? Units, string? Description);

/// <summary>
/// A parametric modeling step, FeatureScript-style but in plain C#: declare
/// <see cref="ParamAttribute"/> properties, implement <see cref="Apply"/> as a pure
/// function of the parameters and the incoming context, and compose instances into a
/// <see cref="FeatureHistory"/>. Purity matters: regeneration caches outputs by
/// parameter values, so bodies must not depend on hidden mutable state.
/// </summary>
public abstract class Feature
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> ParamProperties = new();

    private readonly string? _name;

    /// <summary>Display name; defaults to the type name. Histories de-duplicate with
    /// ".2"-style suffixes; the resolved name keys JSON parameter files.</summary>
    public string Name
    {
        get => _name ?? GetType().Name;
        init => _name = value;
    }

    /// <summary>Suppressed features pass the body through untouched.</summary>
    public bool Suppressed { get; init; }

    public abstract Shape Apply(FeatureContext context);

    internal static PropertyInfo[] PropertiesOf(Type type) =>
        ParamProperties.GetOrAdd(type, t =>
            [.. t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<ParamAttribute>() is not null && p.CanRead)]);

    public IReadOnlyList<ParamInfo> Parameters =>
        [.. PropertiesOf(GetType()).Select(p =>
        {
            var attribute = p.GetCustomAttribute<ParamAttribute>()!;
            return new ParamInfo(p.Name, p.PropertyType, p.GetValue(this),
                attribute.Min, attribute.Max, attribute.Units, attribute.Description);
        })];

    /// <summary>Cache key: instance identity + parameter values + suppression. Same
    /// instance with unchanged parameters ⇒ cached; a fresh instance always re-runs
    /// (safe for non-parameter inputs like sketches, hole specs, and selectors).</summary>
    internal string Snapshot()
    {
        var parts = new List<string>
        {
            GetType().FullName ?? GetType().Name,
            RuntimeHelpers.GetHashCode(this).ToString(CultureInfo.InvariantCulture),
            Suppressed ? "suppressed" : "active",
        };
        foreach (var property in PropertiesOf(GetType()))
            parts.Add($"{property.Name}={FormatValue(property.GetValue(this))}");
        return string.Join("|", parts);
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        Vector2d v => $"({v.X:R},{v.Y:R})",
        Vector3d v => $"({v.X:R},{v.Y:R},{v.Z:R})",
        _ => value.ToString() ?? "?",
    };

    /// <summary>Range violations of the current parameter values (empty = valid).</summary>
    internal IReadOnlyList<string> Validate()
    {
        var violations = new List<string>();
        foreach (var property in PropertiesOf(GetType()))
        {
            var attribute = property.GetCustomAttribute<ParamAttribute>()!;
            double? numeric = property.GetValue(this) switch
            {
                double d => d,
                float f => f,
                int i => i,
                long l => l,
                _ => null,
            };
            if (numeric is { } n && (n < attribute.Min || n > attribute.Max))
                violations.Add(
                    $"{property.Name} = {n.ToString(CultureInfo.InvariantCulture)} is outside [{attribute.Min}, {attribute.Max}]");
        }
        return violations;
    }

    /// <summary>One-off feature from a lambda (no declared parameters — a fresh
    /// instance per history build, so it re-runs whenever upstream changes).</summary>
    public static Feature FromFunc(string name, Func<FeatureContext, Shape> apply) =>
        new FuncFeature(apply) { Name = name };

    private sealed class FuncFeature(Func<FeatureContext, Shape> apply) : Feature
    {
        public override Shape Apply(FeatureContext context) => apply(context);
    }
}

/// <summary>The model state a feature sees: the body so far (null before the first
/// feature), a lazily lowered B-Rep for <c>BrepQueries</c> selectors, and plane
/// conveniences.</summary>
public sealed class FeatureContext
{
    private BrepSolid? _lowered;

    public Shape? Body { get; }

    internal FeatureContext(Shape? body) => Body = body;

    /// <summary>The body lowered to B-Rep (cached) — the target for selector queries.
    /// Selector-based references are this kernel's topological naming: semantic
    /// queries re-run against the regenerated body instead of persisting indices.</summary>
    public BrepSolid Lowered => _lowered ??= (Body
        ?? throw new InvalidOperationException("No body exists yet — this is the first feature.")).ToBrep();

    /// <summary>A sketch plane on the highest +Z planar face (drilling/sketching on top).</summary>
    public SketchPlane TopPlane
    {
        get
        {
            var top = Lowered.PlanarFacesWithNormal(Vector3d.UnitZ)
                .OrderByDescending(f => { f.IsPlanar(out var origin, out _); return origin.Z; })
                .FirstOrDefault()
                ?? throw new InvalidOperationException("The body has no upward-facing planar face.");
            top.IsPlanar(out var faceOrigin, out _);
            return SketchPlane.At((0, 0, faceOrigin.Z), Vector3d.UnitX, Vector3d.UnitY);
        }
    }
}
