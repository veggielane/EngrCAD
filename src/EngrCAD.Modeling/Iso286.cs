namespace EngrCAD.Modeling;

/// <summary>Whether a fit always clears, always binds, or can do either.</summary>
public enum FitKind
{
    /// <summary>The shaft always clears the hole (minimum clearance ≥ 0).</summary>
    Clearance,

    /// <summary>Assembly may clear or bind depending on where each part lands in its
    /// tolerance — a locational fit.</summary>
    Transition,

    /// <summary>The shaft is always larger than the hole (maximum clearance &lt; 0) —
    /// a press fit.</summary>
    Interference,
}

/// <summary>
/// One toleranced limit pair as SIGNED deviations from the nominal size, in model
/// millimetres: a Ø40 H7 bore is <c>Upper = +0.025, Lower = 0</c>, a Ø40 g6 shaft
/// <c>Upper = −0.009, Lower = −0.025</c>.
/// </summary>
public readonly record struct FitLimits(double Upper, double Lower)
{
    /// <summary>The tolerance band's width (always positive).</summary>
    public double Tolerance => Upper - Lower;

    /// <summary>The largest permitted size for nominal <paramref name="nominal"/>.</summary>
    public double MaxSize(double nominal) => nominal + Upper;

    /// <summary>The smallest permitted size.</summary>
    public double MinSize(double nominal) => nominal + Lower;
}

/// <summary>A hole/shaft pairing at one nominal size — see <see cref="Iso286.Fit"/>.</summary>
public sealed class IsoFit
{
    internal IsoFit(string designation, double nominal, FitLimits hole, FitLimits shaft)
    {
        Designation = designation;
        Nominal = nominal;
        Hole = hole;
        Shaft = shaft;
    }

    /// <summary>The fit's name, e.g. "H7/g6".</summary>
    public string Designation { get; }

    /// <summary>The nominal size (mm) the limits were resolved at.</summary>
    public double Nominal { get; }

    /// <summary>The hole's limits (signed deviations, mm).</summary>
    public FitLimits Hole { get; }

    /// <summary>The shaft's limits (signed deviations, mm).</summary>
    public FitLimits Shaft { get; }

    /// <summary>The largest gap the pair can assemble with: largest hole against
    /// smallest shaft. Negative would mean the pair NEVER clears.</summary>
    public double MaxClearance => Hole.Upper - Shaft.Lower;

    /// <summary>The smallest gap: smallest hole against largest shaft. Negative =
    /// possible interference.</summary>
    public double MinClearance => Hole.Lower - Shaft.Upper;

    /// <summary>Clearance, transition or interference — decided by the two clearance
    /// extremes, never by the letter (the letter is how the extremes were built).</summary>
    public FitKind Kind =>
        MinClearance >= 0 ? FitKind.Clearance
        : MaxClearance < 0 ? FitKind.Interference
        : FitKind.Transition;

    /// <inheritdoc/>
    public override string ToString() =>
        $"{Designation} @ Ø{Nominal:G4}: hole {Hole.Lower * 1000:+0;-0;0}/{Hole.Upper * 1000:+0;-0;0} µm, " +
        $"shaft {Shaft.Lower * 1000:+0;-0;0}/{Shaft.Upper * 1000:+0;-0;0} µm, " +
        $"{Kind} ({MinClearance * 1000:+0;-0;0}…{MaxClearance * 1000:+0;-0;0} µm)";
}

