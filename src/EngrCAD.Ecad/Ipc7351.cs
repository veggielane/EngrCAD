using EngrCAD.Core;

namespace EngrCAD.Ecad;

/// <summary>The IPC-7351 land-pattern density level — how much solder fillet the land provides
/// beyond the component terminal. <see cref="Most"/> is the largest land (maximum fillet, rework
/// and reliability), <see cref="Least"/> the smallest (dense portable designs); the members are
/// ordered so that a larger value means a larger land.</summary>
public enum LandDensity
{
    /// <summary>Level C — minimum copper, for the densest designs.</summary>
    Least,

    /// <summary>Level B — the general-purpose default.</summary>
    Nominal,

    /// <summary>Level A — maximum copper, for high-reliability / rework-friendly boards.</summary>
    Most,
}

/// <summary>A toleranced component dimension (mm): the datasheet's min/max pair. An exact figure
/// (a pitch, a nominal span) converts implicitly from a plain <see cref="double"/>.</summary>
public readonly record struct DimRange
{
    /// <summary>The minimum of the dimension (mm).</summary>
    public double Min { get; }

    /// <summary>The maximum of the dimension (mm).</summary>
    public double Max { get; }

    /// <summary>A toleranced dimension. Refuses a non-finite, non-positive or inverted range by
    /// name — a swapped min/max would silently flip every formula's direction.</summary>
    public DimRange(double min, double max)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max) || min <= 0)
            throw new ArgumentException(
                $"A component dimension must be finite and positive; got [{min}, {max}].");
        if (min > max)
            throw new ArgumentException(
                $"A component dimension's minimum must not exceed its maximum; got [{min}, {max}].");
        Min = min;
        Max = max;
    }

    /// <summary>An exact (untoleranced) dimension.</summary>
    public static DimRange Exact(double value) => new(value, value);

    /// <summary>An exact dimension reads as itself.</summary>
    public static implicit operator DimRange(double value) => Exact(value);

    /// <summary>The tolerance band (max − min, mm).</summary>
    public double Tolerance => Max - Min;
}

/// <summary>A two-terminal chip component's body (a rectangular end-cap resistor / capacitor):
/// body length <paramref name="Length"/> (across the terminals), body width
/// <paramref name="Width"/>, and terminal length <paramref name="Terminal"/> (each end cap's
/// reach along the body).</summary>
public sealed record ChipSpec(DimRange Length, DimRange Width, DimRange Terminal);

/// <summary>A gullwing-leaded body (SOIC/SSOP/TSSOP/QFP families): the lead SPAN
/// <paramref name="Span"/> (toe to toe across the body), the lead PITCH (exact, the drawing's
/// basic dimension), the lead foot length <paramref name="LeadLength"/> (toe to heel) and the
/// lead width <paramref name="LeadWidth"/>.</summary>
public sealed record GullwingSpec(DimRange Span, double Pitch, DimRange LeadLength, DimRange LeadWidth);

/// <summary>A ball-grid array: <paramref name="Columns"/> × <paramref name="Rows"/> balls on an
/// exact <paramref name="Pitch"/>, each of <paramref name="BallDiameter"/>.</summary>
public sealed record BgaSpec(int Columns, int Rows, double Pitch, double BallDiameter);

/// <summary>The generator's process assumptions: the density level, the fabrication and placement
/// tolerances the IPC formulas RMS in, and the quantum land dimensions round to. The defaults are
/// the IPC-7351 conventional figures (F = 0.05 mm, P = 0.025 mm, lands to 0.05 mm).</summary>
public sealed record Ipc7351Options(
    LandDensity Density = LandDensity.Nominal,
    double FabricationTolerance = 0.05,
    double PlacementTolerance = 0.025,
    double LandQuantum = 0.05)
{
    /// <summary>The nominal-density defaults.</summary>
    public static readonly Ipc7351Options Default = new();
}

