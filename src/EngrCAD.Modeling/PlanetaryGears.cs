using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// A planetary (epicyclic) gear set: sun, N equally spaced planets on a carrier, and an
/// internal ring, with the assembly conditions checked and the mesh phases solved.
/// </summary>
/// <remarks>
/// <para>This is an ARRANGEMENT, not a tooth form — every member is an ordinary involute
/// gear from <see cref="Gears"/>, and what this type owns is the arithmetic that makes
/// them fit together: the ring tooth count, the two assembly conditions, the neighbour
/// clearance, and the rotation each member must be given so that its teeth actually mesh
/// at its own angular position.</para>
/// <para><b>The three conditions.</b> Coaxiality forces
/// <c>z_ring = z_sun + 2·z_planet</c> at a common module, so the ring count is DERIVED
/// here rather than accepted (a caller cannot state an inconsistent one). Equally spaced
/// planets then need <c>(z_sun + z_ring)</c> divisible by the planet count — the classic
/// assembly condition, and the one that is easy to violate by choosing round numbers.
/// Finally adjacent planets must not touch: their centres are a chord
/// <c>2·a·sin(π/N)</c> apart and their tip circles must clear it.</para>
/// <para><b>The kinematics are not stated anywhere in this type.</b>
/// <see cref="PlanetaryGears.Mechanism"/> builds only the individual meshes as
/// <c>Coupling.Gear</c> constraints — sun to each planet, each planet to the ring — with
/// the sun and ring jointed to the CARRIER so each joint angle is already a relative one.
/// The Willis relation <c>(ω_s − ω_c)/(ω_r − ω_c) = −z_r/z_s</c> and the familiar held-ring
/// ratio <c>1 + z_r/z_s</c> are then EMERGENT from those couplings composing, which is why
/// asserting them against the solver is a real test rather than a tautology.</para>
/// </remarks>
public sealed class PlanetarySet
{
    /// <summary>Builds and validates a planetary set at a common module.</summary>
    /// <param name="module">Module m, shared by all three members.</param>
    /// <param name="sunTeeth">z_sun.</param>
    /// <param name="planetTeeth">z_planet.</param>
    /// <param name="planetCount">N, the number of equally spaced planets.</param>
    /// <param name="pressureAngleDegrees">Pressure angle, shared by all three members.</param>
    /// <exception cref="ArgumentException">The equal-spacing assembly condition fails, or
    /// adjacent planets overlap — both named, with the numbers.</exception>
    public PlanetarySet(
        double module, int sunTeeth, int planetTeeth, int planetCount, double pressureAngleDegrees = 20)
    {
        if (!(module > 0))
            throw new ArgumentOutOfRangeException(nameof(module), "Module must be positive.");
        if (sunTeeth < 3)
            throw new ArgumentOutOfRangeException(nameof(sunTeeth), "A gear needs at least 3 teeth.");
        if (planetTeeth < 3)
            throw new ArgumentOutOfRangeException(nameof(planetTeeth), "A gear needs at least 3 teeth.");
        if (planetCount < 1)
            throw new ArgumentOutOfRangeException(nameof(planetCount), "A planetary set needs at least one planet.");

        Module = module;
        SunTeeth = sunTeeth;
        PlanetTeeth = planetTeeth;
        PlanetCount = planetCount;
        PressureAngleDegrees = pressureAngleDegrees;

        // Coaxiality: the ring count is DERIVED, never accepted, so an inconsistent set
        // cannot be spelled. r_ring = r_sun + 2*r_planet at one module.
        RingTeeth = sunTeeth + 2 * planetTeeth;

        // The assembly condition for EQUALLY SPACED planets. (Unequal spacing has its own
        // weaker condition and is not offered here.)
        if ((SunTeeth + RingTeeth) % planetCount != 0)
            throw new ArgumentException(
                $"z_sun + z_ring = {SunTeeth} + {RingTeeth} = {SunTeeth + RingTeeth} is not divisible by the "
                + $"{planetCount} planets, so they cannot be equally spaced AND all mesh: the planets would "
                + $"have to sit at {(SunTeeth + RingTeeth) / (double)planetCount:0.###} tooth pitches apart. "
                + $"Nearby sets that do assemble: {SuggestCounts(sunTeeth, planetTeeth, planetCount)}.",
                nameof(planetCount));

        // Neighbour clearance: adjacent planet centres are a chord apart, and their tip
        // circles must clear it. Meaningless for a single planet, which has no neighbour.
        CentreDistance = module * (sunTeeth + planetTeeth) / 2.0;
        if (planetCount > 1)
        {
            double chord = 2 * CentreDistance * Math.Sin(Math.PI / planetCount);
            double tipDiameter = module * (planetTeeth + 2);
            NeighbourClearance = chord - tipDiameter;
            if (!(NeighbourClearance > 0))
                throw new ArgumentException(
                    $"{planetCount} planets of {planetTeeth} teeth do not fit around a {sunTeeth}-tooth sun: "
                    + $"adjacent centres are {chord:0.###} apart but the tip circles need {tipDiameter:0.###} "
                    + $"(overlap {-NeighbourClearance:0.###}). Use fewer or smaller planets, or a larger sun.",
                    nameof(planetCount));
        }
        else
        {
            NeighbourClearance = double.PositiveInfinity;
        }
    }

