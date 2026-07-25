using System.Diagnostics.CodeAnalysis;
using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Mesh;

namespace EngrCAD.Modeling;

// The construction tree: "how was this part built?" as a rows-and-children model that
// viewers (and scripts) can walk without knowing anything about the Shape graph's
// internal node types. Two sources feed it:
//
//   - a Shape-backed part  -> the operation graph, one row per node, operands as
//     children (Shape.Describe() already names every node — the same text Explain
//     prints, so labels come free and stay honest).
//   - a FeatureHistory-backed part -> the ordered feature list with names, suppression
//     state, and [Param] values, each feature row targeting the body AS OF that
//     feature (a rollback view).
//
// Design constraints this file honors:
//   - Shape is an immutable graph, so a row is a NODE REFERENCE plus a positional
//     PATH ("0/1/0"). Reference identity alone cannot identify a row (a shared
//     sub-shape — a pattern operand, say — appears at several paths); the path
//     disambiguates rows while the reference is what previews are cached by, so a
//     repeated sub-shape is lowered once.
//   - Previews are LINE geometry (edges), never meshes: they draw through the viewer's
//     existing line program like the annotation and isoline overlays.
//   - Building a preview lowers geometry, which must never happen on a render (or UI)
//     thread: ConstructionPreviewCache.Get is a plain blocking call that hosts run on a
//     background task, exactly the discipline Scene.PreMesh follows.

/// <summary>What one <see cref="ConstructionNode"/> row represents.</summary>
public enum ConstructionNodeKind
{
    /// <summary>A modeling operation with operands (boolean, drill, rim, transform, ...).</summary>
    Operation,

    /// <summary>A primitive solid leaf (box, sphere, cylinder, torus, cone, thread).</summary>
    Primitive,

    /// <summary>A 2D sketch consumed by an extrude/revolve/sweep, plus its placement.</summary>
    Sketch,

    /// <summary>Raw engine geometry wrapped by <c>Shape.From(...)</c>.</summary>
    Source,

    /// <summary>One step of a <see cref="FeatureHistory"/>.</summary>
    Feature,

    /// <summary>One <c>[Param]</c> value of a feature (a leaf label, never previewable).</summary>
    Parameter,
}

/// <summary>
/// One row of a part's construction tree: a label, a kind, a stable positional
/// <see cref="Path"/>, child rows, and — when the row has geometry — the
/// <see cref="Target"/> sub-shape or <see cref="Sketch"/> a viewer can preview.
/// Immutable once built; trees are cached per <see cref="Part"/>, so node references
/// are stable and usable as preview-cache keys.
/// </summary>
public sealed class ConstructionNode
{
    private readonly List<ConstructionNode> _children = [];

    internal ConstructionNode(string label, ConstructionNodeKind kind, string path)
    {
        Label = label;
        Kind = kind;
        Path = path;
    }

    /// <summary>Display text for the row (for shape nodes, <c>Shape.Describe()</c> —
    /// the same wording <c>Explain</c> prints).</summary>
    public string Label { get; }

    public ConstructionNodeKind Kind { get; }

    /// <summary>Positional identity within the tree: "" for the root, then "0", "0/1",
    /// "0/1/2". Stable across rebuilds of the same geometry, so hosts can persist
    /// expansion and selection state by path.</summary>
    public string Path { get; }

    public IReadOnlyList<ConstructionNode> Children => _children;

    /// <summary>The shape sub-graph this row was built from — lower it to preview the
    /// model as of this step. Null for sketch and parameter rows.</summary>
    public Shape? Target { get; internal init; }

    /// <summary>The sketch this row shows (sketch rows only); draw it on
    /// <see cref="Placement"/>.</summary>
    public Sketch? Sketch { get; internal init; }

    /// <summary>Sketch-local to world placement of <see cref="Sketch"/> (identity for
    /// every other kind).</summary>
    public Matrix4d Placement { get; internal init; } = Matrix4d.Identity;

    /// <summary>The feature this row came from (feature rows only).</summary>
    public Feature? Feature { get; internal init; }

    /// <summary>Whether the feature this row represents is suppressed.</summary>
    public bool Suppressed { get; internal init; }

    /// <summary>Secondary text (a parameter's value, "suppressed", ...); may be null.</summary>
    public string? Detail { get; internal init; }

    /// <summary>Whether selecting this row can show geometry.</summary>
    public bool CanPreview => Target is not null || Sketch is not null;

    /// <summary>
    /// The preview-cache key. Shape rows key on the shape reference, so a sub-shape
    /// used twice (a pattern operand) is lowered once and shared; sketch rows key on
    /// the row itself, because the same <see cref="Sketch"/> may be placed on different
    /// planes.
    /// </summary>
    internal object CacheKey => Target is { } target ? target : this;

