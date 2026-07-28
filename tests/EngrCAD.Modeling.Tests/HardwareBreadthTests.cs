using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The component-library breadth families: button head (ISO 7380), countersunk
/// (ISO 10642), hex nuts (ISO 4032), plain washers (ISO 7089), the 60x deep groove
/// bearing family, the opt-in hex socket recess, and fastener stacks that anchor into a
/// PLACED thread provider (insert or nut) instead of cutting their own tap pilot.
/// </summary>
public class HardwareBreadthTests
{
    private const int N = 64;   // tessellation segments per circle for volume checks

    /// <summary>Inscribed n-gon area — what a tessellated circle of radius r encloses.</summary>
    private static double NgonArea(double r, int n = N) => 0.5 * n * r * r * Math.Sin(2 * Math.PI / n);

    /// <summary>Regular hexagon area from the across-flats width (exact — a hex prism
    /// tessellates its own polygon).</summary>
    private static double HexArea(double acrossFlats) => Math.Sqrt(3) / 2 * acrossFlats * acrossFlats;

    /// <summary>n-gon cone frustum volume between radii r1 and r2 over height h.</summary>
    private static double NgonFrustum(double r1, double r2, double h)
    {
        double a1 = NgonArea(r1), a2 = NgonArea(r2);
        return h / 3 * (a1 + a2 + Math.Sqrt(a1 * a2));
    }

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

    // ---- button head (ISO 7380) ----

    [Fact]
    public void ButtonScrew_Catalogue_FollowsIso7380()
    {
        var screw = StandardComponents.ButtonScrew(4, 16);
        Assert.Equal("ISO 7380 M4×16", screw.Designation);
        AssertClose(7.6, screw.HeadDiameter);
        AssertClose(2.2, screw.HeadHeight);
        AssertClose(0, screw.SeatDepth);              // button heads bear on the face
        AssertClose(16, screw.InsertedLength);
        Assert.Equal("M4×0.7", screw.CarriesThread!.Designation);
        Assert.Null(screw.ProvidesThread);

        Assert.Throws<ArgumentOutOfRangeException>(() => StandardComponents.ButtonScrew(7, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => StandardComponents.ButtonScrew(4, 0));
    }

    [Fact]
    public void ButtonScrewBody_HasAnExactSphericalDome()
    {
        var screw = StandardComponents.ButtonScrew(4, 16);
        var solid = screw.Body.ToBrep();
        solid.Validate();

        var mesh = BRepTessellator.Tessellate(solid, N, 24);
        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);    // genus 0

        // Spherical cap πk(3a² + k²)/6 + shank — the profile carries the arc exactly, so
        // the tessellated volume converges on the smooth value (1% at 64 segments).
        double a = 3.8, k = 2.2;
        double exact = Math.PI * k * (3 * a * a + k * k) / 6 + Math.PI * 4 * 16;
        Assert.True(Math.Abs(mesh.Volume() - exact) / exact < 0.01,
            $"volume {mesh.Volume()} vs smooth {exact}");

        // The dome IS the sphere: a point on the cap (implicit revolve of the arc profile
        // is the exact in-plane distance) reads zero.
        double sphereRadius = (a * a + k * k) / (2 * k);
        double theta = 0.5;   // from the axis, inside the cap (edge is at ~1.05 rad)
        var onDome = new Vector3d(
            sphereRadius * Math.Sin(theta), 0, k - sphereRadius + sphereRadius * Math.Cos(theta));
        AssertClose(0, screw.Body.ToImplicit().Evaluate(onDome), 1e-9);
    }

    [Fact]
    public void PlacingAButtonScrew_DrillsPlainClearance()
    {
        var build = new ComponentAssembly("plate", Shape.Box(60, 40, 8));
        build.Place(StandardComponents.ButtonScrew(4, 16), [new(-20, 0)], PlateTop);
        var instances = build.ToAssembly().Flatten();

        var sdf = HostOf(build).ToImplicit();
        AssertClose(2.25, sdf.Evaluate((-20, 0, 0)));     // Ø4.5 normal clearance
        AssertOrigin(instances[1], (-20, 0, 4));          // head bears on the face
    }

