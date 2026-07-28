namespace EngrCAD.Modeling;

/// <summary>
/// A metric screw-thread specification (dimensions in millimetres) built on the
/// ISO 68-1 basic profile — the 60° symmetric V-profile shared by the external and
/// internal thread. All derived dimensions follow from the nominal (major) diameter d
/// and the pitch P via the fundamental triangle height
/// <c>H = (√3/2)·P</c>:
/// <list type="bullet">
/// <item><description>crest truncation H/8 → flat of width P/8 at the major diameter d,</description></item>
/// <item><description>root truncation H/4 → flat of width P/4 at the minor diameter d1,</description></item>
/// <item><description>pitch diameter <c>d2 = d − 2·(3/8)H = d − (3/4)H ≈ d − 0.649519·P</c>,</description></item>
/// <item><description>minor diameter <c>d1 = d − 2·(5/8)H = d − (5/4)H ≈ d − 1.082532·P</c>,</description></item>
/// <item><description>basic thread depth (crest flat to root flat) <c>5H/8 ≈ 0.541266·P</c>.</description></item>
/// </list>
/// This is the shared <em>basic</em> profile: design-profile refinements (external root
/// rounding, allowance classes 6g/6H) are not modeled — 3D-printing fit is handled by
/// the explicit clearance parameter on <see cref="Shape.ExternalThread(ThreadSpec, double, double, bool)"/>
/// and <see cref="Shape.ThreadedHole"/> instead. <see cref="LeftHand"/> selects the
/// winding direction; the profile is identical either way.
/// </summary>
public sealed class ThreadSpec
{
    /// <summary>Creates a custom metric-style 60° thread spec. Prefer
    /// <see cref="StandardThreads.Metric(double)"/> for catalog sizes.</summary>
    /// <param name="nominalDiameter">Major diameter d (the "M" size).</param>
    /// <param name="pitch">Thread pitch P (axial advance per turn).</param>
    /// <param name="tapDrillDiameter">Tap-drill (pilot) diameter; defaults to the
    /// common rule of thumb d − P when omitted.</param>
    /// <param name="leftHand">Wind the thread left-handed; the default is right-hand.</param>
    public ThreadSpec(
        double nominalDiameter, double pitch, double? tapDrillDiameter = null, bool leftHand = false)
    {
        LeftHand = leftHand;
        if (pitch <= 0)
            throw new ArgumentOutOfRangeException(nameof(pitch));
        if (nominalDiameter <= 1.25 * pitch * Math.Sqrt(3) / 2)
            throw new ArgumentOutOfRangeException(nameof(nominalDiameter),
                "The nominal diameter must exceed (5/4)·H or the minor diameter is not positive.");
        double tapDrill = tapDrillDiameter ?? nominalDiameter - pitch;
        if (tapDrill <= 0 || tapDrill >= nominalDiameter)
            throw new ArgumentOutOfRangeException(nameof(tapDrillDiameter),
                "The tap drill must be positive and smaller than the nominal diameter.");
        NominalDiameter = nominalDiameter;
        Pitch = pitch;
        TapDrillDiameter = tapDrill;
    }

    /// <summary>Major (nominal) diameter d.</summary>
    public double NominalDiameter { get; }

    /// <summary>Pitch P — the axial advance per turn.</summary>
    public double Pitch { get; }

    /// <summary>Pilot-hole diameter for cutting this thread (from the catalog table for
    /// <see cref="StandardThreads.Metric(double)"/> sizes, else d − P).</summary>
    public double TapDrillDiameter { get; }

    /// <summary>
    /// Left-hand winding. The profile and every derived diameter are identical to the
    /// right-hand thread's — only the helix's sense changes — so a left-hand thread is
    /// the exact mirror image of its right-hand twin and is Native in every
    /// representation the right-hand one is.
    /// </summary>
    public bool LeftHand { get; }

    /// <summary>This thread wound the other way (see <see cref="LeftHand"/>).</summary>
    public ThreadSpec WithHandedness(bool leftHand) =>
        leftHand == LeftHand ? this : new ThreadSpec(NominalDiameter, Pitch, TapDrillDiameter, leftHand);

    /// <summary>Shorthand for <c>WithHandedness(true)</c>:
    /// <c>StandardThreads.Metric(8).LeftHanded()</c> is an M8×1.25-LH.</summary>
    public ThreadSpec LeftHanded() => WithHandedness(true);

    /// <summary>Fundamental triangle height H = (√3/2)·P (ISO 68-1).</summary>
    public double FundamentalHeight => Pitch * Math.Sqrt(3) / 2;

    /// <summary>Major diameter d (same as <see cref="NominalDiameter"/>).</summary>
    public double MajorDiameter => NominalDiameter;

    /// <summary>Pitch diameter d2 = d − (3/4)·H.</summary>
    public double PitchDiameter => NominalDiameter - 0.75 * FundamentalHeight;

    /// <summary>Minor diameter d1 = d − (5/4)·H.</summary>
    public double MinorDiameter => NominalDiameter - 1.25 * FundamentalHeight;

    /// <summary>Radial depth of the basic profile, crest flat to root flat: 5H/8.</summary>
    public double ThreadDepth => 0.625 * FundamentalHeight;

    /// <summary>Axial width of the crest flat (at the major diameter): P/8.</summary>
    public double CrestFlatWidth => Pitch / 8;

