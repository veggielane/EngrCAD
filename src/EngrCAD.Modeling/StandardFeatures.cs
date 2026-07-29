using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

// Reified wrappers over the Shape operations, so simple histories need no custom
// feature classes. Geometry inputs are declared as GeometryRef properties (see
// GeometryRefs.cs): they re-resolve per regeneration, validate up front by name, and
// contribute an honest term to the cache key. Other non-[Param] inputs (sketches, hole
// specs) are fixed per instance — replace the feature to change them.

/// <summary>Extrudes a sketch; unions with the existing body (or creates it).</summary>
public sealed class ExtrudeSketchFeature(Sketch sketch) : Feature
{
    private readonly PlaneRef _plane = PlaneRef.WorldXY;

    [Param(Min = 1e-9, Description = "Extrusion height along the plane normal")]
    public double Height { get; init; } = 10;

    /// <summary>Where the sketch sits; a <see cref="SketchPlane"/> converts implicitly.</summary>
    [Param(Description = "Sketch placement")]
    public PlaneRef Plane
    {
        get => _plane;
        init => _plane = value ?? PlaneRef.WorldXY;
    }

    public override Shape Apply(FeatureContext context)
    {
        var solid = Shape.Extrude(sketch, Height, Plane.Resolve(context, nameof(Plane)));
        return context.Body is null ? solid : context.Body | solid;
    }

    /// <summary>The sketch, exactly, through the public curve vocabulary — what makes
    /// this feature reconstructible from <see cref="FeatureHistory.SaveHistory"/>.</summary>
    protected internal override System.Text.Json.Nodes.JsonNode? SaveInputs() =>
        new System.Text.Json.Nodes.JsonObject { ["sketch"] = InputJson.SaveSketch(sketch) };
}

/// <summary>Revolves a sketch (full turn by default); unions with the body.</summary>
public sealed class RevolveSketchFeature(Sketch sketch) : Feature
{
    private readonly PlaneRef _plane = PlaneRef.WorldXZ;

    [Param(Min = 1e-6, Max = 2 * Math.PI, Description = "Sweep angle in radians")]
    public double Angle { get; init; } = 2 * Math.PI;

    /// <summary>Where the sketch sits; a <see cref="SketchPlane"/> converts implicitly.</summary>
    [Param(Description = "Sketch placement")]
    public PlaneRef Plane
    {
        get => _plane;
        init => _plane = value ?? PlaneRef.WorldXZ;
    }

    public override Shape Apply(FeatureContext context)
    {
        var solid = Shape.Revolve(sketch, Angle, Plane.Resolve(context, nameof(Plane)));
        return context.Body is null ? solid : context.Body | solid;
    }

    /// <inheritdoc cref="ExtrudeSketchFeature.SaveInputs"/>
    protected internal override System.Text.Json.Nodes.JsonNode? SaveInputs() =>
        new System.Text.Json.Nodes.JsonObject { ["sketch"] = InputJson.SaveSketch(sketch) };
}

/// <summary>Drills one hole spec at a list of points on a plane (defaults to the
/// body's top face).</summary>
public sealed class HoleFeature(HoleSpec hole, IReadOnlyList<Vector2d> points) : Feature
{
    private readonly PlaneRef _plane = PlaneRef.TopPlane;

    [Param(Min = 1e-9, Description = "Cut depth below the plane")]
    public double Depth { get; init; } = 20;

    /// <summary>Where to drill. Defaults to (and null still means)
    /// <see cref="PlaneRef.TopPlane"/> — the body's top face, re-resolved every
    /// regeneration; an explicit <see cref="SketchPlane"/> converts implicitly.</summary>
    [Param(Description = "Drilling plane")]
    public PlaneRef Plane
    {
        get => _plane;
        init => _plane = value ?? PlaneRef.TopPlane;
    }

    public override Shape Apply(FeatureContext context) =>
        (context.Body ?? throw new InvalidOperationException("HoleFeature needs a body to drill."))
            .Drill(hole, points, Depth, Plane.Resolve(context, nameof(Plane)));

    /// <summary>The hole recipe and its points — what makes this feature
    /// reconstructible from <see cref="FeatureHistory.SaveHistory"/>.</summary>
    protected internal override System.Text.Json.Nodes.JsonNode? SaveInputs() =>
        new System.Text.Json.Nodes.JsonObject
        {
            ["hole"] = InputJson.SaveHoleSpec(hole),
            ["points"] = InputJson.SavePoints(points),
        };
}

/// <summary>Fillets the rims of the selected planar faces (the top faces by default).</summary>
public sealed class FilletRimFeature : Feature
{
    private readonly FaceSetRef _faces = FaceSetRef.PlanarWithNormal(Vector3d.UnitZ);

    [Param(Min = 1e-9)]
    public double Radius { get; init; } = 2;

    /// <summary>Which face rims to round. Deferred: the rim operation resolves this at
    /// lowering time against its own solid, so validating it up front would cost an extra
    /// B-Rep lowering per regeneration for no new information.</summary>
    [Param(Description = "Rim faces")]
    [DeferredInput]
    public FaceSetRef Faces
    {
        get => _faces;
        init => _faces = value ?? FaceSetRef.PlanarWithNormal(Vector3d.UnitZ);
    }

    public override Shape Apply(FeatureContext context) =>
        (context.Body ?? throw new InvalidOperationException("FilletRimFeature needs a body."))
            .Fillet(Radius, Faces.AsSelector(nameof(Faces)));
}

/// <summary>Chamfers the rims of the selected planar faces (the top faces by default).</summary>
public sealed class ChamferRimFeature : Feature
{
    private readonly FaceSetRef _faces = FaceSetRef.PlanarWithNormal(Vector3d.UnitZ);

