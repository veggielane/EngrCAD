using System.Text.Json.Nodes;
using EngrCAD.Core;

namespace EngrCAD.Ecad;

public sealed partial class ConstrainedLayout
{
    /// <summary>
    /// Serializes the constrained layout, extending the stage-2 layout seam
    /// (<see cref="PcbLayout.Save"/>) with a <c>constraints</c> array. Write-only-when-stated: a
    /// layout with NO constraints emits the stage-2 bytes verbatim (no <c>constraints</c> key), so
    /// it is byte-identical to <c>Layout.Save()</c>; a constrained one is a <c>save → load → save</c>
    /// byte-identical fixed point.
    /// </summary>
    public string Save()
    {
        var root = PcbLayoutWriter.Write(Layout);
        if (_constraints.Count > 0)
        {
            var array = new JsonArray();
            foreach (var constraint in _constraints)
                array.Add(PcbConstraintJson.Save(constraint));
            root["constraints"] = array;
        }
        return root.ToJsonString(EcadJson.Options);
    }

    /// <summary>Rebuilds a constrained layout from <see cref="Save"/> JSON.</summary>
    /// <param name="json">The saved JSON.</param>
    /// <param name="library">An optional library re-attaching 3D bodies on the embedded schematic.</param>
    /// <exception cref="FormatException">The JSON is not an EngrCAD layout, or a constraint names a
    /// placement the layout does not place — refused by name.</exception>
    public static ConstrainedLayout Load(string json, PartLibrary? library = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        var layout = PcbLayout.Load(json, library);
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new FormatException("A saved constrained layout is a JSON object.");

        var constraints = new List<PcbConstraint>();
        if (root.TryGetPropertyValue("constraints", out var node) && node is JsonArray array)
            foreach (var element in array)
                constraints.Add(PcbConstraintJson.Read(EcadJson.Object(element, "a constraint")));
        return new ConstrainedLayout(layout, constraints);
    }

    /// <summary>Writes this constrained layout to a file (see <see cref="Save"/>).</summary>
    public void SaveFile(string path) => File.WriteAllText(path, Save());

    /// <summary>Reads a constrained layout from a file (see <see cref="Load"/>).</summary>
    public static ConstrainedLayout LoadFile(string path, PartLibrary? library = null) =>
        Load(File.ReadAllText(path), library);
}

/// <summary>The JSON form of each placement constraint — one <c>kind</c> plus its fields, in a
/// fixed order so <c>save → load → save</c> is a byte-identical fixed point. Every value the
/// constraint captured (a signed offset, an <c>AlignEdge</c> side) rides as data, so a reload
/// reproduces the exact constraint rather than recomputing a branch selector.</summary>
internal static class PcbConstraintJson
{
    internal static JsonObject Save(PcbConstraint constraint) => constraint switch
    {
        LockConstraint c => new JsonObject { ["kind"] = "lock", ["ref"] = c.Reference },
        GroupConstraint c => new JsonObject { ["kind"] = "group", ["refs"] = Refs(c.References) },
        FixRotationConstraint c => new JsonObject { ["kind"] = "fixRotation", ["ref"] = c.Reference },
        OrientConstraint c => new JsonObject
        {
            ["kind"] = "orient", ["ref"] = c.Reference, ["degrees"] = c.Degrees,
        },
        CoincidentConstraint c => new JsonObject
        {
            ["kind"] = "coincident", ["a"] = SavePoint(c.A), ["b"] = SavePoint(c.B),
        },
        DistanceConstraint c => new JsonObject
        {
            ["kind"] = "distance", ["a"] = SavePoint(c.A), ["b"] = SavePoint(c.B), ["gap"] = c.Gap,
        },
        AlignConstraint c => new JsonObject
        {
            ["kind"] = "align", ["a"] = SavePoint(c.A), ["b"] = SavePoint(c.B),
            ["axis"] = c.ShareX ? "X" : "Y",
        },
        DirectionPairConstraint c => new JsonObject
        {
            ["kind"] = c.Parallel ? "parallel" : "perpendicular",
            ["a"] = SaveDir(c.A), ["b"] = SaveDir(c.B),
        },
        PointOnLineConstraint c => new JsonObject
        {
            ["kind"] = "pointOnLine", ["point"] = SavePoint(c.Point),
            ["linePoint"] = SavePoint(c.LinePoint), ["lineDir"] = SaveDir(c.LineDirection),
            ["offset"] = c.Offset,
        },
        AlignEdgeConstraint c => new JsonObject
        {
            ["kind"] = "alignEdge", ["component"] = SaveLine(c.Component), ["target"] = SaveLine(c.Target),
            ["gap"] = c.Gap, ["side"] = c.Side,
        },
        InsideRegionConstraint c => new JsonObject
        {
            ["kind"] = "insideRegion", ["ref"] = c.Reference, ["poly"] = SavePoly(c.Polygon),
            ["margin"] = c.Margin,
        },
        ClearOfConstraint c => new JsonObject
        {
            ["kind"] = "clearOf", ["a"] = c.A, ["b"] = c.B, ["distance"] = c.Distance,
        },
        ClearOfRegionConstraint c => new JsonObject
        {
            ["kind"] = "clearOfRegion", ["ref"] = c.Reference, ["poly"] = SavePoly(c.Polygon),
            ["distance"] = c.Distance,
        },
        _ => throw new InvalidOperationException(
            $"No JSON form for constraint kind {constraint.GetType().Name}."),
    };

