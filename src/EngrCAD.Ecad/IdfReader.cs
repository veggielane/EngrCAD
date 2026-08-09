using System.Globalization;
using System.Text;
using EngrCAD.Core;

namespace EngrCAD.Ecad;

/// <summary>The unit a IDF file declares (HEADER line 2). Coordinates are scaled into
/// millimetres on import.</summary>
public enum IdfUnits
{
    /// <summary>Millimetres (<c>MM</c>) — no scaling.</summary>
    Millimetres,

    /// <summary>Thousandths of an inch (<c>THOU</c>, mils) — scaled by 0.0254 to mm.</summary>
    Thou,
}

/// <summary>One component IDF places: its reference designator plus the package/part-number
/// strings IDF carries (IDF has no pins or nets, so a synthesized schematic gives each a
/// data-only definition named by its package).</summary>
/// <param name="Reference">The reference designator (<c>R1</c>).</param>
/// <param name="Package">The package/geometry name.</param>
/// <param name="PartNumber">The part number (may be empty).</param>
public readonly record struct IdfComponent(string Reference, string Package, string PartNumber);

/// <summary>
/// The geometry an IDF <c>.emn</c> (board) file carries: the board (outline, thickness, holes,
/// keep-outs) and the component placements. IDF has NO connectivity — no pins, no nets — so
/// <see cref="ToLayout"/> synthesizes a minimal schematic (a data-only component per placement)
/// to hold the placements against, which is honest: an IDF import is board and placement
/// geometry, and its layout's identity check will report the components have no footprints.
/// </summary>
public sealed class PcbImport
{
    internal PcbImport(
        string boardName, PcbBoard board, IReadOnlyList<IdfComponent> components,
        IReadOnlyList<PcbPlacement> placements, IdfUnits units, IReadOnlyList<string> diagnostics)
    {
        BoardName = boardName;
        Board = board;
        Components = components;
        Placements = placements;
        Units = units;
        Diagnostics = diagnostics;
    }

    /// <summary>The board name from the IDF header.</summary>
    public string BoardName { get; }

    /// <summary>The board (outline, thickness, holes, keep-outs).</summary>
    public PcbBoard Board { get; }

    /// <summary>The placed components (reference, package, part number).</summary>
    public IReadOnlyList<IdfComponent> Components { get; }

    /// <summary>The placements (reference, pose, side).</summary>
    public IReadOnlyList<PcbPlacement> Placements { get; }

    /// <summary>The unit the file declared (before scaling to mm).</summary>
    public IdfUnits Units { get; }

    /// <summary>What the reader did or could not carry — the unit scaling it applied, cutout
    /// loops it dropped, arc segments it flattened (the <c>IgesReader</c>/<c>$INSUNITS</c>
    /// "what the reader did belongs in diagnostics" convention).</summary>
    public IReadOnlyList<string> Diagnostics { get; }

    /// <summary>Builds a <see cref="PcbLayout"/> over a synthesized schematic: one data-only
    /// <see cref="Component"/> per placement (a <see cref="PartDefinition"/> named by the
    /// package, with the reference-prefix derived from the designator, and no pins/footprint,
    /// since IDF carries no connectivity). Components of one package share a definition.</summary>
    public PcbLayout ToLayout()
    {
        var schematic = new Schematic(BoardName);
        var definitions = new Dictionary<string, PartDefinition>(StringComparer.Ordinal);
        foreach (var component in Components)
        {
            if (!definitions.TryGetValue(component.Package, out var definition))
            {
                definition = new PartDefinition(component.Package, PrefixOf(component.Reference), []);
                definitions[component.Package] = definition;
            }
            schematic.Add(component.Reference, definition, component.PartNumber);
        }

        var layout = new PcbLayout(schematic, Board);
        foreach (var placement in Placements)
            layout.Place(placement.Reference, placement.X, placement.Y,
                placement.RotationDegrees, placement.Side);
        return layout;
    }

    private static string PrefixOf(string reference)
    {
        int i = 0;
        while (i < reference.Length && char.IsLetter(reference[i]))
            i++;
        return i > 0 ? reference[..i] : "X";
    }
}