/// <summary>
/// IPC-7351 land-pattern GENERATION — a footprint from a component's own datasheet dimensions,
/// not a file import (the interchange readers' complement). One formula family carries every
/// leaded shape: the outer land extent <c>Zmax = Lmin + 2·J_toe + √(C_L² + F² + P²)</c>, the
/// inner gap <c>Gmin = Smax − 2·J_heel − √(C_S² + F² + P²)</c> (S the heel span
/// <c>L − 2·T</c>, its range taken arithmetically), and the land width
/// <c>Xmax = Wmin + 2·J_side + √(C_W² + F² + P²)</c> — the toe/heel/side FILLET GOALS per
/// <see cref="LandDensity"/> being the ⚠ verify-against-datasheet transcription (nominal IPC-7351B
/// figures, the <c>StandardHoles</c> convention). Z/G/X round to the land quantum and the pads
/// derive EXACTLY from the rounded values, so <c>Z = G + 2·(pad length)</c> is an identity the
/// tests assert rather than a hope; with every tolerance zero the formulas reduce to the bare
/// goals exactly, which is the test that catches a swapped min/max.
///
/// <para>Covered: two-terminal <see cref="Chip"/>s (1608 metric / 0603 imperial and larger — the
/// small-chip goal row is not transcribed, refused by name), <see cref="DualGullwing"/>
/// (SOIC/SSOP/TSSOP numbering: 1..n/2 down the left side, n/2+1..n up the right),
/// <see cref="QuadGullwing"/> (QFP, counter-clockwise from pin 1 at the top of the left side),
/// <see cref="Sot23"/>, and <see cref="Bga"/> (JEDEC row letters skipping I/O/Q/S/X/Z, then
/// AA/AB/…; the land is the ball reduced by the ⚠ nominal collapsing-ball percentage). A land
/// whose inner gap closes (G ≤ 0 — the pads would merge) is refused naming the number; courtyard
/// and silkscreen are not part of a <see cref="Footprint"/> here and derive downstream
/// (<c>PcbSilkscreen</c> builds the courtyard from the pads). Filed by name: the small-chip goal
/// row, QFN/DFN and other no-lead families, MELF, chip arrays, and thermal-pad paste divisions.</para>
/// </summary>
public static class Ipc7351
{
    // ---- fillet-goal tables (⚠ transcribed IPC-7351B nominal figures) --------
    // Indexed [Least, Nominal, Most]; each figure is mm of fillet beyond the terminal.

    private static readonly double[] ChipToe = [0.15, 0.35, 0.55];
    private static readonly double[] ChipHeel = [0.00, 0.00, 0.00];
    private static readonly double[] ChipSide = [-0.05, 0.00, 0.05];

    private static readonly double[] GullwingToe = [0.15, 0.35, 0.55];
    private static readonly double[] GullwingHeel = [0.25, 0.35, 0.45];
    private static readonly double[] GullwingSide = [0.01, 0.03, 0.05];

    // A collapsing BGA ball's land is the ball REDUCED — the ⚠ nominal reduction per density
    // (a larger land for Most, a smaller one for Least).
    private static readonly double[] BgaReduction = [0.25, 0.20, 0.15];

    /// <summary>A two-terminal chip (rectangular end-cap) land pattern. Pads "1" (−x) and "2"
    /// (+x), the body length along x.</summary>
    public static Footprint Chip(string name, ChipSpec spec, Ipc7351Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(spec);
        var opt = options ?? Ipc7351Options.Default;
        if (spec.Length.Max < 1.6)
            throw new ArgumentException(
                $"'{name}': a chip smaller than 1608 metric (0603 imperial) uses IPC-7351's "
                + $"small-chip goal row, which is not transcribed (body length max "
                + $"{spec.Length.Max:0.###} mm < 1.6). Filed.");

        var (z, g, x) = Lands(name, spec.Length, spec.Terminal, spec.Width,
            ChipToe, ChipHeel, ChipSide, opt);
        double len = (z - g) / 2, cx = (z + g) / 4;
        return new Footprint(name,
        [
            new Pad("1", new Vector2d(-cx, 0), len, x, PadShape.RoundedRectangle),
            new Pad("2", new Vector2d(cx, 0), len, x, PadShape.RoundedRectangle),
        ]);
    }

