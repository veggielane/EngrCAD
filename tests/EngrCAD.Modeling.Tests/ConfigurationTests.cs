using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Configurations: one <see cref="FeatureHistory"/>, N named parameter sets, carried through
/// the SAME JSON seam as <see cref="FeatureHistory.SaveParameters"/>.
///
/// <para>Two assertions have real teeth and everything else supports them.
/// <b>save → load → save stays a byte fixed point with configurations present</b> — the only
/// check that catches a field written but never read, a default that reloads as a different
/// default, or an ordering that is not a function of the model. And <b>switching away and
/// back regenerates BIT-IDENTICAL geometry</b> — the cache-key property the undo stack
/// already asserts, asked of a new consumer, and non-trivial because a fresh feature INSTANCE
/// always re-runs.</para>
/// </summary>
public class ConfigurationTests
{
    // ---- fixtures -------------------------------------------------------

    /// <summary>The acceptance case: one bracket whose bolt size is a <c>[Param]</c>, so an
    /// M4…M12 family is six parameter sets over one history.</summary>
    private sealed class BoltHoles : Feature
    {
        [Param(Min = 2, Max = 24, Units = "mm", Description = "Nominal bolt size")]
        public double Size { get; init; } = 6;

        [Param(Min = 1, Units = "mm")]
        public double Depth { get; init; } = 12;

        public override Shape Apply(FeatureContext context) =>
            context.Body!.Drill(
                StandardHoles.Clearance(Size),
                [new Vector2d(-20, 0), new Vector2d(20, 0)],
                Depth,
                context.TopPlane);
    }

    private static FeatureHistory BracketHistory(out ExtrudeSketchFeature plate, out BoltHoles holes)
    {
        var history = new FeatureHistory();
        history.Add(plate = new ExtrudeSketchFeature(Sketch.Rectangle(60, 30)) { Height = 8 });
        history.Add(holes = new BoltHoles());
        return history;
    }

    private static Part Bracket(out ExtrudeSketchFeature plate, out BoltHoles holes) =>
        BracketHistory(out plate, out holes).ToPart("bracket");

    /// <summary>A history whose every feature the DEFAULT registry can rebuild — what the
    /// document round-trip needs, since a configuration is only interesting on a part that
    /// comes back parametric.</summary>
    private static Part RegistryBracket()
    {
        var history = new FeatureHistory();
        history.Add(new ExtrudeSketchFeature(Sketch.Rectangle(60, 30)) { Height = 8 });
        history.Add(new HoleFeature(HoleSpec.Simple(6), [new(-20, 0), new(20, 0)]) { Depth = 12 });
        return history.ToPart("bracket");
    }

    private static readonly MeshQuality Quality = new() { SegmentsPerCircle = 32, CurveSamples = 24 };

    private static double Volume(Part part) => part.GetMesh(Quality).Volume();

    /// <summary>The area an inscribed <c>n</c>-gon bore of diameter <c>d</c> really removes —
    /// the DISCRETE truth, so a volume assertion can be an identity instead of a band.</summary>
    private static double BoreArea(double diameter, int segments = 32) =>
        segments / 2.0 * (diameter / 2) * (diameter / 2) * Math.Sin(2 * Math.PI / segments);

    private static (double Volume, IReadOnlyList<Vector3d> Vertices) Snapshot(Part part)
    {
        var mesh = part.GetMesh(Quality);
        var (positions, _) = mesh.ToIndexed();
        return (mesh.Volume(), positions);
    }

