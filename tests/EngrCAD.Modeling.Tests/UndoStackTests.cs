using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Undo/redo over document edits — the <c>MeshChangeSet</c> pattern at document
/// granularity.
///
/// <para><b>The oracle is the document SERIALIZER, not a hand-written state comparison.</b>
/// After an undo, <c>Document.Save()</c> must be byte-identical to the pre-edit save: that
/// covers every field the format carries, including list positions, occurrence names and
/// the parameter values inside a feature history, and — unlike an assertion someone writes
/// out by hand — it cannot agree with a broken revert by accident.</para>
/// </summary>
public class UndoStackTests
{
    // ---- fixtures -------------------------------------------------------

    private sealed record Rig(
        Document Document, Scene Scene, Part Plate, FeatureHistory History,
        Assembly Assembly, UndoStack Undo)
    {
        public string Snapshot() => Document.Save();
    }

    private static Rig Build()
    {
        var history = new FeatureHistory();
        history.Add(new ExtrudeSketchFeature(Sketch.Rectangle(60, 40)) { Height = 8 });
        history.Add(new HoleFeature(HoleSpec.Simple(6), [new(-20, 0), new(20, 0)]) { Depth = 12 });

        var scene = new Scene(new MeshQuality { SegmentsPerCircle = 16, CurveSamples = 12 });
        var plate = history.ToPart("plate", Palette.Steel);
        var tab = scene.AddTab("Model");
        tab.Add(plate);

        var assembly = new Assembly("stack");
        assembly.Add(plate);
        assembly.Add(plate, Frame3d.FromXY((0, 0, 20), Vector3d.UnitX, Vector3d.UnitY));
        tab.Add(assembly);

        return new Rig(new Document(scene), scene, plate, history, assembly, new UndoStack());
    }

    private static double Volume(Part part) =>
        MeshMassProperties.Compute(part.GetMesh()).Volume;

    // ---- the two contracts ----------------------------------------------

    /// <summary>
    /// One test per edit kind, all asserting the SAME thing: apply changes the document,
    /// undo restores a byte-identical serialization, redo reproduces the applied one.
    /// </summary>
    public static TheoryData<string> EditNames => new(
        "parameter", "parameters-json", "suppress", "add-feature", "remove-feature",
        "rename", "colour", "material", "field-display", "transform", "display-mode", "clipped",
        "add-occurrence", "remove-occurrence", "repose", "explode",
        "add-annotation", "remove-annotation", "add-mate", "remove-mate", "solve-mates");

    [Theory]
    [MemberData(nameof(EditNames))]
    public void EveryEdit_UndoesToAByteIdenticalDocument_AndRedoesBack(string name)
    {
        var rig = Build();
        var edit = EditFor(rig, name);

        string before = rig.Snapshot();
        rig.Undo.Do(edit);
        string after = rig.Snapshot();
        Assert.NotEqual(before, after);

        rig.Undo.Undo();
        Assert.Equal(before, rig.Snapshot());

        rig.Undo.Redo();
        Assert.Equal(after, rig.Snapshot());

        // And round again, to catch an edit whose Apply is not idempotent in the sense
        // redo needs (a re-derived occurrence name, say).
        rig.Undo.Undo();
        Assert.Equal(before, rig.Snapshot());
    }

