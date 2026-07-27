using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>The typed geometry-input vocabulary: descriptors (cache key + serialized
/// form), resolution semantics through regeneration, and the failure messages.</summary>
public class GeometryRefTests
{
    // ---- fixtures ----

    private sealed class PlateFeature : Feature
    {
        [Param(Min = 1)]
        public double Thickness { get; init; } = 8;

        public override Shape Apply(FeatureContext context) =>
            Shape.Extrude(Sketch.Rectangle(40, 30), Thickness);
    }

    /// <summary>A feature that only declares a plane, so tests can watch the resolution
    /// without any geometry noise.</summary>
    private sealed class ProbePlaneFeature : Feature
    {
        public SketchPlane? Seen { get; private set; }
        public int Applications { get; private set; }

        public PlaneRef Plane { get; init; } = PlaneRef.TopPlane;

        public override Shape Apply(FeatureContext context)
        {
            Applications++;
            Seen = Plane.Resolve(context, nameof(Plane));
            return context.Body!;
        }
    }

    private sealed class ProbeFacesFeature : Feature
    {
        public int Matched { get; private set; }

        public FaceSetRef Faces { get; init; } = FaceSetRef.PlanarWithNormal(Vector3d.UnitZ);

        public override Shape Apply(FeatureContext context)
        {
            Matched = Faces.Resolve(context, nameof(Faces)).Count;
            return context.Body!;
        }
    }

    private sealed class ProbeAxisFeature : Feature
    {
        public Ray3d Seen { get; private set; }

        public AxisRef Axis { get; init; } = AxisRef.Z;

        public override Shape Apply(FeatureContext context)
        {
            Seen = Axis.Resolve(context, nameof(Axis));
            return context.Body!;
        }
    }

    private static BrepSolid Plate(double thickness = 8) =>
        Shape.Extrude(Sketch.Rectangle(40, 30), thickness).ToBrep();

    private static BrepSolid DrilledPlate() =>
        Shape.Extrude(Sketch.Rectangle(40, 30), 8)
            .Drill(StandardHoles.Clearance(6), [new Vector2d(0, 0)], 20,
                SketchPlane.At((0, 0, 8), Vector3d.UnitX, Vector3d.UnitY))
            .ToBrep();

    // ---- descriptors: cache key and serialized form are one string ----

    public static TheoryData<GeometryRef> NamedRefs => new()
    {
        FaceSetRef.All,
        FaceSetRef.PlanarWithNormal(Vector3d.UnitZ),
        FaceSetRef.PlanarWithNormal(new Vector3d(0.5, -0.25, 3)),
        FaceSetRef.Cylindrical(),
        FaceSetRef.Cylindrical(3.3),
        FaceSetRef.PlanarWithNormal(Vector3d.UnitZ).Optional(),
        FaceSetRef.RimFacesOf(EdgeSetRef.RimOf(FaceSetRef.PlanarWithNormal(Vector3d.UnitZ))),
        FaceRef.Top,
        FaceRef.Bottom,
        FaceRef.One(FaceSetRef.Cylindrical(2)),
        PlaneRef.TopPlane,
        PlaneRef.OnTopFace,
        PlaneRef.WorldXY,
        PlaneRef.WorldXZ,
        PlaneRef.At(SketchPlane.At((1.5, -2, 3.25), Vector3d.UnitY, Vector3d.UnitZ)),
        EdgeSetRef.Convex,
        EdgeSetRef.Circular(),
        EdgeSetRef.Circular(4.5),
        EdgeSetRef.RimOf(FaceSetRef.All),
        AxisRef.Z,
        AxisRef.Of((1, 2, 3), (0, 1, 1)),
        AxisRef.OfCylindrical(FaceSetRef.Cylindrical(3)),
    };

    [Theory]
    [MemberData(nameof(NamedRefs))]
    public void NamedReferences_RoundTripThroughTheirDescriptor(GeometryRef reference)
    {
        Assert.True(reference.IsSerializable, reference.Descriptor);
        var parsed = GeometryRef.Parse(reference.Descriptor, reference.GetType());
        Assert.Equal(reference.Descriptor, parsed.Descriptor);
        Assert.Equal(reference.Subject, parsed.Subject);
        Assert.Equal(reference.Cardinality, parsed.Cardinality);
    }

    [Fact]
    public void ExplicitPlane_RoundTripsExactly()
    {
        var plane = SketchPlane.At((1.0 / 3, -7.25e-9, 1e17), Vector3d.UnitX, Vector3d.UnitY);
        var parsed = (PlaneRef)GeometryRef.Parse(PlaneRef.At(plane).Descriptor, typeof(PlaneRef));
        var back = parsed.Resolve((BrepSolid?)null, "Plane");
        Assert.Equal(plane.Origin.X, back.Origin.X);
        Assert.Equal(plane.Origin.Y, back.Origin.Y);
        Assert.Equal(plane.Origin.Z, back.Origin.Z);
        Assert.Equal(plane.Normal.Z, back.Normal.Z);
    }

    [Fact]
    public void DistinctQueries_GetDistinctDescriptors()
    {
        Assert.NotEqual(
            FaceSetRef.PlanarWithNormal(Vector3d.UnitZ).Descriptor,
            FaceSetRef.PlanarWithNormal(-Vector3d.UnitZ).Descriptor);
        Assert.NotEqual(PlaneRef.TopPlane.Descriptor, PlaneRef.OnTopFace.Descriptor);
        Assert.NotEqual(FaceSetRef.Cylindrical(3).Descriptor, FaceSetRef.Cylindrical(4).Descriptor);
        Assert.Equal(FaceSetRef.Cylindrical(3).Descriptor, FaceSetRef.Cylindrical(3).Descriptor);
    }

    [Fact]
    public void OpaqueReferences_PrintAMarkerAndRefuseToParse()
    {
        var reference = FaceSetRef.From("top(ish),faces", _ => []);
        Assert.False(reference.IsSerializable);
        Assert.Equal("opaque(topishfaces)", reference.Descriptor);
        Assert.Throws<FormatException>(() =>
            GeometryRef.Parse(reference.Descriptor, typeof(FaceSetRef)));
        Assert.Throws<FormatException>(() => FaceSetRef.Parse("planar([0,0,1]) trailing"));
        Assert.Throws<FormatException>(() => FaceSetRef.Parse("spherical"));
    }

    // ---- resolution semantics ----

    [Fact]
    public void TopPlane_IsWorldAligned_AndTracksThickness()
    {
        var top = PlaneRef.TopPlane.Resolve(Plate(8), "Plane");
        Assert.Equal(new Vector3d(0, 0, 8), top.Origin);
        Assert.Equal(Vector3d.UnitX, top.XAxis);
        Assert.Equal(Vector3d.UnitZ, top.Normal);

        var thicker = PlaneRef.TopPlane.Resolve(Plate(13), "Plane");
        Assert.Equal(13, thicker.Origin.Z);
    }

    [Fact]
    public void OnTopFace_UsesTheFacesOwnFrame_NotTheWorldOrigin()
    {
        var solid = Shape.Extrude(Sketch.Rectangle(40, 30), 8)
            .Translate((100, 50, 0))
            .ToBrep();

        var world = PlaneRef.TopPlane.Resolve(solid, "Plane");
        var onFace = PlaneRef.OnTopFace.Resolve(solid, "Plane");

        Assert.Equal(new Vector3d(0, 0, 8), world.Origin);
        Assert.Equal(8, onFace.Origin.Z);
        Assert.True(onFace.Origin.DistanceTo(new Vector3d(100, 50, 8)) < 1e-9,
            $"the face frame's origin should sit on the face, got {onFace.Origin}");
        Assert.True(onFace.Normal.Dot(Vector3d.UnitZ) > 1 - 1e-9);
    }

    [Fact]
    public void FaceQueries_MatchTheExpectedTopology()
    {
        var solid = DrilledPlate();

        Assert.Equal(6 + 1, FaceSetRef.All.Resolve(solid, "f").Count); // box + bore wall
        Assert.Single(FaceSetRef.PlanarWithNormal(Vector3d.UnitZ).Resolve(solid, "f"));
        var bore = Assert.Single(FaceSetRef.Cylindrical().Resolve(solid, "f"));
        Assert.True(bore.IsCylindrical(out _, out _, out double radius));
        Assert.InRange(radius, 3, 3.5); // ISO 273 medium clearance for M6

        Assert.Empty(FaceSetRef.Cylindrical(99).Optional().Resolve(solid, "f"));
        Assert.Single(FaceSetRef.Cylindrical(radius).Resolve(solid, "f"));
    }

    [Fact]
    public void EdgeQueries_FeedRimSurgeryThroughRimFacesOf()
    {
        var solid = Plate();
        var rim = EdgeSetRef.RimOf(FaceSetRef.PlanarWithNormal(Vector3d.UnitZ));
        Assert.Equal(4, rim.Resolve(solid, "Edges").Count);

        var faces = FaceSetRef.RimFacesOf(rim).Resolve(solid, "Edges");
        Assert.Single(faces);
        Assert.True(faces[0].IsPlanar(out var origin, out var normal));
        Assert.Equal(8, origin.Z);
        Assert.True(normal.Dot(Vector3d.UnitZ) > 1 - 1e-9);

        Assert.Equal(RefCardinality.CompleteRim, FaceSetRef.RimFacesOf(rim).Cardinality);
        Assert.Equal(12, EdgeSetRef.Convex.Resolve(solid, "Edges").Count);
    }

    [Fact]
    public void AxisOfCylindrical_ReadsTheBore()
    {
        var axis = AxisRef.OfCylindrical(FaceSetRef.Cylindrical()).Resolve(DrilledPlate(), "Axis");
        Assert.True(axis.Origin.X is > -1e-9 and < 1e-9);
        Assert.True(axis.Origin.Y is > -1e-9 and < 1e-9);
        Assert.True(Math.Abs(axis.Direction.Dot(Vector3d.UnitZ)) > 1 - 1e-9);
    }

    [Fact]
    public void Cardinality_IsCarriedByTheType()
    {
        Assert.Equal(RefCardinality.AtLeastOne, FaceSetRef.All.Cardinality);
        Assert.Equal(RefCardinality.Any, FaceSetRef.All.Optional().Cardinality);
        Assert.Equal(RefCardinality.ExactlyOne, FaceRef.Top.Cardinality);
        Assert.Equal(RefCardinality.ExactlyOne, PlaneRef.TopPlane.Cardinality);
        Assert.Equal(RefCardinality.ExactlyOne, AxisRef.Z.Cardinality);
        Assert.Equal(RefCardinality.AtLeastOne, EdgeSetRef.Convex.Cardinality);
    }

    [Fact]
    public void ExplicitReferences_ResolveWithoutABody()
    {
        Assert.False(PlaneRef.WorldXY.RequiresBody);
        Assert.False(AxisRef.Z.RequiresBody);
        Assert.True(PlaneRef.TopPlane.RequiresBody);
        Assert.True(FaceSetRef.All.RequiresBody);

        Assert.Equal(Vector3d.Zero, PlaneRef.WorldXY.Resolve((BrepSolid?)null, "Plane").Origin);
        Assert.Equal(Vector3d.UnitZ, AxisRef.Z.Resolve((BrepSolid?)null, "Axis").Direction);
    }

    // ---- failure messages ----

    [Fact]
    public void MissingMatch_NamesTheInputAndWhatItWanted()
    {
        var exception = Assert.Throws<GeometryInputException>(() =>
            FaceSetRef.Cylindrical().Resolve(Plate(), "Bores"));
        Assert.Equal("Bores: expected at least one cylindrical face, found none.", exception.Message);

        var top = Assert.Throws<GeometryInputException>(() =>
            PlaneRef.TopPlane.Resolve(Shape.Sphere(5).ToBrep(), "Plane"));
        Assert.Equal(
            "Plane: expected the outermost planar face with outward normal (0, 0, 1) along (0, 0, 1), found none.",
            top.Message);
    }

    [Fact]
    public void AmbiguousMatch_IsAsMuchAFailureAsNone()
    {
        var exception = Assert.Throws<GeometryInputException>(() =>
            FaceRef.One(FaceSetRef.All).Resolve(Plate(), "Seat"));
        Assert.StartsWith("Seat: expected exactly one face, found 6.", exception.Message);
        Assert.Contains("FaceRef.Extreme", exception.Message);
    }

    [Fact]
    public void NoBodyYet_SaysSo()
    {
        var exception = Assert.Throws<GeometryInputException>(() =>
            PlaneRef.TopPlane.Resolve((BrepSolid?)null, "Plane"));
        Assert.StartsWith("Plane: expected ", exception.Message);
        Assert.Contains("no body exists yet", exception.Message);
    }

    [Fact]
    public void PartialRimSelection_IsRefusedByNameThroughTheInput()
    {
        var solid = Plate();
        var oneEdge = EdgeSetRef.From("one top edge",
            s => s.PlanarFacesWithNormal(Vector3d.UnitZ).SelectMany(f => f.RimEdges()).Take(1));
        var exception = Assert.Throws<GeometryInputException>(() =>
            FaceSetRef.RimFacesOf(oneEdge).Resolve(solid, "Rims"));
        Assert.StartsWith("Rims: ", exception.Message);
        Assert.Contains("not part of a fully selected planar face rim", exception.Message);
    }

    // ---- through the feature history ----

    [Fact]
    public void Validation_ResolvesInputsBeforeApply_AndNamesTheFailure()
    {
        var history = new FeatureHistory();
        history.Add(new PlateFeature());
        var probe = new ProbePlaneFeature { Plane = PlaneRef.On(FaceRef.One(FaceSetRef.Cylindrical())) };
        history.Add(probe);
        history.Add(new PlateFeature());

        var result = history.Regenerate();
        Assert.False(result.Succeeded);
        Assert.Equal(FeatureOutcome.Failed, result.Statuses[1].Outcome);
        Assert.Equal(
            "Plane: expected exactly one cylindrical face, found 0.",
            result.Statuses[1].Error);
        Assert.Equal(0, probe.Applications);                     // never ran
        Assert.Equal(FeatureOutcome.Skipped, result.Statuses[2].Outcome);
        Assert.NotNull(result.Body);                             // last good prefix survives
    }

    [Fact]
    public void FirstFeature_WithABodyReference_FailsCleanlyInsteadOfThrowing()
    {
        var history = new FeatureHistory();
        history.Add(new ProbePlaneFeature());
        var result = history.Regenerate();

        Assert.Equal(FeatureOutcome.Failed, result.Statuses[0].Outcome);
        Assert.Contains("no body exists yet", result.Statuses[0].Error);
        Assert.Null(result.Body);
    }

    [Fact]
    public void SuppressedFeatures_SkipInputValidationEntirely()
    {
        var history = new FeatureHistory();
        history.Add(new PlateFeature());
        history.Add(new ProbePlaneFeature
        {
            Plane = PlaneRef.On(FaceRef.One(FaceSetRef.Cylindrical())),
            Suppressed = true,
        });
        var result = history.Regenerate();

        Assert.True(result.Succeeded, result.ToString());
        Assert.Equal(FeatureOutcome.Suppressed, result.Statuses[1].Outcome);
    }

    [Fact]
    public void Inputs_ReResolveAgainstEveryRegeneration()
    {
        var history = new FeatureHistory();
        history.Add(new PlateFeature { Thickness = 8 });
        var probe = new ProbePlaneFeature();
        history.Add(probe);

        Assert.True(history.Regenerate().Succeeded);
        Assert.Equal(8, probe.Seen!.Value.Origin.Z);

        // Change an upstream thickness: the plane re-seats on the NEW top face.
        history.Replace(0, new PlateFeature { Thickness = 21 });
        Assert.True(history.Regenerate().Succeeded);
        Assert.Equal(21, probe.Seen!.Value.Origin.Z);
    }

    [Fact]
    public void ResolutionIsSharedBetweenValidationAndApply()
    {
        int selections = 0;
        var history = new FeatureHistory();
        history.Add(new PlateFeature());
        var probe = new ProbeFacesFeature
        {
            Faces = FaceSetRef.From("counted", solid =>
            {
                selections++;
                return solid.PlanarFacesWithNormal(Vector3d.UnitZ);
            }),
        };
        history.Add(probe);

        Assert.True(history.Regenerate().Succeeded);
        Assert.Equal(1, probe.Matched);
        // Validation resolved it; Apply reused the context's cached answer.
        Assert.Equal(1, selections);
    }

    [Fact]
    public void ChangingAReference_InvalidatesTheRegenerationCache()
    {
        var history = new FeatureHistory();
        history.Add(new PlateFeature());
        history.Add(new HoleFeature(StandardHoles.Clearance(4), [new Vector2d(0, 0)]) { Depth = 20 });

        Assert.True(history.Regenerate().Succeeded);
        var cached = history.Regenerate();
        Assert.All(cached.Statuses, s => Assert.Equal(FeatureOutcome.Cached, s.Outcome));

        // Only the geometry reference changes; the descriptor is what the cache key sees.
        Assert.Empty(history.LoadParameters(
            """{ "HoleFeature": { "Plane": "plane([0,0,8],[1,0,0],[0,1,0])" } }"""));
        var dirty = history.Regenerate();
        Assert.True(dirty.Succeeded, dirty.ToString());
        Assert.Equal(FeatureOutcome.Cached, dirty.Statuses[0].Outcome);
        Assert.Equal(FeatureOutcome.Applied, dirty.Statuses[1].Outcome);
    }

    // ---- serialization ----

    [Fact]
    public void NamedReferences_RoundTripThroughJson()
    {
        var history = new FeatureHistory();
        history.Add(new PlateFeature());
        history.Add(new HoleFeature(StandardHoles.Clearance(4), [new Vector2d(0, 0)]) { Depth = 20 });
        Assert.True(history.Regenerate().Succeeded);

        string saved = history.SaveParameters();
        Assert.Contains("\"Plane\": \"topPlane\"", saved);

        // Retarget the drilling plane from JSON alone.
        var warnings = history.LoadParameters("""
            { "HoleFeature": { "Plane": "plane([0,0,8],[1,0,0],[0,1,0])" } }
            """);
        Assert.Empty(warnings);
        var hole = (HoleFeature)history.Features[1];
        Assert.Equal("plane([0,0,8],[1,0,0],[0,1,0])", hole.Plane.Descriptor);
        Assert.True(history.Regenerate().Succeeded);

        // And back.
        Assert.Empty(history.LoadParameters(saved));
        Assert.Equal("topPlane", ((HoleFeature)history.Features[1]).Plane.Descriptor);
    }

    [Fact]
    public void OpaqueReferences_LoadAsAWarning_NotACrash()
    {
        var history = new FeatureHistory();
        history.Add(new PlateFeature());
        history.Add(new FilletRimFeature
        {
            Radius = 1,
            Faces = FaceSetRef.From("top", s => s.PlanarFacesWithNormal(Vector3d.UnitZ)),
        });

        string saved = history.SaveParameters();
        Assert.Contains("opaque(top)", saved);

        var warnings = history.LoadParameters(saved);
        string warning = Assert.Single(warnings);
        Assert.Contains("FilletRimFeature.Faces", warning);
        Assert.Contains("opaque marker", warning);
        // The reference itself is untouched, so the model still regenerates.
        Assert.True(history.Regenerate().Succeeded);
    }

    // ---- back-compat of the incumbent spellings ----

    [Fact]
    public void SketchPlaneStillConverts_AndNullStillMeansTopPlane()
    {
        SketchPlane explicitPlane = SketchPlane.At((0, 0, 8), Vector3d.UnitX, Vector3d.UnitY);
        SketchPlane? absent = null;

        var withPlane = new HoleFeature(StandardHoles.Clearance(4), [new Vector2d(0, 0)])
        {
            Plane = explicitPlane,
        };
        var withNull = new HoleFeature(StandardHoles.Clearance(4), [new Vector2d(0, 0)])
        {
            Plane = absent,
        };
        var withLiteralNull = new HoleFeature(StandardHoles.Clearance(4), [new Vector2d(0, 0)])
        {
            Plane = null!,
        };

        Assert.Equal("plane([0,0,8],[1,0,0],[0,1,0])", withPlane.Plane.Descriptor);
        Assert.Equal("topPlane", withNull.Plane.Descriptor);
        Assert.Equal("topPlane", withLiteralNull.Plane.Descriptor);
    }

    [Fact]
    public void RimFeatures_DefaultToTheTopFaces_AndAcceptOtherSelections()
    {
        Assert.Equal(
            FaceSetRef.PlanarWithNormal(Vector3d.UnitZ).Descriptor,
            new FilletRimFeature().Faces.Descriptor);
        Assert.Equal(
            FaceSetRef.PlanarWithNormal(Vector3d.UnitZ).Descriptor,
            new ChamferRimFeature().Faces.Descriptor);

        var history = new FeatureHistory();
        history.Add(new PlateFeature());
        history.Add(new ChamferRimFeature
        {
            Setback = 1,
            Faces = FaceSetRef.PlanarWithNormal(-Vector3d.UnitZ),
        });
        var result = history.Regenerate();
        Assert.True(result.Succeeded, result.ToString());

        // The bottom rim lost material, the top did not.
        double volume = history.Result!.ToMesh().Volume();
        Assert.True(volume < 40 * 30 * 8, $"chamfer removed nothing (volume {volume})");
    }

    [Fact]
    public void ADeferredInputStillFailsByName_AtLoweringTime()
    {
        var history = new FeatureHistory();
        history.Add(new PlateFeature());
        history.Add(new FilletRimFeature { Radius = 1, Faces = FaceSetRef.Cylindrical() });

        // Deferred inputs are not resolved by validation, so the history regenerates...
        Assert.True(history.Regenerate().Succeeded);
        // ...and the rim query names the input when the lowering finally runs it.
        var exception = Assert.Throws<GeometryInputException>(() => history.Result!.ToBrep());
        Assert.Equal("Faces: expected at least one cylindrical face, found none.", exception.Message);
    }

    [Fact]
    public void CircularPattern_TakesAnAxisReference()
    {
        var history = new FeatureHistory();
        history.Add(new PlateFeature());
        var probe = new ProbeAxisFeature { Axis = AxisRef.Of((5, 0, 0), (0, 0, 2)) };
        history.Add(probe);
        Assert.True(history.Regenerate().Succeeded);

        Assert.Equal(new Vector3d(5, 0, 0), probe.Seen.Origin);
        Assert.Equal(Vector3d.UnitZ, probe.Seen.Direction); // normalized at construction

        Assert.Equal(AxisRef.Z.Descriptor, new CircularPatternFeature().Axis.Descriptor);
    }
}
