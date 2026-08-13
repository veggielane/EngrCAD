using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// IPC-7351 land-pattern generation (<see cref="Ipc7351"/>). The fillet-goal tables are ⚠
/// transcribed nominals, so the tests assert what a transcription CANNOT protect on its own: the
/// zero-tolerance identity (with every tolerance zero the formulas reduce to the bare goals
/// exactly — the check that catches a swapped min/max), density and tolerance MONOTONICITY (a
/// denser level and a wider tolerance move Z/G in known directions), the exact pad symmetry and
/// numbering conventions per family, the JEDEC BGA lettering, the closed-gap refusal by name,
/// and end-to-end usability — a generated footprint placed on a real board passes the layout's
/// own pin-covering check and the default DRC.
/// </summary>
public sealed class Ipc7351Tests
{
    /// <summary>Z, G and the pad size read back off a generated two-pad land (the pads are the
    /// only public surface, which is the point — the identity Z = G + 2·len must hold there).</summary>
    private static (double Z, double G, double Len, double Width) LandsOf(Footprint fp)
    {
        var right = fp.Pads.Single(p => p.Number == "2");
        return (2 * (right.Center.X + right.Width / 2),
                2 * (right.Center.X - right.Width / 2),
                right.Width, right.Height);
    }

    [Fact]
    public void AZeroToleranceComponent_ReducesTheFormulasToTheirGoals_Exactly()
    {
        // Exact body: L = 2.0, W = 1.25, T = 0.5; F = P = 0. At Nominal (toe 0.35, heel 0,
        // side 0): Z = L + 2·toe = 2.70, G = (L − 2T) = 1.00, X = W = 1.25 — the bare goals,
        // every figure a multiple of the 0.05 quantum so rounding is the identity.
        var fp = Ipc7351.Chip("chip", new ChipSpec(2.0, 1.25, 0.5),
            new Ipc7351Options(FabricationTolerance: 0, PlacementTolerance: 0));
        var (z, g, len, width) = LandsOf(fp);
        Assert.Equal(2.70, z, 12);
        Assert.Equal(1.00, g, 12);
        Assert.Equal(0.85, len, 12);
        Assert.Equal(1.25, width, 12);

        // And the two pads mirror exactly (bitwise negation, not arithmetic that lands nearby).
        var p1 = fp.Pads.Single(p => p.Number == "1");
        var p2 = fp.Pads.Single(p => p.Number == "2");
        Assert.Equal(-p1.Center.X, p2.Center.X);
        Assert.Equal(0.0, p1.Center.Y);
    }

    [Fact]
    public void DensityLevels_AreMonotone_InBothDirections()
    {
        // A denser level (Most) buys a LARGER land: Z and the pad grow with density, and on a
        // gullwing (heel goals 0.25/0.35/0.45) the inner gap G SHRINKS — the heel fillet eats
        // inward. A chip's heel goal is 0 at every level, so its G stays put; the gullwing is
        // the family that can see the heel column.
        (double Z, double G, double Len, double Width) At(LandDensity d) =>
            LandsOf(Ipc7351.DualGullwing("soic", StandardBodies.SoicNarrow, 2,
                new Ipc7351Options(Density: d)));

        var least = At(LandDensity.Least);
        var nominal = At(LandDensity.Nominal);
        var most = At(LandDensity.Most);
        Assert.True(least.Z < nominal.Z && nominal.Z < most.Z);
        Assert.True(least.G > nominal.G && nominal.G > most.G);
        Assert.True(least.Len < nominal.Len && nominal.Len < most.Len);
        // The side goals step by 0.02 mm — below half the 0.05 land quantum — so adjacent levels
        // can legitimately round to one width; the monotonicity that survives rounding is ≤ per
        // step and < across the whole range.
        Assert.True(least.Width <= nominal.Width && nominal.Width <= most.Width);
        Assert.True(least.Width < most.Width);
    }

    [Fact]
    public void AWiderTolerance_GrowsZ_AndShrinksG()
    {
        // Same mid dimensions, wider body-length band: the RMS term grows, so Z grows (the toe
        // must still be met by the smallest part) and G shrinks (the heel by the largest).
        var tight = LandsOf(Ipc7351.Chip("t", new ChipSpec(new DimRange(1.95, 2.05), 1.25, 0.5)));
        var loose = LandsOf(Ipc7351.Chip("l", new ChipSpec(new DimRange(1.70, 2.30), 1.25, 0.5)));
        Assert.True(loose.Z > tight.Z);
        Assert.True(loose.G < tight.G);
    }

