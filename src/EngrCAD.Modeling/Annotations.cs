using System.Globalization;
using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

// 3D annotations (PMI — product and manufacturing information): dimensions, notes,
// and datum labels attached to model geometry in 3D space, so the model carries its
// own manufacturing information (model-based definition) instead of 2D drawings.
//
// Two kinds of geometry reference:
// - plain points (part-local Vector3d anchors) for notes, datums, and point-to-point
//   dimensions — simple, no lowering required;
// - semantic B-Rep selectors (Func<BrepSolid, BrepFace/BrepEdge>, the same vocabulary
//   the rim features use) for dimensions that MEASURE the model: the selector re-runs
//   against the current body each time the annotation is resolved, so dimensions
//   survive parameter edits and feature-history regeneration (the topological-naming
//   story — semantic queries instead of persisted indices).
//
// This file is UI-free: annotations are data + measurement. Rendering (dimension
// lines, arrowheads, billboarded text) lives in EngrCAD.Viewer.

/// <summary>What a <see cref="ResolvedAnnotation"/> represents, so renderers know
/// which graphic (dimension lines, leader, datum box) to draw.</summary>
public enum AnnotationKind
{
    /// <summary>Distance between two anchors: extension lines + dimension line + text.</summary>
    LinearDimension,

    /// <summary>Radius/diameter of a circular edge: leader from the circle + text.</summary>
    RadialDimension,

    /// <summary>Free text with a leader line to its anchor.</summary>
    LeaderNote,

    /// <summary>A boxed datum letter with a leader to its anchor.</summary>
    DatumLabel,
}

/// <summary>
/// An annotation resolved against concrete geometry — everything a renderer needs, in
/// <b>part-local</b> coordinates (viewers pose it by the instance transform, so
/// assembly instances show their part's annotations in place). For dimensions,
/// <see cref="Value"/> is the measured quantity and <see cref="Text"/> its formatted
/// display form (or the annotation's label override).
/// </summary>
/// <param name="Source">The annotation this was resolved from.</param>
/// <param name="Kind">Which graphic to draw.</param>
/// <param name="AnchorA">First anchor: a measurement point, the on-circle point of a
/// radial dimension, or the leader target of a note/datum.</param>
/// <param name="AnchorB">Second anchor: the other measurement point, the circle
/// center for radial dimensions; equals <paramref name="AnchorA"/> for notes/datums.</param>
/// <param name="Offset">The annotation's placement vector (part-local): where the
/// dimension line / text sits relative to the anchors. Zero lets the renderer choose
/// a screen-space default.</param>
/// <param name="Text">Display text (label override or the formatted measurement).</param>
/// <param name="Value">The measured value (distance, radius or diameter); 0 for notes.</param>
public sealed record ResolvedAnnotation(
    Annotation Source, AnnotationKind Kind, Vector3d AnchorA, Vector3d AnchorB,
    Vector3d Offset, string Text, double Value);

/// <summary>
/// Base class for 3D annotations attached to a <see cref="Part"/> (via
/// <see cref="Part.Annotate"/>). Annotations are pure data + measurement;
/// <see cref="Resolve(Func{BrepSolid})"/> produces the render-ready
/// <see cref="ResolvedAnnotation"/>, re-running any B-Rep selectors against the
/// current geometry so measured values stay correct when parameters change.
/// </summary>
public abstract class Annotation
{
    /// <summary>Optional display-text override; when null, dimensions show their
    /// formatted measured value and notes show their own text.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// Placement vector in part-local space: where the dimension line (or note text)
    /// sits relative to the anchors — the classic "pull the dimension off the part"
    /// offset. Zero (the default) lets the renderer pick a screen-space default.
    /// </summary>
    public Vector3d Offset { get; set; }

    /// <summary>
    /// Resolves this annotation against geometry. <paramref name="solid"/> supplies
    /// the part's B-Rep lazily — it is only invoked when the annotation uses
    /// selectors, so point-anchored annotations never force a lowering.
    /// </summary>
    public abstract ResolvedAnnotation Resolve(Func<BrepSolid> solid);

