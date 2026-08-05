using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Implicit.Tests;

/// <summary>
/// The strut lattices. Their claim is stronger than the TPMS family's — the field is an
/// EXACT distance, not a lower bound — so the test is an EQUALITY rather than a band: the
/// field is compared bit for bit against a brute-force minimum over an explicit block of
/// capsules, which is what proves the folded three-wide neighbourhood is complete.
/// </summary>
public class StrutLatticeTests(ITestOutputHelper output)
{
    public static TheoryData<StrutLatticeKind> Kinds
    {
        get
        {
            var data = new TheoryData<StrutLatticeKind>();
            foreach (StrutLatticeKind kind in Enum.GetValues<StrutLatticeKind>())
                data.Add(kind);
            return data;
        }
    }

    /// <summary>
    /// <b>The test the whole design rests on.</b> The field folds the query point and visits
    /// 27 copies of the unit cell; the oracle instead lays out every strut of a 5x5x5 block
    /// explicitly and takes the minimum over all of them, at the UN-folded point.
    /// <para>
    /// The comparison is to round-off rather than bit for bit, and the reason is worth
    /// keeping: the fold is an isometry mathematically and not arithmetically —
    /// <c>(p - shift) - a</c> and <c>p - (a + shift)</c> are the same real number and
    /// different doubles — so the two paths reach the same distance by different roundings
    /// (measured: never more than a few ulps). That does not weaken what the test is FOR: a
    /// neighbourhood that missed a strut is wrong by a fraction of a cell, not by an ulp, and
    /// the companion below shows the instrument sees exactly that.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void Field_EqualsABruteForceMinimumOverAnExplicitCapsuleBlock(StrutLatticeKind kind)
    {
        const double Cell = 3, Diameter = 0.7;
        var field = Sdf.StrutLattice(kind, Cell, Diameter);
        var explicitStruts = Block(kind, Cell, reach: 2);

        var rng = new Random(20260805);
        double worst = 0;
        for (int t = 0; t < 600; t++)
        {
            // Inside the middle cell, so the explicit block is wide enough to be exhaustive.
            var p = new Vector3d(
                (rng.NextDouble() - 0.5) * Cell,
                (rng.NextDouble() - 0.5) * Cell,
                (rng.NextDouble() - 0.5) * Cell);
            worst = Math.Max(worst, Math.Abs(Nearest(explicitStruts, p) - Diameter / 2 - field.Evaluate(p)));
        }

        output.WriteLine($"{kind,-18} worst deviation from the explicit block: {worst:0.###e+0}");
        Assert.True(worst < 1e-13 * Cell,
            $"{kind}: the folded field differs from an explicit block of capsules by {worst:R} — " +
            "the neighbourhood is missing a strut.");
    }

