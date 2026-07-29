using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

public class FeatureTests
{
    private sealed class PlateFeature : Feature
    {
        public static int Applications;

        [Param(Min = 5, Max = 100, Units = "mm")]
        public double Width { get; init; } = 40;

        [Param(Min = 1, Max = 20)]
        public double Thickness { get; init; } = 8;

        public override Shape Apply(FeatureContext context)
        {
            Applications++;
            return Shape.Extrude(Sketch.RoundedRectangle(Width, Width * 0.75, 4), Thickness);
        }
    }

    private sealed class BoltCircleFeature : Feature
    {
        public static int Applications;

        [Param(Min = 2, Max = 24)]
        public int Count { get; init; } = 4;

        [Param(Min = 1)]
        public double Radius { get; init; } = 9;

        public override Shape Apply(FeatureContext context)
        {
            Applications++;
            var points = Enumerable.Range(0, Count)
                .Select(i => 2 * Math.PI * i / Count)
                .Select(angle => new Vector2d(Radius * Math.Cos(angle), Radius * Math.Sin(angle)))
                .ToList();
            return context.Body!.Drill(StandardHoles.Clearance(4), points, Depth, context.TopPlane);
        }

        [Param(Min = 1)]
        public double Depth { get; init; } = 20;
    }

    private static FeatureHistory BuildHistory(double width = 40, int holes = 4)
    {
        var history = new FeatureHistory();
        history.Add(new PlateFeature { Width = width });
        history.Add(new BoltCircleFeature { Count = holes });
        history.Add(new FilletRimFeature { Radius = 1.5 });
        return history;
    }

    [Fact]
    public void History_RegeneratesAndMatchesHandBuiltShape()
    {
        var history = BuildHistory();
        var result = history.Regenerate();
        Assert.True(result.Succeeded, result.ToString());
        Assert.All(result.Statuses, s => Assert.Equal(FeatureOutcome.Applied, s.Outcome));

        var byHand = Shape.Extrude(Sketch.RoundedRectangle(40, 30, 4), 8)
            .Drill(StandardHoles.Clearance(4),
                [.. Enumerable.Range(0, 4).Select(i => 2 * Math.PI * i / 4)
                    .Select(a => new Vector2d(9 * Math.Cos(a), 9 * Math.Sin(a)))],
                20, SketchPlane.At((0, 0, 8), Vector3d.UnitX, Vector3d.UnitY))
            .Fillet(1.5, s => s.PlanarFacesWithNormal(Vector3d.UnitZ));

        double historyVolume = history.Result!.ToMesh().Volume();
        double handVolume = byHand.ToMesh().Volume();
        Assert.True(Math.Abs(historyVolume - handVolume) < 1e-9,
            $"history {historyVolume} vs hand-built {handVolume}");

        var part = history.ToPart("bracket");
        Assert.Same(history.Result, part.Geometry);
    }

    [Fact]
    public void Caching_ReplaysOnlyTheDirtySuffix()
    {
        PlateFeature.Applications = 0;
        BoltCircleFeature.Applications = 0;
        var history = BuildHistory();

        history.Regenerate();
        Assert.Equal(1, PlateFeature.Applications);
        Assert.Equal(1, BoltCircleFeature.Applications);

        // Nothing changed: everything cached.
        var second = history.Regenerate();
        Assert.Equal(1, PlateFeature.Applications);
        Assert.Equal(1, BoltCircleFeature.Applications);
        Assert.All(second.Statuses, s => Assert.Equal(FeatureOutcome.Cached, s.Outcome));

        // Change the middle feature: the plate stays cached, downstream re-runs.
        history.Replace(1, new BoltCircleFeature { Count = 6 });
        var third = history.Regenerate();
        Assert.Equal(1, PlateFeature.Applications);
        Assert.Equal(2, BoltCircleFeature.Applications);
        Assert.Equal(FeatureOutcome.Cached, third.Statuses[0].Outcome);
        Assert.Equal(FeatureOutcome.Applied, third.Statuses[1].Outcome);
        Assert.Equal(FeatureOutcome.Applied, third.Statuses[2].Outcome);

        // Change the first feature: everything re-runs.
        history.Replace(0, new PlateFeature { Width = 50 });
        history.Regenerate();
        Assert.Equal(2, PlateFeature.Applications);
        Assert.Equal(3, BoltCircleFeature.Applications);
    }

    [Fact]
    public void Validation_FailsBeforeApply_AndSkipsDownstream()
    {
        var history = BuildHistory();
        history.Replace(1, new BoltCircleFeature { Count = 99 }); // Max = 24
        var result = history.Regenerate();

        Assert.False(result.Succeeded);
        Assert.Equal(FeatureOutcome.Applied, result.Statuses[0].Outcome);
        Assert.Equal(FeatureOutcome.Failed, result.Statuses[1].Outcome);
        Assert.Contains("Count", result.Statuses[1].Error);
        Assert.Equal(FeatureOutcome.Skipped, result.Statuses[2].Outcome);
        // Last good prefix survives.
        Assert.NotNull(result.Body);
        Assert.True(result.Body!.ToMesh().IsClosed);
    }

