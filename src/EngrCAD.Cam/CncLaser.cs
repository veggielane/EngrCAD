using System.Globalization;
using System.Text;
using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Cam;

/// <summary>A laser (or drag-knife) cutter's process numbers: the KERF the beam burns away,
/// the feed (mm/min), the power as GRBL's S word (0–1000), and how many passes each path
/// repeats (thick stock cuts in repeats, not by feeding slower forever).</summary>
public sealed record LaserTool(
    double KerfWidth = 0.15, double FeedRate = 600, int Power = 800, int Passes = 1)
{
    /// <summary>Refuses an unusable tool by name.</summary>
    public void Validate()
    {
        if (!(KerfWidth > 0) || !double.IsFinite(KerfWidth))
            throw new ArgumentException($"KerfWidth must be finite and positive; got {KerfWidth:0.###}.");
        if (!(FeedRate > 0) || !double.IsFinite(FeedRate))
            throw new ArgumentException($"FeedRate must be finite and positive; got {FeedRate:0.###}.");
        if (Power is < 0 or > 1000)
            throw new ArgumentException($"Power is GRBL's S word, 0–1000; got {Power}.");
        if (Passes < 1)
            throw new ArgumentException($"Passes must be at least 1; got {Passes}.");
    }
}

/// <summary>A planned laser cut: the beam-centre paths in cut order (holes first — cutting
/// the perimeter first releases the part and the holes drift), each an XY loop at z = 0.</summary>
public sealed record LaserCut(string Name, LaserTool Tool, IReadOnlyList<MillPass> Passes)
{
    /// <summary>Total beam-path length over one repeat of every pass (mm).</summary>
    public double CutLength => Passes.Sum(p => p.CutLength);
}

/// <summary>
/// Laser / drag-knife cutting — the near-free adjacent of the 2D machinery: a part is cut
/// free of sheet stock along its outline with the KERF spent in the waste, and ONE outward
/// offset gives every path with the compensation already right — growing the region by
/// kerf/2 moves its outer loops OUT into the waste and its hole loops IN into the holes,
/// which are exactly the two beam centrelines, so the freed part measures exactly the drawn
/// dimensions with no per-loop case analysis. Holes cut FIRST (the release rule every laser
/// CAM ships: a released part is no longer held by the sheet, so anything cut after the
/// perimeter drifts).
///
/// <para>The G-code is GRBL's laser flavour: <c>M4</c> (dynamic power — the beam gates off
/// during G0 travels by the controller's own rule), one <c>S</c> power word, <c>G1</c> cuts
/// at the feed, <c>M5</c> at the end, and NO Z word anywhere — a laser has no depth axis,
/// and emitting one would make the file mean something on the wrong machine. The twin
/// decoder reads the program (S and M4 are modes, the moves are plain G0/G1), so the cut
/// length is verified through the decoded file, not the plan.</para>
/// </summary>
public static class CncLaser
{
    /// <summary>Plans the cut: one outward offset by kerf/2, hole loops first then outer
    /// loops, each a closed pass at z = 0.</summary>
    public static LaserCut Cut(Region2d region, LaserTool tool, string name = "laser")
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(tool);
        tool.Validate();

        var offsets = Region2dOffset.Offset(region, tool.KerfWidth / 2);
        var passes = new List<MillPass>();
        foreach (var shell in offsets)
        {
            foreach (var hole in shell.Holes)
                passes.Add(ToPass(hole));
            passes.Add(ToPass(shell.Outer));
        }
        if (passes.Count == 0)
            throw new ArgumentException(
                $"'{name}': the outline offset by half the kerf ({tool.KerfWidth / 2:0.###}) "
                + "left nothing to cut.");
        return new LaserCut(name, tool, passes);

        static MillPass ToPass(IReadOnlyList<Vector2d> loop)
        {
            var points = new Vector3d[loop.Count];
            for (int i = 0; i < loop.Count; i++)
                points[i] = new Vector3d(loop[i].X, loop[i].Y, 0);
            return new MillPass(points, IsClosed: true);
        }
    }

    /// <summary>Writes the cut as GRBL laser G-code (M4 dynamic power, no Z words); each
    /// pass repeats <see cref="LaserTool.Passes"/> times from its own start.</summary>
    public static string WriteGcode(LaserCut cut)
    {
        ArgumentNullException.ThrowIfNull(cut);
        var b = new StringBuilder();
        b.Append("; EngrCAD laser\n");
        b.Append("G21 ; millimetres\n");
        b.Append("G90 ; absolute coordinates\n");
        b.Append($"M4 S{cut.Tool.Power} ; dynamic power — beam off during G0\n");
        int feed = (int)Math.Round(cut.Tool.FeedRate);
        bool feedWritten = false;
        foreach (var pass in cut.Passes)
        {
            var start = pass.Points[0];
            b.Append($"G0 X{Num(start.X)} Y{Num(start.Y)}\n");
            for (int repeat = 0; repeat < cut.Tool.Passes; repeat++)
            {
                int count = pass.Points.Count + (pass.IsClosed ? 1 : 0);
                for (int i = 1; i < count; i++)
                {
                    var p = pass.Points[i % pass.Points.Count];
                    b.Append($"G1 X{Num(p.X)} Y{Num(p.Y)}");
                    if (!feedWritten)
                    {
                        b.Append($" F{feed}");
                        feedWritten = true;
                    }
                    b.Append('\n');
                }
            }
        }
        b.Append("M5\n");
        return b.ToString();
    }

    /// <summary>Writes the cut to a <c>.gcode</c>/<c>.nc</c> file.</summary>
    public static void WriteFile(LaserCut cut, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        File.WriteAllText(path, WriteGcode(cut));
    }

    private static string Num(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
