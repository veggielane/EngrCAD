using System.Text.Json;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// Mate persistence, following <see cref="FeatureHistory.SaveParameters"/>'s
/// conventions: JSON out, warnings (never exceptions) back in for anything the model
/// no longer matches.
///
/// <para>Each mate end saves its occurrence <b>path</b>, its <b>pinned coordinates</b>
/// (the point/direction the mate actually solved with — a mate is a numerical
/// constraint, so the numbers are always the truth), and, when the end was built from
/// a typed <see cref="GeometryRef"/> query, the <b>query descriptor</b>
/// ("cylindricalFace(one(cylindrical))"). Loading re-resolves the query against the
/// part <em>eagerly</em> — construction time is load time, the same pin-at-construction
/// rule as <see cref="MateGeometry"/> — and falls back to the pinned coordinates with a
/// warning when the query no longer resolves. A lambda-backed selector saves its opaque
/// marker, which loading declines by design (the same contract as
/// <see cref="FeatureHistory.LoadParameters"/> for opaque references): the coordinates
/// still load, so the mate keeps working, it just stops being semantic.</para>
/// </summary>
public sealed partial class MateSet
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>This mate set as JSON: grounded occurrence paths plus one entry per
    /// mate (kind, name, value, and both ends per the class notes). Angle values are
    /// stored in radians — the internal representation — so no unit conversion
    /// re-enters on load.</summary>
    public string SaveMates()
    {
        var document = new Dictionary<string, object?>
        {
            ["grounded"] = GroundedPaths(),
            ["mates"] = _mates.Select(SaveMate).ToArray(),
        };
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    /// <summary>The grounded occurrences as sorted paths — a stable file, whatever the
    /// set order. Shared with mechanism persistence so the two files spell grounds
    /// identically.</summary>
    internal string[] GroundedPaths() => _groundedPaths
        .OrderBy(p => p, StringComparer.Ordinal)
        .ToArray();

    /// <summary>Resolves and adds one saved ground path, warning instead of throwing —
    /// the shared half of <see cref="LoadMates"/> mechanism persistence also reads.</summary>
    internal void LoadGround(string path, List<string> warnings)
    {
        try
        {
            Ground(path);
        }
        catch (ArgumentException exception)
        {
            warnings.Add($"ground '{path}': {exception.Message}");
        }
    }

    /// <summary>
    /// Adds the grounds and mates a <see cref="SaveMates"/> file describes to this set,
    /// resolving occurrence paths against <see cref="Assembly"/> and re-resolving
    /// queries against the parts. Returns warnings for anything that no longer matches
    /// (missing occurrences, queries that fail, opaque markers) instead of throwing —
    /// a mate whose path resolves always loads, from its query if possible and from its
    /// pinned coordinates otherwise.
    /// </summary>
    public IReadOnlyList<string> LoadMates(string json)
    {
        var warnings = new List<string>();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("grounded", out var grounded))
        {
            foreach (var entry in grounded.EnumerateArray())
                LoadGround(entry.GetString() ?? "", warnings);
        }

        if (!root.TryGetProperty("mates", out var mates))
            return warnings;
        foreach (var entry in mates.EnumerateArray())
            LoadMateEntry(entry, warnings);
        return warnings;
    }

    /// <summary>Loads one saved mate entry into this set, warning instead of throwing —
    /// the per-mate half of <see cref="LoadMates"/>, shared with mechanism persistence
    /// so the mate vocabulary stays here.</summary>
    internal void LoadMateEntry(JsonElement entry, List<string> warnings)
    {
        string name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? "mate" : "mate";
        try
        {
            var kind = Enum.Parse<MateKind>(entry.GetProperty("kind").GetString()
                ?? throw new FormatException("mate kinds are strings"));
            double value = entry.TryGetProperty("value", out var v) ? v.GetDouble() : 0;
            var a = LoadEnd(entry.GetProperty("a"), $"mate '{name}'", "A", warnings);
            var b = LoadEnd(entry.GetProperty("b"), $"mate '{name}'", "B", warnings);
            if (a is not { } endA || b is not { } endB)
                return;   // the end's warning already says why
            Add(Mate.Restore(kind, endA, endB, value, name));
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException or KeyNotFoundException
                      or InvalidOperationException)
        {
            warnings.Add($"mate '{name}': {exception.Message}");
        }
    }

    internal Dictionary<string, object?> SaveMate(Mate mate)
    {
        var entry = new Dictionary<string, object?>
        {
            ["kind"] = mate.Kind.ToString(),
            ["name"] = mate.Name,
        };
        // Exact-zero semantic test: 0 is "no parameter" for every kind that has one.
        if (mate.Value != 0)
            entry["value"] = mate.Value;
        entry["a"] = SaveEnd(mate.A);
        entry["b"] = SaveEnd(mate.B);
        return entry;
    }

    internal static Dictionary<string, object?> SaveEnd(in MateRef reference)
    {
        var end = new Dictionary<string, object?>();
        if (reference.Path is { } path)
            end["path"] = path;
        end["point"] = new[] { reference.Point.X, reference.Point.Y, reference.Point.Z };
        if (reference.Direction != Vector3d.Zero)
        {
            var d = reference.Direction;
            end["direction"] = new[] { d.X, d.Y, d.Z };
        }
        if (reference.Query is { } query)
            end["query"] = query;
        return end;
    }

    /// <param name="element">The saved end.</param>
    /// <param name="owner">The owning record for messages ("mate 'lid hinge'",
    /// "joint 'crank pin'") — mechanism persistence loads joint ends through here too.</param>
    /// <param name="label">Which end ("A"/"B").</param>
    /// <param name="warnings">Accumulates the not-thrown failures.</param>
    internal MateRef? LoadEnd(JsonElement element, string owner, string label, List<string> warnings)
    {
        var point = ReadVector(element, "point");
        var direction = ReadVector(element, "direction");

        IReadOnlyList<Occurrence>? chain = null;
        if (element.TryGetProperty("path", out var pathElement))
        {
            string path = pathElement.GetString() ?? "";
            try
            {
                chain = Assembly.ResolvePath(path);
            }
            catch (ArgumentException exception)
            {
                // No occurrence, no mate end: unlike a failed query there is nothing to
                // pin the coordinates TO, so the whole mate is skipped.
                warnings.Add($"{owner} end {label}: {exception.Message}");
                return null;
            }
        }

        if (chain is not null && element.TryGetProperty("query", out var queryElement))
        {
            string query = queryElement.GetString() ?? "";
            try
            {
                return ResolveQuery(chain, query);
            }
            catch (Exception exception) when (
                exception is FormatException or ArgumentException or GeometryInputException)
            {
                warnings.Add(
                    $"{owner} end {label}: could not re-resolve '{query}' " +
                    $"({exception.Message}); loaded from its pinned coordinates");
            }
        }

        return chain is null
            ? new MateRef((Occurrence?)null, point, direction)
            : new MateRef(chain, point, direction);
    }

    /// <summary>Rebuilds a query-backed end: the wrapper names the resolution rule
    /// (which <see cref="MateGeometry"/> builder ran) and the inside is the
    /// <see cref="GeometryRef"/> descriptor it ran with.</summary>
    private static MateRef ResolveQuery(IReadOnlyList<Occurrence> chain, string query)
    {
        int open = query.IndexOf('(');
        if (open < 0 || !query.EndsWith(')'))
            throw new FormatException($"unrecognized mate query '{query}'");
        string wrapper = query[..open];
        string inner = query[(open + 1)..^1];
        if (inner.StartsWith("opaque", StringComparison.Ordinal))
            throw new FormatException("the query was a lambda, which only its code can rebuild");
        return wrapper switch
        {
            "planarFace" => MateGeometry.PlanarFace(chain, FaceRef.Parse(inner)),
            "cylindricalFace" => MateGeometry.CylindricalFace(chain, FaceRef.Parse(inner)),
            "axisRef" => MateGeometry.Axis(chain, AxisRef.Parse(inner)),
            _ => throw new FormatException($"unknown mate query kind '{wrapper}'"),
        };
    }

    private static Vector3d ReadVector(JsonElement element, string property) =>
        element.TryGetProperty(property, out var vector)
            ? new Vector3d(vector[0].GetDouble(), vector[1].GetDouble(), vector[2].GetDouble())
            : Vector3d.Zero;
}
