using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// A cylindrical worm of the ZA form — straight-sided in the AXIAL plane, which is what
/// makes it a thread rather than a gear: the body is one helical sweep of a trapezoidal
/// (radius, axial) profile, exactly the family <c>SolidFactory.MakeThreadedRod</c>
/// already speaks, with the axial module taking the place of a thread's pitch.
/// </summary>
/// <remarks>
/// <para><b>Starts, not teeth.</b> A worm's "one tooth" is one START, and the reduction
/// ratio is the wheel's tooth count over that number — the classic trap, since a
/// two-start worm on a 40-tooth wheel reduces 20:1 and not 40:1. Every derived quantity
/// here keeps the two apart: <see cref="AxialPitch"/> is the tooth-to-tooth spacing
/// along the axis and <see cref="Lead"/> = starts × axial pitch is the advance per turn.
/// </para>
/// <para><b>The diameter is a free choice.</b> Unlike a gear, whose pitch diameter is
/// m·z, a worm's is independent of everything else — it sets the lead angle
/// tan γ = lead/(π·d₁) = z₁/q and so the efficiency and whether the drive self-locks.
/// It is given directly (the dimension a drawing carries) with the diameter factor
/// q = d₁/m derived; <see cref="FromDiameterFactor"/> takes the other spelling.</para>
/// <para>⚠ The proportion defaults (addendum 1.00·m, dedendum 1.25·m of the AXIAL
/// module) follow the same ISO 53 rack coefficients the rest of this file uses; verify
/// against a current worm-gearing standard before production use.</para>
/// </remarks>
public sealed record WormSpec
{
    /// <param name="axialModule">Axial module m_x (mm): the axial pitch over π. It is the
    /// wheel's TRANSVERSE module too — that identity is the meshing condition.</param>
    /// <param name="starts">Number of thread starts z₁ (1 for the classic self-locking
    /// worm; 2–4 for higher efficiency and lower ratio).</param>
    /// <param name="pitchDiameter">Reference (pitch) diameter d₁ — a free design choice
    /// that sets the lead angle.</param>
    /// <param name="axialPressureAngleDegrees">Flank inclination α_x in the AXIAL plane,
    /// which for a 90° drive is also the wheel's transverse pressure angle.</param>
    /// <param name="leftHand">Wind the worm left-handed; the wheel then takes the same
    /// hand (at a 90° shaft angle the two members always match).</param>
    public WormSpec(double axialModule, int starts, double pitchDiameter,
        double axialPressureAngleDegrees = 20, bool leftHand = false)
    {
        if (!(axialModule > 0))
            throw new ArgumentOutOfRangeException(nameof(axialModule), "Axial module must be positive.");
        if (starts < 1)
            throw new ArgumentOutOfRangeException(nameof(starts), "A worm needs at least one start.");
        if (!(pitchDiameter > 0))
            throw new ArgumentOutOfRangeException(nameof(pitchDiameter), "Pitch diameter must be positive.");
        if (!(axialPressureAngleDegrees > 0) || !(axialPressureAngleDegrees < 45))
            throw new ArgumentOutOfRangeException(nameof(axialPressureAngleDegrees),
                "Pressure angle must lie strictly between 0° and 45°.");
        AxialModule = axialModule;
        Starts = starts;
        PitchDiameter = pitchDiameter;
        AxialPressureAngleDegrees = axialPressureAngleDegrees;
        LeftHand = leftHand;
    }

    /// <summary>The worm of <paramref name="diameterFactor"/> q = d₁/m — the spelling
    /// worm-gearing tables use, since tan γ = z₁/q makes the lead angle a function of
    /// two integers-ish numbers rather than of a diameter.</summary>
    public static WormSpec FromDiameterFactor(double axialModule, int starts, double diameterFactor,
        double axialPressureAngleDegrees = 20, bool leftHand = false) =>
        new(axialModule, starts, axialModule * diameterFactor, axialPressureAngleDegrees, leftHand);

    /// <summary>Axial module m_x (mm).</summary>
    public double AxialModule { get; }

    /// <summary>Number of starts z₁ — the worm's "tooth count" for ratio purposes.</summary>
    public int Starts { get; }

    /// <summary>Reference (pitch) diameter d₁.</summary>
    public double PitchDiameter { get; }

    /// <summary>Flank inclination α_x measured in the axial plane (the ZA form's defining
    /// choice: it is the AXIAL section that is straight).</summary>
    public double AxialPressureAngleDegrees { get; }

    /// <summary>Left-hand winding.</summary>
    public bool LeftHand { get; }

    /// <summary>Addendum coefficient h_a* of the axial module.</summary>
    public double AddendumCoefficient { get; init; } = 1.00;

    /// <summary>Dedendum coefficient h_f* of the axial module, clearance included.</summary>
    public double DedendumCoefficient { get; init; } = 1.25;