    /// <summary>A dual-row gullwing land pattern (SOIC/SSOP/TSSOP): <paramref name="pinCount"/>
    /// pins, numbered 1..n/2 down the LEFT column (top to bottom) then n/2+1..n up the RIGHT
    /// (bottom to top) — the package numbering. The lead span runs along x, the pin rows along y.</summary>
    public static Footprint DualGullwing(
        string name, GullwingSpec spec, int pinCount, Ipc7351Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(spec);
        var opt = options ?? Ipc7351Options.Default;
        if (pinCount < 2 || pinCount % 2 != 0)
            throw new ArgumentException(
                $"'{name}': a dual gullwing needs an even pin count of at least 2; got {pinCount}.");
        RequirePitch(name, spec.Pitch);

        var (z, g, x) = Lands(name, spec.Span, spec.LeadLength, spec.LeadWidth,
            GullwingToe, GullwingHeel, GullwingSide, opt);
        double len = (z - g) / 2, cx = (z + g) / 4;
        int perSide = pinCount / 2;
        double top = (perSide - 1) / 2.0 * spec.Pitch;

        var pads = new List<Pad>(pinCount);
        for (int i = 0; i < perSide; i++)                        // 1..n/2, top to bottom
            pads.Add(new Pad($"{i + 1}", new Vector2d(-cx, top - i * spec.Pitch),
                len, x, PadShape.RoundedRectangle));
        for (int i = 0; i < perSide; i++)                        // n/2+1..n, bottom to top
            pads.Add(new Pad($"{perSide + i + 1}", new Vector2d(cx, -top + i * spec.Pitch),
                len, x, PadShape.RoundedRectangle));
        return new Footprint(name, pads);
    }

    /// <summary>A quad gullwing land pattern (QFP): <paramref name="pinsPerSide"/> pins on each
    /// of the four sides, numbered counter-clockwise from pin 1 at the TOP of the LEFT side
    /// (left top→bottom, bottom left→right, right bottom→top, top right→left — the package
    /// numbering). One <paramref name="spec"/> serves both directions (a square QFP); the span
    /// applies across x for the left/right columns and across y for the top/bottom rows.</summary>
    public static Footprint QuadGullwing(
        string name, GullwingSpec spec, int pinsPerSide, Ipc7351Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(spec);
        var opt = options ?? Ipc7351Options.Default;
        if (pinsPerSide < 1)
            throw new ArgumentException(
                $"'{name}': a quad gullwing needs at least one pin per side; got {pinsPerSide}.");
        RequirePitch(name, spec.Pitch);

        var (z, g, x) = Lands(name, spec.Span, spec.LeadLength, spec.LeadWidth,
            GullwingToe, GullwingHeel, GullwingSide, opt);
        double len = (z - g) / 2, c = (z + g) / 4;
        double top = (pinsPerSide - 1) / 2.0 * spec.Pitch;

        var pads = new List<Pad>(4 * pinsPerSide);
        for (int i = 0; i < pinsPerSide; i++)                    // left, top -> bottom
            pads.Add(new Pad($"{i + 1}", new Vector2d(-c, top - i * spec.Pitch),
                len, x, PadShape.RoundedRectangle));
        for (int i = 0; i < pinsPerSide; i++)                    // bottom, left -> right
            pads.Add(new Pad($"{pinsPerSide + i + 1}", new Vector2d(-top + i * spec.Pitch, -c),
                x, len, PadShape.RoundedRectangle));
        for (int i = 0; i < pinsPerSide; i++)                    // right, bottom -> top
            pads.Add(new Pad($"{2 * pinsPerSide + i + 1}", new Vector2d(c, -top + i * spec.Pitch),
                len, x, PadShape.RoundedRectangle));
        for (int i = 0; i < pinsPerSide; i++)                    // top, right -> left
            pads.Add(new Pad($"{3 * pinsPerSide + i + 1}", new Vector2d(top - i * spec.Pitch, c),
                x, len, PadShape.RoundedRectangle));
        return new Footprint(name, pads);
    }

