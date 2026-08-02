using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// A crossed-helical (screw) gear pair: two ordinary helical gears on SKEW shafts. The
/// geometry is nothing new — each member is a <see cref="Gears.HelicalGear"/> — so what
/// this type carries is the pairing arithmetic and the placement, which is the whole of
/// what "crossed helical" adds.
/// </summary>
/// <remarks>
/// <para><b>The meshing condition is equal NORMAL module and normal pressure angle</b>
/// (the two are cut by the same hob), and the transverse values then differ between the
/// members whenever their helix angles do. <b>The shaft angle is Σ = β₁ + β₂ with SIGNED
/// helix angles</b> (positive = right hand), which is one formula rather than two: the
/// textbook pair "β₁ + β₂ for the same hand, β₁ − β₂ for opposite hands" is what that
/// formula says once the second gear's hand is carried in the sign of its own angle.</para>
/// <para><b>The ratio is z₂/z₁ and NOT the pitch-radius ratio.</b> On parallel axes those
/// coincide and the habit is harmless; here r = m_n·z/(2·cos β), so two members with
/// different helix angles have radii in the ratio (z₂/z₁)·(cos β₁/cos β₂) — a pair at 20°
/// and 50° is out by a factor of 1.46. Speed follows the TEETH, because a tooth is what
/// hands over.</para>
/// <para><b>Contact is a POINT, not a line, and that is the form's real limitation.</b>
/// Two helicoids on skew axes touch at a single point which travels across the flank as
/// the pair turns; the contact stress is therefore concentrated and the load capacity is
/// a small fraction of an equivalent parallel-axis pair's. Crossed helicals are for light
/// drives, instrument trains and motion transfer between skew shafts — not for power. The
/// wear-in that broadens the point into a patch is exactly why they are usually run in
/// dissimilar materials.</para>
/// <para><b>What is placed and what is not</b>: <see cref="FirstGear"/> and
/// <see cref="SecondGear"/> return solids at the correct centre distance and shaft angle
/// with their pitch cylinders tangent at <see cref="ContactPoint"/>. The angular PHASE
/// that would put a tooth of one in the gap of the other is not solved — that is a mate
/// or a mechanism driver, and inventing one here would be a guess about which flank
/// drives.</para>
/// </remarks>
public sealed class CrossedHelicalPair
{
    private CrossedHelicalPair(
        double normalModule, double normalPressureAngleDegrees,
        GearSpec first, GearSpec second,
        double firstHelixAngleDegrees, double secondHelixAngleDegrees)
    {
        NormalModule = normalModule;
        NormalPressureAngleDegrees = normalPressureAngleDegrees;
        First = first;
        Second = second;
        FirstHelixAngleDegrees = firstHelixAngleDegrees;
        SecondHelixAngleDegrees = secondHelixAngleDegrees;
    }

