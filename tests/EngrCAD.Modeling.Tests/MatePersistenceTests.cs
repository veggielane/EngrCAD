using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Mates serialize alongside the FeatureHistory conventions: JSON out, warnings back
/// in. Query-backed ends round-trip as their GeometryRef descriptors and re-resolve
/// eagerly at load; lambda-backed ends load from their pinned coordinates with a
/// warning; a missing occurrence skips the mate and says so.
/// </summary>
public class MatePersistenceTests
{
    private static Frame3d At(double x, double y, double z) =>
        Frame3d.FromXY((x, y, z), Vector3d.UnitX, Vector3d.UnitY);

    private static Part BoxPart(string name) => new(name, MeshPrimitives.Box(4, 4, 4));

    private static Part BoredPlate(string name) =>
        new(name, Shape.Box(40, 30, 6).Drill(
            HoleSpec.Simple(6), [new(0, 0)], 8,
            SketchPlane.At((0, 0, 3), Vector3d.UnitX, Vector3d.UnitY)));

    /// <summary>Two bored plates and a nested carrier with a third — the fixture every
    /// test rebuilds fresh, so load always runs against un-solved frames.</summary>
    private static (Assembly Rig, Occurrence Lower, Occurrence Upper) Rig()
    {
        var carrier = new Assembly("carrier");
        carrier.Add(BoredPlate("plate"), At(3, 4, 5));
        var rig = new Assembly("rig");
        var lower = rig.Add(BoredPlate("lower"));
        var upper = rig.Add(BoredPlate("upper"), At(21, -14, 33));
        rig.Add(carrier, At(17, 9, 25));
        return (rig, lower, upper);
    }

    private static MateSet QueryMates(Assembly rig, Occurrence lower, Occurrence upper) =>
        new MateSet(rig)
            .Ground(lower)
            .Add(Mate.Concentric(
                MateGeometry.CylindricalFace(lower, FaceRef.One(FaceSetRef.Cylindrical())),
                MateGeometry.CylindricalFace(upper, FaceRef.One(FaceSetRef.Cylindrical())),
                "bore"))
            .Add(Mate.Planar(
                MateGeometry.PlanarFace(lower, FaceRef.Top),
                MateGeometry.PlanarFace(rig, "carrier/plate", FaceRef.Bottom),
                gap: 2.5, name: "seat"));

    [Fact]
    public void QueryBackedMates_RoundTripThroughJson()
    {
        var (rig, lower, upper) = Rig();
        string json = QueryMates(rig, lower, upper).SaveMates();

        // The queries serialized as descriptors, not only numbers.
        Assert.Contains("cylindricalFace(one(cylindrical))", json);
        Assert.Contains("planarFace(extreme(planar([0,0,1]),[0,0,1]))", json);
        Assert.Contains("carrier/plate", json);

        // A fresh, un-posed rig: load rebuilds the set with zero warnings...
        var (rig2, lower2, upper2) = Rig();
        var loaded = new MateSet(rig2);
        Assert.Empty(loaded.LoadMates(json));
        Assert.Equal(2, loaded.Mates.Count);
        Assert.Contains(lower2, loaded.Grounded);

        // ...and solving it seats the plates exactly as the original set would.
        var result = loaded.Solve();
        Assert.True(result.Converged, result.ToString());
        Assert.Equal(0, upper2.Frame.Origin.X, 7);           // bore on the world Z axis
        var plate = rig2.Flatten().Single(i => i.Path == "rig/carrier/plate");
        Assert.Equal(5.5, plate.World.TransformPoint(new Vector3d(0, 0, -3)).Z, 7);   // 3 + 2.5 gap
    }

