using System.Globalization;
using System.Text;
using EngrCAD.Core;

namespace EngrCAD.Cam;

/// <summary>
/// The CNC G-code writer (GRBL/LinuxCNC-style words) — the FDM writer's milling sibling over the
/// same conventions: modes STATED (G21/G90), plain G0/G1 only (drilling ships EXPANDED peck
/// moves, so the same twin decoder <see cref="GcodeReader"/> reads every program this writer
/// emits; canned G81/G83 cycles and G2/G3 arcs are filed with the campaign), deterministic
/// byte-for-byte.
///
/// <para><b>A move's meaning is its SHAPE</b>, classified from the pass geometry rather than
/// annotated per move: an XY move cuts at the tool's feed rate, a straight-DOWN move plunges at
/// the plunge rate, a straight-UP move retracts as a rapid — which is what lets one
/// <see cref="MillPass"/> vocabulary carry pockets, tabbed profiles and pecked drills alike.
/// Every pass is entered from the safe height with a rapid over its start.</para>
/// </summary>
public static class CncGcodeWriter
{
    /// <summary>Writes the operations as G-code, rapids above <paramref name="safeZ"/>.</summary>
    public static string Write(IReadOnlyList<MillOperation> operations, double safeZ = 5)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (!(safeZ > 0) || !double.IsFinite(safeZ))
            throw new ArgumentException($"The safe height must be finite and positive; got {safeZ:0.###}.");

        var b = new StringBuilder();
        b.Append("; EngrCAD CNC\n");
        b.Append("G21 ; millimetres\n");
        b.Append("G90 ; absolute coordinates\n");
        double lastFeed = -1;
        foreach (var op in operations)
        {
            b.Append($";OP {op.Name} T{Num(op.Tool.Diameter)}\n");
            b.Append($"M3 S{(int)Math.Round(op.Tool.SpindleRpm)}\n");
            foreach (var pass in op.Passes)
            {
                var start = pass.Points[0];
                b.Append($"G0 Z{Num(safeZ)}\n");
                b.Append($"G0 X{Num(start.X)} Y{Num(start.Y)}\n");
                Move(b, new Vector3d(start.X, start.Y, safeZ), start, op.Tool, ref lastFeed);

                var previous = start;
                int count = pass.Points.Count + (pass.IsClosed ? 1 : 0);
                for (int i = 1; i < count; i++)
                {
                    var point = pass.Points[i % pass.Points.Count];
                    Move(b, previous, point, op.Tool, ref lastFeed);
                    previous = point;
                }
            }
            b.Append($"G0 Z{Num(safeZ)}\n");
        }
        b.Append("M5\n");
        b.Append("M30\n");
        return b.ToString();
    }

    /// <summary>Writes the operations to a <c>.nc</c>/<c>.gcode</c> file.</summary>
    public static void WriteFile(IReadOnlyList<MillOperation> operations, string path, double safeZ = 5)
    {
        ArgumentNullException.ThrowIfNull(path);
        File.WriteAllText(path, Write(operations, safeZ));
    }

    /// <summary>One classified move: XY = cut at feed, straight down = plunge at plunge rate,
    /// straight up = rapid retract.</summary>
    private static void Move(StringBuilder b, in Vector3d from, in Vector3d to, MillTool tool, ref double lastFeed)
    {
        bool movedXy = to.X != from.X || to.Y != from.Y;
        if (!movedXy && to.Z == from.Z)
            return;
        if (!movedXy && to.Z > from.Z)
        {
            b.Append($"G0 Z{Num(to.Z)}\n");                  // retract: a rapid
            return;
        }
        double feed = movedXy ? tool.FeedRate : tool.PlungeRate;
        b.Append("G1");
        if (movedXy)
            b.Append($" X{Num(to.X)} Y{Num(to.Y)}");
        if (to.Z != from.Z)
            b.Append($" Z{Num(to.Z)}");
        if (feed != lastFeed)
        {
            b.Append($" F{(int)Math.Round(feed)}");
            lastFeed = feed;
        }
        b.Append('\n');
    }

    private static string Num(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
