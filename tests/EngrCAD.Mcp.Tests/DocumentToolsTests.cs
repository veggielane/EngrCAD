using EngrCAD.Modeling;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace EngrCAD.Mcp.Tests;

/// <summary>
/// <c>save_document</c> / <c>load_document</c> — how a session's edits survive it.
/// The serialization itself is `Document`'s (and pinned there by its own fixed-point
/// test); what these assert is the TOOL contract: that a save captures an edit the
/// assistant just made, that loading brings it back parametric, that a load is an
/// overlay <c>reload</c> discards, and that the honest reporting — snapshots, warnings,
/// a refused envelope — reaches the caller rather than being swallowed.
/// </summary>
public class DocumentToolsTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"engrcad-doc-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private static JsonElement Json(string literal) => JsonDocument.Parse(literal).RootElement;

    private string Path_(string name) => System.IO.Path.Combine(_directory, name);

    private static JsonObject Payload(CallToolResult result)
    {
        Assert.False(result.IsError == true,
            $"expected success, got: {string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text))}");
        string text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        return (JsonObject)JsonNode.Parse(text)!;
    }

    private static string ErrorText(CallToolResult result)
    {
        Assert.True(result.IsError == true, "expected an error result");
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
    }

    [Fact]
    public void An_edit_made_through_set_param_survives_a_save_and_load()
    {
        var session = new SceneSession(TestScenes.Parametric(), TestScenes.Coarse);
        var tools = new SceneTools(session);

        // The whole point of the pair: tune, hand the tuning back as a file.
        Assert.False(tools.SetParam("plate", "Base", "Height", Json("11")).IsError == true);
        string file = Path_("tuned.json");
        var saved = Payload(tools.SaveDocument(file));
        Assert.Equal(System.IO.Path.GetFullPath(file), (string)saved["wrote"]!);
        Assert.True(File.Exists(file));

        // A fresh session over the ORIGINAL model, then the file: the edited value is
        // back, and it is back as a PARAMETER (the part still has its history), not as
        // a lump of geometry.
        var fresh = new SceneSession(TestScenes.Parametric(), TestScenes.Coarse);
        var freshTools = new SceneTools(fresh);
        var loaded = Payload(freshTools.LoadDocument(file));
        Assert.True((bool)loaded["adopted"]!);

        var plate = fresh.Scene.AllParts.Single(p => p.Name == "plate");
        Assert.NotNull(plate.History);
        Assert.Equal(11.0, Assert.IsAssignableFrom<ExtrudeSketchFeature>(
            plate.History!.Features.First(f => f.Name == "Base")).Height, 9);

        // ... and it is genuinely still editable through the same tool.
        Assert.False(freshTools.SetParam("plate", "Base", "Height", Json("13")).IsError == true);
    }

    [Fact]
    public void Loading_bumps_the_generation_and_reload_discards_it()
    {
        var scene = TestScenes.Parametric();
        var session = new SceneSession(() => TestScenes.Parametric(), TestScenes.Coarse);
        var tools = new SceneTools(session);

        tools.SetParam("plate", "Base", "Height", Json("9"));
        string file = Path_("nine.json");
        tools.SaveDocument(file);

        var other = new SceneSession(() => TestScenes.Parametric(), TestScenes.Coarse);
        var otherTools = new SceneTools(other);
        int before = other.Generation;
        Payload(otherTools.LoadDocument(file));
        Assert.True(other.Generation > before);

        double Height(SceneSession s) =>
            ((ExtrudeSketchFeature)s.Scene.AllParts.Single(p => p.Name == "plate")
                .History!.Features.First(f => f.Name == "Base")).Height;
        Assert.Equal(9.0, Height(other), 9);

        // The program's source is still the truth: reload rebuilds from the factory.
        otherTools.Reload();
        Assert.Equal(TestScenes.PlateHeight, Height(other), 9);
    }

    [Fact]
    public void A_dry_run_reads_and_reports_without_touching_the_model()
    {
        var session = new SceneSession(TestScenes.Parametric(), TestScenes.Coarse);
        var tools = new SceneTools(session);
        string file = Path_("dry.json");
        tools.SetParam("plate", "Base", "Height", Json("7"));
        tools.SaveDocument(file);

        var other = new SceneSession(TestScenes.Parametric(), TestScenes.Coarse);
        var otherTools = new SceneTools(other);
        var scene = other.Scene;
        var report = Payload(otherTools.LoadDocument(file, adopt: false));

        Assert.False((bool)report["adopted"]!);
        Assert.Same(scene, other.Scene);
        Assert.True((int)report["parts"]! > 0);
    }

    [Fact]
    public void Parts_with_no_construction_recipe_are_NAMED_not_silently_flattened()
    {
        // The SDF blob in the fixture has no history, so it can only come back as a
        // snapshot — the file must say so rather than let a client discover later that
        // editing it does nothing. Named "tab/part", the spelling describe_part and
        // set_param already accept, so a client can act on the report without re-deriving
        // which tab each name belongs to.
        var tools = new SceneTools(new SceneSession(TestScenes.Basic(), TestScenes.Coarse));
        string file = Path_("mixed.json");
        var saved = Payload(tools.SaveDocument(file));
        var snapshots = ((JsonArray)saved["snapshots"]!).Select(n => (string)n!).ToList();
        Assert.Contains("field/blob", snapshots);
        Assert.Contains("Model/bracket", snapshots);   // a Shape graph has no recipe either
    }

    [Fact]
    public void A_missing_file_and_a_foreign_file_both_refuse_by_name()
    {
        var tools = new SceneTools(new SceneSession(TestScenes.Basic(), TestScenes.Coarse));

        Assert.Contains("No document at", ErrorText(tools.LoadDocument(Path_("nope.json"))));

        string foreign = Path_("foreign.json");
        File.WriteAllText(foreign, """{ "format": "something-else", "version": 1 }""");
        string error = ErrorText(tools.LoadDocument(foreign));
        Assert.Contains("not a document this build reads", error);

        Assert.Contains("needs a file path", ErrorText(tools.SaveDocument("  ")));
    }

    [Fact]
    public void The_tools_are_advertised_with_their_output_schemas()
    {
        var tools = EngrCadMcpServer.BuildTools(
            new SceneTools(new SceneSession(TestScenes.Basic(), TestScenes.Coarse)));
        foreach (string name in new[] { "save_document", "load_document" })
            Assert.Contains(tools, t => t.ProtocolTool.Name == name && t.ProtocolTool.OutputSchema is not null);
    }
}
