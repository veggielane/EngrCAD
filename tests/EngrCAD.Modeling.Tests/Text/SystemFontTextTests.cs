using EngrCAD.Modeling.Text;
using Xunit;

namespace EngrCAD.Modeling.Tests.Text;

/// <summary>
/// The outline pipeline against a real installed font (see <see cref="SystemFonts"/>):
/// structural assertions only — every letter encloses material, counters come back as
/// holes rather than as extra outlines, and sizes scale quadratically in area.
/// Skips gracefully when the machine has no TrueType font.
/// </summary>
public class SystemFontTextTests
{
    [SkippableFact]
    public void RealFont_EveryLetterAndDigitEnclosesMaterial()
    {
        Skip.If(SystemFonts.SkipReason is not null, SystemFonts.SkipReason);
        var font = SystemFonts.Font;

        foreach (char c in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789")
        {
            var sketches = TextOutlines.GlyphSketches(font, c, 10);
            Assert.NotEmpty(sketches);
            double area = sketches.Sum(s => s.Area());
            Assert.True(area > 0, $"'{c}' encloses {area:g6}, expected a positive area");
        }
    }

    [SkippableFact]
    public void RealFont_CountersComeBackAsHolesNotExtraOutlines()
    {
        Skip.If(SystemFonts.SkipReason is not null, SystemFonts.SkipReason);
        var font = SystemFonts.Font;

        // 'O' and '8' are single outlines with one and two counters: if the counters
        // were classified as outlines we would get 2 and 3 sketches, and the area would
        // exceed the outline's own.
        var o = Assert.Single(TextOutlines.GlyphSketches(font, 'O', 10));
        var eight = Assert.Single(TextOutlines.GlyphSketches(font, '8', 10));
        Assert.True(o.Area() < o.Bounds.Size.X * o.Bounds.Size.Y);
        Assert.True(eight.Area() < eight.Bounds.Size.X * eight.Bounds.Size.Y);

        // A colon really is two disjoint outlines.
        Assert.Equal(2, TextOutlines.GlyphSketches(font, ':', 10).Count);
    }

    [SkippableFact]
    public void RealFont_AreaScalesWithTheSquareOfTheSize()
    {
        Skip.If(SystemFonts.SkipReason is not null, SystemFonts.SkipReason);
        var font = SystemFonts.Font;

        double small = TextOutlines.GlyphSketches(font, 'B', 4).Sum(s => s.Area());
        double large = TextOutlines.GlyphSketches(font, 'B', 12).Sum(s => s.Area());

        Assert.Equal(9 * small, large, 9);
    }
}
