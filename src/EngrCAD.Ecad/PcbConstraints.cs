using EngrCAD.Core;

namespace EngrCAD.Ecad;

/// <summary>
/// A point the placement constraints act on: a footprint-local point on a placed component,
/// or a fixed board point. When <see cref="Reference"/> is null the point is board-fixed at
/// <see cref="Local"/>; otherwise it is <see cref="Local"/> in the named placement's own 2D
/// frame (millimetres), so it rides that placement's pose as the solver moves it.
/// </summary>
/// <param name="Reference">The placed component's reference designator, or null for a
/// board-fixed point.</param>
/// <param name="Local">The point in the placement's own frame (mm), or the board point when
/// <see cref="Reference"/> is null.</param>
public readonly record struct PlacementPoint(string? Reference, Vector2d Local)
{
    /// <summary>The placement's own origin (its <c>(x, y)</c> pose point).</summary>
    public static PlacementPoint Origin(string reference) => new(reference, Vector2d.Zero);

    /// <summary>A footprint-local point on a placement.</summary>
    public static PlacementPoint At(string reference, Vector2d local) => new(reference, local);

    /// <summary>A fixed board point (moves with nothing).</summary>
    public static PlacementPoint Board(Vector2d point) => new(null, point);

    /// <summary>Whether this point is fixed on the board (rides no placement).</summary>
    public bool IsBoardFixed => Reference is null;
}

/// <summary>
/// A direction the placement constraints act on: a footprint-local unit direction on a placed
/// component (which rotates with the placement's pose), or a fixed board direction. The vector
/// is unit-normalised at construction.
/// </summary>
/// <param name="Reference">The placed component's reference designator, or null for a
/// board-fixed direction.</param>
/// <param name="Local">The unit direction in the placement's own frame, or the board direction
/// when <see cref="Reference"/> is null.</param>
public readonly record struct PlacementDirection(string? Reference, Vector2d Local)
{
    /// <summary>The placement's own +X axis (its 0° reference direction).</summary>
    public static PlacementDirection Axis(string reference) => new(reference, Vector2d.UnitX);

    /// <summary>A footprint-local direction on a placement.</summary>
    public static PlacementDirection OfComponent(string reference, Vector2d direction) =>
        new(reference, Unit(direction, nameof(direction)));

    /// <summary>A fixed board direction.</summary>
    public static PlacementDirection Board(Vector2d direction) =>
        new(null, Unit(direction, nameof(direction)));

    /// <summary>A board direction from one point toward another.</summary>
    public static PlacementDirection Board(Vector2d from, Vector2d to) => Board(to - from);

    /// <summary>Whether this direction is fixed on the board (rides no placement).</summary>
    public bool IsBoardFixed => Reference is null;

    private static Vector2d Unit(Vector2d v, string name)
    {
        double length = v.Length;
        if (!(length > 0))   // exact-zero semantic test: a direction needs a nonzero vector
            throw new ArgumentException($"A direction needs a nonzero vector (got {v}).", name);
        return v / length;
    }
}

/// <summary>An infinite line the constraints reference: a point on it and a direction along it.
/// A board edge or a component's side both reduce to this (a point + a direction).</summary>
/// <param name="Point">A point on the line.</param>
/// <param name="Direction">The line's direction (unit).</param>
public readonly record struct PcbLine(PlacementPoint Point, PlacementDirection Direction)
{
    /// <summary>A fixed board edge through two board points (direction a → b).</summary>
    public static PcbLine Board(Vector2d a, Vector2d b) =>
        new(PlacementPoint.Board(a), PlacementDirection.Board(a, b));

    /// <summary>A side of a placed component: a footprint-local point and a footprint-local
    /// direction, both riding the placement's pose.</summary>
    public static PcbLine Component(string reference, Vector2d localPoint, Vector2d localDirection) =>
        new(PlacementPoint.At(reference, localPoint), PlacementDirection.OfComponent(reference, localDirection));
}

