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

/// <summary>The shape of a bend relief's notch.</summary>
/// <remarks>
/// <b>The TEAR relief is deliberately absent.</b> A tear relief is what happens when no
/// relief is cut at all: the material tears as the bend runs out, leaving a torn edge whose
/// shape is a property of the press and the batch — the same call
/// <see cref="SheetMetalSpec"/> makes about spring-back. Modelling it would mean drawing a
/// shape nobody can predict; leaving the relief off is the honest way to say "let it tear".
/// </remarks>
public enum SheetReliefKind
{
    /// <summary>A square-cornered slot: two sides and a flat bottom.</summary>
    Rectangular,

    /// <summary>A slot rounded at the bottom with a semicircle of half the relief's width —
    /// the fatigue-friendly form, since a square inside corner is a stress raiser. Its
    /// <see cref="BendRelief.Depth"/> reaches the deepest point of the dome, so a relief can
    /// never be shallower than its own radius.</summary>
    Obround,
}

/// <summary>
/// A bend relief: the notch cut into the parent sheet beside a flange's bend line, so the
/// bend can run out without tearing the material next to it.
///
/// <para><b>A relief is a change to the BLANK, not a cut in the folded body.</b> It notches
/// the base flange's own outline (<see cref="SheetMetalBody.BaseOutline"/>) — which is what
/// the folded sheet is extruded from and what the flat pattern starts from — so there is no
/// boolean and no second description: the same declaration produces both views, exactly as
/// an edge flange does. It also makes the geometry simpler rather than harder, because a
/// relieved flange then runs the full width of the (now shortened) wall between its two
/// notches, which is the surgery's ordinary flush case.</para>
///
/// <para>A relief is placed at each INSET end of its flange. A flush end has no parent
/// material beside it to relieve, so a flange spanning its whole edge is refused by name
/// rather than silently given nothing.</para>
/// </summary>
/// <param name="Kind">Notch shape.</param>
/// <param name="Width">Notch width along the bend line; null uses one sheet thickness, the
/// usual shop minimum.</param>
/// <param name="Depth">How far the notch reaches into the parent, measured from the bend
/// line; null uses <c>R + T</c>, which clears the bend's own tangent region.</param>
public sealed record BendRelief(
    SheetReliefKind Kind = SheetReliefKind.Rectangular,
    double? Width = null,
    double? Depth = null)
{
    /// <summary>A square-cornered relief.</summary>
    public static BendRelief Rectangular(double? width = null, double? depth = null) =>
        new(SheetReliefKind.Rectangular, width, depth);

    /// <summary>A relief rounded at the bottom, radius half the width.</summary>
    public static BendRelief Obround(double? width = null, double? depth = null) =>
        new(SheetReliefKind.Obround, width, depth);

    /// <summary>Notch width, resolved against the sheet: one thickness unless stated.</summary>
    public double WidthFor(SheetMetalSpec spec) =>
        Width ?? (spec ?? throw new ArgumentNullException(nameof(spec))).Thickness;

    /// <summary>Notch depth, resolved against the sheet and the flange's own inside bend
    /// radius: <c>R + T</c> unless stated, which is the depth that takes the notch past the
    /// bend's outer tangent line.</summary>
    public double DepthFor(SheetMetalSpec spec, double? insideRadius = null) =>
        Depth ?? (insideRadius ?? (spec ?? throw new ArgumentNullException(nameof(spec))).BendRadius)
            + (spec ?? throw new ArgumentNullException(nameof(spec))).Thickness;

    /// <summary>Area of ONE notch — the material a relief removes from the blank, and by
    /// the same token from the folded body, which is why a relief cannot move the
    /// folded-versus-flat discrepancy.</summary>
    public double AreaOf(double width, double depth) => Kind == SheetReliefKind.Obround
        ? width * (depth - width / 2) + Math.PI * width * width / 8
        : width * depth;
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
/// <param name="Width">Span along the edge; null runs to the edge's far end. Either end may
/// be flush with the edge or inset from it, independently.</param>
/// <param name="Relief">The <see cref="BendRelief"/> notched into the parent beside each
/// INSET end of this flange; null cuts none, which is the "let it tear" choice.</param>
/// <param name="Cutouts">Holes punched through this flange's WALL, each a closed
/// <see cref="Sketch"/> in the flange's OWN local coordinates — see
/// <see cref="SheetMetalBody"/>'s remarks on the flange frame. They reach both views from
/// this one declaration, exactly as a bend relief does: the folded wall is punched and the
/// blank gains the same holes through the flange's rigid flat frame.</param>
public sealed record EdgeFlange(
    SheetFlangeTarget Target,
    double Length,
    double AngleDegrees = 90,
    SheetBendDirection Direction = SheetBendDirection.Up,
    double? BendRadius = null,
    double? KFactor = null,
    double StartOffset = 0,
    double? Width = null,
    BendRelief? Relief = null,
    IReadOnlyList<Sketch>? Cutouts = null)
{
    internal double AngleRadians => AngleDegrees * Math.PI / 180;

    /// <summary>The declared cutouts, never null — one spelling for "none".</summary>
    internal IReadOnlyList<Sketch> CutoutList => Cutouts ?? [];
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

    /// <summary>Length of the bend line: how much of the brake's tooling the bend
    /// occupies, and the width the tonnage is quoted per.</summary>
    public double Length => StartTangent.DistanceTo(EndTangent);
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
    /// The bend table a press brake is set up from: one row per bend in the order the
    /// flanges were declared, giving the length of the bend line, the angle, which way it
    /// folds, the inside radius and the allowance.
    /// <para>Every column is READ OFF <see cref="Bends"/> — the same records the drawing's
    /// bend zones are drawn from — rather than recomputed from the flange tree, so the
    /// table and the picture cannot disagree about a bend. A pattern with no bends returns
    /// a header and nothing else, which is the honest answer for a flat part.</para>
    /// </summary>
    public string BendTable()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine("BEND  LENGTH   ANGLE  DIR   RADIUS  ALLOWANCE");
        for (int i = 0; i < Bends.Count; i++)
        {
            var bend = Bends[i];
            text.AppendLine(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{i + 1,4}  {bend.Length,6:0.000}  {bend.AngleDegrees,6:0.0}  " +
                $"{(bend.Up ? "UP  " : "DOWN"),-4}  {bend.InsideRadius,6:0.000}  {bend.Allowance,9:0.000}"));
        }
        return text.ToString();
    }

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

    /// <summary>
    /// The base flange's outline as BUILT: <see cref="BaseSketch"/> with every
    /// <see cref="BendRelief"/> notched into it. This is the region the folded sheet is
    /// extruded from and the region the flat pattern starts from, which is what makes a
    /// relief one declaration rather than a cut applied twice.
    /// <para>With no reliefs declared it IS <see cref="BaseSketch"/>, by reference — so a
    /// design that cuts none is untouched down to the bit.</para>
    /// </summary>
    public Sketch BaseOutline => Tree.BaseOutline;

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
        double startOffset = 0, double? width = null, BendRelief? relief = null,
        IReadOnlyList<Sketch>? cutouts = null) =>
        WithFlange(new EdgeFlange(
            target, length, angleDegrees, direction, bendRadius, kFactor, startOffset, width, relief,
            cutouts));

    /// <summary>
    /// A HEM: the edge folded back on itself, as TWO bends in the same direction rather
    /// than as one 180° fold.
    ///
    /// <para><b>Why two.</b> A single 180° bend has no geometry at all in this model — its
    /// outside setback <c>(R + T)·tan(θ/2)</c> is infinite at exactly 180° — and a return
    /// leg lying flat against the sheet is the coincident boundary the whole feature exists
    /// to avoid. Two bends is also how a press brake actually makes one, so the model and
    /// the process agree: the intermediate leg IS the hem's open gap, and its length is
    /// derived from the gap the caller states.</para>
    ///
    /// <para>The <b>gap is measured between the sheet's own quoted face and the returned
    /// leg's facing surface</b>, and it is <c>2R + L</c> for an intermediate wall of length
    /// L — so a gap of <c>2R</c> or less is a CLOSED hem and is refused by name. A tight
    /// hem is spelt with a small bend radius, which is what flattening the material in the
    /// press means.</para>
    /// </summary>
    /// <param name="target">The edge to hem.</param>
    /// <param name="returnLength">How far the returned leg runs back, from its own outer
    /// virtual sharp — the ordinary <see cref="EdgeFlange.Length"/> convention.</param>
    /// <param name="gap">Open gap between the sheet and the returned leg; null uses
    /// <c>2R + T</c>, which clears the sheet by one thickness.</param>
    public SheetMetalBody WithHem(
        SheetFlangeTarget target, double returnLength, double? gap = null,
        double? bendRadius = null, SheetBendDirection direction = SheetBendDirection.Up,
        double startOffset = 0, double? width = null)
    {
        double radius = bendRadius ?? Spec.BendRadius;
        double opening = gap ?? (2 * radius + Spec.Thickness);
        double intermediate = opening - 2 * radius;
        if (!(intermediate > Weld))
        {
            throw new ArgumentOutOfRangeException(nameof(gap),
                $"A hem gap of {opening:g6} needs an intermediate leg of {intermediate:g6} at bend radius " +
                $"{radius:g6}, since the gap is 2R plus that leg. A gap of 2R or less is a CLOSED hem, whose " +
                "returned leg lies flat against the sheet — coincident boundary, which this kernel refuses " +
                "everywhere. Open the gap, or use a smaller bend radius (flattening the material is what a " +
                "press does to close a hem).");
        }

        // A square bend's setback is exactly R + T, so the first flange's Length -- measured
        // from its own outer virtual sharp -- is the intermediate leg plus that.
        var stage = WithFlange(new EdgeFlange(
            target, intermediate + radius + Spec.Thickness, 90, direction, bendRadius,
            StartOffset: startOffset, Width: width));
        // The SAME direction folds back: a flange's own top face is the continuation of the
        // face its bend line was quoted on, so "toward it" turns the leg back over the sheet.
        return stage.WithFlange(new EdgeFlange(
            SheetFlangeTarget.FlangeTip(stage.Flanges.Count - 1), returnLength, 90, direction, bendRadius));
    }

    /// <summary>
    /// A JOG (offset): two equal and OPPOSITE bends, so the sheet steps sideways and
    /// carries on parallel to itself.
    ///
    /// <para>The intermediate leg is derived in closed form from the offset the caller
    /// states: with both bends at θ and radius R, the perpendicular step is
    /// <c>(2R + T)(1 − cos θ) + L·sin θ</c>, so <c>L = (offset − (2R + T)(1 − cos θ)) /
    /// sin θ</c>. Below that minimum the two bends would have to overlap, and the refusal
    /// names the smallest offset the radius and angle allow.</para>
    /// </summary>
    /// <param name="offset">Perpendicular step between the sheet's quoted face and the
    /// corresponding face of the leg beyond the jog.</param>
    /// <param name="runLength">How far the leg beyond the jog runs, from its own outer
    /// virtual sharp.</param>
    /// <param name="angleDegrees">Each bend's angle; 90 makes a square step.</param>
    public SheetMetalBody WithJog(
        SheetFlangeTarget target, double offset, double runLength, double angleDegrees = 90,
        double? bendRadius = null, SheetBendDirection direction = SheetBendDirection.Up,
        double startOffset = 0, double? width = null)
    {
        double radius = bendRadius ?? Spec.BendRadius;
        double angle = angleDegrees * Math.PI / 180;
        if (!(angleDegrees > 0) || angleDegrees >= 180)
            throw new ArgumentOutOfRangeException(nameof(angleDegrees),
                $"A jog's two bends each turn {angleDegrees:g6} degrees; each must lie strictly between 0 " +
                "and 180.");
        double minimum = (2 * radius + Spec.Thickness) * (1 - Math.Cos(angle));
        double intermediate = (offset - minimum) / Math.Sin(angle);
        if (!(intermediate > Weld))
        {
            throw new ArgumentOutOfRangeException(nameof(offset),
                $"A jog of {offset:g6} at {angleDegrees:g6} degrees and radius {radius:g6} leaves an " +
                $"intermediate leg of {intermediate:g6}: the two bends alone already step " +
                $"{minimum:g6}, so that is the smallest offset this radius and angle can make. Increase the " +
                "offset, reduce the radius, or steepen the bends.");
        }

        var stage = WithFlange(new EdgeFlange(
            target, intermediate + SheetMetalSpec.OutsideSetback(angle, radius, Spec.Thickness),
            angleDegrees, direction, bendRadius, StartOffset: startOffset, Width: width));
        // The OPPOSITE direction turns back, which is what leaves the far leg parallel to
        // the sheet — asserted geometrically rather than assumed (see the tests).
        var back = direction == SheetBendDirection.Up ? SheetBendDirection.Down : SheetBendDirection.Up;
        return stage.WithFlange(new EdgeFlange(
            SheetFlangeTarget.FlangeTip(stage.Flanges.Count - 1), runLength, angleDegrees, back, bendRadius));
    }

    /// <summary>
    /// A CURL: the edge rolled through <paramref name="totalAngleDegrees"/> as a CHAIN of
    /// equal bends.
    ///
    /// <para><b>What this is and is not.</b> A rolled edge past 180° is one continuous
    /// cylindrical band, which this bend model has no way to spell — its outside setback
    /// diverges at 180° and a wall measured from a virtual sharp stops meaning anything
    /// beyond it. A curl is therefore built the way a press actually makes one, in
    /// successive hits, and the result is a POLYGONAL roll of
    /// <paramref name="segments"/> facets rather than a true cylinder. That is stated
    /// rather than approximated away: the flat pattern, the bend table and the volume
    /// identity all describe exactly the part this builds.</para>
    /// </summary>
    /// <param name="totalAngleDegrees">Total turn; each of the <paramref name="segments"/>
    /// bends takes an equal share, and that share must stay under 180.</param>
    /// <param name="segments">How many hits; at least two (one hit is a plain flange).</param>
    /// <param name="legLength">Straight leg between hits; null uses one sheet thickness,
    /// which is about as tight as tooling reaches.</param>
    public SheetMetalBody WithCurl(
        SheetFlangeTarget target, double totalAngleDegrees, int segments,
        double? bendRadius = null, double? legLength = null,
        SheetBendDirection direction = SheetBendDirection.Up,
        double startOffset = 0, double? width = null)
    {
        if (segments < 2)
            throw new ArgumentOutOfRangeException(nameof(segments),
                $"A curl is a CHAIN of bends and this one asks for {segments}. One bend is a plain flange — " +
                $"use {nameof(WithFlange)}.");
        double each = totalAngleDegrees / segments;
        if (!(each > 0) || each >= 180)
            throw new ArgumentOutOfRangeException(nameof(totalAngleDegrees),
                $"{totalAngleDegrees:g6} degrees over {segments} segments is {each:g6} per bend; each must lie " +
                "strictly between 0 and 180. Add segments.");

        double radius = bendRadius ?? Spec.BendRadius;
        double leg = legLength ?? Spec.Thickness;
        double length = leg + SheetMetalSpec.OutsideSetback(each * Math.PI / 180, radius, Spec.Thickness);
        var body = WithFlange(new EdgeFlange(
            target, length, each, direction, bendRadius, StartOffset: startOffset, Width: width));
        for (int i = 1; i < segments; i++)
        {
            body = body.WithFlange(new EdgeFlange(
                SheetFlangeTarget.FlangeTip(body.Flanges.Count - 1), length, each, direction, bendRadius));
        }
        return body;
    }

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
        // A bend relief NOTCHES the blank, so an edge a site names can arrive as two or
        // three pieces of itself. A piece still names the same site — the flange's own
        // StartOffset and Width say where on it the bend goes — so a picked sub-segment
        // resolves rather than reporting an edge the body does not carry. The exact match
        // is tried first, so nothing that already resolved can be answered differently.
        foreach (var site in Sites)
        {
            if (LiesOn(site, start) && LiesOn(site, end))
                return site;
        }
        throw new ArgumentException(
            $"The edge from {start} to {end} is not one of this sheet body's flange-able edges. It carries " +
            $"{Sites.Count}: {string.Join(", ", Sites.Select(s => s.Target.ToString()))}.", nameof(edge));
    }

    /// <summary>Is a point on a site's own segment, at the weld tier? Both were built from
    /// the same frame chain, so the absolute tier is right.</summary>
    private static bool LiesOn(in SheetFlangeSite site, in Vector3d point)
    {
        var along = site.End - site.Start;
        double lengthSquared = along.LengthSquared;
        if (lengthSquared <= 0)
            return false;
        double t = (point - site.Start).Dot(along) / lengthSquared;
        return t >= -Weld && t <= 1 + Weld && (site.Start + along * t).DistanceTo(point) <= Weld;
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
        {
            solid = SheetMetalSurgery.AddEdgeFlange(
                solid, bend.Section, bend.Start, bend.End, bend.WallLength, bend.Cutouts);
        }
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

        /// <summary>The declared relief and its resolved dimensions; <see cref="Relief"/>
        /// null means none was asked for.</summary>
        public BendRelief? Relief { get; init; }
        public double ReliefWidth { get; init; }
        public double ReliefDepth { get; init; }

        /// <summary>Holes through this flange's wall, in the flange's own local
        /// coordinates — the same coordinates its flat frame and its 3D frame both
        /// place, which is what makes one declaration reach both views.</summary>
        public IReadOnlyList<Sketch> Cutouts { get; init; } = [];

        /// <summary>Is this end of the flange inset from its edge's own end? A relief is
        /// cut at exactly the ends that are — a flush end has no parent material beside
        /// it — so this one predicate settles both the notch list and the surgery's rims.</summary>
        public bool[] Inset { get; init; } = [false, false];

        /// <summary>The stretch of the parent's edge this flange occupies, RELIEFS
        /// INCLUDED — what a sibling flange has to keep clear of, since a notch that ate
        /// its neighbour's wall would leave a flange with nothing to fold from.</summary>
        public (double From, double To) Occupies =>
            OccupiedBy(StartOffset, Width, Relief, ReliefWidth, Inset);

        /// <summary>
        /// <inheritdoc cref="Occupies"/>
        /// <para>Static because the overlap check has to ask it about a flange that is
        /// still being resolved and so has no <see cref="Node"/> yet; the property above
        /// asks the same rule rather than restating it, which is what stops the two
        /// disagreeing about whether a relief counts.</para>
        /// </summary>
        public static (double From, double To) OccupiedBy(
            double start, double width, BendRelief? relief, double reliefWidth, bool[] inset) =>
            (start - (relief is not null && inset[0] ? reliefWidth : 0),
             start + width + (relief is not null && inset[1] ? reliefWidth : 0));

        public List<Node> Children { get; } = [];
    }

    /// <summary>The surgery arguments for one bend.</summary>
    internal readonly record struct BendArgs(
        SheetBendSection Section, Vector3d Start, Vector3d End, double WallLength,
        IReadOnlyList<Profile> Cutouts);

    /// <summary>
    /// Builds one outline as an exact chain of <see cref="Curve2d"/>s. Two rules and no
    /// others: a zero-length step is DROPPED (a flange flush with its edge's end, a notch
    /// mouth landing on a vertex already emitted — the two points are the same corner, and
    /// the sketch constructor would refuse the segment), and an arc's neighbours are drawn
    /// to the arc's OWN evaluated endpoints rather than to the points that were used to
    /// construct it, so the chain is continuous by construction instead of to within the
    /// ULP or two that <c>centre + r·(cos θ, sin θ)</c> costs.
    /// </summary>
    private sealed class OutlinePen(List<Curve2d> curves, Vector2d start)
    {
        private Vector2d _at = start;

        public void LineTo(in Vector2d to)
        {
            if (to.DistanceTo(_at) > Weld)
                curves.Add(new Line2d(_at, to));
            _at = to;
        }

        public void ArcTo(in Vector2d centre, double radius, double startAngle, double sweep)
        {
            var arc = new Arc2d(centre, radius, startAngle, sweep);
            LineTo(arc.PointAt(0));
            curves.Add(arc);
            _at = arc.PointAt(1);
        }
    }

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
        private Sketch? _outline;

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
            bool[] inset = [start > Weld, start + width < edgeLength - Weld];

            // Override resolution happens ONCE, in the spec's own accessors, so a flange's
            // per-bend radius and K cannot be applied one way here and another way there.
            double radius = flange.BendRadius ?? body.Spec.BendRadius;
            double angle = flange.AngleRadians;
            var (reliefWidth, reliefDepth) = ResolveRelief(
                body, flange, index, radius, start, width, edgeLength, inset, parent, a2, t2, o2);

            // Compared on OCCUPIED stretches, reliefs included: a notch that ate its
            // neighbour's wall would leave a flange with nothing to fold from, and the
            // failure would surface as a missing bend edge three stages downstream.
            var mine = Node.OccupiedBy(start, width, flange.Relief, reliefWidth, inset);
            foreach (var sibling in parent.Children)
            {
                if (sibling.Flange!.Target.EdgeIndex != flange.Target.EdgeIndex)
                    continue;
                var theirs = sibling.Occupies;
                if (mine.From < theirs.To - Weld && theirs.From < mine.To - Weld)
                    throw new ArgumentException(
                        $"Flange {index} occupies [{mine.From:g6}, {mine.To:g6}] of {flange.Target} and flange " +
                        $"{sibling.Index} occupies [{theirs.From:g6}, {theirs.To:g6}]: two bends cannot share " +
                        "the same stretch of one edge, and a bend relief counts as part of the stretch its " +
                        "flange occupies.");
            }

            double setback = body.Spec.OutsideSetbackAt(angle, flange.BendRadius);
            double wall = flange.Length - setback;
            if (!(wall > Weld))
                throw new ArgumentException(
                    $"Flange {index} is {flange.Length:g6} long, measured from the outer virtual sharp, but the " +
                    $"outside setback (R + T)*tan(angle/2) is already {setback:g6}. Lengthen the flange, reduce " +
                    "the bend radius, or reduce the angle.");
            double allowance = body.Spec.BendAllowanceAt(angle, flange.BendRadius, flange.KFactor);
            RequireCutoutsFitTheWall(flange, index, width, wall);

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
                Relief = flange.Relief,
                ReliefWidth = reliefWidth,
                ReliefDepth = reliefDepth,
                Inset = inset,
                Cutouts = flange.CutoutList,
            };
        }

        /// <summary>
        /// Every refusal a flange CUTOUT can produce, all of them before any geometry is
        /// built. A cutout is a hole through the flange's flat WALL, so what has to hold is
        /// that it lies strictly inside that wall's rectangle in the flange's own local
        /// coordinates: <c>x</c> along the bend line in <c>[0, width]</c> and <c>y</c> out
        /// from the bend's tangent line in <c>[0, wallLength]</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>A cutout crossing the bend line is refused BY NAME rather than
        /// clipped</b>, and the reason is the flat pattern rather than the solid: a hole
        /// that runs into the bend zone is a hole in a CYLINDRICAL band, and its flat shape
        /// is that band's DEVELOPMENT — a different map from the flange's rigid frame. The
        /// unfold's whole claim is that it is bookkeeping over rigid frames, so admitting
        /// one would mean the blank quietly stopped being derived the way it says it is.</para>
        /// <para><b>The overlap test is on BOUNDING BOXES and so is conservative</b>: two
        /// cutouts whose boxes meet are refused even when the shapes themselves are clear
        /// of each other. That direction is the safe one — an admitted overlap puts two
        /// crossing hole loops on one face and produces a solid that cannot be welded,
        /// which is exactly what <c>Shape.Drill</c>'s own near-tangent refusal band buys
        /// for the same reason — and a refusal names both cutouts and what to move.</para>
        /// </remarks>
        private static void RequireCutoutsFitTheWall(
            EdgeFlange flange, int index, double width, double wallLength)
        {
            var cutouts = flange.CutoutList;
            for (int c = 0; c < cutouts.Count; c++)
            {
                var cutout = cutouts[c]
                    ?? throw new ArgumentNullException(nameof(flange), $"Flange {index}'s cutout {c} is null.");
                if (cutout.Holes.Count > 0)
                    throw new NotSupportedException(
                        $"Flange {index}'s cutout {c} has holes of its own. A hole inside a hole is an ISLAND " +
                        "of material with nothing joining it to the wall, so it would be a second body rather " +
                        "than a cutout; declare the island as part of the wall instead.");

                var bounds = cutout.Bounds;
                if (bounds.Min.Y <= Weld)
                    throw new NotSupportedException(
                        $"Flange {index}'s cutout {c} reaches y = {bounds.Min.Y:g6}, i.e. into (or through) the " +
                        "BEND. A hole that crosses the bend line is a hole in a cylindrical band, whose flat " +
                        "shape is that band's development rather than the flange's own rigid frame — and the " +
                        "unfold is bookkeeping over rigid frames. Move the cutout clear of the tangent line, " +
                        "or cut it in the base sketch if it belongs to the parent.");
                if (bounds.Max.Y >= wallLength - Weld
                    || bounds.Min.X <= Weld || bounds.Max.X >= width - Weld)
                    throw new ArgumentException(
                        $"Flange {index}'s cutout {c} spans x [{bounds.Min.X:g6}, {bounds.Max.X:g6}] and y " +
                        $"[{bounds.Min.Y:g6}, {bounds.Max.Y:g6}], which does not lie strictly inside the wall's " +
                        $"[0, {width:g6}] by [0, {wallLength:g6}]. A cutout that reaches a wall edge is a change " +
                        "to the flange's OUTLINE rather than a hole through it.");

                for (int other = 0; other < c; other++)
                {
                    var theirs = cutouts[other].Bounds;
                    if (bounds.Min.X < theirs.Max.X && theirs.Min.X < bounds.Max.X
                        && bounds.Min.Y < theirs.Max.Y && theirs.Min.Y < bounds.Max.Y)
                    {
                        throw new ArgumentException(
                            $"Flange {index}'s cutouts {other} and {c} have overlapping extents. Two hole loops " +
                            "that cross put a face's boundary in a state nothing can weld, so the test is on " +
                            "bounding boxes and errs toward refusing: separate them, or draw the pair as one " +
                            "cutout.");
                    }
                }
            }
        }

        /// <summary>
        /// The flange's relief dimensions, or (0, 0) when none was asked for — and every
        /// refusal a relief can produce, all of them BEFORE any geometry is built.
        /// </summary>
        private static (double Width, double Depth) ResolveRelief(
            SheetMetalBody body, EdgeFlange flange, int index, double radius,
            double start, double width, double edgeLength, bool[] inset, Node parent,
            in Vector2d edgeStart, in Vector2d tangent, in Vector2d outward)
        {
            if (flange.Relief is not { } relief)
                return (0, 0);
            if (parent.Index >= 0)
                throw new NotSupportedException(
                    $"Flange {index} asks for a bend relief on {flange.Target}. A relief notches the parent's " +
                    "own OUTLINE, and a flange's wall is built as a plain rectangle rather than from a sketch, " +
                    "so v2 cuts reliefs only on the base flange's edges. Grow the relieved flange from the " +
                    "base, or leave the relief off and let the corner tear.");
            if (!inset[0] && !inset[1])
                throw new ArgumentException(
                    $"Flange {index} spans the whole of {flange.Target}, so there is no parent material beside " +
                    "it to relieve. Inset the flange from an end, or drop the relief.");

            double reliefWidth = relief.WidthFor(body.Spec);
            double reliefDepth = relief.DepthFor(body.Spec, radius);
            if (!(reliefWidth > 0) || !(reliefDepth > 0))
                throw new ArgumentOutOfRangeException(nameof(flange),
                    $"Flange {index}'s bend relief measures {reliefWidth:g6} by {reliefDepth:g6}; both must be " +
                    "positive.");
            if (relief.Kind == SheetReliefKind.Obround && reliefDepth < reliefWidth / 2 - Weld)
                throw new ArgumentOutOfRangeException(nameof(flange),
                    $"Flange {index}'s obround relief is {reliefDepth:g6} deep and {reliefWidth:g6} wide, so " +
                    $"its own end radius ({reliefWidth / 2:g6}) is deeper than the notch. Depth is measured to " +
                    "the deepest point, so an obround relief can never be shallower than half its width.");
            if (inset[0] && start < reliefWidth - Weld)
                throw new ArgumentException(
                    $"Flange {index}'s relief is {reliefWidth:g6} wide but the flange starts only {start:g6} " +
                    $"along {flange.Target}, so the notch would run off the end of the edge. Move the flange " +
                    "in, narrow the relief, or make that end flush.");
            if (inset[1] && start + width + reliefWidth > edgeLength + Weld)
                throw new ArgumentException(
                    $"Flange {index}'s relief is {reliefWidth:g6} wide but only " +
                    $"{edgeLength - start - width:g6} of {flange.Target} is left beyond the flange, so the " +
                    "notch would run off the end of the edge.");

            // A notch is drawn as a DETOUR in the outline, so one reaching past the far
            // side of the parent makes a self-intersecting blank — and that failure is
            // SILENT: measured on an 80x50 plate with a 200-deep relief, the signed area
            // still reads base-minus-notches (2800) and the extruded solid still
            // validates. So every point of the notch below the surface has to lie strictly
            // inside the parent's own region. Sound for the failure it exists to catch, and
            // it claims no more: a notch that passes clean through a HOLE and comes out in
            // material on the far side has its own corners inside and is not caught.
            var region = new SketchRegion(body.BaseSketch);
            double side = relief.Kind == SheetReliefKind.Obround
                ? reliefDepth - reliefWidth / 2
                : reliefDepth;
            var (a2, t2, o2) = (edgeStart, tangent, outward);
            for (int k = 0; k < 2; k++)
            {
                if (!inset[k])
                    continue;
                double from = k == 0 ? start - reliefWidth : start + width;
                Vector2d At(double s, double depth) => a2 + t2 * s - o2 * depth;
                foreach (var point in (ReadOnlySpan<Vector2d>)
                    [At(from, side), At(from + reliefWidth / 2, reliefDepth), At(from + reliefWidth, side)])
                {
                    if (region.SignedDistance(point) >= -Weld)
                        throw new ArgumentException(
                            $"Flange {index}'s relief reaches {reliefDepth:g6} into the parent at {point}, " +
                            "which is outside the base sketch. A relief notches the blank, so a notch that " +
                            "runs out of the parent would leave a self-intersecting outline rather than a " +
                            "cut; make it shallower.");
                }
            }
            return (reliefWidth, reliefDepth);
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

                bool up = node.Flange!.Direction == SheetBendDirection.Up;
                var frame = new SheetFrame(
                    up ? endSection.InsideTangentPoint : endSection.OutsideTangentPoint,
                    -parent.Direction(node.EdgeTangent),
                    endSection.FlangeDirection);

                // A cutout is declared in the flange's own local coordinates, and the frame
                // just computed is the 3D placement of exactly those coordinates on the
                // flange's top face — so the folded body's holes and the blank's holes come
                // from one declaration through two placements of one frame, which is what
                // makes them incapable of disagreeing.
                bends.Add(new BendArgs(
                    startSection, startSection.BendLinePoint, endSection.BendLinePoint,
                    node.WallLength * scale, PlaceCutouts(node, frame, scale)));

                frames[node.Index] = frame;
            }
            return (frames, bends);
        }

        /// <summary>
        /// One flange's cutouts placed in 3D on its own top face. The frame's X and Y are
        /// already unit and orthogonal, so the placement is that frame's matrix times the
        /// uniform scale — no re-derivation and nothing normalized twice.
        /// </summary>
        private static IReadOnlyList<Profile> PlaceCutouts(Node node, in SheetFrame frame, double scale)
        {
            if (node.Cutouts.Count == 0)
                return [];
            var placement = Frame3d.FromOrthonormal(frame.Origin, frame.X, frame.Y).ToMatrix()
                * Matrix4d.CreateScale(scale);
            var placed = new List<Profile>(node.Cutouts.Count);
            foreach (var cutout in node.Cutouts)
            {
                var (outer, _) = cutout.ToProfiles();   // holes are refused at Resolve
                placed.Add(new Profile(
                    [.. outer.Segments.Select(s => (Curve3d)new TransformedCurve(s, placement))]));
            }
            return placed;
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
            var bends = new List<FlatBendLine>();
            var spliced = new List<Curve2d>();

            for (int i = 0; i < _curves.Count; i++)
            {
                var here = ChildrenOn(i);
                if (here.Count == 0)
                {
                    spliced.Add(_curves[i]);
                    continue;
                }
                var line = (Line2d)_curves[i];
                var pen = new OutlinePen(spliced, _base.Flat.ToFlat(line.Start));
                SpliceEdge(_base, here, pen, bends);
                pen.LineTo(_base.Flat.ToFlat(line.End));
            }

            return new FlatPattern(WithHoles(spliced), bends, _body.Spec.Thickness);
        }

        /// <summary>
        /// The base flange's outline with every relief notched in — the region BOTH the
        /// folded sheet's base extrusion and the flat pattern start from, so a relief is
        /// declared once and appears in both.
        /// </summary>
        public Sketch BaseOutline => _outline ??= BuildBaseOutline();

        private Sketch BuildBaseOutline()
        {
            // Nothing to notch: the declaration IS the outline, by reference. That is what
            // keeps a design with no reliefs bit-identical to what it always built.
            if (!_nodes.Any(n => n.Relief is not null))
                return _body.BaseSketch;

            var curves = new List<Curve2d>();
            for (int i = 0; i < _curves.Count; i++)
            {
                var here = ChildrenOn(i).Where(c => c.Relief is not null).ToList();
                if (here.Count == 0)
                {
                    curves.Add(_curves[i]);
                    continue;
                }
                var line = (Line2d)_curves[i];
                var pen = new OutlinePen(curves, line.Start);
                foreach (var child in here)
                {
                    // The flange itself contributes NOTHING to this outline — the bend
                    // grows outboard of the edge, and this is the region inboard of it.
                    // What the two LineTo calls do is put a vertex at each end of the
                    // flange's span, which is what makes the wall between the notches
                    // exactly the bend line (and so a flush flange to the surgery below).
                    EmitRelief(_base.Flat, child, 0, pen);
                    pen.LineTo(At(_base.Flat, child, child.StartOffset));
                    pen.LineTo(At(_base.Flat, child, child.StartOffset + child.Width));
                    EmitRelief(_base.Flat, child, 1, pen);
                }
                pen.LineTo(line.End);
            }
            return WithBaseHoles(curves);
        }

        /// <summary>The base sketch's holes carry through untouched: the blank's
        /// coordinates ARE the base sketch's, so a drawn bore keeps its position.</summary>
        private Sketch WithBaseHoles(IReadOnlyList<Curve2d> curves)
        {
            var outline = Sketch.FromCurves(curves);
            foreach (var hole in _body.BaseSketch.Holes)
                outline = outline.WithHole(hole);
            return outline;
        }

        /// <summary>
        /// The blank's holes: the base sketch's, plus every flange's own cutouts placed by
        /// that flange's rigid FLAT frame. The folded body punches the same declarations
        /// through the same frame in 3D, which is why a cutout cannot land in two places —
        /// the notch rule for reliefs, applied to a hole.
        /// </summary>
        private Sketch WithHoles(IReadOnlyList<Curve2d> curves)
        {
            var outline = WithBaseHoles(curves);
            foreach (var node in _nodes)
            {
                foreach (var cutout in node.Cutouts)
                    outline = outline.WithHole(cutout.Placed(node.Flat.Origin, node.Flat.X));
            }
            return outline;
        }

        /// <summary>The base flange's children on one of its sketch segments, in order
        /// along the edge — the order both outlines have to emit them in.</summary>
        private List<Node> ChildrenOn(int edgeIndex) =>
            [.. _base.Children.Where(c => c.Flange!.Target.EdgeIndex == edgeIndex)
                .OrderBy(c => c.StartOffset)];

        /// <summary>Detours around every flange on one edge of <paramref name="owner"/>'s
        /// outline, appending FLAT geometry strictly between the edge's own ends.</summary>
        private static void SpliceEdge(
            Node owner, IReadOnlyList<Node> children, OutlinePen pen, List<FlatBendLine> bends)
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
                // the tangent points appear only in the bend record below. A relief, where
                // one is declared, is the notch on the PARENT's side of that edge, so it
                // brackets the detour rather than joining it.
                EmitRelief(owner.Flat, child, 0, pen);
                pen.LineTo(owner.Flat.ToFlat(p0));
                AppendFlangeOutline(child, pen, bends);
                pen.LineTo(owner.Flat.ToFlat(p1));
                EmitRelief(owner.Flat, child, 1, pen);

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
        private static void AppendFlangeOutline(Node node, OutlinePen pen, List<FlatBendLine> bends)
        {
            pen.LineTo(node.Flat.ToFlat(new Vector2d(node.Width, node.WallLength)));
            SpliceEdge(node, [.. node.Children.OrderBy(c => c.StartOffset)], pen, bends);
            pen.LineTo(node.Flat.ToFlat(new Vector2d(0, node.WallLength)));
        }

        /// <summary>A point at parameter <paramref name="s"/> along a child's target edge,
        /// in its owner's flat coordinates.</summary>
        private static Vector2d At(in FlatFrame frame, Node child, double s) =>
            frame.ToFlat(child.EdgeStart + child.EdgeTangent * s);

        /// <summary>
        /// Emits the relief notch beside ONE end of a flange, and nothing at all where that
        /// end is flush or no relief was declared. The notch sits immediately OUTSIDE the
        /// flange's span and reaches INBOARD from the bend line, so it takes material from
        /// the parent beside the bend and never from the bend itself — which is why a
        /// relief cannot move the folded-versus-flat volume discrepancy.
        /// </summary>
        private static void EmitRelief(in FlatFrame frame, Node child, int end, OutlinePen pen)
        {
            if (child.Relief is not { } relief || !child.Inset[end])
                return;
            double from = end == 0 ? child.StartOffset - child.ReliefWidth : child.StartOffset + child.Width;
            double to = from + child.ReliefWidth;
            var inward = -frame.Direction(child.EdgeOutward);

            pen.LineTo(At(frame, child, from));
            if (relief.Kind == SheetReliefKind.Rectangular)
            {
                pen.LineTo(At(frame, child, from) + inward * child.ReliefDepth);
                pen.LineTo(At(frame, child, to) + inward * child.ReliefDepth);
            }
            else
            {
                // The dome's radius is half the notch's width, so the straight sides run in
                // by the REST of the depth and the arc carries the last of it. Its sweep is
                // NEGATIVE: an inward notch is a concavity of a counter-clockwise outline,
                // so its rounded bottom turns the other way from the outline carrying it.
                double radius = child.ReliefWidth / 2;
                var centre = At(frame, child, from + radius) + inward * (child.ReliefDepth - radius);
                var back = -frame.Direction(child.EdgeTangent);
                pen.ArcTo(centre, radius, Math.Atan2(back.Y, back.X), -Math.PI);
            }
            pen.LineTo(At(frame, child, to));
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
