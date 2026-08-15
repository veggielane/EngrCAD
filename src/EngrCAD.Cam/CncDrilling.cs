using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Cam;

/// <summary>
/// The drill program DERIVED from the model's own hole declarations — the one-declaration
/// rule at the CAM boundary: a <see cref="Shape.Drill"/>/<see cref="Shape.ThreadedHole"/> call
/// already states the diameter, the depth and the positions (it is what
/// <see cref="HoleTable"/> letters for the drawing), so the CNC program reads the SAME rows
/// rather than having the coordinates transcribed beside the model. One
/// <see cref="MillOperation"/> per distinct drill diameter, ascending — a real setup is one
/// tool per diameter — with a counterbore/countersink contributing its THROUGH bore (the
/// larger feature is a milling operation, not a drill) and a threaded hole its tap-drill
/// pilot.
///
/// <para><b>The bed frame is the placement plane</b>: the plane is the stock top (z = 0),
/// depths run from it exactly as the model states them — depth to the SHOULDER, which is the
/// drill-cycle convention too, so a real drill's tip reaches deeper exactly as the model's
/// own <c>WithTipAngle</c> draws it. v1 is one setup: every call must sit on ONE
/// world-XY-parallel plane at one height; a tilted plane or a second height refuses naming
/// the row's letter (a 3-axis machine drills straight down, and which face goes up is the
/// fixture's decision, not a silent re-pose). Tap runout and through-hole breakthrough are
/// the caller's to add via <c>extraDepth</c>.</para>
/// </summary>
public static class CncDrilling
{
    /// <summary>Builds the drill operations from the shape's own hole declarations —
    /// one operation per distinct diameter, ascending; empty for a shape declaring no
    /// holes. <paramref name="toolFor"/> maps a diameter to its tool (null = a default
    /// <see cref="MillTool"/> of that diameter); <paramref name="peck"/> is the peck depth
    /// (null = one diameter, the shop rule; 0 = a single plunge);
    /// <paramref name="extraDepth"/> adds to every stated depth (breakthrough, tap
    /// runout).</summary>
    public static IReadOnlyList<MillOperation> FromShape(
        Shape shape, Func<double, MillTool>? toolFor = null, double? peck = null,
        double extraDepth = 0)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (!(extraDepth >= 0) || !double.IsFinite(extraDepth))
            throw new ArgumentException(
                $"extraDepth must be finite and non-negative; got {extraDepth:0.###}.");

        var table = HoleTable.For(shape);
        if (table.Rows.Count == 0)
            return [];

        // One setup: every plane vertical-axis and at one height, refused by letter.
        double? topZ = null;
        foreach (var row in table.Rows)
        {
            var normal = row.Plane.TransformVector(new Vector3d(0, 0, 1));
            if (Math.Abs(Math.Abs(normal.Z) - normal.Length) > 1e-9 * normal.Length)
                throw new ArgumentException(
                    $"Hole row {row.Letter} is placed on a tilted plane (normal "
                    + $"({normal.X:0.###}, {normal.Y:0.###}, {normal.Z:0.###})) — a 3-axis "
                    + "drill program runs straight down, and which face goes up is the "
                    + "fixture's decision. Re-pose the model for this setup.");
            double planeZ = row.Positions.Count > 0 ? row.Positions[0].Z
                : row.Plane.TransformPoint(new Vector3d(0, 0, 0)).Z;
            if (topZ is null)
                topZ = planeZ;
            else if (Math.Abs(topZ.Value - planeZ) > 1e-9)
                throw new ArgumentException(
                    $"Hole row {row.Letter} sits at z = {planeZ:0.###} where earlier rows sit "
                    + $"at z = {topZ.Value:0.###} — v1 drills ONE setup with the placement "
                    + "plane as the stock top. Program the heights as separate setups.");
        }

        var operations = new List<MillOperation>();
        foreach (var group in table.Rows
            .GroupBy(r => r.DrillDiameter).OrderBy(g => g.Key))
        {
            double diameter = group.Key;
            var tool = toolFor?.Invoke(diameter) ?? new MillTool(diameter);
            double bite = peck ?? diameter;
            var passes = new List<MillPass>();
            foreach (var row in group)
            {
                var points = row.Positions
                    .Select(p => new Vector2d(p.X, p.Y)).ToList();
                var op = CncMill.Drill(points, tool, row.Depth + extraDepth, bite,
                    $"drill {row.Letter}");
                passes.AddRange(op.Passes);
            }
            operations.Add(new MillOperation(
                $"drill Ø{diameter.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}",
                tool, passes));
        }
        return operations;
    }

    /// <summary>The drill program for a part whose geometry is a <see cref="Shape"/> —
    /// empty for raw B-Rep/mesh/SDF geometry, which carries no hole declarations.</summary>
    public static IReadOnlyList<MillOperation> FromPart(
        Part part, Func<double, MillTool>? toolFor = null, double? peck = null,
        double extraDepth = 0)
    {
        ArgumentNullException.ThrowIfNull(part);
        return part.Geometry is Shape shape
            ? FromShape(shape, toolFor, peck, extraDepth)
            : [];
    }
}
