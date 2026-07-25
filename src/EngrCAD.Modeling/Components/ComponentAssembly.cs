using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// A host body plus the standard components placed into it — the front door to the
/// component library. <see cref="Place"/> is the "one call does both" API: it prepares
/// the host (the component's clearance hole, counterbore, pilot bore or reamed hole) and
/// records an assembly occurrence of the component at the seating frame.
///
/// <code>
/// var top = SketchPlane.At((0, 0, 4), Vector3d.UnitX, Vector3d.UnitY);
/// var build = new ComponentAssembly("plate", Shape.Box(60, 40, 8));
/// build.Place(StandardComponents.CapScrew(4, 16), [new(-20, 0), new(20, 0)], top);
/// build.Place(StandardComponents.TrisertInsert(4), [new(0, 12)], top);
/// scene.AddTab("bracket").Add(build.ToAssembly());
/// </code>
///
/// <para>Placements are <see cref="ComponentFeature"/>s in a <see cref="FeatureHistory"/>
/// (<see cref="History"/> is public — add your own features to it freely), so they
/// regenerate, cache and <see cref="Suppress">suppress</see> like any other feature.
/// Suppressing a placement removes its bore from the host <em>and</em> its occurrence
/// from the assembly.</para>
/// </summary>
public sealed class ComponentAssembly
{
    private readonly PartColor? _hostColor;

    /// <summary>Wraps a fixed host body: the history starts with one feature returning
    /// <paramref name="host"/>.</summary>
    public ComponentAssembly(string hostName, Shape host, PartColor? hostColor = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        HostName = Validated(hostName);
        _hostColor = hostColor;
        History = new FeatureHistory();
        History.Add(Feature.FromFunc(HostName, _ => host));
    }

    /// <summary>Places components into an existing parametric model: the history's own
    /// features build the host, and placements append to it.</summary>
    public ComponentAssembly(string hostName, FeatureHistory history, PartColor? hostColor = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        HostName = Validated(hostName);
        _hostColor = hostColor;
        History = history;
    }

    private static string Validated(string hostName) =>
        string.IsNullOrWhiteSpace(hostName)
            ? throw new ArgumentException("The host needs a name.", nameof(hostName))
            : hostName;

    /// <summary>Name of the prepared host part.</summary>
    public string HostName { get; }

    /// <summary>The parametric model: the host's own features followed by the
    /// placements. Public so designs can interleave their own steps.</summary>
    public FeatureHistory History { get; }

    /// <summary>The prepared host part from the most recent <see cref="ToAssembly"/>
    /// (null before the first build).</summary>
    public Part? Host { get; private set; }

    /// <summary>Every placement in this model, in history order.</summary>
    public IEnumerable<ComponentFeature> Placements => History.Features.OfType<ComponentFeature>();

    /// <summary>
    /// Places <paramref name="component"/> at each of <paramref name="points"/> on
    /// <paramref name="face"/>: the host gains the preparation the component needs and
    /// the assembly gains one occurrence per point.
    /// </summary>
    /// <param name="face">The seating face (normal pointing out of the host); null seats
    /// on the body's top face, re-resolved on every regeneration.</param>
    /// <param name="depth">Cut depth below the face; 0 (the default) uses the
    /// component's natural depth.</param>
    public ComponentFeature Place(
        HardwareComponent component,
        IReadOnlyList<Vector2d> points,
        SketchPlane? face = null,
        double depth = 0)
    {
        var placement = new ComponentFeature(component, points) { Face = face, Depth = depth };
        History.Add(placement);
        return placement;
    }

    /// <summary>
    /// The full fastener stack: places <paramref name="component"/> through THIS body and
    /// into <paramref name="anchor"/>'s, preparing both — a clearance hole (and
    /// counterbore) here, the threaded or press-fit engagement there. The engagement
    /// depth is computed, not guessed: the grip is the distance from the component's
    /// seating datum down to <paramref name="anchorFace"/>, and what is left of
    /// <see cref="HardwareComponent.InsertedLength"/> engages the far body
    /// (plus the component's own allowance, e.g. tap runout).
    /// </summary>
    /// <param name="face">Seating face on this body (normal out of it).</param>
    /// <param name="anchor">The far body's model.</param>
    /// <param name="anchorFace">The far body's mating face — parallel to
    /// <paramref name="face"/> and below it. Placement points are projected onto it
    /// along the fastener axis, so its 2D axes need not match.</param>
    /// <returns>The near-body placement (the one that carries the occurrence).</returns>
    public ComponentFeature PlaceThrough(
        HardwareComponent component,
        IReadOnlyList<Vector2d> points,
        SketchPlane face,
        ComponentAssembly anchor,
        SketchPlane anchorFace)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(points);
        if (ReferenceEquals(anchor, this))
            throw new ArgumentException(
                "A fastener stack needs two bodies; use Place for a single body.", nameof(anchor));
        if (points.Count == 0)
            throw new ArgumentException("A component placement needs at least one point.", nameof(points));

