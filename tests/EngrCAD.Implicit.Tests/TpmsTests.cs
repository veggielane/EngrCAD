using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Implicit.Tests;

/// <summary>
/// The triply periodic minimal surfaces. Three claims are made about every kind and every one
/// is measured rather than asserted from the algebra it was derived from:
/// <list type="number">
///   <item><description>the field is <b>1-Lipschitz</b> — which is what makes it a lower bound
///   on the distance, and what <c>SurfaceCull</c>, the narrow-band octree and the projection
///   target all read it as. An under-sized gradient constant would break it and drop geometry
///   silently, so the test measures the field's own secants;</description></item>
///   <item><description>the constant is also <b>tight</b> — a bound that is merely large costs
///   wall thickness in direct proportion, so the same measurement asserts the slope reaches 1;</description></item>
///   <item><description>the field is <b>periodic</b> on its stated cell.</description></item>
/// </list>
/// </summary>
public class TpmsTests(ITestOutputHelper output)
{
    public static TheoryData<TpmsKind> Kinds
    {
        get
        {
            var data = new TheoryData<TpmsKind>();
            foreach (TpmsKind kind in Enum.GetValues<TpmsKind>())
                data.Add(kind);
            return data;
        }
    }

    /// <summary>
    /// The gyroid was a hand-written closed form in <c>Primitives.cs</c> before the family
    /// existed; it is now one row of a term table. That refactor is only safe if the value did
    /// not move a bit — <c>lattice.png</c> and the Surface Nets golden fingerprints hang off
    /// it — so the old formula is transcribed here and the two are compared through
    /// <see cref="BitConverter.DoubleToInt64Bits"/>. (This is the same device
    /// <c>SketchRegion</c> uses to hold its ellipse kernel against <c>Curve2d</c>'s: a
    /// transcription compared on the SAME binary, so a future edit fails naming the drift.)
    /// </summary>
    [Fact]
    public void Gyroid_IsBitIdenticalToTheClosedFormItReplaced()
    {
        const double CellSize = 5, Thickness = 1;
        var field = Sdf.Gyroid(CellSize, Thickness);
        double omega = 2 * Math.PI / CellSize;

        var rng = new Random(20260805);
        for (int i = 0; i < 20000; i++)
        {
            var p = new Vector3d(
                (rng.NextDouble() * 2 - 1) * 30,
                (rng.NextDouble() * 2 - 1) * 30,
                (rng.NextDouble() * 2 - 1) * 30);

            double x = p.X * omega, y = p.Y * omega, z = p.Z * omega;
            double g = Math.Sin(x) * Math.Cos(y) + Math.Sin(y) * Math.Cos(z) + Math.Sin(z) * Math.Cos(x);
            double expected = Math.Abs(g) / (Math.Sqrt(3) * omega) - Thickness / 2;

            double actual = field.Evaluate(p);
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));
        }
    }

    /// <summary>
    /// The load-bearing measurement. Dividing by <c>max |grad F|</c> is what makes the field
    /// 1-Lipschitz, so the secant of the SOLID field (whose gradient is exactly
    /// <c>grad F / (bound * omega)</c>) must never exceed 1 — and must reach it, or the
    /// constant is loose and every wall built from it is proportionally too thick.
    /// <para>
    /// Measured over a dense grid on one cell plus a local refinement from the best sample,
    /// which is what stops a coarse grid missing an isolated peak (the I-WP maximum sits at a
    /// single point of its cell).
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void GradientBound_IsSoundAndTight(TpmsKind kind)
    {
        const double CellSize = 1;
        var field = Sdf.TpmsSolid(kind, CellSize);
        double worst = 0;
        Vector3d worstAt = default;

        // The gradient itself, by central differences — not the largest secant over a fixed
        // set of chord directions, which systematically UNDER-reports: 26 directions leave a
        // worst-case angle of about 20 degrees to the true gradient, so such a probe caps out
        // near 0.94 of the slope and could never show a constant tight.
        // The step is short enough to read a local slope and long enough that a difference of
        // two O(1) values keeps twelve digits.
        const double Step = 1e-6;

        void Probe(in Vector3d p)
        {
            double gx = field.Evaluate(p + new Vector3d(Step, 0, 0)) - field.Evaluate(p - new Vector3d(Step, 0, 0));
            double gy = field.Evaluate(p + new Vector3d(0, Step, 0)) - field.Evaluate(p - new Vector3d(0, Step, 0));
            double gz = field.Evaluate(p + new Vector3d(0, 0, Step)) - field.Evaluate(p - new Vector3d(0, 0, Step));
            double slope = new Vector3d(gx, gy, gz).Length / (2 * Step);
            if (slope > worst)
            {
                worst = slope;
                worstAt = p;
            }
        }

        const int Resolution = 48;
        for (int i = 0; i < Resolution; i++)
            for (int j = 0; j < Resolution; j++)
                for (int k = 0; k < Resolution; k++)
                    Probe(new Vector3d(
                        (i + 0.5) / Resolution, (j + 0.5) / Resolution, (k + 0.5) / Resolution));

        // Hill-climb from the best grid sample: the maxima are isolated points, and a grid
        // alone systematically under-reports them.
        var rng = new Random(99);
        double step = 1.0 / Resolution;
        for (int round = 0; round < 120; round++)
        {
            var centre = worstAt;
            double before = worst;
            for (int trial = 0; trial < 200; trial++)
                Probe(centre + new Vector3d(
                    (rng.NextDouble() * 2 - 1) * step,
                    (rng.NextDouble() * 2 - 1) * step,
                    (rng.NextDouble() * 2 - 1) * step));
            if (worst <= before)
                step *= 0.7;
        }

        output.WriteLine($"{kind,-14} bound {Tpms.GradientBound(kind),10:0.######}   " +
                         $"measured slope {worst:0.########} at {worstAt}");

        // Sound: the field never changes faster than the point moves. The slack is round-off
        // in a difference of two O(1) values over a 1e-6 chord, a few parts in 1e-10.
        Assert.True(worst <= 1 + 1e-8,
            $"{kind}: the field is not 1-Lipschitz — measured {worst:R} at {worstAt}. " +
            "The gradient bound is too small and the polygonizer's cull would drop geometry.");
        // Tight: the constant is the supremum, not merely an upper bound.
        Assert.True(worst > 0.99,
            $"{kind}: the gradient bound is loose (measured only {worst:R}), so every wall " +
            "built from it is proportionally thicker than asked.");
    }

    /// <summary>The engine's standing contract, restated for this family: the reported
    /// Lipschitz bound is exactly 1, so nothing downstream widens anything.</summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void Fields_ReportALipschitzBoundOfExactlyOne(TpmsKind kind)
    {
        var region = new Aabb((-20, -20, -20), (20, 20, 20));
        Assert.Equal(1.0, Sdf.TpmsSheet(kind, 6, 1).LipschitzBound(region));
        Assert.Equal(1.0, Sdf.TpmsSolid(kind, 6).LipschitzBound(region));
    }

    /// <summary>Periodicity on the stated cell — the property that makes "cell size" mean
    /// something. Not bit-identical (sin(x + 2*pi) is not sin(x) in doubles), so it is held to
    /// the round-off a 2*pi argument reduction leaves.</summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void Fields_ArePeriodicOnTheCell(TpmsKind kind)
    {
        const double CellSize = 3.5;
        var sheet = Sdf.TpmsSheet(kind, CellSize, 0.4);
        var solid = Sdf.TpmsSolid(kind, CellSize);
        var rng = new Random(4242);
        Vector3d[] steps =
        [
            (CellSize, 0, 0), (0, CellSize, 0), (0, 0, CellSize),
            (2 * CellSize, -CellSize, 3 * CellSize),
        ];

        for (int i = 0; i < 4000; i++)
        {
            var p = new Vector3d(
                (rng.NextDouble() * 2 - 1) * 5,
                (rng.NextDouble() * 2 - 1) * 5,
                (rng.NextDouble() * 2 - 1) * 5);
            foreach (var step in steps)
            {
                // An absolute band rather than Assert.Equal's decimal places, which decides a
                // 3e-16 difference by which side of a rounding boundary the two values fall.
                Assert.True(Math.Abs(sheet.Evaluate(p) - sheet.Evaluate(p + step)) < 1e-11);
                Assert.True(Math.Abs(solid.Evaluate(p) - solid.Evaluate(p + step)) < 1e-11);
            }
        }
    }

    /// <summary>
    /// The 50/50 split at the symmetric level — a real check with a known answer, and the one
    /// that separates the kinds whose polynomial has an antisymmetry from the three whose does
    /// not. Those three are asserted at their MEASURED fractions instead, which is the honest
    /// form: quoting 0.5 for I-WP would be repeating literature the geometry contradicts.
    /// </summary>
    [Theory]
    [InlineData(TpmsKind.SchwarzP, 0.5)]
    [InlineData(TpmsKind.SchwarzD, 0.5)]
    [InlineData(TpmsKind.Gyroid, 0.5)]
    [InlineData(TpmsKind.Neovius, 0.5)]
    [InlineData(TpmsKind.FischerKochS, 0.5)]
    [InlineData(TpmsKind.IwP, 0.469)]
    [InlineData(TpmsKind.Lidinoid, 0.385)]
    [InlineData(TpmsKind.SplitP, 0.510)]
    public void SolidAtLevelZero_EnclosesTheMeasuredFraction(TpmsKind kind, double expected)
    {
        double measured = Tpms.SolidVolumeFraction(kind, 0);
        output.WriteLine($"{kind,-14} fraction(F <= 0) = {measured:0.#####}");
        Assert.Equal(expected, measured, 2);
    }

    /// <summary>
    /// Volume fraction is the parameter an engineer states, so the solve has to land on it.
    /// The achieved value is measured on a grid sharing no sample with the one the parameter
    /// was solved on, so agreement is two measurements agreeing rather than one restating
    /// itself.
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void VolumeFractionSolves_LandOnTheirRequest(TpmsKind kind)
    {
        // The gap between the two grids IS the estimator's honest uncertainty: the parameter
        // is a quantile of a 64-cubed sample and the fraction is counted on a 101-cubed one,
        // so agreement to about a percentage point is what a sampled measure of a surface
        // this dense can support, and the achieved value is the one reported to the caller.
        const double Tolerance = 0.02;
        foreach (double request in new[] { 0.2, 0.4 })
        {
            var sheet = Tpms.SheetForVolumeFraction(kind, 8, request);
            var solid = Tpms.SolidForVolumeFraction(kind, 8, request);
            output.WriteLine(
                $"{kind,-14} f={request}  sheet t={sheet.Parameter,7:0.####} -> {sheet.VolumeFraction:0.####}   " +
                $"solid level={solid.Parameter,8:0.####} -> {solid.VolumeFraction:0.####}");

            Assert.Equal(request, sheet.RequestedVolumeFraction);
            Assert.True(Math.Abs(sheet.VolumeFraction - request) < Tolerance,
                $"{kind}: asked for a sheet at {request}, measured {sheet.VolumeFraction:R}");
            Assert.True(Math.Abs(solid.VolumeFraction - request) < Tolerance,
                $"{kind}: asked for a network at {request}, measured {solid.VolumeFraction:R}");
            Assert.True(sheet.Parameter > 0);
        }
    }

    /// <summary>
    /// The sheet and the solid of one surface are <b>different solids</b>, which is the whole
    /// reason both exist. At the same volume fraction they disagree over a large part of
    /// space — a sheet straddles the surface, a network sits on one side of it — and the
    /// measurement says so rather than a picture.
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void SheetAndSolid_AreDifferentSolidsAtTheSameVolumeFraction(TpmsKind kind)
    {
        var sheet = Tpms.SheetForVolumeFraction(kind, 4, 0.3).Field;
        var solid = Tpms.SolidForVolumeFraction(kind, 4, 0.3).Field;

        var rng = new Random(7);
        int disagree = 0, total = 20000;
        for (int i = 0; i < total; i++)
        {
            var p = new Vector3d(
                (rng.NextDouble() * 2 - 1) * 8,
                (rng.NextDouble() * 2 - 1) * 8,
                (rng.NextDouble() * 2 - 1) * 8);
            if (sheet.Evaluate(p) < 0 != solid.Evaluate(p) < 0)
                disagree++;
        }

        double fraction = (double)disagree / total;
        output.WriteLine($"{kind,-14} sheet and solid disagree over {fraction:0.###} of space");
        Assert.True(fraction > 0.15,
            $"{kind}: the sheet and the network agree over {1 - fraction:0.###} of space — " +
            "one of them is not what it claims to be.");
    }

    /// <summary>
    /// <b>The structural difference between the two variants, measured.</b> A sheet is a wall
    /// SEPARATING the two labyrinths, so its complement falls into two disconnected pieces; a
    /// network IS one labyrinth, so its complement — the other labyrinth — is a single piece.
    /// Counted by a six-connected flood fill over a sampled block rather than by meshing, so
    /// the test needs nothing from Interop and no diagonal can leak through a wall.
    /// <para>
    /// The claim is about the VOID, deliberately. A finite block is not a fair question to ask
    /// of the material: the sheet is one connected surface in infinite space, but a box cut
    /// through it strands the corners where it leaves and re-enters (measured: three pieces
    /// over a 2-cell block), which is a fact about the box rather than about the lattice. The
    /// network's own count IS asserted, since a labyrinth cut by a box stays connected.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(TpmsKind.Gyroid)]
    [InlineData(TpmsKind.SchwarzD)]
    [InlineData(TpmsKind.SchwarzP)]
    public void ASheetSeparatesSpaceInTwo_WhereANetworkDoesNot(TpmsKind kind)
    {
        const double Cell = 1;
        var sheet = Tpms.SheetForVolumeFraction(kind, Cell, 0.35).Field;
        var solid = Tpms.SolidForVolumeFraction(kind, Cell, 0.5).Field;

        var (sheetSolidParts, sheetVoidParts) = CountComponents(sheet, Cell, 2, 40);
        var (solidSolidParts, solidVoidParts) = CountComponents(solid, Cell, 2, 40);
        output.WriteLine($"{kind}: sheet {sheetSolidParts} solid / {sheetVoidParts} void, " +
                         $"network {solidSolidParts} solid / {solidVoidParts} void");

        Assert.Equal(2, sheetVoidParts);
        Assert.Equal(1, solidSolidParts);
        Assert.Equal(1, solidVoidParts);
        Assert.True(sheetSolidParts >= 1);
    }

    /// <summary>Components of the negative and the positive region of a field over a block of
    /// whole cells, six-connected.</summary>
    private static (int Solid, int Void) CountComponents(
        Sdf field, double cellSize, int cells, int samplesPerCell)
    {
        int n = cells * samplesPerCell;
        double step = cellSize / samplesPerCell;
        var inside = new bool[n * n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                for (int k = 0; k < n; k++)
                    inside[(i * n + j) * n + k] = field.Evaluate(
                        new Vector3d(step * (i + 0.5), step * (j + 0.5), step * (k + 0.5))) < 0;

        return (Components(inside, n, true), Components(inside, n, false));
    }

    private static int Components(bool[] inside, int n, bool value)
    {
        var seen = new bool[inside.Length];
        var stack = new Stack<int>();
        int components = 0;
        for (int start = 0; start < inside.Length; start++)
        {
            if (seen[start] || inside[start] != value)
                continue;
            components++;
            stack.Push(start);
            seen[start] = true;
            while (stack.Count > 0)
            {
                int at = stack.Pop();
                int k = at % n, j = (at / n) % n, i = at / (n * n);
                Span<(int, int, int)> neighbours =
                [
                    (i - 1, j, k), (i + 1, j, k), (i, j - 1, k),
                    (i, j + 1, k), (i, j, k - 1), (i, j, k + 1),
                ];
                foreach (var (a, b, c) in neighbours)
                {
                    if (a < 0 || b < 0 || c < 0 || a >= n || b >= n || c >= n)
                        continue;
                    int index = (a * n + b) * n + c;
                    if (seen[index] || inside[index] != value)
                        continue;
                    seen[index] = true;
                    stack.Push(index);
                }
            }
        }
        return components;
    }

    /// <summary>
    /// The compiled form must be bit-identical, and here that is a claim about the term table
    /// being the single source of both spellings rather than about a formula transcribed
    /// twice — so it is worth asserting on every kind.
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void CompiledFields_AreBitIdentical(TpmsKind kind)
    {
        var sheet = Sdf.TpmsSheet(kind, 5, 1);
        var solid = Sdf.TpmsSolid(kind, 5, 0.3);
        var compiledSheet = sheet.Compile();
        var compiledSolid = solid.Compile();

        var rng = new Random(31415);
        for (int i = 0; i < 5000; i++)
        {
            var p = new Vector3d(
                (rng.NextDouble() * 2 - 1) * 20,
                (rng.NextDouble() * 2 - 1) * 20,
                (rng.NextDouble() * 2 - 1) * 20);
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(sheet.Evaluate(p)),
                BitConverter.DoubleToInt64Bits(compiledSheet.Evaluate(p)));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(solid.Evaluate(p)),
                BitConverter.DoubleToInt64Bits(compiledSolid.Evaluate(p)));
        }
    }

    [Fact]
    public void Factories_RefuseNonsense()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Sdf.TpmsSheet(TpmsKind.Gyroid, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Sdf.TpmsSheet(TpmsKind.Gyroid, 5, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Sdf.TpmsSolid(TpmsKind.Gyroid, -5));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Tpms.SheetForVolumeFraction(TpmsKind.Gyroid, 5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Tpms.SolidForVolumeFraction(TpmsKind.Gyroid, 5, 1));
    }

}
