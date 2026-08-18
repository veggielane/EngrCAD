using Xunit;

namespace EngrCAD.Modeling.Tests.Text;

/// <summary>
/// The variable-font reader against a real, shipped variable font (Bahnschrift on
/// Windows 10 1709+, Segoe UI Variable on Windows 11) — the synthetic font proves exact
/// decoding, this proves the reader survives the messy reality: thousands of glyphs,
/// real shared tuples, real packed point numbers, real IUP, real <c>HVAR</c>.
/// <para>Assertions are STRUCTURAL and RELATIONAL only (see <see cref="SystemFonts"/>):
/// what a bold instance's outline is exactly is not ours to pin down, but that it is
/// heavier than the light one, that its cap height does not move with weight, and that
/// the default instance is bit-identical to the un-instanced read all are.</para>
/// </summary>
public class SystemVariableFontTests
{
    [SkippableFact]
    public void ARealVariableFontDeclaresItsAxesAndInstances()
    {
        Skip.If(SystemFonts.VariableSkipReason is not null, SystemFonts.VariableSkipReason);
        var font = SystemFonts.VariableFont;

        Assert.True(font.IsVariable);
        var weight = font.VariationAxes.Single(a => a.Tag == "wght");
        Assert.True(weight.Minimum < weight.Maximum, "a weight axis spans a range");
        Assert.InRange(weight.Default, weight.Minimum, weight.Maximum);
        Assert.NotEmpty(weight.Name);
        Assert.NotEmpty(font.NamedInstances);
        Assert.All(font.NamedInstances, i =>
        {
            Assert.NotEmpty(i.Name);
            Assert.Equal(font.VariationAxes.Count, i.Coordinates.Count);
        });
    }

    /// <summary>
    /// The headline: a heavier instance draws HEAVIER letters. Cap height is measured
    /// from the same glyph and must NOT move — weight thickens stems, it does not grow
    /// the letter — which is what separates "the deltas were applied" from "something
    /// scaled the outline".
    /// </summary>
    [SkippableFact]
    public void AHeavierInstanceThickensTheStemsWithoutGrowingTheLetter()
    {
        Skip.If(SystemFonts.VariableSkipReason is not null, SystemFonts.VariableSkipReason);
        var font = SystemFonts.VariableFont;
        var weight = font.VariationAxes.Single(a => a.Tag == "wght");

        var light = font.WithVariation(("wght", weight.Minimum));
        var heavy = font.WithVariation(("wght", weight.Maximum));
        Assert.True(light.TryGetGlyph('H', out var lightH));
        Assert.True(heavy.TryGetGlyph('H', out var heavyH));

        Assert.True(heavyH.Bounds.Size.X > lightH.Bounds.Size.X,
            $"a bold H should be wider than a light one ({heavyH.Bounds.Size.X} vs {lightH.Bounds.Size.X})");
        Assert.Equal(lightH.Bounds.Size.Y, heavyH.Bounds.Size.Y, 6);
        Assert.Equal(lightH.Bounds.Max.Y, heavyH.Bounds.Max.Y, 6);

        // Every point moved is still a point: the contour structure is a property of the
        // glyph, not of the instance.
        Assert.Equal(lightH.Contours.Count, heavyH.Contours.Count);
        for (int c = 0; c < lightH.Contours.Count; c++)
            Assert.Equal(lightH.Contours[c].Points.Count, heavyH.Contours[c].Points.Count);
    }

    /// <summary>
    /// The advance-width path on real data: a font whose advances vary lays the same
    /// string out to a different width, and one whose advances do not (Bahnschrift keeps
    /// them fixed on many glyphs) at least never lays it out to a NEGATIVE or absurd one.
    /// </summary>
    [SkippableFact]
    public void LayoutFollowsTheInstancesOwnAdvances()
    {
        Skip.If(SystemFonts.VariableSkipReason is not null, SystemFonts.VariableSkipReason);
        var font = SystemFonts.VariableFont;
        var weight = font.VariationAxes.Single(a => a.Tag == "wght");

        double light = TextOutlines.AdvanceWidth("Handgloves", font.WithVariation(("wght", weight.Minimum)), 100);
        double heavy = TextOutlines.AdvanceWidth("Handgloves", font.WithVariation(("wght", weight.Maximum)), 100);

        Assert.True(light > 0 && heavy > 0);
        // A bolder instance never lays out NARROWER than a lighter one.
        Assert.True(heavy >= light - 1e-9, $"bold {heavy} should not be narrower than light {light}");
    }