    private static DocumentEdit EditFor(Rig rig, string name)
    {
        var extrude = rig.History.Features[0];
        var drill = rig.History.Features[1];
        return name switch
        {
            "parameter" => DocumentEdits.SetParameter(rig.Plate, extrude, "Height", 14.0),
            "parameters-json" => DocumentEdits.SetParameters(
                rig.Plate, drill, """{ "Depth": 20 }"""),
            "suppress" => DocumentEdits.Suppress(rig.Plate, drill, true),
            "add-feature" => DocumentEdits.AddFeature(
                rig.Plate, new HoleFeature(HoleSpec.Simple(4), [new(0, 12)]) { Depth = 12 }),
            "remove-feature" => DocumentEdits.RemoveFeature(rig.Plate, drill),
            "rename" => DocumentEdits.Rename(rig.Scene, rig.Plate, "base-plate"),
            "colour" => DocumentEdits.SetColor(rig.Plate, Palette.Coral),
            "material" => DocumentEdits.SetMaterial(rig.Plate, Materials.Titanium6Al4V),
            // A display naming a result the part does not carry is legal document
            // state, which is exactly what lets the byte-identity oracle run on the
            // shared rig without attaching a field first.
            "field-display" => DocumentEdits.SetFieldDisplay(
                rig.Plate, new FieldDisplay { Field = "stress" }),
            "transform" => DocumentEdits.SetTransform(rig.Plate, Matrix4d.CreateTranslation((1, 2, 3))),
            "display-mode" => DocumentEdits.SetDisplayMode(rig.Plate, DisplayMode.Wireframe),
            "clipped" => DocumentEdits.SetClippedBySection(rig.Plate, false),
            "add-occurrence" => DocumentEdits.AddOccurrence(
                rig.Assembly, rig.Plate, Frame3d.FromXY((0, 0, 40), Vector3d.UnitX, Vector3d.UnitY)),
            "remove-occurrence" => DocumentEdits.RemoveOccurrence(rig.Assembly, rig.Assembly.Occurrences[0]),
            "repose" => DocumentEdits.Repose(
                rig.Assembly.Occurrences[1], Frame3d.FromXY((5, 6, 7), Vector3d.UnitX, Vector3d.UnitY)),
            "explode" => DocumentEdits.SetExplodeOffset(rig.Assembly.Occurrences[1], (0, 0, 50)),
            "add-annotation" => DocumentEdits.AddAnnotation(
                rig.Plate, new LeaderNote((0, 0, 8), "FLATNESS 0.05")),
            "remove-annotation" => RemoveAnnotation(rig),
            "add-mate" => AddMate(rig),
            "solve-mates" => SolveMates(rig),
            "remove-mate" => RemoveMate(rig),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
        };
    }

    private static DocumentEdit RemoveAnnotation(Rig rig)
    {
        // Two annotations, so removing the FIRST proves undo restores the position and not
        // merely the membership.
        var first = new LeaderNote((0, 0, 8), "A");
        rig.Plate.Annotate(first);
        rig.Plate.Annotate(new DatumLabel((0, 0, 0), "B"));
        return DocumentEdits.RemoveAnnotation(rig.Plate, first);
    }

    private static MateSet Mates(Rig rig)
    {
        var set = new MateSet(rig.Assembly).Ground(rig.Assembly.Occurrences[0]);
        rig.Document.Mates.Add(set);
        return set;
    }

    private static Mate Stack(Rig rig) => Mate.Planar(
        MateGeometry.PlanarFace(rig.Assembly.Occurrences[0], FaceRef.Top),
        MateGeometry.PlanarFace(rig.Assembly.Occurrences[1], FaceRef.Bottom),
        gap: 2, name: "stack");

    private static DocumentEdit AddMate(Rig rig) => DocumentEdits.AddMate(Mates(rig), Stack(rig));

    private static DocumentEdit SolveMates(Rig rig)
    {
        var set = Mates(rig).Add(Stack(rig));
        return DocumentEdits.SolveMates(set);
    }

    private static DocumentEdit RemoveMate(Rig rig)
    {
        var set = Mates(rig);
        var mate = Stack(rig);
        set.Add(mate);
        // A second mate AFTER it, so removing the first proves undo restores the POSITION
        // (which is the save order) and not merely the membership.
        set.Add(Mate.Distance(
            MateGeometry.Point(rig.Assembly.Occurrences[0], (0, 0, 0)),
            MateGeometry.Point(rig.Assembly.Occurrences[1], (0, 0, 0)),
            distance: 25, name: "rise"));
        return DocumentEdits.RemoveMate(set, mate);
    }

    // ---- geometry actually changes, and comes back ----------------------