    [Fact]
    public void Failure_IsContainedWithMessage()
    {
        var history = new FeatureHistory();
        history.Add(new PlateFeature());
        history.Add(Feature.FromFunc("boom", _ => throw new InvalidOperationException("bad geometry")));
        history.Add(new FilletRimFeature());

        var result = history.Regenerate();
        Assert.Equal(FeatureOutcome.Failed, result.Statuses[1].Outcome);
        Assert.Contains("bad geometry", result.Statuses[1].Error);
        Assert.Equal(FeatureOutcome.Skipped, result.Statuses[2].Outcome);
        Assert.NotNull(result.Body);
    }

    [Fact]
    public void Suppression_PassesBodyThrough()
    {
        var history = BuildHistory();
        history.Replace(1, new BoltCircleFeature { Suppressed = true });
        var result = history.Regenerate();
        Assert.True(result.Succeeded);
        Assert.Equal(FeatureOutcome.Suppressed, result.Statuses[1].Outcome);

        double volume = history.Result!.ToMesh().Volume();
        double solidPlate = Shape.Extrude(Sketch.RoundedRectangle(40, 30, 4), 8)
            .Fillet(1.5, s => s.PlanarFacesWithNormal(Vector3d.UnitZ))
            .ToMesh().Volume();
        Assert.True(Math.Abs(volume - solidPlate) < 1e-9);
    }

    [Fact]
    public void JsonParameters_RoundTripAndInvalidate()
    {
        var history = BuildHistory(width: 40, holes: 4);
        history.Regenerate();
        string saved = history.SaveParameters();
        Assert.Contains("\"Width\"", saved);

        // Override via JSON (init-only properties set through reflection).
        var warnings = history.LoadParameters("""
            { "PlateFeature": { "Width": 60 }, "BoltCircleFeature": { "Count": 6 },
              "Ghost": { "X": 1 }, "FilletRimFeature": { "Nope": 2 } }
            """);
        Assert.Equal(2, warnings.Count);

        var result = history.Regenerate();
        Assert.True(result.Succeeded, result.ToString());
        Assert.All(result.Statuses, s => Assert.Equal(FeatureOutcome.Applied, s.Outcome)); // all dirty
        var plate = (PlateFeature)history.Features[0];
        Assert.Equal(60, plate.Width);

        // Restoring the saved values brings the original result back.
        history.LoadParameters(saved);
        history.Regenerate();
        Assert.Equal(40, ((PlateFeature)history.Features[0]).Width);
    }

    /// <summary>
    /// A feature with OPTIONAL parameters — "null means take the default from somewhere
    /// else", which is what a per-flange radius override, a per-instance thickness or any
    /// inherited setting is. Every optional shape the seam has to carry: a number, an
    /// integer, a flag and an enum.
    /// </summary>
    private sealed class OptionalsFeature : Feature
    {
        [Param(Min = 0.1, Units = "mm", Description = "null = inherit")]
        public double? Radius { get; init; }

        [Param(Min = 1, Max = 64)]
        public int? Segments { get; init; }

        [Param]
        public bool? Rounded { get; init; }

        [Param]
        public DisplayMode? Mode { get; init; }

        public override Shape Apply(FeatureContext context) =>
            Shape.Cylinder(Radius ?? 5, 10);
    }

    /// <summary>
    /// Nullable <c>[Param]</c> values round-trip. Before this, <c>Convert</c> had no
    /// nullable case and threw "unsupported parameter type Nullable`1" — which
    /// <c>ApplyParameters</c> swallows into a warning, so the value was SILENTLY DROPPED
    /// on load and the feature came back at its constructor default. The rest of the seam
    /// already handled it: <c>SerializeValue</c> passes null through and
    /// <c>Feature.FormatValue</c> renders it "null" in the cache key.
    /// </summary>
    [Fact]
    public void NullableParameters_RoundTripThroughJson_IncludingNull()
    {
        var history = new FeatureHistory();
        history.Add(new OptionalsFeature
        {
            Radius = 7.25,
            Segments = 12,
            Rounded = true,
            Mode = DisplayMode.Wireframe,
        });

        string saved = history.SaveParameters();

        // Set every one to null through the JSON seam: "not stated" is a value JSON has.
        var warnings = history.LoadParameters("""
            { "OptionalsFeature": { "Radius": null, "Segments": null,
                                    "Rounded": null, "Mode": null } }
            """);
        Assert.Empty(warnings);

        var cleared = (OptionalsFeature)history.Features[0];
        Assert.Null(cleared.Radius);
        Assert.Null(cleared.Segments);
        Assert.Null(cleared.Rounded);
        Assert.Null(cleared.Mode);

        // And the saved values come back — this is the half that used to fail silently.
        Assert.Empty(history.LoadParameters(saved));
        var restored = (OptionalsFeature)history.Features[0];
        Assert.Equal(7.25, restored.Radius);
        Assert.Equal(12, restored.Segments);
        Assert.True(restored.Rounded);
        Assert.Equal(DisplayMode.Wireframe, restored.Mode);

        // The file is a fixed point, which is what says nothing was quietly re-defaulted.
        Assert.Equal(saved, history.SaveParameters());
    }

