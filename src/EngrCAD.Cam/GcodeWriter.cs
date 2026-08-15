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
    public static string Write(SlicedPart part) => Write(part, header: true, tail: true);

    /// <summary>The sectioned form the SEQUENTIAL composer reads: <paramref name="header"/>
    /// covers the modes/temperatures/start-gcode preamble and <paramref name="tail"/> the
    /// cool-down postamble — both true is <see cref="Write(SlicedPart)"/> byte for byte, and
    /// a middle part of a sequential print takes neither (its handover supplies the
    /// <c>G92 E0</c> and the clearance hop).</summary>
    internal static string Write(SlicedPart part, bool header, bool tail)
    {
        ArgumentNullException.ThrowIfNull(part);
        var p = part.Profile;
        var b = new StringBuilder();
        if (header)
        {
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
            if (part.Layers.Count > 0 && p.StartGcode is { Length: > 0 })
                b.Append(Snippet(p.StartGcode, 0, part.Layers[0].Z));
        }

        double e = 0;
        int travelFeed = (int)Math.Round(p.TravelSpeed * 60);
        int retractFeed = (int)Math.Round(p.RetractionSpeed * 60);
        bool retractionOn = p.RetractionLength > 0;
        Vector2d? pen = null;

        foreach (var layer in part.Layers)
        {
            b.Append($";LAYER:{layer.Index}\n");
            if (p.LayerChangeGcode is { Length: > 0 })
                b.Append(Snippet(p.LayerChangeGcode, layer.Index, layer.Z));
            // The fan turns on once the first FanOffLayers have adhered (write-only-when-
            // stated: FanSpeed 0 writes no fan command at all).
            if (p.FanSpeed > 0 && layer.Index == p.FanOffLayers)
                b.Append($"M106 S{(int)Math.Round(p.FanSpeed * 255)}\n");
            // A vase layer's z is reached by the RAMP, not by a hop — the previous spiral
            // turn ended exactly one layer below, so the nozzle is already there.
            bool vaseLayer = p.SpiralVase
                && layer.Index >= p.RaftLayers + Math.Max(p.BottomSolidLayers, 1);
            if (!vaseLayer)
                b.Append($"G0 Z{Num(layer.Z)} F{travelFeed}\n");

            // Variable layer heights change the bead's stadium per layer, so the
            // E arithmetic reads each layer's own cross-section.
            double deToDistance = p.BeadAreaFor(layer.HeightOr(p)) / p.FilamentArea
                ;
            // Cooling: a layer quicker than MinLayerTime slows every deposition on it by
            // the same factor (floored at MinPrintSpeed), so the layer takes the stated
            // minimum and the plastic below has cooled before the next lands.
            double coolingFactor = 1;
            if (p.MinLayerTime > 0)
            {
                double layerTime = 0;
                foreach (var path in layer.Paths)
                    layerTime += path.Length / p.SpeedFor(path.Role, layer.Index);
                if (layerTime > 0 && layerTime < p.MinLayerTime)
                    coolingFactor = layerTime / p.MinLayerTime;
            }

            foreach (var path in layer.Paths)
            {
                // Travel to the path start, retracting when the hop is long enough to ooze.
                double travel = pen is { } from ? (path.Start - from).Length : double.PositiveInfinity;
                bool worthRetracting = retractionOn && e > 0
                    && travel >= p.MinTravelForRetraction;
                if (worthRetracting)
                {
                    b.Append($"G1 E{NumE(e - p.RetractionLength)} F{retractFeed}\n");
                    if (p.ZHop > 0)
                        b.Append($"G0 Z{Num(layer.Z + p.ZHop)} F{travelFeed}\n");
                }
                if (travel > 0)
                    b.Append($"G0 X{Num(path.Start.X)} Y{Num(path.Start.Y)} F{travelFeed}\n");
                if (worthRetracting)
                {
                    if (p.ZHop > 0)
                        b.Append($"G0 Z{Num(layer.Z)} F{travelFeed}\n");
                    b.Append($"G1 E{NumE(e)} F{retractFeed}\n");
                }

                // The path's feed: the profile's ONE role/layer rule, scaled by the layer's
                // cooling factor (floored), then capped by the volumetric limit — a hard
                // machine bound, so it applies LAST.
                double speed = p.SpeedFor(path.Role, layer.Index) * coolingFactor;
                if (p.MinLayerTime > 0)
                    speed = Math.Max(speed, p.MinPrintSpeed);
                if (p.MaxVolumetricFlow > 0)
                    speed = Math.Min(speed, p.MaxVolumetricFlow / p.BeadArea);
                int printFeed = (int)Math.Round(speed * 60);

                // A vase wall RAMPS z along its own arc length, ending exactly at the
                // layer's top — the continuous spiral that is the mode's whole point.
                bool ramp = vaseLayer && path.Role == SlicePathRole.Wall;
                double rampTotal = ramp ? path.Length : 0;
                double rampDone = 0;
                var previous = path.Start;
                int count = path.Points.Count + (path.IsClosed ? 1 : 0);
                for (int i = 1; i < count; i++)
                {
                    var point = path.Points[i % path.Points.Count];
                    e += (point - previous).Length * deToDistance * path.Flow;
                    string z = "";
                    if (ramp && rampTotal > 0)
                    {
                        rampDone += (point - previous).Length;
                        double layerHeight = layer.HeightOr(p);
                        z = $" Z{Num(layer.Z - layerHeight + layerHeight * rampDone / rampTotal)}";
                    }
                    b.Append($"G1 X{Num(point.X)} Y{Num(point.Y)}{z} E{NumE(e)}"
                        + (i == 1 ? $" F{printFeed}" : "") + "\n");
                    previous = point;
                }
                pen = path.End;
            }
        }

        if (tail)
        {
            if (retractionOn && e > 0)
                b.Append($"G1 E{NumE(e - p.RetractionLength)} F{retractFeed}\n");
            if (p.EndGcode is { Length: > 0 })
                b.Append(Snippet(p.EndGcode,
                    part.Layers.Count > 0 ? part.Layers[^1].Index : 0,
                    part.Layers.Count > 0 ? part.Layers[^1].Z : 0));
            if (p.FanSpeed > 0)
                b.Append("M107\n");
            if (p.HotendTemperature > 0)
                b.Append("M104 S0\n");
            if (p.BedTemperature > 0)
                b.Append("M140 S0\n");
            b.Append("M84 ; motors off\n");
        }
        return b.ToString();
    }

    /// <summary>Writes the sliced part to a <c>.gcode</c> file.</summary>
    public static void WriteFile(SlicedPart part, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        File.WriteAllText(path, Write(part));
    }

    /// <summary>A custom G-code snippet with its placeholders substituted: {layer} is
    /// the layer index, {z} the layer's top height. The text passes through otherwise —
    /// and the twin decoder still reads the file, so a snippet smuggling G91 or G20 in
    /// refuses there BY NAME rather than silently corrupting the geometry.</summary>
    private static string Snippet(string text, int layer, double z) =>
        text.Replace("{layer}", layer.ToString(CultureInfo.InvariantCulture))
            .Replace("{z}", Num(z)).TrimEnd('\n') + "\n";

    /// <summary>Coordinates at 3 decimals (a micron — below any printer's resolution).</summary>
    private static string Num(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>E at 5 decimals — cumulative, so the per-move rounding must sit well below the
    /// smallest ΔE a short segment produces.</summary>
    private static string NumE(double value) =>
        value.ToString("0.#####", CultureInfo.InvariantCulture);
}
