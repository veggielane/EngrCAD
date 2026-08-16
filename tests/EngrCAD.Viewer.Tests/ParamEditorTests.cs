using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// Which editor a <c>[Param]</c> gets, asserted as a value. The metadata to decide it
/// has been in the registry since features landed — a type and a <c>Min</c>/<c>Max</c>
/// range — so free text was the placeholder, not the design.
/// <para>The rule lives in <c>EngrCAD.Viewer.Core</c> rather than in the Avalonia panel
/// for the reason every shared render decision does: a browser properties panel must not
/// grow a second opinion about what a bounded parameter looks like. The tests read the
/// metadata off REAL feature types, not hand-built <c>ParamInfo</c>s, so a standard
/// feature that drops its range is a failure here rather than a worse panel nobody
/// notices.</para>
/// </summary>
public class ParamEditorTests
{
    private enum Fit { Slip, Press, Tap }

    private sealed class Sampler : Feature
    {
        [Param(Min = 1, Max = 20, Units = "mm", Description = "how deep")]
        public double Depth { get; init; } = 5;

        [Param(Min = 1, Max = 8)]
        public int Count { get; init; } = 3;

        [Param]
        public double Unbounded { get; init; } = 1;

        [Param]
        public bool Through { get; init; }

        [Param]
        public Fit Fit { get; init; } = Fit.Slip;

        [Param]
        public Fit? OptionalFit { get; init; }

        [Param]
        public string Label { get; init; } = "";

        [Param(Min = 4, Max = 4)]
        public double Pinned { get; init; } = 4;

        public override Shape Apply(FeatureContext context) => context.Body!;
    }

    private static ParamInfo Param(string name) =>
        new Sampler().Parameters.Single(p => p.Name == name);

    [Theory]
    [InlineData("Depth", ParamEditorKind.Slider)]
    [InlineData("Count", ParamEditorKind.Slider)]
    [InlineData("Unbounded", ParamEditorKind.Text)]   // +-infinity has nothing to map
    [InlineData("Through", ParamEditorKind.Toggle)]
    [InlineData("Fit", ParamEditorKind.Choice)]
    [InlineData("Label", ParamEditorKind.Text)]
    [InlineData("Pinned", ParamEditorKind.Text)]      // Min == Max has nothing to choose
    public void EachParameterGetsTheAffordanceItsMetadataEarns(string name, ParamEditorKind expected) =>
        Assert.Equal(expected, ParamEditors.KindFor(Param(name)));

    [Fact]
    public void AWholeNumberParameterSnaps_AndAFractionalOneDoesNot()
    {
        Assert.True(ParamEditors.IsWhole(Param("Count")));
        Assert.False(ParamEditors.IsWhole(Param("Depth")));
    }

    [Fact]
    public void ASlidersPositionIsClampedIntoItsOwnRange()
    {
        // A value outside the declared range is legal in the model (validation is the
        // feature's job at Apply time), so the SLIDER must not be asked to show it —
        // an out-of-range position throws in Avalonia rather than degrading.
        Assert.Equal(5, ParamEditors.Position(Param("Depth")), 12);
        var over = new ParamInfo("Depth", typeof(double), 500.0, 1, 20, "mm", null);
        Assert.Equal(20, ParamEditors.Position(over), 12);
        var under = new ParamInfo("Depth", typeof(double), -3.0, 1, 20, "mm", null);
        Assert.Equal(1, ParamEditors.Position(under), 12);
    }

    [Fact]
    public void ANullableParameterIsEditedAsItsUnderlyingType()
    {
        // FeatureHistory.Convert's nullable gap is filed separately; the EDITOR choice
        // must not also be wrong, or a double? parameter would get a text box because
        // of a type test rather than because of a decision.
        Assert.Equal(ParamEditorKind.Toggle,
            ParamEditors.KindFor(new ParamInfo("On", typeof(bool?), null,
                double.NegativeInfinity, double.PositiveInfinity, null, null)));
        Assert.Equal(ParamEditorKind.Slider,
            ParamEditors.KindFor(new ParamInfo("Angle", typeof(double?), 30.0, 0, 90, "deg", null)));
    }

    /// <summary>
    /// The optional-ENUM editor, and the reason it earns its place: a dropdown whose rows
    /// are exactly <c>Enum.GetNames</c> has no way to say "unset", so a nullable enum
    /// parameter could be moved off "inherit" and never back — the same gap a slider has,
    /// and the gap that made <c>EdgeFlangeFeature.Relief</c> a SECOND enum carrying its own
    /// <c>None</c> beside the geometry's own kind. The leading null row closes it, and a
    /// non-nullable enum's list must be untouched or every incumbent dropdown grows a row
    /// that means nothing.
    /// </summary>
    [Fact]
    public void ANullableEnumOffersANoneRow_AndAPlainEnumDoesNot()
    {
        Assert.Equal(ParamEditorKind.Choice, ParamEditors.KindFor(Param("OptionalFit")));
        Assert.Equal([null, "Slip", "Press", "Tap"], ParamEditors.EnumChoices(Param("OptionalFit")));
        Assert.Equal(["Slip", "Press", "Tap"], ParamEditors.EnumChoices(Param("Fit")));

        // Rows carry MEMBER names (what the JSON seam parses back) and are LABELLED for a
        // reader — the two are separate on purpose, since "(none)" is not an enum member.
        Assert.Equal(ParamEditors.NoneLabel, ParamEditors.EnumLabel(null));
        Assert.Equal("Press", ParamEditors.EnumLabel("Press"));
        Assert.Empty(ParamEditors.EnumChoices(Param("Depth")));
    }