    /// <summary>
    /// The pair for two gears cut with the same normal module and normal pressure angle.
    /// </summary>
    /// <param name="normalModule">m_n, the module measured perpendicular to the tooth
    /// trace — the cutter's own module, and the quantity the two members must share.</param>
    /// <param name="teeth1">Tooth count of the first member.</param>
    /// <param name="teeth2">Tooth count of the second member.</param>
    /// <param name="helixAngle1Degrees">SIGNED helix angle of the first member
    /// (positive = right hand).</param>
    /// <param name="helixAngle2Degrees">SIGNED helix angle of the second member.</param>
    /// <param name="normalPressureAngleDegrees">α_n, shared by both members (20° standard).</param>
    /// <exception cref="ArgumentException">The shaft angle β₁ + β₂ is zero — the shafts
    /// are then PARALLEL and the pair is an ordinary helical pair with LINE contact, a
    /// different and much stronger thing; use <see cref="Gears.HelicalGear"/> twice.</exception>
    public static CrossedHelicalPair Create(
        double normalModule, int teeth1, int teeth2,
        double helixAngle1Degrees, double helixAngle2Degrees,
        double normalPressureAngleDegrees = 20)
    {
        if (!(normalModule > 0))
            throw new ArgumentOutOfRangeException(nameof(normalModule), "Normal module must be positive.");
        HelicalGearGeometry.RequireAngle(helixAngle1Degrees);
        HelicalGearGeometry.RequireAngle(helixAngle2Degrees);
        if (!(normalPressureAngleDegrees > 0) || !(normalPressureAngleDegrees < 45))
            throw new ArgumentOutOfRangeException(nameof(normalPressureAngleDegrees),
                "Normal pressure angle must lie strictly between 0 and 45 degrees.");

        double shaft = helixAngle1Degrees + helixAngle2Degrees;
        if (shaft == 0)
            throw new ArgumentException(
                $"A shaft angle of zero (helix angles {helixAngle1Degrees:0.###} and "
                + $"{helixAngle2Degrees:0.###} degrees) puts the two shafts PARALLEL, which is an "
                + "ordinary helical pair with LINE contact - a different and far stronger pairing. "
                + "Build it as two Gears.HelicalGear solids of opposite hand on parallel axes.",
                nameof(helixAngle2Degrees));

        var first = HelicalGearGeometry.FromNormal(
            normalModule, teeth1, helixAngle1Degrees, normalPressureAngleDegrees);
        var second = HelicalGearGeometry.FromNormal(
            normalModule, teeth2, helixAngle2Degrees, normalPressureAngleDegrees);
        var pair = new CrossedHelicalPair(
            normalModule, normalPressureAngleDegrees, first, second,
            helixAngle1Degrees, helixAngle2Degrees);

        // Verify what was constructed rather than trusting the formula that built it: the
        // two tooth traces must be the SAME line at the contact point, which is the
        // geometric content of Σ = β₁ + β₂ and the one thing a sign slip would break.
        double agreement = pair.FirstToothDirection.Dot(pair.SecondToothDirection);
        if (Math.Abs(Math.Abs(agreement) - 1) > 1e-12)
            throw new InvalidOperationException(
                $"The placed tooth traces disagree at the contact point (direction dot product "
                + $"{agreement:0.############}) - this is a bug, not a modelling error.");
        return pair;
    }

    /// <summary>The shared normal module m_n.</summary>
    public double NormalModule { get; }

    /// <summary>The shared normal pressure angle α_n, degrees.</summary>
    public double NormalPressureAngleDegrees { get; }

    /// <summary>The first member's TRANSVERSE definition (m_t = m_n/cos β₁).</summary>
    public GearSpec First { get; }

    /// <summary>The second member's TRANSVERSE definition.</summary>
    public GearSpec Second { get; }

    /// <summary>Signed helix angle of the first member, degrees (positive = right hand).</summary>
    public double FirstHelixAngleDegrees { get; }

    /// <summary>Signed helix angle of the second member, degrees.</summary>
    public double SecondHelixAngleDegrees { get; }

    /// <summary>Whether the two members are cut the same hand — the sign test, so it
    /// cannot disagree with the shaft angle the same signs produce.</summary>
    public bool SameHand => FirstHelixAngleDegrees * SecondHelixAngleDegrees > 0;

    /// <summary>Signed shaft angle Σ = β₁ + β₂, degrees: the rotation taking the first
    /// shaft's direction to the second's about their common perpendicular.</summary>
    public double SignedShaftAngleDegrees => FirstHelixAngleDegrees + SecondHelixAngleDegrees;

    /// <summary>The shaft angle as a magnitude, degrees — what a drawing states.</summary>
    public double ShaftAngleDegrees => Math.Abs(SignedShaftAngleDegrees);

    /// <summary>Pitch radius of the first member, m_n·z₁/(2·cos β₁).</summary>
    public double FirstPitchRadius => First.PitchDiameter / 2;

    /// <summary>Pitch radius of the second member.</summary>
    public double SecondPitchRadius => Second.PitchDiameter / 2;

    /// <summary>Centre distance: the two pitch radii, since the pitch cylinders touch.</summary>
    public double CentreDistance => FirstPitchRadius + SecondPitchRadius;

    /// <summary>Transmission ratio ω₁/ω₂ = z₂/z₁ — the TEETH, never the radii
    /// (see the remarks on <see cref="CrossedHelicalPair"/>).</summary>
    public double Ratio => (double)Second.Teeth / First.Teeth;

