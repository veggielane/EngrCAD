using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// One gear placed in mesh with another: where its centre sits, and how far it must be
/// turned about its own axis for its teeth to enter the other's spaces.
/// </summary>
/// <remarks>
/// <para>The <see cref="Phase"/> is a rotation applied to the member BEFORE it is
/// carried out to <see cref="Centre"/> — the spin is a property of the gear, the
/// translation of the shaft that holds it — which is the order
/// <see cref="Place"/>, <see cref="Transform"/> and <see cref="Frame"/> all use.</para>
/// <para>Any whole tooth pitch may be added to the phase without changing the placement,
/// so the value returned is one representative rather than a canonical one; it is
/// deliberately NOT wrapped, so a phase computed across a sweep of azimuths stays
/// continuous.</para>
/// </remarks>
/// <param name="Centre">The member's centre in the other member's frame.</param>
/// <param name="Phase">Rotation about the member's own axis, radians.</param>
public readonly record struct GearMesh(Vector3d Centre, double Phase)
{
    /// <summary>The placement as a matrix: spin about the member's own axis, then carry
    /// it out to its centre.</summary>
    public Matrix4d Transform =>
        Matrix4d.CreateTranslation(Centre) * Matrix4d.CreateRotationZ(Phase);

    /// <summary>The placement as a rigid frame — what an <see cref="Occurrence"/> takes
    /// when the gear's own geometry is drawn about the origin.</summary>
    public Frame3d Frame => Frame3d.FromXY(
        Centre,
        new Vector3d(Math.Cos(Phase), Math.Sin(Phase), 0),
        new Vector3d(-Math.Sin(Phase), Math.Cos(Phase), 0));

    /// <summary>Places a gear drawn about its own axis (the form every factory in
    /// <see cref="Gears"/>, <see cref="CycloidalGears"/> and
    /// <see cref="PlanetaryGears"/> produces).</summary>
    public Shape Place(Shape gear)
    {
        ArgumentNullException.ThrowIfNull(gear);
        return gear.RotateZ(Phase).Translate(Centre);
    }

    /// <summary>
    /// The same placement with the member rolled a further <paramref name="radians"/>
    /// about its own axis — <b>which flank drives is the caller's</b>.
    /// </summary>
    /// <remarks>
    /// The phase a factory returns is the SYMMETRIC one: the tooth sits centred in the
    /// space, which at the standard centre distance and standard proportions is
    /// zero-backlash contact on both flanks at once. A real pair runs with backlash (an
    /// extended centre distance, which an involute pair tolerates exactly, or thinned
    /// teeth) and then touches on one flank only; a positive roll advances this member
    /// counter-clockwise, closing the clearance ahead of its teeth and opening it
    /// behind. Nothing here guesses which side that should be.
    /// </remarks>
    public GearMesh RolledBy(double radians) => this with { Phase = Phase + radians };
}

