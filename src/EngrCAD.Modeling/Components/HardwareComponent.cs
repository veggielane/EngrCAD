using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>Which side of a fastener stack a preparation cuts.</summary>
public enum ComponentRole
{
    /// <summary>The body the component is installed into — its own clearance bore,
    /// counterbore, insert pocket or reamed hole.</summary>
    Host,

    /// <summary>The far body of a stack: the threaded or press-fit engagement the
    /// component reaches into after passing through the host.</summary>
    Anchor,
}

/// <summary>
/// Everything a <see cref="HardwareComponent"/> needs in order to cut the body it is
/// being installed into: the host geometry, the seating face, the 2D points on it, and
/// how deep to go. Depth resolution is the interesting part — a component asks for its
/// natural depth through <see cref="Depth"/>, an explicit request (from
/// <see cref="ComponentFeature.Depth"/>) always wins, and <see cref="ThroughDepth"/>
/// answers "all the way through this body" without the component having to know the
/// host's size.
/// </summary>
public sealed class ComponentSite
{
    private readonly Func<Aabb> _hostBounds;
    private double? _through;

    /// <param name="body">The host geometry to cut.</param>
    /// <param name="points">Placement points in <paramref name="face"/>'s 2D coordinates.</param>
    /// <param name="face">The seating face: origin and axes in world space, normal
    /// pointing OUT of the host (so preparations cut along −normal, as
    /// <see cref="Shape.Drill"/> does).</param>
    /// <param name="role">Which side of a stack this body is.</param>
    /// <param name="requestedDepth">An explicit depth override, or null to let the
    /// component choose.</param>
    /// <param name="hostBounds">Bounds provider for <see cref="ThroughDepth"/>; supply
    /// one to reuse a lowering you already have (<see cref="ComponentFeature"/> passes
    /// <see cref="FeatureContext.Lowered"/>) instead of lowering the body again.</param>
    public ComponentSite(
        Shape body,
        IReadOnlyList<Vector2d> points,
        in SketchPlane face,
        ComponentRole role = ComponentRole.Host,
        double? requestedDepth = null,
        Func<Aabb>? hostBounds = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
            throw new ArgumentException("A component placement needs at least one point.", nameof(points));
        if (requestedDepth is <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestedDepth), "An explicit depth must be positive.");

        Body = body;
        Points = [.. points];
        Face = face;
        Role = role;
        RequestedDepth = requestedDepth;
        var self = Body;
        _hostBounds = hostBounds ?? (() => BoundsOf(self));
    }

    /// <summary>The host geometry being prepared.</summary>
    public Shape Body { get; }

    /// <summary>Placement points in <see cref="Face"/>'s 2D coordinates.</summary>
    public IReadOnlyList<Vector2d> Points { get; }

    /// <summary>The seating face (normal points out of the host).</summary>
    public SketchPlane Face { get; }

    /// <summary>Which side of a fastener stack this body is.</summary>
    public ComponentRole Role { get; }

    /// <summary>An explicit depth override, or null when the component decides.</summary>
    public double? RequestedDepth { get; }

    /// <summary>
    /// A depth that clears the far side of the host: the deepest point of the body
    /// below <see cref="Face"/>, plus 5% — the same proportional overshoot the hole
    /// tools use, so a through tool never ends exactly coplanar with the far face
    /// (which <see cref="Shape.Drill"/> rejects). Computed at most once.
    /// </summary>
    public double ThroughDepth => _through ??= ComputeThroughDepth();

    /// <summary>The depth to cut: the explicit request when there is one, else the
    /// component's own <paramref name="natural"/> depth.</summary>
    public double Depth(double natural) => RequestedDepth ?? natural;

    private double ComputeThroughDepth()
    {
        var bounds = _hostBounds();
        if (bounds.IsEmpty)
            throw new InvalidOperationException("The host body has empty bounds — nothing to prepare.");

        var normal = Face.Normal;
        var origin = Face.Origin;
        double deepest = double.NegativeInfinity;
        for (int corner = 0; corner < 8; corner++)
        {
            var point = new Vector3d(
                (corner & 1) == 0 ? bounds.Min.X : bounds.Max.X,
                (corner & 2) == 0 ? bounds.Min.Y : bounds.Max.Y,
                (corner & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);
            deepest = Math.Max(deepest, (origin - point).Dot(normal));
        }

        if (deepest <= 0)
            throw new InvalidOperationException(
                "The seating face has no material below it — check that its normal points out of the host.");
        return deepest * 1.05;
    }

    /// <summary>Bounds of a shape, preferring its exact B-Rep and falling back to the
    /// display mesh for SDF/blend geometry that has no B-Rep form.</summary>
    internal static Aabb BoundsOf(Shape body)
    {
        if (body.CanConvertTo(TargetRep.Brep))
            return BoundsOf(body.ToBrep());
        return body.ToMesh().ComputeBounds();
    }

    /// <summary>Bounds of an already-lowered solid (face bounds sample surface domains,
    /// so curved faces are covered, not just their edges).</summary>
    internal static Aabb BoundsOf(BrepSolid solid)
    {
        var bounds = Aabb.Empty;
        foreach (var face in solid.Faces)
            bounds = bounds.Union(face.Bounds());
        return bounds;
    }
}

