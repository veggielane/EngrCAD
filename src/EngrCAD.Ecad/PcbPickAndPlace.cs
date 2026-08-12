using System.Globalization;
using System.Text;
using EngrCAD.Core;

namespace EngrCAD.Ecad;

/// <summary>Which axis a bottom-side placement's rotation is mirrored about in a pick-and-place file —
/// a per-fabricator convention. Both are a reflection of the board-frame angle (a sign swap, never a
/// <c>cos</c>), so a quarter turn stays exact.</summary>
public enum BottomFlipAxis
{
    /// <summary>Flip about the board X axis: a bottom row's rotation is <c>(360 − rotation) mod 360</c>
    /// (i.e. −rotation). The default, and every prior emission.</summary>
    X,

    /// <summary>Flip about the board Y axis: a bottom row's rotation is <c>(180 − rotation) mod 360</c>.
    /// The other common machine convention.</summary>
    Y,
}

/// <summary>
/// One row of a pick-and-place (centroid) file — the pose a P&amp;P machine populates one component
/// by. It is a pure PROJECTION of a <see cref="PcbPlacement"/>: the reference designator, the
/// placement's board-frame <see cref="X"/>/<see cref="Y"/> (mm), the machine-facing
/// <see cref="Rotation"/> (degrees, CCW positive), the <see cref="Side"/>, and the component's
/// <see cref="Value"/> and <see cref="Package"/> identifiers a feeder is matched by.
///
/// <para><b>Rotation convention.</b> The angle is the component's orientation as the machine sees
/// it for the placement's side. A <b>top</b>-side row carries the placement's rotation verbatim.
/// A <b>bottom</b>-side row is <b>mirrored</b> — the board is flipped about its X axis to populate
/// the bottom, which negates the board-frame angle — so it is <c>(360 − rotation) mod 360</c>,
/// normalised to <c>[0, 360)</c> (a sign swap, never a <c>cos</c>). The board-frame
/// <see cref="X"/>/<see cref="Y"/> are reported VERBATIM (no flip), the repo's coordinate honesty.</para>
/// </summary>
/// <param name="Designator">The reference designator (<c>R1</c>, <c>U3</c>).</param>
/// <param name="X">The placement X in board coordinates (mm), verbatim.</param>
/// <param name="Y">The placement Y in board coordinates (mm), verbatim.</param>
/// <param name="Rotation">The machine-facing rotation (degrees, CCW positive) — see the type
/// summary for the top/bottom convention.</param>
/// <param name="Side">The board face the component sits on.</param>
/// <param name="Value">The component value (<c>"330"</c>, <c>"100nF"</c>), or the empty string.</param>
/// <param name="Package">The footprint / land-pattern identifier (<c>"R0805"</c>), the component's
/// footprint name, or — when it carries no footprint — its definition type name.</param>
public readonly record struct PickAndPlaceRow(
    string Designator, double X, double Y, double Rotation, CopperSide Side,
    string Value, string Package);

/// <summary>
/// The assembly-side fabrication output: a <b>pick-and-place (centroid) file</b> a P&amp;P machine
/// consumes to populate a board — one row per placed component (reference designator, X, Y,
/// rotation, side, value/package). It is the assembly twin of the copper
/// <see cref="PcbGerberExport">Gerber</see> / <see cref="ExcellonWriter">Excellon</see> set.
///
/// <para><b>One <see cref="Compute"/> feeds both writers</b> — the drawing-sheet rule — so a CSV
/// centroid and a KiCad-style <c>.pos</c> cannot disagree about a pose: both project the SAME row
/// list. The rows are in <b>placement (declaration) order</b> (the same order
/// <see cref="PcbLayout.PlacedPads"/> reports), so the output is a deterministic function of the
/// layout — two emissions of one layout are byte-identical.</para>
///
/// <para><b>The pose is the placement, not the 3D body.</b> A machine places by the footprint
/// origin, which is the layout's <see cref="PcbPlacement"/> pose — independent of any 3D-model
/// offset — so the row is exactly the placement (see <see cref="PickAndPlaceRow"/> for the bottom
/// mirror). Units are millimetres and degrees.</para>
///
/// <para><b>The twin-decoder oracle.</b> <see cref="ParseCsv"/> reads back what <see cref="ToCsv"/>
/// writes and recovers the designator, X, Y, rotation, side and value EXACTLY (to the file's
/// precision) — the repo's rule that a fab file must survive the round trip, not merely a
/// structural check.</para>
///
/// <para><b>One combined file or one per side.</b> <see cref="Write"/> drops the whole board as one
/// pair; <see cref="WriteBySide"/> drops a separate top and bottom pair (the assembly-house need — each
/// side is a different machine setup), one pair per POPULATED side. The split is a partition of the same
/// <see cref="Compute"/> rows filtered by side, so the union of the two side files' rows is exactly the
/// combined file's, pose for pose.</para>
/// </summary>
public static class PcbPickAndPlace
{
    // ---- the one Compute both writers project ------------------------------