/// <summary>
/// ISO 286-1 limits and fits: standard tolerance grades (IT5–IT12) and the
/// fundamental deviations of the shaft and hole letters, for sizes over 1 up to
/// 500 mm — hole basis (H7/g6) and shaft basis (G7/h6) alike.
///
/// <para>⚠ The grade and deviation tables are TRANSCRIBED from the standard's own
/// published tables (the <c>StandardHoles</c> convention — verify against the standard
/// before relying on a row; the tests assert the rows in the standard's own
/// micrometres, the form a human checks). The tables are stored in µm — the datasheet
/// form — and converted to model millimetres at the API, so a transcription error is
/// visible in the stored constant rather than hidden behind a conversion.</para>
///
/// <para><b>Scope, stated</b>: shaft letters a through h (a–c on their own finer
/// SUB-RANGE table — c changes at 40 inside the 30–50 step), js, k, m, n, p and
/// r through z (t, v and y are honestly ABSENT below 24, 14 and 18 mm — the
/// standard's own empty cells, refused naming the size, never interpolated), plus
/// the holes of BOTH systems: the basic hole H, JS, and the shaft-basis holes A–G
/// by the standard's own mirror rule (EI = −es of the same-letter shaft, no
/// correction at any grade — which is why G7/h6 carries exactly H7/g6's
/// clearances: the IT widths commute). Holes J and K–ZC are refused by name:
/// K, M and N (grades ≤ IT8) and P–ZC (grades ≤ IT7) carry the
/// Δ = IT(n) − IT(n−1) correction, which has tabulated exceptions (M6 over
/// 250–315 is −9, not the derived −11) and an IT3/IT4 dependence the grade table
/// does not have — the hole-basis spelling of the same fit (H7/s6 for S7/h6) is
/// supported. The intermediate letters cd, ef, fg, j and the extreme za, zb, zc
/// likewise refuse by name, as do sizes at or below 1 mm and above 500 mm.</para>
/// </summary>
public static class Iso286
{
    // Size ranges: over Lower, up to and including Upper (mm) — the standard's 13 main
    // steps for 1 < D ≤ 500.
    private static readonly (double Over, double UpTo)[] Ranges =
    [
        (1, 3), (3, 6), (6, 10), (10, 18), (18, 30), (30, 50), (50, 80),
        (80, 120), (120, 180), (180, 250), (250, 315), (315, 400), (400, 500),
    ];

    // ⚠ ISO 286-1 standard tolerance grades, µm, one row per grade over the 13 ranges.
    private static readonly Dictionary<int, double[]> Grades = new()
    {
        [5] = [4, 5, 6, 8, 9, 11, 13, 15, 18, 20, 23, 25, 27],
        [6] = [6, 8, 9, 11, 13, 16, 19, 22, 25, 29, 32, 36, 40],
        [7] = [10, 12, 15, 18, 21, 25, 30, 35, 40, 46, 52, 57, 63],
        [8] = [14, 18, 22, 27, 33, 39, 46, 54, 63, 72, 81, 89, 97],
        [9] = [25, 30, 36, 43, 52, 62, 74, 87, 100, 115, 130, 140, 155],
        [10] = [40, 48, 58, 70, 84, 100, 120, 140, 160, 185, 210, 230, 250],
        [11] = [60, 75, 90, 110, 130, 160, 190, 220, 250, 290, 320, 360, 400],
        [12] = [100, 120, 150, 180, 210, 250, 300, 350, 400, 460, 520, 570, 630],
    };

    // ⚠ Fundamental deviations, µm. For d…h the value is the UPPER deviation (es,
    // negative or zero); for k…p the LOWER deviation (ei, positive). js is ±IT/2 and
    // carries no row; k's row applies to grades 4–7 only (0 otherwise, per the
    // standard), and its 1–3 mm cell is 0 in Table 5 — the +0.6·∛D formula rounds
    // away at that size (the published k6 chart row at 3 mm is +6/0).
    private static readonly Dictionary<char, double[]> ShaftDeviations = new()
    {
        ['d'] = [-20, -30, -40, -50, -65, -80, -100, -120, -145, -170, -190, -210, -230],
        ['e'] = [-14, -20, -25, -32, -40, -50, -60, -72, -85, -100, -110, -125, -135],
        ['f'] = [-6, -10, -13, -16, -20, -25, -30, -36, -43, -50, -56, -62, -68],
        ['g'] = [-2, -4, -5, -6, -7, -9, -10, -12, -14, -15, -17, -18, -20],
        ['h'] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        ['k'] = [0, 1, 1, 1, 2, 2, 2, 3, 3, 4, 4, 4, 5],
        ['m'] = [2, 4, 6, 7, 8, 9, 11, 13, 15, 17, 20, 21, 23],
        ['n'] = [4, 8, 10, 12, 15, 17, 20, 23, 27, 31, 34, 37, 40],
        ['p'] = [6, 12, 15, 18, 22, 26, 32, 37, 43, 50, 56, 62, 68],
    };

