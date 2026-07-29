using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// Sheet material and bend model: thickness, default inside bend radius, and the
/// K-factor that locates the neutral axis.
///
/// <para><b>Every sheet-metal answer in this kernel traces to two formulas, and they
/// live here so the folded body and the flat pattern cannot disagree.</b></para>
///
/// <list type="bullet">
/// <item><b>Bend allowance</b> — the developed length of the bend, i.e. how much flat
/// blank one bend consumes: <c>BA = θ·(R + K·T)</c>, with θ the bend angle in radians, R
/// the INSIDE radius, T the thickness, and K in (0, 1) the fraction of the thickness at
/// which the neutral axis sits (K = 0.5 is mid-sheet; real values run 0.3–0.5, because
/// the outer fibres stretch and the neutral axis migrates inward). This is the arc length
/// of the neutral surface, and it is the whole of the unfold model.</item>
/// <item><b>Outside setback</b> — <c>OSSB = (R + T)·tan(θ/2)</c>, the distance from
/// either tangent line to the OUTER VIRTUAL SHARP, the corner the two outside faces would
/// meet at if the bend were square.</item>
/// </list>
///
/// <para><b>Bend deduction</b> is derived, not a third model: <c>BD = 2·OSSB − BA</c>, so
/// a flat length is the sum of the two OUTSIDE leg dimensions minus BD. That identity is
/// asserted by test rather than restated in code.</para>
///
/// <para>Spring-back compensation is out of scope: it is a property of the press and the
/// material batch, not of the geometry, and folding it in here would make one number
/// pretend to be two.</para>
/// </summary>
/// <param name="Thickness">Sheet thickness in model units.</param>
/// <param name="BendRadius">Default INSIDE bend radius; individual flanges may override
/// it.</param>
/// <param name="KFactor">Default K-factor; individual flanges may override it. The
/// default 0.44 is the generic mild-steel air-bending value — see
/// <see cref="SheetMaterials"/>, whose table is transcribed and flagged
/// verify-against-datasheet.</param>
public sealed record SheetMetalSpec(double Thickness, double BendRadius, double KFactor = SheetMaterials.MildSteel)
{
    /// <summary>Bend allowance: <c>θ·(R + K·T)</c>, the flat length one bend consumes.</summary>
    public static double BendAllowance(double angleRadians, double insideRadius, double thickness, double kFactor)
    {
        RequireBend(angleRadians, insideRadius, thickness);
        RequireKFactor(kFactor);
        return angleRadians * (insideRadius + kFactor * thickness);
    }

    /// <summary>Outside setback: <c>(R + T)·tan(θ/2)</c>, tangent line to outer virtual
    /// sharp. Delegates to <see cref="SheetBendSection.OutsideSetbackOf"/>, which the
    /// FOLDED geometry also reads — one formula, one place, across the assembly
    /// boundary.</summary>
    public static double OutsideSetback(double angleRadians, double insideRadius, double thickness)
    {
        RequireBend(angleRadians, insideRadius, thickness);
        return SheetBendSection.OutsideSetbackOf(angleRadians, insideRadius, thickness);
    }

    /// <summary>Bend deduction: <c>2·OSSB − BA</c>. Subtract it from the sum of the two
    /// OUTSIDE leg dimensions to get the flat length.</summary>
    public static double BendDeduction(double angleRadians, double insideRadius, double thickness, double kFactor) =>
        2 * OutsideSetback(angleRadians, insideRadius, thickness)
        - BendAllowance(angleRadians, insideRadius, thickness, kFactor);

    /// <summary>This spec's bend allowance at <paramref name="angleRadians"/>, honouring
    /// per-flange overrides.</summary>
    public double BendAllowanceAt(double angleRadians, double? radius = null, double? kFactor = null) =>
        BendAllowance(angleRadians, radius ?? BendRadius, Thickness, kFactor ?? KFactor);

    /// <summary>This spec's outside setback at <paramref name="angleRadians"/>.</summary>
    public double OutsideSetbackAt(double angleRadians, double? radius = null) =>
        OutsideSetback(angleRadians, radius ?? BendRadius, Thickness);

    private static void RequireBend(double angleRadians, double insideRadius, double thickness)
    {
        if (!(angleRadians > 0) || angleRadians >= Math.PI)
            throw new ArgumentOutOfRangeException(nameof(angleRadians),
                $"A bend angle must lie strictly between 0 and 180 degrees; got {angleRadians * 180 / Math.PI:g6}.");
        if (!(insideRadius > 0))
            throw new ArgumentOutOfRangeException(nameof(insideRadius), "The inside bend radius must be positive.");
        if (!(thickness > 0))
            throw new ArgumentOutOfRangeException(nameof(thickness), "Sheet thickness must be positive.");
    }

