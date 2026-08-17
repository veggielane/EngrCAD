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

/// <summary>
/// Modeled TrueType/OpenType text as a parametric feature — an engraved serial number, an
/// embossed label — driven by <c>[Param]</c> <see cref="Size"/>, <see cref="Height"/>,
/// <see cref="LetterSpacing"/> and <see cref="Engrave"/>, so it re-tunes through the same
/// seam a design study, a configuration and the properties panel drive.
/// <para><b>The text and font are CONSTRUCTOR inputs, not parameters</b> (a font is a binary
/// blob and a text string is not a numeric/enum <c>[Param]</c> type), so changing either
/// replaces the instance. That is what makes the regeneration cache cover the font correctly
/// even though it is not in the parameter snapshot: a fresh instance always re-runs (the
/// sketch/hole-spec rule), so the cache key never has to name the font.</para>
/// <para><b>Persistence is honest, not complete:</b> a font has no data form, so
/// <see cref="SaveInputs"/> returns null and the feature is opaque to
/// <see cref="FeatureHistory.SaveHistory"/> — its type, name and <c>[Param]</c> values are
/// still written, and a load skips it with a warning unless a <c>resolveOpaque</c> hook
/// rebuilds it, exactly as a <c>ComponentFeature</c> over a non-catalogue component does.</para>
/// </summary>
public sealed class TextFeature(string text, TrueTypeFont font) : Feature
{
    private readonly PlaneRef _plane = PlaneRef.WorldXY;

    /// <summary>Em size of the text (the typographic "point size"; convert a letter height
    /// with <see cref="TrueTypeFont.EmSizeForCapHeight"/>).</summary>
    [Param(Min = 1e-9, Units = "mm", Description = "Em size of the text")]
    public double Size { get; init; } = 6;

    /// <summary>Extrusion depth along the plane normal.</summary>
    [Param(Min = 1e-9, Units = "mm", Description = "Extrusion height along the plane normal")]
    public double Height { get; init; } = 1;

    /// <summary>Extra tracking between glyphs as a fraction of the em size (0 = the font's
    /// own spacing; negative tightens).</summary>
    [Param(Description = "Extra letter spacing as a fraction of the em size")]
    public double LetterSpacing { get; init; }

    /// <summary>Subtract the text from the body (engrave) rather than union it (emboss).
    /// With no body yet, both return the bare text solid.</summary>
    [Param(Description = "Engrave (subtract) instead of emboss (union)")]
    public bool Engrave { get; init; }

    /// <summary>Where the text sits; a <see cref="SketchPlane"/> converts implicitly. The
    /// default is the world XY plane so a first-in-history feature always resolves; set
    /// <see cref="PlaneRef.TopPlane"/> to label a body's top face.</summary>
    [Param(Description = "Text placement")]
    public PlaneRef Plane
    {
        get => _plane;
        init => _plane = value ?? PlaneRef.WorldXY;
    }

    public override Shape Apply(FeatureContext context)
    {
        var plane = Plane.Resolve(context, nameof(Plane));
        var style = new TextStyle { LetterSpacing = LetterSpacing };
        if (context.Body is null)
            return Shape.Text(text, font, Size, Height, plane, style);   // nothing to emboss/engrave onto yet

        // Overshoot the surface so the boolean is always TRANSVERSAL rather than sharing a
        // coplanar face with the body (the Drill overshoot doctrine): the tool is 5% taller
        // than the feature and slid so exactly one end pokes past the surface.
        double reach = Height * 0.05;
        var tool = Shape.Text(text, font, Size, Height + reach, plane, style);
        if (Engrave)
            // A recess of depth Height: the tool occupies [surface − Height, surface + reach].
            return context.Body - tool.Translate(plane.Normal * -Height);
        // A proud label Height tall, its base sunk `reach` into the body.
        return context.Body | tool.Translate(plane.Normal * -reach);
    }

    /// <summary>Null — a font is a binary blob with no data form, so the feature is opaque
    /// to whole-history persistence (see the class summary). The <c>[Param]</c> values still
    /// round-trip; the text and font do not.</summary>
    protected internal override System.Text.Json.Nodes.JsonNode? SaveInputs() => null;
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

// ---- direct editing: the operations a body with NO history needs, made parametric ----
//
// A direct edit is a Shape graph node, so it already composed and Explain already reported
// it; what it lacked was a [Param] a design study, a configuration or the properties panel
// could DRIVE. Wrapping each one is the whole change — the selector is a FaceSetRef, which
// already serializes and already re-resolves per regeneration, so nothing new had to be
// invented for the topological-naming half. Each face set is [DeferredInput] for the rim
// features' reason: the edit resolves it at LOWERING against its own solid, so validating it
// up front would buy an extra B-Rep lowering per regeneration and learn nothing.

/// <summary>
/// Pushes the selected faces along their own outward normals — the parametric form of
/// <see cref="Shape.OffsetFaces(double, FaceSetRef)"/>.
/// </summary>
public sealed class OffsetFacesFeature : Feature
{
    private readonly FaceSetRef _faces = FaceSetRef.PlanarWithNormal(Vector3d.UnitZ);

