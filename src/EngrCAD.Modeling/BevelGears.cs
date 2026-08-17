using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// The cone angles of a mating straight bevel pair, from the two tooth counts and the
/// shaft angle — the arithmetic a <see cref="BevelGears.Straight"/> call needs before it
/// can draw either member.
/// </summary>
/// <remarks>
/// <para>Both pitch cones share one apex and one cone distance, so the ratio is carried
/// by the cone angles: <c>tan δ₁ = sin Σ / (z₂/z₁ + cos Σ)</c>, which at the usual
/// Σ = 90° reduces to <c>tan δ₁ = z₁/z₂</c>. The wheel takes the complement in the shaft
/// angle, <c>δ₁ + δ₂ = Σ</c> — asserted, not assumed (see the tests).</para>
/// <para>Only the pitch cones are decided here. Everything a tooth needs — module,
/// pressure angle, profile shift, rack coefficients — stays on each member's own
/// <see cref="GearSpec"/>, and a real pair must share the module and the pressure
/// angle.</para>
/// </remarks>
public sealed record BevelPair
{
    /// <summary>Solves the pitch cone angles for <paramref name="pinionTeeth"/> meshing
    /// <paramref name="wheelTeeth"/> at <paramref name="shaftAngleDegrees"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Non-positive tooth counts, or a shaft
    /// angle outside (0°, 180°).</exception>
    /// <exception cref="ArgumentException">The ratio and shaft angle put a pitch cone at
    /// or past 90° — an internal or crown (face) gear, which this factory does not
    /// model.</exception>
    public BevelPair(int pinionTeeth, int wheelTeeth, double shaftAngleDegrees = 90)
    {
        if (pinionTeeth < 3)
            throw new ArgumentOutOfRangeException(nameof(pinionTeeth), "A gear needs at least 3 teeth.");
        if (wheelTeeth < 3)
            throw new ArgumentOutOfRangeException(nameof(wheelTeeth), "A gear needs at least 3 teeth.");
        if (!(shaftAngleDegrees > 0) || !(shaftAngleDegrees < 180))
            throw new ArgumentOutOfRangeException(nameof(shaftAngleDegrees),
                "Shaft angle must lie strictly between 0 and 180 degrees.");

        PinionTeeth = pinionTeeth;
        WheelTeeth = wheelTeeth;
        ShaftAngleDegrees = shaftAngleDegrees;

        double sigma = shaftAngleDegrees * Math.PI / 180;
        // tan d1 = sin S / (z2/z1 + cos S). Atan2 rather than Atan so a NEGATIVE
        // denominator (a shaft angle past 90 deg with a wheel smaller than the pinion)
        // lands in the second quadrant instead of folding onto the first.
        double delta1 = Math.Atan2(Math.Sin(sigma), (double)wheelTeeth / pinionTeeth + Math.Cos(sigma));
        double delta2 = sigma - delta1;
        if (!(delta1 > 0) || !(delta1 < Math.PI / 2) || !(delta2 > 0) || !(delta2 < Math.PI / 2))
            throw new ArgumentException(
                $"z1={pinionTeeth}/z2={wheelTeeth} at a {shaftAngleDegrees:0.###} deg shaft angle puts a pitch "
                + $"cone at {Math.Max(delta1, delta2) * 180 / Math.PI:0.###} deg. A cone angle of 90 deg is a "
                + "crown (face) gear and past it an internal bevel; neither is a cone this factory can loft "
                + "toward an apex. Reduce the shaft angle or the ratio.", nameof(shaftAngleDegrees));

        PinionConeAngleDegrees = delta1 * 180 / Math.PI;
        WheelConeAngleDegrees = delta2 * 180 / Math.PI;
    }

    /// <summary>Tooth count of the member whose cone angle is <see cref="PinionConeAngleDegrees"/>.</summary>
    public int PinionTeeth { get; }

    /// <summary>Tooth count of the member whose cone angle is <see cref="WheelConeAngleDegrees"/>.</summary>
    public int WheelTeeth { get; }

    /// <summary>Angle between the two shaft axes, degrees (90° is the ordinary case).</summary>
    public double ShaftAngleDegrees { get; }

    /// <summary>Pitch cone angle δ₁ of the pinion, degrees.</summary>
    public double PinionConeAngleDegrees { get; }

    /// <summary>Pitch cone angle δ₂ of the wheel, degrees. <c>δ₁ + δ₂ =</c>
    /// <see cref="ShaftAngleDegrees"/> exactly.</summary>
    public double WheelConeAngleDegrees { get; }

    /// <summary>Speed ratio z₂/z₁ (the pinion turns this many times per wheel turn).</summary>
    public double Ratio => (double)WheelTeeth / PinionTeeth;

    /// <summary>Outer cone distance R_e = m·z₁/(2·sin δ₁) — shared by both members, so
    /// either tooth count gives the same number.</summary>
    public double OuterConeDistance(double module) =>
        module * PinionTeeth / (2 * Math.Sin(PinionConeAngleDegrees * Math.PI / 180));

