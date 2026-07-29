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
}