/// <summary>
/// A catalogue item that is more than geometry. A hardware component carries
/// <list type="number">
/// <item><description>its own parametric <see cref="Body"/> (a <see cref="Shape"/>),</description></item>
/// <item><description>a seating convention (<see cref="SeatDepth"/>,
///   <see cref="SeatFrame"/>) that turns a point on a face into a pose, and</description></item>
/// <item><description>a <b>host preparation</b> (<see cref="Prepare"/>,
///   <see cref="PrepareAnchor"/>): the cut the target body needs so the component
///   fits — a cap screw's clearance hole and counterbore, an insert's pilot bore, a
///   dowel's reamed hole.</description></item>
/// </list>
/// Placing one therefore yields BOTH a modified host and an assembly occurrence — the
/// "smart fastener" idea in plain C#. <see cref="ComponentFeature"/> is how a placement
/// enters a <see cref="FeatureHistory"/> (so it regenerates, caches and suppresses like
/// any other feature) and <see cref="ComponentAssembly"/> is the one-call front door.
///
/// <para><b>The local frame.</b> <see cref="Body"/> is modeled in the component's own
/// frame: <b>+Z points out of the host</b> along the component axis and the origin is
/// the component's <em>seating datum</em> — the surface it bears on (a cap screw's head
/// underside, an insert's or a dowel's top face). Material of the component that goes
/// into the host is therefore at z &lt; 0, reaching <see cref="InsertedLength"/> below
/// the datum. <see cref="SeatDepth"/> says how far below the host's face that datum
/// sits, so a counterbored screw is the same geometry as a proud one, just posed
/// deeper.</para>
/// </summary>
public abstract class HardwareComponent
{
    private readonly Lock _gate = new();
    private Shape? _body;
    private Part? _part;

    /// <summary>Catalogue designation, e.g. "ISO 4762 M4x16" — also the display name of
    /// the component's <see cref="Part"/> and of its assembly occurrences.</summary>
    public abstract string Designation { get; }

    /// <summary>How far the component reaches below its seating datum (a cap screw's
    /// shank length, an insert's body length, a dowel's inserted length). This is what
    /// makes a fastener stack computable: engagement in the far body is
    /// <c>InsertedLength − grip</c>.</summary>
    public abstract double InsertedLength { get; }

    /// <summary>How far below the host's face the seating datum sits: zero for a
    /// component that bears on the face, the recess depth for a counterbored screw.</summary>
    public virtual double SeatDepth => 0;

    /// <summary>Display color of the component's part.</summary>
    public virtual PartColor Color => Palette.Steel;

    /// <summary>The component's geometry in its own frame (see the class remarks),
    /// built once and cached.</summary>
    public Shape Body
    {
        get
        {
            lock (_gate)
                return _body ??= BuildBody();
        }
    }

    /// <summary>Builds <see cref="Body"/>; called at most once per component.</summary>
    protected abstract Shape BuildBody();

    /// <summary>The shared document-model part for this component — one part however
    /// many times it is placed, so it meshes once and every occurrence renders from the
    /// same buffers.</summary>
    public Part ToPart()
    {
        lock (_gate)
            return _part ??= new Part(Designation, Body, Color);
    }

    /// <summary>The cut this component needs in the body it is installed into.</summary>
    public abstract Shape Prepare(ComponentSite site);

    /// <summary>The cut the FAR body of a fastener stack needs (a tapped pilot for a
    /// screw, a reamed hole for a dowel). Components that cannot anchor say so.</summary>
    public virtual Shape PrepareAnchor(ComponentSite site) =>
        throw new NotSupportedException(
            $"{Designation} has no far-side preparation — it is installed into one body only. " +
            "Place the anchoring component (an insert, say) on the far body separately.");

    /// <summary>How deep the far body must be prepared for a given thread/press
    /// engagement. Defaults to the engagement itself; screws add tap runout.</summary>
    public virtual double AnchorDepth(double engagement) => engagement;

    /// <summary>Dispatches to <see cref="Prepare"/> or <see cref="PrepareAnchor"/>
    /// according to <see cref="ComponentSite.Role"/>.</summary>
    public Shape Cut(ComponentSite site)
    {
        ArgumentNullException.ThrowIfNull(site);
        return site.Role == ComponentRole.Anchor ? PrepareAnchor(site) : Prepare(site);
    }

    /// <summary>
    /// The pose of <see cref="Body"/> for a component seated at <paramref name="point"/>
    /// on <paramref name="face"/>: the face's own axes, with the origin dropped
    /// <see cref="SeatDepth"/> below the face along its normal.
    /// </summary>
    public Frame3d SeatFrame(in SketchPlane face, in Vector2d point) =>
        Frame3d.FromOrthonormal(face.ToWorld(point) - face.Normal * SeatDepth, face.XAxis, face.YAxis);

    public override string ToString() => Designation;
}

/// <summary>One resolved placement of a component: which component, where its body
/// sits (<see cref="Seat"/>, ready to be an <see cref="Occurrence"/> frame), and the 2D
/// point on the seating face it came from.</summary>
public sealed record PlacedComponent(HardwareComponent Component, Frame3d Seat, Vector2d Point);
