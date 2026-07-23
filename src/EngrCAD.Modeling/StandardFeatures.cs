using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

// Reified wrappers over the Shape operations, so simple histories need no custom
// feature classes. Non-[Param] inputs (sketches, hole specs, selectors) are fixed per
// instance — replace the feature to change them.

/// <summary>Extrudes a sketch; unions with the existing body (or creates it).</summary>
public sealed class ExtrudeSketchFeature(Sketch sketch) : Feature
{
    [Param(Min = 1e-9, Description = "Extrusion height along the plane normal")]
    public double Height { get; init; } = 10;

    public SketchPlane Plane { get; init; } = SketchPlane.XY;

    public override Shape Apply(FeatureContext context)
    {
        var solid = Shape.Extrude(sketch, Height, Plane);
        return context.Body is null ? solid : context.Body | solid;
    }
}

/// <summary>Revolves a sketch (full turn by default); unions with the body.</summary>
public sealed class RevolveSketchFeature(Sketch sketch) : Feature
{
    [Param(Min = 1e-6, Max = 2 * Math.PI, Description = "Sweep angle in radians")]
    public double Angle { get; init; } = 2 * Math.PI;

    public SketchPlane Plane { get; init; } = SketchPlane.XZ;

    public override Shape Apply(FeatureContext context)
    {
        var solid = Shape.Revolve(sketch, Angle, Plane);
        return context.Body is null ? solid : context.Body | solid;
    }
}

/// <summary>Drills one hole spec at a list of points on a plane (defaults to the
/// body's top face).</summary>
public sealed class HoleFeature(HoleSpec hole, IReadOnlyList<Vector2d> points) : Feature
{
    [Param(Min = 1e-9, Description = "Cut depth below the plane")]
    public double Depth { get; init; } = 20;

    /// <summary>Explicit plane; null drills on <see cref="FeatureContext.TopPlane"/>.</summary>
    public SketchPlane? Plane { get; init; }

    public override Shape Apply(FeatureContext context) =>
        (context.Body ?? throw new InvalidOperationException("HoleFeature needs a body to drill."))
            .Drill(hole, points, Depth, Plane ?? context.TopPlane);
}

/// <summary>Fillets the rims of planar faces facing <see cref="Direction"/>.</summary>
public sealed class FilletRimFeature : Feature
{
    [Param(Min = 1e-9)]
    public double Radius { get; init; } = 2;

    public Vector3d Direction { get; init; } = Vector3d.UnitZ;

    public override Shape Apply(FeatureContext context)
    {
        var direction = Direction;
        return (context.Body ?? throw new InvalidOperationException("FilletRimFeature needs a body."))
            .Fillet(Radius, s => s.PlanarFacesWithNormal(direction));
    }
}

/// <summary>Chamfers the rims of planar faces facing <see cref="Direction"/>.</summary>
public sealed class ChamferRimFeature : Feature
{
    [Param(Min = 1e-9)]
    public double Setback { get; init; } = 1;

    public Vector3d Direction { get; init; } = Vector3d.UnitZ;

    public override Shape Apply(FeatureContext context)
    {
        var direction = Direction;
        return (context.Body ?? throw new InvalidOperationException("ChamferRimFeature needs a body."))
            .Chamfer(Setback, s => s.PlanarFacesWithNormal(direction));
    }
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
    [Param(Min = 1, Max = 360)]
    public int Count { get; init; } = 6;

    public Vector3d AxisOrigin { get; init; } = Vector3d.Zero;
    public Vector3d AxisDirection { get; init; } = Vector3d.UnitZ;

    public override Shape Apply(FeatureContext context) =>
        (context.Body ?? throw new InvalidOperationException("CircularPatternFeature needs a body."))
            .PatternCircular(Count, AxisOrigin, AxisDirection);
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