    /// <summary>
    /// The guard shown to FIRE — and a measured finding that came with it. Dropping every
    /// neighbouring cell and keeping only the query cell's own struts is exactly the naive
    /// fold, and the comparison above must be able to see it.
    /// <para>
    /// <b>It only can for four of the six, and which two it cannot is informative.</b> Simple
    /// cubic and body-centred cubic measure a difference of exactly ZERO: their unit cells are
    /// symmetric about the cell centre and their struts run right through it, so a
    /// neighbouring copy always TIES rather than beating the cell's own struts and the
    /// neighbourhood buys nothing there. The other four need it, for two different reasons —
    /// face-centred cubic and the octet because their cells carry only the three LOW faces'
    /// diagonals (the deduplication that halves the strut count), diamond and Kelvin because
    /// their struts genuinely cross cell boundaries. So the neighbourhood is neither
    /// decoration nor uniformly load-bearing.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(StrutLatticeKind.SimpleCubic, false)]
    [InlineData(StrutLatticeKind.BodyCentredCubic, false)]
    [InlineData(StrutLatticeKind.Kelvin, true)]
    [InlineData(StrutLatticeKind.FaceCentredCubic, true)]
    [InlineData(StrutLatticeKind.Octet, true)]
    [InlineData(StrutLatticeKind.Diamond, true)]
    public void TheComparisonSeesAMissingNeighbour(StrutLatticeKind kind, bool needsNeighbours)
    {
        const double Cell = 3;
        var full = Block(kind, Cell, reach: 2);
        var ownCellOnly = Block(kind, Cell, reach: 0);

        var rng = new Random(20260805);
        double worst = 0;
        for (int t = 0; t < 600; t++)
        {
            var p = new Vector3d(
                (rng.NextDouble() - 0.5) * Cell,
                (rng.NextDouble() - 0.5) * Cell,
                (rng.NextDouble() - 0.5) * Cell);
            double own = Nearest(ownCellOnly, p);
            double all = Nearest(full, p);
            // Dropping candidates can only raise a minimum; the other direction would mean
            // the block is not a superset.
            Assert.True(own >= all - 1e-12);
            worst = Math.Max(worst, own - all);
        }

        output.WriteLine($"{kind,-18} own cell alone is wrong by up to {worst:0.####} ({worst / Cell:0.###} cells)");
        if (needsNeighbours)
            Assert.True(worst > 0.01 * Cell,
                $"{kind}: dropping every neighbour changed the answer by only {worst:R}, so the " +
                "comparison above is not measuring what it claims to.");
        else
            Assert.Equal(0, worst);
    }

    private static List<(Vector3d A, Vector3d B)> Block(StrutLatticeKind kind, double cell, int reach)
    {
        var unitCell = StrutLattices.UnitCell(kind, cell);
        var struts = new List<(Vector3d, Vector3d)>();
        for (int i = -reach; i <= reach; i++)
            for (int j = -reach; j <= reach; j++)
                for (int k = -reach; k <= reach; k++)
                {
                    var shift = new Vector3d(cell * i, cell * j, cell * k);
                    foreach (var (a, b) in unitCell)
                        struts.Add((a + shift, b + shift));
                }
        return struts;
    }

    private static double Nearest(List<(Vector3d A, Vector3d B)> struts, in Vector3d p)
    {
        double best = double.PositiveInfinity;
        foreach (var (a, b) in struts)
            best = Math.Min(best, SegmentDistanceSquared(p, a, b));
        return Math.Sqrt(best);
    }

    /// <summary>
    /// <b>The premise the three-wide neighbourhood rests on, certified rather than assumed.</b>
    /// A copy at lattice index 2 or beyond is at least one whole cell from the folded point,
    /// so the window is complete as soon as the nearest strut it DOES visit is nearer than
    /// that. This measures exactly that quantity — the field's own distance-to-axis, whose
    /// minimum is over the visited window — over a grid on one cell.
    /// <para>
    /// The grid alone would only be evidence, so the bound is certified: a distance function
    /// is 1-Lipschitz, so the true maximum exceeds the grid's by at most half a cell diagonal
    /// of the sampling, and that slack is added before the comparison.
    /// </para>
    /// <para>
    /// The number to watch is face-centred cubic's. Its unit cell carries only the three LOW
    /// faces' diagonals — the deduplication that halves its strut count — so a point at the
    /// far corner is a full cell from the cell's OWN struts and it is the neighbour's copy
    /// that covers it. Measuring the own cell instead would read 0.99 and be measuring the
    /// wrong premise.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void TheVisitedNeighbourhoodCoversTheQueryPoint(StrutLatticeKind kind)
    {
        const double Cell = 1;
        // A diameter of zero would be refused, so ask for a tiny one and add its radius back.
        const double Probe = 1e-9;
        var axes = Sdf.StrutLattice(kind, Cell, Probe);

        double worst = 0;
        const int Resolution = 60;
        for (int i = 0; i < Resolution; i++)
            for (int j = 0; j < Resolution; j++)
                for (int k = 0; k < Resolution; k++)
                    worst = Math.Max(worst, axes.Evaluate(new Vector3d(
                        -0.5 + (i + 0.5) / Resolution,
                        -0.5 + (j + 0.5) / Resolution,
                        -0.5 + (k + 0.5) / Resolution)) + Probe / 2);

        double certified = worst + Math.Sqrt(3) / (2 * Resolution);
        output.WriteLine($"{kind,-18} {StrutLattices.UnitCell(kind, Cell).Count,2} struts, " +
                         $"covering radius {worst:0.####} cells (certified below {certified:0.####})");
        Assert.True(certified < 1,
            $"{kind}: a point in the cell can be {certified:R} cells from every strut the field " +
            "visits, so the three-wide neighbourhood is not provably complete.");
    }

