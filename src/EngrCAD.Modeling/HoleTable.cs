using EngrCAD.Core;

namespace EngrCAD.Modeling;

// Hole tables and auto-attached callouts: the drilling data is already IN the Shape
// graph (a DrillShape/ThreadedHoleShape node carries its spec, its point list, its
// depth and its placement plane), so annotations can be GENERATED from the geometry
// that cut the holes instead of transcribed beside it — the same
// spec-is-the-source-of-truth idea as HoleCallout/ThreadCallout, extended to whole
// parts. Letters follow the order the holes were drilled in (the first Drill call is
// "A"), which is stable across regenerations because the graph is rebuilt in program
// order.

/// <summary>One row of a <see cref="HoleTable"/>: a letter, the callout text of the
/// spec that cut the holes, and the hole positions (part-local, on the placement
/// plane).</summary>
/// <param name="Letter">Row letter ("A", "B", ..., "AA" past 26).</param>
/// <param name="Callout">The spec's callout text (may contain '\n' continuations).</param>
/// <param name="Positions">Hole centres on the placement plane (part-local).</param>
/// <param name="DrillDiameter">The diameter a twist drill CUTS for this row (mm): a
/// simple hole's own diameter, a counterbore/countersink's THROUGH bore (the larger
/// feature is a milling operation, not a drill), a threaded hole's tap-drill pilot —
/// the numeric data a CAM consumer reads (the one-declaration rule: holes are stated
/// once, in the model, and the drill program derives).</param>
/// <param name="Depth">The stated hole depth, to the SHOULDER (mm) — the drill-cycle
/// convention too, so a real drill's tip reaches deeper exactly as the model's own
/// <c>WithTipAngle</c> draws it.</param>
/// <param name="Plane">The placement plane's local-to-world matrix.</param>
/// <param name="Threaded">True for a threaded-hole row (the diameter is the pilot).</param>
public sealed record HoleTableRow(
    string Letter, string Callout, IReadOnlyList<Vector3d> Positions,
    double DrillDiameter = 0, double Depth = 0, Matrix4d Plane = default,
    bool Threaded = false);

/// <summary>
/// A hole table generated from a part's <see cref="Shape"/> graph: one row per
/// <see cref="Shape.Drill"/> / <see cref="Shape.ThreadedHole"/> call, lettered in
/// call order. <see cref="Balloons"/> produces one boxed balloon per hole ("A1",
/// "A2", "B1", ...) and <see cref="TableNote"/> the table itself as a multi-line
/// note; <see cref="Annotate"/> attaches both to a part in one call.
/// </summary>
public sealed class HoleTable
{
    private HoleTable(IReadOnlyList<HoleTableRow> rows) => Rows = rows;

    public IReadOnlyList<HoleTableRow> Rows { get; }

    /// <summary>Total hole count across all rows.</summary>
    public int HoleCount => Rows.Sum(r => r.Positions.Count);

    /// <summary>
    /// Builds the table from a shape's graph. Rows appear in the order the holes were
    /// drilled (program order — the graph nests later calls around earlier ones, so
    /// the walk's parent-first order is reversed here). A shape with no drill or
    /// threaded-hole calls yields an empty table.
    /// </summary>
    public static HoleTable For(Shape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var calls = new List<HoleTableRow>();
        // ConstructionTree.FromShape already walks every node with a stable order;
        // pre-order lists the OUTERMOST (latest) call first, hence the reverse below.
        var nodes = ConstructionTree.FromShape(shape).Flatten().ToList();
        nodes.Reverse();
        int next = 0;
        foreach (var node in nodes)
        {
            switch (node.Target)
            {
                case DrillShape drill:
                    calls.Add(new HoleTableRow(
                        LetterFor(next++),
                        HoleCallout.Text(drill.Spec, drill.Depth),
                        PlanePoints(drill.PlaneMatrix, drill.Points),
                        drill.Spec.Diameter, drill.Depth, drill.PlaneMatrix));
                    break;
                case ThreadedHoleShape threaded:
                    calls.Add(new HoleTableRow(
                        LetterFor(next++),
                        ThreadCallout.Text(threaded.Spec, threaded.Depth),
                        PlanePoints(threaded.PlaneMatrix, threaded.Points),
                        StandardHoles.Tapped(threaded.Spec).Diameter, threaded.Depth,
                        threaded.PlaneMatrix, Threaded: true));
                    break;
            }
        }
        return new HoleTable(calls);
    }

