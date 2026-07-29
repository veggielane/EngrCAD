using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Whole-document persistence: one envelope tying together the scene structure, each
/// part's feature history, assemblies and poses, mates, annotations and results.
///
/// <para>The strong assertion here is the FIXED POINT — <c>save -&gt; load -&gt; save</c>
/// must be byte-identical — because it is the only check that catches a field written but
/// not read, a default that round-trips to a different default, or an ordering that is not
/// a function of the model. Volumes and poses can agree while the file quietly drifts.</para>
/// </summary>
public class DocumentPersistenceTests
{
    // ---- fixtures -------------------------------------------------------

    private static Sketch PlateOutline() => Sketch.Rectangle(40, 30);

    /// <summary>A history whose every feature is registry-constructible, so the whole
    /// thing round-trips: extrude a rectangle, drill two holes.</summary>
    private static FeatureHistory PlateHistory()
    {
        var history = new FeatureHistory();
        history.Add(new ExtrudeSketchFeature(PlateOutline()) { Height = 6 });
        history.Add(new HoleFeature(HoleSpec.Simple(5), [new(-12, 0), new(12, 0)]) { Depth = 8 });
        return history;
    }

    private static Frame3d At(double x, double y, double z) =>
        Frame3d.FromXY((x, y, z), Vector3d.UnitX, Vector3d.UnitY);

    /// <summary>
    /// The realistic document every round-trip test uses: two tabs, a history-backed
    /// plate carrying annotations and a simulation result, a snapshot part with no
    /// construction record, a nested assembly placing the plate twice, explode offsets,
    /// and a mate set.
    /// </summary>
    private static Document Fixture(bool includeOpaque = true)
    {
        var scene = new Scene(new MeshQuality { SegmentsPerCircle = 16, CurveSamples = 12 });

        var plate = new Part("plate", PlateHistory(), Palette.Steel)
        {
            Transform = Matrix4d.CreateTranslation((0, 0, 1)),
            DisplayMode = DisplayMode.Translucent,
            ClippedBySection = false,
        };
        plate.Annotate(new LinearDimension((-20, 0, 6), (20, 0, 6))
        {
            Offset = (0, 0, 12),
            Tolerance = ToleranceSpec.Symmetric(0.1),
        });
        plate.Annotate(new LeaderNote((0, 0, 6), "DEBURR ALL EDGES"));
        plate.Annotate(new DatumLabel((0, -15, 3), "A") { Offset = (0, -8, 0) });
        plate.Annotate(new AngularDimension((0, 0, 0), (10, 0, 0), (0, 10, 0))
        {
            Label = "90 NOM",
        });
        // A selector-backed dimension: a lambda, so it cannot be rebuilt from data.
        if (includeOpaque)
        {
            plate.Annotate(LinearDimension.BetweenFaces(
                s => s.Faces.First(), s => s.Faces.Skip(1).First()));
        }

        int vertices = plate.GetMesh(scene.Options).VertexCount;
        plate.AddResult(MeshField.Scalar("stress", "MPa",
            [.. Enumerable.Range(0, vertices).Select(i => i * 0.5)]));
        plate.FieldDisplay = new FieldDisplay
        {
            Field = "stress",
            ColorMap = FieldColorMap.Diverging,
            Range = new FieldRange(-10, 10),
        };

        // No history, no serialized graph: a snapshot part.
        var jig = new Part("jig", MeshPrimitives.Box(8, 8, 8), Palette.Brass) { Ghost = true };

        var model = scene.AddTab("Model");
        model.Add(plate);

        var carrier = new Assembly("carrier");
        carrier.Add(jig, At(2, 3, 4));
        var rig = new Assembly("rig");
        var lower = rig.Add(plate);
        var upper = rig.Add(plate, At(0, 0, 20));
        upper.ExplodeOffset = (0, 0, 30);
        rig.Add(carrier, At(15, 0, 0));
        model.Add(rig);

        var spares = scene.AddTab("Spares");
        spares.Add(new Part("shim", Shape.Box(10, 10, 1)));

        var document = new Document(scene);
        document.Mates.Add(new MateSet(rig)
            .Ground(lower)
            .Add(Mate.Planar(
                MateGeometry.PlanarFace(lower, FaceRef.Top),
                MateGeometry.PlanarFace(upper, FaceRef.Bottom),
                gap: 2, name: "stack")));
        return document;
    }

    // ---- round trip -----------------------------------------------------