    [Fact]
    public void AngleValueAndWorldEnds_RoundTripExactly()
    {
        var rig = new Assembly("rig");
        var arm = rig.Add(BoxPart("arm"), Frame3d.FromXY((5, 0, 0), (1, 0.4, 0), (-0.4, 1, 0)));

        var mates = new MateSet(rig)
            .Add(Mate.Angle(
                MateGeometry.Axis(arm, Vector3d.Zero, Vector3d.UnitX),
                MateGeometry.World(Vector3d.Zero, Vector3d.UnitX), 30, "tilt"))
            .Add(Mate.Coincident(
                MateGeometry.Point(arm, Vector3d.Zero),
                MateGeometry.World((7, 8, 9)), "pin"));
        string json = mates.SaveMates();

        var rig2 = new Assembly("rig");
        var arm2 = rig2.Add(BoxPart("arm"), Frame3d.FromXY((5, 0, 0), (1, 0.4, 0), (-0.4, 1, 0)));
        var loaded = new MateSet(rig2);
        Assert.Empty(loaded.LoadMates(json));

        var result = loaded.Solve();
        Assert.True(result.Converged, result.ToString());
        // The stored value is radians (no degree conversion re-entered on load).
        double cosine = arm2.Frame.ToWorldVector(Vector3d.UnitX).Dot(Vector3d.UnitX);
        Assert.Equal(Math.Cos(30 * Math.PI / 180), cosine, 8);
        Assert.Equal(9, arm2.Frame.Origin.Z, 8);
    }

    [Fact]
    public void SavedJson_IsAFixedPointUnderRoundTrip()
    {
        var (rig, lower, upper) = Rig();
        string json = QueryMates(rig, lower, upper).SaveMates();

        var (rig2, lower2, upper2) = Rig();
        var loaded = new MateSet(rig2);
        Assert.Empty(loaded.LoadMates(json));
        // Save(Load(Save(x))) == Save(x): descriptors, paths, and coordinates are all
        // stable (the already-unit direction rule in MateRef is what makes the numbers
        // hold bit for bit).
        Assert.Equal(json, loaded.SaveMates());
    }

    [Fact]
    public void LambdaSelectors_LoadFromPinnedCoordinatesWithAWarning()
    {
        var (rig, lower, upper) = Rig();
        var mates = new MateSet(rig)
            .Ground(lower)
            .Add(Mate.Planar(
                MateGeometry.PlanarFace(lower, s => s.PlanarFacesWithNormal(Vector3d.UnitZ).First()),
                MateGeometry.PlanarFace(upper, s => s.PlanarFacesWithNormal(-Vector3d.UnitZ).First()),
                name: "seat"));
        string json = mates.SaveMates();
        Assert.Contains("opaque", json);   // the marker is written, as FeatureHistory does

        var (rig2, _, upper2) = Rig();
        var loaded = new MateSet(rig2);
        var warnings = loaded.LoadMates(json);
        Assert.Equal(2, warnings.Count);   // one per lambda end
        Assert.All(warnings, w => Assert.Contains("pinned coordinates", w));

        // The mate still loads and still solves — it just stopped being semantic.
        var result = loaded.Solve();
        Assert.True(result.Converged, result.ToString());
        Assert.Equal(3, upper2.Frame.ToWorld(new Vector3d(0, 0, -3)).Z, 7);
    }

    [Fact]
    public void MissingOccurrencesAndQueries_WarnInsteadOfThrowing()
    {
        var (rig, lower, upper) = Rig();
        string json = QueryMates(rig, lower, upper).SaveMates();

        // A rig missing the carrier: the deep mate is skipped by name, the direct one
        // still loads and re-resolves its queries.
        var bare = new Assembly("rig");
        var bareLower = bare.Add(BoredPlate("lower"));
        bare.Add(BoredPlate("upper"), At(21, -14, 33));
        var loaded = new MateSet(bare);
        var warnings = loaded.LoadMates(json);

        Assert.Single(loaded.Mates);
        Assert.Equal("bore", loaded.Mates[0].Name);
        Assert.Contains(bareLower, loaded.Grounded);
        Assert.Contains(warnings, w => w.Contains("seat") && w.Contains("carrier"));

        // A query that stops matching (no bore in a plain box) falls back to pinned
        // coordinates rather than losing the mate.
        var plain = new Assembly("rig");
        plain.Add(new Part("lower", Shape.Box(40, 30, 6)));
        plain.Add(new Part("upper", Shape.Box(40, 30, 6)), At(21, -14, 33));
        var carrier = new Assembly("carrier");
        carrier.Add(new Part("plate", Shape.Box(40, 30, 6)), At(3, 4, 5));
        plain.Add(carrier, At(17, 9, 25));
        var fallback = new MateSet(plain);
        var fallbackWarnings = fallback.LoadMates(json);
        Assert.Equal(2, fallback.Mates.Count);
        Assert.Contains(fallbackWarnings, w => w.Contains("pinned coordinates"));
    }
}