    internal void AddChild(ConstructionNode child) => _children.Add(child);

    /// <summary>This node and every descendant, pre-order (the row order a flattened
    /// tree view draws).</summary>
    public IEnumerable<ConstructionNode> Flatten()
    {
        yield return this;
        foreach (var child in _children)
        {
            foreach (var descendant in child.Flatten())
                yield return descendant;
        }
    }

    /// <summary>The node with the given <see cref="Path"/>, or null.</summary>
    public ConstructionNode? Find(string path) => Flatten().FirstOrDefault(n => n.Path == path);

    public override string ToString() => Detail is null ? Label : $"{Label} = {Detail}";
}

/// <summary>Builds <see cref="ConstructionNode"/> trees from the modeling objects that
/// carry construction information (a <see cref="Shape"/> graph or a
/// <see cref="FeatureHistory"/>).</summary>
public static class ConstructionTree
{
    /// <summary>
    /// The construction tree of a part: its feature list when it came from a
    /// <see cref="FeatureHistory"/>, otherwise its <see cref="Shape"/> graph, otherwise
    /// null (raw B-Rep/mesh/SDF parts carry no construction information).
    /// Prefer <see cref="Part.ConstructionTree"/>, which caches the result.
    /// </summary>
    public static ConstructionNode? For(Part part)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (part.History is { } history)
            return FromHistory(history);
        return part.Geometry is Shape shape ? FromShape(shape) : null;
    }

    /// <summary>The operation graph of a shape as nested rows (root = the shape itself).</summary>
    public static ConstructionNode FromShape(Shape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        return BuildShape(shape, "");
    }

    /// <summary>
    /// A feature history as rows: a "Features" root (targeting the regenerated body)
    /// over one row per feature — named, flagged when suppressed, targeting the body as
    /// of that step (rollback preview) — with its <c>[Param]</c> values as leaf rows.
    /// Call after <see cref="FeatureHistory.Regenerate"/>; un-regenerated steps simply
    /// have no preview target.
    /// </summary>
    public static ConstructionNode FromHistory(FeatureHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var root = new ConstructionNode("Features", ConstructionNodeKind.Operation, "")
        {
            Target = history.Result,
        };
        for (int i = 0; i < history.Features.Count; i++)
        {
            var feature = history.Features[i];
            var node = new ConstructionNode(history.NameOf(feature), ConstructionNodeKind.Feature, i.ToString())
            {
                Feature = feature,
                Suppressed = feature.Suppressed,
                // Rollback view: the body as of this step (a suppressed step shows the
                // body it passed through untouched).
                Target = history.BodyAfter(i),
                Detail = feature.Suppressed ? "suppressed" : null,
            };
            int p = 0;
            foreach (var parameter in feature.Parameters)
            {
                node.AddChild(new ConstructionNode(
                    parameter.Name, ConstructionNodeKind.Parameter, $"{i}/{p++}")
                {
                    Detail = FormatParameter(parameter),
                });
            }
            root.AddChild(node);
        }
        return root;
    }

    private static string FormatParameter(ParamInfo parameter)
    {
        string text = parameter.Value switch
        {
            null => "-",
            double d => d.ToString("G6"),
            float f => f.ToString("G6"),
            Vector2d v => $"({v.X:G4}, {v.Y:G4})",
            Vector3d v => $"({v.X:G4}, {v.Y:G4}, {v.Z:G4})",
            var other => other.ToString() ?? "?",
        };
        return parameter.Units is { Length: > 0 } units ? $"{text} {units}" : text;
    }

    // ---- shape graph walk ----

    private static ConstructionNode BuildShape(Shape shape, string path)
    {
        var node = new ConstructionNode(shape.Describe(), KindOf(shape), path) { Target = shape };
        int next = 0;
        switch (shape)
        {
            case BooleanShape boolean:
                AddShape(node, boolean.A, ref next);
                AddShape(node, boolean.B, ref next);
                break;
            case SmoothShape smooth:
                AddShape(node, smooth.A, ref next);
                AddShape(node, smooth.B, ref next);
                break;
            case HullShape hull:
                foreach (var operand in hull.Operands)
                    AddShape(node, operand, ref next);
                break;
            case OffsetShape offset:
                AddShape(node, offset.Child, ref next);
                break;
            case ShellShape shell:
                AddShape(node, shell.Child, ref next);
                break;
            case LatticeShape lattice:
                AddShape(node, lattice.Child, ref next);
                break;
            case RimShape rim:
                AddShape(node, rim.Child, ref next);
                break;
            case DrillShape drill:
                AddShape(node, drill.Child, ref next);
                break;
            case ThreadedHoleShape threaded:
                AddShape(node, threaded.Child, ref next);
                break;
            case TransformShape transform:
                AddShape(node, transform.Child, ref next);
                break;
            case ExtrudeShape { Sketch: { } extruded } extrude:
                AddSketch(node, extruded, extrude.PlaneMatrix, ref next);
                break;
            case RevolveShape { Sketch: { } revolved } revolve:
                AddSketch(node, revolved, revolve.PlaneMatrix, ref next);
                break;
            case SweepShape { Sketch: { } swept } sweep:
                AddSketch(node, swept, sweep.PlaneMatrix, ref next);
                break;
        }
        return node;

        static string ChildPath(string parent, int index) =>
            parent.Length == 0 ? index.ToString() : $"{parent}/{index}";

        static void AddShape(ConstructionNode parent, Shape child, ref int index) =>
            parent.AddChild(BuildShape(child, ChildPath(parent.Path, index++)));

        static void AddSketch(ConstructionNode parent, Sketch sketch, in Matrix4d placement, ref int index)
        {
            string holes = sketch.Holes.Count == 0 ? "" : $", {sketch.Holes.Count} holes";
            parent.AddChild(new ConstructionNode(
                $"Sketch({sketch.Segments.Count} curves{holes})",
                ConstructionNodeKind.Sketch,
                ChildPath(parent.Path, index++))
            {
                Sketch = sketch,
                Placement = placement,
            });
        }
    }

    private static ConstructionNodeKind KindOf(Shape shape) => shape switch
    {
        BoxShape or SphereShape or CylinderShape or TorusShape or ConeShape or ThreadShape =>
            ConstructionNodeKind.Primitive,
        SourceShape => ConstructionNodeKind.Source,
        _ => ConstructionNodeKind.Operation,
    };
}