    [Fact]
    public void Document_RoundTrips_PreservingStructurePosesAndVolumes()
    {
        var original = Fixture();
        string json = original.Save();

        var result = Document.Load(json);
        var scene = result.Scene;

        // Tabs, parts, assemblies.
        Assert.Equal(["Model", "Spares"], scene.Tabs.Select(t => t.Name));
        Assert.Equal(["plate"], scene.Tabs[0].Parts.Select(p => p.Name));
        Assert.Equal(["rig"], scene.Tabs[0].Assemblies.Select(a => a.Name));

        // Instances: same count, same paths, same world poses. The plate is placed twice
        // and shared by reference, so the flattening is the real structural check.
        var before = original.Scene.AllInstances.ToList();
        var after = scene.AllInstances.ToList();
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(before.Select(i => i.Path), after.Select(i => i.Path));
        for (int i = 0; i < before.Count; i++)
            Assert.Equal(before[i].World, after[i].World);

        // Distinct parts are still SHARED, not duplicated per placement.
        Assert.Equal(original.Scene.AllParts.Count(), scene.AllParts.Count());

        // Geometry: the history regenerated to the same solid, the snapshot came back
        // as the same mesh.
        foreach (var (a, b) in before.Zip(after))
        {
            Assert.Equal(
                MeshMassProperties.Compute(a.Part.GetMesh(original.Scene.Options)).Volume,
                MeshMassProperties.Compute(b.Part.GetMesh(scene.Options)).Volume,
                9);
        }

        // Display metadata.
        var plate = scene.Tabs[0].Parts[0];
        Assert.Equal(DisplayMode.Translucent, plate.DisplayMode);
        Assert.False(plate.ClippedBySection);
        Assert.Equal(Matrix4d.CreateTranslation((0, 0, 1)), plate.Transform);
        Assert.Equal(Palette.Steel, plate.Color);

        // Explode offsets and mates.
        var rig = scene.Tabs[0].Assemblies[0];
        Assert.Equal(new Vector3d(0, 0, 30), rig.Occurrences[1].ExplodeOffset);
        var mates = Assert.Single(result.Document.Mates);
        Assert.Equal("stack", Assert.Single(mates.Mates).Name);
        Assert.Equal(2, Assert.Single(mates.Mates).Value);
        Assert.Single(mates.Grounded);
    }

    [Fact]
    public void SaveLoadSave_IsAByteIdenticalFixedPoint()
    {
        string first = Fixture(includeOpaque: false).Save();
        string second = Document.Load(first).Document.Save();
        Assert.Equal(first, second);
    }