    internal double AxialPressureAngleRadians => AxialPressureAngleDegrees * Math.PI / 180;

    /// <summary>Axial pitch p_x = π·m_x — tooth to tooth along the axis, and equal to the
    /// mating wheel's TRANSVERSE circular pitch (the meshing condition).</summary>
    public double AxialPitch => Math.PI * AxialModule;

    /// <summary>Lead p_z = z₁·p_x — the axial advance per turn.</summary>
    public double Lead => Starts * AxialPitch;

    /// <summary>Diameter factor q = d₁/m_x.</summary>
    public double DiameterFactor => PitchDiameter / AxialModule;

    /// <summary>Lead angle γ (radians) at the pitch cylinder: tan γ = lead/(π·d₁), which
    /// reduces to z₁/q. Measured from the transverse plane, so a fast multi-start worm
    /// has a LARGE γ.</summary>
    public double LeadAngleRadians => Math.Atan2(Lead, Math.PI * PitchDiameter);

    /// <summary>Lead angle γ in degrees.</summary>
    public double LeadAngleDegrees => LeadAngleRadians * 180 / Math.PI;

    /// <summary>Helix angle β₁ = 90° − γ, measured from the AXIS. Quoted because the
    /// mating wheel's helix angle is γ, not this: at a 90° shaft angle β₁ + β₂ = 90°.
    /// </summary>
    public double HelixAngleDegrees => 90 - LeadAngleDegrees;

    /// <summary>Addendum h_a = h_a*·m_x.</summary>
    public double Addendum => AxialModule * AddendumCoefficient;

    /// <summary>Dedendum h_f = h_f*·m_x.</summary>
    public double Dedendum => AxialModule * DedendumCoefficient;

    /// <summary>Tip diameter d_a1 = d₁ + 2·h_a.</summary>
    public double TipDiameter => PitchDiameter + 2 * Addendum;

    /// <summary>Root diameter d_f1 = d₁ − 2·h_f.</summary>
    public double RootDiameter => PitchDiameter - 2 * Dedendum;

    /// <summary>Axial tooth thickness at the pitch cylinder, p_x/2 — half the axial pitch,
    /// the worm's version of the rack's π·m/2.</summary>
    public double AxialToothThicknessAtPitch => AxialPitch / 2;

    /// <summary>Normal module m_n = m_x·cos γ (the module a hob or a cutter sees).</summary>
    public double NormalModule => AxialModule * Math.Cos(LeadAngleRadians);

    /// <summary>Normal pressure angle: tan α_n = tan α_x·cos γ. Falls with the lead angle,
    /// which is why a fast multi-start worm has a much flatter normal flank than its
    /// axial 20° suggests.</summary>
    public double NormalPressureAngleDegrees =>
        Math.Atan(Math.Tan(AxialPressureAngleRadians) * Math.Cos(LeadAngleRadians)) * 180 / Math.PI;

    /// <summary>Axial half-width of the flat crest land (at the tip cylinder):
    /// p_x/4 − h_a·tan α_x. Non-positive is a pointed thread and
    /// <see cref="Gears.Worm"/> refuses it by name.</summary>
    public double CrestLandHalfWidth =>
        AxialPitch / 4 - Addendum * Math.Tan(AxialPressureAngleRadians);

    /// <summary>Axial half-width at the root cylinder: p_x/4 + h_f·tan α_x. Twice this
    /// must stay under the axial pitch or adjacent starts overlap at the root.</summary>
    public double RootHalfWidth =>
        AxialPitch / 4 + Dedendum * Math.Tan(AxialPressureAngleRadians);

    /// <summary>
    /// The exact volume of <paramref name="length"/> of this worm, by Pappus over the
    /// helical sweep: averaging the axial cross-section over a full turn recovers one
    /// complete period of the profile, so V = L·(2π/lead)·∫½R(z)² dz over one lead —
    /// which is why <b>any</b> length works, whole turns or not (the phase washes out).
    /// Each straight profile run contributes Δz·(r₀² + r₀r₁ + r₁²)/6.
    /// </summary>
    public double VolumeOfLength(double length)
    {
        if (!(length > 0))
            throw new ArgumentOutOfRangeException(nameof(length));
        double integral = 0;
        var profile = Gears.WormPitchProfile(this);
        for (int k = 0; k < profile.Count; k++)
        {
            var c0 = profile[k];
            var c1 = k + 1 < profile.Count
                ? profile[k + 1]
                : new Vector2d(profile[0].X, profile[0].Y + Lead);
            integral += (c1.Y - c0.Y) * (c0.X * c0.X + c0.X * c1.X + c1.X * c1.X) / 6;
        }
        return length * (2 * Math.PI / Lead) * integral;
    }
}