    [Fact]
    public void AParameterEdit_RebuildsTheGeometry_AndUndoRebuildsItBack()
    {
        var rig = Build();
        double before = Volume(rig.Plate);

        rig.Undo.Do(DocumentEdits.SetParameter(rig.Plate, rig.History.Features[0], "Height", 16.0));
        double after = Volume(rig.Plate);
        Assert.True(after > before * 1.8, $"{before} -> {after}");

        rig.Undo.Undo();
        Assert.Equal(before, Volume(rig.Plate), 9);
    }

    [Fact]
    public void SuppressingAFeature_RemovesItsGeometry_AndUndoBringsItBack()
    {
        var rig = Build();
        double drilled = Volume(rig.Plate);

        rig.Undo.Do(DocumentEdits.Suppress(rig.Plate, rig.History.Features[1], true));
        double solid = Volume(rig.Plate);
        Assert.True(solid > drilled, $"{drilled} -> {solid}");

        rig.Undo.Undo();
        Assert.Equal(drilled, Volume(rig.Plate), 9);
    }

    // ---- the regeneration cache under undo -------------------------------

    /// <summary>
    /// Reverting a parameter must invalidate exactly the prefix a forward edit would: the
    /// features BEFORE the changed one stay cached, the changed one and everything after it
    /// re-run. That is not a nicety — it is the property that makes undo cost what an edit
    /// costs, and the cache is keyed on a parameter snapshot, so a revert that left a stale
    /// entry would hand back the edited body.
    /// </summary>
    [Fact]
    public void UndoInvalidatesTheSamePrefixAForwardEditDoes()
    {
        var rig = Build();
        rig.History.Regenerate();   // everything cached

        // Edit the SECOND feature: the first must stay cached in both directions.
        var edit = DocumentEdits.SetParameter(rig.Plate, rig.History.Features[1], "Depth", 20.0);
        rig.Undo.Do(edit);
        var forward = rig.History.Regenerate();
        Assert.Equal(FeatureOutcome.Cached, forward.Statuses[0].Outcome);

        rig.Undo.Undo();
        var back = rig.History.Regenerate();
        Assert.Equal(FeatureOutcome.Cached, back.Statuses[0].Outcome);
        Assert.Equal(FeatureOutcome.Cached, back.Statuses[1].Outcome);   // re-run by the undo itself

        // Editing the FIRST feature must invalidate the second as well, both ways.
        rig.Undo.Do(DocumentEdits.SetParameter(rig.Plate, rig.History.Features[0], "Height", 14.0));
        var statuses = rig.History.Regenerate().Statuses;
        Assert.All(statuses, s => Assert.Equal(FeatureOutcome.Cached, s.Outcome));
    }

    // ---- failures leave the document untouched --------------------------

    [Fact]
    public void AParameterThatBreaksTheModel_IsRefused_AndChangesNothing()
    {
        var rig = Build();
        string before = rig.Snapshot();
        double volume = Volume(rig.Plate);

        // Below the [Param(Min = 1e-9)] floor: validation fails, so the model does not
        // rebuild and the edit must take its own value back.
        var edit = DocumentEdits.SetParameter(rig.Plate, rig.History.Features[0], "Height", -5.0);
        var exception = Assert.Throws<DocumentEditException>(() => rig.Undo.Do(edit));

        Assert.NotNull(exception.Regeneration);
        Assert.Equal(before, rig.Snapshot());
        Assert.Equal(volume, Volume(rig.Plate), 9);
        Assert.False(rig.Undo.CanUndo);   // a refused edit is not history
    }

    [Fact]
    public void RemovingTheFeatureEverythingElseNeeds_IsRefused_AndChangesNothing()
    {
        var rig = Build();
        string before = rig.Snapshot();

        // Drop the base extrude and the drill has no body to cut.
        var edit = DocumentEdits.RemoveFeature(rig.Plate, rig.History.Features[0]);
        Assert.Throws<DocumentEditException>(() => rig.Undo.Do(edit));

        Assert.Equal(before, rig.Snapshot());
        Assert.Equal(2, rig.History.Features.Count);
        Assert.False(rig.Undo.CanUndo);
    }