    // ---- countersunk (ISO 10642) ----

    [Fact]
    public void CskScrew_Catalogue_DerivesItsHeadFromTheHoleTable()
    {
        var screw = StandardComponents.CskScrew(5, 20);
        Assert.Equal("ISO 10642 M5×20", screw.Designation);

        // The head diameter is the countersink column minus the 0.4 allowance, so screw
        // and hole agree by construction.
        AssertClose(StandardHoles.CountersunkHeadDiameter(5), screw.HeadDiameter);
        AssertClose(11.2, screw.HeadDiameter);
        AssertClose((11.2 - 5) / 2, screw.HeadHeight);    // the sharp 90° cone
        AssertClose(0, screw.SeatDepth);                  // flush by definition
        AssertClose(20, screw.InsertedLength);            // lengths are OVERALL

        // A length shorter than the head is geometric nonsense.
        Assert.Throws<ArgumentOutOfRangeException>(() => StandardComponents.CskScrew(5, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => StandardComponents.CskScrew(7, 20));
    }

    [Fact]
    public void CskScrewBody_IsConeAndShank()
    {
        var screw = StandardComponents.CskScrew(5, 20);
        var solid = screw.Body.ToBrep();
        solid.Validate();

        var mesh = BRepTessellator.Tessellate(solid, N, 24);
        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);

        double k = screw.HeadHeight;
        double exact = NgonFrustum(5.6, 2.5, k) + NgonArea(2.5) * (20 - k);
        AssertClose(exact, mesh.Volume(), 1e-6);
    }

    [Fact]
    public void PlacingACskScrew_CutsTheCountersinkAndSeatsFlush()
    {
        var build = new ComponentAssembly("plate", Shape.Box(60, 40, 8));
        build.Place(StandardComponents.CskScrew(5, 20), [new(-20, 0)], PlateTop);
        var instances = build.ToAssembly().Flatten();

        // Flush: the head TOP is the datum, so the occurrence sits ON the face.
        AssertOrigin(instances[1], (-20, 0, 4));

        var sdf = HostOf(build).ToImplicit();
        AssertClose(2.75, sdf.Evaluate((-20, 0, 0)));               // Ø5.5 bore mid-plate
        // Inside the countersink cone (radius 5.6 at the surface) but outside the bore:
        // void — a plain clearance hole would be solid here.
        Assert.True(sdf.Evaluate((-20 + 4.0, 0, 3.9)) > 0, "countersink cone missing");
        Assert.True(sdf.Evaluate((-20 + 4.0, 0, 0)) < 0, "cone reached too deep");
    }

    // ---- hex socket recess (exact: flat head tops only) ----

