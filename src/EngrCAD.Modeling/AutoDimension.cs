using EngrCAD.Core;

namespace EngrCAD.Modeling;

// A first pass at the dimensions a drawing obviously needs: the overall extents of each
// view, and one callout per hole family with its pattern named.
//
// It reads the CONSTRUCTION GRAPH, exactly as HoleTable.For(part) does, rather than
// measuring circles in the projection -- which is the same reason a hole table cannot put
// an M6 callout on an M10 hole. A drill's spec, its depth and its points are in the graph;
// what is inferred is only the PATTERN, and that inference is closed form (see PatternOf).
//
// Explicit placement stays the contract. This is a starting point: everything it adds is
// an ordinary SheetAnnotation on the view, so a caller keeps, moves or deletes any of it.

/// <summary>What an <see cref="AutoDimension"/> pass places, and where.</summary>
public sealed record AutoDimensionOptions
{
    /// <summary>Dimension the view's overall width and height.</summary>
    public bool OverallExtents { get; init; } = true;

    /// <summary>Call out each hole family the graph carries, with its pattern.</summary>
    public bool Holes { get; init; } = true;

    /// <summary>Standoff of the first dimension line from the view, sheet mm; 0 takes the
    /// style's own default.</summary>
    public double Standoff { get; init; }

    /// <summary>Gap between the view's right edge and the hole callouts' elbow column,
    /// sheet mm.</summary>
    public double CalloutGap { get; init; } = 12;
}

/// <summary>
/// Places the obvious dimensions on a view: overall extents, and a callout per hole family
/// naming its diameter, depth and pattern.
///
/// <para><b>The placement rule is stated rather than searched, which is what makes it
/// verifiable.</b> The overall width goes BELOW the view, the overall height to its LEFT,
/// and every hole callout runs out to a common column to the RIGHT, stacked one text block
/// apart. Three disjoint bands outside the line work, and within the right-hand band the
/// rows are one block apart by construction — so no two of these can collide with each
/// other or with the view, and a test asserts exactly that rather than trusting it.</para>
///
/// <para>Call it AFTER the layout has chosen the view's scale (<c>StandardLayout</c> or
/// <c>Arrange</c>): a leader is a paper-millimetre vector, so it is computed against the
/// scale in force when the callout is placed.</para>
/// </summary>
public static class AutoDimension
{
    /// <summary>
    /// Adds the pass's annotations to <paramref name="view"/> and returns them, in the order
    /// they were placed (a deterministic function of the view and its parts).
    /// </summary>
    public static IReadOnlyList<SheetAnnotation> Apply(
        DrawingView view, AutoDimensionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        var opts = options ?? new AutoDimensionOptions();
        var added = new List<SheetAnnotation>();
        var bounds = view.ContentBounds;
        if (bounds.IsEmpty)
            return added;

        var style = view.Style;
        double standoff = opts.Standoff > 0 ? opts.Standoff : style.DefaultOffset;
        var min = new Vector2d(bounds.Min.X, bounds.Min.Y);
        var max = new Vector2d(bounds.Max.X, bounds.Max.Y);

        if (opts.OverallExtents && max.X - min.X > 0)
        {
            // A negative standoff puts the line on the far side of a->b: below for a
            // horizontal dimension, and the sign lives in the vector rather than a branch.
            added.Add(view.Annotate(SheetLinearDimension.Horizontal(
                min, new Vector2d(max.X, min.Y), -standoff)));
        }
        if (opts.OverallExtents && max.Y - min.Y > 0)
        {
            added.Add(view.Annotate(SheetLinearDimension.Vertical(
                min, new Vector2d(min.X, max.Y), standoff)));
        }

        if (opts.Holes)
            added.AddRange(AddHoleCallouts(view, bounds, opts, style));
        return added;
    }

    /// <summary>
    /// The hole families of a view: one entry per <see cref="Shape.Drill"/> or
    /// <see cref="Shape.ThreadedHole"/> call whose axis points along the view direction, so
    /// the holes read as circles. Public because it is what a caller would otherwise
    /// re-derive to place the callouts by hand.
    /// </summary>
    public static IReadOnlyList<HoleFamily> Families(DrawingView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var families = new List<HoleFamily>();
        foreach (var instance in DebugFilter.Exported(view.Instances))
        {
            if (instance.Part.Geometry is not Shape shape)
                continue;
            foreach (var row in HoleTable.For(shape).Rows)
            {
                if (row.Positions.Count == 0)
                    continue;
                // The hole's axis is its placement plane's own +Z, carried into the world by
                // the instance and then into the view's frame; a hole reads as a CIRCLE only
                // where that axis is along the line of sight, and a callout on a hole seen
                // edge-on would be pointing at a rectangle.
                var axis = instance.World.TransformVector(row.Plane.TransformVector(Vector3d.UnitZ));
                if (!axis.TryNormalize(Tolerance.Default, out var unit)
                    || Math.Abs(unit.Dot(view.Direction)) < 1 - 1e-6)
                    continue;
                var points = new Vector2d[row.Positions.Count];
                for (int i = 0; i < points.Length; i++)
                {
                    var local = view.Frame.ToLocal(instance.World.TransformPoint(row.Positions[i]));
                    points[i] = new Vector2d(local.X, local.Y);
                }
                families.Add(new HoleFamily(row, points));
            }
        }
        return families;
    }

