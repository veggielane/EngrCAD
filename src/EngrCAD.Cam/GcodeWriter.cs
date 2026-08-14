using System.Globalization;
using System.Text;
using EngrCAD.Core;

namespace EngrCAD.Cam;

/// <summary>
/// The Marlin/RepRap-flavour G-code writer — dependency-free text, the fifth hand-rolled format
/// here and the plainest. Absolute coordinates (G90), absolute extrusion (M82), millimetres
/// (G21): the modes are WRITTEN, never assumed, because the reader that cannot see them cannot
/// check them. Every E value is cumulative filament length with
/// <c>ΔE = segment length × BeadArea / FilamentArea</c> — the extrusion bookkeeping is an
/// IDENTITY the twin decoder (<see cref="GcodeReader"/>) re-derives from the decoded
/// coordinates, so a unit slip or a lost segment is caught by arithmetic, not by a print.
///
/// <para>Temperatures follow write-only-when-stated (a profile stating 0 writes no temperature
/// command); retraction fires only on travels of at least
/// <see cref="PrinterProfile.MinTravelForRetraction"/>, as a stationary negative E move paired
/// with an equal unretract, so a decoder can MATCH the pairs. Deterministic: two writes of one
/// slice are byte-identical.</para>
/// </summary>
public static class GcodeWriter
{
    /// <summary>Writes the sliced part as Marlin-flavour G-code text.</summary>
    public static string Write(SlicedPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var p = part.Profile;
        var b = new StringBuilder();
        b.Append("; EngrCAD FDM slice\n");
        b.Append($"; layers: {part.Layers.Count}, layer height: {Num(p.LayerHeight)}, "
            + $"bead: {Num(p.ResolvedBeadWidth)}\n");
        b.Append("M82 ; absolute extrusion\n");
        b.Append("G21 ; millimetres\n");
        b.Append("G90 ; absolute coordinates\n");
        if (p.BedTemperature > 0)
            b.Append($"M140 S{p.BedTemperature}\n");
        if (p.HotendTemperature > 0)
            b.Append($"M104 S{p.HotendTemperature}\n");
        if (p.BedTemperature > 0)
            b.Append($"M190 S{p.BedTemperature}\n");
        if (p.HotendTemperature > 0)
            b.Append($"M109 S{p.HotendTemperature}\n");
        b.Append("G92 E0\n");

        double e = 0;
        double deToDistance = p.BeadArea / p.FilamentArea;
        int travelFeed = (int)Math.Round(p.TravelSpeed * 60);
        int printFeed = (int)Math.Round(p.PrintSpeed * 60);
        int retractFeed = (int)Math.Round(p.RetractionSpeed * 60);
        bool retractionOn = p.RetractionLength > 0;
        Vector2d? pen = null;

        foreach (var layer in part.Layers)
        {
            b.Append($";LAYER:{layer.Index}\n");
            b.Append($"G0 Z{Num(layer.Z)} F{travelFeed}\n");
            foreach (var path in layer.Paths)
            {
                // Travel to the path start, retracting when the hop is long enough to ooze.
                double travel = pen is { } from ? (path.Start - from).Length : double.PositiveInfinity;
                bool worthRetracting = retractionOn && e > 0
                    && travel >= p.MinTravelForRetraction;
                if (worthRetracting)
                    b.Append($"G1 E{NumE(e - p.RetractionLength)} F{retractFeed}\n");
                if (travel > 0)
                    b.Append($"G0 X{Num(path.Start.X)} Y{Num(path.Start.Y)} F{travelFeed}\n");
                if (worthRetracting)
                    b.Append($"G1 E{NumE(e)} F{retractFeed}\n");

                var previous = path.Start;
                int count = path.Points.Count + (path.IsClosed ? 1 : 0);
                for (int i = 1; i < count; i++)
                {
                    var point = path.Points[i % path.Points.Count];
                    e += (point - previous).Length * deToDistance;
                    b.Append($"G1 X{Num(point.X)} Y{Num(point.Y)} E{NumE(e)}"
                        + (i == 1 ? $" F{printFeed}" : "") + "\n");
                    previous = point;
                }
                pen = path.End;
            }
        }

        if (retractionOn && e > 0)
            b.Append($"G1 E{NumE(e - p.RetractionLength)} F{retractFeed}\n");
        if (p.HotendTemperature > 0)
            b.Append("M104 S0\n");
        if (p.BedTemperature > 0)
            b.Append("M140 S0\n");
        b.Append("M84 ; motors off\n");
        return b.ToString();
    }

    /// <summary>Writes the sliced part to a <c>.gcode</c> file.</summary>
    public static void WriteFile(SlicedPart part, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        File.WriteAllText(path, Write(part));
    }

    /// <summary>Coordinates at 3 decimals (a micron — below any printer's resolution).</summary>
    private static string Num(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>E at 5 decimals — cumulative, so the per-move rounding must sit well below the
    /// smallest ΔE a short segment produces.</summary>
    private static string NumE(double value) =>
        value.ToString("0.#####", CultureInfo.InvariantCulture);
}
