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
/// <para>A reference may reach <b>across assembly levels</b>: built from an occurrence
/// path ("clamp.2/bolt", see <see cref="Assembly.ResolvePath"/>), it carries the whole
/// occurrence chain in <see cref="Ancestors"/>, so the solver can compose the target's
/// world pose through its parents — and take the chain rule through them in the
/// Jacobian. A chain names a unique <em>instance</em> even when the sub-assembly type
/// is shared, which is exactly what a bare deep <see cref="Occurrence"/> cannot do.</para>
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
        Ancestors = null;
        Point = point;
        Direction = Normalized(direction);
    }

    /// <param name="chain">The occurrence chain from the mate set's assembly down to the
    /// target, outermost first (<see cref="Assembly.ResolvePath"/>'s result). The last
    /// link is the referenced occurrence; the rest become <see cref="Ancestors"/>.</param>
    /// <param name="point">A point in the target occurrence's local coordinates.</param>
    /// <param name="direction">A direction in the target occurrence's local coordinates
    /// (normalized here; zero when the mate does not use one).</param>
    public MateRef(IReadOnlyList<Occurrence> chain, in Vector3d point, in Vector3d direction = default)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (chain.Count == 0)
            throw new ArgumentException("An occurrence chain must contain at least one occurrence.", nameof(chain));
        Occurrence = chain[^1];
        Ancestors = chain.Count > 1 ? [.. chain.Take(chain.Count - 1)] : null;
        Point = point;
        Direction = Normalized(direction);
    }

    // Directions are unit or exactly zero: the solver's rotation Jacobian assumes it,
    // and "no direction" must stay distinguishable from "a very short one". A direction
    // that is ALREADY unit is kept verbatim rather than divided again — re-normalizing
    // a unit vector moves it by an ulp, and mate persistence round-trips these numbers
    // (the same fixed-point rule AxisRef documents).
    private static Vector3d Normalized(in Vector3d direction) =>
        Tolerance.Default.AreEqual(direction.LengthSquared, 1) ? direction
        : direction.TryNormalize(Tolerance.Default, out var unit) ? unit
        : Vector3d.Zero;

    /// <summary>The occurrence this geometry belongs to (the chain's deepest link for a
    /// cross-level reference); null = world-fixed.</summary>
    public Occurrence? Occurrence { get; }

    /// <summary>For a cross-level reference, the occurrences ABOVE the target, outermost
    /// first — the frames the target's pose composes through. Null for a direct
    /// (one-level) or world-fixed reference.</summary>
    public IReadOnlyList<Occurrence>? Ancestors { get; }

    /// <summary>A point in the occurrence's local coordinates (world when
    /// <see cref="Occurrence"/> is null).</summary>
    public Vector3d Point { get; }

    /// <summary>A unit direction in the occurrence's local coordinates, or zero.</summary>
    public Vector3d Direction { get; }

    /// <summary>The semantic query this end was resolved from ("planarFace(…)",
    /// "cylindricalFace(…)", "axisRef(…)" over a <see cref="GeometryRef"/> descriptor),
    /// or null for explicit coordinates. Carried so <see cref="MateSet.SaveMates"/> can
    /// write the query rather than only the numbers it pinned.</summary>
    internal string? Query { get; init; }

    /// <summary>The occurrence path this end references, relative to the assembly the
    /// mate set is built on ("clamp.2/bolt"); null for a world-fixed end.</summary>
    public string? Path => Occurrence is null
        ? null
        : Ancestors is null
            ? Occurrence.Name
            : string.Join("/", Ancestors.Select(o => o.Name).Append(Occurrence.Name));

    /// <summary>True when this end is fixed in space (no occurrence to move).</summary>
    public bool IsWorld => Occurrence is null;

    public override string ToString() =>
        $"{Path ?? "world"} @ {Point}" + (Direction == Vector3d.Zero ? "" : $" → {Direction}");
}

/// <summary>
/// Builders for <see cref="MateRef"/>s — plain coordinates, <b>semantic B-Rep
/// selectors</b>, or typed <see cref="GeometryRef"/> queries, all resolved against the
/// occurrence's part. The selectors are the same vocabulary rim features and
/// annotations use, and they resolve <em>once, here</em>: a mate is a numerical
/// constraint, so its geometry is pinned when the mate is built, not re-queried inside
/// the solver's inner loop. (Features take the opposite, lazy route — see
/// <see cref="GeometryRef"/> — because a feature is a query; eager-vs-lazy is the
/// consumer's choice, not a different vocabulary.)
/// <para>Every builder also has an <c>(Assembly, path, …)</c> overload that reaches
/// across assembly levels: the path ("clamp.2/bolt") resolves to the occurrence chain
/// via <see cref="Assembly.ResolvePath"/> and the solver composes poses through it.</para>
/// </summary>
public static class MateGeometry
{
    /// <summary>A point on an occurrence (local coordinates).</summary>
    public static MateRef Point(Occurrence occurrence, in Vector3d local) =>
        new(Required(occurrence), local);