    /// <summary>The default coordinate is the un-instanced font, bit for bit — on a real
    /// font with real shared tuples and real IUP, over every glyph a text string
    /// touches.</summary>
    [SkippableFact]
    public void TheDefaultInstanceIsBitIdenticalOnARealFont()
    {
        Skip.If(SystemFonts.VariableSkipReason is not null, SystemFonts.VariableSkipReason);
        var font = SystemFonts.VariableFont;
        var defaulted = font.WithVariation();

        foreach (char c in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789")
        {
            if (!font.TryGetGlyphIndex(c, out int index))
                continue;
            var plain = font.GetGlyph(index);
            var same = defaulted.GetGlyph(index);
            Assert.Equal(plain.Contours.Count, same.Contours.Count);
            for (int k = 0; k < plain.Contours.Count; k++)
            {
                var lp = plain.Contours[k].Points;
                var rp = same.Contours[k].Points;
                Assert.Equal(lp.Count, rp.Count);
                for (int i = 0; i < lp.Count; i++)
                {
                    Assert.Equal(BitConverter.DoubleToInt64Bits(lp[i].Position.X),
                        BitConverter.DoubleToInt64Bits(rp[i].Position.X));
                    Assert.Equal(BitConverter.DoubleToInt64Bits(lp[i].Position.Y),
                        BitConverter.DoubleToInt64Bits(rp[i].Position.Y));
                }
            }
            Assert.Equal(BitConverter.DoubleToInt64Bits(plain.AdvanceWidth),
                BitConverter.DoubleToInt64Bits(same.AdvanceWidth));
        }
    }

    /// <summary>
    /// A named instance the font itself ships resolves to its own coordinate and draws.
    /// <para>ONE glyph, deliberately: a real font's harder letters have pre-existing
    /// B-Rep limits that have nothing to do with variation (Bahnschrift's 'A', 'D', 'R'
    /// and 'N' refuse at the DEFAULT instance too), so a word here would be measuring
    /// the boolean rather than the reader.</para>
    /// </summary>
    [SkippableFact]
    public void ANamedInstanceOfARealFontModelsToASolid()
    {
        Skip.If(SystemFonts.VariableSkipReason is not null, SystemFonts.VariableSkipReason);
        var font = SystemFonts.VariableFont;
        var instance = font.NamedInstances.FirstOrDefault(i => i.Name.Contains("Bold", StringComparison.OrdinalIgnoreCase))
            ?? font.NamedInstances[^1];

        var named = font.WithNamedInstance(instance.Name);

        // The name resolves to the coordinate the font itself records against it.
        foreach (var (tag, value) in instance.Coordinates)
            Assert.Equal(value, named.Variation[tag], 9);

        var mesh = Shape.Text("H", named, size: 20, height: 2).ToMesh();
        Assert.True(mesh.IsClosed, "modelled text should be a closed solid");
        Assert.True(mesh.Volume() > 0);
    }

    /// <summary>Instancing on a real font is a pure function of its coordinate — two
    /// instances at the same value produce the same geometry, bit for bit.</summary>
    [SkippableFact]
    public void InstancingIsDeterministic()
    {
        Skip.If(SystemFonts.VariableSkipReason is not null, SystemFonts.VariableSkipReason);
        var font = SystemFonts.VariableFont;
        var weight = font.VariationAxes.Single(a => a.Tag == "wght");
        double value = (weight.Default + weight.Maximum) / 2;

        var a = font.WithVariation(("wght", value));
        var b = font.WithVariation(("wght", value));
        Assert.True(a.TryGetGlyph('g', out var left));
        Assert.True(b.TryGetGlyph('g', out var right));

        for (int c = 0; c < left.Contours.Count; c++)
        {
            for (int i = 0; i < left.Contours[c].Points.Count; i++)
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(left.Contours[c].Points[i].Position.X),
                    BitConverter.DoubleToInt64Bits(right.Contours[c].Points[i].Position.X));
                Assert.Equal(BitConverter.DoubleToInt64Bits(left.Contours[c].Points[i].Position.Y),
                    BitConverter.DoubleToInt64Bits(right.Contours[c].Points[i].Position.Y));
            }
        }
    }
}