    /// <summary>Axial width of the root flat (at the minor diameter): P/4.</summary>
    public double RootFlatWidth => Pitch / 4;

    /// <summary>The thread designation, e.g. "M8×1.25" — "M8×1.25-LH" when left-handed
    /// (ISO 261 marks the exception, never the right-hand default).</summary>
    public string Designation => $"M{NominalDiameter:g4}×{Pitch:g4}" + (LeftHand ? "-LH" : "");

    public override string ToString() => Designation;
}

/// <summary>
/// Standard metric thread catalog: the ISO 261/262 coarse- and fine-pitch series M2–M12
/// with the ISO 68-1 basic profile (see <see cref="ThreadSpec"/> for the profile
/// formulas). Coarse tap-drill diameters reuse the <see cref="StandardHoles"/> table (the
/// standard-chart values, i.e. d − P rounded to a stock drill: 6.8 for M8, 10.2 for M12);
/// fine pitches are already round numbers, so their tap drills are exactly d − P.
/// ⚠ Verify the pitch and tap-drill columns against a current standard before production
/// use.
/// </summary>
public static class StandardThreads
{
    // ISO 261/262 coarse pitches; tap drills match StandardHoles' TapDrill column.
    private static readonly Dictionary<double, (double Pitch, double TapDrill)> Coarse = new()
    {
        [2.0] = (0.40, 1.60),
        [2.5] = (0.45, 2.05),
        [3.0] = (0.50, 2.50),
        [4.0] = (0.70, 3.30),
        [5.0] = (0.80, 4.20),
        [6.0] = (1.00, 5.00),
        [8.0] = (1.25, 6.80),
        [10.0] = (1.50, 8.50),
        [12.0] = (1.75, 10.20),
    };

    // ISO 261 fine pitches, coarsest (first choice) first — the order Fine() reads.
    // Every fine tap drill is exactly d − P, so no second column is needed: unlike the
    // coarse series, whose d − P lands between stock drills and is rounded on the chart,
    // the fine pitches are round numbers to begin with (M8x1 -> 7.0, M10x1.25 -> 8.75).
    private static readonly Dictionary<double, double[]> FinePitches = new()
    {
        [2.0] = [0.25],
        [2.5] = [0.35],
        [3.0] = [0.35],
        [4.0] = [0.5],
        [5.0] = [0.5],
        [6.0] = [0.75],
        [8.0] = [1.0, 0.75],
        [10.0] = [1.25, 1.0, 0.75],
        [12.0] = [1.5, 1.25, 1.0],
    };

    /// <summary>Coarse-pitch metric thread for an M<paramref name="size"/> fastener
    /// (e.g. <c>Metric(8)</c> → M8×1.25).</summary>
    public static ThreadSpec Metric(double size) =>
        Coarse.TryGetValue(size, out var row)
            ? new ThreadSpec(size, row.Pitch, row.TapDrill)
            : throw new ArgumentOutOfRangeException(nameof(size),
                $"M{size:g3} is not in the coarse-thread table (available: " +
                $"{string.Join(", ", Coarse.Keys.OrderBy(k => k).Select(k => $"M{k:g3}"))}).");

    /// <summary>
    /// The first-choice FINE-pitch metric thread for an M<paramref name="size"/> fastener
    /// (e.g. <c>Fine(8)</c> → M8×1, <c>Fine(10)</c> → M10×1.25). Sizes carrying more than
    /// one fine pitch are reached through <see cref="Metric(double, double)"/>.
    /// </summary>
    public static ThreadSpec Fine(double size) => Metric(size, FinePitchesFor(size)[0]);

    /// <summary>
    /// A catalogued metric thread at an explicit <paramref name="pitch"/> — the way to
    /// name a second- or third-choice fine pitch such as M10×0.75. Pitches outside the
    /// catalogue are refused by name; construct a <see cref="ThreadSpec"/> directly for
    /// genuinely non-standard threads.
    /// </summary>
    public static ThreadSpec Metric(double size, double pitch)
    {
        foreach (double fine in FinePitchesFor(size))
        {
            // Exact match against a catalogue literal: these are table lookups, not
            // measurements, and a near-miss is a caller typo worth reporting.
            if (pitch == fine)
                return new ThreadSpec(size, pitch, size - pitch);
        }
        if (Coarse.TryGetValue(size, out var coarse) && pitch == coarse.Pitch)
            return new ThreadSpec(size, pitch, coarse.TapDrill);
        throw new ArgumentOutOfRangeException(nameof(pitch),
            $"M{size:g3}×{pitch:g4} is not in the catalogue; M{size:g3} is available at " +
            $"{string.Join(", ", Pitches(size).Select(p => p.ToString("g4")))}.");
    }

    /// <summary>Every catalogued pitch for a size, coarse first then the fine series.</summary>
    public static IReadOnlyList<double> Pitches(double size) =>
        [Coarse[ValidSize(size)].Pitch, .. FinePitchesFor(size)];

    private static double[] FinePitchesFor(double size) => FinePitches[ValidSize(size)];

    private static double ValidSize(double size) =>
        Coarse.ContainsKey(size)
            ? size
            : throw new ArgumentOutOfRangeException(nameof(size),
                $"M{size:g3} is not in the thread table (available: " +
                $"{string.Join(", ", Coarse.Keys.OrderBy(k => k).Select(k => $"M{k:g3}"))}).");
}
