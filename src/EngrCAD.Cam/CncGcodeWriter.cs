using System.Globalization;
using System.Text;
using EngrCAD.Core;

namespace EngrCAD.Cam;

/// <summary>
/// The CNC G-code writer (GRBL/LinuxCNC-style words) — the FDM writer's milling sibling over the
/// same conventions: modes STATED (G21/G90), plain G0/G1 by default (drilling ships EXPANDED
/// peck moves, so the twin decoder <see cref="GcodeReader"/> reads every program this writer
/// emits), with opt-in canned drilling cycles and opt-in G2/G3 arc fitting, deterministic byte-for-byte.
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
    /// <summary>Writes the operations as G-code, rapids above <paramref name="safeZ"/>.
    /// <para><b>Arc fitting is opt-in</b> (<paramref name="arcFitting"/>): runs of four or
    /// more consecutive constant-z points EQUIDISTANT from a common centre are emitted as
    /// one <c>G2</c>/<c>G3</c> (I/J centre-offset form) instead of the chord run — and this
    /// RECOVERS geometry rather than approximating it, because the offset machinery's round
    /// joins place their polyline vertices INSCRIBED on the true tool-compensated arc, so
    /// the circle through them IS the arc the chording lost. Off is byte-identical.</para></summary>
    public static string Write(
        IReadOnlyList<MillOperation> operations, double safeZ = 5, bool cannedDrilling = false,
        bool arcFitting = false)
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

                if (arcFitting)
                {
                    EmitFitted(b, pass, op.Tool, ref lastFeed);
                }
                else
                {
                    var previous = start;
                    int count = pass.Points.Count + (pass.IsClosed ? 1 : 0);
                    for (int i = 1; i < count; i++)
                    {
                        var point = pass.Points[i % pass.Points.Count];
                        Move(b, previous, point, op.Tool, ref lastFeed);
                        previous = point;
                    }
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

    /// <summary>Emits a pass with arc fitting: greedy maximal runs of constant-z points on
    /// one circle become a single G2/G3, everything else stays the classified line move.
    /// The on-circle test is the weld tier (relative in the radius), because the candidate
    /// points come from geometry that placed them on the circle EXACTLY up to rounding — a
    /// looser tolerance would start inventing arcs through genuinely polygonal corners.</summary>
    private static void EmitFitted(
        StringBuilder b, MillPass pass, MillTool tool, ref double lastFeed)
    {
        int count = pass.Points.Count + (pass.IsClosed ? 1 : 0);
        var points = new Vector3d[count];
        for (int i = 0; i < count; i++)
            points[i] = pass.Points[i % pass.Points.Count];

        int index = 1;
        while (index < count)
        {
            // Try an arc starting at points[index − 1]: needs at least three chords ahead,
            // all at one z, whose circumcircle the run stays on.
            if (TryFitArc(points, index - 1, out int end, out double cx, out double cy,
                out bool clockwise))
            {
                var from = points[index - 1];
                var to = points[end];
                double feed = tool.FeedRate;
                b.Append(clockwise ? "G2" : "G3");
                b.Append($" X{Num(to.X)} Y{Num(to.Y)}");
                b.Append($" I{NumIj(cx - from.X)} J{NumIj(cy - from.Y)}");
                if (feed != lastFeed)
                {
                    b.Append($" F{(int)Math.Round(feed)}");
                    lastFeed = feed;
                }
                b.Append('\n');
                index = end + 1;
                continue;
            }
            Move(b, points[index - 1], points[index], tool, ref lastFeed);
            index++;
        }
    }

    /// <summary>Fits the longest arc starting at <paramref name="start"/>: the circumcircle
    /// of the first three points, extended while further points stay ON it (weld tier,
    /// relative in the radius), keep one z, keep one turn direction, accumulate under a full
    /// sweep AND keep each chord's sagitta under the file's own coordinate quantum. True only
    /// when at least three chords fit (four points) — shorter runs are not evidence of an arc.
    /// <para><b>The sagitta cap is what the on-circle test cannot supply</b>, and the failing
    /// case is the recorded mirror-symmetric-fixture trap live in production: IEEE negation
    /// is exact, so two points and their y-mirrors are EXACTLY concyclic, and a symmetric
    /// part's straight side flanked by its two tangency vertices reads as four points on a
    /// 675 mm circle — genuinely on it, to the bit. What is wrong is the chord BETWEEN them:
    /// the fitted arc bulges 0.027 mm across the 12 mm straight side, a real gouge. So each
    /// accepted step also caps the arc's rise over its own chord at 1e-3 — the writer states
    /// coordinates at three decimals, so under that cap the substitution is invisible at the
    /// file's own resolution, and over it the fit is inventing geometry the polyline never
    /// stated (a genuine inscribed corner arc measures ~3e-4 here, the phantom 27×).</para></summary>
    private static bool TryFitArc(
        Vector3d[] points, int start, out int end, out double cx, out double cy,
        out bool clockwise)
    {
        end = start;
        cx = cy = 0;
        clockwise = false;
        if (start + 3 >= points.Length)
            return false;
        var a = points[start];
        var m = points[start + 1];
        var c = points[start + 2];
        if (m.Z != a.Z || c.Z != a.Z)
            return false;
        // Circumcentre; a collinear triple (the sine guard, dimensionless) is not an arc.
        double abx = m.X - a.X, aby = m.Y - a.Y;
        double acx = c.X - a.X, acy = c.Y - a.Y;
        double cross = abx * acy - aby * acx;
        double scale = Math.Sqrt((abx * abx + aby * aby) * (acx * acx + acy * acy));
        if (scale <= 0 || Math.Abs(cross) <= 1e-9 * scale)
            return false;
        double ab2 = abx * abx + aby * aby;
        double ac2 = acx * acx + acy * acy;
        double inv = 0.5 / cross;
        cx = a.X + (acy * ab2 - aby * ac2) * inv;
        cy = a.Y + (abx * ac2 - acx * ab2) * inv;
        double radius = Math.Sqrt((a.X - cx) * (a.X - cx) + (a.Y - cy) * (a.Y - cy));
        double tolerance = Math.Max(1e-9, 1e-9 * radius);
        clockwise = cross < 0;

        double sweep = 0;
        double previousAngle = Math.Atan2(a.Y - cy, a.X - cx);
        int k = start + 1;
        for (; k < points.Length; k++)
        {
            var p = points[k];
            if (p.Z != a.Z)
                break;
            double d = Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
            if (Math.Abs(d - radius) > tolerance)
                break;
            double angle = Math.Atan2(p.Y - cy, p.X - cx);
            double step = angle - previousAngle;
            while (step > Math.PI) step -= 2 * Math.PI;
            while (step < -Math.PI) step += 2 * Math.PI;
            if (clockwise ? step >= 0 : step <= 0)
                break;                                       // the turn direction flipped
            if (Math.Abs(sweep + step) >= 2 * Math.PI - 1e-9)
                break;                                       // never a full ambiguous circle
            if (radius * (1 - Math.Cos(step / 2)) > 1e-3)
                break;                                       // the sagitta cap (see above)
            sweep += step;
            previousAngle = angle;
        }
        end = k - 1;
        return end - start >= 3;
    }

    /// <summary>I/J at five decimals — the centre offset must reproduce the radius the
    /// controller recomputes from both endpoints, so it carries more precision than the
    /// micron coordinate words.</summary>
    private static string NumIj(double value) =>
        value.ToString("0.#####", CultureInfo.InvariantCulture);

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