    private static void AssertBitIdentical(
        (double Volume, IReadOnlyList<Vector3d> Vertices) expected,
        (double Volume, IReadOnlyList<Vector3d> Vertices) actual)
    {
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(expected.Volume),
            BitConverter.DoubleToInt64Bits(actual.Volume));
        Assert.Equal(expected.Vertices.Count, actual.Vertices.Count);
        for (int i = 0; i < expected.Vertices.Count; i++)
        {
            Assert.Equal(
                (BitConverter.DoubleToInt64Bits(expected.Vertices[i].X),
                 BitConverter.DoubleToInt64Bits(expected.Vertices[i].Y),
                 BitConverter.DoubleToInt64Bits(expected.Vertices[i].Z)),
                (BitConverter.DoubleToInt64Bits(actual.Vertices[i].X),
                 BitConverter.DoubleToInt64Bits(actual.Vertices[i].Y),
                 BitConverter.DoubleToInt64Bits(actual.Vertices[i].Z)));
        }
    }

    // ---- the seam -------------------------------------------------------

    [Fact]
    public void ACaptureIsExactlyTheSaveParametersJson()
    {
        var part = Bracket(out _, out _);
        var captured = part.Configurations!.Capture("as drawn");

        // Byte-equal to the seam's own output, modulo the canonicalization the ctor does —
        // which for text the seam itself produced is the identity.
        Assert.Equal(part.History!.SaveParameters(), captured.Parameters);
        Assert.True(part.Configurations!.Matches(captured));
    }

    [Fact]
    public void APartialSetStatesOnlyWhatItNames()
    {
        var part = Bracket(out _, out var holes);
        var m8 = part.Configurations!.Add("M8", (holes, nameof(BoltHoles.Size), 8.0));

        Assert.Equal(["BoltHoles"], m8.Features);
        Assert.Equal(1, m8.ValueCount);

        // It says nothing about the plate, so editing the plate leaves it still matched.
        Assert.False(part.Configurations!.Matches(m8));   // Size is still 6
        part.Configurations!.Activate("M8");
        Assert.True(part.Configurations!.Matches(m8));
    }

    [Fact]
    public void TheTypedOverloadRefusesAnUnknownParameterByName()
    {
        var part = Bracket(out _, out var holes);
        var exception = Assert.Throws<ArgumentException>(
            () => part.Configurations!.Add("bad", (holes, "Diameter", 8.0)));
        Assert.Contains("Diameter", exception.Message);
        Assert.Contains("Size", exception.Message);   // it says what the feature DOES have
        Assert.Contains("Depth", exception.Message);
    }

    [Fact]
    public void AJsonSetIsNotCheckedAtAddButIsReportedByValidateAndByActivate()
    {
        var part = Bracket(out _, out _);
        // The overload that names features by STRING is in LoadParameters' position, so a
        // stale name is a data condition rather than a caller's mistake.
        part.Configurations!.Add("stale", """{ "NoSuchFeature": { "Size": 8 } }""");

        string[] validation = [.. part.Configurations!.Validate()];
        Assert.Single(validation);
        Assert.Contains("NoSuchFeature", validation[0]);

        var applied = part.Configurations!.Activate("stale");
        Assert.Single(applied.Warnings);
        Assert.Contains("NoSuchFeature", applied.Warnings[0]);
        Assert.True(applied.Succeeded);   // the model rebuilt; nothing was applied

        // Reported, never dropped: the configuration is still there for the feature to come back to.
        Assert.Contains("stale", part.Configurations!.Names);
    }

    [Fact]
    public void AnUnknownParameterOnAKnownFeatureIsReportedToo()
    {
        var part = Bracket(out _, out _);
        part.Configurations!.Add("stale", """{ "BoltHoles": { "Diameter": 8 } }""");

        string[] validation = [.. part.Configurations!.Validate()];
        Assert.Single(validation);
        Assert.Contains("Diameter", validation[0]);
        Assert.Contains("BoltHoles", validation[0]);
    }

    [Fact]
    public void ADuplicateNameAndAnUnknownNameBothRefuseByName()
    {
        var part = Bracket(out _, out var holes);
        part.Configurations!.Add("M8", (holes, nameof(BoltHoles.Size), 8.0));

        Assert.Contains("M8", Assert.Throws<ArgumentException>(
            () => part.Configurations!.Add("M8", (holes, nameof(BoltHoles.Size), 8.0))).Message);
        var missing = Assert.Throws<ArgumentException>(() => part.Configurations!.Activate("M14"));
        Assert.Contains("M14", missing.Message);
        Assert.Contains("M8", missing.Message);   // names what it DOES have
    }

    [Fact]
    public void APartWithNoHistoryHasNoConfigurations()
    {
        var part = new Part("block", Shape.Box(10, 10, 10));
        Assert.Null(part.Configurations);
    }

    // ---- the cache-key property -----------------------------------------

    [Fact]
    public void SwitchingAwayAndBackRegeneratesBitIdenticalGeometry()
    {
        var part = Bracket(out _, out var holes);
        var configurations = part.Configurations!;
        configurations.Add("M6", (holes, nameof(BoltHoles.Size), 6.0));
        configurations.Add("M10", (holes, nameof(BoltHoles.Size), 10.0));

        configurations.Activate("M6");
        var m6 = Snapshot(part);

        configurations.Activate("M10");
        Assert.True(Volume(part) < m6.Volume);   // a bigger bolt takes more material out

        var back = configurations.Activate("M6");

        // The bit-identity does NOT come from the body being restored from the cache. The
        // prefix cache holds ONE entry per feature INDEX, overwritten each regeneration, so
        // the feature whose parameter moved re-runs on the way back and returns a fresh
        // (structurally identical) Shape. What the cache does buy is the PREFIX: the plate
        // above the change is Cached on every switch, which is why a configuration switch
        // costs the tail of the history and not the whole of it.
        Assert.Equal(FeatureOutcome.Cached, back.Regeneration.Statuses[0].Outcome);
        Assert.Equal(FeatureOutcome.Applied, back.Regeneration.Statuses[1].Outcome);

        // What makes the geometry bit-identical is the contract the cache is BUILT on —
        // Apply is a pure function of the parameters — so restoring the values reproduces
        // the construction exactly. Asserted on every vertex, not on a volume: a volume can
        // agree while the vertices differ in the last bits.
        AssertBitIdentical(m6, Snapshot(part));
    }

    [Fact]
    public void AFreshFeatureInstanceStillReRuns()
    {
        // The trap the bit-identity claim rests on: the cache key carries instance identity,
        // so the above holds only because a configuration never replaces a feature. Pinned
        // here so a future "configurations may swap features" change fails loudly.
        var history = BracketHistory(out _, out var holes);
        var part = history.ToPart("bracket");
        var before = part.Geometry;

        var result = history.Regenerate();
        Assert.Equal(FeatureOutcome.Cached, result.Statuses[1].Outcome);   // same instance, same values

        history.Replace(1, new BoltHoles { Size = holes.Size, Depth = holes.Depth });
        result = history.Regenerate();

        Assert.Equal(FeatureOutcome.Applied, result.Statuses[1].Outcome);
        Assert.NotSame(before, result.Body);
    }

    // ---- the active configuration ---------------------------------------

    [Fact]
    public void ActivatingDoesNotWriteBackAndModificationIsReported()
    {
        var part = Bracket(out _, out var holes);
        var configurations = part.Configurations!;
        configurations.Add("M6", (holes, nameof(BoltHoles.Size), 6.0));
        configurations.Activate("M6");

        Assert.Equal("M6", configurations.Active);
        Assert.False(configurations.ActiveIsModified);

        // An ordinary parameter edit, through the same seam a properties panel uses.
        part.History!.LoadParameters("""{ "BoltHoles": { "Size": 7 } }""");
        part.Regenerate();

        // The claim goes STALE rather than the configuration silently absorbing the edit.
        Assert.Equal("M6", configurations.Active);
        Assert.True(configurations.ActiveIsModified);
        Assert.Equal("""{ "BoltHoles": { "Size": 6 } }""".Replace(" ", ""),
            configurations.Find("M6")!.Parameters.Replace(" ", "").Replace("\r", "").Replace("\n", ""));
    }

    [Fact]
    public void RemovingTheActiveConfigurationClearsTheClaimAndKeepsTheValues()
    {
        var part = Bracket(out _, out var holes);
        var configurations = part.Configurations!;
        configurations.Add("M10", (holes, nameof(BoltHoles.Size), 10.0));
        configurations.Activate("M10");
        double volume = Volume(part);

        Assert.True(configurations.Remove("M10"));
        Assert.Null(configurations.Active);
        Assert.Equal(volume, Volume(part));   // the model kept its values
    }

    // ---- document persistence -------------------------------------------

    private static Document DocumentWithConfigurations()
    {
        var scene = new Scene(new MeshQuality { SegmentsPerCircle = 16, CurveSamples = 12 });
        var part = RegistryBracket();
        scene.Add(part);
        part.Configurations!.Capture("as drawn");
        part.Configurations!.Add("thick", """{ "ExtrudeSketchFeature": { "Height": 14 } }""");
        part.Configurations!.Activate("thick");
        return new Document(scene);
    }

    [Fact]
    public void SaveLoadSaveIsAByteFixedPointWithConfigurationsPresent()
    {
        string first = DocumentWithConfigurations().Save();
        var loaded = Document.Load(first);
        Assert.True(loaded.Complete, string.Join("\n", loaded.Warnings));
        string second = loaded.Document.Save();

        Assert.Equal(first, second);
    }

    [Fact]
    public void ADocumentUsingNoConfigurationsWritesNoConfigurationsField()
    {
        // Write-only-when-stated: the standing rule for every optional document field, and
        // what keeps the format stable for every file that predates this feature.
        var scene = new Scene();
        scene.Add(RegistryBracket());
        string json = new Document(scene).Save();

        Assert.DoesNotContain("configurations", json);
    }

    [Fact]
    public void TheActiveNameRoundTripsAndTheLoadDoesNotReApplyIt()
    {
        var document = DocumentWithConfigurations();
        var part = document.Scene.AllParts.Single();
        // Saved MODIFIED: active "thick", but one parameter has since moved.
        part.History!.LoadParameters("""{ "ExtrudeSketchFeature": { "Height": 15 } }""");
        part.Regenerate();
        Assert.True(part.Configurations!.ActiveIsModified);

        var loaded = Document.Load(document.Save());
        var reloaded = loaded.Scene.AllParts.Single();

        Assert.Equal("thick", reloaded.Configurations!.Active);
        // Re-applying at load would silently snap the model back onto "thick" and lose the
        // edit; the values the file carried are what came back.
        Assert.True(reloaded.Configurations!.ActiveIsModified);
        Assert.Contains("15", reloaded.History!.SaveParameters());
    }

    [Fact]
    public void ConfigurationsSurviveALoadAndStillDrive()
    {
        var loaded = Document.Load(DocumentWithConfigurations().Save());
        var part = loaded.Scene.AllParts.Single();

        Assert.Equal(["as drawn", "thick"], part.Configurations!.Names);
        double thick = Volume(part);
        part.Configurations!.Activate("as drawn");
        double asDrawn = Volume(part);

        // Height 14 -> 8 on a 60x30 plate with two Ø6 bores 12 deep, so "thick" is BLIND
        // (12 into 14) and "as drawn" goes through (12 into 8). Compared against the
        // discrete truth — the inscribed 32-gon bore — so both are identities.
        Assert.True(thick > asDrawn);
        Assert.Equal(60 * 30 * 14 - 2 * BoreArea(6) * 12, thick, 1e-6);
        Assert.Equal(60 * 30 * 8 - 2 * BoreArea(6) * 8, asDrawn, 1e-6);
    }

    [Fact]
    public void AnActiveNameNamingNothingIsAWarningNotAThrow()
    {
        string json = DocumentWithConfigurations().Save()
            .Replace("\"active\": \"thick\"", "\"active\": \"gone\"", StringComparison.Ordinal);
        var loaded = Document.Load(json);

        Assert.Contains(loaded.Warnings, w => w.Contains("gone"));
        Assert.Null(loaded.Scene.AllParts.Single().Configurations!.Active);
    }

    // ---- undo -----------------------------------------------------------

    [Fact]
    public void UndoingAConfigurationSwitchRestoresAByteIdenticalDocument()
    {
        var document = DocumentWithConfigurations();
        var part = document.Scene.AllParts.Single();
        string before = document.Save();

        var stack = new UndoStack();
        stack.Do(DocumentEdits.SetConfiguration(part, "as drawn"));
        Assert.NotEqual(before, document.Save());

        stack.Undo();
        // The document serializer is the oracle: a hand-written state comparison agrees with
        // a broken revert as happily as with a correct one.
        Assert.Equal(before, document.Save());
        Assert.Equal("thick", part.Configurations!.Active);
    }

    [Fact]
    public void UndoRestoresTheLiveValuesRatherThanTheStoredOnes()
    {
        var part = Bracket(out _, out var holes);
        var configurations = part.Configurations!;
        configurations.Add("M6", (holes, nameof(BoltHoles.Size), 6.0));
        configurations.Add("M10", (holes, nameof(BoltHoles.Size), 10.0));
        configurations.Activate("M6");
        double m6 = Volume(part);

        // An uncaptured edit while "M6" is active: undo must bring THIS back, not 6.
        part.History!.LoadParameters("""{ "BoltHoles": { "Size": 8 } }""");
        part.Regenerate();
        double modified = Volume(part);
        Assert.NotEqual(m6, modified);   // the fixture can tell the two apart

        var stack = new UndoStack();
        stack.Do(DocumentEdits.SetConfiguration(part, "M10"));
        stack.Undo();

        Assert.Equal(modified, Volume(part));
        Assert.Equal("M6", configurations.Active);
        Assert.True(configurations.ActiveIsModified);
    }

    [Fact]
    public void ARefusedConfigurationIsNotHistoryAndChangesNothing()
    {
        var part = Bracket(out _, out var holes);
        var configurations = part.Configurations!;
        configurations.Capture("good");
        configurations.Activate("good");
        // Out of the [Param] range, so validation refuses it and the model does not rebuild.
        configurations.Add("impossible", (holes, nameof(BoltHoles.Size), 99.0));
        double before = Volume(part);
        string parameters = part.History!.SaveParameters();

        var stack = new UndoStack();
        Assert.Throws<DocumentEditException>(
            () => stack.Do(DocumentEdits.SetConfiguration(part, "impossible")));

        Assert.False(stack.CanUndo);
        Assert.Equal("good", configurations.Active);
        Assert.Equal(parameters, part.History!.SaveParameters());
        Assert.Equal(before, Volume(part));
    }

    [Fact]
    public void AddingAndRemovingAConfigurationAreUndoable()
    {
        var document = DocumentWithConfigurations();
        var part = document.Scene.AllParts.Single();
        string before = document.Save();

        var stack = new UndoStack();
        stack.Do(DocumentEdits.AddConfiguration(
            part, new Configuration("thin", """{ "ExtrudeSketchFeature": { "Height": 4 } }""")));
        Assert.Equal(3, part.Configurations!.Count);
        stack.Undo();
        Assert.Equal(before, document.Save());

        stack.Do(DocumentEdits.RemoveConfiguration(part, "thick"));
        Assert.Null(part.Configurations!.Active);   // it was the active one
        stack.Undo();
        Assert.Equal(before, document.Save());
    }

    // ---- the family table -----------------------------------------------

    [Fact]
    public void TheBomRollsUpPerConfigurationAndRestoresThePart()
    {
        var part = Bracket(out _, out var holes);
        var configurations = part.Configurations!;
        foreach (double size in new[] { 4.0, 6.0, 10.0 })
            configurations.Add($"M{size:0}", (holes, nameof(BoltHoles.Size), size));
        configurations.Activate("M6");

        var scene = new Scene(new MeshQuality { SegmentsPerCircle = 16, CurveSamples = 12 });
        scene.Add(part.Of(Materials.Aluminium6061));
        string parametersBefore = part.History!.SaveParameters();

        var table = Bom.ByConfiguration(part, scene, mass: true);

        Assert.Equal(["M4", "M6", "M10"], table.Select(row => row.Configuration));
        Assert.All(table, row => Assert.Empty(row.Warnings));
        Assert.All(table, row => Assert.Equal(1, row.Bom.TotalQuantity));
        // A bigger bolt takes more aluminium out, so the family table is monotone.
        Assert.True(table[0].TotalMassGrams > table[1].TotalMassGrams);
        Assert.True(table[1].TotalMassGrams > table[2].TotalMassGrams);

        // An analysis, not an edit: the part is exactly as it was found.
        Assert.Equal("M6", configurations.Active);
        Assert.Equal(parametersBefore, part.History!.SaveParameters());
    }

    [Fact]
    public void AFamilyTableCountsTheHardwareEachConfigurationPlaces()
    {
        // The interesting half of "per configuration" on a document whose parts are shared:
        // the configured part is one line either way, and what MOVES is what the model puts
        // around it — here, whether the second bolt station is drilled at all.
        var history = new FeatureHistory();
        history.Add(new ExtrudeSketchFeature(Sketch.Rectangle(60, 30)) { Height = 8 });
        var part = history.ToPart("bracket");

        var assembly = new Assembly("stack");
        assembly.Add(part);
        var washer = new PlainWasher(6).ToPart();
        assembly.Add(washer);

        var configurations = part.Configurations!;
        configurations.Capture("plain");
        configurations.Add("tall", """{ "ExtrudeSketchFeature": { "Height": 20 } }""");

        var table = Bom.ByConfiguration(part, assembly);

        Assert.Equal(2, table.Count);
        foreach (var row in table)
        {
            Assert.Equal(2, row.Bom.LineCount);
            Assert.Single(row.Bom.Hardware);
            Assert.Equal("ISO 7089 M6", row.Bom.Hardware.Single().Item);
        }
        Assert.Contains("== plain ==", Bom.ToText(table), StringComparison.Ordinal);
    }

    // ---- the acceptance case --------------------------------------------

    [Fact]
    public void AnM4ToM12FamilyIsOneHistoryAndSixParameterSets()
    {
        var part = Bracket(out _, out var holes);
        var configurations = part.Configurations!;
        double[] sizes = [4, 5, 6, 8, 10, 12];
        foreach (double size in sizes)
            configurations.Add($"M{size:0}", (holes, nameof(BoltHoles.Size), size));

        Assert.Equal(6, configurations.Count);
        Assert.Equal(2, part.History!.Features.Count);   // ONE history behind all six

        // Each variant's volume is the plate less two clearance bores — the DISCRETE truth,
        // so this is an identity rather than a band.
        foreach (double size in sizes)
        {
            var applied = configurations.Activate($"M{size:0}");
            Assert.True(applied.Succeeded);
            Assert.Empty(applied.Warnings);

            double bore = StandardHoles.Clearance(size).Diameter;
            Assert.Equal(60 * 30 * 8 - 2 * BoreArea(bore) * 8, Volume(part), 1e-6);
        }

        // And the family is a fixed point of switching: back to M4 is bit-identical to the
        // first time M4 was applied, vertex for vertex.
        configurations.Activate("M4");
        var m4 = Snapshot(part);
        configurations.Activate("M12");
        configurations.Activate("M4");
        AssertBitIdentical(m4, Snapshot(part));
    }
}