    /// <summary>Module m, shared by sun, planets and ring.</summary>
    public double Module { get; }

    /// <summary>Pressure angle, shared by all three members.</summary>
    public double PressureAngleDegrees { get; }

    /// <summary>z_sun.</summary>
    public int SunTeeth { get; }

    /// <summary>z_planet.</summary>
    public int PlanetTeeth { get; }

    /// <summary>z_ring = z_sun + 2·z_planet — derived from coaxiality, not supplied.</summary>
    public int RingTeeth { get; }

    /// <summary>Number of equally spaced planets, N.</summary>
    public int PlanetCount { get; }

    /// <summary>Sun-to-planet centre distance a = m(z_sun + z_planet)/2 — the carrier's
    /// crank radius.</summary>
    public double CentreDistance { get; }

    /// <summary>Gap between adjacent planets' tip circles along the centre chord
    /// (positive by construction; +∞ for a single planet).</summary>
    public double NeighbourClearance { get; }

    /// <summary>The sun as an ordinary external gear.</summary>
    public GearSpec Sun => new(Module, SunTeeth, PressureAngleDegrees);

    /// <summary>A planet as an ordinary external gear.</summary>
    public GearSpec Planet => new(Module, PlanetTeeth, PressureAngleDegrees);

    /// <summary>The ring's own nominal definition. Its addendum points INWARD, so
    /// <see cref="GearSpec.TipDiameter"/> and <see cref="GearSpec.RootDiameter"/> read
    /// the wrong way round for it — use <see cref="RingTipRadius"/> and
    /// <see cref="RingRootRadius"/>.</summary>
    public GearSpec Ring => new(Module, RingTeeth, PressureAngleDegrees);

    /// <summary>The ring's tip radius r + h_a·m measured INWARD: m(z_ring/2 − h_a*).</summary>
    public double RingTipRadius => Module * (RingTeeth / 2.0 - Ring.AddendumCoefficient);

    /// <summary>The ring's root radius, OUTSIDE its pitch circle: m(z_ring/2 + h_f*).</summary>
    public double RingRootRadius => Module * (RingTeeth / 2.0 + Ring.DedendumCoefficient);

    /// <summary>Azimuth of planet <paramref name="k"/>, 2π·k/N radians.</summary>
    public double PlanetAzimuth(int k) => 2 * Math.PI * Index(k) / PlanetCount;

