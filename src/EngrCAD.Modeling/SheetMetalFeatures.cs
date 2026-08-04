using System.Text.Json.Nodes;
using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

// Sheet metal in the parametric feature system. A sheet part regenerates like any other:
// the material spec and every flange dimension are [Param] values, and an edge flange
// names its bend line with an EdgeSetRef — the topological-naming story, re-resolved
// against the regenerated body, so a thickness or length edit re-finds the edge instead
// of remembering an index.

/// <summary>
/// The base flange: a sketch and a <see cref="SheetMetalSpec"/>, which is a flat sheet
/// solid — an extrusion, exactly. It is the ROOT of a sheet part: every later
/// <see cref="EdgeFlangeFeature"/> appends to the <see cref="SheetMetalBody"/> this
/// produced, which is what keeps the flat pattern derivable from the same declaration as
/// the folded solid.
/// </summary>
public sealed class BaseFlangeFeature(Sketch sketch) : Feature
{
    private readonly PlaneRef _plane = PlaneRef.WorldXY;

    [Param(Min = 1e-9, Units = "mm", Description = "Sheet thickness")]
    public double Thickness { get; init; } = 1.5;

    [Param(Min = 1e-9, Units = "mm", Description = "Default inside bend radius")]
    public double BendRadius { get; init; } = 1.5;

    [Param(Min = 1e-6, Max = 1 - 1e-6, Description = "Default K-factor (neutral-axis position)")]
    public double KFactor { get; init; } = SheetMaterials.MildSteel;

    /// <summary>Where the blank sits; a <see cref="SketchPlane"/> converts implicitly.</summary>
    [Param(Description = "Sketch placement")]
    public PlaneRef Plane
    {
        get => _plane;
        init => _plane = value ?? PlaneRef.WorldXY;
    }

    public override Shape Apply(FeatureContext context) =>
        SheetMetalBody.Base(
            sketch,
            new SheetMetalSpec(Thickness, BendRadius, KFactor),
            Plane.Resolve(context, nameof(Plane))).Solid;

    /// <summary>The blank outline, exactly, through the public curve vocabulary — what
    /// makes this feature reconstructible from <c>FeatureHistory.SaveHistory</c>.</summary>
    protected internal override JsonNode? SaveInputs() =>
        new JsonObject { ["sketch"] = InputJson.SaveSketch(sketch) };
}

/// <summary>
/// One edge flange on a sheet part. The bend line is named with an
/// <see cref="EdgeSetRef"/> resolved against the regenerated body, then mapped back into
/// the flange tree by <see cref="SheetMetalBody.SiteFor"/> — so "the flange on THAT edge"
/// survives a change to any dimension upstream of it, which a persisted index would not.
///
/// <para>Refuses by name when the body is not a sheet part: a flange is a declaration in a
/// tree, not a surgery on arbitrary geometry, because the flat pattern has to be derivable
/// from the same numbers.</para>
///
/// <para><b>How an OPTIONAL parameter is spelt, and why the three overrides here do not
/// agree.</b> <see cref="EdgeFlange"/> states each override as <c>double?</c> — null means
/// "take the body's". <see cref="Width"/> and <see cref="BendRadius"/> say the same thing
/// the same way; <see cref="KFactor"/> keeps a sentinel 0. The deciding fact is not the
/// serializer (a nullable <c>[Param]</c> round-trips through the JSON seam) but the
/// EDITOR the parameter gets: <c>ParamEditors.KindFor</c> offers a SLIDER exactly when
/// <c>[Param(Min=, Max=)]</c> is finite at both ends, and a slider is a total function
/// onto its range with no way to say "unset" — so an optional parameter behind one could
/// be moved off "inherit" and never back. K-factor is the one with a finite range (0, 1),
/// its slider's minimum IS 0, and 0 is refused as a K-factor anyway
/// (<c>SheetMetalSpec</c> requires it strictly inside (0, 1)) — so the sentinel costs no
/// legal value and keeps "inherit" one drag away. Width and bend radius are unbounded
/// above, get a text box, and a text box shows an unset value as empty and takes
/// <c>null</c> back. <b>The rule: a parameter whose editor can express absence takes the
/// nullable type; one whose editor cannot keeps a sentinel that is outside its legal
/// range.</b></para>
/// </summary>
public sealed class EdgeFlangeFeature : Feature
{
    private readonly EdgeSetRef _edge = EdgeSetRef.Convex;