/// <summary>
/// One placement constraint — an opaque value produced by the <see cref="ConstrainedLayout"/>
/// builder methods (never constructed directly). Carries only a <see cref="Name"/> for the DOF
/// report; the constraint's data and residual are internal to the solver and the persistence.
/// </summary>
public abstract class PcbConstraint
{
    private protected PcbConstraint(string name) => Name = name;

    /// <summary>A human-readable name, used in the solve report's diagnostics.</summary>
    public string Name { get; }

    /// <summary>The reference designators of the placements this constraint mentions.</summary>
    internal abstract IEnumerable<string> Placements { get; }

    public override string ToString() => Name;
}

// ---- the internal constraint records the solver and persistence read ----------------------

/// <summary>Pins a placement in place (a datum): its group is grounded.</summary>
internal sealed class LockConstraint(string reference) : PcbConstraint($"Lock {reference}")
{
    public string Reference { get; } = reference;
    internal override IEnumerable<string> Placements => [Reference];
}

/// <summary>Locks the relative poses of several placements into one rigid body.</summary>
internal sealed class GroupConstraint(IReadOnlyList<string> references)
    : PcbConstraint($"Group ({string.Join(", ", references)})")
{
    public IReadOnlyList<string> References { get; } = references;
    internal override IEnumerable<string> Placements => References;
}

/// <summary>Pins a placement's rotation to the value it was drawn at.</summary>
internal sealed class FixRotationConstraint(string reference)
    : PcbConstraint($"FixRotation {reference}")
{
    public string Reference { get; } = reference;
    internal override IEnumerable<string> Placements => [Reference];
}

/// <summary>Pins a placement's rotation to an absolute angle (degrees).</summary>
internal sealed class OrientConstraint(string reference, double degrees)
    : PcbConstraint($"Orient {reference} to {degrees:g6}°")
{
    public string Reference { get; } = reference;
    public double Degrees { get; } = degrees;
    internal override IEnumerable<string> Placements => [Reference];
}

/// <summary>Two points coincide (2 rows).</summary>
internal sealed class CoincidentConstraint(PlacementPoint a, PlacementPoint b)
    : PcbConstraint($"Coincident {Describe.Point(a)} = {Describe.Point(b)}")
{
    public PlacementPoint A { get; } = a;
    public PlacementPoint B { get; } = b;
    internal override IEnumerable<string> Placements => Describe.Refs(A, B);
}

/// <summary>Two points a stated distance apart (1 row).</summary>
internal sealed class DistanceConstraint(PlacementPoint a, PlacementPoint b, double gap)
    : PcbConstraint($"Distance {Describe.Point(a)} ↔ {Describe.Point(b)} = {gap:g6}")
{
    public PlacementPoint A { get; } = a;
    public PlacementPoint B { get; } = b;
    public double Gap { get; } = gap;
    internal override IEnumerable<string> Placements => Describe.Refs(A, B);
}

/// <summary>Two points share a coordinate — an x (a column) or a y (a row) (1 row).</summary>
internal sealed class AlignConstraint(PlacementPoint a, PlacementPoint b, bool shareX)
    : PcbConstraint($"Align{(shareX ? "X" : "Y")} {Describe.Point(a)} = {Describe.Point(b)}")
{
    public PlacementPoint A { get; } = a;
    public PlacementPoint B { get; } = b;
    public bool ShareX { get; } = shareX;
    internal override IEnumerable<string> Placements => Describe.Refs(A, B);
}

/// <summary>Two directions parallel or perpendicular (1 row).</summary>
internal sealed class DirectionPairConstraint(PlacementDirection a, PlacementDirection b, bool parallel)
    : PcbConstraint($"{(parallel ? "Parallel" : "Perpendicular")} directions")
{
    public PlacementDirection A { get; } = a;
    public PlacementDirection B { get; } = b;
    public bool Parallel { get; } = parallel;
    internal override IEnumerable<string> Placements => Describe.Refs(A.Reference, B.Reference);
}