    /// <summary>The struts each kind is made of, pinned so a generated set (Kelvin's, above
    /// all) cannot silently change count.</summary>
    [Theory]
    [InlineData(StrutLatticeKind.SimpleCubic, 3)]
    [InlineData(StrutLatticeKind.BodyCentredCubic, 4)]
    [InlineData(StrutLatticeKind.FaceCentredCubic, 6)]
    [InlineData(StrutLatticeKind.Octet, 18)]
    [InlineData(StrutLatticeKind.Diamond, 16)]
    [InlineData(StrutLatticeKind.Kelvin, 24)]
    public void UnitCells_HaveTheExpectedStrutCount(StrutLatticeKind kind, int expected) =>
        Assert.Equal(expected, StrutLattices.UnitCell(kind, 2).Count);

    /// <summary>
    /// The strut lengths, in cells — the cheapest check that a generated vertex set was paired
    /// up correctly (Kelvin's 36 edges are all equal, and a wrongly paired vertex set would
    /// not be). Two of the kinds carry <em>merged</em> struts: a face diagonal is two
    /// corner-to-face-centre struts end to end, so representing it as one segment is the same
    /// SET of material for half the segment distances per query, and the octet then reports
    /// two lengths — its merged diagonals and its octahedron edges, which are half as long.
    /// </summary>
    [Theory]
    [InlineData(StrutLatticeKind.SimpleCubic, 1.0, 1.0)]
    [InlineData(StrutLatticeKind.BodyCentredCubic, 1.7320508075688772, 1.7320508075688772)]
    [InlineData(StrutLatticeKind.FaceCentredCubic, 1.4142135623730951, 1.4142135623730951)]
    [InlineData(StrutLatticeKind.Octet, 0.7071067811865476, 1.4142135623730951)]
    [InlineData(StrutLatticeKind.Diamond, 0.4330127018922193, 0.4330127018922193)]
    [InlineData(StrutLatticeKind.Kelvin, 0.3535533905932738, 0.3535533905932738)]
    public void UnitCells_HaveTheExpectedStrutLengths(
        StrutLatticeKind kind, double shortest, double longest)
    {
        const double Cell = 4;
        var lengths = StrutLattices.UnitCell(kind, Cell)
            .Select(s => (s.B - s.A).Length / Cell).ToArray();
        output.WriteLine($"{kind,-18} {lengths.Min():0.######} .. {lengths.Max():0.######} cells");
        Assert.Equal(shortest, lengths.Min(), 9);
        Assert.Equal(longest, lengths.Max(), 9);
    }