    /// <summary>
    /// The rotation the named member needs about its OWN axis to mesh, radians — the
    /// <see cref="GearMeshing"/> tooth-phasing rule solved for a pair rolling on its
    /// pitch CONES rather than on a line of centres.
    /// </summary>
    /// <remarks>
    /// <para><b>The mounting convention the phase means anything under.</b> The OTHER
    /// member sits as drawn — apex at the origin, axis on +Z, one tooth centred on +X
    /// (the <see cref="BevelGears.Straight"/> datum, measured off the section by test
    /// rather than assumed) — turned about its own axis by
    /// <paramref name="otherPhase"/>; THIS member, drawn the same way, is turned by the
    /// returned phase about its own +Z and then tilted by the MINIMAL rotation taking
    /// +Z to the mounted axis (polar angle = <see cref="ShaftAngleDegrees"/>, azimuth
    /// <paramref name="azimuth"/> from +X). At azimuth π/2 on a 90° pair that tilt is
    /// <c>RotateX(−π/2)</c>, the docs example's own placement.</para>
    /// <para><b>The derivation is spherical rolling pulled back through that tilt, and
    /// it lands on the parallel-axis EXTERNAL rule.</b> Both pitch cones share the apex
    /// and roll along the common element, which sits at azimuth ψ in the fixed member's
    /// frame and — a property of the minimal rotation, for EVERY shaft angle Σ — at
    /// azimuth ψ + π in the tilted member's own frame (Rodrigues about n̂ = ẑ×ŵ takes
    /// the element to (−sin δ₂·cos ψ, −sin δ₂·sin ψ, cos δ₂), the Σ-dependence
    /// cancelling). Rolling ties the spins through the pitch radii r_i = R·sin δ_i, so
    /// in the members' OWN frames ω₂ = −(z₁/z₂)·ω₁ — the pair counter-rotates exactly
    /// as an external spur pair does — hence u₁(ψ) + u₂(ψ+π) is the rolling invariant
    /// and the mesh condition is the external rule's own <c>u₁ + u₂ ≡ ½ (mod 1)</c>.
    /// So the SHAFT ANGLE decides the cone angles and never the phase, and this method
    /// DELEGATES to <see cref="GearMeshing.ExternalPhase"/> bit-identically (the
    /// <c>PlanetPhase</c> convention: one derivation, asked rather than restated).</para>
    /// <para>The phase returned is the SYMMETRIC zero-backlash one (a tooth centred in
    /// the space at the contact element) and is deliberately NOT wrapped, the
    /// <see cref="GearMesh"/> conventions; which flank drives is the caller's, via a
    /// further roll at the tooth ratio. Both members must share a module and pressure
    /// angle for the pair to mesh at all — the phase rule itself depends only on the
    /// tooth counts and the drawing datum, which is why nothing here asks for a
    /// module.</para>
    /// </remarks>
    /// <param name="member">Which member is being PLACED (spun, then tilted).</param>
    /// <param name="azimuth">Azimuth of the mounted axis from +X, radians (π/2 puts the
    /// placed member's axis toward +Y, the ordinary layout).</param>
    /// <param name="otherPhase">How far the FIXED member is turned from its own drawing
    /// datum (0 = as drawn, a tooth centred on +X).</param>
    /// <exception cref="ArgumentOutOfRangeException">An unknown member.</exception>
    public double PhaseFor(BevelMember member, double azimuth = 0, double otherPhase = 0) => member switch
    {
        BevelMember.Pinion => GearMeshing.ExternalPhase(WheelTeeth, PinionTeeth, azimuth, otherPhase),
        BevelMember.Wheel => GearMeshing.ExternalPhase(PinionTeeth, WheelTeeth, azimuth, otherPhase),
        _ => throw new ArgumentOutOfRangeException(nameof(member), member,
            "A bevel pair has two members: BevelMember.Pinion and BevelMember.Wheel."),
    };
}

/// <summary>Names one member of a <see cref="BevelPair"/> — the argument
/// <see cref="BevelPair.PhaseFor"/> takes to say which member is being placed.</summary>
public enum BevelMember
{
    /// <summary>The member with <see cref="BevelPair.PinionTeeth"/> teeth.</summary>
    Pinion,

    /// <summary>The member with <see cref="BevelPair.WheelTeeth"/> teeth.</summary>
    Wheel,
}

/// <summary>
/// A generated straight bevel gear: the heel cross-section as a <see cref="Sketch"/> plus
/// every derived cone dimension and the measured fit deviation.
/// </summary>
/// <remarks>
/// <para>The section is the gear's planar cross-section at the plane through the OUTER
/// pitch circle. The solid is the cone from the pitch apex over that section, truncated
/// between the toe and heel planes — so <see cref="ToeScale"/> is the only other number
/// a loft needs, and every section between the two ends is this one scaled about the
/// apex.</para>
/// </remarks>
public sealed class BevelGearProfile
{
    internal BevelGearProfile(
        GearSpec spec, double pitchConeAngleDegrees, double faceWidth, Sketch sketch,
        double fitTolerance, double maxFitDeviation, int curvesPerFlank,
        double outerConeDistance, double backConeDistance, double virtualTeeth,
        double faceConeAngleDegrees, double rootConeAngleDegrees,
        double heelPlaneZ, double toeScale,
        double sectionTipRadius, double sectionRootRadius)
    {
        Spec = spec;
        PitchConeAngleDegrees = pitchConeAngleDegrees;
        FaceWidth = faceWidth;
        Sketch = sketch;
        FitTolerance = fitTolerance;
        MaxFitDeviation = maxFitDeviation;
        CurvesPerFlank = curvesPerFlank;
        OuterConeDistance = outerConeDistance;
        BackConeDistance = backConeDistance;
        VirtualTeeth = virtualTeeth;
        FaceConeAngleDegrees = faceConeAngleDegrees;
        RootConeAngleDegrees = rootConeAngleDegrees;
        HeelPlaneZ = heelPlaneZ;
        ToeScale = toeScale;
        SectionTipRadius = sectionTipRadius;
        SectionRootRadius = sectionRootRadius;
    }

