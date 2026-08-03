using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The meshing PHASE, verified from CONTACT rather than from the formula it was derived
/// with. Every claim here is measured by probing one member's outline against the other's
/// exact 2D signed distance — the same instrument the conjugacy tests use, and for the
/// same reason: the mechanism solver's <c>Coupling.Gear</c> enforces a RATIO and says
/// nothing at all about phase, so it cannot be the witness for a phase rule.
/// </summary>
public class GearMeshingTests
{
    // ---- the datum: what the factories draw, restated as a measurement ----

    [Fact]
    public void SpurProfile_DrawsAToothCentredOnPlusX()
    {
        var spec = new GearSpec(module: 2, teeth: 18);
        var region = Gears.Spur(spec).Sketch.ToRegion();
        double r = spec.PitchDiameter / 2;

        Assert.True(GearContact.At(region, r, 0) < 0, "the +X pitch point is inside the tooth");
        Assert.True(GearContact.At(region, r, Math.PI / spec.Teeth) > 0, "half a pitch round is a space");
    }

    [Fact]
    public void RingProfile_DrawsAToothSpaceCentredOnPlusX()
    {
        var spec = new GearSpec(module: 2, teeth: 60);
        var region = PlanetaryGears.RingProfile(spec, outerDiameter: 140).ToRegion();
        double r = spec.PitchDiameter / 2;

        // The ring is an annulus with a toothed bore, so at the pitch circle a SPACE is a
        // hole in the material (outside the region) and a tooth is material.
        Assert.True(GearContact.At(region, r, 0) > 0, "the +X pitch point is in a space");
        Assert.True(GearContact.At(region, r, Math.PI / spec.Teeth) < 0, "half a pitch round is a tooth");
    }

    // ---- external mesh ----

    [Fact]
    public void ExternalPhase_AtTheOrdinaryLayout_IsTheFamiliarHalfTurnLessHalfAPitch()
    {
        // The value every gear snippet used to re-derive by hand.
        Assert.Equal(Math.PI - Math.PI / 28, GearMeshing.ExternalPhase(18, 28), 12);
    }

    /// <summary>
    /// The sign of the azimuth term, settled by measurement rather than by re-reading the
    /// derivation. An external pair COUNTER-rotates, so carrying the driven member round
    /// to a new azimuth spins it the opposite way from an internal pair's — and the two
    /// candidate rules differ by 2ψ(z_A + z_B)/z_B, which is a whole tooth pitch only
    /// under the planetary assembly condition. At a general azimuth exactly one meshes.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(0.7)]     // the near miss: the mirrored rule lands 10.03 tooth pitches away
    [InlineData(1.9)]
    [InlineData(-2.4)]
    public void ExternalMesh_TeethClearWithoutBiting_AtAnyAzimuth(double azimuth)
    {
        var driver = new GearSpec(module: 2, teeth: 18);
        var driven = new GearSpec(module: 2, teeth: 27);
        // Mounted 0.4 long, which an involute pair tolerates exactly: the backlash is what
        // makes a symmetric phase a CLEARANCE rather than a double-flank touch.
        double a = GearMeshing.ExternalCentreDistance(driver, driven) + 0.4;

        var mesh = GearMeshing.External(driver.Teeth, driven.Teeth, a, azimuth);
        double clearance = GearContact.Clearance(driver, driven, mesh);
        Assert.True(clearance > 0.05 && clearance < 0.30,
            $"a correctly phased pair should clear by roughly the backlash; measured {clearance:0.#####}");

        // Mutation one: half a tooth pitch of phase error puts a tooth where a space
        // should be. The instrument must see it, and by a wide margin.
        double bitten = GearContact.Clearance(driver, driven, mesh.RolledBy(Math.PI / driven.Teeth));
        Assert.True(bitten < -1.0, $"a half-pitch phase error must bite deep; measured {bitten:0.#####}");

        // Mutation two: the OTHER sign on the azimuth term — the internal rule's
        // dependence, which is this formula reflected about its azimuth-0 value. It agrees
        // at azimuth 0 (there is nothing to disagree about) and must bite elsewhere.
        // Measured −0.815 / −0.017 / −1.131 / −1.673 at 0.5 / 0.7 / 1.9 / −2.4: the 0.7
        // row is the near miss, where the wrong sign lands 10.03 tooth pitches from the
        // right one and so very nearly meshes — and still bites by 17 µm, which is 80×
        // the flank fit deviation, so the instrument reads it as material in material.
        if (azimuth != 0)
        {
            double flipped = GearContact.Clearance(
                driver, driven,
                mesh with { Phase = 2 * (Math.PI - Math.PI / driven.Teeth) - mesh.Phase });
            Assert.True(flipped < -0.005,
                $"the mirrored azimuth dependence must not mesh; measured {flipped:0.#####}");
        }
    }