/// <summary>
/// Where a gear must sit and how far it must be turned to mesh with another — the
/// arithmetic every gear layout otherwise re-derives at the call site.
/// </summary>
/// <remarks>
/// <para><b>The rule depends on the tooth COUNTS and the drawing datum, not on the tooth
/// form.</b> Two gears mesh when the pitch circles roll together and a tooth of one sits
/// in a space of the other; neither statement mentions the flank curve, which is why one
/// helper serves involute, cycloidal and any other profile drawn to the same datum. The
/// datum is stated rather than assumed: <see cref="Gears.Spur"/> and
/// <see cref="CycloidalGears.Spur"/> draw a TOOTH centred on +X, and
/// <see cref="PlanetaryGears.RingProfile"/> draws a tooth SPACE centred on +X (its bore
/// is a cutter gear's outline, and the cutter's tooth is the ring's space).</para>
///
/// <para><b>The derivation.</b> Give a gear of z teeth, turned by φ from a datum whose
/// tooth centre lies at τ, the <i>tooth-index coordinate</i> along a direction θ in its
/// own frame:</para>
/// <code>u(θ) = z·(θ − φ − τ) / 2π</code>
/// <para>which is an integer exactly when a tooth centre lies along θ and a half-integer
/// exactly when a space centre does. For a pair, the engaging directions are the ones
/// pointing at the contact, and the mesh condition is that one shows a tooth where the
/// other shows a space. What makes it a <i>constraint</i> rather than a coincidence is
/// that the combination is invariant under rolling:</para>
/// <list type="bullet">
/// <item><b>External</b> (centres a = m(z₁+z₂)/2 apart, engaging directions ψ from A and
/// ψ+π from B): the members counter-rotate, ω_B = −(z_A/z_B)·ω_A, so u_A(ψ) + u_B(ψ+π)
/// is constant, and the mesh condition is <c>u_A + u_B ≡ ½ (mod 1)</c>.</item>
/// <item><b>Internal</b> (ring R about a pinion P inside it, a = m(z_R−z_P)/2, engaging
/// direction ψ from BOTH — the pinion's engaging tooth points outward, away from the
/// ring's centre): the members co-rotate, ω_P = (z_R/z_P)·ω_R, so u_R(ψ) − u_P(ψ) is
/// constant, and the condition is <c>u_R − u_P ≡ ½ (mod 1)</c>.</item>
/// <item><b>Rack</b> (pitch line y = 0, teeth +Y, a space centre at x = 0 — the
/// <see cref="RackProfile.Sketch"/> datum — pinion centre at (x, r)): rolling ties the
/// pinion's spin to the rack's advance by x = −r·φ, so the pinion's tooth-index
/// coordinate at −π/2 minus x/p is constant, and the condition is <c>≡ 0 (mod 1)</c>,
/// i.e. simply <c>φ = −π/2 − x/r</c>.</item>
/// </list>
///
/// <para><b>External and internal have OPPOSITE dependence on the azimuth</b>, and that
/// is not a sign slip in one of them: an external pair counter-rotates where an internal
/// pair co-rotates, so carrying the driven member round to a different azimuth spins it
/// the other way. The two rules agree modulo one tooth pitch exactly when
/// <c>(z_sun + z_ring)</c> is divisible by the planet count — <b>which is what the
/// planetary assembly condition IS</b>, and why <see cref="PlanetarySet"/> can satisfy
/// both meshes with one number. Away from that condition they genuinely differ, so a
/// caller must ask for the mesh they have.</para>
///
/// <para><b>Helical pairs need nothing extra</b>, and the reason is worth stating: a
/// twisted extrusion's transverse section at height z is the drawn section rotated by
/// ψ(z) = z·tan β / r, so the mesh condition holds at every section simultaneously iff
/// ψ_A·z_A + ψ_B·z_B is constant in z — which, since ψ·z = 2z·tan β / m, is exactly the
/// requirement that the two helix angles be equal and opposite. So phase a correctly
/// paired helical set at its drawn section (z = 0, where both factories put it) and it
/// is phased everywhere.</para>
/// </remarks>
public static class GearMeshing
{
    /// <summary>
    /// The standard centre distance m(z₁ + z₂)/2 of an external pair.
    /// </summary>
    public static double ExternalCentreDistance(GearSpec driver, GearSpec driven)
    {
        RequirePair(driver, driven);
        return driver.Module * (driver.Teeth + driven.Teeth) / 2.0;
    }

    /// <summary>
    /// The centre distance m(z_ring − z_pinion)/2 of an internal pair.
    /// </summary>
    public static double InternalCentreDistance(GearSpec ring, GearSpec pinion)
    {
        RequirePair(ring, pinion);
        if (ring.Teeth <= pinion.Teeth)
            throw new ArgumentException(
                $"An internal mesh needs the ring to have more teeth than the pinion inside it "
                + $"(ring {ring.Teeth}, pinion {pinion.Teeth}).", nameof(pinion));
        return ring.Module * (ring.Teeth - pinion.Teeth) / 2.0;
    }

