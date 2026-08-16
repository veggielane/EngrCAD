using System.Text.Json.Nodes;
using EngrCAD.Modeling;
using ModelContextProtocol.Protocol;
using Xunit;

namespace EngrCAD.Mcp.Tests;

/// <summary>
/// The tool layer invoked directly — no client, no transport, no process. These are
/// the tests that pin what an assistant actually receives.
/// </summary>
public class SceneToolsTests
{
    private static SceneTools Tools(Scene scene) => new(new SceneSession(scene));

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

    // ---- listing ----

    [Fact]
    public void ListTabs_reports_every_tab_with_its_counts()
    {
        var payload = Payload(Tools(TestScenes.Basic()).ListTabs());
        var tabs = Assert.IsType<JsonArray>(payload["tabs"]);
        Assert.Equal(2, tabs.Count);
        Assert.Equal("Model", (string?)tabs[0]!["name"]);
        Assert.Equal(2, (int?)tabs[0]!["parts"]);
        Assert.Equal(2, (int?)tabs[0]!["instances"]);
        Assert.Equal("field", (string?)tabs[1]!["name"]);
        Assert.Equal(1, (int?)tabs[1]!["parts"]);
    }

    [Fact]
    public void ListParts_reports_kind_tab_paths_and_the_brep_route()
    {
        var payload = Payload(Tools(TestScenes.Basic()).ListParts());
        var parts = Assert.IsType<JsonArray>(payload["parts"]);
        Assert.Equal(3, parts.Count);

        var bracket = parts.Single(p => (string?)p!["name"] == "bracket")!;
        Assert.Equal("Model", (string?)bracket["tab"]);
        Assert.Equal("Shape (unified)", (string?)bracket["kind"]);
        Assert.True((bool?)bracket["exactBrep"]);            // a box-cylinder union lowers exactly
        Assert.Equal(1, (int?)bracket["instances"]);
        Assert.Equal("bracket", (string?)Assert.IsType<JsonArray>(bracket["paths"])[0]);

        var blob = parts.Single(p => (string?)p!["name"] == "blob")!;
        Assert.Equal("field", (string?)blob["tab"]);
        Assert.Equal("implicit (SDF)", (string?)blob["kind"]);
        Assert.False((bool?)blob["exactBrep"]);              // an SDF has no exact B-Rep
    }

    [Fact]
    public void ListParts_filters_by_tab_and_rejects_an_unknown_one()
    {
        var tools = Tools(TestScenes.Basic());
        var parts = Assert.IsType<JsonArray>(Payload(tools.ListParts("field"))["parts"]);
        Assert.Equal("blob", (string?)Assert.Single(parts)!["name"]);

        string error = ErrorText(tools.ListParts("nope"));
        Assert.Contains("No tab named 'nope'", error);
        Assert.Contains("Model", error);                     // the message lists what does exist
    }

    // ---- laziness ----

    [Fact]
    public void Listing_tools_never_evaluate_geometry()
    {
        var counter = new TestScenes.CountingSdf();
        var tools = Tools(TestScenes.Basic(counter));

        tools.ListTabs();
        tools.ListParts();
        tools.SceneJson();

        Assert.Equal(0, counter.Evaluations);
    }

    [Fact]
    public void DescribePart_meshes_only_the_part_it_was_asked_about()
    {
        var counter = new TestScenes.CountingSdf();
        var tools = Tools(TestScenes.Basic(counter));

        tools.DescribePart("bracket");
        Assert.Equal(0, counter.Evaluations);                // the SDF part is untouched

        tools.DescribePart("blob");
        Assert.True(counter.Evaluations > 0, "describing the SDF part should polygonize it");
    }

    // ---- describe ----