/// <summary>
/// Display-only line geometry for one construction-tree row: the sub-shape's feature
/// edges, or a sketch's curves flattened onto its plane. World-space (a part's own
/// transform and an assembly instance's pose are applied by the viewer).
/// <see cref="Error"/> is non-null when the row could not be lowered — a preview
/// failure is a status message, never an exception.
/// </summary>
public sealed class ConstructionPreview
{
    internal ConstructionPreview(IReadOnlyList<(Vector3d A, Vector3d B)> segments, in Aabb bounds, string? error)
    {
        Segments = segments;
        Bounds = bounds;
        Error = error;
    }

    /// <summary>Line segments to draw (part-local coordinates).</summary>
    public IReadOnlyList<(Vector3d A, Vector3d B)> Segments { get; }

    /// <summary>Bounds of <see cref="Segments"/> (empty when there are none).</summary>
    public Aabb Bounds { get; }

    /// <summary>Diagnostic when the row could not be previewed; null on success.</summary>
    public string? Error { get; }

    public bool IsEmpty => Segments.Count == 0;

    /// <summary>
    /// Builds the preview for one row. Lowers geometry (a shape row tessellates or
    /// lowers to B-Rep), so callers must run this OFF the UI/render thread — see
    /// <see cref="ConstructionPreviewCache"/>.
    /// </summary>
    public static ConstructionPreview Build(ConstructionNode node, MeshQuality? quality = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        var q = quality ?? MeshQuality.Default;
        try
        {
            if (node.Sketch is { } sketch)
                return From(SketchOutline(sketch, node.Placement, q));
            if (node.Target is { } shape)
                return From(ShapeEdges(shape, q));
            return new ConstructionPreview([], Aabb.Empty, $"'{node.Label}' has no geometry to preview.");
        }
        catch (Exception exception)
        {
            return new ConstructionPreview([], Aabb.Empty,
                $"preview of '{node.Label}' failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static ConstructionPreview From(IReadOnlyList<(Vector3d A, Vector3d B)> segments)
    {
        var bounds = Aabb.Empty;
        foreach (var (a, b) in segments)
            bounds = bounds.Union(a).Union(b);
        return new ConstructionPreview(segments, bounds, null);
    }

    /// <summary>
    /// Display edges of a sub-shape, by the same rule <see cref="Part.GetFeatureEdges"/>
    /// uses for whole parts: the ACTUAL B-Rep edges when the shape has a B-Rep lowering
    /// (exact circles stay smooth), mesh dihedrals otherwise.
    /// </summary>
    internal static IReadOnlyList<(Vector3d A, Vector3d B)> ShapeEdges(Shape shape, MeshQuality quality)
    {
        if (shape.CanConvertTo(TargetRep.Brep))
        {
            try
            {
                return BrepFeatureEdges.Extract(
                    shape.ToBrep(),
                    Math.Max(96, quality.SegmentsPerCircle),
                    Math.Max(48, quality.CurveSamples));
            }
            catch
            {
                // Extraction hiccup: fall back to the mesh route below, as Part does.
            }
        }
        return MeshFeatureEdges.Extract(shape.ToMesh(quality));
    }

    /// <summary>
    /// A sketch drawn as it is: every curve of the outer loop and each hole, flattened
    /// to polylines and placed on the sketch plane. Display-only — arcs and Béziers are
    /// chorded at display resolution, which is all a preview needs (the modeling
    /// lowerings keep the exact curves).
    /// </summary>
    internal static IReadOnlyList<(Vector3d A, Vector3d B)> SketchOutline(
        Sketch sketch, in Matrix4d placement, MeshQuality quality)
    {
        var segments = new List<(Vector3d A, Vector3d B)>();
        AppendLoop(segments, sketch, placement, quality);
        foreach (var hole in sketch.Holes)
            AppendLoop(segments, hole, placement, quality);
        return segments;
    }

    private static void AppendLoop(
        List<(Vector3d A, Vector3d B)> segments, Sketch loop, in Matrix4d placement, MeshQuality quality)
    {
        foreach (var piece in loop.Segments)
        {
            var curve = piece.ToCurve();
            var domain = curve.Domain;
            int count = SampleCount(curve, quality);
            var previous = placement.TransformPoint(curve.PointAt(domain.Start));
            for (int i = 1; i <= count; i++)
            {
                var point = placement.TransformPoint(curve.PointAt(domain.ParameterAt((double)i / count)));
                segments.Add((previous, point));
                previous = point;
            }
        }
    }

    /// <summary>Chord count for one sketch curve: lines need one, full circles the
    /// quality's circle resolution, arcs their share of it, everything else (Béziers)
    /// the curve-sample count.</summary>
    private static int SampleCount(Curve3d curve, MeshQuality quality)
    {
        int circle = Math.Max(8, quality.SegmentsPerCircle);
        return curve switch
        {
            Line3d => 1,
            Circle3d => circle,
            CurveSegment arc when arc.Base.Underlying is Circle3d => Math.Max(2,
                (int)Math.Ceiling(Math.Abs(arc.BaseEnd - arc.BaseStart) / (2 * Math.PI) * circle)),
            _ => Math.Max(8, quality.CurveSamples),
        };
    }
}

/// <summary>
/// Memoizes construction-row previews so selecting a row never re-lowers geometry.
/// Entries are keyed by the row's <see cref="ConstructionNode.CacheKey"/> — the shape
/// reference for operation rows (a sub-shape shared by several rows is lowered once),
/// the row itself for sketch rows. Thread-safe; hosts call <see cref="Get"/> on a
/// background task (lowering must never run on a UI or render thread) and
/// <see cref="TryGet"/> synchronously to take the fast path.
/// </summary>
public sealed class ConstructionPreviewCache
{
    private readonly Dictionary<object, ConstructionPreview> _previews = new(ReferenceEqualityComparer.Instance);
    private readonly Lock _lock = new();

    /// <summary>The cached preview for a row, when one has already been built.</summary>
    public bool TryGet(ConstructionNode node, [MaybeNullWhen(false)] out ConstructionPreview preview)
    {
        ArgumentNullException.ThrowIfNull(node);
        lock (_lock)
            return _previews.TryGetValue(node.CacheKey, out preview);
    }

    /// <summary>
    /// The preview for a row, building (and caching) it on the calling thread when it
    /// is not cached yet. Blocking — call it from a background task.
    /// </summary>
    public ConstructionPreview Get(ConstructionNode node, MeshQuality? quality = null)
    {
        if (TryGet(node, out var cached))
            return cached;
        // Built outside the lock: two threads racing on the same row do the work twice
        // but publish the same value, which is far better than serializing every
        // preview behind one lock.
        var preview = ConstructionPreview.Build(node, quality);
        lock (_lock)
        {
            if (_previews.TryGetValue(node.CacheKey, out var raced))
                return raced;
            _previews[node.CacheKey] = preview;
            return preview;
        }
    }

    /// <summary>Number of cached previews (diagnostics and tests).</summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _previews.Count;
        }
    }

    /// <summary>Drops every cached preview.</summary>
    public void Clear()
    {
        lock (_lock)
            _previews.Clear();
    }
}
