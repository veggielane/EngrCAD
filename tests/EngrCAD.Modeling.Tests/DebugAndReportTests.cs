using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Part-level debug modifiers (<see cref="DebugFilter"/> — the OpenSCAD #/%/!/*
/// analog) and the model-validation report (<see cref="SceneReport"/> — the
/// assert/echo analog).
/// </summary>
public class DebugAndReportTests
{
    // ------------------------------------------------------------- debug modifiers

    [Fact]
    public void DefaultFlags_AreTheIdentity()
    {
        var scene = new Scene();
        scene.Add(new Part("a", Shape.Box(1, 1, 1)));
        scene.Add(new Part("b", Shape.Box(1, 1, 1), transform: Matrix4d.CreateTranslation((3, 0, 0))));
        var all = scene.AllInstances.ToList();

        Assert.Equal(all, DebugFilter.Shown(all));
        Assert.Equal(all, DebugFilter.Exported(all));
    }

    [Fact]
    public void Hidden_IsNeitherShownNorExported()
    {
        var scene = new Scene();
        scene.Add(new Part("keep", Shape.Box(1, 1, 1)));
        var hidden = scene.Add(new Part("gone", Shape.Box(1, 1, 1)));
        hidden.Hidden = true;

        Assert.Equal(["keep"], DebugFilter.Shown([.. scene.AllInstances]).Select(i => i.Part.Name));
        Assert.Equal(["keep"], DebugFilter.Exported([.. scene.AllInstances]).Select(i => i.Part.Name));
    }

    [Fact]
    public void Ghost_IsShownTranslucentButNotExported()
    {
        var scene = new Scene();
        scene.Add(new Part("solid", Shape.Box(1, 1, 1)));
        var ghost = scene.Add(new Part("reference", Shape.Box(2, 2, 2)));
        ghost.Ghost = true;

        Assert.Equal(DisplayMode.Translucent, ghost.EffectiveDisplayMode);
        Assert.Equal(DisplayMode.Shaded, ghost.DisplayMode);   // the raw mode is untouched
        Assert.Equal(["solid", "reference"],
            DebugFilter.Shown([.. scene.AllInstances]).Select(i => i.Part.Name));
        Assert.Equal(["solid"],
            DebugFilter.Exported([.. scene.AllInstances]).Select(i => i.Part.Name));
    }

    [Fact]
    public void Isolate_ShowsOnlyIsolatedParts()
    {
        var scene = new Scene();
        scene.Add(new Part("a", Shape.Box(1, 1, 1)));
        var focus = scene.Add(new Part("focus", Shape.Box(1, 1, 1)));
        scene.Add(new Part("c", Shape.Box(1, 1, 1)));
        focus.Isolated = true;

        Assert.Equal(["focus"], DebugFilter.Shown([.. scene.AllInstances]).Select(i => i.Part.Name));
        Assert.Equal(["focus"], DebugFilter.Exported([.. scene.AllInstances]).Select(i => i.Part.Name));
    }

    [Fact]
    public void IsolatedGhost_ShowsButStillDoesNotExport()
    {
        var part = new Part("g", Shape.Box(1, 1, 1)) { Ghost = true, Isolated = true };
        Assert.True(DebugFilter.IsShown(part, anyIsolated: true));
        Assert.False(DebugFilter.IsExported(part, anyIsolated: true));
    }

    // ------------------------------------------------------------ validation report

    [Fact]
    public void CleanScene_ReportsAllClean()
    {
        var scene = new Scene();
        scene.Add(new Part("plate", Shape.Box(20, 10, 5)));
        scene.Add(new Part("boss", Shape.Cylinder(3, 8)));

        var report = SceneReport.Create(scene);
        Assert.True(report.AllClean);
        Assert.Equal(2, report.Parts.Count);
        var plate = report.Parts.Single(p => p.Name == "plate");
        Assert.True(plate.Closed);
        Assert.Equal(1000, plate.Volume!.Value, 9);
        Assert.Equal("Shape", plate.Kind);
        Assert.Contains("all clean", report.ToText());
    }

    [Fact]
    public void OpenMesh_IsFlaggedWithBoundaryLoopCount()
    {
        // A single triangle is a valid, manifold-with-boundary half-edge mesh — and
        // exactly the kind of import a validation report exists to flag.
        var open = HalfEdgeMesh.Build(
            [new Vector3d(0, 0, 0), new Vector3d(1, 0, 0), new Vector3d(0, 1, 0)],
            [[0, 1, 2]]);
        var scene = new Scene();
        scene.Add(new Part("sheet", open));

        var report = SceneReport.Create(scene);
        Assert.False(report.AllClean);
        var check = Assert.Single(report.Parts);
        Assert.False(check.Closed);
        Assert.Null(check.Volume);            // an open shell has no volume
        Assert.Contains(check.Notes, n => n.Contains("1 boundary loop"));
        Assert.Contains("NO", report.ToText());
    }

    [Fact]
    public void DebugModifiers_AppearAsNotes()
    {
        var scene = new Scene();
        var part = scene.Add(new Part("ghosted", Shape.Box(1, 1, 1)));
        part.Ghost = true;

        var report = SceneReport.Create(scene);
        Assert.False(report.AllClean);
        Assert.Contains(Assert.Single(report.Parts).Notes, n => n.Contains("ghost"));
    }

    [Fact]
    public void Report_CoversEveryTab()
    {
        var scene = new Scene();
        scene.AddTab("housing").Add(new Part("body", Shape.Box(4, 4, 4)));
        scene.AddTab("hardware").Add(new Part("pin", Shape.Cylinder(1, 5)));

        var report = SceneReport.Create(scene);
        Assert.Equal(["housing", "hardware"], report.Parts.Select(p => p.Tab).Distinct());
    }
}