    [Fact]
    public void DescribePart_reports_the_properties_panel_facts()
    {
        var payload = Payload(Tools(TestScenes.Basic()).DescribePart("pin"));

        Assert.Equal("pin", (string?)payload["name"]);
        Assert.Equal("Model", (string?)payload["tab"]);
        Assert.True((bool?)payload["closed"]);
        Assert.True((int?)payload["faces"] > 0);
        Assert.True((int?)payload["vertices"] > 0);

        // A 16-sided prism under-fills the cylinder by ~2.6%; the analytic volume is the
        // ground truth and the discretization sets the tolerance.
        double analytic = Math.PI * TestScenes.PinRadius * TestScenes.PinRadius * TestScenes.PinHeight;
        double volume = (double)payload["volume"]!;
        Assert.InRange(volume, analytic * 0.95, analytic * 1.0001);

        // World bounds carry the part transform (translated to z in [0, 14]).
        var size = Assert.IsType<JsonArray>(payload["bounds"]!["size"]);
        Assert.Equal(TestScenes.PinHeight, (double)size[2]!, 9);
        var min = Assert.IsType<JsonArray>(payload["bounds"]!["min"]);
        Assert.Equal(0.0, (double)min[2]!, 9);
        Assert.Equal(-6.0, (double)Assert.IsType<JsonArray>(payload["position"])[0]!, 9);
    }

    [Fact]
    public void DescribePart_includes_the_construction_tree()
    {
        var payload = Payload(Tools(TestScenes.Basic()).DescribePart("bracket"));
        var tree = Assert.IsType<JsonObject>(payload["constructionTree"]);

        Assert.Equal("", (string?)tree["path"]);             // the root's positional path
        var children = Assert.IsType<JsonArray>(tree["children"]);
        Assert.Equal(2, children.Count);                     // the union's two operands
        Assert.Equal(["0", "1"], children.Select(c => (string?)c!["path"]));

        // Labels come straight from Shape.Describe(), so the box's dimensions read out.
        string labels = string.Join(" | ", Flatten(tree).Select(n => (string?)n["label"]));
        Assert.Contains("Box", labels);
        Assert.Contains("Cylinder", labels);
    }

    [Fact]
    public void DescribePart_can_omit_the_construction_tree()
    {
        var payload = Payload(Tools(TestScenes.Basic()).DescribePart("bracket", constructionTree: false));
        Assert.Null(payload["constructionTree"]);
    }

    [Fact]
    public void DescribePart_resolves_annotations_and_reports_their_measured_values()
    {
        var scene = TestScenes.Basic();
        scene.Tabs[0].Parts.Single(p => p.Name == "pin")
            .Annotate(new LinearDimension(new(0, 0, -7), new(0, 0, 7)));

        var annotations = Assert.IsType<JsonArray>(
            Payload(Tools(scene).DescribePart("pin"))["annotations"]);

        var dimension = Assert.IsType<JsonObject>(Assert.Single(annotations));
        Assert.Equal("LinearDimension", (string?)dimension["type"]);
        Assert.Equal(TestScenes.PinHeight, (double)dimension["value"]!, 9);
        Assert.Contains("14", (string?)dimension["text"]);
    }

    [Fact]
    public void DescribePart_accepts_a_tab_slash_part_path()
    {
        var payload = Payload(Tools(TestScenes.Basic()).DescribePart("field/blob"));
        Assert.Equal("blob", (string?)payload["name"]);
        Assert.Equal("field", (string?)payload["tab"]);
    }

    [Fact]
    public void DescribePart_names_the_parts_that_do_exist_when_one_does_not()
    {
        string error = ErrorText(Tools(TestScenes.Basic()).DescribePart("flange"));
        Assert.Contains("No part named 'flange'", error);
        Assert.Contains("Model/bracket", error);
        Assert.Contains("field/blob", error);
    }

    [Fact]
    public void DescribePart_reports_a_meshing_failure_instead_of_throwing()
    {
        var tools = Tools(TestScenes.Basic(new TestScenes.ThrowingSdf()));
        string error = ErrorText(tools.DescribePart("blob"));
        Assert.Contains("could not be meshed", error);
        Assert.Contains("deliberately broken", error);

        // ... and the rest of the scene is still usable.
        Assert.Equal("bracket", (string?)Payload(tools.DescribePart("bracket"))["name"]);
    }

    // ---- reload ----

    [Fact]
    public void Reload_rebuilds_the_scene_and_bumps_the_generation()
    {
        int builds = 0;
        var session = new SceneSession(() => { builds++; return TestScenes.Basic(); });
        var tools = new SceneTools(session);
        Assert.Equal(1, builds);
        Assert.Equal(0, session.Generation);

        var first = session.Scene;
        var payload = Payload(tools.Reload());

        Assert.Equal(2, builds);
        Assert.Equal(1, (int?)payload["generation"]);
        Assert.Equal(3, (int?)payload["parts"]);
        Assert.NotSame(first, session.Scene);
    }

