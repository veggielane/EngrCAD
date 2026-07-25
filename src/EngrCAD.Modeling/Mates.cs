using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>The assembly constraints <see cref="MateSet"/> can solve.</summary>
public enum MateKind
{
    /// <summary>Two points occupy the same place (3 constraints).</summary>
    Coincident,

    /// <summary>Two planar faces bear against each other: normals oppose and the faces
    /// sit a given gap apart (3 constraints — one along the normal, two of tilt).</summary>
    Planar,

    /// <summary>Two axes are collinear — a shaft in a bore, a screw in a hole
    /// (4 constraints; spin about the axis and slide along it stay free).</summary>
    Concentric,

    /// <summary>Two points a given distance apart (1 constraint).</summary>
    Distance,

    /// <summary>Two directions are parallel, either sense (2 constraints).</summary>
    Parallel,

    /// <summary>Two directions are perpendicular (1 constraint).</summary>
    Perpendicular,

    /// <summary>Two directions at a given angle (1 constraint).</summary>
    Angle,
}

/// <summary>
/// One end of a <see cref="Mate"/>: a point and a direction on a particular
/// <see cref="Occurrence"/>, expressed in that occurrence's <b>local</b> coordinates —
/// the same space the referenced <see cref="Part"/>'s geometry lives in, which is what
/// <see cref="BrepQueries"/> selectors return.
/// <para>A null <see cref="Occurrence"/> means the reference is <b>world-fixed</b>
/// (<see cref="MateGeometry.World"/>): the point and direction are already world
/// coordinates and the solver treats them as ground.</para>
/// </summary>
public readonly record struct MateRef
{
    /// <param name="occurrence">The occurrence the geometry belongs to; null for a
    /// world-fixed reference.</param>
    /// <param name="point">A point in the occurrence's local coordinates.</param>
    /// <param name="direction">A direction in the occurrence's local coordinates
    /// (normalized here; zero when the mate does not use one).</param>
    public MateRef(Occurrence? occurrence, in Vector3d point, in Vector3d direction = default)
    {
        Occurrence = occurrence;
        Point = point;
        // Directions are unit or exactly zero: the solver's rotation Jacobian assumes it,
        // and "no direction" must stay distinguishable from "a very short one".
        Direction = direction.TryNormalize(Tolerance.Default, out var unit) ? unit : Vector3d.Zero;
    }

    /// <summary>The occurrence this geometry belongs to; null = world-fixed.</summary>
    public Occurrence? Occurrence { get; }

    /// <summary>A point in the occurrence's local coordinates (world when
    /// <see cref="Occurrence"/> is null).</summary>
    public Vector3d Point { get; }

    /// <summary>A unit direction in the occurrence's local coordinates, or zero.</summary>
    public Vector3d Direction { get; }

    /// <summary>True when this end is fixed in space (no occurrence to move).</summary>
    public bool IsWorld => Occurrence is null;

    public override string ToString() =>
        $"{Occurrence?.Name ?? "world"} @ {Point}" + (Direction == Vector3d.Zero ? "" : $" → {Direction}");
}

/// <summary>
/// Builders for <see cref="MateRef"/>s — plain coordinates, or <b>semantic B-Rep
/// selectors</b> resolved against the occurrence's part. The selectors are the same
/// vocabulary rim features and annotations use, and they resolve <em>once, here</em>:
/// a mate is a numerical constraint, so its geometry is pinned when the mate is built,
/// not re-queried inside the solver's inner loop.
/// </summary>
public static class MateGeometry
{
    /// <summary>A point on an occurrence (local coordinates).</summary>
    public static MateRef Point(Occurrence occurrence, in Vector3d local) =>
        new(Required(occurrence), local);

    /// <summary>An axis on an occurrence: a point on it plus its direction (local).</summary>
    public static MateRef Axis(Occurrence occurrence, in Vector3d localOrigin, in Vector3d localDirection) =>
        new(Required(occurrence), localOrigin, localDirection);

    /// <summary>A world-fixed reference — ground. Mating to one pins the moving part
    /// against the world rather than against another occurrence.</summary>
    public static MateRef World(in Vector3d point, in Vector3d direction = default) =>
        new(null, point, direction);

    /// <summary>
    /// A planar face of the occurrence's part, selected semantically: the point is the
    /// face's in-plane anchor and the direction its <b>outward</b> normal
    /// (<see cref="BrepQueries.Frame(BrepFace)"/>). The natural end of a
    /// <see cref="Mate.Planar"/> mate.
    /// </summary>
    public static MateRef PlanarFace(Occurrence occurrence, Func<BrepSolid, BrepFace> selector)
    {
        var (part, solid) = Resolve(occurrence, selector);
        var face = selector(solid);
        var frame = face.Frame()
            ?? throw new ArgumentException(
                $"The face selected on '{part.Name}' is not planar — a Planar mate needs a plane.",
                nameof(selector));
        return new MateRef(occurrence, Local(part, frame.Origin), LocalVector(part, frame.Z));
    }

    /// <summary>
    /// The axis of a cylindrical face of the occurrence's part, selected semantically —
    /// the natural end of a <see cref="Mate.Concentric"/> mate (a bore, a shank, a boss).
    /// </summary>
    public static MateRef CylindricalFace(Occurrence occurrence, Func<BrepSolid, BrepFace> selector)
    {
        var (part, solid) = Resolve(occurrence, selector);
        var face = selector(solid);
        if (!face.IsCylindrical(out var axisOrigin, out var axisDirection, out _))
            throw new ArgumentException(
                $"The face selected on '{part.Name}' is not cylindrical — a Concentric mate needs an axis.",
                nameof(selector));
        return new MateRef(occurrence, Local(part, axisOrigin), LocalVector(part, axisDirection));
    }