    /// <summary>The SOT-23 three-lead land pattern: pins 1 and 2 below (x = ∓pitch/2, so pin 1 is
    /// bottom-LEFT), pin 3 above at x = 0 — the package numbering. The lead span runs along y;
    /// <paramref name="pitch12"/> is the pin 1–2 spacing (1.90 mm on the standard body).</summary>
    public static Footprint Sot23(
        string name, GullwingSpec spec, double pitch12 = 1.90, Ipc7351Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(spec);
        var opt = options ?? Ipc7351Options.Default;
        RequirePitch(name, pitch12);

        var (z, g, x) = Lands(name, spec.Span, spec.LeadLength, spec.LeadWidth,
            GullwingToe, GullwingHeel, GullwingSide, opt);
        double len = (z - g) / 2, cy = (z + g) / 4, half = pitch12 / 2;
        return new Footprint(name,
        [
            new Pad("1", new Vector2d(-half, -cy), x, len, PadShape.RoundedRectangle),
            new Pad("2", new Vector2d(half, -cy), x, len, PadShape.RoundedRectangle),
            new Pad("3", new Vector2d(0, cy), x, len, PadShape.RoundedRectangle),
        ]);
    }

    /// <summary>A ball-grid-array land pattern: round pads on the grid, the land the ball reduced
    /// by the density's ⚠ nominal collapsing-ball percentage, numbered the JEDEC way — row letters
    /// from the TOP (A, B, …, skipping I/O/Q/S/X/Z, then AA, AB, …), columns 1..n from the LEFT,
    /// pad "A1" top-left, the grid centred on the origin.</summary>
    public static Footprint Bga(string name, BgaSpec spec, Ipc7351Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(spec);
        var opt = options ?? Ipc7351Options.Default;
        if (spec.Columns < 1 || spec.Rows < 1)
            throw new ArgumentException(
                $"'{name}': a BGA needs at least one column and one row; got "
                + $"{spec.Columns}×{spec.Rows}.");
        RequirePitch(name, spec.Pitch);
        if (!(spec.BallDiameter > 0))
            throw new ArgumentException(
                $"'{name}': a BGA ball diameter must be positive; got {spec.BallDiameter:0.###}.");

        double land = RoundTo(spec.BallDiameter * (1 - BgaReduction[(int)opt.Density]),
            opt.LandQuantum);
        if (!(land > 0))
            throw new ArgumentException(
                $"'{name}': the reduced BGA land rounds to {land:0.###} mm — the ball "
                + $"({spec.BallDiameter:0.###} mm) is below the land quantum.");

        var pads = new List<Pad>(spec.Columns * spec.Rows);
        for (int r = 0; r < spec.Rows; r++)
            for (int c = 0; c < spec.Columns; c++)
                pads.Add(new Pad(
                    $"{BgaRowName(r + 1)}{c + 1}",
                    new Vector2d(
                        (c - (spec.Columns - 1) / 2.0) * spec.Pitch,
                        ((spec.Rows - 1) / 2.0 - r) * spec.Pitch),
                    land, land, PadShape.Round));
        return new Footprint(name, pads);
    }

    /// <summary>The JEDEC BGA row letter for a 1-based row: A…Y skipping I/O/Q/S/X/Z (20 single
    /// letters), then AA, AB, … over the same alphabet.</summary>
    public static string BgaRowName(int row)
    {
        if (row < 1)
            throw new ArgumentException($"A BGA row is 1-based; got {row}.");
        const string letters = "ABCDEFGHJKLMNPRTUVWY";
        int index = row - 1;
        if (index < letters.Length)
            return letters[index].ToString();
        int prefix = index / letters.Length - 1, rest = index % letters.Length;
        if (prefix >= letters.Length)
            throw new ArgumentException($"BGA row {row} exceeds the two-letter JEDEC range.");
        return $"{letters[prefix]}{letters[rest]}";
    }

    // ---- the one formula family ----------------------------------------------