    [Fact]
    public void Reload_keeps_the_previous_scene_when_the_model_throws()
    {
        bool poisoned = false;
        var session = new SceneSession(() =>
            poisoned ? throw new InvalidOperationException("bad sketch") : TestScenes.Basic());
        var tools = new SceneTools(session);
        var good = session.Scene;

        poisoned = true;
        string error = ErrorText(tools.Reload());

        Assert.Contains("bad sketch", error);
        Assert.Contains("keeping the previous scene", error);
        Assert.Same(good, session.Scene);
        Assert.Equal(0, session.Generation);
    }

    // ---- export ----

    [Fact]
    public void Export_writes_stl_obj_and_step()
    {
        var tools = Tools(TestScenes.Basic());
        string directory = Path.Combine(Path.GetTempPath(), $"engrcad-mcp-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string stl = Path.Combine(directory, "model.stl");
            Assert.Equal(Path.GetFullPath(stl), (string?)Payload(tools.Export(stl))["wrote"]);
            Assert.True(new FileInfo(stl).Length > 84);          // binary STL header + facets

            string obj = Path.Combine(directory, "model.obj");
            Payload(tools.Export(obj));
            Assert.Contains("o bracket", File.ReadAllText(obj));

            // Only the Model tab: two exact solids, so STEP writes one file per part.
            var step = Payload(tools.Export(Path.Combine(directory, "model.step"), tab: "Model"));
            var written = Assert.IsType<JsonArray>(step["wrote"]);
            Assert.Equal(2, written.Count);
            foreach (var file in written)
                Assert.Contains("ISO-10303-21", File.ReadAllText((string)file!));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Export_writes_vtu_with_the_result_array_count()
    {
        // The parity case the CLI's --export already had: a .vtu carries the geometry
        // plus every part's results, and the reply states how many arrays came out —
        // a .vtu with none is a valid geometry file, and the difference must be visible.
        var tools = Tools(TestScenes.Basic());
        string directory = Path.Combine(Path.GetTempPath(), $"engrcad-mcp-vtu-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string vtu = Path.Combine(directory, "model.vtu");
            var payload = Payload(tools.Export(vtu));
            Assert.Equal(Path.GetFullPath(vtu), (string?)payload["wrote"]);
            Assert.Equal("VTU", (string?)payload["format"]);
            Assert.NotNull(payload["resultArrays"]);
            Assert.Contains("<VTKFile", File.ReadAllText(vtu));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Export_rejects_an_unknown_format()
    {
        string error = ErrorText(Tools(TestScenes.Basic()).Export("model.iges"));
        Assert.Contains(".step", error);
        Assert.Contains(".stl", error);
    }

    [Fact]
    public void Export_step_refuses_a_tab_with_no_exact_solids()
    {
        string path = Path.Combine(Path.GetTempPath(), $"engrcad-mcp-{Guid.NewGuid():N}.step");
        string error = ErrorText(Tools(TestScenes.Basic()).Export(path, tab: "field"));

        Assert.Contains("No B-Rep-representable parts", error);
        Assert.Contains(".stl", error);                      // ... and says what to do instead
        Assert.False(File.Exists(path));
    }

    // ---- the scene resource ----

    [Fact]
    public void SceneJson_summarizes_the_document()
    {
        var json = Assert.IsType<JsonObject>(JsonNode.Parse(Tools(TestScenes.Basic()).SceneJson()));
        var tabs = Assert.IsType<JsonArray>(json["tabs"]);
        Assert.Equal(2, tabs.Count);
        var parts = Assert.IsType<JsonArray>(tabs[0]!["parts"]);
        Assert.Equal(["bracket", "pin"], parts.Select(p => (string?)p!["name"]));
    }

    private static IEnumerable<JsonObject> Flatten(JsonObject node)
    {
        yield return node;
        if (node["children"] is JsonArray children)
        {
            foreach (var child in children.OfType<JsonObject>())
            {
                foreach (var descendant in Flatten(child))
                    yield return descendant;
            }
        }
    }
}