    [Fact]
    public void ADualGullwing_NumbersItsPins_ThePackageWay()
    {
        // SOIC-16: 1..8 down the LEFT column, 9..16 up the RIGHT — so pin 1 (top-left) and
        // pin 16 (top-right) face each other, as do 8 and 9 at the bottom.
        var fp = Ipc7351.DualGullwing("SOIC-16", StandardBodies.SoicNarrow, 16);
        Assert.Equal(16, fp.Pads.Count);
        var p1 = fp.Pads.Single(p => p.Number == "1");
        var p8 = fp.Pads.Single(p => p.Number == "8");
        var p9 = fp.Pads.Single(p => p.Number == "9");
        var p16 = fp.Pads.Single(p => p.Number == "16");
        Assert.True(p1.Center.X < 0 && p8.Center.X < 0);
        Assert.True(p9.Center.X > 0 && p16.Center.X > 0);
        Assert.Equal(3.5 * 1.27, p1.Center.Y, 12);
        Assert.Equal(-3.5 * 1.27, p8.Center.Y, 12);
        Assert.Equal(p8.Center.Y, p9.Center.Y, 12);
        Assert.Equal(p1.Center.Y, p16.Center.Y, 12);
        Assert.Equal(-p1.Center.X, p16.Center.X);

        // The pitch is exact along the column (positions by multiplication, never accumulation).
        var left = fp.Pads.Where(p => p.Center.X < 0).OrderByDescending(p => p.Center.Y).ToList();
        for (int i = 1; i < left.Count; i++)
            Assert.Equal(1.27, left[i - 1].Center.Y - left[i].Center.Y, 12);
    }

    [Fact]
    public void AQuadGullwing_RunsCounterClockwise_FromTheTopOfTheLeftSide()
    {
        var fp = Ipc7351.QuadGullwing("LQFP-32", StandardBodies.Lqfp0p8, 8);
        Assert.Equal(32, fp.Pads.Count);
        var p1 = fp.Pads.Single(p => p.Number == "1");    // left side, top
        var p9 = fp.Pads.Single(p => p.Number == "9");    // bottom side, left
        var p17 = fp.Pads.Single(p => p.Number == "17");  // right side, bottom
        var p25 = fp.Pads.Single(p => p.Number == "25");  // top side, right
        Assert.True(p1.Center.X < 0 && p1.Center.Y > 0);
        Assert.True(p9.Center.Y < 0 && p9.Center.X < 0);
        Assert.True(p17.Center.X > 0 && p17.Center.Y < 0);
        Assert.True(p25.Center.Y > 0 && p25.Center.X > 0);
        // A bottom-row pad is the left-column pad ROTATED: width and height swap.
        Assert.Equal(p1.Width, p9.Height, 12);
        Assert.Equal(p1.Height, p9.Width, 12);
    }

    [Fact]
    public void Sot23_PutsPins1And2Below_AndPin3AboveOnTheCentreline()
    {
        var fp = Ipc7351.Sot23("SOT-23", StandardBodies.Sot23);
        Assert.Equal(3, fp.Pads.Count);
        var p1 = fp.Pads.Single(p => p.Number == "1");
        var p2 = fp.Pads.Single(p => p.Number == "2");
        var p3 = fp.Pads.Single(p => p.Number == "3");
        Assert.True(p1.Center.X < 0 && p1.Center.Y < 0);   // bottom-left
        Assert.Equal(-p1.Center.X, p2.Center.X);
        Assert.Equal(p1.Center.Y, p2.Center.Y);
        Assert.Equal(0.0, p3.Center.X);
        Assert.Equal(-p1.Center.Y, p3.Center.Y);
        Assert.Equal(1.90, p2.Center.X - p1.Center.X, 12);
    }

    [Fact]
    public void ABga_NumbersItsGrid_TheJedecWay()
    {
        // Row letters skip I, O, Q, S, X, Z; row 21 begins the two-letter range.
        Assert.Equal("A", Ipc7351.BgaRowName(1));
        Assert.Equal("J", Ipc7351.BgaRowName(9));    // I skipped
        Assert.Equal("P", Ipc7351.BgaRowName(14));   // O skipped
        Assert.Equal("R", Ipc7351.BgaRowName(15));   // Q skipped
        Assert.Equal("T", Ipc7351.BgaRowName(16));   // S skipped
        Assert.Equal("Y", Ipc7351.BgaRowName(20));   // X skipped
        Assert.Equal("AA", Ipc7351.BgaRowName(21));
        Assert.Equal("BA", Ipc7351.BgaRowName(41));

        // A 4×4 at 0.8 mm pitch, 0.5 mm balls, Nominal (20% reduction): 0.40 mm round lands,
        // A1 top-LEFT, the grid centred.
        var fp = Ipc7351.Bga("BGA-16", new BgaSpec(4, 4, 0.8, 0.5));
        Assert.Equal(16, fp.Pads.Count);
        var a1 = fp.Pads.Single(p => p.Number == "A1");
        var d4 = fp.Pads.Single(p => p.Number == "D4");
        Assert.Equal(-1.2, a1.Center.X, 12);
        Assert.Equal(1.2, a1.Center.Y, 12);
        Assert.Equal(-a1.Center.X, d4.Center.X, 12);
        Assert.Equal(-a1.Center.Y, d4.Center.Y, 12);
        Assert.All(fp.Pads, p => Assert.Equal(PadShape.Round, p.Shape));
        Assert.All(fp.Pads, p => Assert.Equal(0.40, p.Width, 12));
    }