    /// <summary>A point on the occurrence an occurrence path names ("clamp.2/bolt"),
    /// in that occurrence's local coordinates — the cross-level form.</summary>
    public static MateRef Point(Assembly assembly, string path, in Vector3d local) =>
        new(ResolveChain(assembly, path), local);

    /// <summary>An axis on an occurrence: a point on it plus its direction (local).</summary>
    public static MateRef Axis(Occurrence occurrence, in Vector3d localOrigin, in Vector3d localDirection) =>
        new(Required(occurrence), localOrigin, localDirection);

    /// <summary>An axis on the occurrence an occurrence path names — the cross-level
    /// form.</summary>
    public static MateRef Axis(Assembly assembly, string path, in Vector3d localOrigin, in Vector3d localDirection) =>
        new(ResolveChain(assembly, path), localOrigin, localDirection);

    /// <summary>An axis of the occurrence's part named by a typed
    /// <see cref="AxisRef"/> query, resolved once, here. A serializable query makes the
    /// mate serializable (<see cref="MateSet.SaveMates"/>).</summary>
    public static MateRef Axis(Occurrence occurrence, AxisRef axis) =>
        Axis([Required(occurrence)], axis);

    /// <inheritdoc cref="Axis(Occurrence, AxisRef)"/>
    public static MateRef Axis(Assembly assembly, string path, AxisRef axis) =>
        Axis(ResolveChain(assembly, path), axis);

    internal static MateRef Axis(IReadOnlyList<Occurrence> chain, AxisRef axis)
    {
        ArgumentNullException.ThrowIfNull(axis);
        // An explicit axis needs no body; on a sub-assembly occurrence there is no part
        // transform to bake either, so its coordinates pass through verbatim (they are
        // the sub-assembly's own space, exactly like Point/Axis with raw coordinates).
        if (!axis.RequiresBody && chain[^1].Part is null)
        {
            var explicitRay = axis.Resolve((BrepSolid?)null, "Axis");
            return new MateRef(chain, explicitRay.Origin, explicitRay.Direction)
                { Query = Query("axisRef", axis) };
        }
        var (part, solid) = ResolvePart(chain, requireSolid: axis.RequiresBody);
        Ray3d ray;
        try
        {
            ray = axis.Resolve(solid, "Axis");
        }
        catch (GeometryInputException exception)
        {
            throw new ArgumentException(
                $"Axis query on '{part.Name}' failed: {exception.Message}", nameof(axis));
        }
        return new MateRef(chain, Local(part, ray.Origin), LocalVector(part, ray.Direction))
            { Query = Query("axisRef", axis) };
    }

    /// <summary>A world-fixed reference — ground. Mating to one pins the moving part
    /// against the world rather than against another occurrence.</summary>
    public static MateRef World(in Vector3d point, in Vector3d direction = default) =>
        new((Occurrence?)null, point, direction);

    /// <summary>
    /// A planar face of the occurrence's part, selected semantically: the point is the
    /// face's in-plane anchor and the direction its <b>outward</b> normal
    /// (<see cref="BrepQueries.Frame(BrepFace)"/>). The natural end of a
    /// <see cref="Mate.Planar"/> mate.
    /// </summary>
    public static MateRef PlanarFace(Occurrence occurrence, Func<BrepSolid, BrepFace> selector) =>
        PlanarFace([Required(occurrence)], Opaque(selector));

    /// <inheritdoc cref="PlanarFace(Occurrence, Func{BrepSolid, BrepFace})"/>
    public static MateRef PlanarFace(Assembly assembly, string path, Func<BrepSolid, BrepFace> selector) =>
        PlanarFace(ResolveChain(assembly, path), Opaque(selector));

    /// <summary>The planar face named by a typed <see cref="FaceRef"/> query, resolved
    /// once, here. A serializable query makes the mate serializable
    /// (<see cref="MateSet.SaveMates"/>); a lambda-backed one saves only its pinned
    /// coordinates.</summary>
    public static MateRef PlanarFace(Occurrence occurrence, FaceRef face) =>
        PlanarFace([Required(occurrence)], face);

    /// <inheritdoc cref="PlanarFace(Occurrence, FaceRef)"/>
    public static MateRef PlanarFace(Assembly assembly, string path, FaceRef face) =>
        PlanarFace(ResolveChain(assembly, path), face);

    internal static MateRef PlanarFace(IReadOnlyList<Occurrence> chain, FaceRef face)
    {
        var (part, selected) = SelectFace(chain, face, "PlanarFace");
        var frame = selected.Frame()
            ?? throw new ArgumentException(
                $"The face selected on '{part.Name}' is not planar — a Planar mate needs a plane.",
                nameof(face));
        return new MateRef(chain, Local(part, frame.Origin), LocalVector(part, frame.Z))
            { Query = Query("planarFace", face) };
    }