    /// <summary>Separate from <see cref="RequireBend"/> on purpose: the setback has no
    /// opinion about the K-factor, and passing it a stand-in value to get past a shared
    /// check would put a number in the code that means nothing.</summary>
    private static void RequireKFactor(double kFactor)
    {
        if (!(kFactor > 0) || kFactor >= 1)
            throw new ArgumentOutOfRangeException(nameof(kFactor),
                $"The K-factor locates the neutral axis inside the sheet, so it lies in (0, 1); got {kFactor:g6}.");
    }
}

/// <summary>
/// K-factor defaults by material family. <b>Transcribed from common shop practice and
/// flagged verify-against-datasheet</b>, exactly as <c>StandardHoles</c>' Trisert table
/// is: a K-factor is a property of the material, the tooling and the forming method
/// together, and the authority is your press brake's bend-deduction chart, not this file.
/// They exist so a design can say what it means rather than typing 0.44 everywhere.
/// </summary>
public static class SheetMaterials
{
    /// <summary>Soft aluminium (1100, 3003), air bent: the neutral axis migrates furthest
    /// inward of the common materials.</summary>
    public const double SoftAluminium = 0.33;

    /// <summary>Half-hard aluminium (5052, 6061-T4).</summary>
    public const double Aluminium = 0.40;

    /// <summary>Mild steel, air bending — the generic default.</summary>
    public const double MildSteel = 0.44;

    /// <summary>Austenitic stainless (304, 316).</summary>
    public const double Stainless = 0.45;

    /// <summary>Bottoming or coining, where the bend is forced against the die and the
    /// neutral axis stays near mid-sheet. At exactly 0.5 the flat blank and the folded
    /// body have IDENTICAL volume, which is why it is the K the volume-identity test
    /// uses.</summary>
    public const double Coined = 0.50;
}

/// <summary>Which way an edge flange folds, relative to the face its edge is quoted on.</summary>
public enum SheetBendDirection
{
    /// <summary>Toward that face's own outward normal, so the face becomes the INSIDE of
    /// the bend — name an edge of the top face and the flange rises.</summary>
    Up,

    /// <summary>Away from it: the opposite face becomes the inside of the bend.</summary>
    Down,
}

/// <summary>
/// Which edge a flange grows from. The base flange's edges are its SKETCH SEGMENTS — a
/// sketch is authored data, so a segment index is a stable name rather than a derived
/// topological one — and a flange's only available edge is its TIP.
/// </summary>
/// <param name="ParentFlange">−1 for the base flange, otherwise the index of an earlier
/// flange.</param>
/// <param name="EdgeIndex">Which edge of that parent's flat outline.</param>
public readonly record struct SheetFlangeTarget(int ParentFlange, int EdgeIndex)
{
    /// <summary>Segment <paramref name="segmentIndex"/> of the base flange's sketch
    /// (counter-clockwise from the sketch's first segment).</summary>
    public static SheetFlangeTarget BaseEdge(int segmentIndex) => new(-1, segmentIndex);

    /// <summary>The tip edge of flange <paramref name="flangeIndex"/> — the only edge of a
    /// flange v1 grows from (its two SIDE edges would put two bends in one corner).</summary>
    public static SheetFlangeTarget FlangeTip(int flangeIndex) => new(flangeIndex, FlangeTipEdge);

    /// <summary>Index of a flange rectangle's tip edge in its own counter-clockwise
    /// outline: 0 is the bend line it hangs from, 1 and 3 its sides, 2 the tip.</summary>
    internal const int FlangeTipEdge = 2;

    public override string ToString() =>
        ParentFlange < 0 ? $"base edge {EdgeIndex}" : $"flange {ParentFlange} tip";
}

/// <summary>
/// One declared edge flange. <see cref="Length"/> is measured from the OUTER VIRTUAL
/// SHARP along the flange's outside face — the dimension a drawing carries — and the bend
/// is placed <b>bend-outside</b>: its tangent line IS the named edge, so the parent's flat
/// region is exactly the outline you drew and the bend grows outboard of it. That is what
/// makes the flat pattern the base sketch plus one rectangle per flange.
/// </summary>
/// <param name="Target">The edge it grows from.</param>
/// <param name="Length">Overall flange length from the outer virtual sharp; must exceed
/// the outside setback.</param>
/// <param name="AngleDegrees">Bend angle (90 = square), strictly between 0 and 180.</param>
/// <param name="Direction">Which way it folds relative to the parent face.</param>
/// <param name="BendRadius">Inside radius override; null uses the body's spec.</param>
/// <param name="KFactor">K-factor override; null uses the body's spec.</param>
/// <param name="StartOffset">Inset from the target edge's start, measured along the parent
/// outline's counter-clockwise traversal direction.</param>
/// <param name="Width">Span along the edge; null runs to the edge's far end. A flange must
/// span the WHOLE edge or be inset from both ends.</param>
public sealed record EdgeFlange(
    SheetFlangeTarget Target,
    double Length,
    double AngleDegrees = 90,
    SheetBendDirection Direction = SheetBendDirection.Up,
    double? BendRadius = null,
    double? KFactor = null,
    double StartOffset = 0,
    double? Width = null)
{
    internal double AngleRadians => AngleDegrees * Math.PI / 180;
}

