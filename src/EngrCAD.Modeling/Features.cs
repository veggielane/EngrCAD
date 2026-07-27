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

/// <summary>
/// Marks a geometry input that is handed to a <em>deferred</em> selector — one the
/// <see cref="Shape"/> graph resolves later, at lowering time (the rim operations'
/// face queries). Up-front validation skips it on purpose: resolving it early would
/// force a B-Rep lowering that regeneration otherwise never pays for, and the deferred
/// resolution already names the input when it fails (see
/// <c>FaceSetRef.AsSelector</c>). Cache-key and serialization behaviour are unaffected.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DeferredInputAttribute : Attribute;

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
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> GeometryInputProperties = new();

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

    /// <summary>Public properties whose type is a <see cref="GeometryRef"/> and which are
    /// not <see cref="DeferredInputAttribute">deferred</see> — the inputs up-front
    /// validation resolves.</summary>
    internal static PropertyInfo[] GeometryInputsOf(Type type) =>
        GeometryInputProperties.GetOrAdd(type, t =>
            [.. t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead
                    && typeof(GeometryRef).IsAssignableFrom(p.PropertyType)
                    && p.GetCustomAttribute<DeferredInputAttribute>() is null)]);

    /// <summary>
    /// Resolves every declared geometry input against <paramref name="context"/> BEFORE
    /// <see cref="Apply"/> runs, returning one message per input that could not be
    /// resolved (empty = valid). Resolution is all-or-nothing: the feature does not run
    /// unless every input answered, and each resolution is cached on the context, so
    /// <c>Apply</c> reuses it rather than querying the body a second time.
    ///
    /// <para>Override to add checks of your own; call <c>base.ValidateInputs(context)</c>
    /// first so the declared references are still resolved (and cached).</para>
    /// </summary>
    public virtual IReadOnlyList<string> ValidateInputs(FeatureContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<string>? failures = null;
        foreach (var property in GeometryInputsOf(GetType()))
        {
            if (property.GetValue(this) is not GeometryRef reference)
                continue;
            if (!context.TryResolveInput(reference, property.Name, out string? error))
                (failures ??= []).Add(error!);
        }
        return failures ?? (IReadOnlyList<string>)[];
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
    private readonly Dictionary<GeometryRef, (string Name, object? Value, string? Error)> _resolved = new();
    private BrepSolid? _lowered;

    public Shape? Body { get; }

    internal FeatureContext(Shape? body) => Body = body;

    /// <summary>The body lowered to B-Rep (cached) — the target for selector queries.
    /// Selector-based references are this kernel's topological naming: semantic
    /// queries re-run against the regenerated body instead of persisting indices.</summary>
    public BrepSolid Lowered => _lowered ??= (Body
        ?? throw new InvalidOperationException("No body exists yet — this is the first feature.")).ToBrep();

    /// <summary>A sketch plane on the highest +Z planar face (drilling/sketching on top) —
    /// <see cref="PlaneRef.TopPlane"/> resolved against this context, and the same
    /// world-axis-aligned origin convention it has always had.</summary>
    public SketchPlane TopPlane => PlaneRef.TopPlane.Resolve(this, nameof(TopPlane));

    /// <summary>
    /// Resolves a geometry reference against this context, computing it at most once per
    /// context (and replaying a failure verbatim). The cache is keyed on the reference
    /// INSTANCE and lives only as long as this context — a reference never memoizes a
    /// resolution across regenerations, which is what makes selector-based references
    /// track an edited model.
    /// </summary>
    internal object ResolveInput(GeometryRef reference, string? inputName)
    {
        if (_resolved.TryGetValue(reference, out var entry))
        {
            return entry.Error is null
                ? entry.Value!
                : throw new GeometryInputException(entry.Error);
        }

        string name = inputName ?? "geometry input";
        var value = Resolve(reference, name);
        _resolved[reference] = (name, value, null);
        return value;
    }

    /// <summary>Resolve-and-remember for up-front validation: a failure becomes a message
    /// (cached, so <c>Apply</c> cannot accidentally retry it) instead of an exception.</summary>
    internal bool TryResolveInput(GeometryRef reference, string inputName, out string? error)
    {
        if (_resolved.TryGetValue(reference, out var entry))
        {
            error = entry.Error;
            return error is null;
        }

        try
        {
            _resolved[reference] = (inputName, Resolve(reference, inputName), null);
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            error = exception is GeometryInputException
                ? exception.Message
                : $"{inputName}: {exception.GetType().Name}: {exception.Message}";
            _resolved[reference] = (inputName, null, error);
            return false;
        }
    }

    /// <summary>The body is lowered only when the reference actually needs it — an
    /// explicit plane or axis resolves before any geometry exists, and a feature that
    /// declares none never pays for a lowering at all.</summary>
    private object Resolve(GeometryRef reference, string inputName)
    {
        if (!reference.RequiresBody)
            return reference.ResolveAgainst(null, inputName);
        if (Body is null)
            throw new GeometryInputException(
                $"{inputName}: expected {reference.Subject}, but no body exists yet — this is the first feature.");
        return reference.ResolveAgainst(Lowered, inputName);
    }
}