    // The standard's FINER size steps: letters a–c and r–z split their fundamental
    // deviations at sub-range boundaries the 13 main steps do not have (c changes at
    // 40 inside the 30–50 step, u at 40, r at 65 inside 50–80, …) — 25 rows over
    // 1 < D ≤ 500, same over/up-to-and-including convention as the main table.
    private static readonly (double Over, double UpTo)[] SubRanges =
    [
        (1, 3), (3, 6), (6, 10), (10, 14), (14, 18), (18, 24), (24, 30),
        (30, 40), (40, 50), (50, 65), (65, 80), (80, 100), (100, 120),
        (120, 140), (140, 160), (160, 180), (180, 200), (200, 225), (225, 250),
        (250, 280), (280, 315), (315, 355), (355, 400), (400, 450), (450, 500),
    ];

    // ⚠ ISO 286-1:2010 Tables 4/5, µm, one row per letter over the 25 sub-ranges.
    // For a…c the value is the UPPER deviation (es, negative); for r…z the LOWER
    // deviation (ei, positive). NaN is the standard's own EMPTY cell — t below 24,
    // v below 14 and y below 18 mm have no fundamental deviation and are refused
    // naming the size, never interpolated.
    private static readonly Dictionary<char, double[]> SplitShaftDeviations = new()
    {
        ['a'] = [-270, -270, -280, -290, -290, -300, -300, -310, -320, -340, -360,
                 -380, -410, -460, -520, -580, -660, -740, -820, -920, -1050,
                 -1200, -1350, -1500, -1650],
        ['b'] = [-140, -140, -150, -150, -150, -160, -160, -170, -180, -190, -200,
                 -220, -240, -260, -280, -310, -340, -380, -420, -480, -540,
                 -600, -680, -760, -840],
        ['c'] = [-60, -70, -80, -95, -95, -110, -110, -120, -130, -140, -150,
                 -170, -180, -200, -210, -230, -240, -260, -280, -300, -330,
                 -360, -400, -440, -480],
        ['r'] = [10, 15, 19, 23, 23, 28, 28, 34, 34, 41, 43,
                 51, 54, 63, 65, 68, 77, 80, 84, 94, 98,
                 108, 114, 126, 132],
        ['s'] = [14, 19, 23, 28, 28, 35, 35, 43, 43, 53, 59,
                 71, 79, 92, 100, 108, 122, 130, 140, 158, 170,
                 190, 208, 232, 252],
        ['t'] = [double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
                 double.NaN, 41, 48, 54, 66, 75,
                 91, 104, 122, 134, 146, 166, 180, 196, 218, 240,
                 268, 294, 330, 360],
        ['u'] = [18, 23, 28, 33, 33, 41, 48, 60, 70, 87, 102,
                 124, 144, 170, 190, 210, 236, 258, 284, 315, 350,
                 390, 435, 490, 540],
        ['v'] = [double.NaN, double.NaN, double.NaN, double.NaN, 39,
                 47, 55, 68, 81, 102, 120,
                 146, 172, 202, 228, 252, 284, 310, 340, 385, 425,
                 475, 530, 595, 660],
        ['x'] = [20, 28, 34, 40, 45, 54, 64, 80, 97, 122, 146,
                 178, 210, 248, 280, 310, 350, 385, 425, 475, 525,
                 590, 660, 740, 820],
        ['y'] = [double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
                 63, 75, 94, 114, 144, 174,
                 214, 254, 300, 340, 380, 425, 470, 520, 580, 650,
                 730, 820, 920, 1000],
        ['z'] = [26, 35, 42, 50, 60, 73, 88, 112, 136, 172, 210,
                 258, 310, 365, 415, 465, 520, 575, 640, 710, 790,
                 900, 1000, 1100, 1250],
    };

    // Two-letter families that would otherwise be misread as their first letter by
    // the grade parse — refused by name instead (cd/ef/fg are the fine-mechanism
    // intermediates, za/zb/zc the extreme interference band).
    private static readonly string[] IntermediateFamilies =
        ["cd", "ef", "fg", "za", "zb", "zc", "CD", "EF", "FG", "ZA", "ZB", "ZC"];

