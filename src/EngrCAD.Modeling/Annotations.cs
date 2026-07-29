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

    /// <summary>Angle at a vertex between two rays: extension rays + arc + degree text.
    /// The vertex rides in <see cref="ResolvedAnnotation.AnchorC"/>.</summary>
    AngularDimension,
}

/// <summary>
/// Tolerance text for a dimension, appended to the formatted measured value
/// ("40 &#xB1;0.1", "40 +0.2/-0.1"). Pure text sugar: the model is not analyzed —
/// this is what the drawing says, exactly as on paper. A <see cref="Annotation.Label"/>
/// override wins over it (the author already controls the whole text there).
/// </summary>
public sealed record ToleranceSpec
{
    private readonly double _plus;
    private readonly double _minus;

    private ToleranceSpec(double plus, double minus)
    {
        _plus = plus;
        _minus = minus;
    }

    /// <summary>&#xB1;t (equal both ways).</summary>
    public static ToleranceSpec Symmetric(double t) =>
        t > 0 ? new ToleranceSpec(t, t)
              : throw new ArgumentOutOfRangeException(nameof(t), "Tolerance must be positive.");

    /// <summary>Asymmetric limits: +<paramref name="plus"/> / -<paramref name="minus"/>
    /// (both given as magnitudes; either may be zero, not both).</summary>
    public static ToleranceSpec Limits(double plus, double minus)
    {
        if (plus < 0 || minus < 0)
            throw new ArgumentOutOfRangeException(nameof(plus), "Limits are magnitudes (non-negative).");
        if (plus == 0 && minus == 0)   // exact-zero semantic test: "no tolerance" is null, not 0/0
            throw new ArgumentException("A 0/0 tolerance is no tolerance — omit the spec instead.");
        return new ToleranceSpec(plus, minus);
    }

    /// <summary>The upper limit's magnitude (the serialized form's first number).</summary>
    internal double Plus => _plus;

    /// <summary>The lower limit's magnitude.</summary>
    internal double Minus => _minus;

    /// <summary>The text appended to a dimension value (leading space included).
    /// &#xB1; is the plus-minus sign; source files stay pure ASCII (escapes only).
    /// The equality is exact on purpose: <see cref="Symmetric"/> stores one value
    /// twice, so bit-equality IS "was built symmetric".</summary>
    public string Suffix => _plus == _minus
        ? " \u00B1" + Annotation.Format(_plus)
        : $" +{Annotation.Format(_plus)}/-{Annotation.Format(_minus)}";
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
    Vector3d Offset, string Text, double Value)
{
    /// <summary>Third anchor, used only by <see cref="AnnotationKind.AngularDimension"/>
    /// (the vertex the two rays meet at); zero otherwise. Non-positional so every
    /// existing two-anchor construction is unchanged.</summary>
    public Vector3d AnchorC { get; init; }
}

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

    /// <summary>Optional tolerance text appended to a dimension's formatted value
    /// ("40 &#xB1;0.1"); ignored when <see cref="Label"/> overrides the text, and by
    /// notes/datums (they have no measured value to tolerance).</summary>
    public ToleranceSpec? Tolerance { get; init; }

    /// <summary>A measured value's display text: the label override when set, else the
    /// formatted value plus any <see cref="Tolerance"/> suffix.</summary>
    private protected string DimensionText(double value) =>
        Label ?? Format(value) + (Tolerance?.Suffix ?? "");

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

    /// <summary>
    /// This annotation's IDENTITY as JSON — its kind and the anchors or text it was built
    /// from — or null when it has no serialized form. The mirror of
    /// <see cref="Feature.SaveInputs"/>, and it fails the same honest way: an annotation
    /// that measures through a <b>selector lambda</b> (<see cref="LinearDimension.BetweenFaces"/>,
    /// <see cref="RadialDimension.OnEdge"/>) cannot be rebuilt from data, so it returns
    /// null and <see cref="Document.Save"/> writes an opaque marker that
    /// <see cref="Document.Load"/> reports as a warning.
    /// <para>Shared placement — <see cref="Label"/>, <see cref="Offset"/>,
    /// <see cref="Tolerance"/> — is NOT written here: the document format owns it, so a
    /// subclass only ever describes what makes it itself.</para>
    /// </summary>
    protected internal virtual System.Text.Json.Nodes.JsonNode? SaveJson() => null;
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
                _a, _b, Offset, DimensionText(distance), distance);
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
            anchorA, anchorB, Offset, DimensionText(value), value);
    }

    /// <inheritdoc />
    protected internal override System.Text.Json.Nodes.JsonNode? SaveJson() =>
        _faceA is not null || _faceB is not null
            ? null   // a face-selector dimension is a lambda: only its own code can rebuild it
            : new System.Text.Json.Nodes.JsonObject
            {
                ["kind"] = "linear",
                ["a"] = DocumentWriter.SaveVector(_a),
                ["b"] = DocumentWriter.SaveVector(_b),
            };

    /// <summary>
    /// <b>Chain dimensioning</b>: one dimension per consecutive pair of
    /// <paramref name="points"/>, all on the same <paramref name="offset"/> so they
    /// read as one line of end-to-end dimensions (the classic chain style). At least
    /// two points; attach each returned dimension with <see cref="Part.Annotate"/>.
    /// </summary>
    public static IReadOnlyList<LinearDimension> Chain(
        IReadOnlyList<Vector3d> points, Vector3d offset)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2)
            throw new ArgumentException("Chain dimensioning needs at least two points.", nameof(points));
        var chain = new List<LinearDimension>(points.Count - 1);
        for (int i = 1; i < points.Count; i++)
            chain.Add(new LinearDimension(points[i - 1], points[i]) { Offset = offset });
        return chain;
    }

    /// <summary>
    /// <b>Ordinate (baseline/running) dimensioning</b>: every point dimensioned from
    /// the FIRST point — the common datum — with each successive dimension line pulled
    /// further out by <paramref name="spacing"/> (default: 40% of the offset length)
    /// so the stacked lines never overlap. Where chain dimensioning accumulates each
    /// segment's tolerance, the baseline style holds every position to the datum,
    /// which is why drawings prefer it for hole rows.
    /// </summary>
    public static IReadOnlyList<LinearDimension> Ordinate(
        IReadOnlyList<Vector3d> points, Vector3d offset, double? spacing = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2)
            throw new ArgumentException("Ordinate dimensioning needs at least two points.", nameof(points));
        double length = offset.Length;
        // Exact-zero guard: a zero offset has no direction to stack along.
        if (length == 0)
            throw new ArgumentException("Ordinate dimensioning needs a non-zero offset direction.", nameof(offset));
        var direction = offset / length;
        double step = spacing ?? length * 0.4;
        var ordinates = new List<LinearDimension>(points.Count - 1);
        for (int i = 1; i < points.Count; i++)
        {
            ordinates.Add(new LinearDimension(points[0], points[i])
            {
                Offset = direction * (length + step * (i - 1)),
            });
        }
        return ordinates;
    }
}

