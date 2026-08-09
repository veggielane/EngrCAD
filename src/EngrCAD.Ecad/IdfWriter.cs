using System.Globalization;
using System.Text;

namespace EngrCAD.Ecad;

/// <summary>
/// Writes a <see cref="PcbImport"/> back to a canonical IDF 3.0 <c>.emn</c> (board) file — the
/// export half that closes the interchange loop, so <c>read → write → read → write</c> is a
/// byte-identical fixed point for the geometry IDF carries (outline, thickness, holes,
/// placements, keep-outs). It writes in <b>millimetres</b> (a THOU file normalises to MM on the
/// round trip), with a deterministic header, so two writes of one import are byte-identical.
/// <para>The board name, outline, holes, placements and via/route/place keep-outs travel; the
/// synthesized keep-out names and the fixed header source/date do not, because IDF has no field
/// for them.</para>
/// </summary>
public static class IdfWriter
{
    /// <summary>Writes the import as canonical IDF board text.</summary>
    public static string Write(PcbImport import)
    {
        ArgumentNullException.ThrowIfNull(import);
        var text = new StringBuilder();

        text.AppendLine(".HEADER");
        text.AppendLine("BOARD_FILE 3.0 \"EngrCAD\" 2000/01/01.00:00:00 1");
        text.AppendLine($"\"{import.BoardName}\" MM");
        text.AppendLine(".END_HEADER");

        var board = import.Board;
        text.AppendLine(".BOARD_OUTLINE ECAD");
        text.AppendLine(Num(board.Thickness));
        WriteLoop(text, board.OutlinePoints);
        text.AppendLine(".END_BOARD_OUTLINE");

        if (board.Holes.Count > 0)
        {
            text.AppendLine(".DRILLED_HOLES");
            foreach (var hole in board.Holes)
            {
                string type = hole.Kind == BoardHoleKind.Mounting ? "MTG" : "VIA";
                text.AppendLine(
                    $"{Num(hole.Diameter)} {Num(hole.Center.X)} {Num(hole.Center.Y)} "
                    + $"PTH BOARD {type} ECAD");
            }
            text.AppendLine(".END_DRILLED_HOLES");
        }

        if (import.Placements.Count > 0)
        {
            // The import keeps Components (package/part) aligned with Placements by reference.
            var byRef = import.Components.ToDictionary(c => c.Reference, StringComparer.Ordinal);
            text.AppendLine(".PLACEMENT");
            foreach (var placement in import.Placements)
            {
                var component = byRef.TryGetValue(placement.Reference, out var c)
                    ? c : new IdfComponent(placement.Reference, "", "");
                string side = placement.Side == CopperSide.Top ? "TOP" : "BOTTOM";
                text.AppendLine($"\"{component.Package}\" \"{component.PartNumber}\" {placement.Reference}");
                text.AppendLine(
                    $"{Num(placement.X)} {Num(placement.Y)} 0 {Num(placement.RotationDegrees)} "
                    + $"{side} PLACED");
            }
            text.AppendLine(".END_PLACEMENT");
        }

        foreach (var keepOut in board.KeepOuts)
        {
            string section = keepOut.Kind switch
            {
                KeepOutKind.Via => "VIA_KEEPOUT",
                KeepOutKind.Route => "ROUTE_KEEPOUT",
                _ => "PLACE_KEEPOUT",
            };
            text.AppendLine($".{section} ECAD");
            WriteLoop(text, keepOut.Polygon);
            text.AppendLine($".END_{section}");
        }

        return text.ToString();
    }

    private static void WriteLoop(StringBuilder text, IReadOnlyList<EngrCAD.Core.Vector2d> loop)
    {
        foreach (var p in loop)
            text.AppendLine($"0 {Num(p.X)} {Num(p.Y)} 0");
        // Close the loop explicitly (IDF convention), repeating the first point.
        if (loop.Count > 0)
            text.AppendLine($"0 {Num(loop[0].X)} {Num(loop[0].Y)} 0");
    }

    private static string Num(double value) => value.ToString(CultureInfo.InvariantCulture);
}
