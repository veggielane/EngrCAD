using EngrCAD.Modeling;
using EngrCAD.Viewer;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The rollback bar's suppression semantics (FeatureRollback — UI-free; SceneHost only
/// owns the marker buttons). The invariants worth pinning: rolling back suppresses
/// exactly the features below the marker, moving the marker restores exactly what the
/// bar suppressed, and a feature the USER suppressed is never restored by the bar.
/// </summary>
public class FeatureRollbackTests
{
    private sealed class StepFeature : Feature
    {
        [Param(Min = 0.1, Max = 100)]
        public double Size { get; init; } = 10;

        public override Shape Apply(FeatureContext context)
        {
            var box = Shape.Box(Size, Size, 1);
            return context.Body is { } body ? body | box : box;
        }
    }

    private static (FeatureHistory History, StepFeature A, StepFeature B, StepFeature C) ThreeSteps()
    {
        var a = new StepFeature { Name = "A", Size = 10 };
        var b = new StepFeature { Name = "B", Size = 8 };
        var c = new StepFeature { Name = "C", Size = 6 };
        var history = new FeatureHistory();
        history.Add(a);
        history.Add(b);
        history.Add(c);
        return (history, a, b, c);
    }

    [Fact]
    public void RollBack_SuppressesEverythingBelowTheMarker()
    {
        var (history, a, b, c) = ThreeSteps();
        var rolled = new HashSet<Feature>();

        Assert.True(FeatureRollback.RollBackTo(history, a, rolled));
        Assert.False(a.Suppressed);
        Assert.True(b.Suppressed);
        Assert.True(c.Suppressed);
        Assert.Equal(2, rolled.Count);
    }

    [Fact]
    public void MovingTheMarkerDown_RestoresTheStepsAboveIt()
    {
        var (history, a, b, c) = ThreeSteps();
        var rolled = new HashSet<Feature>();
        FeatureRollback.RollBackTo(history, a, rolled);

        Assert.True(FeatureRollback.RollBackTo(history, b, rolled));
        Assert.False(a.Suppressed);
        Assert.False(b.Suppressed);
        Assert.True(c.Suppressed);
        Assert.Single(rolled);
    }

    [Fact]
    public void MarkerOnTheLastFeature_RestoresTheWholeHistory()
    {
        var (history, a, b, c) = ThreeSteps();
        var rolled = new HashSet<Feature>();
        FeatureRollback.RollBackTo(history, a, rolled);

        Assert.True(FeatureRollback.RollBackTo(history, c, rolled));
        Assert.False(a.Suppressed);
        Assert.False(b.Suppressed);
        Assert.False(c.Suppressed);
        Assert.Empty(rolled);
    }

    [Fact]
    public void UserSuppressedFeature_IsNeverRestoredByTheBar()
    {
        var (history, a, b, c) = ThreeSteps();
        b.Suppressed = true;   // the user's own decision, before any rollback
        var rolled = new HashSet<Feature>();

        FeatureRollback.RollBackTo(history, a, rolled);   // suppresses c (b already was)
        Assert.Single(rolled);

        FeatureRollback.RollBackTo(history, c, rolled);   // full restore
        Assert.True(b.Suppressed);    // the user's suppression survives
        Assert.False(c.Suppressed);
        Assert.Empty(rolled);
    }

    [Fact]
    public void MarkerAlreadyInPlace_ReportsNoChange()
    {
        var (history, a, _, _) = ThreeSteps();
        var rolled = new HashSet<Feature>();
        Assert.True(FeatureRollback.RollBackTo(history, a, rolled));
        Assert.False(FeatureRollback.RollBackTo(history, a, rolled));
    }

    [Fact]
    public void ForeignFeature_IsRefusedWithoutTouchingAnything()
    {
        var (history, a, _, _) = ThreeSteps();
        var stranger = new StepFeature { Name = "elsewhere" };
        Assert.False(FeatureRollback.RollBackTo(history, stranger, []));
        Assert.False(a.Suppressed);
    }

    [Fact]
    public void RolledBackHistory_RegeneratesToThePrefix()
    {
        // The bar's whole point: after rolling back to A, the regenerated body is A's
        // body alone (suppressed features pass the body through untouched).
        var (history, a, _, _) = ThreeSteps();
        var full = history.Regenerate();
        Assert.True(full.Succeeded);

        FeatureRollback.RollBackTo(history, a, []);
        var rolledBack = history.Regenerate();
        Assert.True(rolledBack.Succeeded);
        Assert.NotNull(rolledBack.Body);
        Assert.Equal(10 * 10 * 1, rolledBack.Body!.ToMesh().Volume(), 1e-6);
    }
}