    /// <summary>The table for a part whose geometry is a <see cref="Shape"/> (incl.
    /// feature-history parts — their regenerated body is one). Parts with raw
    /// B-Rep/mesh/SDF geometry carry no drilling data and get an empty table.</summary>
    public static HoleTable For(Part part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return part.Geometry is Shape shape ? For(shape) : new HoleTable([]);
    }

    /// <summary>One boxed balloon per hole — "A1", "A2", "B1", ... — anchored at the
    /// hole's centre on its placement plane (a <see cref="DatumLabel"/>-style boxed
    /// leader, the drawing convention for hole-table balloons).</summary>
    public IReadOnlyList<Annotation> Balloons()
    {
        var balloons = new List<Annotation>();
        foreach (var row in Rows)
        {
            for (int i = 0; i < row.Positions.Count; i++)
                balloons.Add(new DatumLabel(row.Positions[i], $"{row.Letter}{i + 1}"));
        }
        return balloons;
    }

    /// <summary>The table as text: one line per row — letter, callout (continuation
    /// lines folded to one line), hole count ("A &#x2300;5.5 &#x21A7;14 (4&#xD7;)").</summary>
    public string ToText()
    {
        var lines = new List<string>(Rows.Count);
        foreach (var row in Rows)
        {
            string callout = row.Callout.Replace('\n', ' ');
            lines.Add($"{row.Letter} {callout} ({row.Positions.Count}\u00D7)");
        }
        return string.Join("\n", lines);
    }

    /// <summary>The table as a multi-line <see cref="LeaderNote"/> at
    /// <paramref name="anchor"/> (part-local).</summary>
    public LeaderNote TableNote(Vector3d anchor) => new(anchor, ToText());

    /// <summary>Attaches the balloons and the table note to a part. Returns the number
    /// of annotations attached (0 for an empty table — nothing is attached).</summary>
    public int Annotate(Part part, Vector3d tableAnchor)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (Rows.Count == 0)
            return 0;
        int attached = 0;
        foreach (var balloon in Balloons())
        {
            part.Annotate(balloon);
            attached++;
        }
        part.Annotate(TableNote(tableAnchor));
        return attached + 1;
    }

    /// <summary>"A".."Z", then "AA", "AB", ... (spreadsheet lettering).</summary>
    internal static string LetterFor(int index)
    {
        string letter = "";
        int remaining = index;
        do
        {
            letter = (char)('A' + remaining % 26) + letter;
            remaining = remaining / 26 - 1;
        }
        while (remaining >= 0);
        return letter;
    }

    private static IReadOnlyList<Vector3d> PlanePoints(
        in Matrix4d planeMatrix, IReadOnlyList<Vector2d> points)
    {
        var positions = new Vector3d[points.Count];
        for (int i = 0; i < points.Count; i++)
            positions[i] = planeMatrix.TransformPoint(new Vector3d(points[i].X, points[i].Y, 0));
        return positions;
    }
}

/// <summary>
/// Auto-attached callouts: one <see cref="HoleCallout"/>/<see cref="ThreadCallout"/>
/// note per <see cref="Shape.Drill"/>/<see cref="Shape.ThreadedHole"/> call in a
/// part's graph — the opt-in that turns "v1 generates callouts, attachment is manual"
/// into one call. Deliberately an explicit method rather than a flag on Drill itself:
/// annotations belong to the PART (the document object), not to the geometry graph,
/// and a graph node cannot know which part will carry it.
/// </summary>
public static class HoleAnnotations
{
    /// <summary>
    /// Attaches one callout note per drill/threaded-hole call, anchored at the call's
    /// first hole centre, with an "N&#xD7; " count prefix when the call placed several
    /// holes ("4&#xD7; &#x2300;5.5 &#x21A7;14"). Returns how many notes were attached.
    /// For per-hole balloons and a summary table, use <see cref="HoleTable"/> instead.
    /// </summary>
    public static int AutoAttach(Part part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var table = HoleTable.For(part);
        foreach (var row in table.Rows)
        {
            string prefix = row.Positions.Count > 1 ? $"{row.Positions.Count}\u00D7 " : "";
            part.Annotate(new LeaderNote(row.Positions[0], prefix + row.Callout));
        }
        return table.Rows.Count;
    }
}
