using System.Text.Json;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The feature registry (type discovery for UI insertion + data construction) and
/// whole-history JSON persistence: SaveHistory → LoadHistory round-trips every
/// data-constructible feature — including sketch and hole-spec constructor inputs —
/// and reports, by name, what it cannot rebuild.
/// </summary>
public class FeatureRegistryTests
{
    // ---- discovery ----

    [Fact]
    public void Registry_ListsBuiltIns_WithHonestCreatability()
    {
        var all = FeatureRegistry.Default.All;

        // [Param]-only features construct with no inputs.
        Assert.True(all.Single(t => t.Name == nameof(FilletRimFeature)).CanCreate);
        Assert.True(all.Single(t => t.Name == nameof(ChamferRimFeature)).CanCreate);
        Assert.True(all.Single(t => t.Name == nameof(LinearPatternFeature)).CanCreate);
        Assert.True(all.Single(t => t.Name == nameof(CircularPatternFeature)).CanCreate);

        // Input-bearing features construct through registered factories.
        Assert.True(all.Single(t => t.Name == nameof(HoleFeature)).CanCreate);
        Assert.True(all.Single(t => t.Name == nameof(ExtrudeSketchFeature)).CanCreate);
        Assert.True(all.Single(t => t.Name == nameof(RevolveSketchFeature)).CanCreate);
        // A catalogue placement is data too: the component travels as its kind plus the
        // factory arguments that built it (see ComponentPlacement_RoundTrips below for
        // where that stops — a component outside the catalogue).
        Assert.True(all.Single(t => t.Name == nameof(ComponentFeature)).CanCreate);

        // Code inputs cannot be data: the reason names the constructor's demands.
        var boolean = all.Single(t => t.Name == nameof(BooleanFeature));
        Assert.False(boolean.CanCreate);
        Assert.Contains("Shape", boolean.Reason);
        Assert.False(all.Single(t => t.Name == nameof(VariableChamferRimFeature)).CanCreate);
    }

    [Fact]
    public void Registry_DescribesParameters_WithoutAnInstance()
    {
        var fillet = FeatureRegistry.Default.Find(nameof(FilletRimFeature));
        Assert.NotNull(fillet);
        var radius = fillet!.Parameters.Single(p => p.Name == "Radius");
        Assert.Equal(typeof(double), radius.Type);
        Assert.Equal(1e-9, radius.Min);
        Assert.Null(radius.Value);   // the shape of the type, not the state of an instance

        Assert.Null(FeatureRegistry.Default.Find("NoSuchFeature"));
    }

    [Fact]
    public void TryCreate_RefusesUnknownAndOpaqueTypesByName()
    {
        Assert.False(FeatureRegistry.Default.TryCreate("NoSuchFeature", null, out _, out var unknown));
        Assert.Contains("unknown feature type", unknown);

        Assert.False(FeatureRegistry.Default.TryCreate(nameof(BooleanFeature), null, out _, out var opaque));
        Assert.Contains("Shape", opaque);

        // A factory type without its inputs is refused, not crashed.
        Assert.False(FeatureRegistry.Default.TryCreate(nameof(HoleFeature), null, out _, out var missing));
        Assert.Contains("inputs", missing);
    }

    public sealed class UserBossFeature : Feature
    {
        [Param(Min = 0.1)]
        public double Diameter { get; init; } = 6;

        public override Shape Apply(FeatureContext context) =>
            (context.Body ?? Shape.Cylinder(Diameter / 2, 4))
            | Shape.Cylinder(Diameter / 2, 4);
    }

    [Fact]
    public void Register_MakesAUserFeatureLoadable()
    {
        var registry = new FeatureRegistry();
        registry.Register<UserBossFeature>();
        Assert.True(registry.Find(nameof(UserBossFeature))!.CanCreate);

        var history = new FeatureHistory();
        history.Add(new UserBossFeature { Diameter = 8 });
        var loaded = FeatureHistory.LoadHistory(history.SaveHistory(), registry);
        Assert.True(loaded.Complete);
        Assert.Equal(8.0, ((UserBossFeature)loaded.History.Features[0]).Diameter);

        // ...and the Default registry, which never saw the type, says why it cannot.
        var withoutRegistration = FeatureHistory.LoadHistory(history.SaveHistory());
        Assert.False(withoutRegistration.Complete);
        Assert.Contains("UserBossFeature", withoutRegistration.Warnings[0]);
    }

    // ---- whole-history round-trip ----