/// <summary>One bend as it appears on the flat pattern: the strip of blank the bend
/// consumes, given by its two tangent lines, plus what the press brake needs to know.</summary>
/// <param name="StartTangent">One end of the tangent line on the PARENT's side.</param>
/// <param name="EndTangent">The other end of that line.</param>
/// <param name="StartFar">The matching end of the far tangent line, one bend allowance
/// outboard.</param>
/// <param name="EndFar">The other end of the far tangent line.</param>
/// <param name="AngleDegrees">Bend angle.</param>
/// <param name="InsideRadius">Inside bend radius.</param>
/// <param name="Allowance">Bend allowance: the flat width of this strip.</param>
/// <param name="Up">True when the flange folds toward the face the flat pattern is drawn
/// from — the press brake's "up" versus "down".</param>
public readonly record struct FlatBendLine(
    Vector2d StartTangent, Vector2d EndTangent, Vector2d StartFar, Vector2d EndFar,
    double AngleDegrees, double InsideRadius, double Allowance, bool Up)
{
    /// <summary>The bend centre line — midway between the two tangent lines, which is
    /// where a single-line bend annotation goes.</summary>
    public (Vector2d Start, Vector2d End) CenterLine =>
        ((StartTangent + StartFar) * 0.5, (EndTangent + EndFar) * 0.5);
}

/// <summary>
/// A sheet-metal part's flat pattern: the blank a laser cuts, plus the bend lines a press
/// brake folds on. Coordinates are the base flange's own sketch coordinates, so a hole
/// drawn in the base sketch keeps its position.
/// </summary>
/// <param name="Outline">The blank, holes included.</param>
/// <param name="Bends">One entry per bend, in the order the flanges were added.</param>
/// <param name="Thickness">Sheet thickness — the flat's third dimension.</param>
public sealed record FlatPattern(Sketch Outline, IReadOnlyList<FlatBendLine> Bends, double Thickness)
{
    /// <summary>Blank area (outline minus holes) — what a nesting quote is priced from.</summary>
    public double Area => Outline.Area();

    /// <summary>Blank volume: <see cref="Area"/> × thickness. Compare it against the
    /// folded body's volume to check the bend model: they agree EXACTLY at K = 0.5 and
    /// differ by <c>Σ width·θ·T²·(0.5 − K)</c> otherwise, which is the K-factor doing its
    /// job rather than an error.</summary>
    public double Volume => Area * Thickness;

    /// <summary>
    /// The flat pattern as a DXF document — what a laser or turret shop actually cuts
    /// from. The blank goes on <paramref name="cutLayer"/> and each bend zone's two
    /// tangent lines on <paramref name="bendLayer"/>, which is given the CENTER line type
    /// so a reader that honours the LTYPE table shows them chain-dashed rather than as
    /// cuts.
    /// </summary>
    public DxfDocument ToDxf(string cutLayer = "CUT", string bendLayer = "BEND")
    {
        var document = new DxfDocument();
        document.Add(Outline, cutLayer);
        foreach (var bend in Bends)
        {
            document.Add(new DxfLine(bend.StartTangent, bend.EndTangent, bendLayer));
            document.Add(new DxfLine(bend.StartFar, bend.EndFar, bendLayer));
        }
        document.LayerLineTypes[cutLayer] = DxfLineTypes.Continuous.Name;
        if (Bends.Count > 0)
            document.LayerLineTypes[bendLayer] = DxfLineTypes.Center.Name;
        return document;
    }

    /// <summary>
    /// The flat pattern as an SVG drawing — the same content as <see cref="ToDxf"/>, for
    /// looking at rather than cutting. The blank draws as a visible outline and the bend
    /// zones as chain-dashed <see cref="SvgLineClass.Section"/> lines, which is what a
    /// cutting plane looks like in this kernel's line-class vocabulary and reads correctly
    /// as "fold here, do not cut".
    /// </summary>
    public SvgDrawing ToDrawing()
    {
        var drawing = new SvgDrawing();
        drawing.Add(Outline, SvgLineClass.Visible, "cut");
        foreach (var bend in Bends)
        {
            drawing.AddPolyline([bend.StartTangent, bend.EndTangent], false, SvgLineClass.Section, "bend");
            drawing.AddPolyline([bend.StartFar, bend.EndFar], false, SvgLineClass.Section, "bend");
        }
        return drawing;
    }
}