    /// <summary>The exactness claim, in the form every consumer reads: the reported Lipschitz
    /// bound is 1, and the field really does not change faster than that.</summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void Fields_AreExactDistanceFields(StrutLatticeKind kind)
    {
        var field = Sdf.StrutLattice(kind, 4, 1);
        var region = new Aabb((-12, -12, -12), (12, 12, 12));
        Assert.Equal(1.0, field.LipschitzBound(region));

        var rng = new Random(11);
        double worst = 0;
        for (int i = 0; i < 40000; i++)
        {
            var p = new Vector3d(
                (rng.NextDouble() * 2 - 1) * 10,
                (rng.NextDouble() * 2 - 1) * 10,
                (rng.NextDouble() * 2 - 1) * 10);
            var step = new Vector3d(
                rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5)
                .Normalized() * 1e-6;
            worst = Math.Max(worst, Math.Abs(field.Evaluate(p + step) - field.Evaluate(p)) / step.Length);
        }
        Assert.True(worst <= 1 + 1e-6, $"{kind}: measured slope {worst:R}");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Fields_ArePeriodicOnTheCell(StrutLatticeKind kind)
    {
        const double Cell = 2.5;
        var field = Sdf.StrutLattice(kind, Cell, 0.5);
        var rng = new Random(5150);
        for (int i = 0; i < 3000; i++)
        {
            var p = new Vector3d(
                (rng.NextDouble() * 2 - 1) * 4,
                (rng.NextDouble() * 2 - 1) * 4,
                (rng.NextDouble() * 2 - 1) * 4);
            foreach (var step in new Vector3d[]
                     { (Cell, 0, 0), (0, Cell, 0), (0, 0, Cell), (-2 * Cell, Cell, 3 * Cell) })
                Assert.True(Math.Abs(field.Evaluate(p) - field.Evaluate(p + step)) < 1e-12);
        }
    }

    /// <summary>
    /// The diameter means what it says, which is the contrast with the TPMS sheet's thickness:
    /// a point exactly one radius from a strut axis reads exactly zero, and the field's own
    /// value at a probe is the true distance to the axis less the radius.
    /// </summary>
    [Fact]
    public void StrutDiameter_IsTheActualDiameter()
    {
        const double Cell = 6, Diameter = 1.4;
        var field = Sdf.StrutLattice(StrutLatticeKind.SimpleCubic, Cell, Diameter);
        // The simple-cubic cell has a strut along EACH axis through the origin, so probe a
        // quarter of the way along the x strut, where it is the nearest of the three, and
        // move perpendicular to it.
        for (double r = 0.1; r < 1.4; r += 0.1)
            Assert.Equal(r - Diameter / 2, field.Evaluate(new Vector3d(1.5, r, 0)), 12);
    }

    /// <summary>
    /// Volume fraction, the parameter an engineer states. The diameter is solved as a quantile
    /// over one sampled cell and the fraction re-measured on a grid sharing no sample with it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void VolumeFractionSolves_LandOnTheirRequest(StrutLatticeKind kind)
    {
        foreach (double request in new[] { 0.1, 0.25 })
        {
            var fit = StrutLattices.ForVolumeFraction(kind, 5, request);
            output.WriteLine($"{kind,-18} f={request}  diameter {fit.Parameter:0.####} -> {fit.VolumeFraction:0.####}");
            Assert.Equal(request, fit.RequestedVolumeFraction);
            Assert.True(Math.Abs(fit.VolumeFraction - request) < 0.02,
                $"{kind}: asked for {request}, measured {fit.VolumeFraction:R}");
            Assert.True(fit.Parameter > 0);
        }
    }

    /// <summary>A thicker strut cannot enclose less material — the monotonicity the quantile
    /// solve depends on.</summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void VolumeFraction_IsMonotoneInTheDiameter(StrutLatticeKind kind)
    {
        double previous = -1;
        for (double d = 0.2; d <= 2.0; d += 0.2)
        {
            double fraction = StrutLattices.VolumeFraction(kind, 5, d);
            Assert.True(fraction >= previous, $"{kind}: fraction fell from {previous} to {fraction} at d = {d}");
            previous = fraction;
        }
    }

    [Fact]
    public void Factories_RefuseNonsense()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Sdf.StrutLattice(StrutLatticeKind.Octet, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Sdf.StrutLattice(StrutLatticeKind.Octet, 5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StrutLattices.ForVolumeFraction(StrutLatticeKind.Octet, 5, 1.5));
    }

    private static double SegmentDistanceSquared(in Vector3d p, in Vector3d a, in Vector3d b)
    {
        var pa = p - a;
        var ba = b - a;
        double h = Math.Clamp(pa.Dot(ba) / ba.LengthSquared, 0, 1);
        return (pa - ba * h).LengthSquared;
    }
}