/// <summary>
/// An angular dimension — the angle at a vertex between two rays, drawn as extension
/// rays, an arc with arrowheads, and degree text. Two flavors: three fixed part-local
/// points (vertex + a point on each ray), and <see cref="BetweenFaces"/> — two planar
/// face selectors measuring the faces' included angle as they open from their common
/// edge line toward each face's own interior, re-measured per resolution like every
/// selector-based annotation.
/// </summary>
public sealed class AngularDimension : Annotation
{
    // Same selector-sugar angular tier LinearDimension uses (selection semantics).
    private const double AngularTolerance = 1e-6;

    private readonly Vector3d _vertex, _a, _b;
    private readonly Func<BrepSolid, BrepFace>? _faceA;
    private readonly Func<BrepSolid, BrepFace>? _faceB;

    /// <summary>Angle at <paramref name="vertex"/> between the rays toward
    /// <paramref name="a"/> and <paramref name="b"/> (part-local points; the value is
    /// in degrees, [0, 180]).</summary>
    public AngularDimension(Vector3d vertex, Vector3d a, Vector3d b)
    {
        _vertex = vertex;
        _a = a;
        _b = b;
    }

    private AngularDimension(Func<BrepSolid, BrepFace> faceA, Func<BrepSolid, BrepFace> faceB)
    {
        _faceA = faceA;
        _faceB = faceB;
    }

    /// <summary>
    /// Auto-measuring dimension between two <b>non-parallel planar</b> faces selected
    /// by semantic queries. The measured value is the faces' included angle: the angle
    /// between their in-plane directions perpendicular to the shared intersection
    /// line, each pointing from the line toward its own face's centroid — i.e. the
    /// angle a drafter would dimension between the two surfaces, not the angle between
    /// their normals. The vertex is the point of the intersection line nearest the two
    /// centroids. Parallel or non-planar selections fail loudly.
    /// </summary>
    public static AngularDimension BetweenFaces(
        Func<BrepSolid, BrepFace> faceA, Func<BrepSolid, BrepFace> faceB) => new(faceA, faceB);