/// <summary>A flange-able edge of the current body: what a selector can name, and the
/// bridge from a picked <c>BrepEdge</c> back into the flange tree.</summary>
/// <param name="Target">The edge's coordinates in the flange tree.</param>
/// <param name="Start">Its 3D start, on the parent's "top" face.</param>
/// <param name="End">Its 3D end.</param>
/// <param name="TopNormal">Outward normal of the face the edge is quoted on.</param>
/// <param name="Outward">Direction out of the material at that edge.</param>
public readonly record struct SheetFlangeSite(
    SheetFlangeTarget Target, Vector3d Start, Vector3d End, Vector3d TopNormal, Vector3d Outward)
{
    public double Length => Start.DistanceTo(End);
}

/// <summary>
/// A sheet-metal part: a base flange (a sketch, extruded to the sheet's thickness) plus an
/// ordered tree of edge flanges. <b>The declaration IS the model</b> — both the folded
/// solid and the flat pattern are derived from the same numbers, which is what stops them
/// drifting apart.
///
/// <para>The body is immutable: <see cref="WithFlange(EdgeFlange)"/> returns a new one, so
/// a <c>FeatureHistory</c> can hold a chain of them and regenerate.</para>
///
/// <para><b>v1 scope</b>, refused by name rather than approximated: flanges only on
/// STRAIGHT base-sketch segments and on flange TIPS; no closed corners, miters, bend
/// reliefs, jogs, hems, louvres or multi-body sheets; holes belong to the base sketch and
/// carry through to the flat pattern unchanged, but a flange carries none.</para>
/// </summary>
public sealed class SheetMetalBody
{
    /// <summary>The epsilon ladder's absolute WELD tier, named rather than re-typed. Every
    /// use here compares a model-unit LENGTH between points a single frame chain
    /// constructed, so the absolute tier is right and none of it is a measurement
    /// tolerance.</summary>
    internal static readonly double Weld = Tolerance.Default.Linear;

    private readonly List<EdgeFlange> _flanges;
    private ResolvedTree? _tree;

    private SheetMetalBody(Sketch baseSketch, SketchPlane plane, SheetMetalSpec spec, List<EdgeFlange> flanges)
    {
        BaseSketch = baseSketch;
        Plane = plane;
        Spec = spec;
        _flanges = flanges;
    }