/// <summary>
/// A worm and its wheel at a 90° shaft angle, with the matching arithmetic done once so
/// the two members cannot be specified inconsistently.
/// </summary>
/// <remarks>
/// <para><b>The wheel is a CROSSED-HELICAL approximation and the caveat is the design,
/// not a footnote.</b> A true worm wheel is throated (globoid): its teeth wrap the worm,
/// and their surface is the ENVELOPE of the worm's motion — hobbing kinematics, with no
/// closed form to draw. What is offered instead is an ordinary helical gear whose helix
/// angle equals the worm's LEAD angle, which is the exact geometry of a crossed-helical
/// (screw) pair: it meshes, it transmits the stated ratio, and it touches the worm at a
/// POINT rather than along a line. That is right for a motion drive, a 3D print or a
/// layout, and wrong for a load-carrying reducer, where the throat is what carries the
/// contact.</para>
/// <para>Two identities make the pairing work and both are asserted rather than assumed:
/// the worm's AXIAL pitch is the wheel's TRANSVERSE circular pitch (so the wheel's
/// transverse module is the worm's axial module), and at a 90° shaft angle the worm's
/// axial plane IS the wheel's transverse plane at the central point — so the wheel's
/// transverse pressure angle is the worm's axial one, with nothing to convert.</para>
/// </remarks>
public sealed class WormPair
{
    internal WormPair(WormSpec worm, int wheelTeeth)
    {
        Worm = worm;
        WheelTeeth = wheelTeeth;
        Wheel = new GearSpec(worm.AxialModule, wheelTeeth, worm.AxialPressureAngleDegrees)
        {
            AddendumCoefficient = worm.AddendumCoefficient,
            DedendumCoefficient = worm.DedendumCoefficient,
        };
    }

    /// <summary>The worm.</summary>
    public WormSpec Worm { get; }

    /// <summary>Wheel tooth count z₂.</summary>
    public int WheelTeeth { get; }

    /// <summary>
    /// The wheel as an ordinary helical gear specification — TRANSVERSE module and
    /// pressure angle, which are the worm's axial ones (see the type remarks). Feed it to
    /// <see cref="Gears.HelicalGear"/> with <see cref="WheelHelixAngleDegrees"/>, or use
    /// <see cref="Gears.WormWheel"/>, which does exactly that.
    /// </summary>
    public GearSpec Wheel { get; }

    /// <summary>Wheel pitch diameter d₂ = m_x·z₂.</summary>
    public double WheelPitchDiameter => Worm.AxialModule * WheelTeeth;

    /// <summary>
    /// The wheel's helix angle, equal in magnitude to the worm's LEAD angle (β₁ + β₂ = 90°
    /// at a 90° shaft angle, and β₁ = 90° − γ), signed so that the wheel takes the worm's
    /// hand — the two members of a 90° crossed pair always match.
    /// </summary>
    public double WheelHelixAngleDegrees =>
        Worm.LeftHand ? -Worm.LeadAngleDegrees : Worm.LeadAngleDegrees;

    /// <summary>Shaft angle Σ — 90° is the only arrangement this pairing covers.</summary>
    public double ShaftAngleDegrees => 90;

    /// <summary>Centre distance a = (d₁ + d₂)/2.</summary>
    public double CentreDistance => (Worm.PitchDiameter + WheelPitchDiameter) / 2;

    /// <summary>
    /// Reduction ratio i = z₂/z₁ — the wheel's tooth count over the worm's number of
    /// STARTS. A worm's "one tooth" is one start, so a two-start worm halves the ratio a
    /// tooth count alone would suggest.
    /// </summary>
    public double GearRatio => (double)WheelTeeth / Worm.Starts;
}

public static partial class Gears
{
    /// <summary>
    /// The worm's (radius, axial) profile over ONE LEAD — <see cref="WormSpec.Starts"/>
    /// teeth of it, since a helical sweep repeats every lead. Corners run bottom to top: crest
    /// land at the tip radius, descending flank, root land at the root radius, and the
    /// closing segment wraps to the next crest.
    /// </summary>
    internal static IReadOnlyList<Vector2d> WormPitchProfile(WormSpec spec)
    {
        double px = spec.AxialPitch;
        double ra = spec.TipDiameter / 2, rf = spec.RootDiameter / 2;
        double a = spec.CrestLandHalfWidth, b = spec.RootHalfWidth;
        var profile = new List<Vector2d>(4 * spec.Starts);
        for (int j = 0; j < spec.Starts; j++)
        {
            double z = j * px;
            profile.Add(new Vector2d(ra, z - a));
            profile.Add(new Vector2d(ra, z + a));
            profile.Add(new Vector2d(rf, z + b));
            profile.Add(new Vector2d(rf, z + px - b));
        }
        return profile;
    }