    /// <summary>
    /// Places <paramref name="driven"/> in external mesh with <paramref name="driver"/>,
    /// which sits at the origin turned by <paramref name="driverPhase"/>.
    /// </summary>
    /// <param name="driver">The gear at the origin.</param>
    /// <param name="driven">The gear being placed.</param>
    /// <param name="azimuth">Direction of the driven member's centre from the driver's,
    /// radians from +X (0 puts it on +X, the ordinary layout).</param>
    /// <param name="driverPhase">How far the driver is turned from its own drawing datum
    /// (0 = as drawn, a tooth centred on +X).</param>
    /// <exception cref="ArgumentException">The two members do not share a tooth system
    /// (module or pressure angle), or their profile shifts do not sum to zero — a shifted
    /// pair runs at an OPERATING centre distance this overload cannot state, so use
    /// <see cref="External(int, int, double, double, double)"/> with that distance.</exception>
    public static GearMesh External(
        GearSpec driver, GearSpec driven, double azimuth = 0, double driverPhase = 0)
    {
        RequirePair(driver, driven);
        RequireBalancedShift(driver, driven);
        return External(
            driver.Teeth, driven.Teeth, ExternalCentreDistance(driver, driven), azimuth, driverPhase);
    }

    /// <summary>
    /// The external-mesh placement from tooth counts and a stated centre distance — the
    /// form for tooth systems <see cref="GearSpec"/> does not describe (cycloidal pairs),
    /// for a profile-shifted pair at its operating centre distance, and for a pair
    /// mounted deliberately long to open backlash.
    /// </summary>
    public static GearMesh External(
        int driverTeeth, int drivenTeeth, double centreDistance,
        double azimuth = 0, double driverPhase = 0)
    {
        RequireTeeth(driverTeeth, nameof(driverTeeth));
        RequireTeeth(drivenTeeth, nameof(drivenTeeth));
        RequireDistance(centreDistance);
        return new GearMesh(
            Polar(centreDistance, azimuth),
            ExternalPhase(driverTeeth, drivenTeeth, azimuth, driverPhase));
    }

    /// <summary>
    /// The rotation the driven member of an EXTERNAL pair needs, in radians:
    /// <c>ψ + π − π/z_driven − (z_driver/z_driven)·(driverPhase − ψ)</c>.
    /// </summary>
    /// <remarks>At the ordinary layout (azimuth 0, driver as drawn) this is the familiar
    /// <c>π − π/z</c> — a half turn to face the driver, less half a tooth pitch to put a
    /// space where the driver shows a tooth.</remarks>
    public static double ExternalPhase(
        int driverTeeth, int drivenTeeth, double azimuth = 0, double driverPhase = 0)
    {
        RequireTeeth(driverTeeth, nameof(driverTeeth));
        RequireTeeth(drivenTeeth, nameof(drivenTeeth));
        return azimuth + Math.PI - Math.PI / drivenTeeth
            - (driverTeeth * (driverPhase - azimuth)) / drivenTeeth;
    }

    /// <summary>
    /// Places <paramref name="pinion"/> in INTERNAL mesh inside <paramref name="ring"/>,
    /// which sits at the origin turned by <paramref name="ringPhase"/>.
    /// </summary>
    /// <param name="ring">The internal gear at the origin. Its phase is measured from
    /// <see cref="PlanetaryGears.RingProfile"/>'s datum — a tooth SPACE on +X.</param>
    /// <param name="pinion">The external gear inside it.</param>
    /// <param name="azimuth">Direction of the pinion's centre from the ring's.</param>
    /// <param name="ringPhase">How far the ring is turned from its own drawing datum.</param>
    public static GearMesh Internal(
        GearSpec ring, GearSpec pinion, double azimuth = 0, double ringPhase = 0)
    {
        RequirePair(ring, pinion);
        RequireBalancedShift(ring, pinion);
        return Internal(
            ring.Teeth, pinion.Teeth, InternalCentreDistance(ring, pinion), azimuth, ringPhase);
    }

