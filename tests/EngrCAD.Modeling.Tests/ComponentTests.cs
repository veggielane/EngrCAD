using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The standard component library. A component is more than geometry, so these tests
/// exercise the MECHANISM: placing prepares the host with the right hole, the occurrence
/// lands at the right pose, suppressing a placement removes its bore too, regeneration
/// after a parameter change re-seats everything, and a stack prepares both bodies.
/// </summary>
public class ComponentTests
{
    private const int N = 64;   // tessellation segments per circle for volume checks

    /// <summary>Inscribed n-gon area — what a tessellated circle of radius r encloses.</summary>
    private static double NgonArea(double r, int n = N) => 0.5 * n * r * r * Math.Sin(2 * Math.PI / n);

    private static readonly SketchPlane PlateTop =
        SketchPlane.At((0, 0, 4), Vector3d.UnitX, Vector3d.UnitY);   // top of Box(60, 40, 8)

    private static Shape HostOf(ComponentAssembly build) => (Shape)build.Host!.Geometry;

    private static void AssertClose(double expected, double actual, double tolerance = 1e-9) =>
        Assert.True(Math.Abs(actual - expected) < tolerance, $"{actual} vs expected {expected}");

    private static void AssertOrigin(PartInstance instance, Vector3d expected)
    {
        var actual = instance.World.TransformPoint(Vector3d.Zero);
        Assert.True(actual.DistanceTo(expected) < 1e-9, $"{instance.Path} at {actual}, expected {expected}");
    }

    // ---- catalogue ----

    [Fact]
    public void CapScrew_Catalogue_FollowsIso4762()
    {
        var screw = StandardComponents.CapScrew(4, 16);
        Assert.Equal("ISO 4762 M4×16", screw.Designation);
        AssertClose(7.0, screw.HeadDiameter);
        AssertClose(4.0, screw.HeadHeight);            // k = d for this family
        AssertClose(16.0, screw.InsertedLength);       // length is measured under the head
        Assert.Equal("M4×0.7", screw.Thread.Designation);

        // Counterbored by default: the head sits in the DIN 974 recess, k + 0.5 deep.
        AssertClose(4.5, screw.SeatDepth);
        AssertClose(0, StandardComponents.CapScrew(4, 16, ScrewSeating.OnFace).SeatDepth);

        // Every catalogue size seats its head below the face.
        foreach (double size in new[] { 2.0, 2.5, 3.0, 4.0, 5.0, 6.0, 8.0, 10.0, 12.0 })
        {
            var s = StandardComponents.CapScrew(size, 4 * size);
            Assert.True(s.SeatDepth > s.HeadHeight, $"M{size} recess does not swallow its head");
            Assert.True(s.HeadDiameter > size, $"M{size} head is not wider than its shank");
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => StandardComponents.CapScrew(7, 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => StandardComponents.CapScrew(4, 0));
    }

    [Fact]
    public void Insert_And_Dowel_Catalogue()
    {
        var insert = StandardComponents.TrisertInsert(4);
        Assert.Equal("Tappex Trisert M4", insert.Designation);
        AssertClose(StandardHoles.TrisertDiameter(4), insert.OutsideDiameter);
        AssertClose(StandardHoles.TrisertLength(4), insert.BodyLength);
        AssertClose(insert.BodyLength + 2, StandardHoles.TrisertMinimumDepth(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => StandardComponents.TrisertInsert(7));

        var dowel = StandardComponents.Dowel(5, 20);
        Assert.Equal("ISO 2338 Ø5×20 m6", dowel.Designation);
        AssertClose(10.0, dowel.InsertedLength);       // half in by default
        AssertClose(16.0, StandardComponents.Dowel(5, 20, 16).InsertedLength);
        Assert.Throws<ArgumentOutOfRangeException>(() => StandardComponents.Dowel(5, 20, 21));
        Assert.Throws<ArgumentOutOfRangeException>(() => StandardComponents.Dowel(0, 20));
    }

    // ---- bodies ----

    [Fact]
    public void CapScrewBody_IsAnExactSolidOfRevolution()
    {
        var screw = StandardComponents.CapScrew(4, 16);
        var solid = screw.Body.ToBrep();
        solid.Validate();

        var mesh = BRepTessellator.Tessellate(solid, N, 24);
        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);    // genus 0

        double exact = NgonArea(3.5) * 4 + NgonArea(2.0) * 16;
        AssertClose(exact, mesh.Volume());

        // The local-frame contract: head above the bearing face at z = 0, shank below.
        var sdf = screw.Body.ToImplicit();
        Assert.True(sdf.Evaluate((0, 0, -8)) < 0);    // mid-shank
        Assert.True(sdf.Evaluate((0, 0, 2)) < 0);     // mid-head
        Assert.True(sdf.Evaluate((3, 0, -8)) > 0);    // clear of the shank, under the head
    }

    [Fact]
    public void InsertBody_IsABoredSleeve()
    {
        var insert = StandardComponents.TrisertInsert(4);
        var solid = insert.Body.ToBrep();
        solid.Validate();

        var mesh = BRepTessellator.Tessellate(solid, N, 24);
        Assert.True(mesh.IsClosed);
        Assert.Equal(0, mesh.EulerCharacteristic);    // genus 1: bored through

        double exact = (NgonArea(insert.OutsideDiameter / 2) - NgonArea(insert.Thread.MinorDiameter / 2))
            * insert.BodyLength;
        AssertClose(exact, mesh.Volume());
    }

    [Fact]
    public void DowelBody_HasChamferedEnds()
    {
        var dowel = StandardComponents.Dowel(5, 20);
        var solid = dowel.Body.ToBrep();
        solid.Validate();

        var mesh = BRepTessellator.Tessellate(solid, N, 24);
        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);

        // A full cylinder less two 45-degree chamfer rings: each is a cylinder slice of
        // height c minus the n-gon frustum the chamfer leaves behind.
        double r = 2.5, c = dowel.Chamfer;
        double big = NgonArea(r), small = NgonArea(r - c);
        double ring = big * c - c / 3 * (big + small + Math.Sqrt(big * small));
        AssertClose(big * 20 - 2 * ring, mesh.Volume());
    }