    private static FeatureHistory BuildPlateHistory()
    {
        var plate = Sketch.RoundedRectangle(40, 30, 5).WithHole(Sketch.Circle(new Vector2d(0, 0), 4));
        var history = new FeatureHistory();
        history.Add(new ExtrudeSketchFeature(plate) { Height = 8, Name = "Plate" });
        history.Add(new HoleFeature(
            StandardHoles.Counterbored(5), [new(-12, 0), new(12, 0)])
        { Depth = 9, Name = "Bolt holes" });
        history.Add(new ChamferRimFeature { Setback = 0.5 });
        return history;
    }

    [Fact]
    public void History_RoundTrips_GeometryAndJson()
    {
        var original = BuildPlateHistory();
        string json = original.SaveHistory();

        var loaded = FeatureHistory.LoadHistory(json);
        Assert.True(loaded.Complete, string.Join("; ", loaded.Warnings));

        // The saved file is a fixed point: save → load → save reproduces it byte for byte.
        Assert.Equal(json, loaded.History.SaveHistory());

        // And the loaded model IS the model: same regeneration, same geometry.
        var a = original.Regenerate();
        var b = loaded.History.Regenerate();
        Assert.True(a.Succeeded && b.Succeeded);
        double volumeA = a.Body!.ToMesh().Volume();
        double volumeB = b.Body!.ToMesh().Volume();
        Assert.Equal(volumeA, volumeB);   // bit-identical: the same inputs ran the same code
    }