    /// <summary>
    /// A worm along +Z, z ∈ [0, <paramref name="length"/>], capped flat at both ends.
    /// </summary>
    /// <remarks>
    /// <para><b>The worm IS a thread</b>, so this is one boolean-free helical sweep of the
    /// ZA trapezoid through <see cref="SolidFactory.MakeThreadedRod"/> — no core cylinder
    /// exists, because the root lands are part of the same sweep, which is exactly what
    /// keeps a coaxial tangent seam (unsupported boolean input) from ever arising. A
    /// multi-start worm is not a different construction: the profile handed over covers
    /// one LEAD and simply contains <see cref="WormSpec.Starts"/> teeth.</para>
    /// <para>Representation support: B-Rep-Native (the sweep is the solid), mesh and
    /// implicit bridged through its tessellation. Helical surfaces have no AP214 entity,
    /// so a worm is not STEP-exportable; <c>BrepArchive</c> (.ecb) round-trips it
    /// losslessly.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">Refused by name: a non-positive root diameter,
    /// a pointed thread (the crest land vanishes) and adjacent starts overlapping at the
    /// root cylinder.</exception>
    public static Shape Worm(WormSpec spec, double length)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!(length > 0))
            throw new ArgumentOutOfRangeException(nameof(length));
        if (!(spec.AddendumCoefficient > 0))
            throw new ArgumentException("Addendum coefficient must be positive.", nameof(spec));
        if (!(spec.DedendumCoefficient > 0))
            throw new ArgumentException("Dedendum coefficient must be positive.", nameof(spec));
        if (!(spec.RootDiameter > 0))
            throw new ArgumentException(
                $"Root diameter {spec.RootDiameter:0.###} is not positive: a dedendum of "
                + $"{spec.DedendumCoefficient}*m consumes the whole pitch radius "
                + $"{spec.PitchDiameter / 2:0.###}. Increase the pitch diameter (the diameter factor "
                + $"is only q = {spec.DiameterFactor:0.##}).", nameof(spec));
        // Weld-tier LENGTHS, as everywhere else in this file.
        if (!(2 * spec.CrestLandHalfWidth > 1e-9))
            throw new ArgumentException(
                $"The worm thread comes to a point: crest land {2 * spec.CrestLandHalfWidth:0.###e0} at "
                + $"addendum {spec.AddendumCoefficient}*m and {spec.AxialPressureAngleDegrees:0.#} deg "
                + "axial pressure angle. Reduce the addendum or the pressure angle.", nameof(spec));
        if (!(spec.AxialPitch - 2 * spec.RootHalfWidth > 1e-9))
            throw new ArgumentException(
                $"Adjacent starts overlap at the root cylinder: the root land would be "
                + $"{spec.AxialPitch - 2 * spec.RootHalfWidth:0.###}. Reduce the dedendum "
                + $"({spec.DedendumCoefficient}*m) or the axial pressure angle "
                + $"({spec.AxialPressureAngleDegrees:0.#} deg).", nameof(spec));

        var solid = SolidFactory.MakeThreadedRod(
            WormPitchProfile(spec), spec.Lead, length, frame: null, leftHand: spec.LeftHand);
        return Shape.From(solid);
    }

    /// <summary>The worm and a <paramref name="wheelTeeth"/>-tooth wheel at a 90° shaft
    /// angle; see <see cref="Modeling.WormPair"/> for the crossed-helical caveat.</summary>
    public static WormPair WormPair(WormSpec worm, int wheelTeeth)
    {
        ArgumentNullException.ThrowIfNull(worm);
        if (wheelTeeth < 3)
            throw new ArgumentOutOfRangeException(nameof(wheelTeeth), "A wheel needs at least 3 teeth.");
        return new WormPair(worm, wheelTeeth);
    }

    /// <summary>
    /// The wheel of <paramref name="pair"/> as a solid: an ordinary
    /// <see cref="HelicalGear"/> on the wheel's transverse spec at the worm's lead angle.
    /// <b>Point contact</b> — this is the crossed-helical approximation, not a throated
    /// wheel; see <see cref="Modeling.WormPair"/>.
    /// </summary>
    /// <remarks>Representation support follows the twisted extrusion: mesh and implicit
    /// only, B-Rep honestly Impossible (a twist has no exact B-Rep form). The WORM is the
    /// exact half of the pair.</remarks>
    public static Shape WormWheel(WormPair pair, double faceWidth, double boreDiameter = 0,
        double? fitTolerance = null, int? slices = null)
    {
        ArgumentNullException.ThrowIfNull(pair);
        return HelicalGear(pair.Wheel, faceWidth, pair.WheelHelixAngleDegrees, boreDiameter,
            fitTolerance: fitTolerance, slices: slices);
    }
}