    [Fact]
    public void ExternalMesh_CarriesADriverPhase()
    {
        var driver = new GearSpec(module: 2.5, teeth: 20);
        var driven = new GearSpec(module: 2.5, teeth: 32);
        double a = GearMeshing.ExternalCentreDistance(driver, driven) + 0.4;

        // Turning the driver by delta must turn the driven member by −(z1/z2)·delta.
        const double delta = 0.31;
        double at0 = GearMeshing.ExternalPhase(driver.Teeth, driven.Teeth);
        double atDelta = GearMeshing.ExternalPhase(driver.Teeth, driven.Teeth, driverPhase: delta);
        Assert.Equal(-(double)driver.Teeth / driven.Teeth * delta, atDelta - at0, 12);

        double clearance = GearContact.Clearance(
            driver, driven, new GearMesh(new Vector3d(a, 0, 0), atDelta), driverPhase: delta);
        Assert.True(clearance > 0.05, $"a turned driver must still mesh; measured {clearance:0.#####}");
    }

    [Fact]
    public void ExternalMesh_ServesACycloidalPairToo_TheRuleIsAboutCountsNotTheToothForm()
    {
        // A cycloidal pair runs only at its DESIGN centre distance, so backlash comes from
        // THINNING the teeth rather than from mounting long — which is exactly why the
        // phase rule has to be independent of the tooth form to serve both.
        var pair = CycloidalGears.Pair(module: 2, pinionTeeth: 10, wheelTeeth: 30);
        var pinionSpec = pair.Pinion with { BacklashAllowance = 0.12 };
        var wheelSpec = pair.Wheel with { BacklashAllowance = 0.12 };
        var mesh = GearMeshing.External(pinionSpec.Teeth, wheelSpec.Teeth, pair.CentreDistance);

        Assert.Equal(Math.PI - Math.PI / wheelSpec.Teeth, mesh.Phase, 12);

        double clearance = GearContact.Clearance(
            CycloidalGears.Spur(pinionSpec).Sketch.ToRegion(),
            CycloidalGears.Spur(wheelSpec).Sketch,
            mesh, driverPhase: 0, reach: pinionSpec.TipDiameter / 2 + 0.05);
        Assert.True(clearance > 0.005 && clearance < 0.2,
            $"the thinned cycloidal pair should clear by roughly the backlash; measured {clearance:0.#####}");

        double bitten = GearContact.Clearance(
            CycloidalGears.Spur(pinionSpec).Sketch.ToRegion(),
            CycloidalGears.Spur(wheelSpec).Sketch,
            mesh.RolledBy(Math.PI / wheelSpec.Teeth), driverPhase: 0,
            reach: pinionSpec.TipDiameter / 2 + 0.05);
        Assert.True(bitten < -0.5, $"a half-pitch phase error must bite deep; measured {bitten:0.#####}");
    }

    // ---- internal mesh, and the assembly condition as an identity ----

    [Fact]
    public void InternalPhase_IsExactlyWhatPlanetarySetSolves()
    {
        var set = new PlanetarySet(module: 2, sunTeeth: 24, planetTeeth: 18, planetCount: 3);
        for (int k = 0; k < set.PlanetCount; k++)
        {
            // Bit-identical: PlanetarySet delegates rather than restating.
            Assert.Equal(
                GearMeshing.InternalPhase(
                    set.RingTeeth, set.PlanetTeeth, set.PlanetAzimuth(k), set.RingPhase),
                set.PlanetPhase(k));
        }
    }

    [Fact]
    public void RingPhase_IsTheGeneralRuleAtAzimuthZero()
    {
        var set = new PlanetarySet(module: 2, sunTeeth: 24, planetTeeth: 18, planetCount: 3);
        // The ring phase that meshes with a planet already phased to the sun.
        double planetAtZero = GearMeshing.ExternalPhase(set.SunTeeth, set.PlanetTeeth);
        double derived = GearMeshing.RingPhase(set.RingTeeth, set.PlanetTeeth, pinionPhase: planetAtZero);
        Assert.Equal(set.RingPhase, derived, 12);
    }