    /// <summary>The definition this gear was generated from (module, teeth, pressure
    /// angle and profile shift are the OUTER, back-cone values, as a bevel is dimensioned).</summary>
    public GearSpec Spec { get; }

    /// <summary>Pitch cone angle δ, degrees.</summary>
    public double PitchConeAngleDegrees { get; }

    /// <summary>Face width b, measured along the pitch cone element.</summary>
    public double FaceWidth { get; }

    /// <summary>The heel cross-section as a closed sketch (CCW, no holes), centred on the
    /// axis with one tooth centred on +X — the section in the plane z =
    /// <see cref="HeelPlaneZ"/>, with the pitch apex at the origin and the axis on +Z.</summary>
    public Sketch Sketch { get; }

    /// <summary>The flank fit tolerance that was requested (mm).</summary>
    public double FitTolerance { get; }

    /// <summary>Measured maximum deviation of the fitted flank from the closed-form
    /// projected back-cone involute (mm) — at most <see cref="FitTolerance"/>. This is the
    /// FIT's error only; the modelling error of the back-cone approximation itself is a
    /// property of the method and is quoted in the remarks on <see cref="BevelGears"/>.</summary>
    public double MaxFitDeviation { get; }

    /// <summary>Number of curves the biarc chain spent on one flank.</summary>
    public int CurvesPerFlank { get; }

    /// <summary>Outer (heel) cone distance R_e = d/(2·sin δ).</summary>
    public double OuterConeDistance { get; }

    /// <summary>Inner (toe) cone distance R_i = R_e − b.</summary>
    public double InnerConeDistance => OuterConeDistance - FaceWidth;

    /// <summary>Back cone distance r_v = R_e·tan δ = r/cos δ — the virtual spur gear's
    /// pitch radius.</summary>
    public double BackConeDistance { get; }

    /// <summary>Virtual (Tredgold) tooth count z_v = z/cos δ. Generally NOT an integer,
    /// which is exactly why the virtual gear is a construction and not a gear.</summary>
    public double VirtualTeeth { get; }

    /// <summary>Face cone angle δ_a = δ + arctan(h_a/R_e), degrees.</summary>
    public double FaceConeAngleDegrees { get; }

    /// <summary>Root cone angle δ_f = δ − arctan(h_f/R_e), degrees.</summary>
    public double RootConeAngleDegrees { get; }

    /// <summary>Axial position of the heel section plane, R_e·cos δ (apex at the origin,
    /// axis along +Z).</summary>
    public double HeelPlaneZ { get; }

    /// <summary>Axial position of the toe section plane, R_i·cos δ.</summary>
    public double ToePlaneZ => HeelPlaneZ * ToeScale;

    /// <summary>R_i/R_e — the uniform scale, about the apex, taking the heel section to
    /// the toe section.</summary>
    public double ToeScale { get; }

    /// <summary>Tip radius of the HEEL SECTION, z_heel·tan δ_a. Larger than the standard
    /// outside radius <see cref="BackConeTipRadius"/> because the end face is a plane
    /// rather than the back cone — see the remarks on <see cref="BevelGears"/>.</summary>
    public double SectionTipRadius { get; }

    /// <summary>Root radius of the heel section, z_heel·tan δ_f.</summary>
    public double SectionRootRadius { get; }

    /// <summary>The standard outside radius d_ae/2 = r + h_a·cos δ, i.e. the tip measured
    /// where a real bevel's tooth ends — ON the back cone, not in the heel plane.</summary>
    public double BackConeTipRadius =>
        Spec.PitchDiameter / 2
        + Spec.Module * (Spec.AddendumCoefficient + Spec.ProfileShift)
            * Math.Cos(PitchConeAngleDegrees * Math.PI / 180);
}