    /// <summary>The internal-mesh placement from tooth counts and a stated centre
    /// distance.</summary>
    public static GearMesh Internal(
        int ringTeeth, int pinionTeeth, double centreDistance,
        double azimuth = 0, double ringPhase = 0)
    {
        RequireTeeth(ringTeeth, nameof(ringTeeth));
        RequireTeeth(pinionTeeth, nameof(pinionTeeth));
        RequireDistance(centreDistance);
        return new GearMesh(
            Polar(centreDistance, azimuth),
            InternalPhase(ringTeeth, pinionTeeth, azimuth, ringPhase));
    }

    /// <summary>
    /// The rotation a pinion inside a ring needs, in radians:
    /// <c>ψ − z_ring·(ψ − ringPhase)/z_pinion</c>.
    /// </summary>
    /// <remarks>Note the azimuth's dependence runs the OTHER way from
    /// <see cref="ExternalPhase"/>'s — an internal pair co-rotates where an external pair
    /// counter-rotates. The two agree modulo a tooth pitch exactly under the planetary
    /// assembly condition; see the class remarks.</remarks>
    public static double InternalPhase(
        int ringTeeth, int pinionTeeth, double azimuth = 0, double ringPhase = 0)
    {
        RequireTeeth(ringTeeth, nameof(ringTeeth));
        RequireTeeth(pinionTeeth, nameof(pinionTeeth));
        return azimuth - (ringTeeth * (azimuth - ringPhase)) / pinionTeeth;
    }

    /// <summary>
    /// The rotation the RING needs so that a pinion already placed at
    /// <paramref name="azimuth"/> with <paramref name="pinionPhase"/> meshes with it —
    /// <see cref="InternalPhase"/> solved the other way, which is the same formula with
    /// the two tooth counts swapped.
    /// </summary>
    public static double RingPhase(
        int ringTeeth, int pinionTeeth, double azimuth = 0, double pinionPhase = 0)
    {
        RequireTeeth(ringTeeth, nameof(ringTeeth));
        RequireTeeth(pinionTeeth, nameof(pinionTeeth));
        return azimuth - (pinionTeeth * (azimuth - pinionPhase)) / ringTeeth;
    }

    /// <summary>
    /// Places a pinion in mesh with a rack laid along +X: the pitch line on y = 0, teeth
    /// pointing +Y, and a tooth SPACE centred at x = <paramref name="rackOrigin"/> —
    /// <see cref="RackProfile.Sketch"/>'s own contract, so a bar placed at the origin
    /// needs no offset.
    /// </summary>
    /// <param name="rack">The rack's tooth system.</param>
    /// <param name="pinion">The pinion's; must share the rack's module and pressure
    /// angle.</param>
    /// <param name="x">Where along the rack the pinion's axis sits.</param>
    /// <param name="rackOrigin">x of the rack's own datum space centre.</param>
    /// <remarks>The centre is (x, r_pitch) — a standard rack, its pitch line on the
    /// pinion's pitch circle. A profile-shifted pinion rides higher by x·m, which the
    /// caller applies to the centre; the PHASE is unaffected, since it depends only on
    /// where the rolling has got to.</remarks>
    public static GearMesh Rack(RackSpec rack, GearSpec pinion, double x = 0, double rackOrigin = 0)
    {
        ArgumentNullException.ThrowIfNull(rack);
        ArgumentNullException.ThrowIfNull(pinion);
        if (!Nearly(rack.Module, pinion.Module))
            throw new ArgumentException(
                $"A rack and pinion must share a module (rack {rack.Module:0.####}, "
                + $"pinion {pinion.Module:0.####}).", nameof(pinion));
        if (!Nearly(rack.PressureAngleDegrees, pinion.PressureAngleDegrees))
            throw new ArgumentException(
                $"A rack and pinion must share a pressure angle (rack "
                + $"{rack.PressureAngleDegrees:0.###} deg, pinion {pinion.PressureAngleDegrees:0.###} deg).",
                nameof(pinion));

        double pitchRadius = pinion.PitchDiameter / 2;
        return new GearMesh(
            new Vector3d(x, pitchRadius, 0),
            RackPinionPhase(pitchRadius, x - rackOrigin));
    }