    [Param(Min = 1e-9, Units = "mm", Description = "Flange length from the outer virtual sharp")]
    public double Length { get; init; } = 20;

    [Param(Min = 1e-6, Max = 180 - 1e-6, Units = "deg", Description = "Bend angle")]
    public double AngleDegrees { get; init; } = 90;

    [Param(Description = "Which way the flange folds relative to the face its edge is on")]
    public SheetBendDirection Direction { get; init; } = SheetBendDirection.Up;

    [Param(Min = 0, Units = "mm", Description = "Inset from the edge's start; 0 spans the whole edge")]
    public double StartOffset { get; init; }

    /// <summary>Span along the edge; <b>null runs to its far end</b> — the same "not
    /// stated" <see cref="EdgeFlange.Width"/> carries, rather than a second spelling of
    /// it. See the class remarks for why <see cref="KFactor"/> is the one that is not
    /// nullable.</summary>
    [Param(Min = 1e-9, Units = "mm", Description = "Span along the edge; empty runs to its far end")]
    public double? Width { get; init; }

    /// <summary>Inside bend radius override; <b>null uses the sheet's own</b>.</summary>
    [Param(Min = 1e-9, Units = "mm", Description = "Inside bend radius override; empty uses the sheet's own")]
    public double? BendRadius { get; init; }

    /// <summary>
    /// K-factor override; <b>0 uses the sheet's own</b>. Deliberately NOT nullable, unlike
    /// its two neighbours — see the class remarks.
    /// </summary>
    [Param(Min = 0, Max = 1 - 1e-6, Description = "K-factor override; 0 uses the sheet's own")]
    public double KFactor { get; init; }

    /// <summary>The <see cref="BendRelief"/> notched beside each INSET end of this flange;
    /// <b>null cuts none</b>, which is the "let it tear" choice. Nullable rather than a
    /// second enum carrying its own <c>None</c>: a dropdown now offers a "(none)" row for
    /// a nullable enum (<c>ParamEditors.EnumChoices</c>), so the editor CAN express absence
    /// and the rule the class remarks state applies — one spelling, and nothing to drift
    /// from <see cref="SheetReliefKind"/>.</summary>
    [Param(Description = "Bend relief cut beside each inset end; empty cuts none")]
    public SheetReliefKind? Relief { get; init; }

    /// <summary>Relief width along the bend line; <b>null uses one sheet thickness</b>.</summary>
    [Param(Min = 1e-9, Units = "mm", Description = "Relief width; empty uses one sheet thickness")]
    public double? ReliefWidth { get; init; }

    /// <summary>Relief depth into the parent; <b>null uses R + T</b>.</summary>
    [Param(Min = 1e-9, Units = "mm", Description = "Relief depth; empty uses the bend radius plus thickness")]
    public double? ReliefDepth { get; init; }

    /// <summary>
    /// The bend line. Any <see cref="EdgeSetRef"/> that resolves to exactly one straight
    /// edge of the sheet will do; the default is deliberately unhelpful, so a design says
    /// which edge it means.
    /// </summary>
    [Param(Description = "The edge to fold on")]
    public EdgeSetRef Edge
    {
        get => _edge;
        init => _edge = value ?? EdgeSetRef.Convex;
    }

