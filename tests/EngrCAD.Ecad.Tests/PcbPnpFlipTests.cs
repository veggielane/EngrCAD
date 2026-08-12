using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// The pick-and-place bottom-flip axis option — a per-fabricator convention. A bottom-side row's
/// rotation is a REFLECTION of the board-frame angle (a sign swap, never a cos), so both axes stay
/// exact for a quarter turn: flip about X is <c>(360 − rot) mod 360</c> (the default, unchanged), flip
/// about Y is <c>(180 − rot) mod 360</c>. Top rows are the placement angle verbatim either way.
/// </summary>
public sealed class PcbPnpFlipTests
{
    private static PartDefinition Res() => new(
        "R", "R",
        [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive)],
        new Footprint("R", [Pad.Smd("1", new Vector2d(-0.5, 0), 0.6, 0.6), Pad.Smd("2", new Vector2d(0.5, 0), 0.6, 0.6)]));

    private static PcbLayout Bottom4()
    {
        var sch = new Schematic("flip");
        foreach (var r in new[] { "R0", "R90", "R180", "R270" })
            sch.Add(r, Res());
        var layout = new PcbLayout(sch, PcbBoard.Rectangle(40, 30, 1.6));
        layout.Place("R0", 0, 0, 0, CopperSide.Bottom);
        layout.Place("R90", 1, 0, 90, CopperSide.Bottom);
        layout.Place("R180", 2, 0, 180, CopperSide.Bottom);
        layout.Place("R270", 3, 0, 270, CopperSide.Bottom);
        return layout;
    }

    [Fact]
    public void FlipAboutY_MirrorsTheBottomRotationAs180MinusRot_Exactly()
    {
        var rows = PcbPickAndPlace.Compute(Bottom4(), BottomFlipAxis.Y);
        Assert.Equal(180.0, rows[0].Rotation);   // 180 - 0
        Assert.Equal(90.0, rows[1].Rotation);    // 180 - 90
        Assert.Equal(0.0, rows[2].Rotation);     // 180 - 180
        Assert.Equal(270.0, rows[3].Rotation);   // 180 - 270 → -90 → 270
    }

    [Fact]
    public void FlipAboutX_IsTheDefaultAndUnchanged()
    {
        var byDefault = PcbPickAndPlace.Compute(Bottom4());
        var explicitX = PcbPickAndPlace.Compute(Bottom4(), BottomFlipAxis.X);

        // The default IS flip-about-X (the prior emission), and it differs from flip-about-Y.
        Assert.Equal(0.0, byDefault[0].Rotation);    // 360 - 0
        Assert.Equal(270.0, byDefault[1].Rotation);  // 360 - 90
        for (int i = 0; i < byDefault.Count; i++)
            Assert.Equal(byDefault[i].Rotation, explicitX[i].Rotation);

        // The CSV is byte-identical between the no-arg call and the explicit X — the default changed
        // nothing.
        Assert.Equal(PcbPickAndPlace.ToCsv(Bottom4()), PcbPickAndPlace.ToCsv(Bottom4(), BottomFlipAxis.X));
    }

    [Fact]
    public void TopRowsAreVerbatim_UnderEitherFlip()
    {
        var sch = new Schematic("top");
        sch.Add("T1", Res());
        var layout = new PcbLayout(sch, PcbBoard.Rectangle(40, 30, 1.6));
        layout.Place("T1", 5, 5, 123, CopperSide.Top);

        Assert.Equal(123.0, PcbPickAndPlace.Compute(layout, BottomFlipAxis.X)[0].Rotation);
        Assert.Equal(123.0, PcbPickAndPlace.Compute(layout, BottomFlipAxis.Y)[0].Rotation);
    }
}
