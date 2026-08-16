using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

public class PartResultsTests
{
    private static Part PlateWithResults(out MeshField stress, out MeshField displacement)
    {
        var part = new Part("plate", Shape.Box(20, 10, 2));
        var mesh = part.GetMesh();
        stress = MeshField.Sample(mesh, "von Mises", "MPa", p => p.Z * 5);
        displacement = MeshField.SampleVector(mesh, "displacement", "mm", p => new Vector3d(0, 0, p.X * 0.01));
        part.AddResult(stress).AddResult(displacement);
        return part;
    }

    [Fact]
    public void AddResult_AttachesResultsToTheDocumentModel()
    {
        var part = PlateWithResults(out var stress, out var displacement);

        Assert.Equal(2, part.Results.Count);
        Assert.Same(stress, part.Result("von Mises"));
        Assert.Same(displacement, part.Result("displacement"));
        Assert.Null(part.Result("temperature"));
    }

    [Fact]
    public void AddResult_ReplacesAResultOfTheSameName()
    {
        var part = PlateWithResults(out _, out _);
        var rerun = MeshField.Sample(part.GetMesh(), "von Mises", "MPa", _ => 1);
        part.AddResult(rerun);

        // A re-solve updates the display instead of accumulating stale twins under one
        // name — which is what keeps FieldDisplay, which refers to results by name,
        // pointing at the live one.
        Assert.Equal(2, part.Results.Count);
        Assert.Same(rerun, part.Result("von Mises"));
    }

    [Fact]
    public void AddResult_DoesNotMesh_SoPreMeshStaysFreeToRunInParallel()
    {
        var part = new Part("box", Shape.Box(4, 4, 4));
        Assert.False(part.HasMesh);
        part.AddResult(MeshField.Scalar("s", "", [1, 2, 3]));
        Assert.False(part.HasMesh);
    }

    [Fact]
    public void Results_AreEmptyAndFieldDisplayNullByDefault()
    {
        var part = new Part("box", Shape.Box(1, 1, 1));
        Assert.Empty(part.Results);
        Assert.Null(part.FieldDisplay);
        Assert.False(part.TryResolveFieldDisplay(out _, out string? error));
        Assert.Null(error);   // no display asked for is not a failure
    }

    [Fact]
    public void TryResolveFieldDisplay_SettlesTheRangeFromTheFieldWhenNoneIsGiven()
    {
        var part = PlateWithResults(out var stress, out _);
        part.FieldDisplay = new FieldDisplay { Field = "von Mises" };

        Assert.True(part.TryResolveFieldDisplay(out var resolved, out string? error));
        Assert.Null(error);
        Assert.Same(stress, resolved.Field);
        Assert.Equal(stress.Range, resolved.Range);
        Assert.Equal(FieldColorMap.Viridis, resolved.ColorMap);
        Assert.Null(resolved.Deform);
        Assert.Equal("von Mises [MPa]", resolved.Label);
    }

    [Fact]
    public void TryResolveFieldDisplay_LogScale_ThreadsThroughTheResolution()
    {
        var part = PlateWithResults(out _, out _);
        part.FieldDisplay = new FieldDisplay
        {
            Field = "von Mises", LogScale = true, Range = new FieldRange(10, 1e5),
        };

        Assert.True(part.TryResolveFieldDisplay(out var resolved, out string? error), error);
        Assert.True(resolved.LogScale);
    }

    [Fact]
    public void TryResolveFieldDisplay_LogScale_RefusesANonPositiveRangeByName()
    {
        // log10 of a non-positive bound has no value, so a log display whose range
        // reaches zero is refused when it RESOLVES — naming the field and the range —
        // rather than painting every node the bottom stop.
        var part = PlateWithResults(out _, out _);
        part.FieldDisplay = new FieldDisplay
        {
            Field = "von Mises", LogScale = true, Range = new FieldRange(0, 100),
        };

        Assert.False(part.TryResolveFieldDisplay(out _, out string? error));
        Assert.Contains("strictly positive", error);
        Assert.Contains("von Mises", error);
    }

    [Fact]
    public void TryResolveFieldDisplay_AnExplicitRangeWins()
    {
        var part = PlateWithResults(out _, out _);
        part.FieldDisplay = new FieldDisplay
        {
            Field = "von Mises",
            Range = new FieldRange(0, 100),
            ColorMap = FieldColorMap.Diverging,
        };

        Assert.True(part.TryResolveFieldDisplay(out var resolved, out _));
        Assert.Equal(new FieldRange(0, 100), resolved.Range);
        Assert.Equal(FieldColorMap.Diverging, resolved.ColorMap);
    }

    [Fact]
    public void TryResolveFieldDisplay_ResolvesTheDeformationField()
    {
        var part = PlateWithResults(out _, out var displacement);
        part.FieldDisplay = new FieldDisplay
        {
            Field = "von Mises",
            Deform = "displacement",
            DeformScale = 25,
            ShowUndeformed = false,
        };

        Assert.True(part.TryResolveFieldDisplay(out var resolved, out _));
        Assert.Same(displacement, resolved.Deform);
        Assert.Equal(25, resolved.DeformScale);
        Assert.False(resolved.ShowUndeformed);
    }

    [Fact]
    public void TryResolveFieldDisplay_NamesAMissingResultAndWhatDoesExist()
    {
        var part = PlateWithResults(out _, out _);
        part.FieldDisplay = new FieldDisplay { Field = "temperature" };

        Assert.False(part.TryResolveFieldDisplay(out _, out string? error));
        Assert.Contains("plate", error);
        Assert.Contains("temperature", error);
        Assert.Contains("von Mises", error);
    }

    [Fact]
    public void TryResolveFieldDisplay_RefusesToDeformByAScalarField()
    {
        var part = PlateWithResults(out _, out _);
        part.FieldDisplay = new FieldDisplay { Field = "von Mises", Deform = "von Mises" };

        Assert.False(part.TryResolveFieldDisplay(out _, out string? error));
        Assert.Contains("vector (displacement) field", error);
    }

    [Fact]
    public void TryResolveFieldDisplay_RefusesAFieldWithNoFiniteValues()
    {
        var part = new Part("box", Shape.Box(1, 1, 1));
        var mesh = part.GetMesh();
        part.AddResult(MeshField.Sample(mesh, "nan", "", _ => double.NaN));
        part.FieldDisplay = new FieldDisplay { Field = "nan" };

        Assert.False(part.TryResolveFieldDisplay(out _, out string? error));
        Assert.Contains("no finite values", error);
    }

    [Fact]
    public void Results_SurviveSceneAndTabPlumbing()
    {
        var part = PlateWithResults(out _, out _);
        part.FieldDisplay = new FieldDisplay { Field = "von Mises" };
        var scene = new Scene();
        scene.Add(part);
        scene.PreMesh();

        var instance = Assert.Single(scene.AllInstances);
        Assert.Equal(2, instance.Part.Results.Count);
        Assert.True(instance.Part.TryResolveFieldDisplay(out _, out _));
    }

    [Fact]
    public void ResultsMatchTheDisplayMeshVertexCount()
    {
        // The seam a solver publishes through: a field's values index the part's
        // DISPLAY mesh vertices, in vertex-index order.
        var part = PlateWithResults(out var stress, out _);
        Assert.Equal(part.GetMesh().VertexCount, stress.Count);
    }
}