    /// <summary>The other half of an optional enum being editable: the model layer has to
    /// take the null back. Asserted through the SAME JSON seam every editor writes through,
    /// because a dropdown row that no seam accepts is not an editor.</summary>
    [Fact]
    public void AnUnsetOptionalEnumRoundTripsThroughTheParameterSeam()
    {
        var flange = new EdgeFlangeFeature { Relief = SheetReliefKind.Obround };
        var history = new FeatureHistory();
        history.Add(new BaseFlangeFeature(Sketch.Rectangle(40, 30)));
        history.Add(flange);

        Assert.Contains("\"Relief\": \"Obround\"", history.SaveParameters(), StringComparison.Ordinal);
        Assert.Empty(history.LoadParameters(
            "{\"" + flange.Name + "\": {\"Relief\": null}}"));
        Assert.Null(((EdgeFlangeFeature)history.Features[1]).Relief);
        Assert.Contains("\"Relief\": null", history.SaveParameters(), StringComparison.Ordinal);
    }

    [Fact]
    public void RealStandardFeaturesGetRealAffordances()
    {
        // The point of the whole change, read off a feature a user actually edits: a
        // hole's depth is a bounded number, so it gets a slider rather than a box to
        // type a number into and hope.
        var hole = new HoleFeature(StandardHoles.Clearance(5), [new Vector2d(0, 0)]) { Depth = 10 };
        var depth = hole.Parameters.Single(p => p.Name == nameof(HoleFeature.Depth));
        Assert.True(ParamEditors.HasRange(depth) == (ParamEditors.KindFor(depth) == ParamEditorKind.Slider),
            "a bounded numeric parameter and a slider are the same claim");
    }

    // ---- the material dropdown (the properties panel's other typed editor) ----

    [Fact]
    public void MaterialChoices_OfferNoneFirst_ThenTheCatalogueInOrder()
    {
        var choices = ParamEditors.MaterialChoices(null);

        // "(none)" is a legal and common answer, so it must be reachable and it leads.
        Assert.Null(choices[0]);
        Assert.Equal("(none)", ParamEditors.MaterialLabel(choices[0]));
        Assert.Equal(Materials.All.Count + 1, choices.Count);
        Assert.Equal(Materials.All, choices.Skip(1).Select(m => m!).ToList());
    }

    [Fact]
    public void ACatalogueMaterialSelectsItsOwnRow_RatherThanAddingOne()
    {
        var choices = ParamEditors.MaterialChoices(Materials.Brass);
        Assert.Equal(Materials.All.Count + 1, choices.Count);
        Assert.Equal(
            Materials.All.ToList().IndexOf(Materials.Brass) + 1,
            choices.ToList().IndexOf(Materials.Brass));

        // Value equality, not reference: a rebuilt copy of a catalogue entry must select
        // the catalogue's row instead of appending a twin.
        var rebuilt = new Material("Brass C36000", 97_000, 0.31, 8.50e-9, 115.0, 3.80e8, 20.5e-6);
        Assert.Equal(Materials.All.Count + 1, ParamEditors.MaterialChoices(rebuilt).Count);
    }

    /// <summary>
    /// A material the catalogue does not carry — one a design built, or a fastener grade
    /// a catalogue component brought with it — gets its own row. Without it the dropdown
    /// would show nothing selected for a part that plainly states a material, which reads
    /// as "not set", and one idle click would discard it.
    /// </summary>
    [Fact]
    public void AMaterialTheCatalogueDoesNotCarry_StillHasARow()
    {
        var custom = new Material("Mystery alloy", density: 7.8e-9);
        var choices = ParamEditors.MaterialChoices(custom);

        Assert.Equal(Materials.All.Count + 2, choices.Count);
        Assert.Same(custom, choices[^1]);
        Assert.Equal("Mystery alloy", ParamEditors.MaterialLabel(choices[^1]));

        // The case that made the rule: a screw's material is a FastenerMaterials grade.
        var screw = StandardComponents.CapScrew(6, 20).ToPart();
        var screwChoices = ParamEditors.MaterialChoices(screw.Material);
        Assert.Equal(screw.Material, screwChoices[^1]);
        Assert.True(screwChoices.ToList().IndexOf(screw.Material) > 0, "selectable, not lost");
    }

    [Fact]
    public void ResultChoices_ListNonePlusTheResults_AndKeepACurrentTheEditRemoved()
    {
        // "(none)" first, the results in their own order — and a current display whose
        // result an edit has since removed is LISTED (the MaterialChoices rule: a
        // control that cannot show the current value reads as "not set", and one idle
        // click would discard it).
        var plain = ParamEditors.ResultChoices(["T@0", "T@5"], currentField: null);
        Assert.Equal([null, "T@0", "T@5"], plain);

        var current = ParamEditors.ResultChoices(["T@0"], currentField: "removed");
        Assert.Equal([null, "T@0", "removed"], current);

        // A current that IS among the results appears once, not twice.
        var listed = ParamEditors.ResultChoices(["T@0", "T@5"], currentField: "T@5");
        Assert.Equal([null, "T@0", "T@5"], listed);

        Assert.Equal("(none)", ParamEditors.ResultLabel(null));
        Assert.Equal("T@5", ParamEditors.ResultLabel("T@5"));
    }
}