    /// <summary>How far to push, along each face's own outward normal; positive grows the
    /// solid. Signed on purpose — a bore wall's outward normal points into the void, so a
    /// positive value closes the hole in.</summary>
    [Param(Units = "mm", Description = "Offset distance")]
    public double Distance { get; init; } = 1;

    /// <summary>Which faces to push. Deferred: the edit resolves it at lowering.</summary>
    [Param(Description = "Faces")]
    [DeferredInput]
    public FaceSetRef Faces
    {
        get => _faces;
        init => _faces = value ?? FaceSetRef.PlanarWithNormal(Vector3d.UnitZ);
    }

    public override Shape Apply(FeatureContext context) =>
        (context.Body ?? throw new InvalidOperationException("OffsetFacesFeature needs a body."))
            .OffsetFaces(Distance, Faces.AsSelector(nameof(Faces)));
}

/// <summary>
/// Translates the selected faces — the parametric form of
/// <see cref="Shape.MoveFaces(in Vector3d, FaceSetRef)"/>.
/// </summary>
public sealed class MoveFacesFeature : Feature
{
    private readonly FaceSetRef _faces = FaceSetRef.PlanarWithNormal(Vector3d.UnitZ);

    /// <summary>The displacement. A PLANAR face takes its own projection of it, so moving one
    /// parallel to itself does nothing at all and several faces moved by one vector each move
    /// by their own amount; a curved face is carried bodily, axis and all.</summary>
    [Param(Units = "mm", Description = "Translation")]
    public Vector3d Translation { get; init; } = new(0, 0, 1);

    /// <inheritdoc cref="OffsetFacesFeature.Faces"/>
    [Param(Description = "Faces")]
    [DeferredInput]
    public FaceSetRef Faces
    {
        get => _faces;
        init => _faces = value ?? FaceSetRef.PlanarWithNormal(Vector3d.UnitZ);
    }

    public override Shape Apply(FeatureContext context) =>
        (context.Body ?? throw new InvalidOperationException("MoveFacesFeature needs a body."))
            .MoveFaces(Translation, Faces.AsSelector(nameof(Faces)));
}

/// <summary>
/// Turns the selected faces about an axis — the parametric form of
/// <see cref="Shape.RotateFaces(in Ray3d, double, FaceSetRef)"/>, i.e. a draft angle on a
/// body with no history to re-parameterize.
/// </summary>
public sealed class RotateFacesFeature : Feature
{
    private readonly FaceSetRef _faces = FaceSetRef.PlanarWithNormal(Vector3d.UnitX);
    private readonly AxisRef _axis = AxisRef.Y;

    /// <summary>The turn, in degrees about <see cref="Axis"/>.</summary>
    [Param(Min = -89, Max = 89, Units = "deg", Description = "Rotation angle")]
    public double AngleDegrees { get; init; } = 5;

    /// <summary>The hinge. A face the axis LIES IN tilts about that line and keeps the points
    /// on it, which is what a drafting caller means; a face the axis misses swings bodily.</summary>
    [Param(Description = "Hinge axis")]
    public AxisRef Axis
    {
        get => _axis;
        init => _axis = value ?? AxisRef.Y;
    }

    /// <inheritdoc cref="OffsetFacesFeature.Faces"/>
    [Param(Description = "Faces")]
    [DeferredInput]
    public FaceSetRef Faces
    {
        get => _faces;
        init => _faces = value ?? FaceSetRef.PlanarWithNormal(Vector3d.UnitX);
    }

    public override Shape Apply(FeatureContext context) =>
        (context.Body ?? throw new InvalidOperationException("RotateFacesFeature needs a body."))
            .RotateFaces(Axis.Resolve(context, nameof(Axis)), AngleDegrees,
                Faces.AsSelector(nameof(Faces)));
}

/// <summary>
/// Removes the selected faces and heals the wound — the parametric form of
/// <see cref="Shape.DeleteFaces(FaceSetRef)"/>. It carries no numeric parameter of its own,
/// which is the point: what a design drives here is the SELECTION.
/// </summary>
public sealed class DeleteFacesFeature : Feature
{
    private readonly FaceSetRef _faces = FaceSetRef.Cylindrical();

    /// <inheritdoc cref="OffsetFacesFeature.Faces"/>
    [Param(Description = "Faces to remove")]
    [DeferredInput]
    public FaceSetRef Faces
    {
        get => _faces;
        init => _faces = value ?? FaceSetRef.Cylindrical();
    }

    public override Shape Apply(FeatureContext context) =>
        (context.Body ?? throw new InvalidOperationException("DeleteFacesFeature needs a body."))
            .DeleteFaces(Faces.AsSelector(nameof(Faces)));
}