    // ---- the mechanism: one call cuts the host and assembles the component ----

    [Fact]
    public void PlacingAScrew_DrillsItsClearanceHoleAndPlacesItself()
    {
        var screw = StandardComponents.CapScrew(4, 16, ScrewSeating.OnFace);
        var build = new ComponentAssembly("plate", Shape.Box(60, 40, 8));
        build.Place(screw, [new(-20, 0), new(20, 0)], PlateTop);

        var assembly = build.ToAssembly("bracket");

        // The host really has the M4 normal-clearance bore (Ø4.5) through it: the exact
        // distance on the bore axis, solid material away from the holes, and void all
        // the way to the far face.
        var sdf = HostOf(build).ToImplicit();
        AssertClose(2.25, sdf.Evaluate((-20, 0, 0)));
        AssertClose(2.25, sdf.Evaluate((20, 0, 0)));
        Assert.True(sdf.Evaluate((0, 0, 0)) < 0);
        Assert.True(sdf.Evaluate((-20, 0, -3.9)) > 0);

        // ...and the components assembled themselves: host first, one occurrence per point.
        var instances = assembly.Flatten();
        Assert.Equal(3, instances.Count);
        Assert.Equal("bracket/plate", instances[0].Path);
        Assert.Same(screw.ToPart(), instances[1].Part);
        Assert.Same(screw.ToPart(), instances[2].Part);   // one shared Part, two occurrences
        AssertOrigin(instances[1], (-20, 0, 4));          // OnFace: the head bears on the face
        AssertOrigin(instances[2], (20, 0, 4));
    }