    /// <summary>
    /// The rotation planet <paramref name="k"/> must be given, in radians, for its teeth
    /// to mesh with BOTH the sun and the ring at its own azimuth, with the sun at
    /// <see cref="SunPhase"/> = 0.
    /// </summary>
    /// <remarks>
    /// <para>Each mesh fixes a phase relation, and the two are consistent for every planet
    /// exactly when the assembly condition holds — which is what that condition IS. For
    /// an external mesh along the line of centres the sun's and planet's tooth phases must
    /// sum to a half pitch (a tooth opposite a space); for the internal mesh they must
    /// differ by one. Solving the pair gives γ_k = ψ_k − z_ring(ψ_k − ρ)/z_planet.</para>
    /// <para>That is the INTERNAL half, and it is <see cref="GearMeshing.InternalPhase"/>
    /// — the same rule stated once, for every internal pair, with its derivation. The
    /// external half (<see cref="GearMeshing.ExternalPhase"/>) depends on the azimuth with
    /// the opposite SIGN, because an external pair counter-rotates where an internal pair
    /// co-rotates; the assembly condition is exactly what makes the two agree modulo one
    /// tooth pitch here, which is why one number can satisfy both meshes.</para>
    /// </remarks>
    public double PlanetPhase(int k) =>
        GearMeshing.InternalPhase(RingTeeth, PlanetTeeth, PlanetAzimuth(k), RingPhase);

    /// <summary>The sun's datum rotation: zero, with a tooth centred on +X — the
    /// convention <c>Gears.Spur</c> already draws to, so the sun needs no rotation at all
    /// and every other phase is measured from it.</summary>
    public double SunPhase => 0;

    /// <summary>
    /// The rotation the ring must be given, (z_planet − 1)·π/z_ring radians, measured
    /// from the datum where a ring tooth SPACE is centred on +X (which is how
    /// <see cref="PlanetaryGears.RingProfile"/> draws it).
    /// </summary>
    /// <remarks>
    /// <para>The (z_planet − 1) is the parity term: with a sun tooth on +X and a planet
    /// at azimuth 0, the planet must show a space to the sun and a tooth to the ring, and
    /// whether that costs a half pitch depends on whether the planet's tooth count is
    /// even. Nothing here has to special-case it — it falls out of the same solve.</para>
    /// <para>It is the general rule at azimuth 0: feed
    /// <see cref="GearMeshing.ExternalPhase"/>'s answer for the sun–planet mesh into
    /// <see cref="GearMeshing.RingPhase"/> and this closed form comes back (asserted, not
    /// assumed — the two are equal to round-off rather than bit for bit, since
    /// z(π − π/z) and (z−1)π are the same number by different arithmetic). The closed
    /// form is kept because it is the simpler statement, and the test is what keeps them
    /// from drifting.</para>
    /// </remarks>
    public double RingPhase => (PlanetTeeth - 1) * Math.PI / RingTeeth;

    /// <summary>
    /// The Willis (train-value) ratio (ω_sun − ω_carrier)/(ω_ring − ω_carrier) = −z_ring/z_sun.
    /// Stated here for a caller to compare against; <see cref="PlanetaryGears.Mechanism"/>
    /// does NOT constrain it — it emerges from the individual meshes.
    /// </summary>
    public double WillisRatio => -(double)RingTeeth / SunTeeth;

    /// <summary>Sun turns per carrier turn with the ring held: 1 + z_ring/z_sun.</summary>
    public double RingHeldRatio => 1 + (double)RingTeeth / SunTeeth;

    /// <summary>Sun turns per ring turn with the carrier held: −z_ring/z_sun (the members
    /// counter-rotate, which is the Willis ratio at ω_carrier = 0).</summary>
    public double CarrierHeldRatio => WillisRatio;

    private int Index(int k)
    {
        if (k < 0 || k >= PlanetCount)
            throw new ArgumentOutOfRangeException(nameof(k),
                $"Planet index {k} is outside [0, {PlanetCount}).");
        return k;
    }