/// <summary>
/// Reads IDF 3.0/4.0 board (<c>.emn</c>) files into a <see cref="PcbImport"/> — the interchange
/// half the ECAD campaign starts with (import before export). It honours the header's unit
/// declaration (MM / THOU), scaling coordinates into millimetres and recording what it did in
/// <see cref="PcbImport.Diagnostics"/> (the <c>IgesReader</c>/<c>$INSUNITS</c> lesson). The
/// section/record structure is validated up front and refused BY NAME (the
/// <c>StlReader</c>/<c>IgesReader</c> rule): a section without a matching <c>.END</c>, an
/// <c>.END</c> without an open section, or a missing <c>.HEADER</c>.
///
/// <para><b>v1 scope, stated:</b> straight-segment outlines and keep-outs (a nonzero IDF arc
/// angle is flattened to a chord, reported); outline cutout loops are dropped, reported; the
/// component library (<c>.emp</c>) is accepted but not required (component 3D extents are not
/// modelled in v1 — the schematic's own bodies are). Every board hole imports as a mounting
/// hole or via and drills the plate.</para>
/// </summary>
public static class IdfReader
{
    /// <summary>Reads an IDF board file's text.</summary>
    /// <param name="emn">The <c>.emn</c> board file text.</param>
    /// <param name="emp">The optional <c>.emp</c> library file text (accepted, not required in v1).</param>
    /// <exception cref="FormatException">The file's section structure is malformed — refused by
    /// name.</exception>
    public static PcbImport Read(string emn, string? emp = null)
    {
        ArgumentNullException.ThrowIfNull(emn);
        var sections = IdfSections.Parse(emn);
        var diagnostics = new List<string>();

        // Header: the file must open with .HEADER, whose second data line carries the board
        // name and the unit.
        var header = sections.FirstOrDefault(s => s.Name == "HEADER")
            ?? throw new FormatException("Not an IDF file: it has no .HEADER section.");
        if (sections[0].Name != "HEADER")
            throw new FormatException("An IDF file must open with the .HEADER section.");
        var (boardName, units) = ReadHeader(header);
        double scale = units == IdfUnits.Thou ? 0.0254 : 1.0;
        if (units == IdfUnits.Thou)
            diagnostics.Add("Header declared THOU; coordinates scaled to millimetres by 0.0254.");

        // Board outline: thickness then the outer loop.
        var outlineSection = sections.FirstOrDefault(s => s.Name == "BOARD_OUTLINE")
            ?? throw new FormatException("The IDF file has no .BOARD_OUTLINE section.");
        var (outline, thickness) = ReadOutline(outlineSection, scale, diagnostics);

        // Holes.
        var holes = new List<BoardHole>();
        if (sections.FirstOrDefault(s => s.Name == "DRILLED_HOLES") is { } holesSection)
            holes.AddRange(ReadHoles(holesSection, scale));

        // Keep-outs (route / place / via).
        var keepOuts = new List<KeepOut>();
        foreach (var section in sections)
        {
            var kind = section.Name switch
            {
                "VIA_KEEPOUT" => (KeepOutKind?)KeepOutKind.Via,
                "ROUTE_KEEPOUT" => KeepOutKind.Route,
                "PLACE_KEEPOUT" => KeepOutKind.Placement,
                _ => null,
            };
            if (kind is null)
                continue;
            var loop = ReadLoop(section, scale, diagnostics);
            if (loop.Count >= 3)
                keepOuts.Add(new KeepOut($"{section.Name}_{keepOuts.Count}", kind.Value, loop));
        }

        var board = new PcbBoard(outline, thickness, stackup: null, holes, keepOuts);

        // Placements.
        var components = new List<IdfComponent>();
        var placements = new List<PcbPlacement>();
        if (sections.FirstOrDefault(s => s.Name == "PLACEMENT") is { } placementSection)
            ReadPlacements(placementSection, scale, components, placements);

        if (emp is not null)
            diagnostics.Add("An .emp library was supplied; component 3D extents are not modelled in v1.");

        return new PcbImport(boardName, board, components, placements, units, diagnostics);
    }