    /// <summary>The IPC-7351 land triple for one lead span: outer extent Z, inner gap G, land
    /// width X — each rounded to the land quantum, with the guards that make a refusal a message
    /// about the geometry rather than a downstream overlap.</summary>
    private static (double Z, double G, double X) Lands(
        string name, DimRange span, DimRange lead, DimRange width,
        double[] toe, double[] heel, double[] side, Ipc7351Options opt)
    {
        if (2 * lead.Max >= span.Max)
            throw new ArgumentException(
                $"'{name}': the two leads overlap — 2 × lead length max "
                + $"({lead.Max:0.###}) reaches across the span max ({span.Max:0.###}).");
        int d = (int)opt.Density;
        double f = opt.FabricationTolerance, p = opt.PlacementTolerance;

        double z = span.Min + 2 * toe[d] + Rms(span.Tolerance, f, p);
        // The heel span S = L − 2T; its range is taken arithmetically (Smax = Lmax − 2·Tmin),
        // the conservative reading toward fillet.
        double sMax = span.Max - 2 * lead.Min;
        double sMin = span.Min - 2 * lead.Max;
        double g = sMax - 2 * heel[d] - Rms(sMax - sMin, f, p);
        double x = width.Min + 2 * side[d] + Rms(width.Tolerance, f, p);

        z = RoundTo(z, opt.LandQuantum);
        g = RoundTo(g, opt.LandQuantum);
        x = RoundTo(x, opt.LandQuantum);
        if (!(g > 0))
            throw new ArgumentException(
                $"'{name}': the inner land gap closes (G = {g:0.###} mm ≤ 0) — the two pad rows "
                + "would merge across the body. A lower density level or a longer body is needed.");
        if (g >= z)
            throw new ArgumentException(
                $"'{name}': the land degenerates (G = {g:0.###} ≥ Z = {z:0.###}).");
        if (!(x > 0))
            throw new ArgumentException(
                $"'{name}': the land width rounds to {x:0.###} mm ≤ 0.");
        return (z, g, x);
    }

    private static double Rms(double c, double f, double p) => Math.Sqrt(c * c + f * f + p * p);

    private static double RoundTo(double value, double quantum) =>
        quantum > 0 ? Math.Round(value / quantum, MidpointRounding.AwayFromZero) * quantum : value;

    private static void RequirePitch(string name, double pitch)
    {
        if (!(pitch > 0) || !double.IsFinite(pitch))
            throw new ArgumentException($"'{name}': a pitch must be positive; got {pitch:0.###}.");
    }
}

/// <summary>Common component bodies for <see cref="Ipc7351"/> — ⚠ transcribed nominal JEDEC/EIA
/// dimensions, verify against the actual part's datasheet (the <c>StandardHoles</c> convention;
/// real parts vary by vendor, and the datasheet's own min/max is always the better input).</summary>
public static class StandardBodies
{
    /// <summary>0603 imperial / 1608 metric chip: 1.6×0.8 mm body.</summary>
    public static ChipSpec Chip0603 { get; } = new(
        new DimRange(1.45, 1.75), new DimRange(0.65, 0.95), new DimRange(0.20, 0.50));

    /// <summary>0805 imperial / 2012 metric chip: 2.0×1.25 mm body.</summary>
    public static ChipSpec Chip0805 { get; } = new(
        new DimRange(1.90, 2.10), new DimRange(1.15, 1.35), new DimRange(0.35, 0.65));

    /// <summary>1206 imperial / 3216 metric chip: 3.2×1.6 mm body.</summary>
    public static ChipSpec Chip1206 { get; } = new(
        new DimRange(3.00, 3.40), new DimRange(1.40, 1.80), new DimRange(0.35, 0.65));

    /// <summary>The narrow (3.9 mm) SOIC body: 6.0 mm lead span, 1.27 mm pitch — SOIC-8/14/16
    /// share it, differing only in pin count.</summary>
    public static GullwingSpec SoicNarrow { get; } = new(
        new DimRange(5.80, 6.20), 1.27, new DimRange(0.40, 1.27), new DimRange(0.31, 0.51));

    /// <summary>The SOT-23 (TO-236AB) body: 2.1–2.64 mm lead span, pins 1–2 at 1.90 mm.</summary>
    public static GullwingSpec Sot23 { get; } = new(
        new DimRange(2.10, 2.64), 1.90, new DimRange(0.30, 0.55), new DimRange(0.30, 0.51));

    /// <summary>An LQFP body at 0.8 mm pitch, 9.0 mm lead span (both directions) — the common
    /// LQFP-32 outline.</summary>
    public static GullwingSpec Lqfp0p8 { get; } = new(
        new DimRange(8.80, 9.20), 0.80, new DimRange(0.45, 0.75), new DimRange(0.30, 0.45));
}
