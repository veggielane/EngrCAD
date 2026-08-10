using System.Globalization;
using System.Text;
using EngrCAD.Core;

namespace EngrCAD.Ecad;

/// <summary>One drilled hole in an NC-drill program: a centre and a finished diameter (mm). The
/// numbers are STATED (a placement, a board hole, a via), so the round-trip recovers them exactly to
/// the file's precision.</summary>
/// <param name="Center">The hole centre in board-local 2D coordinates (mm).</param>
/// <param name="Diameter">The finished drilled diameter (mm).</param>
public readonly record struct DrillHit(Vector2d Center, double Diameter);

/// <summary>
/// The Excellon NC-drill writer (and its twin decoder): a tool table — one tool per distinct drill
/// diameter — followed by the hits (`X…Y…`) grouped by tool. Deterministic: tools are ordered by
/// diameter and hits keep their source order, so one board yields byte-identical Excellon.
///
/// <para>Metric, absolute, with DECIMAL-POINT coordinates so any value round-trips to the file's
/// precision — the same coordinate resolution as the Gerber, so a scaled board's drill hits recover
/// as faithfully as its copper.</para>
///
/// <para>v1 does NOT split plated from non-plated holes — the copper model does not distinguish them
/// at the drill (a mounting hole, a via and a through-hole pad are all <c>DrilledHole</c>s), so all
/// holes ride in one program (the "else all holes" contract).</para>
/// </summary>
public static class ExcellonWriter
{
    /// <summary>Writes an Excellon NC-drill program for the given hits (millimetres). The
    /// <paramref name="format"/> fixes the coordinate precision (shared with the Gerber export so the
    /// two agree). An empty hit list still writes a well-formed (header + `M30`) program.</summary>
    public static string Write(IReadOnlyList<DrillHit> hits, GerberFormat format)
    {
        ArgumentNullException.ThrowIfNull(hits);

        // One tool per distinct diameter (rounded to the coordinate grid so bit-equal diameters share
        // a tool), ordered by diameter for determinism.
        var byDiameter = new SortedDictionary<double, int>();
        foreach (var hit in hits)
            byDiameter.TryAdd(Round(hit.Diameter, format), 0);
        int t = 1;
        var tool = new Dictionary<double, int>();
        foreach (var d in byDiameter.Keys)
            tool[d] = t++;

        var sb = new StringBuilder();
        sb.Append("M48\n");
        sb.Append("; EngrCAD Excellon drill file\n");
        sb.Append("; FORMAT={-:-/ absolute / metric / decimal}\n");
        sb.Append("FMAT,2\n");
        sb.Append("METRIC\n");
        foreach (var (d, code) in tool.OrderBy(kv => kv.Value))
            sb.Append('T').Append(code).Append('C').Append(format.Decimal(d)).Append('\n');
        sb.Append("%\n");
        sb.Append("G90\n");   // absolute
        sb.Append("G05\n");   // drill mode

        foreach (var (d, code) in tool.OrderBy(kv => kv.Value))
        {
            sb.Append('T').Append(code).Append('\n');
            foreach (var hit in hits)
                if (Round(hit.Diameter, format) == d)
                    sb.Append('X').Append(format.Decimal(hit.Center.X))
                      .Append('Y').Append(format.Decimal(hit.Center.Y)).Append('\n');
        }

        sb.Append("T0\n");
        sb.Append("M30\n");
        return sb.ToString();
    }

    private static double Round(double value, GerberFormat format) => format.Decode(format.Encode(value));
}

/// <summary>
/// The Excellon NC-drill decoder — the TWIN of <see cref="ExcellonWriter"/>, so the drill round-trip
/// is provable rather than assumed. Its subset is what the writer emits (a metric, decimal-coordinate
/// program); anything outside it is refused BY NAME.
/// </summary>
public static class ExcellonReader
{
    /// <summary>Decodes an Excellon program back into its hits.</summary>
    /// <exception cref="FormatException">The program is not a well-formed Excellon of the writer's
    /// subset — a missing header, a truncated file, a hit before any tool is selected, an undefined
    /// tool, or a malformed coordinate — each refused by name.</exception>
    public static IReadOnlyList<DrillHit> Read(string excellon)
    {
        ArgumentNullException.ThrowIfNull(excellon);

        var tools = new Dictionary<int, double>();
        var hits = new List<DrillHit>();
        int current = -1;
        bool sawHeader = false;
        bool ended = false;
        double x = 0, y = 0;

        foreach (var raw in excellon.Split('\n'))
        {
            string line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line[0] == ';')
                continue;
            if (line == "M48")
            {
                sawHeader = true;
                continue;
            }
            if (line is "M30" or "M00")
            {
                ended = true;
                continue;
            }
            if (line is "%" or "FMAT,2" or "METRIC" or "G90" or "G05" or "G00" or "G01")
                continue;
            if (line.StartsWith("INCH", StringComparison.Ordinal))
                throw new FormatException(
                    "This Excellon reader accepts millimetre programs only (METRIC); it is the "
                    + "round-trip decoder for an exporter that always writes millimetres.");

            if (line[0] == 'T')
            {
                ParseTool(line, tools, ref current);
                continue;
            }
            if (line[0] is 'X' or 'Y')
            {
                if (current < 0)
                    throw new FormatException("An Excellon hit was given before any tool was selected.");
                ParseHit(line, ref x, ref y);
                hits.Add(new DrillHit(new Vector2d(x, y), tools[current]));
                continue;
            }
            throw new FormatException($"Unrecognised Excellon line '{line}'.");
        }

        if (!sawHeader)
            throw new FormatException("An Excellon program must begin with its header command (M48).");
        if (!ended)
            throw new FormatException("An Excellon program ended without its end command (M30).");
        return hits;
    }

    private static void ParseTool(string line, Dictionary<int, double> tools, ref int current)
    {
        // T{n}         — select tool n (n may be 0 to unselect)
        // T{n}C{d}     — define tool n with diameter d
        int p = 1;
        int start = p;
        while (p < line.Length && char.IsDigit(line[p]))
            p++;
        if (p == start)
            throw new FormatException($"Malformed Excellon tool command '{line}'.");
        int code = int.Parse(line[start..p], CultureInfo.InvariantCulture);
        int c = line.IndexOf('C', p);
        if (c >= 0)
        {
            if (!double.TryParse(line[(c + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                throw new FormatException($"Malformed Excellon tool diameter in '{line}'.");
            tools[code] = d;
        }
        else
        {
            if (code != 0 && !tools.ContainsKey(code))
                throw new FormatException($"Excellon selects tool T{code}, which was never defined.");
            current = code;
        }
    }

    private static void ParseHit(string line, ref double x, ref double y)
    {
        int p = 0;
        while (p < line.Length)
        {
            char c = line[p++];
            int start = p;
            if (p < line.Length && (line[p] == '+' || line[p] == '-'))
                p++;
            while (p < line.Length && (char.IsDigit(line[p]) || line[p] == '.'))
                p++;
            string num = line[start..p];
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                throw new FormatException($"Malformed Excellon coordinate '{c}{num}' in '{line}'.");
            switch (c)
            {
                case 'X': x = v; break;
                case 'Y': y = v; break;
                default:
                    throw new FormatException($"Unrecognised Excellon coordinate token '{c}' in '{line}'.");
            }
        }
    }
}