    /// <summary>The first member's axis: world +Z through the origin.</summary>
    public Ray3d FirstAxis => new(Vector3d.Zero, Vector3d.UnitZ);

    /// <summary>The second member's axis, offset by the centre distance along +X and
    /// turned by the shaft angle.</summary>
    public Ray3d SecondAxis => new(SecondFrame.Origin, SecondFrame.Z);

    /// <summary>The single point at which the two pitch cylinders touch: the foot of the
    /// shafts' common perpendicular, at <see cref="FirstPitchRadius"/> from the first
    /// axis. Contact is a POINT — see the remarks.</summary>
    public Vector3d ContactPoint => new(FirstPitchRadius, 0, 0);

    /// <summary>
    /// The pose the second member's own frame takes in world: origin on its axis at the
    /// common perpendicular's foot, Z along its axis, X along the common perpendicular
    /// (pointing from the first axis toward the second).
    /// </summary>
    public Frame3d SecondFrame
    {
        get
        {
            double sigma = HelicalGearGeometry.Radians(SignedShaftAngleDegrees);
            double sin = Math.Sin(sigma), cos = Math.Cos(sigma);
            return Frame3d.FromOrthonormal(
                new Vector3d(CentreDistance, 0, 0),
                Vector3d.UnitX,
                new Vector3d(0, cos, -sin));       // Y = Z x X for Z = (0, sin, cos)
        }
    }

    /// <summary>The first member's tooth trace direction at <see cref="ContactPoint"/>:
    /// the tangent of its pitch helix there.</summary>
    public Vector3d FirstToothDirection => HelixTangent(
        Frame3d.WorldXY, ContactPoint, FirstHelixAngleDegrees);

    /// <summary>The second member's tooth trace direction at <see cref="ContactPoint"/>.
    /// Equal to <see cref="FirstToothDirection"/> up to sign — that identity IS the
    /// meshing condition, and it is checked at construction.</summary>
    public Vector3d SecondToothDirection => HelixTangent(
        SecondFrame, ContactPoint, SecondHelixAngleDegrees);

    /// <summary>
    /// The first member's solid, centred on the contact plane with its axis on world +Z.
    /// </summary>
    public Shape FirstGear(double faceWidth, double boreDiameter = 0, double? fitTolerance = null) =>
        Placed(First, FirstHelixAngleDegrees, faceWidth, boreDiameter, fitTolerance, Frame3d.WorldXY);

    /// <summary>
    /// The second member's solid, posed by <see cref="SecondFrame"/> and centred on the
    /// contact plane.
    /// </summary>
    public Shape SecondGear(double faceWidth, double boreDiameter = 0, double? fitTolerance = null) =>
        Placed(Second, SecondHelixAngleDegrees, faceWidth, boreDiameter, fitTolerance, SecondFrame);

    private static Shape Placed(
        GearSpec spec, double helixAngleDegrees, double faceWidth, double boreDiameter,
        double? fitTolerance, in Frame3d frame)
    {
        var gear = Gears.HelicalGear(spec, faceWidth, helixAngleDegrees, boreDiameter, fitTolerance);
        // HelicalGear grows from z = 0, so the gear is dropped half its width to put the
        // contact plane at its middle, and only then posed.
        return gear.Translate(0, 0, -faceWidth / 2).Transform(frame.ToMatrix());
    }

    /// <summary>
    /// The tangent of the pitch helix of a gear whose axis is <paramref name="frame"/>'s
    /// Z, at <paramref name="point"/>: cos β along the axis plus sin β around it. A
    /// positive (right-hand) helix advances counter-clockwise about the axis, which is
    /// the same sign convention the twisted extrusion takes.
    /// </summary>
    private static Vector3d HelixTangent(in Frame3d frame, in Vector3d point, double helixAngleDegrees)
    {
        var axis = frame.Z;
        var radial = point - frame.Origin;
        radial -= axis * radial.Dot(axis);
        radial = radial.Normalized();
        var around = axis.Cross(radial);
        double beta = HelicalGearGeometry.Radians(helixAngleDegrees);
        return (axis * Math.Cos(beta) + around * Math.Sin(beta)).Normalized();
    }
}
