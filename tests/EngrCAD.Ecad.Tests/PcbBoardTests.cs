using EngrCAD.Core;
using EngrCAD.Ecad;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Ecad.Tests;

public class PcbBoardTests
{
    // ---- the plate volume oracle: area x thickness - hole cylinders ---------

    [Fact]
    public void UndrilledPlate_HasExactlyOutlineAreaTimesThickness()
    {
        var board = PcbBoard.Rectangle(50, 40, 1.6);
        double expected = 2000.0 * 1.6;   // 50 x 40 x 1.6

        Assert.Equal(2000.0, board.OutlineArea(), 9);
        Assert.Equal(expected, board.ExpectedPlateVolume(), 9);

        // A polygon prism is an exact identity (planar caps, planar walls).
        double measured = new Part("plate", board.Plate()).MassProperties().Volume;
        Assert.Equal(expected, measured, expected * 1e-6);
    }

    [Fact]
    public void DrilledPlate_MatchesTheClosedFormMinusHoleCylinders()
    {
        var board = PcbBoard.Rectangle(50, 40, 1.6, PcbStackup.TwoLayer(1.6));
        board = new PcbBoard(board.OutlinePoints, 1.6, holes:
        [
            new BoardHole(new Vector2d(-20, -15), 3.0, BoardHoleKind.Mounting),
            new BoardHole(new Vector2d(20, 15), 3.0, BoardHoleKind.Mounting),
        ]);

        double expected = (2000.0 - 2 * System.Math.PI * 1.5 * 1.5) * 1.6;
        Assert.Equal(expected, board.ExpectedPlateVolume(), 9);

        double measured = new Part("plate", board.Plate()).MassProperties().Volume;
        // Richardson-extrapolated B-Rep mass properties, so a tight relative agreement.
        Assert.Equal(expected, measured, expected * 1e-4);
    }

    // ---- the stackup ---------------------------------------------------------

    [Fact]
    public void TwoLayerStackup_PutsTopAtThicknessAndBottomAtZero()
    {
        var s = PcbStackup.TwoLayer(1.6);
        Assert.Equal(2, s.Coppers.Count);
        Assert.Equal(1.6, s.Top.Z);
        Assert.Equal("Top", s.Top.Name);
        Assert.Equal(0, s.Bottom.Z);
        Assert.Equal(s.Top, s.Outer(CopperSide.Top));
        Assert.Equal(s.Bottom, s.Outer(CopperSide.Bottom));
    }

    [Fact]
    public void FourLayerStackup_OrdersInnerCoppersBetweenTopAndBottom()
    {
        var s = PcbStackup.Layers(1.6, ("In1", 1.1), ("In2", 0.5));
        Assert.Equal(4, s.Coppers.Count);
        Assert.Equal(new[] { 1.6, 1.1, 0.5, 0.0 }, s.Coppers.Select(c => c.Z));
        Assert.Equal("Top", s.Top.Name);
        Assert.Equal("Bottom", s.Bottom.Name);
    }

    [Fact]
    public void InnerCopperOutsideTheBoard_IsRefusedByName()
    {
        var ex = Assert.Throws<System.ArgumentException>(() => PcbStackup.Layers(1.6, ("Bad", 2.0)));
        Assert.Contains("Bad", ex.Message);
    }

    // ---- outline containment -------------------------------------------------

    [Fact]
    public void ContainsInOutline_SeesInsideAndOutside()
    {
        var board = PcbBoard.Rectangle(50, 40, 1.6);
        Assert.True(board.ContainsInOutline(new Vector2d(0, 0)));
        Assert.True(board.ContainsInOutline(new Vector2d(24, 19)));
        Assert.False(board.ContainsInOutline(new Vector2d(26, 0)));
        Assert.False(board.ContainsInOutline(new Vector2d(0, 25)));
    }

    // ---- refusals ------------------------------------------------------------

    [Fact]
    public void NonPositiveThickness_IsRefusedByName()
    {
        var ex = Assert.Throws<System.ArgumentException>(() => PcbBoard.Rectangle(50, 40, 0));
        Assert.Contains("thickness", ex.Message);
    }

    [Fact]
    public void OutlineWithTooFewPoints_IsRefusedByName()
    {
        var ex = Assert.Throws<System.ArgumentException>(() =>
            new PcbBoard([new Vector2d(0, 0), new Vector2d(1, 0)], 1.6));
        Assert.Contains("3 points", ex.Message);
    }
}