    /// <summary>
    /// The fixed point holds for everything that ROUND-TRIPS, which is the honest claim:
    /// a file carrying opaque records (a lambda-backed dimension here) is smaller the
    /// second time round by exactly those records — the ones the load already warned
    /// about — and is a fixed point from there on. That is the difference between "a
    /// record was reported and then dropped" and "the file is drifting".
    /// </summary>
    [Fact]
    public void ASecondSave_LosesExactlyTheRecordsTheLoadWarnedAbout()
    {
        string first = Fixture().Save();
        var loaded = Document.Load(first);
        string second = loaded.Document.Save();

        Assert.NotEqual(first, second);
        Assert.Contains("opaque", first, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque", second, StringComparison.Ordinal);
        Assert.Single(loaded.Warnings);

        // From the second save on, nothing more is lost.
        Assert.Equal(second, Document.Load(second).Document.Save());
    }

    [Fact]
    public void LoadedHistory_RegeneratesToTheSameGeometry()
    {
        var original = Fixture();
        var plateBefore = original.Scene.Tabs[0].Parts[0];
        var plateAfter = Document.Load(original.Save()).Scene.Tabs[0].Parts[0];

        Assert.NotNull(plateAfter.History);
        Assert.Equal(2, plateAfter.History!.Features.Count);

        // Regenerating the loaded history again must not change it — the parametric
        // model survived, it was not merely snapshotted.
        var regenerated = plateAfter.Regenerate();
        Assert.True(regenerated.Succeeded, regenerated.ToString());
        Assert.Equal(
            MeshMassProperties.Compute(plateBefore.GetMesh(original.Scene.Options)).Volume,
            MeshMassProperties.Compute(plateAfter.GetMesh(original.Scene.Options)).Volume,
            9);
    }

    [Fact]
    public void EditingALoadedParameter_ChangesTheGeometry()
    {
        var plate = Document.Load(Fixture().Save()).Scene.Tabs[0].Parts[0];
        double before = MeshMassProperties.Compute(plate.GetMesh()).Volume;

        plate.History!.LoadParameters("""{ "ExtrudeSketchFeature": { "Height": 12 } }""");
        Assert.True(plate.Regenerate().Succeeded);

        double after = MeshMassProperties.Compute(plate.GetMesh()).Volume;
        Assert.True(after > before * 1.7, $"{before} -> {after}");
    }

    // ---- annotations ----------------------------------------------------

    [Fact]
    public void PointAnchoredAnnotations_RoundTrip_AndSelectorOnesWarn()
    {
        var result = Document.Load(Fixture().Save());
        var plate = result.Scene.Tabs[0].Parts[0];

        // Four of the five come back; the selector-backed one is reported, not dropped
        // silently.
        Assert.Equal(4, plate.Annotations.Count);
        Assert.Contains(result.Warnings, w => w.Contains("selector"));

        var resolved = plate.ResolveAnnotations();
        var linear = resolved.First(a => a.Kind == AnnotationKind.LinearDimension);
        Assert.Equal(40, linear.Value, 9);
        Assert.Equal("40 ±0.1", linear.Text);
        Assert.Equal(new Vector3d(0, 0, 12), linear.Offset);

        Assert.Equal("DEBURR ALL EDGES",
            resolved.First(a => a.Kind == AnnotationKind.LeaderNote).Text);
        Assert.Equal("A", resolved.First(a => a.Kind == AnnotationKind.DatumLabel).Text);
        Assert.Equal("90 NOM",
            resolved.First(a => a.Kind == AnnotationKind.AngularDimension).Text);
    }

    [Fact]
    public void AsymmetricTolerances_RoundTrip()
    {
        var scene = new Scene();
        var part = new Part("bar", Shape.Box(10, 10, 10));
        part.Annotate(new LinearDimension((0, 0, 0), (10, 0, 0))
        {
            Tolerance = ToleranceSpec.Limits(0.2, 0.1),
        });
        scene.Add(part);

        var loaded = Document.Load(new Document(scene).Save()).Scene.Tabs[0].Parts[0];
        Assert.Equal("10 +0.2/-0.1", loaded.ResolveAnnotations()[0].Text);
    }

    // ---- results --------------------------------------------------------

    [Fact]
    public void ResultsAndFieldDisplay_RoundTripExactly()
    {
        var original = Fixture();
        var before = original.Scene.Tabs[0].Parts[0].Result("stress")!;
        var plate = Document.Load(original.Save()).Scene.Tabs[0].Parts[0];

        var after = plate.Result("stress");
        Assert.NotNull(after);
        Assert.Equal("MPa", after!.Units);
        Assert.Equal(before.Count, after.Count);
        // A base64 double payload is EXACT — assert bits, not a tolerance.
        for (int i = 0; i < before.Values.Count; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(before.Values[i]),
                BitConverter.DoubleToInt64Bits(after.Values[i]));

        Assert.True(plate.TryResolveFieldDisplay(out var display, out string? error), error);
        Assert.Equal(FieldColorMap.Diverging, display.ColorMap);
        Assert.Equal(new FieldRange(-10, 10), display.Range);
    }

    [Fact]
    public void ResultsCanBeLeftOut()
    {
        var json = Fixture().Save(new DocumentSaveOptions { IncludeResults = false });
        var plate = Document.Load(json).Scene.Tabs[0].Parts[0];
        Assert.Empty(plate.Results);
        // The display survives and names the missing result rather than crashing.
        Assert.False(plate.TryResolveFieldDisplay(out _, out string? error));
        Assert.Contains("stress", error);
    }

    // ---- snapshots ------------------------------------------------------

    [Fact]
    public void SnapshotParts_AreNamed_AndReloadWithTheSameMesh()
    {
        var original = Fixture();
        var result = Document.Load(original.Save());

        // "jig" (a raw mesh) and "shim" (a Shape with no history) have no construction
        // record; "plate" does.
        Assert.Equal(["jig", "shim"], result.Snapshots.Order());

        var jigBefore = original.Scene.AllParts.First(p => p.Name == "jig").GetMesh(original.Scene.Options);
        var jigAfter = result.Scene.AllParts.First(p => p.Name == "jig").GetMesh();
        Assert.Equal(jigBefore.VertexCount, jigAfter.VertexCount);
        Assert.Equal(jigBefore.FaceCount, jigAfter.FaceCount);
        // The payload is binary-exact, so vertex positions match bit for bit.
        var (beforePositions, beforeFaces) = jigBefore.ToIndexed();
        var (afterPositions, afterFaces) = jigAfter.ToIndexed();
        for (int i = 0; i < beforePositions.Length; i++)
            Assert.Equal(beforePositions[i], afterPositions[i]);
        for (int f = 0; f < beforeFaces.Count; f++)
            Assert.Equal(beforeFaces[f], afterFaces[f]);
    }

    [Fact]
    public void WithoutEmbeddedGeometry_ASnapshotPartIsRefusedByName()
    {
        string json = Fixture().Save(new DocumentSaveOptions { EmbedGeometry = false });
        var result = Document.Load(json);

        Assert.Contains(result.Warnings, w => w.Contains("jig") && w.Contains("no geometry"));
        Assert.DoesNotContain(result.Scene.AllParts, p => p.Name == "jig");
        // The history-backed part still loads: a recipe needs no snapshot.
        Assert.Contains(result.Scene.AllParts, p => p.Name == "plate");
    }

    // ---- failure behaviour ----------------------------------------------

    [Fact]
    public void AnUnrecognizedFile_ThrowsRatherThanGuessing()
    {
        Assert.Throws<FormatException>(() => Document.Load("""{ "hello": 1 }"""));
        Assert.Throws<FormatException>(() => Document.Load(
            $$"""{ "format": "{{Document.Format}}", "version": 99, "tabs": [] }"""));
    }

    [Fact]
    public void AnOpaqueFeature_LoadsAsAWarning_NotAnException()
    {
        var scene = new Scene();
        var history = new FeatureHistory();
        history.Add(new ExtrudeSketchFeature(PlateOutline()) { Height = 4 });
        // A Shape-graph tool has no serialized form (FeatureRegistry says so by name).
        history.Add(new BooleanFeature(Shape.Cylinder(3, 20)) { Subtract = true });
        scene.Add(history.ToPart("cut"));

        var result = Document.Load(new Document(scene).Save());
        Assert.Contains(result.Warnings, w => w.Contains("BooleanFeature"));
        Assert.False(result.Complete);
        // The prefix that COULD be rebuilt is still there, and it still regenerates.
        var part = result.Scene.Tabs[0].Parts[0];
        Assert.Single(part.History!.Features);
        Assert.True(part.Regenerate().Succeeded);
    }

    [Fact]
    public void AnOpaqueFeature_CanBeSuppliedByTheLoadHook()
    {
        var scene = new Scene();
        var history = new FeatureHistory();
        history.Add(new ExtrudeSketchFeature(PlateOutline()) { Height = 4 });
        history.Add(new BooleanFeature(Shape.Cylinder(3, 20)) { Subtract = true });
        scene.Add(history.ToPart("cut"));

        var result = Document.Load(new Document(scene).Save(), new DocumentLoadOptions
        {
            ResolveOpaqueFeature = (part, record) => record.TypeName == nameof(BooleanFeature)
                ? new BooleanFeature(Shape.Cylinder(3, 20))
                : null,
        });
        Assert.True(result.Complete, string.Join("; ", result.Warnings));
        Assert.Equal(2, result.Scene.Tabs[0].Parts[0].History!.Features.Count);
    }

    [Fact]
    public void HardwareParts_KeepTheirGeometry_AndSayTheComponentDidNotComeBack()
    {
        var scene = new Scene();
        var screw = StandardComponents.CapScrew(6, 20).ToPart();
        scene.Add(screw);

        var result = Document.Load(new Document(scene).Save());
        Assert.Contains(result.Warnings, w => w.Contains("catalogue item"));
        var loaded = result.Scene.AllParts.Single();
        Assert.Null(loaded.Hardware);
        Assert.True(loaded.GetMesh().FaceCount > 0);
        // The one behaviour that must survive: a fastener is not cut by a section plane.
        Assert.False(loaded.ClippedBySection);
    }

    [Fact]
    public void AnEmptySceneRoundTrips()
    {
        string json = new Document(new Scene()).Save();
        var result = Document.Load(json);
        Assert.True(result.Complete);
        Assert.Empty(result.Scene.Tabs);
        Assert.Equal(json, result.Document.Save());
    }

    [Fact]
    public void ExplicitMeshQualitySurvives_AndADefaultSceneStaysDefault()
    {
        var explicitScene = new Scene(new MeshQuality { SegmentsPerCircle = 48, SdfResolution = 96 });
        explicitScene.Add(new Part("b", Shape.Box(1, 1, 1)));
        var loaded = Document.Load(new Document(explicitScene).Save()).Scene;
        Assert.True(loaded.HasExplicitOptions);
        Assert.Equal(48, loaded.Options.SegmentsPerCircle);
        Assert.Equal(96, loaded.Options.SdfResolution);

        var plainScene = new Scene();
        plainScene.Add(new Part("b", Shape.Box(1, 1, 1)));
        Assert.False(Document.Load(new Document(plainScene).Save()).Scene.HasExplicitOptions);
    }

    [Fact]
    public void FilesRoundTripThroughDisk()
    {
        string path = Path.Combine(Path.GetTempPath(), $"engrcad-{Guid.NewGuid():N}.json");
        try
        {
            Fixture().SaveFile(path);
            var result = Document.LoadFile(path);
            Assert.Equal(2, result.Scene.Tabs.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
