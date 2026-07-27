using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// Placing a standard component, as a parametric <see cref="Feature"/>. Applying it cuts
/// the host the component needs (<see cref="HardwareComponent.Prepare"/> or
/// <see cref="HardwareComponent.PrepareAnchor"/>) and records where each component body
/// lands (<see cref="Placements"/>), so a placement is one step in a
/// <see cref="FeatureHistory"/> like any other — with the property that makes smart
/// components worth having: <b>suppressing the placement removes its bore too</b>, and
/// the assembly loses the occurrence with it.
///
/// <para>Seating tracks the model. Leave <see cref="Face"/> null and the component seats
/// on <see cref="FeatureContext.TopPlane"/>, re-resolved on every regeneration — change
/// an upstream thickness parameter and the fasteners move with the face they sit on.</para>
///
/// <para><see cref="Placements"/> is derived <em>output</em>, not input: it is refreshed
/// whenever <see cref="Apply"/> runs, and the regeneration cache key (parameter values
/// plus instance identity) still determines the result completely — a cache hit means
/// the inputs were identical, so the recorded placements were too.</para>
/// </summary>
public sealed class ComponentFeature : Feature
{
    private readonly PlaneRef _face = PlaneRef.TopPlane;
    private IReadOnlyList<PlacedComponent> _placements = [];

    /// <param name="component">The catalogue item being placed.</param>
    /// <param name="points">Placement points in the seating face's 2D coordinates.</param>
    public ComponentFeature(HardwareComponent component, IReadOnlyList<Vector2d> points)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
            throw new ArgumentException("A component placement needs at least one point.", nameof(points));
        Component = component;
        Points = [.. points];
        Name = component.Designation;
    }

    /// <summary>The component being placed.</summary>
    public HardwareComponent Component { get; }

    /// <summary>Placement points in <see cref="Face"/>'s 2D coordinates.</summary>
    public IReadOnlyList<Vector2d> Points { get; }

    /// <summary>The seating face. Defaults to (and null still means)
    /// <see cref="PlaneRef.TopPlane"/>, re-resolved every regeneration; an explicit
    /// <see cref="SketchPlane"/> converts implicitly, and a semantic reference such as
    /// <c>PlaneRef.On(FaceRef.Bottom)</c> tracks the model.</summary>
    [Param(Description = "Seating face")]
    public PlaneRef Face
    {
        get => _face;
        init => _face = value ?? PlaneRef.TopPlane;
    }

    /// <summary>Which side of a fastener stack this body is.</summary>
    [Param(Description = "Host = the body the component sits in; Anchor = the far body of a stack")]
    public ComponentRole Role { get; init; } = ComponentRole.Host;

    /// <summary>Cut depth below the seating face; <b>0 means the component's own natural
    /// depth</b> (through the host for a cap screw, the catalogue depth for an insert).</summary>
    [Param(Min = 0, Units = "mm", Description = "Cut depth below the face; 0 = the component's natural depth")]
    public double Depth { get; init; }

    /// <summary>Whether this placement contributes occurrences to the assembly. False on
    /// the far half of a fastener stack, where the same screw is already placed by the
    /// near half.</summary>
    [Param(Description = "Contribute assembly occurrences (false on a stack's far body)")]
    public bool Assemble { get; init; } = true;

    /// <summary>Where each component body landed, resolved by the last
    /// <see cref="Apply"/>. Empty until the feature has run.</summary>
    public IReadOnlyList<PlacedComponent> Placements => _placements;

    public override Shape Apply(FeatureContext context)
    {
        var body = context.Body
            ?? throw new InvalidOperationException(
                $"{Component.Designation} needs a body to install into — add the host feature first.");

        var face = Face.Resolve(context, nameof(Face));
        var site = new ComponentSite(
            body, Points, face, Role,
            Depth > 0 ? Depth : null,
            () => HostBounds(context));

        var prepared = Component.Cut(site);
        _placements = [.. Points.Select(point =>
            new PlacedComponent(Component, Component.SeatFrame(face, point), point))];
        return prepared;
    }

    /// <summary>A copy of this placement with <see cref="Feature.Suppressed"/> set — the
    /// history-friendly way to toggle a component off (see
    /// <see cref="ComponentAssembly.Suppress"/>). A new instance re-runs the downstream
    /// prefix, which is exactly what removing a bore requires.</summary>
    public ComponentFeature WithSuppressed(bool suppressed = true) =>
        new(Component, Points)
        {
            Face = Face,
            Role = Role,
            Depth = Depth,
            Assemble = Assemble,
            Name = Name,
            Suppressed = suppressed,
        };

    /// <summary>Host bounds for "through the body" depths, reusing the context's cached
    /// B-Rep lowering rather than lowering a second time.</summary>
    private static Aabb HostBounds(FeatureContext context) =>
        context.Body!.CanConvertTo(TargetRep.Brep)
            ? ComponentSite.BoundsOf(context.Lowered)
            : ComponentSite.BoundsOf(context.Body!);
}