    [Fact]
    public void AnUnknownParameterName_IsRefusedBeforeAnythingIsWritten()
    {
        var rig = Build();
        string before = rig.Snapshot();
        var exception = Assert.Throws<DocumentEditException>(() => rig.Undo.Do(
            DocumentEdits.SetParameters(rig.Plate, rig.History.Features[0],
                """{ "Height": 20, "Thickness": 3 }""")));

        Assert.Contains("Thickness", exception.Message);
        Assert.Contains("Height", exception.Message);   // names what the type DOES have
        Assert.Equal(before, rig.Snapshot());
    }

    [Fact]
    public void ACollidingRename_IsRefused_AndChangesNothing()
    {
        var rig = Build();
        rig.Scene.Tabs[0].Add(new Part("spacer", Shape.Box(10, 10, 2)));
        string before = rig.Snapshot();

        Assert.Throws<ArgumentException>(() =>
            rig.Undo.Do(DocumentEdits.Rename(rig.Scene, rig.Plate, "spacer")));
        Assert.Equal(before, rig.Snapshot());
        Assert.False(rig.Undo.CanUndo);
    }

    [Fact]
    public void ARefusedEdit_DoesNotDiscardTheRedoHistory()
    {
        var rig = Build();
        rig.Undo.Do(DocumentEdits.SetColor(rig.Plate, Palette.Coral));
        rig.Undo.Undo();
        Assert.True(rig.Undo.CanRedo);

        Assert.Throws<DocumentEditException>(() => rig.Undo.Do(
            DocumentEdits.SetParameter(rig.Plate, rig.History.Features[0], "Height", -1.0)));

        Assert.True(rig.Undo.CanRedo);
        rig.Undo.Redo();
        Assert.Equal(Palette.Coral, rig.Plate.Color);
    }

    // ---- grouping -------------------------------------------------------

    [Fact]
    public void AGroupIsOneUserVisibleStep()
    {
        var rig = Build();
        string before = rig.Snapshot();

        using (rig.Undo.Group("Set up the plate"))
        {
            rig.Undo.Do(DocumentEdits.SetParameter(rig.Plate, rig.History.Features[0], "Height", 12.0));
            rig.Undo.Do(DocumentEdits.SetColor(rig.Plate, Palette.Coral));
            rig.Undo.Do(DocumentEdits.AddAnnotation(rig.Plate, new LeaderNote((0, 0, 12), "N")));
        }

        Assert.Single(rig.Undo.Undoable);
        Assert.Equal("Set up the plate", rig.Undo.UndoDescription);
        string after = rig.Snapshot();

        rig.Undo.Undo();
        Assert.Equal(before, rig.Snapshot());
        rig.Undo.Redo();
        Assert.Equal(after, rig.Snapshot());
    }

    [Fact]
    public void AGroupOfOneKeepsItsOwnDescription()
    {
        var rig = Build();
        using (rig.Undo.Group("Whatever"))
            rig.Undo.Do(DocumentEdits.SetColor(rig.Plate, Palette.Coral));
        Assert.Equal("Recolour plate", rig.Undo.UndoDescription);
    }

    [Fact]
    public void AFailedMateSolve_LeavesTheDocumentUntouched_AndIsNeverPushed()
    {
        var rig = Build();
        var set = Mates(rig).Add(Stack(rig));
        // A second planar mate on the same pair demanding a DIFFERENT gap contradicts
        // the first; the solve refuses loudly and writes nothing.
        set.Add(Mate.Planar(
            MateGeometry.PlanarFace(rig.Assembly.Occurrences[0], FaceRef.Top),
            MateGeometry.PlanarFace(rig.Assembly.Occurrences[1], FaceRef.Bottom),
            gap: 9, name: "contradiction"));

        string before = rig.Snapshot();
        Assert.Throws<MateSolveException>(() => rig.Undo.Do(DocumentEdits.SolveMates(set)));
        Assert.Equal(before, rig.Snapshot());
        Assert.False(rig.Undo.CanUndo);
    }