/// <summary>
/// Straight bevel gears by <b>Tredgold's back-cone approximation</b>, stated as such.
/// Spiral bevel and hypoid gears are refused BY NAME (see <see cref="Spiral"/> and
/// <see cref="Hypoid"/>).
/// </summary>
/// <remarks>
/// <para><b>The construction.</b> Every straight bevel tooth flank is a ruled surface
/// through the pitch apex, so the solid is exactly a loft between one planar section and
/// its uniform scale about that apex — that half is not an approximation at all, and it
/// is why <see cref="BevelGearProfile.ToeScale"/> is a single number. What IS approximate
/// is the section. Tredgold replaces the pitch sphere by the tangent BACK CONE, whose
/// development is an ordinary spur gear of z_v = z/cos δ teeth at the same module (the
/// <i>virtual</i> gear; note z_v is not generally an integer, which is why it is a
/// construction rather than a gear one could build). This factory wraps that development
/// onto the back cone and projects it CENTRALLY from the pitch apex onto the section
/// plane, which is what a ruled-to-the-apex tooth does by definition.</para>
/// <para><b>Why central and not axial projection.</b> The central projection reproduces
/// the standard bevel cone angles as an identity: face cone δ_a = δ + arctan(h_a/R_e) and
/// root cone δ_f = δ − arctan(h_f/R_e) come out to 3e-16 rad (asserted). Projecting
/// along the AXIS instead — the obvious cheap alternative, and the one that reproduces
/// the textbook d_ae = d + 2h_a·cos δ directly — gives a flank whose trace on the back
/// cone is NOT the Tredgold curve, and measures 5e-3 … 3.7e-2 module away from the true
/// spherical involute against 7e-4 … 2.9e-2 for this one.</para>
/// <para><b>The error, measured and ranked.</b> Against the exact spherical involute
/// (the theoretically conjugate bevel tooth), the back-cone approximation itself measures
/// <b>7e-4 … 2.9e-2 module</b> over a family of nine gears spanning m 1–5, z 12–60 and
/// δ 20°–70°. The biarc fit of the flank defaults to module·1e-4 and reports what it
/// achieved on <see cref="BevelGearProfile.MaxFitDeviation"/>, so the fit sits about two
/// orders BELOW the method's own error and is never the limiting term. Note also that
/// production straight bevels are generated OCTOID, not spherical-involute, so the
/// spherical involute is a reference rather than the manufactured truth. Treat these as
/// modelling-grade teeth: right pitch, right cone angles, right pressure angle at the
/// pitch point, sound for kinematics, assembly, casting patterns and printing — not a
/// substitute for a generated flank on a ground gear.</para>
/// <para><b>Flat ends.</b> The two end faces are PLANES perpendicular to the axis, not the
/// back and front cones, because a loft section must be planar. The teeth themselves are
/// the correct cones, so every angle and the pitch diameter are exact; only the trim
/// differs. The consequence is reported rather than hidden:
/// <see cref="BevelGearProfile.SectionTipRadius"/> is what the model measures across the
/// heel and <see cref="BevelGearProfile.BackConeTipRadius"/> is the standard d_ae/2, and
/// they separate as δ grows.</para>
/// </remarks>
public static class BevelGears
{
    /// <summary>
    /// Generates the straight bevel gear's heel cross-section and every derived cone
    /// dimension. The pitch apex is at the origin with the axis along +Z.
    /// </summary>
    /// <param name="spec">Module, tooth count, pressure angle, profile shift and rack
    /// coefficients — all OUTER (back-cone) values, as a bevel is dimensioned.</param>
    /// <param name="pitchConeAngleDegrees">Pitch cone angle δ, strictly between 0° and
    /// 90°. Get it from a <see cref="BevelPair"/>.</param>
    /// <param name="faceWidth">Face width b along the pitch cone element; must be less
    /// than the outer cone distance. Practice keeps b ≤ R_e/3 and b ≤ 10·m — not enforced,
    /// because it is a strength convention rather than a geometric limit.</param>
    /// <param name="fitTolerance">Maximum deviation of the fitted flank from the
    /// closed-form projected involute (mm); defaults to module·1e-4.</param>
    /// <exception cref="ArgumentException">Refused by name: a virtual tooth count below
    /// the rack undercut limit — and because z_v = z/cos δ is LARGER than z, a bevel
    /// tolerates FEWER real teeth than a spur gear at the same pressure angle (at
    /// δ = 45° and α = 20° the limit falls from 18 teeth to 13) — a face cone angle
    /// reaching 90°, a pointed tooth, a root fillet that does not fit its gap, and
    /// degenerate radii.</exception>
    public static BevelGearProfile Straight(
        GearSpec spec, double pitchConeAngleDegrees, double faceWidth, double? fitTolerance = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!(pitchConeAngleDegrees > 0) || !(pitchConeAngleDegrees < 90))
            throw new ArgumentOutOfRangeException(nameof(pitchConeAngleDegrees),
                "Pitch cone angle must lie strictly between 0 and 90 degrees (90 degrees is a crown gear).");
        if (!(faceWidth > 0))
            throw new ArgumentOutOfRangeException(nameof(faceWidth), "Face width must be positive.");
        double tolerance = fitTolerance ?? spec.Module * 1e-4;
        if (!(tolerance > 0))
            throw new ArgumentOutOfRangeException(nameof(fitTolerance));
        if (!(spec.AddendumCoefficient > 0))
            throw new ArgumentException("Addendum coefficient must be positive.", nameof(spec));
        if (!(spec.DedendumCoefficient > 0))
            throw new ArgumentException("Dedendum coefficient must be positive.", nameof(spec));
        if (spec.RootFilletCoefficient < 0)
            throw new ArgumentException("Root fillet coefficient cannot be negative.", nameof(spec));

        double alpha = spec.PressureAngleRadians;
        double m = spec.Module;
        int z = spec.Teeth;
        double x = spec.ProfileShift;
        double delta = pitchConeAngleDegrees * Math.PI / 180;
        double cosD = Math.Cos(delta), sinD = Math.Sin(delta);

        double r = m * z / 2;                 // outer pitch radius
        double coneDistance = r / sinD;       // R_e
        if (!(faceWidth < coneDistance))
            throw new ArgumentOutOfRangeException(nameof(faceWidth),
                $"Face width {faceWidth:0.###} reaches the pitch apex: the outer cone distance is "
                + $"{coneDistance:0.###}. A bevel's teeth stop short of the apex.");

        // ---- the virtual (Tredgold) spur gear on the back cone development ----
        double rv = r / cosD;                 // back cone distance = virtual pitch radius
        double zv = z / cosD;                 // virtual tooth count (not an integer)
        double sinAlpha = Math.Sin(alpha);
        if (zv * sinAlpha * sinAlpha / 2 + x < spec.AddendumCoefficient)
            throw new ArgumentException(
                $"A z={z} bevel at delta={pitchConeAngleDegrees:0.###} deg has a VIRTUAL tooth count "
                + $"z_v = z/cos(delta) = {zv:0.##}, which is undercut: the rack limit is "
                + $"z_v,min = 2(h_a* - x)/sin^2(a) = {2 * (spec.AddendumCoefficient - x) / (sinAlpha * sinAlpha):0.##}. "
                + "Undercut on a bevel is decided by the VIRTUAL gear, and z_v > z, so a bevel tolerates "
                + $"FEWER real teeth than a spur gear does: the limit here is z = z_v,min*cos(delta) = "
                + $"{2 * (spec.AddendumCoefficient - x) / (sinAlpha * sinAlpha) * cosD:0.##}. "
                + "Add teeth, raise the pressure angle, or increase the profile shift.",
                nameof(spec));