    private static string SuggestCounts(int sun, int planet, int count)
    {
        var found = new List<string>();
        for (int ds = -2; ds <= 2 && found.Count < 3; ds++)
        {
            int s = sun + ds;
            if (s < 3) continue;
            int ring = s + 2 * planet;
            if ((s + ring) % count == 0)
                found.Add($"z_sun={s} (z_ring={ring})");
        }
        return found.Count > 0 ? string.Join(", ", found) : "none within +/-2 teeth of the sun";
    }
}

/// <summary>The joints and the assembly a <see cref="PlanetaryGears.Mechanism"/> built,
/// so a caller can drive and read the set without re-finding them.</summary>
/// <remarks>The load-bearing detail is WHICH bodies the joints connect: the sun and ring
/// pin to the CARRIER, not to the housing, so <see cref="SunPin"/>'s and
/// <see cref="RingPin"/>'s angles are already ω − ω_carrier. A gear mesh constrains
/// rotation about the line of centres, and that line turns with the carrier; pinning the
/// sun to the housing instead would silently constrain ω_sun where the physics constrains
/// ω_sun − ω_carrier, and the set would return a plausible wrong ratio.</remarks>
public sealed class PlanetaryMechanism
{
    internal PlanetaryMechanism(
        Mechanism mechanism, RevoluteJoint carrierPin, RevoluteJoint sunPin, RevoluteJoint ringPin,
        IReadOnlyList<RevoluteJoint> planetPins)
    {
        Mechanism = mechanism;
        CarrierPin = carrierPin;
        SunPin = sunPin;
        RingPin = ringPin;
        PlanetPins = planetPins;
    }

    /// <summary>The mechanism, ready to drive.</summary>
    public Mechanism Mechanism { get; }

    /// <summary>Housing → carrier. Its angle IS the carrier's absolute rotation.</summary>
    public RevoluteJoint CarrierPin { get; }

    /// <summary>Carrier → sun. Its angle is ω_sun − ω_carrier.</summary>
    public RevoluteJoint SunPin { get; }

    /// <summary>Carrier → ring. Its angle is ω_ring − ω_carrier.</summary>
    public RevoluteJoint RingPin { get; }

    /// <summary>Carrier → planet k. Its angle is ω_planet − ω_carrier.</summary>
    public IReadOnlyList<RevoluteJoint> PlanetPins { get; }
}