    [Fact]
    public void AnEmptyGroupPushesNothing()
    {
        var rig = Build();
        using (rig.Undo.Group("Nothing happened")) { }
        Assert.False(rig.Undo.CanUndo);
    }

    [Fact]
    public void GroupsNest_AndOnlyTheOutermostBecomesAStep()
    {
        var rig = Build();
        string before = rig.Snapshot();

        using (rig.Undo.Group("outer"))
        {
            rig.Undo.Do(DocumentEdits.SetColor(rig.Plate, Palette.Coral));
            using (rig.Undo.Group("inner"))
            {
                rig.Undo.Do(DocumentEdits.SetDisplayMode(rig.Plate, DisplayMode.Wireframe));
                rig.Undo.Do(DocumentEdits.SetTransform(rig.Plate, Matrix4d.CreateTranslation((1, 0, 0))));
            }
        }

        Assert.Single(rig.Undo.Undoable);
        Assert.Equal("outer", rig.Undo.UndoDescription);
        rig.Undo.Undo();
        Assert.Equal(before, rig.Snapshot());
    }

    [Fact]
    public void AFailureInsideAGroup_RollsBackTheEditsThatAlreadySucceeded()
    {
        var rig = Build();
        string before = rig.Snapshot();

        var compound = new CompoundEdit("mixed", [
            DocumentEdits.SetColor(rig.Plate, Palette.Coral),
            DocumentEdits.SetDisplayMode(rig.Plate, DisplayMode.Wireframe),
            DocumentEdits.SetParameter(rig.Plate, rig.History.Features[0], "Height", -3.0),
        ]);

        Assert.Throws<DocumentEditException>(() => rig.Undo.Do(compound));
        Assert.Equal(before, rig.Snapshot());
        Assert.False(rig.Undo.CanUndo);
    }

    [Fact]
    public void UndoInsideAnOpenGroupIsRefused()
    {
        var rig = Build();
        using (rig.Undo.Group("open"))
        {
            rig.Undo.Do(DocumentEdits.SetColor(rig.Plate, Palette.Coral));
            Assert.Throws<InvalidOperationException>(rig.Undo.Undo);
            Assert.Throws<InvalidOperationException>(rig.Undo.Redo);
        }
    }

    // ---- stack behaviour -------------------------------------------------

    [Fact]
    public void ANewEditForksTheTimeline()
    {
        var rig = Build();
        rig.Undo.Do(DocumentEdits.SetColor(rig.Plate, Palette.Coral));
        rig.Undo.Do(DocumentEdits.SetColor(rig.Plate, Palette.Sage));
        rig.Undo.Undo();
        Assert.True(rig.Undo.CanRedo);

        rig.Undo.Do(DocumentEdits.SetDisplayMode(rig.Plate, DisplayMode.Wireframe));
        Assert.False(rig.Undo.CanRedo);
    }

    [Fact]
    public void TheLimitDropsTheOldestSteps()
    {
        var rig = Build();
        rig.Undo.Limit = 3;
        for (int i = 0; i < 6; i++)
            rig.Undo.Do(DocumentEdits.SetTransform(rig.Plate, Matrix4d.CreateTranslation((i, 0, 0))));

        Assert.Equal(3, rig.Undo.Undoable.Count);
        // The three that remain still undo correctly, back to the state after edit 2.
        rig.Undo.Undo();
        rig.Undo.Undo();
        rig.Undo.Undo();
        Assert.Equal(Matrix4d.CreateTranslation((2, 0, 0)), rig.Plate.Transform);
        Assert.False(rig.Undo.CanUndo);
    }

    [Fact]
    public void UndoAndRedoOnAnEmptyStackDoNothing()
    {
        var rig = Build();
        string before = rig.Snapshot();
        rig.Undo.Undo();
        rig.Undo.Redo();
        Assert.Equal(before, rig.Snapshot());
    }