        double rbv = rv * Math.Cos(alpha);
        double rav = rv + m * (spec.AddendumCoefficient + x);
        double rfv = rv - m * (spec.DedendumCoefficient - x);
        if (!(rfv > 0))
            throw new ArgumentException(
                $"The virtual gear's root radius {rfv:0.###} is not positive.", nameof(spec));
        if (!(rav > rbv))
            throw new ArgumentException(
                $"The virtual gear's tip radius {rav:0.###} does not clear its base radius {rbv:0.###} - "
                + "there is no involute to draw.", nameof(spec));

        // ---- the projection: back cone -> heel plane, centrally from the pitch apex ----
        // A back-cone point at slant rho from the BACK cone's apex sits at cylindrical
        // radius rho*cos(d) and axial (R_e/cos d - rho*sin d); projecting it from the
        // PITCH apex onto z = R_e*cos(d) leaves the azimuth alone and takes the radius to
        //     R(w) = r * w * cos^2(d) / (1 - w * sin^2(d)),   w = rho / r_v,
        // which is 1:1 at the pitch circle (w = 1 -> R = r) and has a POLE at
        // w = 1/sin^2(d) - the face cone reaching 90 degrees, refused below.
        double heelZ = coneDistance * cosD;
        double sin2 = sinD * sinD, cos2 = cosD * cosD;
        double wTip = rav / rv, wRoot = rfv / rv, wBase = rbv / rv;
        if (!(wTip * sin2 < 1))
            throw new ArgumentException(
                $"The face cone reaches 90 degrees: at delta={pitchConeAngleDegrees:0.###} deg the tip's "
                + $"back-cone position w={wTip:0.####} meets the projection's pole at 1/sin^2(delta)="
                + $"{1 / sin2:0.####}. The addendum would project to infinity. Reduce the cone angle, the "
                + "addendum or the profile shift.", nameof(spec));

        double Radius(double w) => r * w * cos2 / (1 - w * sin2);
        double RadiusSlope(double w) => r * cos2 / ((1 - w * sin2) * (1 - w * sin2));

        double tipRadius = Radius(wTip), rootRadius = Radius(wRoot);
        if (!(tipRadius > rootRadius))
            throw new ArgumentException(
                $"The section's tip radius {tipRadius:0.###} does not clear its root radius "
                + $"{rootRadius:0.###}.", nameof(spec));

        // ---- the canonical tooth, right side, root -> tip ----
        double invAlpha = Math.Tan(alpha) - alpha;
        double psiV = (Math.PI / 2 + 2 * x * Math.Tan(alpha)) / zv;   // half tooth ANGLE, virtual plane
        double theta0 = -psiV - invAlpha;                             // involute origin ray
        double tTip = Math.Sqrt(rav * rav / (rbv * rbv) - 1);

        // The mapped flank and its EXACT tangent (chain rule through the map; nothing
        // finite-differenced - the weld-tier rule).
        Vector2d Flank(double t)
        {
            double rho = rbv * Math.Sqrt(1 + t * t);
            double theta = (theta0 + t - Math.Atan(t)) / cosD;
            double rad = Radius(rho / rv);
            return new Vector2d(rad * Math.Cos(theta), rad * Math.Sin(theta));
        }
        // The UNIT tangent, with the involute's base-circle cusp removed ANALYTICALLY.
        // dQ/dt = (dR/dt)*e_r + R*(dTheta/dt)*e_theta, and BOTH components carry a factor
        // of t (dR/dt = t*A, R*dTheta/dt = t*B), so the speed vanishes at t = 0 and a
        // normalized derivative would be 0/0 exactly where the fit starts. Dividing the
        // common t out first leaves A*e_r + B*e_theta, which is smooth through t = 0 and
        // gives the cusp's RADIAL tangent there - the same direction Gears.Spur's
        // closed-form involute tangent reports, so the two flanks join the same way.
        Vector2d FlankTangent(double t)
        {
            double s = Math.Sqrt(1 + t * t);
            double w = rbv * s / rv;
            double theta = (theta0 + t - Math.Atan(t)) / cosD;
            double a = RadiusSlope(w) * rbv / (s * rv);               // (dR/dt) / t
            double b = Radius(w) * t / ((1 + t * t) * cosD);          // R*(dTheta/dt) / t
            double cos = Math.Cos(theta), sin = Math.Sin(theta);
            var direction = new Vector2d(a * cos - b * sin, a * sin + b * cos);
            return direction / direction.Length;
        }

        // The root fillet is a RELIEF, not a conjugate surface, and a circle does not map
        // to a circle here, so it is CONSTRUCTED in the section rather than mapped - at
        // the basic-rack radius rho_f* * m, unscaled. Carrying it through the map's local
        // stretch instead was tried and is wrong: the map is anisotropic (radial and
        // tangential lengths scale differently, by a factor of 2 at delta = 63 deg), so
        // there is no single stretch to carry, and using the radial one inflated the
        // fillet until adjacent ones overlapped on ordinary gears (m2 z30 d45, m1 z60 d45).
        // The rack coefficient is a fraction of the MODULE, which is a property of the
        // gear and not of the section's stretched coordinates.
        double rho_f = m * spec.RootFilletCoefficient;