/// <summary>
/// Geometry and kinematics for planetary sets: the internal ring gear, the placed
/// members, and the mechanism whose composed gear couplings reproduce the Willis relation.
/// </summary>
public static class PlanetaryGears
{
    /// <summary>
    /// The internal (ring) gear's cross-section: an annulus whose BORE is toothed.
    /// </summary>
    /// <remarks>
    /// <para>An internal gear's tooth space is bounded by involutes of the same base
    /// circle as an external gear of the same module, tooth count and pressure angle, with
    /// the same arc thickness at the pitch circle — only the tip and root roles swap, the
    /// tip pointing inward. So the ring's tooth SPACES are exactly the teeth of an
    /// external "cutter" gear whose addendum reaches the ring's root and whose dedendum
    /// reaches the ring's tip, and the ring is that cutter's outline used as a HOLE in a
    /// disc. No boolean is involved: <c>Sketch.WithHole</c> is the whole construction, and
    /// the result stays lines and arcs, hence exact in all three representations.</para>
    /// <para>Two consequences are stated rather than hidden. The ring's tooth TIPS carry
    /// the cutter's root fillet, so they are rounded (harmless, and closer to a real
    /// chamfered tip than a sharp corner would be), while the ring's ROOT corners are
    /// sharp — a root fillet on an internal gear is not modelled in v1. And the undercut
    /// check that fires is <c>Gears.Spur</c>'s, applied to the CUTTER, whose addendum is
    /// the ring's dedendum coefficient: that makes it conservative for a ring (it wants
    /// z ≥ 22 at 20°, where real rings are far larger) and it is NOT the internal-mesh
    /// interference check, which v1 does not do — see the remarks on
    /// <see cref="Mechanism"/>.</para>
    /// </remarks>
    /// <param name="spec">The ring's own definition (module, tooth count, pressure angle).</param>
    /// <param name="outerDiameter">Outside diameter of the ring blank; must clear the
    /// ring's root circle.</param>
    /// <param name="fitTolerance">Flank fit tolerance, as <see cref="Gears.Spur"/>.</param>
    public static Sketch RingProfile(GearSpec spec, double outerDiameter, double? fitTolerance = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        double rootRadius = spec.Module * (spec.Teeth / 2.0 + spec.DedendumCoefficient);
        if (!(outerDiameter / 2 > rootRadius))
            throw new ArgumentOutOfRangeException(nameof(outerDiameter),
                $"The ring's outer radius {outerDiameter / 2:0.###} does not clear its root circle "
                + $"(radius {rootRadius:0.###}) - there would be no rim.");

        // The cutter: same involutes, tip and root swapped. Its teeth ARE the ring's
        // tooth spaces, so its outline is the ring's bore.
        var cutter = new GearSpec(spec.Module, spec.Teeth, spec.PressureAngleDegrees, spec.ProfileShift)
        {
            AddendumCoefficient = spec.DedendumCoefficient,
            DedendumCoefficient = spec.AddendumCoefficient,
            RootFilletCoefficient = spec.RootFilletCoefficient,
        };
        var bore = Gears.Spur(cutter, fitTolerance).Sketch;
        return Sketch.Circle(outerDiameter / 2).WithHole(bore);
    }

    /// <summary>The internal ring gear as a solid, extruded from
    /// <see cref="RingProfile"/>. Exact in all three representations.</summary>
    public static Shape RingGear(
        GearSpec spec, double faceWidth, double outerDiameter, double? fitTolerance = null)
    {
        if (!(faceWidth > 0))
            throw new ArgumentOutOfRangeException(nameof(faceWidth));
        return Shape.Extrude(RingProfile(spec, outerDiameter, fitTolerance), faceWidth);
    }

    /// <summary>
    /// Every member of <paramref name="set"/>, drawn and placed in the plane z ∈
    /// [0, faceWidth] with the sun's axis on +Z: sun at the origin, each planet at its own
    /// azimuth and phase, and the ring about them all.
    /// </summary>
    /// <param name="set">The validated set.</param>
    /// <param name="faceWidth">Common face width.</param>
    /// <param name="ringOuterDiameter">Ring blank OD; null gives root diameter + 4·m.</param>
    /// <param name="sunBore">Optional bore in the sun.</param>
    /// <param name="planetBore">Optional bore in each planet.</param>
    /// <param name="fitTolerance">Flank fit tolerance, as <see cref="Gears.Spur"/>.</param>
    public static IReadOnlyList<(string Name, Shape Shape)> Layout(
        PlanetarySet set, double faceWidth, double? ringOuterDiameter = null,
        double sunBore = 0, double planetBore = 0, double? fitTolerance = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        var members = new List<(string, Shape)>(set.PlanetCount + 2)
        {
            ("sun", Gears.SpurGear(set.Sun, faceWidth, sunBore, fitTolerance: fitTolerance)),
        };

        var planet = Gears.SpurGear(set.Planet, faceWidth, planetBore, fitTolerance: fitTolerance);
        for (int k = 0; k < set.PlanetCount; k++)
        {
            double azimuth = set.PlanetAzimuth(k);
            // Spin the planet about its OWN axis first, then carry it out to its azimuth:
            // the phase is a property of the planet, the azimuth of the carrier.
            members.Add(($"planet.{k + 1}", planet
                .RotateZ(set.PlanetPhase(k))
                .Translate(
                    set.CentreDistance * Math.Cos(azimuth),
                    set.CentreDistance * Math.Sin(azimuth),
                    0)));
        }

        double od = ringOuterDiameter ?? 2 * set.RingRootRadius + 4 * set.Module;
        members.Add(("ring", RingGear(set.Ring, faceWidth, od, fitTolerance).RotateZ(set.RingPhase)));
        return members;
    }