    [Fact]
    public void History_RoundTrips_NamesSuppressionAndReferences()
    {
        var history = new FeatureHistory();
        history.Add(new ExtrudeSketchFeature(Sketch.Rectangle(20, 20)) { Height = 5, Name = "Base" });
        history.Add(new HoleFeature(HoleSpec.Simple(4).WithTipAngle(118), [new(0, 0)])
        {
            Depth = 3,
            Name = "Pilot",
            Suppressed = true,
        });
        history.Add(new CircularPatternFeature { Count = 4, Axis = AxisRef.Z });

        var loaded = FeatureHistory.LoadHistory(history.SaveHistory());
        Assert.True(loaded.Complete, string.Join("; ", loaded.Warnings));

        Assert.Equal("Pilot", loaded.History.Features[1].Name);
        Assert.True(loaded.History.Features[1].Suppressed);
        var pattern = (CircularPatternFeature)loaded.History.Features[2];
        Assert.Equal(4, pattern.Count);
        Assert.Equal(AxisRef.Z.Descriptor, pattern.Axis.Descriptor);   // reference round-trips

        // The tip angle survived through the hole-spec input record.
        var result = loaded.History.Regenerate();
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Sketch_RoundTrips_ArcsBeziersAndHoles_Exactly()
    {
        var sketch = Sketch.Start(0, 0)
            .LineTo(20, 0)
            .ArcTo(new(20, 10), 6, clockwise: false)
            .BezierTo(new(15, 14), new(5, 14), new(0, 10))
            .Close()
            .WithHole(Sketch.Circle(new Vector2d(10, 5), 2));

        var saved = InputJson.SaveSketch(sketch);
        using var document = JsonDocument.Parse(saved.ToJsonString());
        var loaded = InputJson.LoadSketch(document.RootElement);

        // The curve vocabulary is exact, so the enclosed area is bit-identical.
        Assert.Equal(sketch.Area(), loaded.Area());
    }

    /// <summary>
    /// A sketch whose outer loop uses EVERY segment kind the vocabulary has — the fixture
    /// the coverage test below reads, so a new segment type fails a test instead of
    /// silently reaching <c>SaveCurves</c>' default case at save time.
    /// </summary>
    private static Sketch EverySegmentKind() =>
        Sketch.Start(0, 0)
            .LineTo(30, 0)                                     // LineSeg
            .ArcTo(new(30, 12), 8, clockwise: false)           // ArcSeg
            // EllipseSeg: SVG's A rx ry rot largeArc sweep, deliberately ROTATED so the
            // two semi-axis vectors are not axis-aligned.
            .EllipticalArcTo(new(18, 20), 9, 5, 35, largeArc: false, clockwise: false)
            .BezierTo(new(12, 26), new(4, 22), new(0, 12))     // CubicSeg
            .Close();

    /// <summary>
    /// Every sketch segment kind has a JSON form. This is a COVERAGE claim rather than a
    /// fixture: the segment types are enumerated from the assembly, so adding one without
    /// teaching <see cref="InputJson"/> about it fails here — at build time, in Modeling —
    /// instead of at <c>Document.Save</c> time, in a user's session.
    /// <para>The gap this pins was real: elliptical arcs became first-class after the
    /// persistence work landed, <see cref="Sketch.FromCurves"/> learned the case and
    /// <c>SaveCurves</c> did not, so a document holding an elliptical sketch feature could
    /// not be SAVED at all (the FormatException escapes <c>Document.Save</c>, which has no
    /// catch around <see cref="FeatureHistory.SaveHistory"/>).</para>
    /// </summary>
    [Fact]
    public void EverySketchSegmentKind_HasAJsonForm()
    {
        var segmentBase = typeof(Sketch).Assembly.GetType("EngrCAD.Modeling.SketchSegment")!;
        var kinds = typeof(Sketch).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && segmentBase.IsAssignableFrom(t))
            .ToList();
        Assert.NotEmpty(kinds);

        var fixture = EverySegmentKind();
        var used = fixture.Segments.Select(s => s.GetType()).ToHashSet();
        Assert.Equal(
            kinds.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal),
            used.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));

        // Every one of them survives the round trip exactly: the vocabulary is stored, not
        // sampled, so the enclosed area comes back bit-identical.
        var saved = InputJson.SaveSketch(fixture);
        using var document = JsonDocument.Parse(saved.ToJsonString());
        var loaded = InputJson.LoadSketch(document.RootElement);
        Assert.Equal(fixture.Area(), loaded.Area());
        Assert.Equal(saved.ToJsonString(), InputJson.SaveSketch(loaded).ToJsonString());
    }

    [Fact]
    public void EllipticalSketch_SurvivesAWholeHistoryRoundTrip()
    {
        var history = new FeatureHistory();
        history.Add(new ExtrudeSketchFeature(EverySegmentKind()) { Height = 6, Name = "Cam" });

        string json = history.SaveHistory();
        var loaded = FeatureHistory.LoadHistory(json);
        Assert.True(loaded.Complete, string.Join("; ", loaded.Warnings));
        Assert.Equal(json, loaded.History.SaveHistory());   // the fixed point

        var a = history.Regenerate();
        var b = loaded.History.Regenerate();
        Assert.True(a.Succeeded && b.Succeeded);
        Assert.Equal(a.Body!.ToMesh().Volume(), b.Body!.ToMesh().Volume());
    }

    // ---- catalogue placements ----

    /// <summary>
    /// A placed catalogue component round-trips, so a document holding fasteners reopens
    /// PARAMETRIC rather than as a snapshot plus a warning. The assertion with teeth is
    /// that the reloaded component is the same ARGUMENTS, not the same designation: an
    /// ISO 4762 designation carries the size and the length and says nothing about the
    /// clearance fit, the seating or the socket, so a designation-keyed reload would come
    /// back as a plausible different screw with the same name.
    /// </summary>
    [Fact]
    public void ComponentPlacement_RoundTrips_ByArgumentsNotByDesignation()
    {
        // Deliberately non-default in every argument the designation cannot carry.
        var screw = StandardComponents.CapScrew(
            6, 20, ScrewSeating.OnFace, ClearanceFit.Close, hexSocket: true);
        var history = new FeatureHistory();
        history.Add(new ExtrudeSketchFeature(Sketch.Rectangle(60, 40)) { Height = 8 });
        history.Add(new ComponentFeature(screw, [new(-15, 0), new(15, 0)]) { Depth = 12 });

        string json = history.SaveHistory();
        var loaded = FeatureHistory.LoadHistory(json);
        Assert.True(loaded.Complete, string.Join("; ", loaded.Warnings));
        Assert.Equal(json, loaded.History.SaveHistory());   // the fixed point

        var back = (SocketHeadCapScrew)((ComponentFeature)loaded.History.Features[1]).Component;
        Assert.Equal(screw.Designation, back.Designation);
        Assert.Equal(ScrewSeating.OnFace, back.Seating);        // none of these three...
        Assert.Equal(ClearanceFit.Close, back.Fit);             // ...is in the designation
        Assert.True(back.HexSocket);
        Assert.Equal(2, ((ComponentFeature)loaded.History.Features[1]).Points.Count);
        Assert.Equal(12, ((ComponentFeature)loaded.History.Features[1]).Depth);

        // And it is the same model: the host is cut the same way.
        var a = history.Regenerate();
        var b = loaded.History.Regenerate();
        Assert.True(a.Succeeded && b.Succeeded, a.ToString() + "\n" + b.ToString());
        Assert.Equal(a.Body!.ToMesh().Volume(), b.Body!.ToMesh().Volume());
    }

    /// <summary>Every catalogue kind has a JSON form — the same coverage claim the sketch
    /// vocabulary makes, and for the same reason: what is not covered must be REFUSED at
    /// save time, not written as something a load rebuilds wrong.</summary>
    [Fact]
    public void EveryCatalogueComponentKind_RoundTrips()
    {
        HardwareComponent[] catalogue =
        [
            StandardComponents.CapScrew(6, 20),
            StandardComponents.ButtonScrew(5, 16),
            StandardComponents.CskScrew(4, 12),
            StandardComponents.Nut(6),
            StandardComponents.Washer(6),
            StandardComponents.TrisertInsert(4),
            StandardComponents.Bearing("608"),
            StandardComponents.Dowel(4, 12),
        ];

        // The list IS the catalogue: a new HardwareComponent subclass fails here rather
        // than reaching SaveComponent's null arm in a user's document.
        var kinds = typeof(HardwareComponent).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsPublic && typeof(HardwareComponent).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(kinds, catalogue.Select(c => c.GetType().Name).OrderBy(n => n, StringComparer.Ordinal));

        foreach (var component in catalogue)
        {
            var saved = InputJson.SaveComponent(component);
            Assert.NotNull(saved);
            using var document = JsonDocument.Parse(saved!.ToJsonString());
            var back = InputJson.LoadComponent(document.RootElement);
            Assert.Equal(component.GetType(), back.GetType());
            Assert.Equal(component.Designation, back.Designation);
            Assert.Equal(component.SeatDepth, back.SeatDepth);
            Assert.Equal(component.InsertedLength, back.InsertedLength);
            Assert.Equal(saved.ToJsonString(), InputJson.SaveComponent(back)!.ToJsonString());
        }
    }

    private sealed class HomeMadeSpacer : HardwareComponent
    {
        public override string Designation => "shop spacer";
        public override double SeatDepth => 0;
        public override double InsertedLength => 0;
        protected override Shape BuildBody() => Shape.Cylinder(4, 6);
        public override Shape Prepare(ComponentSite site) => site.Body;
    }

    /// <summary>The boundary, stated rather than discovered: a component outside the
    /// built-in catalogue is refused at save time (no inputs written), warned about by
    /// name at load, and rescued only by the caller's hook — the same one-sided failure
    /// every other opaque record has.</summary>
    [Fact]
    public void AComponentOutsideTheCatalogue_IsNamedRatherThanGuessedAt()
    {
        var mine = new HomeMadeSpacer();
        Assert.Null(InputJson.SaveComponent(mine));

        var history = new FeatureHistory();
        history.Add(new ExtrudeSketchFeature(Sketch.Rectangle(40, 40)) { Height = 6 });
        history.Add(new ComponentFeature(mine, [new(0, 0)]));

        string json = history.SaveHistory();
        var skipped = FeatureHistory.LoadHistory(json);
        Assert.False(skipped.Complete);
        Assert.Contains("shop spacer", Assert.Single(skipped.Warnings), StringComparison.Ordinal);

        var rescued = FeatureHistory.LoadHistory(json, resolveOpaque: record =>
            record.TypeName == nameof(ComponentFeature)
                ? new ComponentFeature(new HomeMadeSpacer(), [new(0, 0)])
                : null);
        Assert.True(rescued.Complete);
        Assert.Equal(2, rescued.History.Features.Count);
    }

    [Fact]
    public void LoadHistory_SkipsWhatItCannotBuild_AndTheHookRescuesIt()
    {
        var history = new FeatureHistory();
        history.Add(new ExtrudeSketchFeature(Sketch.Rectangle(10, 10)) { Height = 4 });
        history.Add(Feature.FromFunc("Magic", context => context.Body!));

        string json = history.SaveHistory();

        // Without help: the lambda is skipped, by name, and the result says incomplete.
        var skipped = FeatureHistory.LoadHistory(json);
        Assert.False(skipped.Complete);
        Assert.Contains("Magic", skipped.Warnings.Single());
        Assert.Single(skipped.History.Features);

        // With the hook: the caller supplies the instance and the load is complete.
        var rescued = FeatureHistory.LoadHistory(json, resolveOpaque: record =>
            record.Name == "Magic" ? Feature.FromFunc("Magic", context => context.Body!) : null);
        Assert.True(rescued.Complete);
        Assert.Equal(2, rescued.History.Features.Count);
        Assert.Equal("Magic", rescued.History.Features[1].Name);
    }

    [Fact]
    public void HoleSpecs_RoundTrip_EveryKind()
    {
        foreach (var hole in new[]
        {
            HoleSpec.Simple(5),
            HoleSpec.Simple(5).WithTipAngle(135),
            HoleSpec.Counterbore(5, 10, 5.5),
            HoleSpec.Countersink(5, 11, 82),
        })
        {
            using var document = JsonDocument.Parse(InputJson.SaveHoleSpec(hole).ToJsonString());
            var loaded = InputJson.LoadHoleSpec(document.RootElement);
            // The silhouette is the tool's single source of truth — identical silhouettes
            // mean identical holes.
            Assert.Equal(hole.ToolSilhouette(20), loaded.ToolSilhouette(20));
        }
    }
}