        // Fillet tangency on the flank, if it falls above the base circle. |C(t)| is
        // monotone in t (the flank runs outward), so a sign change over [0, tTip]
        // brackets it; g(0) > 0 means the tangency would sit below the base circle and
        // the flank continues RADIALLY down to it (a radial ray maps to a radial ray, so
        // the joint stays tangent-continuous exactly as in the spur case).
        Vector2d FilletCentre(double t)
        {
            var p = Flank(t);
            var tangent = FlankTangent(t);
            var n = new Vector2d(tangent.Y, -tangent.X);
            n /= n.Length;
            // The gap is on the more-negative-azimuth side of the right flank; pick the
            // normal by MEASUREMENT rather than by re-deriving a sign convention.
            var plus = p + n * rho_f;
            var minus = p - n * rho_f;
            return Angle(plus) < Angle(minus) ? plus : minus;
        }
        double G(double t) => FilletCentre(t).Length - (rootRadius + rho_f);

        bool filletOnFlank = rho_f > 0 && G(0) <= 0 && G(tTip) > 0;
        double tLow = 0;
        if (filletOnFlank)
        {
            double lo = 0, hi = tTip;
            for (int i = 0; i < 200; i++)
            {
                double mid = (lo + hi) / 2;
                if (G(mid) < 0) lo = mid; else hi = mid;
            }
            tLow = (lo + hi) / 2;
        }
        if (tLow >= tTip)
            throw new ArgumentException(
                $"The root fillet (rho_f* = {spec.RootFilletCoefficient}) consumes the whole flank: its "
                + $"tangency at roll {tLow:0.###} is past the tip at roll {tTip:0.###}.", nameof(spec));

        var right = new List<Curve2d>();
        Vector2d filletCentre, filletRootPoint, filletFlankPoint;
        double baseAzimuth = theta0 / cosD;    // the flank's radial stretch runs along this ray
        if (filletOnFlank)
        {
            filletCentre = FilletCentre(tLow);
            filletFlankPoint = Flank(tLow);
            filletRootPoint = filletCentre * (rootRadius / filletCentre.Length);
        }
        else
        {
            // Tangent to the radial stretch: centre at rootRadius + rho_f from the origin,
            // perpendicular distance rho_f from the ray, on the gap side.
            double gamma = rho_f > 0 ? Math.Asin(Math.Min(1, rho_f / (rootRadius + rho_f))) : 0;
            double thetaC = baseAzimuth - gamma;
            filletCentre = new Vector2d(Math.Cos(thetaC), Math.Sin(thetaC)) * (rootRadius + rho_f);
            filletRootPoint = new Vector2d(Math.Cos(thetaC), Math.Sin(thetaC)) * rootRadius;
            double along = Math.Sqrt(Math.Max(0,
                (rootRadius + rho_f) * (rootRadius + rho_f) - rho_f * rho_f));
            filletFlankPoint = new Vector2d(Math.Cos(baseAzimuth), Math.Sin(baseAzimuth)) * along;
        }

        double filletSweep = 0;
        if (rho_f > 1e-9)   // weld tier: a fillet below weld tolerance is no segment at all
        {
            var fillet = ArcBetween(filletCentre, rho_f, filletRootPoint, filletFlankPoint);
            filletSweep = fillet.SweepAngle;
            right.Add(fillet);
        }
        if (!filletOnFlank)
        {
            var basePoint = new Vector2d(Math.Cos(baseAzimuth), Math.Sin(baseAzimuth)) * Radius(wBase);
            if (Radius(wBase) - filletFlankPoint.Length > 1e-9)
                right.Add(new Line2d(filletFlankPoint, basePoint));
        }

        var flankCurves = FitFlank(Flank, FlankTangent, tLow, tTip, tolerance, out double deviation);
        right.AddRange(flankCurves);

        // Tip arc: an origin-centred arc maps to an origin-centred arc EXACTLY (constant
        // radius, azimuth scaled), so nothing is fitted here.
        double tipAzimuth = (theta0 + tTip - Math.Atan(tTip)) / cosD;
        double tipHalfAngle = -tipAzimuth;
        double tipThickness = 2 * tipHalfAngle * tipRadius;
        if (tipThickness < 1e-9)
            throw new ArgumentException(
                $"The tooth comes to a point: tip thickness {tipThickness:0.###e0} in the heel section. "
                + "Reduce the profile shift or the addendum, or add teeth.", nameof(spec));
        var tipArc = new Arc2d(Vector2d.Zero, tipRadius, tipAzimuth, 2 * tipHalfAngle);

        // Left side: the arithmetic mirror of the right, traversed tip -> root.
        var left = new List<Curve2d>(right.Count);
        for (int i = right.Count - 1; i >= 0; i--)
            left.Add(ReverseCurve(MirrorX(right[i])));

        double rootPointAngle = -Angle(filletRootPoint);
        double pitchAngle = 2 * Math.PI / z;
        double rootSweep = pitchAngle - 2 * rootPointAngle;
        double rootLength = rootSweep * rootRadius;
        if (rootLength < -1e-9)
            throw new ArgumentException(
                $"The root fillet (rho_f* = {spec.RootFilletCoefficient}) does not fit the root gap at "
                + $"delta = {pitchConeAngleDegrees:0.###} deg: adjacent fillets overlap by {-rootSweep:0.###} rad "
                + $"at the root circle. The heel SECTION's tooth is {(tipRadius - rootRadius) / (m * (spec.AddendumCoefficient + spec.DedendumCoefficient)):0.#}x "
                + "as deep as the real back-cone tooth, because the end face is a plane rather than the back "
                + "cone and the face and root cones spread apart between them - so a steep cone leaves a "
                + "narrow root gap under a deep tooth. Reduce RootFilletCoefficient (0.30 reaches 75 deg and "
                + "0.20 reaches 80 deg, where the ISO 53 profile-A 0.38 stops near 68 deg), or reduce the "
                + "cone angle. At a 90 deg shaft angle only the WHEEL is ever this steep: a 3:1 pair puts it "
                + "at 71.6 deg and a 4:1 pair at 76.0 deg.", nameof(spec));
        Arc2d? rootArc = rootLength < 1e-9
            ? null
            : new Arc2d(Vector2d.Zero, rootRadius, rootPointAngle, rootSweep);