    [Fact]
    public void AGeneratedFootprint_IsUsable_OnARealBoard()
    {
        // The end-to-end claim: generated lands feed the layout machinery unchanged — every pin
        // covered by exactly one pad, and the bare placed board passes the default DRC (the
        // lands the generator computes CLEAR each other; a closed gap or an oversize pad would
        // fail here rather than at a fab).
        var soic = Ipc7351.DualGullwing("SOIC-8", StandardBodies.SoicNarrow, 8);
        var chip = Ipc7351.Chip("0805", StandardBodies.Chip0805);
        var ic = new PartDefinition("IC", "U",
            soic.Pads.Select(p => new Pin(p.Number, PinType.Passive)), soic);
        var r = new PartDefinition("R", "R",
            chip.Pads.Select(p => new Pin(p.Number, PinType.Passive)), chip);

        var sch = new Schematic("ipc");
        var u1 = sch.Add("U1", ic);
        var r1 = sch.Add("R1", r);
        sch.Connect("SIG", u1.Pin("1"), r1.Pin("1"));

        var layout = new PcbLayout(sch, PcbBoard.Rectangle(30, 20, 1.6));
        layout.Place("U1", -6, 0, 0);
        layout.Place("R1", 6, 0, 90);
        var check = layout.Check();
        Assert.True(check.Ok);
        Assert.Equal(check.PlacedPinCount, check.PinsCoveredByExactlyOnePad);

        var drc = PcbDrc.Check(layout);
        Assert.True(drc.Ok, string.Join("; ", drc.Violations.Select(v => v.Message)));
    }

    [Fact]
    public void TheStandardBodies_AllGenerate_AtEveryDensity()
    {
        // The catalogue is a claim: every published body must generate at every density level
        // (a body that only works at Nominal would be a trap on the shelf).
        foreach (var density in new[] { LandDensity.Least, LandDensity.Nominal, LandDensity.Most })
        {
            var opt = new Ipc7351Options(Density: density);
            Assert.NotEmpty(Ipc7351.Chip("c0603", StandardBodies.Chip0603, opt).Pads);
            Assert.NotEmpty(Ipc7351.Chip("c0805", StandardBodies.Chip0805, opt).Pads);
            Assert.NotEmpty(Ipc7351.Chip("c1206", StandardBodies.Chip1206, opt).Pads);
            Assert.NotEmpty(Ipc7351.DualGullwing("soic8", StandardBodies.SoicNarrow, 8, opt).Pads);
            Assert.NotEmpty(Ipc7351.QuadGullwing("lqfp32", StandardBodies.Lqfp0p8, 8, opt).Pads);
            Assert.NotEmpty(Ipc7351.Sot23("sot23", StandardBodies.Sot23, options: opt).Pads);
            Assert.NotEmpty(Ipc7351.Bga("bga", new BgaSpec(4, 4, 0.8, 0.5), opt).Pads);
        }
    }

    [Fact]
    public void TheRefusals_NameTheirGeometry()
    {
        // A chip below 1608 metric: the small-chip goal row is not transcribed.
        var small = Assert.Throws<ArgumentException>(() =>
            Ipc7351.Chip("0402", new ChipSpec(1.0, 0.5, 0.25)));
        Assert.Contains("small-chip", small.Message);

        // Leads reaching across the whole span: the two pads would be one.
        var overlap = Assert.Throws<ArgumentException>(() =>
            Ipc7351.DualGullwing("x", new GullwingSpec(2.0, 1.27, 1.2, 0.4), 2));
        Assert.Contains("overlap", overlap.Message);

        // A closed inner gap (G ≤ 0) is refused NAMING G, not shipped as merged pads: a short
        // body with long leads at Most density drives the heel fillets into each other.
        var closed = Assert.Throws<ArgumentException>(() =>
            Ipc7351.DualGullwing("tight", new GullwingSpec(2.4, 1.27, 1.0, 0.4), 2,
                new Ipc7351Options(Density: LandDensity.Most)));
        Assert.Contains("gap closes", closed.Message);

        // An inverted dimension range, an odd dual pin count, a bad pitch, a 1-based BGA row.
        Assert.Throws<ArgumentException>(() => new DimRange(2.0, 1.0));
        Assert.Throws<ArgumentException>(() =>
            Ipc7351.DualGullwing("odd", StandardBodies.SoicNarrow, 7));
        Assert.Throws<ArgumentException>(() =>
            Ipc7351.Bga("z", new BgaSpec(2, 2, 0, 0.5)));
        Assert.Throws<ArgumentException>(() => Ipc7351.BgaRowName(0));
    }
}