        var axis = face.Normal;
        // The two faces must share one fastener axis. 1e-9 is the weld tier: these are
        // exactly-constructed frames, not measured ones.
        if (Math.Abs(axis.Dot(anchorFace.Normal)) < 1 - 1e-9)
            throw new ArgumentException(
                "A fastener stack needs parallel faces — the seating face and the anchor face " +
                "must share the fastener's axis.", nameof(anchorFace));

        var seatOrigin = face.Origin - axis * component.SeatDepth;
        double grip = (seatOrigin - anchorFace.Origin).Dot(axis);
        if (grip <= 0)
            throw new ArgumentException(
                $"The anchor face must sit below the seating datum along the fastener axis " +
                $"(measured {grip:g4}).", nameof(anchorFace));

        double engagement = component.InsertedLength - grip;
        if (engagement <= 0)
            throw new ArgumentException(
                $"{component.Designation} reaches {component.InsertedLength:g4} below its seat but the " +
                $"anchor face is {grip:g4} away — nothing engages. Use a longer component.", nameof(component));

        var near = Place(component, points, face);
        var anchorPoints = points.Select(p => Project(face.ToWorld(p), anchorFace, axis)).ToList();
        anchor.History.Add(new ComponentFeature(component, anchorPoints)
        {
            Face = anchorFace,
            Role = ComponentRole.Anchor,
            Depth = component.AnchorDepth(engagement),
            Assemble = false,
            Name = $"{component.Designation} anchor",
        });
        return near;
    }

    /// <summary>Suppresses (or restores) a placement: the host loses its bore AND the
    /// assembly loses the occurrence. Returns the replacement feature — suppression
    /// swaps in a new instance, so keep the returned one to restore it later.</summary>
    public ComponentFeature Suppress(ComponentFeature placement, bool suppressed = true)
    {
        ArgumentNullException.ThrowIfNull(placement);
        for (int i = 0; i < History.Features.Count; i++)
        {
            if (!ReferenceEquals(History.Features[i], placement))
                continue;
            var replacement = placement.WithSuppressed(suppressed);
            History.Replace(i, replacement);
            return replacement;
        }
        throw new ArgumentException("That placement is not part of this model.", nameof(placement));
    }

    /// <summary>
    /// Regenerates the model and builds the assembly: occurrence 0 is the prepared host
    /// (also available as <see cref="Host"/>), followed by one occurrence per placed
    /// component. Components are shared by reference, so N placements of one catalogue
    /// item mesh once and render N times.
    /// </summary>
    public Assembly ToAssembly(string? name = null)
    {
        var result = History.Regenerate();
        if (result.Body is null || !result.Succeeded)
            throw new InvalidOperationException($"'{HostName}' did not regenerate:\n{result}");

        var host = new Part(HostName, result.Body, History, _hostColor, null);
        Host = host;

        var assembly = new Assembly(name ?? HostName);
        assembly.Add(host);
        for (int i = 0; i < History.Features.Count; i++)
        {
            if (History.Features[i] is not ComponentFeature placement || !placement.Assemble)
                continue;
            // A suppressed placement cut nothing, so it places nothing either.
            if (result.Statuses[i].Outcome is not (FeatureOutcome.Applied or FeatureOutcome.Cached))
                continue;
            foreach (var placed in placement.Placements)
                assembly.Add(placed.Component.ToPart(), placed.Seat);
        }
        return assembly;
    }

    /// <summary>Projects a world point onto <paramref name="plane"/> along
    /// <paramref name="axis"/> and returns its 2D coordinates there.</summary>
    private static Vector2d Project(in Vector3d world, in SketchPlane plane, in Vector3d axis)
    {
        var normal = plane.Normal;
        double t = (plane.Origin - world).Dot(normal) / axis.Dot(normal);  // parallel axes checked by the caller
        var onPlane = world + axis * t - plane.Origin;
        return new Vector2d(onPlane.Dot(plane.XAxis), onPlane.Dot(plane.YAxis));
    }
}
