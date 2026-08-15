using System.Globalization;
using System.Text;
using EngrCAD.Core;

namespace EngrCAD.Cam;

/// <summary>
/// The CNC G-code writer (GRBL/LinuxCNC-style words) — the FDM writer's milling sibling over the
/// same conventions: modes STATED (G21/G90), plain G0/G1 by default (drilling ships EXPANDED
/// peck moves, so the twin decoder <see cref="GcodeReader"/> reads every program this writer
/// emits; G2/G3 arcs stay filed with the campaign), deterministic byte-for-byte.
///
/// <para><b>A move's meaning is its SHAPE</b>, classified from the pass geometry rather than
/// annotated per move: an XY move cuts at the tool's feed rate, a straight-DOWN move plunges at
/// the plunge rate, a straight-UP move retracts as a rapid — which is what lets one
/// <see cref="MillPass"/> vocabulary carry pockets, tabbed profiles and pecked drills alike.
/// Every pass is entered from the safe height with a rapid over its start.</para>
///
/// <para><b>Canned drilling cycles are opt-in</b> (<c>cannedDrilling: true</c>): a pass whose
/// points all share one XY — a drill — is emitted as ONE <c>G81</c> (single plunge) or
/// <c>G83 Q</c> (peck) line under <c>G98</c>, closed by <c>G80</c>, with Z/R/Q RECONSTRUCTED
/// from the pass's own moves and verified against the peck arithmetic — a pass whose bites are
/// not the uniform ladder falls back to expanded emission (sound in the accept direction, since
/// expanded is always correct). The canned spelling pecks from the R plane, so its bites sit R
/// above the expanded twin's — conservative, never deeper per bite — while the sites and the
/// final depth are identical, which is what the round-trip test asserts through the decoder.</para>
/// </summary>
public static class CncGcodeWriter
{
    /// <summary>Writes the operations as G-code, rapids above <paramref name="safeZ"/>.</summary>
    public static string Write(
        IReadOnlyList<MillOperation> operations, double safeZ = 5, bool cannedDrilling = false)
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
            bool inCycle = false;
            foreach (var pass in op.Passes)
            {
                if (cannedDrilling && TryDrillCycle(pass, out var site, out double depth,
                    out double peck, out double retract))
                {
                    if (!inCycle)
                    {
                        // The initial level G98 returns to is the position at cycle start.
                        b.Append($"G0 Z{Num(safeZ)}\n");
                        b.Append("G98 ; return to initial level\n");
                        inCycle = true;
                    }
                    int plunge = (int)Math.Round(op.Tool.PlungeRate);
                    b.Append(peck > 0
                        ? $"G83 X{Num(site.X)} Y{Num(site.Y)} Z{Num(-depth)} R{Num(retract)} "
                            + $"Q{Num(peck)} F{plunge}\n"
                        : $"G81 X{Num(site.X)} Y{Num(site.Y)} Z{Num(-depth)} R{Num(retract)} "
                            + $"F{plunge}\n");
                    lastFeed = op.Tool.PlungeRate;
                    continue;
                }
                if (inCycle)
                {
                    b.Append("G80\n");
                    inCycle = false;
                }
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
            if (inCycle)
                b.Append("G80\n");
            b.Append($"G0 Z{Num(safeZ)}\n");
        }
        b.Append("M5\n");
        b.Append("M30\n");
        return b.ToString();
    }

    /// <summary>Writes the operations to a <c>.nc</c>/<c>.gcode</c> file.</summary>
    public static void WriteFile(
        IReadOnlyList<MillOperation> operations, string path, double safeZ = 5,
        bool cannedDrilling = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        File.WriteAllText(path, Write(operations, safeZ, cannedDrilling));
    }

    /// <summary>Recognizes a drill pass and reconstructs its cycle parameters from the moves
    /// themselves: every point at ONE XY, bites descending by a uniform peck (retracting to one
    /// chip-clear height between) and ending at the final depth — exactly what
    /// <see cref="CncMill.Drill"/> builds. Anything else — an irregular ladder, a hand-built
    /// pass — is refused here and emitted expanded, which is always correct.</summary>
    private static bool TryDrillCycle(
        MillPass pass, out Vector2d site, out double depth, out double peck, out double retract)
    {
        site = default;
        depth = peck = 0;
        retract = CncMill.DrillRetract;
        if (pass.IsClosed || pass.Points.Count == 0)
            return false;
        var p0 = pass.Points[0];
        site = new Vector2d(p0.X, p0.Y);
        foreach (var p in pass.Points)
            if (p.X != p0.X || p.Y != p0.Y)
                return false;
        double bottom = pass.Points[^1].Z;
        if (!(bottom < 0))
            return false;
        depth = -bottom;
        if (pass.Points.Count == 1)
            return true;                                     // a single plunge: G81
        // A peck ladder: bites at −q, −2q, … with the chip-clear retract between, the last
        // bite at −depth. Verify against the arithmetic; any mismatch falls back.
        peck = -p0.Z;
        if (!(peck > 0))
            return false;
        for (int i = 0; i < pass.Points.Count; i++)
        {
            // The k-th bite bottoms at max(−depth, −(k+1)·peck) — the LAST point must land
            // there too, which is what refuses a ladder that skipped bites on the way down.
            double expected = i % 2 == 0
                ? Math.Max(-depth, -(i / 2 + 1) * peck)
                : CncMill.DrillRetract;
            if (Math.Abs(pass.Points[i].Z - expected) > 1e-9)
                return false;
        }
        return pass.Points.Count % 2 == 1;                   // ends on a bite, not a retract
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