    /// <summary>Reads an IDF board file from disk (its optional <c>.emp</c> sibling is read too
    /// when it exists next to the <c>.emn</c>).</summary>
    public static PcbImport ReadFile(string emnPath)
    {
        string emn = File.ReadAllText(emnPath);
        string empPath = Path.ChangeExtension(emnPath, ".emp");
        string? emp = File.Exists(empPath) ? File.ReadAllText(empPath) : null;
        return Read(emn, emp);
    }

    private static (string BoardName, IdfUnits Units) ReadHeader(IdfSection header)
    {
        // Line 0: BOARD_FILE <ver> "<source>" <date> <board_ver>. Line 1: "<name>" <units>.
        if (header.Body.Count < 2)
            throw new FormatException("The .HEADER section is too short (needs a name/units line).");
        var fields = IdfTokens.Split(header.Body[1]);
        if (fields.Count < 2)
            throw new FormatException(
                $"The .HEADER name/units line '{header.Body[1]}' needs a board name and a unit.");
        string name = fields[0];
        string unit = fields[1].ToUpperInvariant();
        var units = unit switch
        {
            "MM" => IdfUnits.Millimetres,
            "THOU" => IdfUnits.Thou,
            _ => throw new FormatException($"Unknown IDF unit '{fields[1]}' (expected MM or THOU)."),
        };
        return (name, units);
    }

    private static (List<Vector2d> Outline, double Thickness) ReadOutline(
        IdfSection section, double scale, List<string> diagnostics)
    {
        if (section.Body.Count < 1)
            throw new FormatException("The .BOARD_OUTLINE section is empty (needs a thickness).");
        // First data line: board thickness.
        var thicknessFields = IdfTokens.Split(section.Body[0]);
        if (thicknessFields.Count < 1 || !TryNum(thicknessFields[0], out double thickness))
            throw new FormatException(
                $"The .BOARD_OUTLINE thickness line '{section.Body[0]}' is not a number.");
        thickness *= scale;

        // Loop points: label x y angle. Label 0 is the outer outline.
        var outer = new List<Vector2d>();
        bool droppedCutout = false, flattenedArc = false;
        for (int i = 1; i < section.Body.Count; i++)
        {
            if (!TryLoopPoint(section.Body[i], scale, out int label, out Vector2d point, out double angle))
                continue;
            if (label != 0)
            {
                droppedCutout = true;
                continue;
            }
            if (angle != 0)
                flattenedArc = true;
            outer.Add(point);
        }
        if (outer.Count < 3)
            throw new FormatException("The .BOARD_OUTLINE has fewer than 3 outer-loop points.");
        if (droppedCutout)
            diagnostics.Add("Board outline cutout loops (label > 0) were dropped in v1.");
        if (flattenedArc)
            diagnostics.Add("Board outline arc segments were flattened to chords in v1.");
        return (outer, thickness);
    }

    private static IEnumerable<BoardHole> ReadHoles(IdfSection section, double scale)
    {
        foreach (var line in section.Body)
        {
            var f = IdfTokens.Split(line);
            // dia x y plating assoc_part hole_type owner
            if (f.Count < 3 || !TryNum(f[0], out double dia)
                || !TryNum(f[1], out double x) || !TryNum(f[2], out double y))
                continue;
            string type = f.Count > 5 ? f[5].ToUpperInvariant() : "";
            var kind = type == "MTG" ? BoardHoleKind.Mounting : BoardHoleKind.Via;
            yield return new BoardHole(new Vector2d(x * scale, y * scale), dia * scale, kind);
        }
    }

    private static List<Vector2d> ReadLoop(IdfSection section, double scale, List<string> diagnostics)
    {
        var loop = new List<Vector2d>();
        bool flattenedArc = false;
        foreach (var line in section.Body)
            if (TryLoopPoint(line, scale, out _, out Vector2d point, out double angle))
            {
                if (angle != 0)
                    flattenedArc = true;
                loop.Add(point);
            }
        if (flattenedArc)
            diagnostics.Add($"Keep-out {section.Name} arc segments were flattened to chords in v1.");
        return loop;
    }