    /// <summary>
    /// <b>The planetary assembly condition IS the statement that the two phase rules
    /// agree.</b> They differ by 2ψ(z_sun + z_planet)/z_planet, and ψ = 2πk/N, so the
    /// difference is a whole number of tooth pitches for every planet exactly when N
    /// divides 2k(z_sun + z_planet) — i.e. when N divides z_sun + z_ring.
    /// </summary>
    [Theory]
    [InlineData(24, 18, 3)]   // 24 + 60 = 84, divisible by 3
    [InlineData(20, 20, 4)]   // 20 + 60 = 80, divisible by 4
    [InlineData(30, 15, 5)]   // 30 + 60 = 90, divisible by 5
    public void AssemblyCondition_MakesTheExternalAndInternalRulesAgree(int sun, int planet, int count)
    {
        var set = new PlanetarySet(module: 2, sunTeeth: sun, planetTeeth: planet, planetCount: count);
        double pitch = 2 * Math.PI / set.PlanetTeeth;
        for (int k = 0; k < set.PlanetCount; k++)
        {
            double psi = set.PlanetAzimuth(k);
            double external = GearMeshing.ExternalPhase(set.SunTeeth, set.PlanetTeeth, psi);
            double internalRule =
                GearMeshing.InternalPhase(set.RingTeeth, set.PlanetTeeth, psi, set.RingPhase);
            double pitches = (external - internalRule) / pitch;
            Assert.Equal(Math.Round(pitches), pitches, 9);
        }
    }