    public override Shape Apply(FeatureContext context)
    {
        var body = SheetMetalFeatures.BodyOf(context.Body, nameof(EdgeFlangeFeature));
        var edges = Edge.Resolve(context, nameof(Edge));
        if (edges.Count != 1)
            throw new GeometryInputException(
                $"{nameof(Edge)}: a flange folds on exactly one bend line, but this query matched " +
                $"{edges.Count} edges. Narrow it — the sheet body offers " +
                $"{string.Join(", ", body.Sites.Select(s => s.Target.ToString()))}.");

        var site = body.SiteFor(edges[0]);
        return body.WithFlange(new EdgeFlange(
            site.Target,
            Length,
            AngleDegrees,
            Direction,
            BendRadius,
            KFactor > 0 ? KFactor : null,
            StartOffset,
            Width,
            DeclaredRelief())).Solid;
    }

    /// <summary>This feature's relief as the geometry vocabulary spells it: null when none
    /// was asked for, otherwise the declared kind with its two optional dimensions. There
    /// is no enum mapping left to get wrong — the parameter carries
    /// <see cref="SheetReliefKind"/> itself.</summary>
    public BendRelief? DeclaredRelief() =>
        Relief is { } kind ? new BendRelief(kind, ReliefWidth, ReliefDepth) : null;
}

/// <summary>Shared helpers for the sheet-metal features.</summary>
public static class SheetMetalFeatures
{
    /// <summary>
    /// The <see cref="SheetMetalBody"/> a shape carries, or a loud refusal. Only a shape
    /// produced by <see cref="SheetMetalBody.Solid"/> qualifies: a sheet flange is an entry
    /// in a flange TREE, and appending to arbitrary geometry would produce a folded solid
    /// whose flat pattern could not be derived.
    /// </summary>
    public static SheetMetalBody BodyOf(Shape? body, string feature) =>
        body switch
        {
            null => throw new InvalidOperationException(
                $"{feature} needs a sheet body; start the history with a {nameof(BaseFlangeFeature)}."),
            SheetMetalShape sheet => sheet.Body,
            // A placed sheet is still a sheet — say so, rather than sending the caller off
            // to rebuild a body they already have.
            TransformShape => throw new NotSupportedException(
                $"{feature} applies to a sheet-metal body, and this one has been PLACED (moved, rotated or " +
                "scaled) since it was built. A flange is declared in the sheet's own coordinates, so add every " +
                "flange first and place the finished part afterwards."),
            _ => throw new NotSupportedException(
                $"{feature} applies to a sheet-metal body, but the current body is {body.Describe()}. A flange " +
                "is a declaration in a flange tree (that is what makes the flat pattern derivable), so it " +
                "cannot be grown onto arbitrary geometry. Build the sheet with a " +
                $"{nameof(BaseFlangeFeature)} and add flanges to it; cut holes and other features AFTER the " +
                "folds, on the resulting solid."),
        };

    /// <summary>The flat pattern of a sheet-metal shape, or null when the shape is not
    /// one — the polite spelling for a viewer or exporter that wants to offer a flat
    /// pattern only where there is one.</summary>
    public static FlatPattern? TryUnfold(Shape? body) =>
        body is SheetMetalShape sheet ? sheet.Body.Unfold() : null;

    /// <summary>The flat pattern of a PART, or null when its geometry is not a sheet body —
    /// what a viewer button and the <c>--flat</c> CLI route ask, so neither has to know how
    /// a part carries its shape. Deliberately reads <see cref="Part.Geometry"/> rather than
    /// meshing or lowering anything: an unfold is bookkeeping over the flange tree, so
    /// offering a blank must never cost a tessellation.</summary>
    public static FlatPattern? TryUnfold(Part? part) =>
        part is not null ? TryUnfold(part.Geometry as Shape) : null;

    /// <summary>An <see cref="EdgeSetRef"/> naming one sheet edge by the two endpoints it
    /// runs between — the escape hatch for a design that knows exactly which bend line it
    /// means and would rather not compose a query. Not serializable (it is a lambda), so a
    /// saved history reports it and skips it, as every opaque reference does.</summary>
    public static EdgeSetRef EdgeBetween(Vector3d start, Vector3d end) =>
        EdgeSetRef.From($"edge {start} to {end}", solid => solid.Edges.Where(edge =>
            edge.IsLinear(out var a, out var b)
            && SheetMetalBody.JoinsSamePoints(a, b, start, end)));
}