    /// <summary>Projects a layout's placements into pick-and-place rows, in placement order. Every
    /// placement yields exactly one row (a body-less or footprint-less component is still placed by
    /// its pose), so the row count equals the placement count.</summary>
    /// <param name="layout">The board layout.</param>
    /// <param name="bottomFlip">How a BOTTOM-side row's rotation is mirrored — flip about the board X
    /// axis (<c>(360 − rotation) mod 360</c>, the default and every prior emission) or about the Y axis
    /// (<c>(180 − rotation) mod 360</c>). A per-fabricator convention; both are a sign swap, so a quarter
    /// turn is exact. A TOP row is the placement angle verbatim either way.</param>
    public static IReadOnlyList<PickAndPlaceRow> Compute(
        PcbLayout layout, BottomFlipAxis bottomFlip = BottomFlipAxis.X)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var rows = new List<PickAndPlaceRow>(layout.Placements.Count);
        foreach (var p in layout.Placements)
        {
            // The reference was validated at Place/AddLoadedPlacement, so Find never returns null.
            var component = layout.Schematic.Find(p.Reference)!;
            string value = component.Value;
            string package = component.Definition.Footprint?.Name ?? component.Definition.Name;

            // Top: the placement angle verbatim (a pure projection). Bottom: mirrored — the board is
            // flipped to populate it, which reflects the board-frame angle (a sign swap, not a cos):
            // about X → −rotation (the default), about Y → 180 − rotation.
            double rotation = p.Side == CopperSide.Top
                ? p.RotationDegrees
                : Normalize360(bottomFlip == BottomFlipAxis.X ? -p.RotationDegrees : 180 - p.RotationDegrees);

            rows.Add(new PickAndPlaceRow(p.Reference, p.X, p.Y, rotation, p.Side, value, package));
        }
        return rows;
    }

    // ---- CSV centroid (the machine format) ---------------------------------

    /// <summary>Writes the ubiquitous CSV centroid file for a layout — the header
    /// <c>Designator,X,Y,Rotation,Side,Value</c> followed by one row per placement.
    /// <paramref name="bottomFlip"/> chooses the bottom-side mirror axis (see
    /// <see cref="Compute(PcbLayout, BottomFlipAxis)"/>).</summary>
    public static string ToCsv(PcbLayout layout, BottomFlipAxis bottomFlip = BottomFlipAxis.X) =>
        ToCsv(Compute(layout, bottomFlip));

    /// <summary>Writes the ubiquitous CSV centroid file from computed rows — columns
    /// <c>Designator,X,Y,Rotation,Side,Value</c>. Coordinates and the rotation are written with
    /// round-trippable precision (so <see cref="ParseCsv"/> recovers them exactly), the side as
    /// <c>top</c>/<c>bottom</c>, and text fields are RFC-4180 quoted only when they carry a comma,
    /// quote or newline.</summary>
    public static string ToCsv(IReadOnlyList<PickAndPlaceRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new StringBuilder();
        sb.Append("Designator,X,Y,Rotation,Side,Value\n");
        foreach (var r in rows)
            sb.Append(CsvField(r.Designator)).Append(',')
              .Append(Round(r.X)).Append(',')
              .Append(Round(r.Y)).Append(',')
              .Append(Round(r.Rotation)).Append(',')
              .Append(SideText(r.Side)).Append(',')
              .Append(CsvField(r.Value)).Append('\n');
        return sb.ToString();
    }

    // ---- KiCad-style aligned .pos (the human format) -----------------------

    /// <summary>Writes a KiCad-style aligned <c>.pos</c> file for a layout — columns
    /// <c>Ref  Val  Package  PosX  PosY  Rot  Side</c>.</summary>
    public static string ToPos(PcbLayout layout, BottomFlipAxis bottomFlip = BottomFlipAxis.X) =>
        ToPos(Compute(layout, bottomFlip));

    /// <summary>Writes a KiCad-style aligned <c>.pos</c> file from computed rows — a metadata header
    /// stating the units and the bottom-mirror convention, then columns
    /// <c>Ref  Val  Package  PosX  PosY  Rot  Side</c> in fixed 4-decimal display precision. It is a
    /// human-readable view; the round-trip machine format is <see cref="ToCsv"/>.</summary>
    public static string ToPos(IReadOnlyList<PickAndPlaceRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        string[] headers = ["Ref", "Val", "Package", "PosX", "PosY", "Rot", "Side"];
        var cells = new List<string[]>(rows.Count);
        foreach (var r in rows)
            cells.Add(
            [
                r.Designator, Dash(r.Value), Dash(r.Package),
                Fixed(r.X), Fixed(r.Y), Fixed(r.Rotation), SideText(r.Side),
            ]);

        // Column widths from the header and every cell, so the columns line up.
        var width = new int[headers.Length];
        for (int c = 0; c < headers.Length; c++)
        {
            width[c] = headers[c].Length;
            foreach (var row in cells)
                width[c] = Math.Max(width[c], row[c].Length);
        }

        var sb = new StringBuilder();
        sb.Append("### Pick and place\n");
        sb.Append("## Unit = mm, Angle = deg (CCW positive), Side = top/bottom.\n");
        sb.Append("## Bottom-side rotation is mirrored (the board is flipped about its X axis to populate it).\n");
        AppendRow(sb, headers, width);
        foreach (var row in cells)
            AppendRow(sb, row, width);
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, string[] cells, int[] width)
    {
        for (int c = 0; c < cells.Length; c++)
        {
            sb.Append(cells[c]);
            if (c < cells.Length - 1)
                sb.Append(' ', width[c] - cells[c].Length + 2);
        }
        sb.Append('\n');
    }

    // ---- the twin decoder (the round-trip oracle) --------------------------

    /// <summary>Reads a CSV centroid file back into rows — the TWIN of <see cref="ToCsv"/>, so the
    /// round trip is provable rather than assumed. Recovers the designator, X, Y, rotation, side and
    /// value; the <see cref="PickAndPlaceRow.Package"/> is the empty string (the CSV has no Package
    /// column). Its subset is what <see cref="ToCsv"/> emits.</summary>
    /// <exception cref="FormatException">The text is not a CSV of the writer's shape — a missing or
    /// wrong header, a wrong field count, a malformed number, or an unrecognised side — each refused
    /// by name.</exception>
    public static IReadOnlyList<PickAndPlaceRow> ParseCsv(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        var records = ParseRecords(csv);
        if (records.Count == 0)
            throw new FormatException(
                "A pick-and-place CSV must begin with its header row "
                + "(Designator,X,Y,Rotation,Side,Value).");

        string[] expected = ["Designator", "X", "Y", "Rotation", "Side", "Value"];
        if (records[0].Count != expected.Length || !records[0].SequenceEqual(expected))
            throw new FormatException(
                $"Unexpected pick-and-place CSV header '{string.Join(",", records[0])}'; expected "
                + $"'{string.Join(",", expected)}'.");

        var rows = new List<PickAndPlaceRow>(records.Count - 1);
        for (int i = 1; i < records.Count; i++)
        {
            var f = records[i];
            if (f.Count != expected.Length)
                throw new FormatException(
                    $"Pick-and-place CSV row {i} has {f.Count} fields; expected {expected.Length} "
                    + "(Designator,X,Y,Rotation,Side,Value).");
            double x = ParseNum(f[1], i, "X");
            double y = ParseNum(f[2], i, "Y");
            double rot = ParseNum(f[3], i, "Rotation");
            CopperSide side = ParseSide(f[4], i);
            rows.Add(new PickAndPlaceRow(f[0], x, y, rot, side, f[5], ""));
        }
        return rows;
    }

    // ---- disk ---------------------------------------------------------------

    /// <summary>Writes the pick-and-place file set to a directory — the CSV centroid
    /// (<c>&lt;name&gt;-pos.csv</c>) and the aligned <c>.pos</c> (<c>&lt;name&gt;.pos</c>) — and
    /// returns the paths written. <paramref name="name"/> defaults to the schematic's name (or
    /// <c>pnp</c>).</summary>
    public static IReadOnlyList<string> Write(
        PcbLayout layout, string directory, string? name = null,
        BottomFlipAxis bottomFlip = BottomFlipAxis.X)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory.CreateDirectory(directory);
        string b = BaseName(layout, name);

        var rows = Compute(layout, bottomFlip);
        string csvPath = Path.Combine(directory, b + "-pos.csv");
        string posPath = Path.Combine(directory, b + ".pos");
        File.WriteAllText(csvPath, ToCsv(rows));
        File.WriteAllText(posPath, ToPos(rows));
        return [csvPath, posPath];
    }

    /// <summary>Writes SEPARATE top- and bottom-side pick-and-place file sets — the common assembly-house
    /// need, since populating each side is a different machine setup. One CSV + <c>.pos</c> pair per side
    /// that carries at least one component (<c>&lt;name&gt;-top-pos.csv</c> / <c>&lt;name&gt;-top.pos</c>
    /// and the <c>-bottom-</c> pair), and nothing for a side with no components (a single-sided board
    /// yields exactly one pair). The rows are the SAME projection <see cref="Compute"/> gives, filtered by
    /// side, so a side file's poses are byte-identical to the combined <see cref="Write"/>'s for those
    /// rows — the split is a partition, not a re-projection. Returns the paths written, top pair first.</summary>
    public static IReadOnlyList<string> WriteBySide(
        PcbLayout layout, string directory, string? name = null,
        BottomFlipAxis bottomFlip = BottomFlipAxis.X)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory.CreateDirectory(directory);
        string b = BaseName(layout, name);

        var rows = Compute(layout, bottomFlip);
        var files = new List<string>();
        foreach (var side in new[] { CopperSide.Top, CopperSide.Bottom })
        {
            var sideRows = rows.Where(r => r.Side == side).ToList();
            if (sideRows.Count == 0)
                continue;   // no components on this side — no file, an honest empty rather than a stub
            string token = side == CopperSide.Top ? "top" : "bottom";
            string csvPath = Path.Combine(directory, $"{b}-{token}-pos.csv");
            string posPath = Path.Combine(directory, $"{b}-{token}.pos");
            File.WriteAllText(csvPath, ToCsv(sideRows));
            File.WriteAllText(posPath, ToPos(sideRows));
            files.Add(csvPath);
            files.Add(posPath);
        }
        return files;
    }

    private static string BaseName(PcbLayout layout, string? name) => string.IsNullOrEmpty(name)
        ? (layout.Schematic.Name.Length > 0 ? layout.Schematic.Name : "pnp")
        : name;

    // ---- helpers ------------------------------------------------------------

    private static string SideText(CopperSide side) => side == CopperSide.Top ? "top" : "bottom";

    /// <summary>Normalises an angle to <c>[0, 360)</c> for any real input.</summary>
    private static double Normalize360(double degrees)
    {
        double r = degrees % 360.0;
        if (r < 0)
            r += 360.0;
        return r;
    }

    // Round-trippable (shortest) invariant text, with negative zero canonicalised so the file never
    // carries "-0"; double.Parse recovers the value exactly.
    private static string Round(double v) => Canonical(v).ToString(CultureInfo.InvariantCulture);

    // Fixed display precision for the aligned .pos.
    private static string Fixed(double v) => Canonical(v).ToString("0.0000", CultureInfo.InvariantCulture);

    private static double Canonical(double v) => v == 0.0 ? 0.0 : v;

    private static string Dash(string s) => string.IsNullOrEmpty(s) ? "-" : s;

    private static readonly char[] CsvSpecial = [',', '"', '\n', '\r'];

    private static string CsvField(string field)
    {
        field ??= "";
        if (field.IndexOfAny(CsvSpecial) < 0)
            return field;
        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }

    private static double ParseNum(string s, int row, string col)
    {
        if (double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return v;
        throw new FormatException($"Pick-and-place CSV row {row} has a malformed {col} value '{s}'.");
    }

    private static CopperSide ParseSide(string s, int row)
    {
        string t = s.Trim();
        if (t.Equals("top", StringComparison.OrdinalIgnoreCase))
            return CopperSide.Top;
        if (t.Equals("bottom", StringComparison.OrdinalIgnoreCase))
            return CopperSide.Bottom;
        throw new FormatException(
            $"Pick-and-place CSV row {row} has an unrecognised Side '{s}' (expected top or bottom).");
    }

    // A minimal RFC-4180 CSV tokenizer: fields split on commas, quoted fields carry embedded commas,
    // quotes (doubled) and newlines; records split on unquoted newlines. Blank lines are skipped.
    private static List<List<string>> ParseRecords(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;
        bool started = false;   // any field or character seen on the current line

        void EndField()
        {
            record.Add(field.ToString());
            field.Clear();
        }

        void EndRecord()
        {
            if (started || record.Count > 0 || field.Length > 0)
            {
                EndField();
                records.Add(record);
                record = [];
            }
            started = false;
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    started = true;
                    break;
                case ',':
                    EndField();
                    started = true;
                    break;
                case '\r':
                    break;   // ignore; the '\n' ends the record
                case '\n':
                    EndRecord();
                    break;
                default:
                    field.Append(c);
                    started = true;
                    break;
            }
        }

        if (inQuotes)
            throw new FormatException("Pick-and-place CSV ended inside a quoted field.");
        EndRecord();   // flush a final record with no trailing newline
        return records;
    }
}