    /// <summary>Resolves against an already-lowered solid (null is fine for
    /// point-anchored annotations; selector-based ones then throw).</summary>
    public ResolvedAnnotation Resolve(BrepSolid? solid = null) =>
        Resolve(() => solid ?? throw new InvalidOperationException(
            $"{GetType().Name} uses B-Rep selectors and needs a solid to measure."));

    /// <summary>Dimension-value formatting: invariant culture, up to three decimals,
    /// trailing zeros trimmed ("40", "5.5", "33.333").</summary>
    internal static string Format(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// A linear dimension — the distance between two anchors, drawn with extension lines,
/// a dimension line with arrowheads, and centered text. Two flavors:
/// point-to-point (fixed part-local points, used by the viewer's measure tool) and
/// <see cref="BetweenFaces"/> (semantic face selectors that re-measure the actual
/// face-to-face distance every time the annotation is resolved).
/// </summary>
public sealed class LinearDimension : Annotation
{
    // Angular tolerance for the parallel-faces check: the selector-sugar tier
    // (matches BrepQueries.PlanarFacesWithNormal's default — selection semantics,
    // not weld geometry).
    private const double AngularTolerance = 1e-6;

    private readonly Vector3d _a, _b;
    private readonly Func<BrepSolid, BrepFace>? _faceA;
    private readonly Func<BrepSolid, BrepFace>? _faceB;

    /// <summary>Point-to-point dimension between two fixed part-local points; the
    /// measured value is their distance.</summary>
    public LinearDimension(Vector3d a, Vector3d b)
    {
        _a = a;
        _b = b;
    }

    private LinearDimension(Func<BrepSolid, BrepFace> faceA, Func<BrepSolid, BrepFace> faceB)
    {
        _faceA = faceA;
        _faceB = faceB;
    }

    /// <summary>
    /// Auto-measuring dimension between two <b>parallel planar</b> faces selected by
    /// semantic queries (<c>BrepQueries</c> vocabulary, e.g.
    /// <c>s =&gt; s.PlanarFacesWithNormal(Vector3d.UnitZ).First()</c>). The selectors
    /// re-run per resolution, so the dimension tracks the model through parameter
    /// edits and regeneration. Anchors are the first face's loop centroid and its
    /// projection onto the second face's plane; the value is the plane-to-plane
    /// distance. Non-planar or non-parallel faces fail loudly.
    /// </summary>
    public static LinearDimension BetweenFaces(
        Func<BrepSolid, BrepFace> faceA, Func<BrepSolid, BrepFace> faceB) => new(faceA, faceB);

    /// <inheritdoc />
    public override ResolvedAnnotation Resolve(Func<BrepSolid> solid)
    {
        if (_faceA is null || _faceB is null)
        {
            double distance = _a.DistanceTo(_b);
            return new ResolvedAnnotation(this, AnnotationKind.LinearDimension,
                _a, _b, Offset, Label ?? Format(distance), distance);
        }

        var body = solid();
        var faceA = _faceA(body) ?? throw new InvalidOperationException(
            "LinearDimension: the first face selector returned null.");
        var faceB = _faceB(body) ?? throw new InvalidOperationException(
            "LinearDimension: the second face selector returned null.");
        if (!faceA.IsPlanar(out _, out var normalA))
            throw new InvalidOperationException(
                "LinearDimension: the first selected face is not planar.");
        if (!faceB.IsPlanar(out var originB, out var normalB))
            throw new InvalidOperationException(
                "LinearDimension: the second selected face is not planar.");
        if (Math.Abs(normalA.Dot(normalB)) < 1 - AngularTolerance)
            throw new InvalidOperationException(
                "LinearDimension: the selected faces are not parallel (cannot measure a face-to-face distance).");

        // Anchor A: the face's stable in-plane centroid (BrepQueries.Frame origin).
        var anchorA = faceA.Frame()?.Origin ?? throw new InvalidOperationException(
            "LinearDimension: could not derive a frame for the first face.");
        // Anchor B: anchor A projected onto face B's plane along its normal — the
        // two anchors then differ exactly by the measured distance.
        double along = (originB - anchorA).Dot(normalB);
        var anchorB = anchorA + normalB * along;
        double value = Math.Abs(along);
        return new ResolvedAnnotation(this, AnnotationKind.LinearDimension,
            anchorA, anchorB, Offset, Label ?? Format(value), value);
    }
}

/// <summary>
/// A radial (or diameter) dimension on a circular B-Rep edge — a bore rim, a fillet
/// arc, a cylinder cap circle. The edge selector re-runs per resolution and the
/// radius is read from the actual edge, so the dimension tracks parameter edits.
/// Text is "R5" for radii and "&#x2300;10" (diameter sign) for diameters.
/// </summary>
public sealed class RadialDimension : Annotation
{
    private readonly Func<BrepSolid, BrepEdge> _edge;
    private readonly bool _diameter;

    private RadialDimension(Func<BrepSolid, BrepEdge> edge, bool diameter)
    {
        _edge = edge;
        _diameter = diameter;
    }

    /// <summary>
    /// Dimension on a circular edge selected by a semantic query, e.g.
    /// <c>s =&gt; s.Faces.SelectMany(f =&gt; f.Edges()).First(e =&gt; e.IsCircular(out _, out _, out _))</c>.
    /// With <paramref name="diameter"/> the value and text are the diameter
    /// ("&#x2300;10"), otherwise the radius ("R5"). A non-circular selection fails loudly.
    /// </summary>
    public static RadialDimension OnEdge(Func<BrepSolid, BrepEdge> edge, bool diameter = false) =>
        new(edge, diameter);

    /// <inheritdoc />
    public override ResolvedAnnotation Resolve(Func<BrepSolid> solid)
    {
        var edge = _edge(solid()) ?? throw new InvalidOperationException(
            "RadialDimension: the edge selector returned null.");
        if (!edge.IsCircular(out var center, out var normal, out double radius))
            throw new InvalidOperationException(
                "RadialDimension: the selected edge is not circular.");

        // The on-circle anchor: along the Offset direction projected into the circle
        // plane when one is set (the author's leader direction), else the circle
        // plane's own arbitrary-perpendicular convention.
        var radial = Offset - normal * Offset.Dot(normal);
        if (!radial.TryNormalize(Tolerance.Default, out var direction))
            direction = normal.ArbitraryPerpendicular(Tolerance.Default);
        var anchor = center + direction * radius;

        double value = _diameter ? 2 * radius : radius;
        // \u2300 is the diameter sign; source files stay pure ASCII (escapes only).
        string text = Label ?? (_diameter ? "\u2300" + Format(value) : "R" + Format(value));
        return new ResolvedAnnotation(this, AnnotationKind.RadialDimension,
            anchor, center, Offset, text, value);
    }
}

/// <summary>A free-text note with a leader line pointing at a part-local anchor
/// (hole callouts, material notes, assembly instructions).</summary>
public sealed class LeaderNote : Annotation
{
    private readonly Vector3d _anchor;
    private readonly string _text;

    public LeaderNote(Vector3d anchor, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Note text must be non-empty.", nameof(text));
        _anchor = anchor;
        _text = text;
    }

    /// <inheritdoc />
    public override ResolvedAnnotation Resolve(Func<BrepSolid> solid) =>
        new(this, AnnotationKind.LeaderNote, _anchor, _anchor, Offset, Label ?? _text, 0);
}

/// <summary>A datum label — a boxed reference letter ("A", "B") with a leader to its
/// part-local anchor, the GD&amp;T datum-feature symbol (v1: box + leader).</summary>
public sealed class DatumLabel : Annotation
{
    private readonly Vector3d _anchor;
    private readonly string _letter;

    public DatumLabel(Vector3d anchor, string letter)
    {
        if (string.IsNullOrWhiteSpace(letter))
            throw new ArgumentException("Datum letter must be non-empty.", nameof(letter));
        _anchor = anchor;
        _letter = letter;
    }

    /// <inheritdoc />
    public override ResolvedAnnotation Resolve(Func<BrepSolid> solid) =>
        new(this, AnnotationKind.DatumLabel, _anchor, _anchor, Offset, Label ?? _letter, 0);
}
