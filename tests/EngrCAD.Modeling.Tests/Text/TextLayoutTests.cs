using EngrCAD.Modeling.Text;
using Xunit;

namespace EngrCAD.Modeling.Tests.Text;

/// <summary>
/// Text layout against the synthetic font, whose metrics are known exactly: advance
/// widths 400 ('I'), 800 ('O'), 500 (space) font units on a 1000-unit em, and a single
/// kerning pair I|O of -50. At size 10 those are 4, 8, 5 and -0.5 — so every expected
/// number below is arithmetic, not a recorded output.
/// </summary>
public class TextLayoutTests
{
    private static readonly TrueTypeFont Font = TrueTypeFont.Load(SyntheticFont.Build());
    private const double Size = 10;                                  // scale = 10/1000 = 0.01

    [Fact]
    public void AdvanceWidth_SumsTheFontsAdvanceWidths()
    {
        Assert.Equal(4, TextOutlines.AdvanceWidth("I", Font, Size), 9);
        Assert.Equal(8, TextOutlines.AdvanceWidth("II", Font, Size), 9);
        Assert.Equal(4 + 5 + 8, TextOutlines.AdvanceWidth("I O", Font, Size), 9);   // space still advances
        Assert.Equal(0, TextOutlines.AdvanceWidth("", Font, Size), 9);
    }

    [Fact]
    public void AdvanceWidth_AppliesPairKerningUnlessTurnedOff()
    {
        // The font kerns I|O by -50 units = -0.5 at this size.
        Assert.Equal(4 + 8 - 0.5, TextOutlines.AdvanceWidth("IO", Font, Size), 9);
        Assert.Equal(4 + 8, TextOutlines.AdvanceWidth("IO", Font, Size, new TextStyle { Kerning = false }), 9);

        // Kerning is directional: O|I is not a pair.
        Assert.Equal(8 + 4, TextOutlines.AdvanceWidth("OI", Font, Size), 9);
    }

    [Fact]
    public void LetterSpacing_IsInsertedBetweenGlyphsOnly()
    {
        var tracked = new TextStyle { LetterSpacing = 0.1 };          // 0.1 em = 1.0 at size 10

        Assert.Equal(4, TextOutlines.AdvanceWidth("I", Font, Size, tracked), 9);          // no trailing gap
        Assert.Equal(4 + 1 + 4, TextOutlines.AdvanceWidth("II", Font, Size, tracked), 9);
        Assert.Equal(4 + 1 + 4 + 1 + 4, TextOutlines.AdvanceWidth("III", Font, Size, tracked), 9);
    }

    [Fact]
    public void Sketches_PlaceEachGlyphAtItsPenPosition()
    {
        // 'I' is a 200-unit-wide bar with a 100-unit left side bearing: x in [1, 3] at
        // size 10, and the second copy 4 further along.
        var sketches = TextOutlines.Sketches("II", Font, Size);

        Assert.Equal(2, sketches.Count);
        Assert.Equal(1, sketches[0].Bounds.Min.X, 9);
        Assert.Equal(3, sketches[0].Bounds.Max.X, 9);
        Assert.Equal(5, sketches[1].Bounds.Min.X, 9);
        Assert.Equal(7, sketches[1].Bounds.Max.X, 9);
        Assert.Equal(0, sketches[0].Bounds.Min.Y, 9);                 // the baseline is y = 0
        Assert.Equal(7, sketches[0].Bounds.Max.Y, 9);
    }

    [Fact]
    public void Sketches_BlankGlyphsAdvanceThePenWithoutDrawing()
    {
        var sketches = TextOutlines.Sketches("I I", Font, Size);

        Assert.Equal(2, sketches.Count);                              // the space draws nothing
        Assert.Equal(1, sketches[0].Bounds.Min.X, 9);
        Assert.Equal(4 + 5 + 1, sketches[1].Bounds.Min.X, 9);         // 'I' advance + space advance + bearing
    }