    private static List<SheetAnnotation> AddHoleCallouts(
        DrawingView view, in Aabb bounds, AutoDimensionOptions options, SheetStyle style)
    {
        var added = new List<SheetAnnotation>();
        var families = Families(view);
        if (families.Count == 0)
            return added;

        double extent = Math.Max(bounds.Size.X, bounds.Size.Y);
        var texts = new List<string>(families.Count);
        int lines = 1;
        foreach (var family in families)
        {
            // The multiplication and diameter signs ride as unicode ESCAPES so this file
            // stays ASCII, the same rule HoleCallout follows.
            string prefix = family.Points.Count > 1 ? $"{family.Points.Count}\u00D7 " : "";
            string text = prefix + family.Row.Callout;
            if (PatternOf(family.Points, extent) is { } pattern)
                text += "\n" + pattern;
            texts.Add(text);
            lines = Math.Max(lines, text.Split('\n').Length);
        }

        // One row per family, a whole text block apart, so the notes cannot overlap each
        // other however many lines the longest one runs to.
        double rowStep = lines * style.TextHeight * SheetStyle.LineSpacing + style.TextHeight;
        double scale = view.Scale;
        for (int i = 0; i < families.Count; i++)
        {
            var anchor = families[i].Points[0];
            var leader = new Vector2d(
                (bounds.Max.X - anchor.X) * scale + options.CalloutGap,
                (bounds.Max.Y - anchor.Y) * scale - i * rowStep);
            added.Add(view.Annotate(new SheetNote(anchor, leader, texts[i])));
        }
        return added;
    }

    /// <summary>
    /// The pattern a set of hole centres forms, as the text a drawing states it with, or
    /// null when they form none this recognises.
    ///
    /// <para><b>Both recognitions are closed form and exact where the pattern is exact.</b>
    /// A bolt circle is "every point the same distance from their centroid", which is what
    /// <see cref="LocationSet.Polar"/> constructs, so its diameter comes back as exactly
    /// twice that distance; a grid is "the distinct x and y coordinates are evenly spaced
    /// and every combination is present", so its pitches come back as exactly the spacing
    /// <see cref="LocationSet.Grid"/> was given. Anything else is reported as no pattern
    /// rather than as an approximate one.</para>
    /// </summary>
    /// <param name="points">Hole centres in one view's projected model coordinates.</param>
    /// <param name="extent">The view's own extent, which the comparisons are relative to
    /// (the scale-free tier: a bolt circle on a 4 mm part and one on a 4 m part are the
    /// same recognition).</param>
    public static string? PatternOf(IReadOnlyList<Vector2d> points, double extent)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2 || !(extent > 0))
            return null;
        double tolerance = extent * 1e-9;

        var centroid = Vector2d.Zero;
        foreach (var p in points)
            centroid += p;
        centroid /= points.Count;

        if (points.Count >= 3)
        {
            double radius = (points[0] - centroid).Length;
            bool concyclic = radius > tolerance;
            foreach (var p in points)
            {
                if (Math.Abs((p - centroid).Length - radius) > tolerance)
                {
                    concyclic = false;
                    break;
                }
            }
            if (concyclic)
                return $"ON \u2300{Annotation.Format(2 * radius)} B.C.";
        }

        var xs = Distinct(points.Select(p => p.X), tolerance);
        var ys = Distinct(points.Select(p => p.Y), tolerance);
        if (xs.Count * ys.Count != points.Count)
            return null;
        double? px = UniformPitch(xs, tolerance);
        double? py = UniformPitch(ys, tolerance);
        if (px is null && py is null)
            return null;
        if (px is { } pitchX && py is { } pitchY)
            return $"{xs.Count}\u00D7{ys.Count} PITCH {Annotation.Format(pitchX)} \u00D7 "
                 + Annotation.Format(pitchY);
        return px is { } onlyX
            ? $"{xs.Count}\u00D7 PITCH {Annotation.Format(onlyX)}"
            : $"{ys.Count}\u00D7 PITCH {Annotation.Format(py!.Value)}";
    }

    /// <summary>The distinct coordinates, ascending, merged at the stated tolerance.</summary>
    private static List<double> Distinct(IEnumerable<double> values, double tolerance)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var distinct = new List<double>();
        foreach (double v in sorted)
        {
            if (distinct.Count == 0 || Math.Abs(v - distinct[^1]) > tolerance)
                distinct.Add(v);
        }
        return distinct;
    }

    /// <summary>The common spacing of an ascending list, or null when it has fewer than two
    /// entries or its gaps differ.</summary>
    private static double? UniformPitch(List<double> values, double tolerance)
    {
        if (values.Count < 2)
            return null;
        double pitch = values[1] - values[0];
        for (int i = 1; i + 1 < values.Count; i++)
        {
            if (Math.Abs(values[i + 1] - values[i] - pitch) > tolerance)
                return null;
        }
        return pitch;
    }
}

/// <summary>One drill or threaded-hole call as a view sees it: the graph's own row, and its
/// hole centres in that view's projected model coordinates.</summary>
/// <param name="Row">The hole-table row the construction graph carries.</param>
/// <param name="Points">The centres, projected into the view.</param>
public sealed record HoleFamily(HoleTableRow Row, IReadOnlyList<Vector2d> Points);