        var tooth = new List<Curve2d>(right.Count + left.Count + 2);
        tooth.AddRange(right);
        tooth.Add(tipArc);
        tooth.AddRange(left);
        if (rootArc is not null)
            tooth.Add(rootArc);

        var outline = new List<Curve2d>(tooth.Count * z);
        for (int k = 0; k < z; k++)
        {
            double beta = k * pitchAngle;
            double cos = Math.Cos(beta), sin = Math.Sin(beta);
            foreach (var piece in tooth)
                outline.Add(Rotate(piece, cos, sin, beta));
        }

        double faceCone = delta + Math.Atan(m * (spec.AddendumCoefficient + x) / coneDistance);
        double rootCone = delta - Math.Atan(m * (spec.DedendumCoefficient - x) / coneDistance);
        return new BevelGearProfile(
            spec, pitchConeAngleDegrees, faceWidth, Sketch.FromCurves(outline),
            tolerance, deviation, flankCurves.Count,
            coneDistance, rv, zv,
            faceCone * 180 / Math.PI, rootCone * 180 / Math.PI,
            heelZ, (coneDistance - faceWidth) / coneDistance,
            tipRadius, rootRadius);
    }

    /// <summary>
    /// A straight bevel gear solid: the <see cref="Straight"/> heel section lofted to its
    /// own scale about the pitch apex, with an optional plain bore. The apex sits at the
    /// origin and the axis on +Z, so a pair assembles by posing each member's apex at the
    /// shared one.
    /// </summary>
    /// <remarks>Exact in all three representations — the section is lines and circular
    /// arcs, and a two-section ruled loft is B-Rep-Native.</remarks>
    public static Shape StraightGear(
        GearSpec spec, double pitchConeAngleDegrees, double faceWidth,
        double boreDiameter = 0, double? fitTolerance = null)
    {
        var profile = Straight(spec, pitchConeAngleDegrees, faceWidth, fitTolerance);
        return Solid(profile, boreDiameter);
    }

    /// <summary>Builds the solid for an already-generated <paramref name="profile"/> —
    /// the two-call spelling, for callers that want the dimensions first.</summary>
    public static Shape Solid(BevelGearProfile profile, double boreDiameter = 0)
    {
        ArgumentNullException.ThrowIfNull(profile);
        double k = profile.ToeScale;
        var heel = profile.Sketch;
        var toe = Sketch.FromCurves([.. heel.ToCurves().Select(c => ScaleCurve(c, k))]);

        var gear = Shape.Loft(
            [
                (toe, SketchPlane.At((0, 0, profile.ToePlaneZ), Vector3d.UnitX, Vector3d.UnitY)),
                (heel, SketchPlane.At((0, 0, profile.HeelPlaneZ), Vector3d.UnitX, Vector3d.UnitY)),
            ],
            LoftStyle.Ruled);

        if (boreDiameter <= 0)
            return gear;
        double toeRoot = profile.SectionRootRadius * k;
        if (boreDiameter / 2 >= toeRoot)
            throw new ArgumentOutOfRangeException(nameof(boreDiameter),
                $"Bore radius {boreDiameter / 2:0.###} reaches the toe root circle (radius {toeRoot:0.###}), "
                + "the narrowest material the gear has.");
        double height = profile.HeelPlaneZ - profile.ToePlaneZ;
        double mid = (profile.HeelPlaneZ + profile.ToePlaneZ) / 2;
        // Overshoot both ends so the bore never ends coplanar with a face (the Drill rule).
        var bore = Shape.Cylinder(boreDiameter / 2, height * 1.2).Translate(0, 0, mid);
        return gear - bore;
    }

    /// <summary>
    /// <b>Refused by name.</b> Spiral bevel geometry is machine-tool kinematics — the
    /// flank is the envelope swept by a face-mill or face-hob cutter on a generating
    /// machine, with the spiral angle, cutter radius and machine settings all entering
    /// the surface — not a closed-form profile that can be fitted. Transcribing a
    /// published approximation would be a guess wearing a standard's name.
    /// </summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public static Shape Spiral(GearSpec spec, double pitchConeAngleDegrees, double faceWidth,
        double spiralAngleDegrees) =>
        throw new NotSupportedException(
            "Spiral bevel gears are not modelled, deliberately. A spiral bevel flank is the envelope of a "
            + "face-mill or face-hob cutter under a generating machine's motion (cutter radius, machine "
            + "centre-to-back, root angle and roll ratio all enter it) rather than a closed-form curve this "
            + "kernel could fit and stand behind. Use BevelGears.StraightGear for a straight bevel at the "
            + "same cone angles, and treat any spiral-bevel model as a supplier deliverable.");

    /// <summary>
    /// <b>Refused by name.</b> A hypoid pair's axes do not intersect, so there is no
    /// common pitch apex and no pitch cone pair — the pitch surfaces are hyperboloids and
    /// the tooth form is spiral bevel's, with the same machine-kinematic definition plus
    /// lengthwise sliding.
    /// </summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public static Shape Hypoid(GearSpec spec, double pitchConeAngleDegrees, double faceWidth,
        double offset) =>
        throw new NotSupportedException(
            "Hypoid gears are not modelled, deliberately. Offset axes have no common apex, so the pitch "
            + "surfaces are hyperboloids rather than cones and the whole back-cone construction has nothing "
            + "to stand on; the tooth form is spiral bevel's, which is machine kinematics (see "
            + "BevelGears.Spiral). A zero-offset hypoid IS a spiral bevel.");

    // ------------------------------------------------------------------ fitting

    /// <summary>
    /// Fits a tangent-continuous biarc chain to the projected flank over roll
    /// [<paramref name="tLow"/>, <paramref name="tTip"/>] by recursive bisection, with
    /// EXACT endpoint tangents. <paramref name="deviation"/> is measured against the
    /// closed form at 512 samples after the fit — an independent pass, denser than the
    /// per-span acceptance samples.
    /// </summary>
    private static List<Curve2d> FitFlank(
        Func<double, Vector2d> point, Func<double, Vector2d> tangent,
        double tLow, double tTip, double tolerance, out double deviation)
    {
        var curves = new List<Curve2d>();
        Fit(tLow, tTip, 0);

        double worst = 0;
        for (int i = 0; i <= 512; i++)
        {
            var p = point(tLow + (tTip - tLow) * i / 512);
            double best = double.PositiveInfinity;
            foreach (var curve in curves)
                best = Math.Min(best, curve.DistanceTo(p));
            worst = Math.Max(worst, best);
        }
        deviation = worst;
        return curves;

        void Fit(double a, double b, int depth)
        {
            if (depth > 48)
                throw new InvalidOperationException(
                    "Bevel flank biarc fit did not converge - this is a bug, not a modelling error.");
            if (BiArcFit.TryFit(point(a), tangent(a), point(b), tangent(b), out var biarc)
                == BiArcFitStatus.Success)
            {
                double worstInterior = -1;
                double worstT = (a + b) / 2;
                for (int i = 1; i < 32; i++)
                {
                    double t = a + (b - a) * i / 32;
                    double d = biarc!.DistanceTo(point(t));
                    if (d > worstInterior)
                    {
                        worstInterior = d;
                        worstT = t;
                    }
                }
                if (worstInterior <= tolerance)
                {
                    AddPiece(biarc!.First);
                    AddPiece(biarc.Second);
                    return;
                }
                Fit(a, worstT, depth + 1);
                Fit(worstT, b, depth + 1);
                return;
            }
            double mid = (a + b) / 2;
            Fit(a, mid, depth + 1);
            Fit(mid, b, depth + 1);
        }

        void AddPiece(Curve2d piece)
        {
            double length = piece switch
            {
                Line2d line => (line.End - line.Start).Length,
                Arc2d arc => arc.Length,
                _ => double.PositiveInfinity,
            };
            if (length > 1e-9)
                curves.Add(piece);
        }
    }

    // ------------------------------------------------------------------ curve helpers

    private static double Angle(in Vector2d p) => Math.Atan2(p.Y, p.X);

    private static Arc2d ArcBetween(in Vector2d center, double radius, in Vector2d from, in Vector2d to)
    {
        double a0 = Math.Atan2(from.Y - center.Y, from.X - center.X);
        double a1 = Math.Atan2(to.Y - center.Y, to.X - center.X);
        double sweep = a1 - a0;
        if (sweep > Math.PI) sweep -= 2 * Math.PI;
        else if (sweep < -Math.PI) sweep += 2 * Math.PI;
        return new Arc2d(center, radius, a0, sweep);
    }

    private static Curve2d MirrorX(Curve2d curve) => curve switch
    {
        Line2d line => new Line2d(new(line.Start.X, -line.Start.Y), new(line.End.X, -line.End.Y)),
        Arc2d arc => new Arc2d(new(arc.Center.X, -arc.Center.Y), arc.Radius, -arc.StartAngle, -arc.SweepAngle),
        _ => throw new InvalidOperationException($"Unexpected bevel outline curve {curve.GetType().Name}."),
    };

    private static Curve2d ReverseCurve(Curve2d curve) => curve switch
    {
        Line2d line => new Line2d(line.End, line.Start),
        Arc2d arc => arc.Reversed(),
        _ => throw new InvalidOperationException($"Unexpected bevel outline curve {curve.GetType().Name}."),
    };

    private static Curve2d Rotate(Curve2d curve, double cos, double sin, double angle)
    {
        Vector2d Rot(in Vector2d p) => new(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos);
        return curve switch
        {
            Line2d line => new Line2d(Rot(line.Start), Rot(line.End)),
            Arc2d arc => new Arc2d(Rot(arc.Center), arc.Radius, arc.StartAngle + angle, arc.SweepAngle),
            _ => throw new InvalidOperationException($"Unexpected bevel outline curve {curve.GetType().Name}."),
        };
    }

    /// <summary>Uniform scale about the origin — the toe section is the heel section
    /// scaled, so it is the ARITHMETIC scale of the same curves (identical segment count
    /// by construction, which is what the loft's index correspondence needs) and never a
    /// re-fit at a scaled tolerance.</summary>
    private static Curve2d ScaleCurve(Curve2d curve, double k) => curve switch
    {
        Line2d line => new Line2d(line.Start * k, line.End * k),
        Arc2d arc => new Arc2d(arc.Center * k, arc.Radius * k, arc.StartAngle, arc.SweepAngle),
        _ => throw new InvalidOperationException($"Unexpected bevel outline curve {curve.GetType().Name}."),
    };
}