/// <summary>A point lies at a signed distance from a line's carrier (offset 0 = on the line) —
/// the SIGNED point-to-line residual (1 row).</summary>
internal sealed class PointOnLineConstraint(
    PlacementPoint point, PlacementPoint linePoint, PlacementDirection lineDirection, double offset)
    : PcbConstraint($"PointOnLine {Describe.Point(point)}" + (offset == 0 ? "" : $" @ {offset:g6}"))
{
    public PlacementPoint Point { get; } = point;
    public PlacementPoint LinePoint { get; } = linePoint;
    public PlacementDirection LineDirection { get; } = lineDirection;
    public double Offset { get; } = offset;
    internal override IEnumerable<string> Placements =>
        Describe.Refs(Point.Reference, LinePoint.Reference, LineDirection.Reference);
}

/// <summary>A component's edge flush (or at a gap) to another edge — parallel + on-line
/// (2 rows). <see cref="Side"/> is the drawn side, the branch selector for a nonzero gap.</summary>
internal sealed class AlignEdgeConstraint(PcbLine component, PcbLine target, double gap, double side)
    : PcbConstraint($"AlignEdge{(gap == 0 ? " (flush)" : $" @ {gap:g6}")}")
{
    public PcbLine Component { get; } = component;
    public PcbLine Target { get; } = target;
    public double Gap { get; } = gap;
    public double Side { get; } = side;
    internal override IEnumerable<string> Placements =>
        Describe.Refs(Component.Point.Reference, Component.Direction.Reference,
            Target.Point.Reference, Target.Direction.Reference);
}

/// <summary>A component's footprint stays inside a convex-or-simple board region — a one-sided
/// (active-set) containment residual (1 row).</summary>
internal sealed class InsideRegionConstraint(string reference, IReadOnlyList<Vector2d> polygon, double margin)
    : PcbConstraint($"InsideRegion {reference}")
{
    public string Reference { get; } = reference;
    public IReadOnlyList<Vector2d> Polygon { get; } = polygon;
    public double Margin { get; } = margin;
    internal override IEnumerable<string> Placements => [Reference];
}

/// <summary>Two component footprints stay at least a distance apart — a one-sided (active-set)
/// clearance residual (1 row).</summary>
internal sealed class ClearOfConstraint(string a, string b, double distance)
    : PcbConstraint($"ClearOf {a} ↔ {b} ≥ {distance:g6}")
{
    public string A { get; } = a;
    public string B { get; } = b;
    public double Distance { get; } = distance;
    internal override IEnumerable<string> Placements => [A, B];
}

/// <summary>A component footprint stays at least a distance clear of a keep-out region — a
/// one-sided (active-set) clearance residual (1 row).</summary>
internal sealed class ClearOfRegionConstraint(
    string reference, IReadOnlyList<Vector2d> polygon, double distance)
    : PcbConstraint($"ClearOfRegion {reference} ≥ {distance:g6}")
{
    public string Reference { get; } = reference;
    public IReadOnlyList<Vector2d> Polygon { get; } = polygon;
    public double Distance { get; } = distance;
    internal override IEnumerable<string> Placements => [Reference];
}

/// <summary>Small helpers for the constraint names and the placement enumerations.</summary>
internal static class Describe
{
    internal static string Point(in PlacementPoint p) =>
        p.Reference is null ? $"board({p.Local.X:g4},{p.Local.Y:g4})" : p.Reference;

    internal static IEnumerable<string> Refs(params string?[] references)
    {
        foreach (var r in references)
            if (r is not null)
                yield return r;
    }

    internal static IEnumerable<string> Refs(in PlacementPoint a, in PlacementPoint b) =>
        Refs(a.Reference, b.Reference);
}