    /// <summary>The base flange: this outline, extruded along the plane's normal to the
    /// sheet's thickness.</summary>
    public static SheetMetalBody Base(Sketch sketch, SheetMetalSpec spec, SketchPlane? plane = null)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        ArgumentNullException.ThrowIfNull(spec);
        if (!(spec.Thickness > 0))
            throw new ArgumentOutOfRangeException(nameof(spec), "Sheet thickness must be positive.");
        if (!(spec.BendRadius > 0))
            throw new ArgumentOutOfRangeException(nameof(spec), "The default inside bend radius must be positive.");
        return new SheetMetalBody(sketch, plane ?? SketchPlane.XY, spec, []);
    }

    public Sketch BaseSketch { get; }
    public SketchPlane Plane { get; }
    public SheetMetalSpec Spec { get; }
    public IReadOnlyList<EdgeFlange> Flanges => _flanges;

    /// <summary>This body with one more flange — a NEW body; this one is unchanged. The
    /// addition is validated here, at the call that made it.</summary>
    public SheetMetalBody WithFlange(EdgeFlange flange)
    {
        ArgumentNullException.ThrowIfNull(flange);
        var body = new SheetMetalBody(BaseSketch, Plane, Spec, [.. _flanges, flange]);
        _ = body.Tree;
        return body;
    }

    /// <inheritdoc cref="WithFlange(EdgeFlange)"/>
    public SheetMetalBody WithFlange(
        SheetFlangeTarget target, double length, double angleDegrees = 90,
        SheetBendDirection direction = SheetBendDirection.Up,
        double? bendRadius = null, double? kFactor = null,
        double startOffset = 0, double? width = null) =>
        WithFlange(new EdgeFlange(
            target, length, angleDegrees, direction, bendRadius, kFactor, startOffset, width));

    /// <summary>The folded solid as a <see cref="Shape"/> — B-Rep native, built by
    /// topology surgery rather than by booleans (see <see cref="SheetMetalSurgery"/>).</summary>
    public Shape Solid => new SheetMetalShape(this);

    /// <summary>Every edge a flange could grow from on the CURRENT body — the catalogue a
    /// selector resolves against, and what <see cref="SiteFor"/> matches a picked edge to.</summary>
    public IReadOnlyList<SheetFlangeSite> Sites => Tree.Sites;

    /// <summary>
    /// The flange-tree coordinates of a 3D edge — how a selector-based feature ("the
    /// flange on THAT edge") reaches the declarative model. Matched on the edge's two
    /// endpoints at the weld tier, since both were constructed from the same frames.
    /// </summary>
    public SheetFlangeSite SiteFor(BrepEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        if (!edge.IsLinear(out var start, out var end))
            throw new ArgumentException(
                "A sheet flange grows from a STRAIGHT edge; this one is curved.", nameof(edge));
        foreach (var site in Sites)
        {
            if (JoinsSamePoints(site.Start, site.End, start, end))
                return site;
        }
        throw new ArgumentException(
            $"The edge from {start} to {end} is not one of this sheet body's flange-able edges. It carries " +
            $"{Sites.Count}: {string.Join(", ", Sites.Select(s => s.Target.ToString()))}.", nameof(edge));
    }

    /// <summary>Do two segments join the same pair of points, either way round? The one
    /// spelling of "this is that edge", shared by <see cref="SiteFor"/> and
    /// <see cref="SheetMetalFeatures.EdgeBetween"/> so a selector and its resolution
    /// cannot disagree.</summary>
    internal static bool JoinsSamePoints(
        in Vector3d aStart, in Vector3d aEnd, in Vector3d bStart, in Vector3d bEnd)
    {
        var tolerance = Tolerance.Default;
        return (aStart.AreEqual(bStart, tolerance) && aEnd.AreEqual(bEnd, tolerance))
            || (aStart.AreEqual(bEnd, tolerance) && aEnd.AreEqual(bStart, tolerance));
    }

    /// <summary>The flat pattern: blank outline plus bend lines, in the base sketch's own
    /// coordinates. Pure bookkeeping over the flange tree — each bend contributes its
    /// allowance, and nothing is re-derived from geometry.</summary>
    public FlatPattern Unfold() => Tree.Unfold();

    internal ResolvedTree Tree => _tree ??= ResolvedTree.Build(this);

    /// <summary>Grows every flange onto an already-built base solid. <paramref name="top"/>
    /// is the placed base flange's TOP face — where every bend line is quoted from — and
    /// <paramref name="scale"/> the placement's uniform factor (the compiler has already
    /// refused a shear or a non-uniform scale).</summary>
    internal BrepSolid BuildBrep(BrepSolid baseSolid, in SheetFrame top, double scale)
    {
        var solid = baseSolid;
        foreach (var bend in Tree.Bends(top, scale))
            solid = SheetMetalSurgery.AddEdgeFlange(solid, bend.Section, bend.Start, bend.End, bend.WallLength);
        return solid;
    }

    internal string Describe() =>
        $"SheetMetal(t={Spec.Thickness:g4}, {_flanges.Count} flange{(_flanges.Count == 1 ? "" : "s")})";

    // ------------------------------------------------------------------ resolved tree

    /// <summary>A node's flat-pattern frame: an origin and a unit X in the flat plane
    /// (Y is X turned a quarter turn counter-clockwise), so the node's own 2D coordinates
    /// map rigidly and orientation-preservingly onto the blank.</summary>
    internal readonly record struct FlatFrame(Vector2d Origin, Vector2d X)
    {
        public Vector2d Y => X.Perpendicular;
        public Vector2d ToFlat(in Vector2d local) => Origin + X * local.X + Y * local.Y;
        public Vector2d Direction(in Vector2d local) => X * local.X + Y * local.Y;
    }

    /// <summary>A node's 3D frame on its "top" face: the same local coordinates, placed in
    /// space. Right-handed, with Z the top face's outward normal.</summary>
    internal readonly record struct SheetFrame(Vector3d Origin, Vector3d X, Vector3d Y)
    {
        public Vector3d Normal => X.Cross(Y);
        public Vector3d ToWorld(in Vector2d local, double scale) =>
            Origin + X * (local.X * scale) + Y * (local.Y * scale);
        public Vector3d Direction(in Vector2d local) => X * local.X + Y * local.Y;
    }

    /// <summary>One resolved flange: everything the folded build and the unfold both read,
    /// with no geometry in it beyond the parent-local edge it grows from.</summary>
    internal sealed class Node
    {
        public required int Index { get; init; }       // −1 for the base
        public required int Parent { get; init; }
        public EdgeFlange? Flange { get; init; }
        public required FlatFrame Flat { get; init; }
        public Vector2d EdgeStart { get; init; }       // in the PARENT's local coordinates
        public Vector2d EdgeTangent { get; init; }

        /// <summary>Out of the material at that edge: the RIGHT of a counter-clockwise
        /// traversal. Derived, so it can never be stored inconsistent with the tangent.</summary>
        public Vector2d EdgeOutward => -EdgeTangent.Perpendicular;

        public double StartOffset { get; init; }
        public double Width { get; init; }
        public double WallLength { get; init; }
        public double Allowance { get; init; }
        public double InsideRadius { get; init; }
        public List<Node> Children { get; } = [];
    }

    /// <summary>The surgery arguments for one bend.</summary>
    internal readonly record struct BendArgs(
        SheetBendSection Section, Vector3d Start, Vector3d End, double WallLength);

    /// <summary>The declaration resolved into flat frames and lengths — computed once, and
    /// the single source both the folded build and the unfold read.</summary>
    internal sealed class ResolvedTree
    {
        private readonly SheetMetalBody _body;
        private readonly Node _base;
        private readonly List<Node> _nodes;

        /// <summary>The base sketch's own curves, materialized ONCE. A `Sketch` is
        /// immutable, so the resolver, the unfold and the site catalogue can share one
        /// list instead of each rebuilding it (which is a fresh `Curve2d` per segment).</summary>
        private readonly IReadOnlyList<Curve2d> _curves;

        private IReadOnlyList<SheetFlangeSite>? _sites;

        private ResolvedTree(
            SheetMetalBody body, Node baseNode, List<Node> nodes, IReadOnlyList<Curve2d> curves)
        {
            _body = body;
            _base = baseNode;
            _nodes = nodes;
            _curves = curves;
        }

        /// <summary>Lazy on purpose: <see cref="SheetMetalBody.WithFlange(EdgeFlange)"/>
        /// builds the tree to VALIDATE the addition, and only a selector ever asks for the
        /// sites — so building a body flange by flange stays linear instead of walking the
        /// whole chain once per addition.</summary>
        public IReadOnlyList<SheetFlangeSite> Sites
        {
            get
            {
                if (_sites is null)
                {
                    var frame = _body.Plane.Frame;
                    _sites = CollectSites(
                        new SheetFrame(frame.Origin + frame.Z * _body.Spec.Thickness, frame.X, frame.Y), 1);
                }
                return _sites;
            }
        }

        public static ResolvedTree Build(SheetMetalBody body)
        {
            var baseNode = new Node
            {
                Index = -1,
                Parent = int.MinValue,
                Flat = new FlatFrame(default, new Vector2d(1, 0)),
            };
            var curves = body.BaseSketch.ToCurves();
            var nodes = new List<Node>(body._flanges.Count);
            for (int i = 0; i < body._flanges.Count; i++)
            {
                var flange = body._flanges[i];
                var parent = flange.Target.ParentFlange < 0
                    ? baseNode
                    : flange.Target.ParentFlange >= 0 && flange.Target.ParentFlange < nodes.Count
                        ? nodes[flange.Target.ParentFlange]
                        : throw new ArgumentException(
                            $"Flange {i} names parent flange {flange.Target.ParentFlange}, which has not been " +
                            "added yet; a flange grows from the base or from an EARLIER flange.");

                var node = Resolve(body, curves, i, flange, parent);
                parent.Children.Add(node);
                nodes.Add(node);
            }
            return new ResolvedTree(body, baseNode, nodes, curves);
        }

        private static Node Resolve(
            SheetMetalBody body, IReadOnlyList<Curve2d> curves, int index, EdgeFlange flange, Node parent)
        {
            var (a2, b2) = EdgeOf(curves, parent, flange.Target, index);
            var along = b2 - a2;
            double edgeLength = along.Length;
            var t2 = along.Normalized();
            var o2 = -t2.Perpendicular;   // outward: right of a counter-clockwise traversal

            double start = flange.StartOffset;
            double width = flange.Width ?? edgeLength - start;
            if (start < 0 || width <= 0 || start + width > edgeLength + Weld)
                throw new ArgumentException(
                    $"Flange {index} on {flange.Target} spans [{start:g6}, {start + width:g6}] of an edge " +
                    $"{edgeLength:g6} long; the span must lie inside it.");
            foreach (var sibling in parent.Children)
            {
                if (sibling.Flange!.Target.EdgeIndex != flange.Target.EdgeIndex)
                    continue;
                if (start < sibling.StartOffset + sibling.Width - Weld
                    && sibling.StartOffset < start + width - Weld)
                    throw new ArgumentException(
                        $"Flange {index} overlaps flange {sibling.Index} on {flange.Target}: two bends cannot " +
                        "share the same stretch of one edge.");
            }

            // Override resolution happens ONCE, in the spec's own accessors, so a flange's
            // per-bend radius and K cannot be applied one way here and another way there.
            double radius = flange.BendRadius ?? body.Spec.BendRadius;
            double angle = flange.AngleRadians;
            double setback = body.Spec.OutsideSetbackAt(angle, flange.BendRadius);
            double wall = flange.Length - setback;
            if (!(wall > Weld))
                throw new ArgumentException(
                    $"Flange {index} is {flange.Length:g6} long, measured from the outer virtual sharp, but the " +
                    $"outside setback (R + T)*tan(angle/2) is already {setback:g6}. Lengthen the flange, reduce " +
                    "the bend radius, or reduce the angle.");
            double allowance = body.Spec.BendAllowanceAt(angle, flange.BendRadius, flange.KFactor);

            // The flange's own flat frame: origin at the far end of its bend zone, x
            // running back along the edge so the rectangle occupies x in [0, width].
            return new Node
            {
                Index = index,
                Parent = parent.Index,
                Flange = flange,
                Flat = new FlatFrame(
                    parent.Flat.ToFlat(a2 + t2 * (start + width) + o2 * allowance),
                    parent.Flat.Direction(-t2)),
                EdgeStart = a2,
                EdgeTangent = t2,
                StartOffset = start,
                Width = width,
                WallLength = wall,
                Allowance = allowance,
                InsideRadius = radius,
            };
        }

        /// <summary>The target edge in the parent's own 2D coordinates, start to end in
        /// counter-clockwise traversal order.</summary>
        private static (Vector2d Start, Vector2d End) EdgeOf(
            IReadOnlyList<Curve2d> curves, Node parent, SheetFlangeTarget target, int index)
        {
            if (parent.Index < 0)
            {
                if (target.EdgeIndex < 0 || target.EdgeIndex >= curves.Count)
                    throw new ArgumentException(
                        $"Flange {index} names base edge {target.EdgeIndex}, but the base sketch has " +
                        $"{curves.Count} segment(s).");
                if (curves[target.EdgeIndex] is not Line2d line)
                    throw new NotSupportedException(
                        $"Flange {index} names base edge {target.EdgeIndex}, which is a " +
                        $"{curves[target.EdgeIndex].GetType().Name}. A bend line must be STRAIGHT: a bend along " +
                        "a curved edge sweeps a developable band rather than a cylinder, and v1 refuses it " +
                        "rather than approximating it.");
                return (line.Start, line.End);
            }

            if (target.EdgeIndex != SheetFlangeTarget.FlangeTipEdge)
                throw new NotSupportedException(
                    $"Flange {index} grows from edge {target.EdgeIndex} of flange {parent.Index}. v1 grows a " +
                    "flange only from another flange's TIP (edge 2): edge 0 is the bend line it already hangs " +
                    "from, and edges 1 and 3 are its sides, where two bends would meet in a corner.");
            // The flange's rectangle, counter-clockwise: (0,0) (W,0) (W,wall) (0,wall).
            return (new Vector2d(parent.Width, parent.WallLength), new Vector2d(0, parent.WallLength));
        }

        // ------------------------------------------------------------------ folded build

        /// <summary>Each bend's surgery arguments, in declaration order. The 3D chain is
        /// recomputed from the base frame every time, so a placed body and an unplaced one
        /// run the same code.</summary>
        public IReadOnlyList<BendArgs> Bends(in SheetFrame top, double scale) => Chain(top, scale).Bends;

        private (Dictionary<int, SheetFrame> Frames, List<BendArgs> Bends) Chain(
            in SheetFrame baseFrame, double scale)
        {
            var frames = new Dictionary<int, SheetFrame> { [-1] = baseFrame };
            var bends = new List<BendArgs>(_nodes.Count);
            double thickness = _body.Spec.Thickness * scale;
            foreach (var node in _nodes)
            {
                var parent = frames[node.Parent];
                var startSection = SectionAt(parent, node, node.StartOffset, thickness, scale);
                var endSection = SectionAt(parent, node, node.StartOffset + node.Width, thickness, scale);
                bends.Add(new BendArgs(
                    startSection, startSection.BendLinePoint, endSection.BendLinePoint, node.WallLength * scale));

                bool up = node.Flange!.Direction == SheetBendDirection.Up;
                frames[node.Index] = new SheetFrame(
                    up ? endSection.InsideTangentPoint : endSection.OutsideTangentPoint,
                    -parent.Direction(node.EdgeTangent),
                    endSection.FlangeDirection);
            }
            return (frames, bends);
        }

        /// <summary>The bend cross-section at parameter <paramref name="s"/> along the
        /// target edge — the closed form both the folded build and the frame recursion
        /// read, so they cannot disagree about where a bend sits.</summary>
        private static SheetBendSection SectionAt(
            in SheetFrame parent, Node node, double s, double thickness, double scale)
        {
            var top = parent.ToWorld(node.EdgeStart + node.EdgeTangent * s, scale);
            var normal = parent.Normal;
            bool up = node.Flange!.Direction == SheetBendDirection.Up;
            return new SheetBendSection(
                BendLinePoint: up ? top : top - normal * thickness,
                Inside: up ? normal : -normal,
                Outward: parent.Direction(node.EdgeOutward),
                Thickness: thickness,
                BendRadius: node.InsideRadius * scale,
                AngleRadians: node.Flange!.AngleRadians);
        }

        // ---------------------------------------------------------------------- unfold

        public FlatPattern Unfold()
        {
            var curves = _curves;
            var bends = new List<FlatBendLine>();
            var spliced = new List<Curve2d>();

            for (int i = 0; i < curves.Count; i++)
            {
                var here = _base.Children
                    .Where(c => c.Flange!.Target.EdgeIndex == i)
                    .OrderBy(c => c.StartOffset)
                    .ToList();
                if (here.Count == 0)
                {
                    spliced.Add(curves[i]);
                    continue;
                }
                var line = (Line2d)curves[i];
                var points = new List<Vector2d> { _base.Flat.ToFlat(line.Start) };
                SpliceEdge(_base, here, points, bends);
                points.Add(_base.Flat.ToFlat(line.End));
                spliced.AddRange(Polyline(points));
            }

            var outline = Sketch.FromCurves(spliced);
            foreach (var hole in _body.BaseSketch.Holes)
                outline = outline.WithHole(hole);
            return new FlatPattern(outline, bends, _body.Spec.Thickness);
        }

        /// <summary>Detours around every flange on one edge of <paramref name="owner"/>'s
        /// outline, appending FLAT points strictly between the edge's own ends.</summary>
        private static void SpliceEdge(
            Node owner, IReadOnlyList<Node> children, List<Vector2d> points, List<FlatBendLine> bends)
        {
            foreach (var child in children)
            {
                var p0 = child.EdgeStart + child.EdgeTangent * child.StartOffset;
                var p1 = child.EdgeStart + child.EdgeTangent * (child.StartOffset + child.Width);
                var far0 = p0 + child.EdgeOutward * child.Allowance;
                var far1 = p1 + child.EdgeOutward * child.Allowance;

                // The bend zone and the flange's wall are one straight run of blank in
                // the outline — the tangent line between them is a BEND, not a cut — so
                // the detour goes straight from the edge to the flange's far corner and
                // the tangent points appear only in the bend record below.
                points.Add(owner.Flat.ToFlat(p0));
                AppendFlangeOutline(child, points, bends);
                points.Add(owner.Flat.ToFlat(p1));

                bends.Add(new FlatBendLine(
                    owner.Flat.ToFlat(p0), owner.Flat.ToFlat(p1),
                    owner.Flat.ToFlat(far0), owner.Flat.ToFlat(far1),
                    child.Flange!.AngleDegrees, child.InsideRadius, child.Allowance,
                    child.Flange.Direction == SheetBendDirection.Up));
            }
        }

        /// <summary>Appends a flange's own outline in flat coordinates, entered at its
        /// local (width, 0) and left at (0, 0) — counter-clockwise past its side, its tip
        /// (with any flanges of its own spliced in) and its other side.</summary>
        private static void AppendFlangeOutline(Node node, List<Vector2d> points, List<FlatBendLine> bends)
        {
            points.Add(node.Flat.ToFlat(new Vector2d(node.Width, node.WallLength)));
            SpliceEdge(node, [.. node.Children.OrderBy(c => c.StartOffset)], points, bends);
            points.Add(node.Flat.ToFlat(new Vector2d(0, node.WallLength)));
        }

        private static IEnumerable<Curve2d> Polyline(IReadOnlyList<Vector2d> points)
        {
            for (int i = 0; i + 1 < points.Count; i++)
            {
                // A zero-length step arises wherever a flange sits flush with its edge's
                // end; the sketch constructor would reject it, and dropping it is exactly
                // right — the two points are the same corner.
                if (points[i].DistanceTo(points[i + 1]) > Weld)
                    yield return new Line2d(points[i], points[i + 1]);
            }
        }

        private IReadOnlyList<SheetFlangeSite> CollectSites(in SheetFrame baseFrame, double scale)
        {
            var frames = Chain(baseFrame, scale).Frames;
            var sites = new List<SheetFlangeSite>();
            for (int i = 0; i < _curves.Count; i++)
            {
                if (_curves[i] is not Line2d line)
                    continue;
                var t2 = (line.End - line.Start).Normalized();
                sites.Add(new SheetFlangeSite(
                    SheetFlangeTarget.BaseEdge(i),
                    baseFrame.ToWorld(line.Start, scale), baseFrame.ToWorld(line.End, scale),
                    baseFrame.Normal, baseFrame.Direction(-t2.Perpendicular)));
            }
            foreach (var node in _nodes)
            {
                var frame = frames[node.Index];
                sites.Add(new SheetFlangeSite(
                    SheetFlangeTarget.FlangeTip(node.Index),
                    frame.ToWorld(new Vector2d(node.Width, node.WallLength), scale),
                    frame.ToWorld(new Vector2d(0, node.WallLength), scale),
                    frame.Normal, frame.Y));   // a flange's tip faces along its own +y
            }
            return sites;
        }
    }
}