    private static (Part Part, BrepSolid Solid) Resolve(
        Occurrence occurrence, Func<BrepSolid, BrepFace> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var part = Required(occurrence).Part
            ?? throw new ArgumentException(
                $"Occurrence '{occurrence.Name}' places a sub-assembly, which has no single solid to " +
                "select faces on. Mate against a point/axis, or mate the sub-assembly's own occurrences.",
                nameof(occurrence));
        var solid = part.TryGetSolid()
            ?? throw new ArgumentException(
                $"Part '{part.Name}' has no exact B-Rep, so face selectors cannot run on it. " +
                "Use MateGeometry.Point/Axis with explicit coordinates instead.", nameof(occurrence));
        return (part, solid);
    }

    private static Occurrence Required(Occurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        return occurrence;
    }

    // A part's own Transform sits between the occurrence frame and the geometry
    // (PartInstance.World = frame × Part.Transform), and selectors return geometry from
    // BEFORE it. Bake it in here so the solver only ever deals with the occurrence frame.
    // Assemblies pose with occurrence frames, so this is the identity in practice.
    private static Vector3d Local(Part part, in Vector3d point) => part.Transform.TransformPoint(point);

    private static Vector3d LocalVector(Part part, in Vector3d vector) => part.Transform.TransformVector(vector);
}

/// <summary>
/// One assembly constraint between two <see cref="MateRef"/>s. Immutable; build them
/// with the named factories and hand them to a <see cref="MateSet"/>, which solves for
/// the occurrence frames that satisfy them all.
/// </summary>
public sealed class Mate
{
    private Mate(MateKind kind, in MateRef a, in MateRef b, double value, string? name)
    {
        Kind = kind;
        A = a;
        B = b;
        Value = value;
        Name = name ?? kind.ToString();
    }

    /// <summary>What this mate constrains.</summary>
    public MateKind Kind { get; }

    /// <summary>The first end.</summary>
    public MateRef A { get; }

    /// <summary>The second end.</summary>
    public MateRef B { get; }

    /// <summary>The mate's numeric parameter: the gap for
    /// <see cref="MateKind.Planar"/>, the separation for <see cref="MateKind.Distance"/>,
    /// the angle in radians for <see cref="MateKind.Angle"/>; unused otherwise.</summary>
    public double Value { get; }

    /// <summary>A label for diagnostics (defaults to the kind).</summary>
    public string Name { get; }

    /// <summary>Two points occupy the same place.</summary>
    public static Mate Coincident(in MateRef a, in MateRef b, string? name = null) =>
        new(MateKind.Coincident, a, b, 0, name);

    /// <summary>Two planar faces bear against each other: normals oppose, faces
    /// <paramref name="gap"/> apart along A's normal (0 = flush contact).</summary>
    public static Mate Planar(in MateRef a, in MateRef b, double gap = 0, string? name = null) =>
        Directed(MateKind.Planar, a, b, gap, name);

    /// <summary>Two axes are collinear (a shaft in a bore).</summary>
    public static Mate Concentric(in MateRef a, in MateRef b, string? name = null) =>
        Directed(MateKind.Concentric, a, b, 0, name);

    /// <summary>Two points a given distance apart.</summary>
    public static Mate Distance(in MateRef a, in MateRef b, double distance, string? name = null)
    {
        if (distance < 0)
            throw new ArgumentOutOfRangeException(nameof(distance), "A mate distance cannot be negative.");
        return new Mate(MateKind.Distance, a, b, distance, name);
    }

    /// <summary>Two directions are parallel (either sense).</summary>
    public static Mate Parallel(in MateRef a, in MateRef b, string? name = null) =>
        Directed(MateKind.Parallel, a, b, 0, name);

    /// <summary>Two directions are perpendicular.</summary>
    public static Mate Perpendicular(in MateRef a, in MateRef b, string? name = null) =>
        Directed(MateKind.Perpendicular, a, b, 0, name);

    /// <summary>Two directions at <paramref name="degrees"/> to each other.</summary>
    public static Mate Angle(in MateRef a, in MateRef b, double degrees, string? name = null) =>
        Directed(MateKind.Angle, a, b, degrees * Math.PI / 180, name);

    private static Mate Directed(MateKind kind, in MateRef a, in MateRef b, double value, string? name)
    {
        // Exact-zero semantic test: MateRef's constructor normalizes or zeroes, so a zero
        // direction means "none was supplied", never "a short one".
        if (a.Direction == Vector3d.Zero || b.Direction == Vector3d.Zero)
            throw new ArgumentException(
                $"A {kind} mate needs a direction on both ends (use MateGeometry.Axis or a face selector).");
        return new Mate(kind, a, b, value, name);
    }

    /// <summary>How many scalar residuals this mate contributes (before rank analysis —
    /// the rotational encodings are deliberately redundant, 3 rows carrying 2 constraints,
    /// which the solver's rank-revealing factorization sees through).</summary>
    internal int RowCount => Kind switch
    {
        MateKind.Coincident => 3,
        MateKind.Planar => 4,
        MateKind.Concentric => 6,
        MateKind.Parallel => 3,
        _ => 1,
    };

    public override string ToString() => $"{Name}: {A} ↔ {B}";
}