    private static void ReadPlacements(
        IdfSection section, double scale, List<IdfComponent> components, List<PcbPlacement> placements)
    {
        // Two lines per component: "package" "part" refdes / x y z rot side status.
        for (int i = 0; i + 1 < section.Body.Count; i += 2)
        {
            var head = IdfTokens.Split(section.Body[i]);
            var pose = IdfTokens.Split(section.Body[i + 1]);
            if (head.Count < 3 || pose.Count < 5)
                throw new FormatException(
                    $"A .PLACEMENT record near line '{section.Body[i]}' is malformed "
                    + "(expected \"package\" \"part\" refdes / x y z rot side status).");
            string package = head[0], part = head[1], refdes = head[2];
            if (!TryNum(pose[0], out double x) || !TryNum(pose[1], out double y)
                || !TryNum(pose[3], out double rot))
                throw new FormatException(
                    $"A .PLACEMENT pose '{section.Body[i + 1]}' has non-numeric coordinates.");
            var side = pose[4].ToUpperInvariant() switch
            {
                "TOP" => CopperSide.Top,
                "BOTTOM" => CopperSide.Bottom,
                _ => throw new FormatException(
                    $"A .PLACEMENT side '{pose[4]}' is not TOP or BOTTOM."),
            };
            components.Add(new IdfComponent(refdes, package, part));
            placements.Add(new PcbPlacement(refdes, x * scale, y * scale, rot, side));
        }
    }

    // ---- field parsing --------------------------------------------------------

    private static bool TryNum(string s, out double value) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryLoopPoint(
        string line, double scale, out int label, out Vector2d point, out double angle)
    {
        label = 0; point = default; angle = 0;
        var f = IdfTokens.Split(line);
        if (f.Count < 3
            || !int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out label)
            || !TryNum(f[1], out double x) || !TryNum(f[2], out double y))
            return false;
        point = new Vector2d(x * scale, y * scale);
        if (f.Count > 3)
            TryNum(f[3], out angle);
        return true;
    }
}

/// <summary>One IDF section: its name (after the leading <c>.</c>) and its body lines (between
/// the <c>.NAME</c> and <c>.END_NAME</c> markers, comments and blanks removed).</summary>
internal sealed record IdfSection(string Name, IReadOnlyList<string> Body);

/// <summary>Splits an IDF file into sections and validates the <c>.NAME</c>/<c>.END_NAME</c>
/// nesting up front — a section without its <c>.END</c>, or an <c>.END</c> with no open section,
/// is refused BY NAME.</summary>
internal static class IdfSections
{
    internal static IReadOnlyList<IdfSection> Parse(string text)
    {
        var sections = new List<IdfSection>();
        string? open = null;
        var body = new List<string>();

        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (line[0] == '.')
            {
                string marker = line[1..].Split([' ', '\t'], 2)[0].ToUpperInvariant();
                if (marker.StartsWith("END_", StringComparison.Ordinal))
                {
                    string closing = marker[4..];
                    if (open is null)
                        throw new FormatException($"IDF .END_{closing} closes no open section.");
                    if (!string.Equals(open, closing, StringComparison.OrdinalIgnoreCase))
                        throw new FormatException(
                            $"IDF .END_{closing} does not match the open section .{open}.");
                    sections.Add(new IdfSection(open, body));
                    open = null;
                    body = [];
                }
                else
                {
                    if (open is not null)
                        throw new FormatException(
                            $"IDF section .{marker} opens before .{open} is closed with .END_{open}.");
                    open = marker;
                    body = [];
                }
            }
            else if (open is not null)
            {
                body.Add(line);
            }
        }
        if (open is not null)
            throw new FormatException($"IDF section .{open} is never closed with .END_{open}.");
        return sections;
    }
}

/// <summary>Splits an IDF data line into whitespace-separated fields, honouring
/// double-quoted strings (a package/board name may contain spaces).</summary>
internal static class IdfTokens
{
    internal static List<string> Split(string line)
    {
        var fields = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i]))
                i++;
            if (i >= line.Length)
                break;
            if (line[i] == '"')
            {
                int end = line.IndexOf('"', i + 1);
                if (end < 0)
                {
                    fields.Add(line[(i + 1)..]);
                    break;
                }
                fields.Add(line[(i + 1)..end]);
                i = end + 1;
            }
            else
            {
                int start = i;
                while (i < line.Length && !char.IsWhiteSpace(line[i]))
                    i++;
                fields.Add(line[start..i]);
            }
        }
        return fields;
    }
}