    [Theory]
    [InlineData(TextAlign.Left, 1, 7)]
    [InlineData(TextAlign.Center, -3, 3)]
    [InlineData(TextAlign.Right, -7, -1)]
    public void Align_PositionsTheLineRelativeToXZero(TextAlign align, double minX, double maxX)
    {
        // "II" is 8 wide; the ink runs from the left bearing (1) to 7.
        var bounds = TextOutlines.Bounds("II", Font, Size, new TextStyle { Align = align });

        Assert.Equal(minX, bounds.Min.X, 9);
        Assert.Equal(maxX, bounds.Max.X, 9);
    }

    [Fact]
    public void Align_MeasuresEachLineSeparately()
    {
        // Centered, the 4-wide line starts at -2 and the 8-wide line at -4.
        var sketches = TextOutlines.Sketches("I\nII", Font, Size, new TextStyle { Align = TextAlign.Center });

        Assert.Equal(3, sketches.Count);
        Assert.Equal(-1, sketches[0].Bounds.Min.X, 9);                // -2 + 1 bearing
        Assert.Equal(-3, sketches[1].Bounds.Min.X, 9);                // -4 + 1 bearing
    }

    [Fact]
    public void MultiLine_StacksBaselinesDownward()
    {
        Assert.Equal(12, TextOutlines.LineHeight(Size), 9);           // default 1.2 em
        Assert.Equal(15, TextOutlines.LineHeight(Size, new TextStyle { LineSpacing = 1.5 }), 9);

        var sketches = TextOutlines.Sketches("I\nI", Font, Size);
        Assert.Equal(2, sketches.Count);
        Assert.Equal(0, sketches[0].Bounds.Min.Y, 9);
        Assert.Equal(-12, sketches[1].Bounds.Min.Y, 9);
        Assert.Equal(-5, sketches[1].Bounds.Max.Y, 9);                // -12 + 7

        var bounds = TextOutlines.Bounds("I\nI", Font, Size);
        Assert.Equal(-12, bounds.Min.Y, 9);
        Assert.Equal(7, bounds.Max.Y, 9);
    }

    [Fact]
    public void MultiLine_AcceptsWindowsAndClassicMacLineEndings()
    {
        double reference = TextOutlines.Bounds("I\nI", Font, Size).Min.Y;

        Assert.Equal(reference, TextOutlines.Bounds("I\r\nI", Font, Size).Min.Y, 9);
        Assert.Equal(reference, TextOutlines.Bounds("I\rI", Font, Size).Min.Y, 9);
    }

    [Fact]
    public void BlankText_DrawsNothingButIsNotAnError()
    {
        Assert.Empty(TextOutlines.Sketches("", Font, Size));
        Assert.Empty(TextOutlines.Sketches("   ", Font, Size));
        Assert.True(TextOutlines.Bounds("   ", Font, Size).IsEmpty);
        Assert.Equal(15, TextOutlines.AdvanceWidth("   ", Font, Size), 9);   // spaces still advance
    }

    [Fact]
    public void MissingGlyph_NamesTheCharacterAndTheFont()
    {
        var error = Assert.Throws<ArgumentException>(() => TextOutlines.Sketches("IZI", Font, Size));

        Assert.Contains("'Z'", error.Message);
        Assert.Contains("U+005A", error.Message);
        Assert.Contains(SyntheticFont.FamilyName, error.Message);
    }

    [Fact]
    public void Layout_ValidatesItsArguments()
    {
        Assert.Throws<ArgumentNullException>(() => TextOutlines.Sketches(null!, Font, Size));
        Assert.Throws<ArgumentNullException>(() => TextOutlines.Sketches("I", null!, Size));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextOutlines.Sketches("I", Font, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextOutlines.AdvanceWidth("I", Font, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextOutlines.LineHeight(0));
    }

    [Fact]
    public void CapHeightSizing_MakesCapitalsTheRequestedHeight()
    {
        // The synthetic font declares a 700-unit cap height, and 'I' is exactly that
        // tall — so sizing by cap height puts the top of 'I' at the requested value.
        double size = Font.EmSizeForCapHeight(5);
        var bounds = TextOutlines.Bounds("I", Font, size);

        Assert.Equal(5, bounds.Max.Y, 9);
    }
}
