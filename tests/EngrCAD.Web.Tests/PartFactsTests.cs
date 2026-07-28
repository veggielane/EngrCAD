using EngrCAD.Modeling;
using EngrCAD.Web;
using Xunit;

namespace EngrCAD.Web.Tests;

/// <summary>
/// The properties panel's facts as a value — the browser mirror of the desktop
/// <c>SceneHost.ShowProperties</c>. The mesh-derived numbers are checked against the
/// part's own mesh rather than re-typed, and the HasMesh gate is pinned because it is
/// the rule that keeps the panel from meshing a part the loader never queued.
/// </summary>
public class PartFactsTests
{
    private static Tab TabWith(params Part[] parts)
    {
        var scene = new Scene();
        var tab = scene.AddTab("Model");
        foreach (var part in parts)
            tab.Add(part);
        return tab;
    }

    private static string Value(
        IReadOnlyList<(string Label, string Value)> facts, string label) =>
        facts.Single(f => f.Label == label).Value;

    [Fact]
    public void MeshedPartReportsTheDesktopFacts()
    {
        var part = new Part("plate", Shape.Box(4, 3, 2));
        var tab = TabWith(part);
        part.Prepare();   // the panel's numbers come from the DISPLAY mesh
        var tree = SceneTree.Build(tab);
        var instance = tab.Instances()[0];

        var facts = PartFacts.For(tree.Rows[0], instance);

        Assert.Equal("plate", Value(facts, "Name"));
        Assert.Equal("Shape (unified)", Value(facts, "Kind"));
        Assert.Equal("shaded", Value(facts, "Display"));
        var mesh = part.GetMesh();
        Assert.Equal(mesh.FaceCount.ToString("N0"), Value(facts, "Faces"));
        Assert.Equal("yes", Value(facts, "Closed"));
        Assert.Equal(mesh.Volume().ToString("G6"), Value(facts, "Volume"));
        Assert.Equal(mesh.SurfaceArea().ToString("G6"), Value(facts, "Area"));
        Assert.Contains("Size", facts.Select(f => f.Label));
        Assert.Contains("Position", facts.Select(f => f.Label));
    }

    [Fact]
    public void UnmeshedPartReportsStatusInsteadOfComputing()
    {
        // The desktop rule: asking for the mesh here would compute one for a part the
        // loader is still working on, so an unprepared part reports its status.
        var part = new Part("slow", Shape.Box(1, 1, 1));
        var tree = SceneTree.Build(TabWith(part));

        var facts = PartFacts.For(tree.Rows[0], null);

        Assert.False(part.HasMesh);   // the facts must not have meshed it
        Assert.Equal("meshing...", Value(facts, "Status"));
        Assert.DoesNotContain("Volume", facts.Select(f => f.Label));
    }

    [Fact]
    public void FailedPartNamesItsFailure()
    {
        var part = new Part("broken", Shape.Box(1, 1, 1));
        var tree = SceneTree.Build(TabWith(part),
            new Dictionary<Part, string> { [part] = "boom" });

        var facts = PartFacts.For(tree.Rows[0], null);

        Assert.Contains("boom", Value(facts, "Status"));
    }

    [Fact]
    public void AssemblyHeaderIsAGroupNotAPart()
    {
        var scene = new Scene();
        var tab = scene.AddTab("Model");
        var assembly = new Assembly("stack");
        assembly.Add(new Part("bolt", Shape.Box(1, 1, 1)));
        tab.Add(assembly);
        var tree = SceneTree.Build(tab);

        var facts = PartFacts.For(tree.Rows[0], null);

        Assert.Equal("assembly", Value(facts, "Kind"));
        Assert.DoesNotContain("Volume", facts.Select(f => f.Label));
    }
}