    [Fact]
    public void CounterboredScrew_SeatsItsHeadInTheRecess()
    {
        var screw = StandardComponents.CapScrew(4, 16);   // counterbored by default
        var build = new ComponentAssembly("plate", Shape.Box(60, 40, 8));
        build.Place(screw, [new(-20, 0)], PlateTop);
        var instances = build.ToAssembly().Flatten();

        // The recess is 4.5 deep, so the head's bearing face lands at z = 4 - 4.5.
        AssertOrigin(instances[1], (-20, 0, -0.5));

        // The prepared host is a valid B-Rep with the DIN 974 recess actually cut.
        var solid = build.Host!.TryGetSolid();
        Assert.NotNull(solid);
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid, 48, 24);
        Assert.True(mesh.IsClosed);
        Assert.Equal(0, mesh.EulerCharacteristic);   // one through hole ⇒ genus 1

        double exact = 60 * 40 * 8 - NgonArea(4.0, 48) * 4.5 - NgonArea(2.25, 48) * 3.5;
        AssertClose(exact, mesh.Volume(), 1e-6);
    }

    [Fact]
    public void PlacingAnInsert_DrillsItsPilotBore()
    {
        var insert = StandardComponents.TrisertInsert(4);
        var build = new ComponentAssembly("plate", Shape.Box(60, 40, 8));
        build.Place(insert, [new(0, 12)], PlateTop);
        var instances = build.ToAssembly().Flatten();

        // The pilot is the catalogue diameter (Ø6.4 for M4), not a clearance hole.
        AssertClose(StandardHoles.TrisertDiameter(4) / 2, HostOf(build).ToImplicit().Evaluate((0, 12, 0)));

        // The insert seats flush: its top face IS the host's face.
        AssertOrigin(instances[1], (0, 12, 4));
        Assert.Equal(Palette.Brass, instances[1].Part.Color!.Value);
    }

    [Fact]
    public void PlacingADowel_ReamsABlindHoleForItsInsertedHalf()
    {
        var dowel = StandardComponents.Dowel(4, 12);   // 6 in, 6 proud
        var build = new ComponentAssembly("plate", Shape.Box(60, 40, 8));
        build.Place(dowel, [new(10, 0)], PlateTop);
        build.ToAssembly();

        // Reamed to the NOMINAL diameter: the m6/H7 interference lives in tolerances
        // this kernel does not model. (Probed mid-bore, clear of the tool's end faces,
        // where the difference SDF is the exact radial distance.)
        var sdf = HostOf(build).ToImplicit();
        AssertClose(2.0, sdf.Evaluate((10, 0, 1)));
        Assert.True(sdf.Evaluate((10, 0, -3)) < 0);      // blind: solid below the pin
    }

    // ---- suppression: the property that makes preparation a Feature worth having ----

    [Fact]
    public void SuppressingAPlacement_RemovesItsBoreAndItsOccurrence()
    {
        var build = new ComponentAssembly("plate", Shape.Box(60, 40, 8));
        build.Place(StandardComponents.CapScrew(4, 16, ScrewSeating.OnFace), [new(-20, 0)], PlateTop);
        var insert = build.Place(StandardComponents.TrisertInsert(4), [new(0, 12)], PlateTop);

        var before = build.ToAssembly();
        Assert.Equal(3, before.Occurrences.Count);
        Assert.True(HostOf(build).ToImplicit().Evaluate((0, 12, 0)) > 0);   // pilot present

        var suppressed = build.Suppress(insert);
        Assert.Equal(2, build.ToAssembly().Occurrences.Count);              // occurrence gone

        var sdf = HostOf(build).ToImplicit();
        Assert.True(sdf.Evaluate((0, 12, 0)) < 0, "the insert's bore survived suppression");
        AssertClose(2.25, sdf.Evaluate((-20, 0, 0)));                       // the screw's did not

        // ...and restoring the placement brings both back.
        build.Suppress(suppressed, suppressed: false);
        Assert.Equal(3, build.ToAssembly().Occurrences.Count);
        Assert.True(HostOf(build).ToImplicit().Evaluate((0, 12, 0)) > 0);
    }

    [Fact]
    public void Suppression_ReportsAsASuppressedFeature()
    {
        var build = new ComponentAssembly("plate", Shape.Box(60, 40, 8));
        var placement = build.Place(StandardComponents.TrisertInsert(4), [new(0, 12)], PlateTop);
        build.Suppress(placement);

        var result = build.History.Regenerate();
        Assert.True(result.Succeeded);   // suppression is not failure
        Assert.Equal(FeatureOutcome.Suppressed, result.Statuses[1].Outcome);
        Assert.Equal("Tappex Trisert M4", result.Statuses[1].Name);
    }

    // ---- regeneration ----

    private sealed class PlateFeature : Feature
    {
        [Param(Min = 1, Max = 40, Units = "mm")]
        public double Thickness { get; init; } = 8;

        // The top face sits at z = Thickness, so FeatureContext.TopPlane tracks it.
        public override Shape Apply(FeatureContext context) =>
            Shape.Box(60, 40, Thickness).Translate(0, 0, Thickness / 2);
    }

    [Fact]
    public void Regeneration_AfterAParameterChange_ReSeatsTheComponents()
    {
        var history = new FeatureHistory();
        history.Add(new PlateFeature { Thickness = 8 });
        var build = new ComponentAssembly("plate", history);
        build.Place(StandardComponents.CapScrew(4, 16, ScrewSeating.OnFace), [new(-20, 0)]);

        var thin = build.ToAssembly().Flatten();
        AssertOrigin(thin[1], (-20, 0, 8));
        AssertClose(2.25, HostOf(build).ToImplicit().Evaluate((-20, 0, 4)));

        // Thicken the plate: the screw re-seats on the new top face and its bore is
        // re-cut through the thicker body — no placement code re-run by hand.
        history.Replace(0, new PlateFeature { Thickness = 14 });
        var thick = build.ToAssembly().Flatten();
        AssertOrigin(thick[1], (-20, 0, 14));

        var sdf = HostOf(build).ToImplicit();
        AssertClose(2.25, sdf.Evaluate((-20, 0, 7)));
        Assert.True(sdf.Evaluate((-20, 0, 1)) > 0, "the bore did not follow the thicker plate");
    }

    [Fact]
    public void Regeneration_CachesUnchangedPlacements()
    {
        var history = new FeatureHistory();
        history.Add(new PlateFeature());
        var build = new ComponentAssembly("plate", history);
        build.Place(StandardComponents.CapScrew(4, 16, ScrewSeating.OnFace), [new(-20, 0)]);

        Assert.Equal(FeatureOutcome.Applied, build.History.Regenerate().Statuses[1].Outcome);
        Assert.Equal(FeatureOutcome.Cached, build.History.Regenerate().Statuses[1].Outcome);

        // A cached placement still reports where it put things.
        var placement = build.Placements.Single();
        Assert.Single(placement.Placements);
        Assert.Equal(new Vector2d(-20, 0), placement.Placements[0].Point);
    }

    // ---- the full fastener stack (two bodies, one call) ----

    [Fact]
    public void FastenerStack_PreparesBothBodiesAndPlacesTheScrewOnce()
    {
        // Cover z in [0, 10] on a base z in [-20, 0]; they mate at z = 0.
        var coverFace = SketchPlane.At((0, 0, 10), Vector3d.UnitX, Vector3d.UnitY);
        var mateFace = SketchPlane.At((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY);

        var cover = new ComponentAssembly("cover", Shape.Box(60, 40, 10).Translate(0, 0, 5));
        var basePlate = new ComponentAssembly("base", Shape.Box(60, 40, 20).Translate(0, 0, -10));

        var screw = StandardComponents.CapScrew(5, 16);   // counterbored: seat 5.5 down
        cover.PlaceThrough(screw, [new(-20, 0), new(20, 0)], coverFace, basePlate, mateFace);

        var coverAssembly = cover.ToAssembly();
        var baseAssembly = basePlate.ToAssembly();

        // Near body: Ø5.5 clearance right through, under the Ø10 counterbore.
        var coverSdf = HostOf(cover).ToImplicit();
        AssertClose(2.75, coverSdf.Evaluate((-20, 0, 4)));
        Assert.True(coverSdf.Evaluate((0, 0, 5)) < 0);

        // Far body: the M5 coarse tap-drill pilot (Ø4.2), blind — 11.5 of engagement plus
        // two pitches of tap runout is 13.1 deep, well inside the 20 mm base.
        var baseSdf = HostOf(basePlate).ToImplicit();
        AssertClose(2.1, baseSdf.Evaluate((-20, 0, -2)));
        Assert.True(baseSdf.Evaluate((-20, 0, -16)) < 0, "the pilot ran through the base");
        Assert.True(baseSdf.Evaluate((0, 0, -2)) < 0);

        // The screw is placed once — on the near body, seated in its counterbore.
        Assert.Equal(3, coverAssembly.Occurrences.Count);
        AssertOrigin(coverAssembly.Flatten()[1], (-20, 0, 4.5));
        Assert.Single(baseAssembly.Occurrences);   // the far body is prepared, not populated
    }

    [Fact]
    public void FastenerStack_ProjectsPointsOntoTheAnchorFace()
    {
        // The anchor face's 2D axes are turned 90 degrees from the seating face's, so the
        // anchor points must be PROJECTED along the fastener axis, not copied.
        var coverFace = SketchPlane.At((0, 0, 10), Vector3d.UnitX, Vector3d.UnitY);
        var mateFace = SketchPlane.At((0, 0, 0), Vector3d.UnitY, -Vector3d.UnitX);

        var cover = new ComponentAssembly("cover", Shape.Box(60, 40, 10).Translate(0, 0, 5));
        var basePlate = new ComponentAssembly("base", Shape.Box(60, 40, 20).Translate(0, 0, -10));
        cover.PlaceThrough(StandardComponents.CapScrew(5, 16), [new(-20, 6)], coverFace, basePlate, mateFace);

        cover.ToAssembly();
        basePlate.ToAssembly();

        var baseSdf = HostOf(basePlate).ToImplicit();
        AssertClose(2.1, baseSdf.Evaluate((-20, 6, -2)));      // the pilot is under the screw
        Assert.True(baseSdf.Evaluate((6, 15, -2)) < 0);        // not at the copied coordinates
    }

    [Fact]
    public void FastenerStack_RejectsGeometryItCannotFasten()
    {
        var coverFace = SketchPlane.At((0, 0, 10), Vector3d.UnitX, Vector3d.UnitY);
        var mateFace = SketchPlane.At((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY);
        var cover = new ComponentAssembly("cover", Shape.Box(60, 40, 10).Translate(0, 0, 5));
        var basePlate = new ComponentAssembly("base", Shape.Box(60, 40, 20).Translate(0, 0, -10));

        // Too short: an M5x4 screw counterbored 5.5 deep never reaches the far body.
        var tooShort = Assert.Throws<ArgumentException>(() => cover.PlaceThrough(
            StandardComponents.CapScrew(5, 4), [new(0, 0)], coverFace, basePlate, mateFace));
        Assert.Contains("nothing engages", tooShort.Message);

        // Non-parallel faces share no fastener axis.
        var tilted = SketchPlane.At((0, 0, 0), Vector3d.UnitX, Vector3d.UnitZ);
        Assert.Throws<ArgumentException>(() => cover.PlaceThrough(
            StandardComponents.CapScrew(5, 16), [new(0, 0)], coverFace, basePlate, tilted));

        // The anchor must lie below the seat, and must be a different body.
        Assert.Throws<ArgumentException>(() => cover.PlaceThrough(
            StandardComponents.CapScrew(5, 16), [new(0, 0)], mateFace, basePlate, coverFace));
        Assert.Throws<ArgumentException>(() => cover.PlaceThrough(
            StandardComponents.CapScrew(5, 16), [new(0, 0)], coverFace, cover, mateFace));

        // None of those partial calls left a placement behind.
        Assert.Empty(cover.Placements);
    }

    // ---- API guards ----

    [Fact]
    public void Components_RejectWhatTheyCannotDo()
    {
        var site = new ComponentSite(
            Shape.Box(10, 10, 10), [new(0, 0)],
            SketchPlane.At((0, 0, 5), Vector3d.UnitX, Vector3d.UnitY));

        // An insert is installed into one body; it has no far-side preparation.
        var unsupported = Assert.Throws<NotSupportedException>(
            () => StandardComponents.TrisertInsert(4).PrepareAnchor(site));
        Assert.Contains("Tappex Trisert M4", unsupported.Message);

        // A dowel reams the far body of a stack exactly as it reams the near one.
        StandardComponents.Dowel(4, 12).PrepareAnchor(site);

        Assert.Throws<ArgumentException>(() =>
            new ComponentFeature(StandardComponents.TrisertInsert(4), []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ComponentSite(
            Shape.Box(10, 10, 10), [new(0, 0)], SketchPlane.XY, requestedDepth: -1));
    }

    [Fact]
    public void ComponentSite_ResolvesDepthsExplicitFirst()
    {
        var body = Shape.Box(10, 10, 10);
        var top = SketchPlane.At((0, 0, 5), Vector3d.UnitX, Vector3d.UnitY);

        var auto = new ComponentSite(body, [new(0, 0)], top);
        AssertClose(10 * 1.05, auto.ThroughDepth);   // clears the far face by 5%
        AssertClose(3, auto.Depth(3));

        var forced = new ComponentSite(body, [new(0, 0)], top, requestedDepth: 4);
        AssertClose(4, forced.Depth(3));

        // A face whose normal points INTO the body has nothing below it.
        var upsideDown = new ComponentSite(body, [new(0, 0)],
            SketchPlane.At((0, 0, -5), Vector3d.UnitX, Vector3d.UnitY));
        Assert.Throws<InvalidOperationException>(() => upsideDown.ThroughDepth);
    }

    [Fact]
    public void Placement_OverridesTheNaturalDepth()
    {
        var build = new ComponentAssembly("plate", Shape.Box(60, 40, 8));
        // The insert's natural pilot is 10.1 deep (through an 8 mm plate); force a blind 6.
        build.Place(StandardComponents.TrisertInsert(4), [new(0, 12)], PlateTop, depth: 6);
        build.ToAssembly();

        var sdf = HostOf(build).ToImplicit();
        Assert.True(sdf.Evaluate((0, 12, 0)) > 0);        // bored 2 mm down
        Assert.True(sdf.Evaluate((0, 12, -3.5)) < 0);     // solid below the blind bottom
    }

    [Fact]
    public void CounterboredScrew_InAThinBody_FailsWithGuidance()
    {
        var build = new ComponentAssembly("shim", Shape.Box(60, 40, 4));
        build.Place(StandardComponents.CapScrew(5, 16),   // needs 5.5 of counterbore
            [new(0, 0)], SketchPlane.At((0, 0, 2), Vector3d.UnitX, Vector3d.UnitY));

        var failure = Assert.Throws<InvalidOperationException>(() => build.ToAssembly());
        Assert.Contains("counterbore", failure.Message);
        Assert.Contains("ScrewSeating.OnFace", failure.Message);
    }

    [Fact]
    public void PreparedHost_ShowsItsPlacementsInTheConstructionTree()
    {
        var build = new ComponentAssembly("plate", Shape.Box(60, 40, 8));
        build.Place(StandardComponents.CapScrew(4, 16, ScrewSeating.OnFace), [new(-20, 0)], PlateTop);
        build.ToAssembly();

        var rows = build.Host!.ConstructionTree()!.Flatten().ToList();
        Assert.Contains(rows, r => r.Label.Contains("ISO 4762 M4×16"));
    }

    [Fact]
    public void Placements_NameThemselvesAfterTheirComponent()
    {
        var build = new ComponentAssembly("plate", Shape.Box(60, 40, 8));
        var screws = build.Place(StandardComponents.CapScrew(4, 16), [new(-20, 0), new(20, 0)], PlateTop);
        Assert.Equal("ISO 4762 M4×16", build.History.NameOf(screws));

        var assembly = build.ToAssembly();
        Assert.Equal("plate", assembly.Occurrences[0].Name);
        Assert.Equal("ISO 4762 M4×16", assembly.Occurrences[1].Name);
        Assert.Equal("ISO 4762 M4×16.2", assembly.Occurrences[2].Name);
    }
}