    /// <summary>A null optional is still a cache-key term, so clearing one re-runs the
    /// feature rather than replaying a stale body; and a range on an optional does not
    /// fire on "not stated", which has no value to be out of range.</summary>
    [Fact]
    public void ANullOptional_InvalidatesTheCache_AndPassesRangeValidation()
    {
        var history = new FeatureHistory();
        var feature = new OptionalsFeature { Radius = 7.25 };
        history.Add(feature);
        Assert.Equal(FeatureOutcome.Applied, history.Regenerate().Statuses[0].Outcome);
        Assert.Equal(FeatureOutcome.Cached, history.Regenerate().Statuses[0].Outcome);

        history.LoadParameters("""{ "OptionalsFeature": { "Radius": null } }""");
        var result = history.Regenerate();
        Assert.Equal(FeatureOutcome.Applied, result.Statuses[0].Outcome);
        Assert.True(result.Succeeded, result.ToString());   // Min = 0.1 does not fire on null
    }

    [Fact]
    public void NullableParameters_RoundTripThroughAWholeHistory()
    {
        var registry = new FeatureRegistry();
        registry.Register<OptionalsFeature>();
        var history = new FeatureHistory();
        history.Add(new OptionalsFeature { Radius = 3.5, Rounded = false });

        string json = history.SaveHistory();
        var loaded = FeatureHistory.LoadHistory(json, registry);
        Assert.True(loaded.Complete, string.Join("; ", loaded.Warnings));
        Assert.Equal(json, loaded.History.SaveHistory());

        var back = (OptionalsFeature)loaded.History.Features[0];
        Assert.Equal(3.5, back.Radius);
        Assert.False(back.Rounded);
        Assert.Null(back.Segments);
        Assert.Null(back.Mode);
    }

    [Fact]
    public void Selectors_SurviveUpstreamParameterChanges()
    {
        // The fillet selects "top planar face" semantically; growing the plate must
        // not break it (queries re-run on the regenerated body).
        var history = BuildHistory(width: 40);
        Assert.True(history.Regenerate().Succeeded);

        history.LoadParameters("""{ "PlateFeature": { "Width": 80, "Thickness": 12 } }""");
        var result = history.Regenerate();
        Assert.True(result.Succeeded, result.ToString());
        Assert.True(history.Result!.ToMesh().IsClosed);
    }

    [Fact]
    public void PartRegenerate_SwapsGeometryAndClearsEveryCache()
    {
        var history = new FeatureHistory();
        history.Add(new ExtrudeSketchFeature(Sketch.Rectangle(20, 10)) { Name = "Base", Height = 6 });
        var part = history.ToPart("plate");

        // Prime the caches the regenerate must clear.
        double before = part.GetMesh().Volume();
        Assert.NotNull(part.TryGetSolid());
        Assert.NotNull(part.ConstructionTree());
        Assert.Equal(20 * 10 * 6, before, 9);

        history.LoadParameters("""{ "Base": { "Height": 12 } }""");
        var result = part.Regenerate();

        Assert.True(result.Succeeded, result.ToString());
        Assert.Same(result.Body, part.Geometry);              // the fresh body is live
        Assert.False(part.HasMesh);                           // mesh cache cleared
        Assert.Equal(20 * 10 * 12, part.GetMesh().Volume(), 9);
        // The tree was rebuilt: the Height parameter row shows the new value.
        var heightRow = part.ConstructionTree()!.Children[0].Children.Single(c => c.Label == "Height");
        Assert.Contains("12", heightRow.Detail);
    }

    [Fact]
    public void PartRegenerate_FailureKeepsThePreviousGeometry()
    {
        var history = new FeatureHistory();
        history.Add(new ExtrudeSketchFeature(Sketch.Rectangle(20, 10)) { Name = "Base", Height = 6 });
        var part = history.ToPart("plate");
        var geometryBefore = part.Geometry;
        double before = part.GetMesh().Volume();

        history.LoadParameters("""{ "Base": { "Height": -1 } }""");   // violates Min
        var result = part.Regenerate();

        Assert.False(result.Succeeded);
        Assert.Same(geometryBefore, part.Geometry);           // untouched
        Assert.True(part.HasMesh);                            // caches untouched too
        Assert.Equal(before, part.GetMesh().Volume(), 9);
    }

    [Fact]
    public void PartRegenerate_RequiresAHistory()
    {
        var part = new Part("box", Shape.Box(1, 1, 1));
        var exception = Assert.Throws<InvalidOperationException>(() => part.Regenerate());
        Assert.Contains("no feature history", exception.Message);
    }

    [Fact]
    public void DuplicateNames_GetSuffixes()
    {
        var history = new FeatureHistory();
        var first = new PlateFeature();
        var second = new PlateFeature { Width = 20 };
        history.Add(first);
        history.Add(second);
        Assert.Equal("PlateFeature", history.NameOf(first));
        Assert.Equal("PlateFeature.2", history.NameOf(second));
    }
}
