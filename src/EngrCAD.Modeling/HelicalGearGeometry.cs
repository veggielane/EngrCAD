namespace EngrCAD.Modeling;

/// <summary>
/// The transverse ↔ normal arithmetic every helical gear form needs: a helical gear is
/// cut with a NORMAL module (the hob's own module, measured perpendicular to the tooth
/// trace) but drawn from its TRANSVERSE section (the profile a plane perpendicular to
/// the axis cuts), and the two differ by cos β.
/// </summary>
/// <remarks>
/// <para><see cref="GearSpec"/> is a TRANSVERSE definition — <see cref="Gears.Spur"/>
/// generates the section that <see cref="Gears.HelicalGear"/> twists — so a design
/// stated the way a cutter is ordered (normal module, normal pressure angle) reaches it
/// through <see cref="FromNormal"/>. The relations are exact and stated as arithmetic so
/// a caller (and a test) can ask rather than restate them:</para>
/// <list type="bullet">
/// <item><description>m_n = m_t·cos β</description></item>
/// <item><description>tan α_n = tan α_t·cos β</description></item>
/// <item><description>axial pitch p_x = π·m_t/tan β = π·m_n/sin β</description></item>
/// <item><description>lead (one full turn of the helix) = 2π·r/tan β</description></item>
/// </list>
/// <para>Sign convention throughout: a POSITIVE helix angle is a RIGHT-hand helix, which
/// is a counter-clockwise (right-handed) twist about the gear's own +Z as the section
/// advances — the same sign <see cref="Gears.HelicalGear"/> and
/// <see cref="Shape.Extrude(Sketch, double, double, double, SketchPlane?, int?)"/> take.</para>
/// </remarks>
public static class HelicalGearGeometry
{
    /// <summary>The largest helix angle magnitude the gear factories admit (degrees).
    /// Past it the transverse profile is so stretched that "a gear seen end on" stops
    /// being a useful description, and <see cref="Gears.HelicalGear"/> refuses.</summary>
    public const double MaximumHelixAngleDegrees = 60;

    /// <summary>Normal module m_n = m_t·cos β from a transverse module.</summary>
    public static double NormalModule(double transverseModule, double helixAngleDegrees)
    {
        Require(transverseModule, helixAngleDegrees, nameof(transverseModule));
        return transverseModule * Math.Cos(Radians(helixAngleDegrees));
    }

    /// <summary>Transverse module m_t = m_n/cos β — what a <see cref="GearSpec"/> takes.</summary>
    public static double TransverseModule(double normalModule, double helixAngleDegrees)
    {
        Require(normalModule, helixAngleDegrees, nameof(normalModule));
        return normalModule / Math.Cos(Radians(helixAngleDegrees));
    }

    /// <summary>Normal pressure angle α_n from the transverse one: tan α_n = tan α_t·cos β.</summary>
    public static double NormalPressureAngleDegrees(double transversePressureAngleDegrees, double helixAngleDegrees)
    {
        RequireAngle(helixAngleDegrees);
        return Degrees(Math.Atan(
            Math.Tan(Radians(transversePressureAngleDegrees)) * Math.Cos(Radians(helixAngleDegrees))));
    }

    /// <summary>Transverse pressure angle α_t from the normal one: tan α_t = tan α_n/cos β
    /// — what a <see cref="GearSpec"/> takes.</summary>
    public static double TransversePressureAngleDegrees(double normalPressureAngleDegrees, double helixAngleDegrees)
    {
        RequireAngle(helixAngleDegrees);
        return Degrees(Math.Atan(
            Math.Tan(Radians(normalPressureAngleDegrees)) / Math.Cos(Radians(helixAngleDegrees))));
    }