    internal static PcbConstraint Read(JsonObject o)
    {
        string kind = EcadJson.String(o, "kind");
        return kind switch
        {
            "lock" => new LockConstraint(EcadJson.String(o, "ref")),
            "group" => new GroupConstraint(ReadRefs(o, "refs")),
            "fixRotation" => new FixRotationConstraint(EcadJson.String(o, "ref")),
            "orient" => new OrientConstraint(EcadJson.String(o, "ref"), EcadJson.Double(o, "degrees")),
            "coincident" => new CoincidentConstraint(ReadPoint(o, "a"), ReadPoint(o, "b")),
            "distance" => new DistanceConstraint(ReadPoint(o, "a"), ReadPoint(o, "b"), EcadJson.Double(o, "gap")),
            "align" => new AlignConstraint(
                ReadPoint(o, "a"), ReadPoint(o, "b"), EcadJson.String(o, "axis") == "X"),
            "parallel" => new DirectionPairConstraint(ReadDir(o, "a"), ReadDir(o, "b"), parallel: true),
            "perpendicular" => new DirectionPairConstraint(ReadDir(o, "a"), ReadDir(o, "b"), parallel: false),
            "pointOnLine" => new PointOnLineConstraint(
                ReadPoint(o, "point"), ReadPoint(o, "linePoint"), ReadDir(o, "lineDir"),
                EcadJson.Double(o, "offset")),
            "alignEdge" => new AlignEdgeConstraint(
                ReadLine(o, "component"), ReadLine(o, "target"),
                EcadJson.Double(o, "gap"), EcadJson.Double(o, "side")),
            "insideRegion" => new InsideRegionConstraint(
                EcadJson.String(o, "ref"), ReadPoly(o, "poly"), EcadJson.Double(o, "margin")),
            "clearOf" => new ClearOfConstraint(
                EcadJson.String(o, "a"), EcadJson.String(o, "b"), EcadJson.Double(o, "distance")),
            "clearOfRegion" => new ClearOfRegionConstraint(
                EcadJson.String(o, "ref"), ReadPoly(o, "poly"), EcadJson.Double(o, "distance")),
            _ => throw new FormatException($"Unknown constraint kind '{kind}'."),
        };
    }

    // ---- reference forms ----------------------------------------------------

    private static JsonObject SavePoint(in PlacementPoint p)
    {
        var record = new JsonObject();
        if (p.Reference is not null)   // write-only-when-stated: a board-fixed point omits the ref
            record["ref"] = p.Reference;
        record["x"] = p.Local.X;
        record["y"] = p.Local.Y;
        return record;
    }

    private static PlacementPoint ReadPoint(JsonObject parent, string key)
    {
        var o = EcadJson.Object(parent[key], $"the '{key}' point");
        string? reference = o["ref"]?.GetValue<string>();
        return new PlacementPoint(reference, new Vector2d(EcadJson.Double(o, "x"), EcadJson.Double(o, "y")));
    }

    private static JsonObject SaveDir(in PlacementDirection d)
    {
        var record = new JsonObject();
        if (d.Reference is not null)
            record["ref"] = d.Reference;
        record["dx"] = d.Local.X;
        record["dy"] = d.Local.Y;
        return record;
    }

    private static PlacementDirection ReadDir(JsonObject parent, string key)
    {
        var o = EcadJson.Object(parent[key], $"the '{key}' direction");
        string? reference = o["ref"]?.GetValue<string>();
        // The stored direction is already unit (its factories normalized it); keep it VERBATIM
        // rather than re-normalizing, or a re-save would drift by an ulp (the AxisRef rule).
        return new PlacementDirection(reference, new Vector2d(EcadJson.Double(o, "dx"), EcadJson.Double(o, "dy")));
    }

    private static JsonObject SaveLine(in PcbLine line) => new()
    {
        ["point"] = SavePoint(line.Point),
        ["dir"] = SaveDir(line.Direction),
    };

    private static PcbLine ReadLine(JsonObject parent, string key)
    {
        var o = EcadJson.Object(parent[key], $"the '{key}' line");
        return new PcbLine(ReadPoint(o, "point"), ReadDir(o, "dir"));
    }

    private static JsonArray SavePoly(IReadOnlyList<Vector2d> polygon)
    {
        var array = new JsonArray();
        foreach (var p in polygon)
            array.Add(new JsonObject { ["x"] = p.X, ["y"] = p.Y });
        return array;
    }

    private static IReadOnlyList<Vector2d> ReadPoly(JsonObject parent, string key)
    {
        var points = new List<Vector2d>();
        foreach (var node in EcadJson.Array(parent, key))
        {
            var p = EcadJson.Object(node, "a polygon point");
            points.Add(new Vector2d(EcadJson.Double(p, "x"), EcadJson.Double(p, "y")));
        }
        return points;
    }

    private static JsonArray Refs(IReadOnlyList<string> references)
    {
        var array = new JsonArray();
        foreach (var reference in references)
            array.Add(reference);
        return array;
    }

    private static IReadOnlyList<string> ReadRefs(JsonObject parent, string key)
    {
        var references = new List<string>();
        foreach (var node in EcadJson.Array(parent, key))
            references.Add(node?.GetValue<string>()
                ?? throw new FormatException($"A '{key}' entry must be a reference designator string."));
        return references;
    }
}