    /// <summary>
    /// The rotation a pinion of pitch radius <paramref name="pitchRadius"/> needs when
    /// its axis sits <paramref name="x"/> along a rack from the rack's own space-centre
    /// datum: <c>−π/2 − x/r</c>.
    /// </summary>
    /// <remarks>The two terms are the whole story: −π/2 turns the drawn tooth from +X to
    /// point down at the rack, and −x/r is how far the pinion has ROLLED to get there.</remarks>
    public static double RackPinionPhase(double pitchRadius, double x)
    {
        if (!(pitchRadius > 0) || !double.IsFinite(pitchRadius))
            throw new ArgumentOutOfRangeException(nameof(pitchRadius),
                "A pinion's pitch radius must be a positive length.");
        return -Math.PI / 2 - x / pitchRadius;
    }

    private static Vector3d Polar(double distance, double azimuth) =>
        new(distance * Math.Cos(azimuth), distance * Math.Sin(azimuth), 0);

    private static void RequireTeeth(int teeth, string name)
    {
        if (teeth < 3)
            throw new ArgumentOutOfRangeException(name, "A gear needs at least 3 teeth.");
    }

    private static void RequireDistance(double centreDistance)
    {
        if (!(centreDistance > 0) || !double.IsFinite(centreDistance))
            throw new ArgumentOutOfRangeException(nameof(centreDistance),
                "A centre distance must be a positive length.");
    }

    private static void RequirePair(GearSpec a, GearSpec b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (!Nearly(a.Module, b.Module))
            throw new ArgumentException(
                $"Two gears mesh only in one tooth system: modules {a.Module:0.####} and "
                + $"{b.Module:0.####} differ.", nameof(b));
        if (!Nearly(a.PressureAngleDegrees, b.PressureAngleDegrees))
            throw new ArgumentException(
                $"Two gears mesh only in one tooth system: pressure angles "
                + $"{a.PressureAngleDegrees:0.###} deg and {b.PressureAngleDegrees:0.###} deg differ.",
                nameof(b));
    }

    private static void RequireBalancedShift(GearSpec a, GearSpec b)
    {
        double sum = a.ProfileShift + b.ProfileShift;
        // Exact-zero semantic test: x1 + x2 = 0 is what KEEPS the standard centre
        // distance (the two pitch-circle thicknesses then sum to the circular pitch, so
        // one member's tooth exactly fills the other's space and the centres coincide).
        // Any other sum moves the pair onto an operating centre distance that needs the
        // involute-function solve inv(a_w) = inv(a) + 2(x1+x2)tan(a)/(z1+z2), which this
        // overload does not do; the phase rule itself is unaffected, so the way out is to
        // state the distance.
        if (sum != 0)
            throw new ArgumentException(
                $"The profile shifts sum to {sum:0.####} rather than zero, so this pair does not run at "
                + "the standard centre distance m(z1+z2)/2 and its operating distance is not stated here. "
                + "Compute the operating centre distance (the involute-function solve) and use the "
                + "tooth-count overload, which takes it as an argument.", nameof(b));
    }

    // Tooth-system agreement is an exact-construction question (both members are built
    // from the same numbers), so the weld tier is the right comparison.
    private static bool Nearly(double a, double b) => Math.Abs(a - b) <= Tolerance.Default.Linear;
}