    /// <summary>
    /// Builds the set's kinematics over occurrences the caller has already placed: a
    /// revolute per member, and one <c>Coupling.Gear</c> per MESH — sun to each planet
    /// (external) and each planet to the ring (internal). Nothing states the train ratio.
    /// </summary>
    /// <remarks>
    /// <para><b>The sun and the ring pin to the CARRIER</b>, not to the housing, so that
    /// every gear coupling is written on angles that are already relative to the rotating
    /// line of centres. That is the whole subtlety of an epicyclic train: pinning the sun
    /// to the housing would constrain ω_sun where the mesh constrains ω_sun − ω_carrier,
    /// and the set would run and return a wrong ratio rather than fail.</para>
    /// <para>To hold a member, ground its occurrence (a held ring is the ordinary case);
    /// to drive one, drive its joint — remembering that <see cref="PlanetaryMechanism.SunPin"/>
    /// reads relative to the carrier, so an absolute sun input is driven on the sun pin
    /// only when the carrier is held.</para>
    /// <para><b>Interference is not checked here.</b> Sun–planet is an ordinary external
    /// mesh, but the planet–ring pair has internal-mesh failure modes of its own (tip
    /// interference, involute interference and trimming during assembly) that v1 does not
    /// test. The geometric conditions this kernel does enforce are the three assembly
    /// conditions on <see cref="PlanetarySet"/>.</para>
    /// </remarks>
    public static PlanetaryMechanism Mechanism(
        PlanetarySet set, Assembly rig, Occurrence housing, Occurrence carrier, Occurrence sun,
        Occurrence ring, IReadOnlyList<Occurrence> planets)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(planets);
        if (planets.Count != set.PlanetCount)
            throw new ArgumentException(
                $"The set has {set.PlanetCount} planets but {planets.Count} occurrences were supplied.",
                nameof(planets));

        var z = Vector3d.UnitZ;
        var origin = Vector3d.Zero;
        var carrierPin = Joint.Revolute(
            MateGeometry.Axis(housing, origin, z), MateGeometry.Axis(carrier, origin, z), "carrier");
        var sunPin = Joint.Revolute(
            MateGeometry.Axis(carrier, origin, z), MateGeometry.Axis(sun, origin, z), "sun");
        var ringPin = Joint.Revolute(
            MateGeometry.Axis(carrier, origin, z), MateGeometry.Axis(ring, origin, z), "ring");

        var mechanism = new Mechanism(rig).Ground(housing).Add(carrierPin).Add(sunPin).Add(ringPin);

        var planetPins = new List<RevoluteJoint>(set.PlanetCount);
        for (int k = 0; k < set.PlanetCount; k++)
        {
            double azimuth = set.PlanetAzimuth(k);
            var centre = new Vector3d(
                set.CentreDistance * Math.Cos(azimuth), set.CentreDistance * Math.Sin(azimuth), 0);
            var pin = Joint.Revolute(
                MateGeometry.Axis(carrier, centre, z),
                MateGeometry.Axis(planets[k], Vector3d.Zero, z),
                $"planet.{k + 1}");
            mechanism.Add(pin);
            planetPins.Add(pin);
        }

        // ONE coupling per mesh. The train ratio is never written down.
        foreach (var pin in planetPins)
        {
            mechanism.Add(Coupling.Gear(sunPin, pin, set.SunTeeth, set.PlanetTeeth));
            mechanism.Add(Coupling.Gear(pin, ringPin, set.PlanetTeeth, set.RingTeeth, internalMesh: true));
        }

        return new PlanetaryMechanism(mechanism, carrierPin, sunPin, ringPin, planetPins);
    }
}