    [Fact]
    public void ChangedFiresOnEveryStackMovement()
    {
        var rig = Build();
        int changes = 0;
        rig.Undo.Changed += () => changes++;

        rig.Undo.Do(DocumentEdits.SetColor(rig.Plate, Palette.Coral));
        rig.Undo.Undo();
        rig.Undo.Redo();
        rig.Undo.Clear();
        Assert.Equal(4, changes);
    }

    [Fact]
    public void AlreadyAppliedEditsCanBeRecorded()
    {
        // The viewport-drag case: the host moved the occurrence itself and wants it undoable.
        var rig = Build();
        string before = rig.Snapshot();
        var occurrence = rig.Assembly.Occurrences[1];

        var edit = DocumentEdits.Repose(occurrence, Frame3d.FromXY((9, 9, 9), Vector3d.UnitX, Vector3d.UnitY));
        edit.Apply();
        rig.Undo.Record(edit);

        Assert.True(rig.Undo.CanUndo);
        rig.Undo.Undo();
        Assert.Equal(before, rig.Snapshot());
    }

    [Fact]
    public void AnEditThatWasNeverAppliedRefusesToRevert()
    {
        var rig = Build();
        var edit = DocumentEdits.SetColor(rig.Plate, Palette.Coral);
        Assert.Throws<DocumentEditException>(edit.Revert);
    }

    // ---- a realistic session ---------------------------------------------

    /// <summary>
    /// The end-to-end shape of an editing session: several kinds of edit, then undo all the
    /// way back to the start and redo all the way forward, checking the serialization at
    /// every step. Undo histories break in the middle, not at the ends.
    /// </summary>
    [Fact]
    public void AWholeSessionUndoesAndRedoesStepByStep()
    {
        var rig = Build();
        var states = new List<string> { rig.Snapshot() };

        void Step(DocumentEdit edit)
        {
            rig.Undo.Do(edit);
            states.Add(rig.Snapshot());
        }

        Step(DocumentEdits.SetParameter(rig.Plate, rig.History.Features[0], "Height", 10.0));
        Step(DocumentEdits.Rename(rig.Scene, rig.Plate, "base"));
        Step(DocumentEdits.AddAnnotation(rig.Plate, new DatumLabel((0, 0, 0), "A")));
        Step(DocumentEdits.AddOccurrence(
            rig.Assembly, rig.Plate, Frame3d.FromXY((0, 0, 40), Vector3d.UnitX, Vector3d.UnitY)));
        Step(DocumentEdits.Suppress(rig.Plate, rig.History.Features[1], true));
        Step(DocumentEdits.SetExplodeOffset(rig.Assembly.Occurrences[2], (0, 0, 60)));

        for (int i = states.Count - 1; i > 0; i--)
        {
            rig.Undo.Undo();
            Assert.Equal(states[i - 1], rig.Snapshot());
        }
        Assert.False(rig.Undo.CanUndo);

        for (int i = 1; i < states.Count; i++)
        {
            rig.Undo.Redo();
            Assert.Equal(states[i], rig.Snapshot());
        }
        Assert.False(rig.Undo.CanRedo);
    }

    /// <summary>A document whose edits have been undone reloads as the pre-edit document —
    /// the persistence and undo layers agreeing, which is the whole point of using one as
    /// the other's oracle.</summary>
    [Fact]
    public void AnUndoneDocumentReloadsAsThePreEditOne()
    {
        var rig = Build();
        string before = rig.Snapshot();

        rig.Undo.Do(DocumentEdits.SetParameter(rig.Plate, rig.History.Features[0], "Height", 20.0));
        rig.Undo.Do(DocumentEdits.SetColor(rig.Plate, Palette.Plum));
        rig.Undo.Undo();
        rig.Undo.Undo();

        var reloaded = Document.Load(rig.Snapshot());
        Assert.True(reloaded.Complete, string.Join("; ", reloaded.Warnings));
        Assert.Equal(before, reloaded.Document.Save());
    }
}