    [Fact]
    public void WithoutTheAssemblyCondition_TheTwoRulesDisagree()
    {
        // 5 planets round a 24/18 set: z_sun + z_ring = 84 is not divisible by 5, so the
        // equal-spacing condition fails — and the two rules land a FRACTIONAL number of
        // tooth pitches apart (16.8 at the first planet), which is exactly why it fails.
        const int sun = 24, planet = 18, ring = sun + 2 * planet, count = 5;
        Assert.NotEqual(0, (sun + ring) % count);

        double ringPhase = (planet - 1) * Math.PI / ring;
        double pitch = 2 * Math.PI / planet;
        double psi = 2 * Math.PI / count;
        double pitches =
            (GearMeshing.ExternalPhase(sun, planet, psi)
             - GearMeshing.InternalPhase(ring, planet, psi, ringPhase)) / pitch;
        Assert.True(Math.Abs(pitches - Math.Round(pitches)) > 0.1,
            $"the two rules must genuinely disagree here; they are {pitches:0.####} pitches apart");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.9)]
    [InlineData(-1.7)]
    public void InternalMesh_TeethTouchWithoutBiting(double azimuth)
    {
        var ring = new GearSpec(module: 2, teeth: 60);
        var pinion = new GearSpec(module: 2, teeth: 18);
        var ringRegion = PlanetaryGears.RingProfile(ring, outerDiameter: 140).ToRegion();
        var pinionSketch = Gears.Spur(pinion).Sketch;

        var mesh = GearMeshing.Internal(ring, pinion, azimuth);
        // A NOMINAL internal pair is zero-backlash: the pinion tooth exactly fills the ring
        // space at the pitch circle, so the conjugate flanks TOUCH and there is no gap to
        // measure. What this asserts is that nothing bites.
        double contact = GearContact.Clearance(
            ringRegion, pinionSketch, mesh, driverPhase: 0, reach: double.PositiveInfinity);
        // Measured 4e-5 / −9e-5 / 0 at the three azimuths: the conjugate flanks touch, and
        // what is left is the flank fit's own deviation.
        Assert.True(contact > -0.001,
            $"a correctly phased internal pair must not interpenetrate; measured {contact:0.#####}");

        double bitten = GearContact.Clearance(
            ringRegion, pinionSketch, mesh.RolledBy(Math.PI / pinion.Teeth), driverPhase: 0,
            reach: double.PositiveInfinity);
        Assert.True(bitten < -0.5, $"a half-pitch phase error must bite deep; measured {bitten:0.#####}");
    }

    // ---- rack and pinion ----

    [Fact]
    public void RackPinionPhase_ReproducesTheDocumentedPlacement()
    {
        var rackSpec = new RackSpec(module: 2);
        var pinionSpec = rackSpec.MatingGear(teeth: 18);
        var rack = Gears.Rack(rackSpec, teeth: 12, backHeight: 4);
        double midpoint = rack.Length / 2;

        var mesh = GearMeshing.Rack(rackSpec, pinionSpec, midpoint);
        Assert.Equal(midpoint, mesh.Centre.X, 12);
        Assert.Equal(pinionSpec.PitchDiameter / 2, mesh.Centre.Y, 12);

        // The bar's midpoint is a whole number of pitches from x = 0, so it is another
        // space centre and the phase reduces to the docs page's own −π/2 — modulo a whole
        // tooth pitch, which is what makes them the same placement.
        double pitch = 2 * Math.PI / pinionSpec.Teeth;
        double pitches = (mesh.Phase - -Math.PI / 2) / pitch;
        Assert.Equal(Math.Round(pitches), pitches, 9);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(3.7)]
    [InlineData(11.31)]
    [InlineData(-5.2)]
    public void RackMesh_TeethClearWithoutBiting_AnywhereAlongTheBar(double x)
    {
        var rackSpec = new RackSpec(module: 2);
        var pinionSpec = rackSpec.MatingGear(teeth: 18);
        var rack = Gears.Rack(rackSpec, teeth: 12, backHeight: 4);
        var rackRegion = rack.Sketch.ToRegion();
        var pinionSketch = Gears.Spur(pinionSpec).Sketch;

        // Lift the pinion 0.4 to open backlash — legal for an involute against a straight
        // rack at ANY mounting height, which the rack conjugacy test already measures.
        var mesh = GearMeshing.Rack(rackSpec, pinionSpec, rack.Length / 2 + x);
        var lifted = mesh with { Centre = mesh.Centre + new Vector3d(0, 0.4, 0) };

        double clearance = GearContact.Clearance(
            rackRegion, pinionSketch, lifted, driverPhase: 0, reach: double.PositiveInfinity);
        Assert.True(clearance > 0.02 && clearance < 0.6,
            $"a correctly phased rack mesh should clear by roughly the backlash; measured {clearance:0.#####}");

        double bitten = GearContact.Clearance(
            rackRegion, pinionSketch, lifted.RolledBy(Math.PI / pinionSpec.Teeth), driverPhase: 0,
            reach: double.PositiveInfinity);
        Assert.True(bitten < -0.5, $"a half-pitch phase error must bite deep; measured {bitten:0.#####}");
    }

    // ---- helical: the drawn section is enough ----

    [Fact]
    public void HelicalPair_PhasedAtItsDrawnSection_StaysPhasedAtEverySection()
    {
        // Opposite hands, equal angles: ψ_A·z_A + ψ_B·z_B is then identically zero in z, so
        // the mesh condition is z-independent and the plain phase serves.
        var driver = new GearSpec(module: 2, teeth: 18);
        var driven = new GearSpec(module: 2, teeth: 27);
        const double beta = 20 * Math.PI / 180, width = 12;
        double a = GearMeshing.ExternalCentreDistance(driver, driven) + 0.4;
        var mesh = GearMeshing.External(driver.Teeth, driven.Teeth, a);

        double TwistOf(GearSpec s) => width * Math.Tan(beta) / (s.PitchDiameter / 2);
        foreach (double f in new[] { 0.0, 0.25, 0.5, 1.0 })
        {
            // The driver's section at height f·W is its profile turned +f·twist; the driven
            // member's (opposite hand) is turned −f·twist.
            double clearance = GearContact.Clearance(
                driver, driven, mesh.RolledBy(-f * TwistOf(driven)),
                driverPhase: f * TwistOf(driver));
            Assert.True(clearance > 0.05,
                $"section at {f:0.##}·W bites: clearance {clearance:0.#####}");
        }
    }

    // ---- the value's three spellings must agree ----

    [Fact]
    public void Transform_Frame_And_Place_AreTheSamePlacement()
    {
        var mesh = GearMeshing.External(
            new GearSpec(2, 18), new GearSpec(2, 27), azimuth: 0.6, driverPhase: 0.2);

        // A Frame3d for an Occurrence and a Matrix4d for a Shape must be one placement,
        // or a mechanism rig and a static layout would draw the same pair differently.
        // Compare them by what they DO to points rather than entry by entry.
        var fromFrame = mesh.Frame.ToMatrix();
        foreach (var p in new[]
                 {
                     Vector3d.Zero, new Vector3d(5, 0, 0), new Vector3d(0, 7, 0), new Vector3d(1, -2, 3),
                 })
        {
            var a = mesh.Transform.TransformPoint(p);
            var b = fromFrame.TransformPoint(p);
            Assert.Equal(a.X, b.X, 12);
            Assert.Equal(a.Y, b.Y, 12);
            Assert.Equal(a.Z, b.Z, 12);
        }

        // Spin first, then carry out: a point on the member's own +X lands at the centre
        // plus the TURNED radius, never at the un-turned one.
        var probe = mesh.Transform.TransformPoint(new Vector3d(5, 0, 0));
        Assert.Equal(mesh.Centre.X + 5 * Math.Cos(mesh.Phase), probe.X, 12);
        Assert.Equal(mesh.Centre.Y + 5 * Math.Sin(mesh.Phase), probe.Y, 12);
    }

    // ---- refusals, by name ----

    [Fact]
    public void MismatchedToothSystem_RefusedByName()
    {
        var a = new GearSpec(module: 2, teeth: 18);
        Assert.Contains("module", Assert.Throws<ArgumentException>(
            () => GearMeshing.External(a, new GearSpec(module: 2.5, teeth: 27))).Message);
        Assert.Contains("pressure angle", Assert.Throws<ArgumentException>(
            () => GearMeshing.External(a, new GearSpec(2, 27, pressureAngleDegrees: 25))).Message);
    }

    [Fact]
    public void UnbalancedProfileShift_RefusedNamingTheWayOut()
    {
        var driver = new GearSpec(2, 18, 20, profileShift: 0.3);
        var driven = new GearSpec(2, 27, 20, profileShift: 0.1);
        var ex = Assert.Throws<ArgumentException>(() => GearMeshing.External(driver, driven));
        Assert.Contains("operating centre distance", ex.Message);
        Assert.Contains("tooth-count overload", ex.Message);

        // ...and the balanced pair, which does keep the standard distance, is fine.
        _ = GearMeshing.External(driver, new GearSpec(2, 27, 20, profileShift: -0.3));
    }

    [Fact]
    public void AnInternalMeshNeedsTheRingToBeBigger()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            GearMeshing.Internal(new GearSpec(2, 18), new GearSpec(2, 24)));
        Assert.Contains("more teeth than the pinion", ex.Message);
    }
}