    /// <summary>
    /// The limits for one designation at a nominal size: <c>Limits(40, "H7")</c> is
    /// +0.025/0, <c>Limits(40, "g6")</c> −0.009/−0.025. An UPPERCASE letter is a hole
    /// (A–H and JS — the shaft-basis holes mirror the same-letter shaft, EI = −es),
    /// lowercase a shaft — the ISO convention, so "H7" and "h7" are different things
    /// and both work.
    /// </summary>
    public static FitLimits Limits(double size, string designation)
    {
        ArgumentNullException.ThrowIfNull(designation);
        if (designation.Length < 2)
            throw new ArgumentException(
                $"'{designation}' is not a fit designation (a letter then a grade, like H7 or g6).",
                nameof(designation));
        foreach (var family in IntermediateFamilies)
        {
            if (designation.StartsWith(family, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"'{designation}': the intermediate letters cd, ef, fg (fine mechanisms) and " +
                    "the extreme-interference za, zb, zc are not transcribed — refused by name " +
                    "rather than misread as their first letter.",
                    nameof(designation));
        }
        char letter = designation[0];
        bool symmetric = designation.StartsWith("js", StringComparison.Ordinal)
            || designation.StartsWith("JS", StringComparison.Ordinal);
        if (!int.TryParse(designation[(symmetric ? 2 : 1)..], out int grade))
        {
            throw new ArgumentException(
                $"'{designation}' has no numeric grade after " +
                $"'{(symmetric ? designation[..2] : letter.ToString())}'.",
                nameof(designation));
        }
        double it = GradeMicrons(size, grade) / 1000;

        if (symmetric)
            return new FitLimits(it / 2, -it / 2); // js and JS: ±IT/2 by definition

        if (char.IsUpper(letter))
        {
            if (letter == 'H')
                return new FitLimits(it, 0);
            if (letter is >= 'A' and <= 'G')
            {
                // The shaft-basis holes: ISO 286-1's own mirror rule — for A through H
                // the hole's lower deviation is EI = −es of the same-letter shaft, with
                // NO correction at any grade. That identity is why G7/h6 carries exactly
                // H7/g6's clearances (the IT widths commute across the sum).
                double ei = -ShaftUpperMicrons(size, char.ToLowerInvariant(letter)) / 1000;
                return new FitLimits(ei + it, ei);
            }
            if (letter is 'J' or 'K' or 'M' or 'N' or 'P' or 'R' or 'S' or 'T'
                or 'U' or 'V' or 'X' or 'Y' or 'Z')
            {
                throw new ArgumentException(
                    $"Hole '{designation}': only A–H and JS holes are in the table. J carries " +
                    "per-grade special values, and K–ZC carry the delta = IT(n) − IT(n−1) " +
                    "correction for fine grades (K/M/N up to IT8, P–ZC up to IT7) with tabulated " +
                    "exceptions (M6 over 250–315 is −9, not the derived −11) and an IT3/IT4 " +
                    "dependence the grade table does not have — refused rather than " +
                    "half-transcribed. The hole-basis spelling of the same fit is supported " +
                    "(H7/s6 for S7/h6).",
                    nameof(designation));
            }
            throw new ArgumentException(
                $"Hole letter '{letter}' is not an ISO 286 fundamental deviation.",
                nameof(designation));
        }
        if (ShaftDeviations.TryGetValue(letter, out var row))
        {
            double deviation = row[RangeIndex(size)] / 1000;
            if (letter == 'k' && grade is < 4 or > 7)
                deviation = 0; // the standard: k's +0.6·∛D row applies to grades 4–7 only
            return letter is 'd' or 'e' or 'f' or 'g' or 'h'
                ? new FitLimits(deviation, deviation - it)   // es given, ei = es − IT
                : new FitLimits(deviation + it, deviation);  // ei given, es = ei + IT
        }
        if (SplitShaftDeviations.TryGetValue(letter, out var split))
        {
            double micron = split[SubRangeIndex(size)];
            if (double.IsNaN(micron)) // the standard's own empty cell, never interpolated
            {
                throw new ArgumentException(
                    $"Shaft letter '{letter}' has no fundamental deviation at {size} mm — " +
                    $"ISO 286-1 defines {letter} only above {FirstDefinedAbove(split)} mm " +
                    "(the table's own empty cell, refused rather than interpolated).",
                    nameof(designation));
            }
            double deviation = micron / 1000;
            return letter is 'a' or 'b' or 'c'
                ? new FitLimits(deviation, deviation - it)   // es given, ei = es − IT
                : new FitLimits(deviation + it, deviation);  // ei given, es = ei + IT
        }
        if (letter == 'j')
        {
            throw new ArgumentException(
                "Shaft 'j' carries per-grade special values (an asymmetric js) the table does " +
                "not transcribe — use js, k or h.",
                nameof(designation));
        }
        throw new ArgumentException(
            $"Shaft letter '{letter}' is not in the table (a–h, js, k, m, n, p, r–z; " +
            "cd, ef, fg, j and za–zc refuse by name).",
            nameof(designation));
    }

    /// <summary>The upper deviation es of a shaft letter a…h in µm — the quantity the
    /// shaft-basis holes A–H mirror (EI = −es).</summary>
    private static double ShaftUpperMicrons(double size, char letter) =>
        SplitShaftDeviations.TryGetValue(letter, out var split)
            ? split[SubRangeIndex(size)]           // a, b, c — no empty cells over 1–500
            : ShaftDeviations[letter][RangeIndex(size)]; // d…g (h is the basic hole H)

    private static double FirstDefinedAbove(double[] split)
    {
        for (int i = 0; i < split.Length; i++)
        {
            if (!double.IsNaN(split[i]))
                return SubRanges[i].Over;
        }
        throw new InvalidOperationException("A split row with no defined cell cannot exist.");
    }

    /// <summary>The standard tolerance for a grade at a size, in MILLIMETRES
    /// (`IT7` at Ø40 is 0.025).</summary>
    public static double GradeTolerance(double size, int grade) => GradeMicrons(size, grade) / 1000;

    /// <summary>A hole-basis fit: <c>Fit(40, "H7", "g6")</c>. The kind and the
    /// clearance extremes are DERIVED from the two limit pairs, never looked up.</summary>
    public static IsoFit Fit(double size, string hole, string shaft)
    {
        ArgumentNullException.ThrowIfNull(hole);
        ArgumentNullException.ThrowIfNull(shaft);
        if (hole.Length > 0 && !char.IsUpper(hole[0]))
            throw new ArgumentException(
                $"'{hole}' is not a hole designation (holes are uppercase: H7, not h7).", nameof(hole));
        if (shaft.Length > 0 && char.IsUpper(shaft[0]))
            throw new ArgumentException(
                $"'{shaft}' is not a shaft designation (shafts are lowercase: g6, not G6).", nameof(shaft));
        return new IsoFit($"{hole}/{shaft}", size, Limits(size, hole), Limits(size, shaft));
    }

    private static double GradeMicrons(double size, int grade)
    {
        if (!Grades.TryGetValue(grade, out var row))
        {
            throw new ArgumentOutOfRangeException(nameof(grade),
                $"IT{grade} is not in the v1 table (IT5–IT12).");
        }
        return row[RangeIndex(size)];
    }

    private static int RangeIndex(double size)
    {
        if (!double.IsFinite(size) || size <= Ranges[0].Over || size > Ranges[^1].UpTo)
        {
            throw new ArgumentOutOfRangeException(nameof(size),
                $"Size {size} mm is outside the table (over 1 up to 500 mm; smaller and " +
                "larger sizes have their own tables in the standard).");
        }
        for (int i = 0; i < Ranges.Length; i++)
        {
            if (size <= Ranges[i].UpTo)
                return i;
        }
        throw new ArgumentOutOfRangeException(nameof(size)); // unreachable
    }

    private static int SubRangeIndex(double size)
    {
        // The finer table covers exactly the same 1 < D ≤ 500 span, so the SAME refusal
        // (asked through RangeIndex) covers both — one message, one boundary.
        RangeIndex(size);
        for (int i = 0; i < SubRanges.Length; i++)
        {
            if (size <= SubRanges[i].UpTo)
                return i;
        }
        throw new ArgumentOutOfRangeException(nameof(size)); // unreachable
    }
}
