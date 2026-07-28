using System.Text.Json;
using System.Text.Json.Nodes;
using EngrCAD.Modeling;
using ModelContextProtocol.Protocol;
using Xunit;

namespace EngrCAD.Mcp.Tests;

/// <summary>
/// The write tools — set_param / suppress_feature / unsuppress_feature — invoked
/// directly. These pin the whole editing contract: values change geometry, failures
/// keep the previous geometry AND report the failing feature, errors name what exists,
/// and none of it evaluates geometry that was not asked for.
/// </summary>
public class WriteToolsTests
{
    private static SceneTools Tools(Scene scene) => new(new SceneSession(scene));

    private static JsonElement Json(string literal) => JsonDocument.Parse(literal).RootElement;

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static JsonObject Payload(CallToolResult result)
    {
        Assert.False(result.IsError == true, $"expected success, got error: {Text(result)}");
        return Assert.IsType<JsonObject>(JsonNode.Parse(Text(result)));
    }

    private static string ErrorText(CallToolResult result)
    {
        Assert.True(result.IsError == true, "expected an error result");
        return Text(result);
    }

    private static double Volume(SceneTools tools, string part) =>
        (double)Payload(tools.DescribePart(part))["volume"]!;

    // ---- set_param ----

    [Fact]
    public void SetParam_changes_the_geometry_and_reports_the_regeneration()
    {
        var tools = Tools(TestScenes.Parametric());
        double before = Volume(tools, "plate");
        int generationBefore = (int)Payload(tools.ListTabs())["generation"]!;

        var payload = Payload(tools.SetParam("plate", "Base", "Height", Json("12")));

        Assert.True((bool?)payload["succeeded"]);
        Assert.True((bool?)payload["geometryUpdated"]);
        Assert.True((int?)payload["generation"] > generationBefore);   // stale-read token bumped
        var features = Assert.IsType<JsonArray>(payload["features"]);
        Assert.Equal(["Base", "Boss"], features.Select(f => (string?)f!["name"]));
        Assert.All(features, f => Assert.Equal("applied", (string?)f!["outcome"]));

        // Doubling the base height adds exactly footprint x extra height to the union
        // (the boss pokes through both tops, so its contribution outside the plate
        // shrinks by its own footprint share).
        double after = Volume(tools, "plate");
        Assert.True(after > before, $"volume should grow: {before} -> {after}");
        double expectedGrowth = (TestScenes.PlateWidth * TestScenes.PlateDepth - 4 * 4)
            * (12 - TestScenes.PlateHeight);
        Assert.Equal(expectedGrowth, after - before, 6);
    }

    [Fact]
    public void SetParam_failure_keeps_the_previous_geometry_and_names_the_feature()
    {
        var tools = Tools(TestScenes.Parametric());
        double before = Volume(tools, "plate");

        // Height has [Param(Min = 1e-9)]: -1 fails validation-first, the prefix is kept,
        // Boss is skipped.
        var result = tools.SetParam("plate", "Base", "Height", Json("-1"));
        string error = ErrorText(result);
        Assert.Contains("Base", error);
        Assert.Contains("previous geometry", error);
        Assert.NotNull(result.StructuredContent);
        var structured = Assert.IsType<JsonObject>(
            JsonNode.Parse(result.StructuredContent.Value.GetRawText()));
        Assert.False((bool?)structured["succeeded"]);
        Assert.False((bool?)structured["geometryUpdated"]);
        var features = Assert.IsType<JsonArray>(structured["features"]);
        Assert.Equal("failed", (string?)features[0]!["outcome"]);
        Assert.Equal("skipped", (string?)features[1]!["outcome"]);

        Assert.Equal(before, Volume(tools, "plate"), 9);   // untouched

        // The edit stays applied (feature-tree semantics): setting a good value again
        // regenerates cleanly.
        var repaired = Payload(tools.SetParam("plate", "Base", "Height", Json("6")));
        Assert.True((bool?)repaired["succeeded"]);
        Assert.Equal(before, Volume(tools, "plate"), 9);
    }

    [Fact]
    public void SetParam_errors_name_what_exists()
    {
        var tools = Tools(TestScenes.Parametric());

        string noPart = ErrorText(tools.SetParam("nope", "Base", "Height", Json("6")));
        Assert.Contains("No part named 'nope'", noPart);
        Assert.Contains("plate", noPart);

        string noHistory = ErrorText(tools.SetParam("blob", "Base", "Height", Json("6")));
        Assert.Contains("no parametric feature history", noHistory);
        Assert.Contains("Model/plate", noHistory);   // points at the editable part

        string noFeature = ErrorText(tools.SetParam("plate", "Flange", "Height", Json("6")));
        Assert.Contains("no feature named 'Flange'", noFeature);
        Assert.Contains("Base", noFeature);
        Assert.Contains("Boss", noFeature);

        string noParam = ErrorText(tools.SetParam("plate", "Base", "Depth", Json("6")));
        Assert.Contains("no parameter named 'Depth'", noParam);
        Assert.Contains("Height", noParam);           // lists what exists, with its range
        Assert.Contains("number", noParam);

        string badValue = ErrorText(tools.SetParam("plate", "Base", "Height", Json("\"tall\"")));
        Assert.Contains("Could not set Base.Height", badValue);
    }

    [Fact]
    public void SetParam_evaluates_no_unrelated_geometry()
    {
        var counter = new TestScenes.CountingSdf();
        var tools = Tools(TestScenes.Parametric(counter));

        Payload(tools.SetParam("plate", "Base", "Height", Json("8")));

        // Regeneration rebuilds the Shape graph; nothing meshes until a tool asks.
        Assert.Equal(0, counter.Evaluations);
    }

    // ---- suppression ----

    [Fact]
    public void Suppress_and_unsuppress_toggle_a_features_contribution()
    {
        var tools = Tools(TestScenes.Parametric());
        double withBoss = Volume(tools, "plate");
        double plainPlate = TestScenes.PlateWidth * TestScenes.PlateDepth * TestScenes.PlateHeight;
        Assert.True(withBoss > plainPlate);

        var suppressed = Payload(tools.SuppressFeature("plate", "Boss"));
        Assert.True((bool?)suppressed["succeeded"]);
        Assert.Equal("suppressed",
            (string?)suppressed["features"]!.AsArray().Single(f => (string?)f!["name"] == "Boss")!["outcome"]);
        Assert.Equal(plainPlate, Volume(tools, "plate"), 9);   // exactly the bare extrusion

        var restored = Payload(tools.UnsuppressFeature("plate", "Boss"));
        Assert.True((bool?)restored["succeeded"]);
        Assert.Equal(withBoss, Volume(tools, "plate"), 9);
    }

    [Fact]
    public void Suppress_errors_mirror_set_param()
    {
        var tools = Tools(TestScenes.Parametric());
        string error = ErrorText(tools.SuppressFeature("plate", "Flange"));
        Assert.Contains("no feature named 'Flange'", error);
        Assert.Contains("Boss", error);
    }
}