/// <summary>
/// The contact instrument: one member's outline sampled and probed against the other's
/// EXACT 2D signed distance. Nothing here tessellates and nothing here solves — a negative
/// reading is material genuinely inside material, in millimetres.
/// </summary>
internal static class GearContact
{
    public static double At(IPlanarRegion region, double r, double theta) =>
        region.SignedDistance(new Vector2d(r * Math.Cos(theta), r * Math.Sin(theta)));

    public static double Clearance(
        GearSpec driver, GearSpec driven, in GearMesh mesh, double driverPhase = 0) =>
        Clearance(
            Gears.Spur(driver).Sketch.ToRegion(), Gears.Spur(driven).Sketch, mesh, driverPhase,
            reach: driver.TipDiameter / 2 + 0.05);

    /// <summary>
    /// The minimum signed distance of <paramref name="driven"/>'s outline points, mapped
    /// through the mesh placement and back into the driver's (turned) frame, measured in
    /// the driver's region. Positive = a gap; negative = interpenetration, by that depth.
    /// </summary>
    public static double Clearance(
        IPlanarRegion driverRegion, Sketch driven, in GearMesh mesh, double driverPhase, double reach)
    {
        double c1 = Math.Cos(-driverPhase), s1 = Math.Sin(-driverPhase);
        double c2 = Math.Cos(mesh.Phase), s2 = Math.Sin(mesh.Phase);
        double reachSquared = double.IsPositiveInfinity(reach) ? double.PositiveInfinity : reach * reach;

        double min = double.PositiveInfinity;
        foreach (var p in Outline(driven))
        {
            double wx = mesh.Centre.X + p.X * c2 - p.Y * s2;
            double wy = mesh.Centre.Y + p.X * s2 + p.Y * c2;
            if (wx * wx + wy * wy > reachSquared)
                continue;
            min = Math.Min(min, driverRegion.SignedDistance(
                new Vector2d(wx * c1 - wy * s1, wx * s1 + wy * c1)));
        }
        return min;
    }

    public static List<Vector2d> Outline(Sketch sketch)
    {
        var points = new List<Vector2d>();
        foreach (var curve in sketch.ToCurves())
        {
            double length = curve switch
            {
                Line2d line => (line.End - line.Start).Length,
                Arc2d arc => arc.Length,
                _ => curve.ArcLength(),
            };
            int n = Math.Max(2, (int)Math.Ceiling(length / 0.03));
            for (int i = 0; i < n; i++)
                points.Add(curve.PointAt((double)i / n));
        }
        return points;
    }
}