    [Fact]
    public void HexSocket_IsAnExactPocketInTheHeadTop()
    {
        var plain = StandardComponents.CapScrew(6, 16, ScrewSeating.OnFace);
        var socketed = StandardComponents.CapScrew(6, 16, ScrewSeating.OnFace, hexSocket: true);
        AssertClose(5, socketed.SocketAcrossFlats);
        AssertClose(3, socketed.SocketDepth);

        var solid = socketed.Body.ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid, N, 24);
        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);    // a pocket changes no genus

        // The recess removes EXACTLY its hexagonal prism: n-gon head + shank, minus the
        // exact hex pocket — every surface involved is planar or an n-gon cylinder.
        double plainVolume = NgonArea(5) * 6 + NgonArea(3) * 16;
        AssertClose(plainVolume - HexArea(5) * 3, mesh.Volume(), 1e-6);

        // The plain screw really is the same body without the recess.
        AssertClose(plainVolume, BRepTessellator.Tessellate(plain.Body.ToBrep(), N, 24).Volume(), 1e-6);
    }

    [Fact]
    public void HexSocket_SurvivesEveryRepresentation()
    {
        // The socketed body is a boolean cascade, so make sure the implicit route agrees:
        // inside the socket void, the field is positive; inside the head wall, negative.
        var screw = StandardComponents.CapScrew(6, 16, ScrewSeating.OnFace, hexSocket: true);
        var sdf = screw.Body.ToImplicit();
        Assert.True(sdf.Evaluate((0, 0, 5)) > 0, "socket centre should be void");
        Assert.True(sdf.Evaluate((4, 0, 5)) < 0, "head wall should be solid");
        Assert.True(sdf.Evaluate((0, 0, 1)) < 0, "head below the pocket floor should be solid");
    }

    // ---- hex nut (ISO 4032) ----

    [Fact]
    public void HexNut_Catalogue_And_Body()
    {
        var nut = StandardComponents.Nut(5);
        Assert.Equal("ISO 4032 M5", nut.Designation);
        AssertClose(8, nut.AcrossFlats);
        AssertClose(4.7, nut.Height);
        AssertClose(0, nut.InsertedLength);               // bears on the face
        Assert.Equal("M5×0.8", nut.ProvidesThread!.Designation);
        AssertClose(4.7, nut.MinimumEngagement!.Value);   // a bolt goes THROUGH its nut
        Assert.Null(nut.MaximumEngagement);

        var solid = nut.Body.ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid, N, 24);
        Assert.True(mesh.IsClosed);
        Assert.Equal(0, mesh.EulerCharacteristic);        // genus 1: bored through

        // Exact hex prism minus the n-gon nominal bore.
        AssertClose(HexArea(8) * 4.7 - NgonArea(2.5) * 4.7, mesh.Volume(), 1e-6);

        Assert.Throws<ArgumentOutOfRangeException>(() => StandardComponents.Nut(7));
    }

    [Fact]
    public void PlacingANut_DrillsTheBoltClearance()
    {
        var build = new ComponentAssembly("plate", Shape.Box(60, 40, 8));
        build.Place(StandardComponents.Nut(5), [new(10, 0)], PlateTop);
        var instances = build.ToAssembly().Flatten();

        // A nut implies a through bolt: ISO 273 normal clearance, nothing tapped.
        AssertClose(2.75, HostOf(build).ToImplicit().Evaluate((10, 0, 0)));
        AssertOrigin(instances[1], (10, 0, 4));
    }

    // ---- plain washer (ISO 7089) ----

    [Fact]
    public void Washer_Catalogue_Body_And_NoOpPreparation()
    {
        var washer = StandardComponents.Washer(5);
        Assert.Equal("ISO 7089 M5", washer.Designation);
        AssertClose(5.3, washer.InnerDiameter);
        AssertClose(10, washer.OuterDiameter);
        AssertClose(1, washer.Thickness);
        AssertClose(0, washer.InsertedLength);

        var solid = washer.Body.ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid, N, 24);
        Assert.True(mesh.IsClosed);
        AssertClose((NgonArea(5) - NgonArea(2.65)) * 1, mesh.Volume(), 1e-6);

        // Placing a washer cuts NOTHING — the hole belongs to the screw it spaces.
        var build = new ComponentAssembly("plate", Shape.Box(60, 40, 8));
        build.Place(washer, [new(10, 0)], PlateTop);
        var instances = build.ToAssembly().Flatten();
        Assert.True(HostOf(build).ToImplicit().Evaluate((10, 0, 0)) < 0, "washer removed material");
        AssertOrigin(instances[1], (10, 0, 4));
    }

    // ---- deep groove bearing (60x family) ----

    [Fact]
    public void Bearing_Catalogue_And_TwoRingBody()
    {
        var bearing = StandardComponents.Bearing("608");
        Assert.Equal("Bearing 608", bearing.Designation);
        AssertClose(8, bearing.Bore);
        AssertClose(22, bearing.OuterDiameter);
        AssertClose(7, bearing.Width);
        AssertClose(7, bearing.InsertedLength);           // pressed flush

        var solid = bearing.Body.ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid, N, 24);
        Assert.True(mesh.IsClosed);
        Assert.Equal(0, mesh.EulerCharacteristic);        // two genus-1 shells: 0 + 0

        // Radial thirds: inner ring 4→6.333, gap, outer ring 8.667→11.
        double t = (11.0 - 4.0) / 3;
        double exact = (NgonArea(11) - NgonArea(11 - t)) * 7 + (NgonArea(4 + t) - NgonArea(4)) * 7;
        AssertClose(exact, mesh.Volume(), 1e-6);

        Assert.Throws<ArgumentOutOfRangeException>(() => StandardComponents.Bearing("999"));
    }

    [Fact]
    public void PlacingABearing_CutsItsFlushHousingPocket()
    {
        var top = SketchPlane.At((0, 0, 10), Vector3d.UnitX, Vector3d.UnitY);
        var build = new ComponentAssembly("housing", Shape.Box(60, 40, 20));
        build.Place(StandardComponents.Bearing("608"), [new(0, 0)], top);
        var instances = build.ToAssembly().Flatten();

        // Flat-bottomed Ø22 pocket, exactly one width (7) deep: on the axis mid-pocket
        // the nearest surface is the pocket bottom, 3.5 away — a real face, so the
        // difference SDF is exact there.
        AssertClose(3.5, HostOf(build).ToImplicit().Evaluate((0, 0, 10 - 3.5)));
        AssertOrigin(instances[1], (0, 0, 10));           // outer face flush
    }

    // ---- fastener stacks anchored into a PLACED provider ----

    [Fact]
    public void PlaceThrough_AnchorsIntoAPlacedInsert()
    {
        var mateFace = SketchPlane.At((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY);
        var coverFace = SketchPlane.At((0, 0, 10), Vector3d.UnitX, Vector3d.UnitY);

        var basePlate = new ComponentAssembly("base", Shape.Box(60, 40, 20).Translate(0, 0, -10));
        var insertPlacement = basePlate.Place(
            StandardComponents.TrisertInsert(5), [new(-20, 0), new(20, 0)], mateFace);

        var cover = new ComponentAssembly("cover", Shape.Box(60, 40, 10).Translate(0, 0, 5));
        // M5×12 counterbored: grip = (10 − 5.5) = 4.5, engagement 7.5 ≤ the insert's 9.5.
        cover.PlaceThrough(StandardComponents.CapScrew(5, 12), [new(-20, 0), new(20, 0)],
            coverFace, basePlate, mateFace, insertPlacement);

        var coverAssembly = cover.ToAssembly();
        var baseAssembly = basePlate.ToAssembly();

        // The far body got NO new preparation — the insert's pilot is the only cut.
        // (Probed mid-pilot, clear of the tool's overshoot top, where the difference SDF
        // is the exact radial distance.)
        Assert.Equal(2, basePlate.History.Features.Count);   // host + insert placement
        var baseSdf = HostOf(basePlate).ToImplicit();
        AssertClose(StandardHoles.TrisertDiameter(5) / 2, baseSdf.Evaluate((-20, 0, -5)));

        // Near body prepared as usual; the screw placed once per point.
        var coverSdf = HostOf(cover).ToImplicit();
        AssertClose(2.75, coverSdf.Evaluate((-20, 0, 4)));   // Ø5.5 clearance under the cbore
        Assert.Equal(3, coverAssembly.Occurrences.Count);    // host + 2 screws
        Assert.Equal(3, baseAssembly.Occurrences.Count);     // host + 2 inserts
    }

    [Fact]
    public void PlaceThrough_AnchorsIntoAPlacedNut()
    {
        var coverFace = SketchPlane.At((0, 0, 10), Vector3d.UnitX, Vector3d.UnitY);
        // The base's BOTTOM face: normal −Z (out of the body), so X × (−Y) axes.
        var bottomFace = SketchPlane.At((0, 0, -20), Vector3d.UnitX, -Vector3d.UnitY);

        var basePlate = new ComponentAssembly("base", Shape.Box(60, 40, 20).Translate(0, 0, -10));
        var nutPlacement = basePlate.Place(StandardComponents.Nut(5), [new(-20, 0)], bottomFace);

        var cover = new ComponentAssembly("cover", Shape.Box(60, 40, 10).Translate(0, 0, 5));
        // Grip to the nut's face = 4.5 + 20 = 24.5; M5×30 leaves 5.5 ≥ the nut's 4.7.
        cover.PlaceThrough(StandardComponents.CapScrew(5, 30), [new(-20, 0)],
            coverFace, basePlate, bottomFace, nutPlacement);

        cover.ToAssembly();
        var baseAssembly = basePlate.ToAssembly();

        // Nutted joints tap nothing: the base has the nut's clearance hole all through.
        Assert.Equal(2, basePlate.History.Features.Count);
        AssertClose(2.75, HostOf(basePlate).ToImplicit().Evaluate((-20, 0, -10)));

        // The nut hangs on the bottom face, reaching down (its +Z is world −Z).
        AssertOrigin(baseAssembly.Flatten()[1], (-20, 0, -20));

        // Too short to pass through the nut: engagement 3.5 < the required 4.7.
        var tooShort = Assert.Throws<ArgumentException>(() => cover.PlaceThrough(
            StandardComponents.CapScrew(5, 28), [new(-20, 0)],
            coverFace, basePlate, bottomFace, nutPlacement));
        Assert.Contains("at least", tooShort.Message);
    }

    [Fact]
    public void PlaceThrough_AnchorInto_RejectsWhatCannotEngage()
    {
        var mateFace = SketchPlane.At((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY);
        var coverFace = SketchPlane.At((0, 0, 10), Vector3d.UnitX, Vector3d.UnitY);
        var basePlate = new ComponentAssembly("base", Shape.Box(60, 40, 20).Translate(0, 0, -10));
        var insertPlacement = basePlate.Place(
            StandardComponents.TrisertInsert(5), [new(-20, 0)], mateFace);
        var cover = new ComponentAssembly("cover", Shape.Box(60, 40, 10).Translate(0, 0, 5));

        // Too long: engagement 11.5 exceeds the insert's 9.5 — it bottoms out.
        var tooLong = Assert.Throws<ArgumentException>(() => cover.PlaceThrough(
            StandardComponents.CapScrew(5, 16), [new(-20, 0)],
            coverFace, basePlate, mateFace, insertPlacement));
        Assert.Contains("bottoms out", tooLong.Message);

        // Thread mismatch: an M4 screw into an M5 insert.
        var mismatch = Assert.Throws<ArgumentException>(() => cover.PlaceThrough(
            StandardComponents.CapScrew(4, 12), [new(-20, 0)],
            coverFace, basePlate, mateFace, insertPlacement));
        Assert.Contains("Thread mismatch", mismatch.Message);

        // A point with no insert under it would silently miss — refused by coordinates.
        var missed = Assert.Throws<ArgumentException>(() => cover.PlaceThrough(
            StandardComponents.CapScrew(5, 12), [new(0, 0)],
            coverFace, basePlate, mateFace, insertPlacement));
        Assert.Contains("miss", missed.Message);

        // A dowel carries no thread to engage the insert.
        var unthreaded = Assert.Throws<ArgumentException>(() => cover.PlaceThrough(
            StandardComponents.Dowel(5, 30), [new(-20, 0)],
            coverFace, basePlate, mateFace, insertPlacement));
        Assert.Contains("carries no thread", unthreaded.Message);

        // A placement that belongs to some other model is refused.
        var stray = new ComponentFeature(StandardComponents.TrisertInsert(5), [new(-20, 0)]);
        Assert.Throws<ArgumentException>(() => cover.PlaceThrough(
            StandardComponents.CapScrew(5, 12), [new(-20, 0)],
            coverFace, basePlate, mateFace, stray));

        // A washer provides no thread.
        var washerPlacement = basePlate.Place(StandardComponents.Washer(5), [new(-20, 0)], mateFace);
        var unprovided = Assert.Throws<ArgumentException>(() => cover.PlaceThrough(
            StandardComponents.CapScrew(5, 12), [new(-20, 0)],
            coverFace, basePlate, mateFace, washerPlacement));
        Assert.Contains("provides no thread", unprovided.Message);

        // None of those partial calls left a placement on the near body.
        Assert.Empty(cover.Placements);
    }
}