    /// <summary>
    /// The axis of a cylindrical face of the occurrence's part, selected semantically —
    /// the natural end of a <see cref="Mate.Concentric"/> mate (a bore, a shank, a boss).
    /// </summary>
    public static MateRef CylindricalFace(Occurrence occurrence, Func<BrepSolid, BrepFace> selector) =>
        CylindricalFace([Required(occurrence)], Opaque(selector));

    /// <inheritdoc cref="CylindricalFace(Occurrence, Func{BrepSolid, BrepFace})"/>
    public static MateRef CylindricalFace(Assembly assembly, string path, Func<BrepSolid, BrepFace> selector) =>
        CylindricalFace(ResolveChain(assembly, path), Opaque(selector));

    /// <summary>The cylindrical face named by a typed <see cref="FaceRef"/> query,
    /// resolved once, here — see <see cref="PlanarFace(Occurrence, FaceRef)"/> for the
    /// serialization contract.</summary>
    public static MateRef CylindricalFace(Occurrence occurrence, FaceRef face) =>
        CylindricalFace([Required(occurrence)], face);

    /// <inheritdoc cref="CylindricalFace(Occurrence, FaceRef)"/>
    public static MateRef CylindricalFace(Assembly assembly, string path, FaceRef face) =>
        CylindricalFace(ResolveChain(assembly, path), face);

    internal static MateRef CylindricalFace(IReadOnlyList<Occurrence> chain, FaceRef face)
    {
        var (part, selected) = SelectFace(chain, face, "CylindricalFace");
        if (!selected.IsCylindrical(out var axisOrigin, out var axisDirection, out _))
            throw new ArgumentException(
                $"The face selected on '{part.Name}' is not cylindrical — a Concentric mate needs an axis.",
                nameof(face));
        return new MateRef(chain, Local(part, axisOrigin), LocalVector(part, axisDirection))
            { Query = Query("cylindricalFace", face) };
    }

    // ---- resolution plumbing ----

    /// <summary>The raw-selector overloads wrapped as an (opaque, non-serializable)
    /// <see cref="FaceRef"/>, so face selection has ONE code path.</summary>
    private static FaceRef Opaque(Func<BrepSolid, BrepFace> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return FaceRef.One(FaceSetRef.From("selector", s => [selector(s)]));
    }

    private static (Part Part, BrepFace Face) SelectFace(
        IReadOnlyList<Occurrence> chain, FaceRef face, string inputName)
    {
        ArgumentNullException.ThrowIfNull(face);
        var (part, solid) = ResolvePart(chain, requireSolid: true);
        try
        {
            return (part, face.Resolve(solid, inputName));
        }
        catch (GeometryInputException exception)
        {
            throw new ArgumentException(
                $"Face query on '{part.Name}' failed: {exception.Message}", nameof(face));
        }
    }

    private static (Part Part, BrepSolid? Solid) ResolvePart(IReadOnlyList<Occurrence> chain, bool requireSolid)
    {
        var occurrence = chain[^1];
        var part = occurrence.Part
            ?? throw new ArgumentException(
                $"Occurrence '{occurrence.Name}' places a sub-assembly, which has no single solid to " +
                "select faces on. Mate against a point/axis, or reference one of its own occurrences by path.",
                nameof(chain));
        var solid = part.TryGetSolid();
        if (solid is null && requireSolid)
            throw new ArgumentException(
                $"Part '{part.Name}' has no exact B-Rep, so face selectors cannot run on it. " +
                "Use MateGeometry.Point/Axis with explicit coordinates instead.", nameof(chain));
        return (part, solid);
    }

    private static IReadOnlyList<Occurrence> ResolveChain(Assembly assembly, string path)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly.ResolvePath(path);
    }

    private static Occurrence Required(Occurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        return occurrence;
    }

    /// <summary>The serialized query wrapper: which resolution rule, over the
    /// reference's own descriptor. A lambda-backed reference yields its opaque marker
    /// here, which <see cref="MateSet.LoadMates"/> declines with a warning.</summary>
    private static string Query(string wrapper, GeometryRef reference) =>
        $"{wrapper}({reference.Descriptor})";

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

    /// <summary>Rebuilds a mate from its serialized pieces (<see cref="MateSet.LoadMates"/>).
    /// Routes through the public factories so every construction check re-runs; the value
    /// is the STORED one (radians for Angle), so no unit conversion re-enters.</summary>
    internal static Mate Restore(MateKind kind, in MateRef a, in MateRef b, double value, string? name) => kind switch
    {
        MateKind.Coincident => Coincident(a, b, name),
        MateKind.Planar => Planar(a, b, value, name),
        MateKind.Concentric => Concentric(a, b, name),
        MateKind.Distance => Distance(a, b, value, name),
        MateKind.Parallel => Parallel(a, b, name),
        MateKind.Perpendicular => Perpendicular(a, b, name),
        MateKind.Angle => Directed(MateKind.Angle, a, b, value, name),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

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
