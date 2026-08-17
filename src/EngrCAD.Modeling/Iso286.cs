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
/// fundamental deviations of the common shaft letters, for sizes over 1 up to 500 mm.
///
/// <para>⚠ The grade and deviation tables are TRANSCRIBED from the standard's own
/// published tables (the <c>StandardHoles</c> convention — verify against the standard
/// before relying on a row; the tests assert the rows in the standard's own
/// micrometres, the form a human checks). The tables are stored in µm — the datasheet
/// form — and converted to model millimetres at the API, so a transcription error is
/// visible in the stored constant rather than hidden behind a conversion.</para>
///
/// <para><b>Scope, stated</b>: shaft letters d, e, f, g, h, js, k, m, n, p and the
/// basic hole H — the hole-basis system ISO 286 itself prefers, covering the preferred
/// fits H7/g6, H7/h6, H8/f7, H9/d9, H7/k6, H7/n6 and H7/p6. Letters a, b and c (large
/// clearances) and r through z (heavier interference) split their deviations at
/// sub-range boundaries the d–p table does not have, so they are refused BY NAME
/// rather than approximated; holes other than H (the shaft-basis system) likewise.
/// Sizes at or below 1 mm and above 500 mm have their own tables in the standard and
/// are refused by name.</para>
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
    // standard).
    private static readonly Dictionary<char, double[]> ShaftDeviations = new()
    {
        ['d'] = [-20, -30, -40, -50, -65, -80, -100, -120, -145, -170, -190, -210, -230],
        ['e'] = [-14, -20, -25, -32, -40, -50, -60, -72, -85, -100, -110, -125, -135],
        ['f'] = [-6, -10, -13, -16, -20, -25, -30, -36, -43, -50, -56, -62, -68],
        ['g'] = [-2, -4, -5, -6, -7, -9, -10, -12, -14, -15, -17, -18, -20],
        ['h'] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        ['k'] = [1, 1, 1, 1, 2, 2, 2, 3, 3, 4, 4, 4, 5],
        ['m'] = [2, 4, 6, 7, 8, 9, 11, 13, 15, 17, 20, 21, 23],
        ['n'] = [4, 8, 10, 12, 15, 17, 20, 23, 27, 31, 34, 37, 40],
        ['p'] = [6, 12, 15, 18, 22, 26, 32, 37, 43, 50, 56, 62, 68],
    };

    /// <summary>
    /// The limits for one designation at a nominal size: <c>Limits(40, "H7")</c> is
    /// +0.025/0, <c>Limits(40, "g6")</c> −0.009/−0.025. An UPPERCASE letter is a hole
    /// (only H in v1), lowercase a shaft — the ISO convention, so "H7" and "h7" are
    /// different things and both work.
    /// </summary>
    public static FitLimits Limits(double size, string designation)
    {
        ArgumentNullException.ThrowIfNull(designation);
        if (designation.Length < 2)
            throw new ArgumentException(
                $"'{designation}' is not a fit designation (a letter then a grade, like H7 or g6).",
                nameof(designation));
        char letter = designation[0];
        bool js = designation.StartsWith("js", StringComparison.Ordinal);
        if (!int.TryParse(designation[(js ? 2 : 1)..], out int grade))
            throw new ArgumentException(
                $"'{designation}' has no numeric grade after '{(js ? "js" : letter.ToString())}'.",
                nameof(designation));
        double it = GradeMicrons(size, grade) / 1000;

        if (char.IsUpper(letter))
        {
            if (letter != 'H')
                throw new ArgumentException(
                    $"Hole '{designation}': only the basic hole H is supported (the hole-basis " +
                    "system ISO 286 prefers); other hole letters are the shaft-basis system, filed.",
                    nameof(designation));
            return new FitLimits(it, 0);
        }
        if (js)
            return new FitLimits(it / 2, -it / 2);
        if (!ShaftDeviations.TryGetValue(letter, out var row))
        {
            throw new ArgumentException(
                $"Shaft letter '{letter}' is not in the v1 table (d, e, f, g, h, js, k, m, n, p). " +
                "a–c and r–z split their deviations at sub-range boundaries and are filed by name.",
                nameof(designation));
        }
        double deviation = row[RangeIndex(size)] / 1000;
        if (letter == 'k' && grade is < 4 or > 7)
            deviation = 0; // the standard: k's +0.6·∛D row applies to grades 4–7 only
        return letter is 'd' or 'e' or 'f' or 'g' or 'h'
            ? new FitLimits(deviation, deviation - it)   // es given, ei = es − IT
            : new FitLimits(deviation + it, deviation);  // ei given, es = ei + IT
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
}