    [Param(Min = 1e-9)]
    public double Setback { get; init; } = 1;

    /// <inheritdoc cref="FilletRimFeature.Faces"/>
    [Param(Description = "Rim faces")]
    [DeferredInput]
    public FaceSetRef Faces
    {
        get => _faces;
        init => _faces = value ?? FaceSetRef.PlanarWithNormal(Vector3d.UnitZ);
    }

    public override Shape Apply(FeatureContext context) =>
        (context.Body ?? throw new InvalidOperationException("ChamferRimFeature needs a body."))
            .Chamfer(Setback, Faces.AsSelector(nameof(Faces)));
}

/// <summary>
/// Chamfers the rims of the selected planar faces with a VARIABLE setback: the law is
/// evaluated at each rim corner of the lowered body and interpolates linearly along each
/// edge (<see cref="Shape.ChamferAtAngle(Func{Vector3d, double}, double, Func{BrepSolid, IEnumerable{BrepFace}})"/>).
/// The law is a code input, like sketches and hole specs — not a <c>[Param]</c> — so it
/// is covered by the regeneration cache's instance-identity rule: a fresh instance
/// always re-runs.
/// </summary>
public sealed class VariableChamferRimFeature(Func<Vector3d, double> setbackAt) : Feature
{
    private readonly FaceSetRef _faces = FaceSetRef.PlanarWithNormal(Vector3d.UnitZ);

    /// <summary>Chamfer angle measured from the face, degrees; 45 is the symmetric
    /// chamfer. Constant along the rim — that is what keeps every strip planar.</summary>
    [Param(Min = 1e-6, Max = 90 - 1e-6, Description = "Angle from the face, degrees")]
    public double AngleDegrees { get; init; } = 45;

    /// <inheritdoc cref="FilletRimFeature.Faces"/>
    [Param(Description = "Rim faces")]
    [DeferredInput]
    public FaceSetRef Faces
    {
        get => _faces;
        init => _faces = value ?? FaceSetRef.PlanarWithNormal(Vector3d.UnitZ);
    }

    public override Shape Apply(FeatureContext context) =>
        (context.Body ?? throw new InvalidOperationException("VariableChamferRimFeature needs a body."))
            .ChamferAtAngle(setbackAt, AngleDegrees, Faces.AsSelector(nameof(Faces)));
}

/// <summary>
/// Fillets the rims of the selected planar faces with a VARIABLE radius: the law is evaluated
/// at each rim corner of the lowered body and interpolates linearly along each edge
/// (<see cref="Shape.Fillet(Func{Vector3d, double}, Func{BrepSolid, IEnumerable{BrepFace}})"/>).
/// Like <see cref="VariableChamferRimFeature"/>, the law is a code input rather than a
/// <c>[Param]</c>, so it is covered by the regeneration cache's instance-identity rule: a
/// fresh instance always re-runs, and the feature does not round-trip through JSON.
/// </summary>
public sealed class VariableFilletRimFeature(Func<Vector3d, double> radiusAt) : Feature
{
    private readonly FaceSetRef _faces = FaceSetRef.PlanarWithNormal(Vector3d.UnitZ);

    /// <inheritdoc cref="FilletRimFeature.Faces"/>
    [Param(Description = "Rim faces")]
    [DeferredInput]
    public FaceSetRef Faces
    {
        get => _faces;
        init => _faces = value ?? FaceSetRef.PlanarWithNormal(Vector3d.UnitZ);
    }

    public override Shape Apply(FeatureContext context) =>
        (context.Body ?? throw new InvalidOperationException("VariableFilletRimFeature needs a body."))
            .Fillet(radiusAt, Faces.AsSelector(nameof(Faces)));
}

/// <summary>Unions or subtracts a fixed shape (bosses, cutters).</summary>
public sealed class BooleanFeature(Shape tool) : Feature
{
    [Param(Description = "true = subtract the tool, false = union it")]
    public bool Subtract { get; init; }

    public override Shape Apply(FeatureContext context)
    {
        if (context.Body is null)
            return Subtract ? throw new InvalidOperationException("Nothing to subtract from.") : tool;
        return Subtract ? context.Body - tool : context.Body | tool;
    }
}

/// <summary>Circular-patterns the whole body about an axis.</summary>
public sealed class CircularPatternFeature : Feature
{
    private readonly AxisRef _axis = AxisRef.Z;

    [Param(Min = 1, Max = 360)]
    public int Count { get; init; } = 6;

    /// <summary>The axis to pattern about — an explicit one, or a semantic reference such
    /// as <c>AxisRef.OfCylindrical(FaceSetRef.Cylindrical(6))</c>.</summary>
    [Param(Description = "Pattern axis")]
    public AxisRef Axis
    {
        get => _axis;
        init => _axis = value ?? AxisRef.Z;
    }

    public override Shape Apply(FeatureContext context)
    {
        var axis = Axis.Resolve(context, nameof(Axis));
        return (context.Body ?? throw new InvalidOperationException("CircularPatternFeature needs a body."))
            .PatternCircular(Count, axis.Origin, axis.Direction);
    }
}

/// <summary>Linear-patterns the whole body along a step vector.</summary>
public sealed class LinearPatternFeature : Feature
{
    [Param(Min = 1, Max = 10000)]
    public int Count { get; init; } = 3;

    [Param]
    public Vector3d Step { get; init; } = new(10, 0, 0);

    public override Shape Apply(FeatureContext context) =>
        (context.Body ?? throw new InvalidOperationException("LinearPatternFeature needs a body."))
            .PatternLinear(Count, Step);
}