    /// <inheritdoc />
    public override ResolvedAnnotation Resolve(Func<BrepSolid> solid)
    {
        if (_faceA is null || _faceB is null)
            return ResolveFromPoints(_vertex, _a - _vertex, _b - _vertex);

        var body = solid();
        var faceA = _faceA(body) ?? throw new InvalidOperationException(
            "AngularDimension: the first face selector returned null.");
        var faceB = _faceB(body) ?? throw new InvalidOperationException(
            "AngularDimension: the second face selector returned null.");
        if (!faceA.IsPlanar(out var originA, out var normalA))
            throw new InvalidOperationException(
                "AngularDimension: the first selected face is not planar.");
        if (!faceB.IsPlanar(out var originB, out var normalB))
            throw new InvalidOperationException(
                "AngularDimension: the second selected face is not planar.");

        var lineDirection = normalA.Cross(normalB);
        if (!lineDirection.TryNormalize(Core.Tolerance.Default, out var along))
            throw new InvalidOperationException(
                "AngularDimension: the selected faces are parallel — no angle to measure "
                + "(use LinearDimension.BetweenFaces for a distance).");

        // The planes' intersection line: solve for the point nearest the origin that
        // lies in both planes (two plane equations + minimal norm; closed form via the
        // 2x2 Gram system of the two normals).
        double dA = originA.Dot(normalA);
        double dB = originB.Dot(normalB);
        double nn = normalA.Dot(normalB);
        double det = 1 - nn * nn;   // normals are unit; det > 0 since not parallel
        double cA = (dA - dB * nn) / det;
        double cB = (dB - dA * nn) / det;
        var linePoint = normalA * cA + normalB * cB;

        // Face centroids (frame origins — the stable in-plane centroid).
        var centroidA = faceA.Frame()?.Origin ?? throw new InvalidOperationException(
            "AngularDimension: could not derive a frame for the first face.");
        var centroidB = faceB.Frame()?.Origin ?? throw new InvalidOperationException(
            "AngularDimension: could not derive a frame for the second face.");

        // Vertex: the intersection-line point nearest the two centroids' midpoint, so
        // the arc lands beside the faces rather than at the line's arbitrary origin.
        var mid = (centroidA + centroidB) * 0.5;
        var vertex = linePoint + along * (mid - linePoint).Dot(along);

        // Ray directions: in-plane, perpendicular to the shared line, toward each
        // face's own centroid — the included angle as the faces open from their edge.
        var rayA = InPlaneToward(centroidA, vertex, along);
        var rayB = InPlaneToward(centroidB, vertex, along);
        // Scale the ray anchors to reach toward the centroids (visual, not metric).
        double reach = Math.Max((centroidA - vertex).Length, (centroidB - vertex).Length);
        return ResolveFromPoints(vertex, rayA * reach, rayB * reach);

        static Vector3d InPlaneToward(in Vector3d centroid, in Vector3d vertex, in Vector3d along)
        {
            var toward = centroid - vertex;
            var perpendicular = toward - along * toward.Dot(along);
            if (!perpendicular.TryNormalize(Core.Tolerance.Default, out var unit))
                throw new InvalidOperationException(
                    "AngularDimension: a face's centroid lies on the intersection line — "
                    + "cannot orient the angle's rays.");
            return unit;
        }
    }

    /// <inheritdoc />
    protected internal override System.Text.Json.Nodes.JsonNode? SaveJson() =>
        _faceA is not null || _faceB is not null
            ? null
            : new System.Text.Json.Nodes.JsonObject
            {
                ["kind"] = "angular",
                ["vertex"] = DocumentWriter.SaveVector(_vertex),
                ["a"] = DocumentWriter.SaveVector(_a),
                ["b"] = DocumentWriter.SaveVector(_b),
            };

    private ResolvedAnnotation ResolveFromPoints(in Vector3d vertex, in Vector3d va, in Vector3d vb)
    {
        double la = va.Length;
        double lb = vb.Length;
        // Exact-zero guards: a ray point on the vertex spans no angle.
        if (la == 0 || lb == 0)
            throw new InvalidOperationException(
                "AngularDimension: a ray point coincides with the vertex.");
        double cosine = Math.Clamp(va.Dot(vb) / (la * lb), -1, 1);
        double degrees = Math.Acos(cosine) * 180 / Math.PI;
        if (degrees < AngularTolerance)
            throw new InvalidOperationException(
                "AngularDimension: the rays are parallel — no angle to measure.");
        // \u00B0 is the degree sign; source files stay pure ASCII (escapes only).
        string text = Label ?? Format(degrees) + "\u00B0" + (Tolerance?.Suffix ?? "");
        return new ResolvedAnnotation(this, AnnotationKind.AngularDimension,
            vertex + va, vertex + vb, Offset, text, degrees)
        {
            AnchorC = vertex,
        };
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
        if (!radial.TryNormalize(Core.Tolerance.Default, out var direction))
            direction = normal.ArbitraryPerpendicular(Core.Tolerance.Default);
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

    /// <inheritdoc />
    protected internal override System.Text.Json.Nodes.JsonNode? SaveJson() =>
        new System.Text.Json.Nodes.JsonObject
        {
            ["kind"] = "note",
            ["anchor"] = DocumentWriter.SaveVector(_anchor),
            ["text"] = _text,
        };
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

    /// <inheritdoc />
    protected internal override System.Text.Json.Nodes.JsonNode? SaveJson() =>
        new System.Text.Json.Nodes.JsonObject
        {
            ["kind"] = "datum",
            ["anchor"] = DocumentWriter.SaveVector(_anchor),
            ["letter"] = _letter,
        };
}