    /// <summary>
    /// The transverse <see cref="GearSpec"/> for a gear ordered in NORMAL terms — the way
    /// a cutter is specified.
    /// </summary>
    /// <remarks>
    /// <para><b>Every per-module coefficient scales by cos β, and that includes the
    /// profile shift.</b> The addendum, the dedendum, the root fillet radius and the rack
    /// datum shift are RADIAL LENGTHS — a hob cutting 1.00·m_n of addendum leaves the same
    /// millimetres of tooth height whichever section you measure the module in — so their
    /// coefficients, which <see cref="GearSpec"/> reads against the TRANSVERSE module,
    /// must be divided by m_t/m_n = 1/cos β. Everything downstream then falls out
    /// consistently: the transverse tooth thickness comes back as
    /// m_t(π/2 + 2·x_n·tan α_n), which is the normal thickness over cos β as it must be,
    /// and the undercut limit as 2·h_a*_n·cos β/sin²α_t, the classical helical form.</para>
    /// <para>The scaling is not cosmetic. At β = 45° a 0.38·m_n root fillet becomes
    /// 0.2687·m_t; left unscaled it reads 0.38·m_t = 1.34× too large, and an 18-tooth
    /// member is REFUSED outright by <see cref="Gears.Spur"/> for adjacent root fillets
    /// overlapping — a plausible-looking pair that cannot be drawn.</para>
    /// </remarks>
    /// <param name="normalModule">m_n, the cutter's module.</param>
    /// <param name="teeth">Tooth count.</param>
    /// <param name="helixAngleDegrees">Signed helix angle (positive = right hand).</param>
    /// <param name="normalPressureAngleDegrees">α_n (20° standard).</param>
    /// <param name="profileShift">x_n, the rack datum shift in NORMAL modules.</param>
    /// <param name="normalAddendumCoefficient">h_a*_n (ISO 53 profile A: 1.00).</param>
    /// <param name="normalDedendumCoefficient">h_f*_n (1.25).</param>
    /// <param name="normalRootFilletCoefficient">ρ_f*_n (0.38).</param>
    public static GearSpec FromNormal(
        double normalModule, int teeth, double helixAngleDegrees,
        double normalPressureAngleDegrees = 20, double profileShift = 0,
        double normalAddendumCoefficient = 1.00,
        double normalDedendumCoefficient = 1.25,
        double normalRootFilletCoefficient = 0.38)
    {
        double cos = Math.Cos(Radians(helixAngleDegrees));
        return new GearSpec(
            TransverseModule(normalModule, helixAngleDegrees), teeth,
            TransversePressureAngleDegrees(normalPressureAngleDegrees, helixAngleDegrees),
            profileShift * cos)
        {
            AddendumCoefficient = normalAddendumCoefficient * cos,
            DedendumCoefficient = normalDedendumCoefficient * cos,
            RootFilletCoefficient = normalRootFilletCoefficient * cos,
        };
    }

    /// <summary>Pitch radius r = m_n·z/(2·cos β) — equivalently m_t·z/2.</summary>
    public static double PitchRadiusFromNormal(double normalModule, int teeth, double helixAngleDegrees) =>
        TransverseModule(normalModule, helixAngleDegrees) * teeth / 2;

    /// <summary>
    /// Total twist of the transverse section over <paramref name="height"/>, radians:
    /// the section rotates by height·tan β / r, which is what makes the flank a helix of
    /// angle β at the pitch cylinder. Positive = right-hand.
    /// </summary>
    public static double Twist(double pitchRadius, double height, double helixAngleDegrees)
    {
        if (!(pitchRadius > 0))
            throw new ArgumentOutOfRangeException(nameof(pitchRadius));
        if (!double.IsFinite(height))
            throw new ArgumentOutOfRangeException(nameof(height));
        RequireAngle(helixAngleDegrees);
        return height * Math.Tan(Radians(helixAngleDegrees)) / pitchRadius;
    }

    /// <summary>Axial pitch p_x = π·m_t/tan β — the axial distance between successive
    /// tooth traces on one flank, and the face width one tooth needs to overlap itself.</summary>
    public static double AxialPitch(double transverseModule, double helixAngleDegrees)
    {
        Require(transverseModule, helixAngleDegrees, nameof(transverseModule));
        double tan = Math.Tan(Radians(helixAngleDegrees));
        if (tan == 0)
            throw new ArgumentOutOfRangeException(nameof(helixAngleDegrees),
                "A zero helix angle is a spur gear: its axial pitch is infinite.");
        return Math.PI * transverseModule / Math.Abs(tan);
    }

    /// <summary>Lead: the axial advance of one flank helix over a full turn, 2π·r/tan β.</summary>
    public static double Lead(double pitchRadius, double helixAngleDegrees)
    {
        if (!(pitchRadius > 0))
            throw new ArgumentOutOfRangeException(nameof(pitchRadius));
        RequireAngle(helixAngleDegrees);
        double tan = Math.Tan(Radians(helixAngleDegrees));
        if (tan == 0)
            throw new ArgumentOutOfRangeException(nameof(helixAngleDegrees),
                "A zero helix angle is a spur gear: its lead is infinite.");
        return 2 * Math.PI * pitchRadius / Math.Abs(tan);
    }

    internal static double Radians(double degrees) => degrees * Math.PI / 180;

    internal static double Degrees(double radians) => radians * 180 / Math.PI;

    internal static void RequireAngle(double helixAngleDegrees)
    {
        if (!(Math.Abs(helixAngleDegrees) < MaximumHelixAngleDegrees))
            throw new ArgumentOutOfRangeException(nameof(helixAngleDegrees),
                $"Helix angle must lie strictly between -{MaximumHelixAngleDegrees} and "
                + $"{MaximumHelixAngleDegrees} degrees.");
    }

    private static void Require(double module, double helixAngleDegrees, string moduleName)
    {
        if (!(module > 0))
            throw new ArgumentOutOfRangeException(moduleName, "Module must be positive.");
        RequireAngle(helixAngleDegrees);
    }
}
