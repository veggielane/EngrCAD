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
        double engagement = StackEngagement(component, points, face, anchor, anchorFace);

        var near = Place(component, points, face);
        var axis = face.Normal;
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

    /// <summary>
    /// The fastener stack anchored into a PLACED thread provider — an insert or a nut
    /// already placed on <paramref name="anchor"/> — instead of cutting the screw's own
    /// tap pilot. The far body gets NO new preparation (the provider's placement already
    /// cut its pilot or clearance); what this overload adds is the checking: the provider
    /// must actually provide the thread the screw carries
    /// (<see cref="HardwareComponent.ProvidesThread"/> vs
    /// <see cref="HardwareComponent.CarriesThread"/>), the engagement — measured to
    /// <paramref name="anchorFace"/>, the face the provider seats on — must satisfy the
    /// provider's <see cref="HardwareComponent.MinimumEngagement"/> (a nut wants the screw
    /// through its full height) and <see cref="HardwareComponent.MaximumEngagement"/> (a
    /// blind insert bottoms out), and each placement point must project onto one of the
    /// provider's own points, so a screw cannot silently miss its insert.
    /// </summary>
    /// <param name="anchorFace">The face the PROVIDER seats on: the mate face for an
    /// insert, the far body's outer face for a nut.</param>
    /// <param name="anchorInto">The provider's placement on <paramref name="anchor"/>
    /// (the feature <see cref="Place"/> returned).</param>
    /// <remarks>The point check runs only when the provider's seating face is explicit;
    /// a provider seated by a semantic reference (<c>PlaneRef.TopPlane</c>) resolves per
    /// regeneration, so its points cannot be checked at call time and are trusted.</remarks>
    public ComponentFeature PlaceThrough(
        HardwareComponent component,
        IReadOnlyList<Vector2d> points,
        SketchPlane face,
        ComponentAssembly anchor,
        SketchPlane anchorFace,
        ComponentFeature anchorInto)
    {
        ArgumentNullException.ThrowIfNull(anchorInto);
        double engagement = StackEngagement(component, points, face, anchor, anchorFace);

        if (!anchor.History.Features.Any(f => ReferenceEquals(f, anchorInto)))
            throw new ArgumentException(
                "That placement is not part of the anchor body's model.", nameof(anchorInto));

        var provider = anchorInto.Component;
        var provided = provider.ProvidesThread
            ?? throw new ArgumentException(
                $"{provider.Designation} provides no thread to anchor into.", nameof(anchorInto));
        var carried = component.CarriesThread
            ?? throw new ArgumentException(
                $"{component.Designation} carries no thread to engage {provider.Designation}.",
                nameof(component));
        if (provided.Designation != carried.Designation)
            throw new ArgumentException(
                $"Thread mismatch: {component.Designation} carries {carried.Designation} but " +
                $"{provider.Designation} provides {provided.Designation}.", nameof(anchorInto));

        if (provider.MinimumEngagement is { } minimum && engagement < minimum)
            throw new ArgumentException(
                $"{component.Designation} engages only {engagement:g4} of {provider.Designation}, " +
                $"which needs at least {minimum:g4} — use a longer component.", nameof(component));
        if (provider.MaximumEngagement is { } maximum && engagement > maximum)
            throw new ArgumentException(
                $"{component.Designation} would engage {engagement:g4} but {provider.Designation} " +
                $"accepts at most {maximum:g4} — it bottoms out. Use a shorter component.",
                nameof(component));

        ValidateAnchorPoints(points, face, anchorFace, anchorInto);
        return Place(component, points, face);
    }

    /// <summary>Shared stack validation: two distinct bodies, parallel faces, positive
    /// grip, positive engagement. Returns the engagement below the anchor face.</summary>
    private double StackEngagement(
        HardwareComponent component,
        IReadOnlyList<Vector2d> points,
        in SketchPlane face,
        ComponentAssembly anchor,
        in SketchPlane anchorFace)
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
        return engagement;
    }

    /// <summary>Each fastener point must project onto one of the provider's placement
    /// points (weld tier — both sets are exactly-constructed coordinates). Skipped when
    /// the provider's face is a semantic reference resolved per regeneration.</summary>
    private static void ValidateAnchorPoints(
        IReadOnlyList<Vector2d> points,
        in SketchPlane face,
        in SketchPlane anchorFace,
        ComponentFeature anchorInto)
    {
        if (anchorInto.Face.RequiresBody)
            return;
        var providerPlane = anchorInto.Face.Resolve(new FeatureContext(null), nameof(ComponentFeature.Face));

        var axis = face.Normal;
        // The provider must actually be seated on the anchor face this stack measures to.
        if (Math.Abs(providerPlane.Normal.Dot(axis)) < 1 - 1e-9
            || Math.Abs((providerPlane.Origin - anchorFace.Origin).Dot(axis)) > 1e-9)
            throw new ArgumentException(
                $"{anchorInto.Component.Designation} is not seated on the anchor face — a stack " +
                "must anchor into a provider on the face it measures engagement to.", nameof(anchorInto));

        foreach (var point in points)
        {
            var projected = Project(face.ToWorld(point), providerPlane, axis);
            if (!anchorInto.Points.Any(p => (p - projected).Length < 1e-9))
                throw new ArgumentException(
                    $"No {anchorInto.Component.Designation} at projected point " +
                    $"({projected.X:g6}, {projected.Y:g6}) — the fastener would miss the provider. " +
                    $"Provider points: {string.Join(", ", anchorInto.Points.Select(p => $"({p.X:g6}, {p.Y:g6})"))}.",
                    nameof(points));
        }
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
