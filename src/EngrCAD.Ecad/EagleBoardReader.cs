using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using EngrCAD.Core;

namespace EngrCAD.Ecad;

/// <summary>The result of reading a whole Eagle board — the reconstructed layout plus the reader's
/// diagnostics (the <c>KiCadPcb</c> convention).</summary>
/// <param name="Layout">The reconstructed board layout (board + placements + copper).</param>
/// <param name="Diagnostics">What was skipped, approximated, or looked wrong — never thrown for
/// per-element dirt (the readers-never-throw-on-dirty culture).</param>
public sealed record EagleBoard(PcbLayout Layout, IReadOnlyList<string> Diagnostics);

/// <summary>
/// Whole Eagle <c>.brd</c> board import — the board twin of <see cref="EagleSchematicReader"/>, and
/// like it a RESOLUTION rather than a reconstruction: an Eagle signal DECLARES its terminals
/// (<c>&lt;contactref element pad&gt;</c>), so the synthesized schematic's nets are the file's own
/// intent and the imported copper (tracks, vias) can then be CHECKED against it — the
/// <see cref="PcbConnectivity">connectivity engine</see> confirming that the routed wires really do
/// join the declared pads is the import's strong oracle, not an assumption.
///
/// <para><b>Elements reference PACKAGES directly</b> (unlike a schematic's deviceset/device), so a
/// placed part becomes a data-only <see cref="PartDefinition"/> whose pins are the package's own pad
/// names — the <c>KiCadPcbReader</c> pattern verbatim, with packages resolved through the board's
/// embedded <c>&lt;libraries&gt;</c> via the shared <see cref="EagleLibraryReader.ReadLibraryElement"/>.
/// A rotation is <c>R&lt;deg&gt;</c>, with a leading <c>M</c> meaning MIRRORED — placed on the BOTTOM
/// side (the angle carried as stated).</para>
///
/// <para><b>The covered copper subset is the two-layer board</b>: signal wires on Eagle layers 1
/// (Top) and 16 (Bottom) become traces, <c>&lt;via&gt;</c>s become through-vias (an absent via
/// diameter takes Eagle's own auto-restring rule, pad = drill + 2·max(25% drill, 0.254 mm) — a
/// ⚠ transcribed nominal), and the outline is the chained layer-20 (Dimension) wires of
/// <c>&lt;plain&gt;</c>. Airwires (layer 19, the ratsnest), inner-layer wires, signal polygons
/// (pours) and curved wires are reported and skipped/flattened by name. The board thickness is not
/// stated in a <c>.brd</c> (it lives in the fab profile), so 1.6 mm is assumed with a note.</para>
///
/// <para><b>Refused BY NAME</b>: malformed XML, a non-<c>&lt;eagle&gt;</c> root, a
/// <c>.lbr</c>/<c>.sch</c> handed here, a missing/unclosed outline. Reported, never thrown: an
/// element whose library/package is absent, a contactref to an unknown element/pad, a zero-width
/// wire, an unsupported layer.</para>
/// </summary>
public static class EagleBoardReader
{
    private const double Weld = 1e-6;          // outline chaining tolerance (exact-decimal file coords)
    private const double DefaultThickness = 1.6;

    /// <summary>Reads an Eagle <c>.brd</c> file's XML text.</summary>
    /// <exception cref="FormatException">The XML is malformed, the root is not <c>&lt;eagle&gt;</c>,
    /// the file is a library / schematic rather than a board, or the board has no closed layer-20
    /// outline — refused by name.</exception>
    public static EagleBoard Read(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException ex)
        {
            throw new FormatException($"Not a valid Eagle .brd: the XML is malformed — {ex.Message}", ex);
        }

        var root = document.Root
            ?? throw new FormatException("The Eagle .brd XML has no root element.");
        if (!string.Equals(root.Name.LocalName, "eagle", StringComparison.Ordinal))
            throw new FormatException(
                $"Not an Eagle file: the root element is '{root.Name.LocalName}', expected 'eagle'.");

        var drawing = root.Element("drawing")
            ?? throw new FormatException(
                "The Eagle file has no <drawing> — it is not a well-formed Eagle document.");
        var boardElement = drawing.Element("board");
        if (boardElement is null)
        {
            string kind = drawing.Element("schematic") is not null
                ? "a schematic (.sch) — use EagleSchematicReader for whole-schematic import"
                : drawing.Element("library") is not null
                    ? "a component library (.lbr) — use EagleLibraryReader for libraries"
                    : "not a board";
            throw new FormatException(
                $"This Eagle file has no <board>: it is {kind}. Only Eagle boards (.brd) are read here.");
        }

        var diagnostics = new List<string>();
        var once = new HashSet<string>(StringComparer.Ordinal);
        void Note(string message) { if (once.Add(message)) diagnostics.Add(message); }

        Note($"An Eagle .brd states no board thickness (it lives in the fab profile); "
            + $"{DefaultThickness.ToString("0.0#", CultureInfo.InvariantCulture)} mm was assumed.");

        // ---- libraries (packages are what a board consumes) -------------------
        var libraries = new Dictionary<string, EagleLibrary>(StringComparer.Ordinal);
        var librariesElement = boardElement.Element("libraries");
        if (librariesElement is not null)
            foreach (var libraryElement in librariesElement.Elements("library"))
            {
                string? libName = libraryElement.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(libName))
                {
                    Note("An embedded <library> has no name and was skipped.");
                    continue;
                }
                var library = EagleLibraryReader.ReadLibraryElement(libraryElement, libName);
                libraries[libName] = library;
                foreach (var d in library.Diagnostics)
                    Note($"Library '{libName}': {d}");
            }

        // ---- the outline: chained layer-20 (Dimension) wires of <plain> -------
        var outline = ReadOutline(boardElement, Note);

        // ---- elements -> data-only definitions + placements -------------------
        var schematic = new Schematic("");
        var partDefs = new Dictionary<(string, string), PartDefinition>();
        var placements = new List<(string Name, double X, double Y, double Angle, CopperSide Side)>();
        var elementsElement = boardElement.Element("elements");
        if (elementsElement is not null)
            foreach (var element in elementsElement.Elements("element"))
            {
                string? name = element.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(name))
                {
                    Note("An <element> has no name and was skipped.");
                    continue;
                }
                string libName = element.Attribute("library")?.Value ?? "";
                string packageName = element.Attribute("package")?.Value ?? "";
                if (!libraries.TryGetValue(libName, out var library))
                {
                    Note($"Element '{name}' references library '{libName}', which the board does "
                        + "not embed; the element was skipped.");
                    continue;
                }
                var package = library.Package(packageName);
                if (package is null)
                {
                    Note($"Element '{name}' references package '{packageName}', which library "
                        + $"'{libName}' does not contain; the element was skipped.");
                    continue;
                }

                var key = (libName, packageName);
                if (!partDefs.TryGetValue(key, out var definition))
                {
                    var footprint = new Footprint(package.Name, package.Pads);
                    partDefs[key] = definition = new PartDefinition(
                        packageName, PrefixOf(name), PinsOf(footprint), footprint);
                    foreach (var d in package.Diagnostics)
                        Note($"Package '{packageName}': {d}");
                }

                double x = Num(element, "x"), y = Num(element, "y");
                var (angle, mirrored) = ParseRotation(element.Attribute("rot")?.Value, name, Note);
                schematic.Add(name, definition, element.Attribute("value")?.Value ?? "");
                placements.Add((name, x, y, angle, mirrored ? CopperSide.Bottom : CopperSide.Top));
            }

        // ---- signals: DECLARED terminals + the copper that should join them ----
        var netOrder = new List<string>();
        var netToPins = new Dictionary<string, List<PinRef>>(StringComparer.Ordinal);
        var tracks = new List<(string Net, string Layer, double Width, Vector2d A, Vector2d B)>();
        var vias = new List<(string Net, double X, double Y, double Drill, double Pad)>();

        var thickness = DefaultThickness;
        var stackup = PcbStackup.TwoLayer(thickness);
        string top = stackup.Top.Name, bottom = stackup.Bottom.Name;

        var signalsElement = boardElement.Element("signals");
        if (signalsElement is not null)
            foreach (var signal in signalsElement.Elements("signal"))
            {
                string? netName = signal.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(netName))
                {
                    Note("A <signal> has no name and was skipped.");
                    continue;
                }

                foreach (var contactref in signal.Elements("contactref"))
                    ResolveContactref(contactref, netName, schematic, netOrder, netToPins, Note);

                foreach (var wire in signal.Elements("wire"))
                {
                    int layer = (int)Num(wire, "layer");
                    string? copper = layer switch { 1 => top, 16 => bottom, _ => null };
                    if (copper is null)
                    {
                        if (layer == 19)
                            Note("Airwires (layer 19, the unrouted ratsnest) were skipped — they "
                                + "are intent, not copper; the contactrefs already carry it.");
                        else
                            Note($"A signal wire on Eagle layer {layer} was skipped (the covered "
                                + "copper subset is the two-layer board: layers 1 and 16).");
                        continue;
                    }
                    double width = Num(wire, "width");
                    if (!(width > 0))
                    {
                        Note($"A zero-width signal wire on net '{netName}' was skipped.");
                        continue;
                    }
                    if (wire.Attribute("curve") is not null)
                        Note($"A curved signal wire on net '{netName}' was flattened to its chord.");
                    tracks.Add((netName, copper,
                        width,
                        new Vector2d(Num(wire, "x1"), Num(wire, "y1")),
                        new Vector2d(Num(wire, "x2"), Num(wire, "y2"))));
                }

                foreach (var via in signal.Elements("via"))
                {
                    string extent = via.Attribute("extent")?.Value ?? "1-16";
                    if (!string.Equals(extent, "1-16", StringComparison.Ordinal))
                        Note($"A via with extent '{extent}' on net '{netName}' was imported as a "
                            + "THROUGH via (the covered subset is the two-layer board).");
                    double drill = Num(via, "drill");
                    // An absent diameter means Eagle's auto restring: 25% of the drill per side,
                    // clamped to at least 10 mil (0.254 mm) — ⚠ transcribed nominal.
                    double pad = via.Attribute("diameter") is { } d
                        ? double.Parse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture)
                        : drill + 2 * Math.Max(0.25 * drill, 0.254);
                    vias.Add((netName, Num(via, "x"), Num(via, "y"), drill, pad));
                }

                if (signal.Element("polygon") is not null)
                    Note($"A signal polygon (copper pour) on net '{netName}' was skipped — pour "
                        + "import from .brd is filed.");
            }

        foreach (var net in netOrder)
        {
            var pins = netToPins[net];
            if (pins.Count >= 2)
                schematic.Connect(net, pins);
            else
                schematic.Stub(net, pins[0]);
        }

        // A signal with copper but no resolvable terminal declares no net for the copper to carry
        // (a pad-less stitching net, or contactrefs that all failed to resolve) — reported, and its
        // copper skipped, rather than thrown three calls later by the layout's own unknown-net gate.
        int orphanTracks = tracks.RemoveAll(t => !netToPins.ContainsKey(t.Net));
        int orphanVias = vias.RemoveAll(v => !netToPins.ContainsKey(v.Net));
        if (orphanTracks + orphanVias > 0)
            Note("A signal with copper but no resolvable contactref was skipped — its net has no "
                + "terminals to carry.");

        // ---- assemble ---------------------------------------------------------
        var board = new PcbBoard(outline, thickness, stackup);
        var layout = new PcbLayout(schematic, board);
        foreach (var (name, x, y, angle, side) in placements)
            layout.Place(name, x, y, angle, side);
        foreach (var (net, layer, width, a, b) in tracks)
            layout.AddTrace(net, layer, width, [a, b]);
        foreach (var (net, x, y, drill, pad) in vias)
            layout.AddVia(net, x, y, top, bottom, drill, pad);

        return new EagleBoard(layout, diagnostics);
    }

    /// <summary>Reads an Eagle <c>.brd</c> file from disk.</summary>
    public static EagleBoard ReadFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Read(File.ReadAllText(path));
    }

    // ---- helpers -------------------------------------------------------------

    private static double Num(XElement element, string attribute) =>
        element.Attribute(attribute) is { } a
            ? double.Parse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture)
            : 0;

    private static string PrefixOf(string refDes)
    {
        int i = 0;
        while (i < refDes.Length && char.IsLetter(refDes[i])) i++;
        return i > 0 ? refDes[..i] : "U";
    }

    private static IEnumerable<Pin> PinsOf(Footprint footprint)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pad in footprint.Pads)
            if (pad.Number.Length > 0 && seen.Add(pad.Number))
                yield return new Pin(pad.Number, PinType.Passive);
    }

    /// <summary>Parses an Eagle rotation token: <c>R&lt;deg&gt;</c>, with optional leading
    /// <c>M</c> (mirrored → the BOTTOM side) and <c>S</c> (spin — the text-orientation flag, carried
    /// as the same angle).</summary>
    private static (double Angle, bool Mirrored) ParseRotation(
        string? rot, string element, Action<string> note)
    {
        if (string.IsNullOrEmpty(rot))
            return (0, false);
        string s = rot;
        bool mirrored = false;
        if (s.StartsWith('M')) { mirrored = true; s = s[1..]; }
        if (s.StartsWith('S')) s = s[1..];
        if (s.StartsWith('R')
            && double.TryParse(s[1..], NumberStyles.Float, CultureInfo.InvariantCulture, out double angle))
            return (angle, mirrored);
        note($"Element '{element}' has an unrecognised rotation '{rot}'; 0° was used.");
        return (0, mirrored);
    }

    private static void ResolveContactref(
        XElement contactref, string netName, Schematic schematic,
        List<string> netOrder, Dictionary<string, List<PinRef>> netToPins, Action<string> note)
    {
        string? element = contactref.Attribute("element")?.Value;
        string? pad = contactref.Attribute("pad")?.Value;
        if (string.IsNullOrEmpty(element) || string.IsNullOrEmpty(pad))
        {
            note($"Signal '{netName}' has a contactref with no element or pad; it was skipped.");
            return;
        }
        var component = schematic.Find(element);
        if (component is null)
        {
            note($"Signal '{netName}' references element '{element}', which was skipped or not "
                + "declared; the contactref was skipped.");
            return;
        }
        if (!component.Definition.HasPin(pad))
        {
            note($"Signal '{netName}' references pad '{pad}' of element '{element}', which its "
                + $"package ('{component.Definition.Name}') does not have; the contactref was skipped.");
            return;
        }
        if (!netToPins.TryGetValue(netName, out var pins))
        {
            netToPins[netName] = pins = [];
            netOrder.Add(netName);
        }
        var pin = component.Pin(pad);
        if (!pins.Contains(pin))
            pins.Add(pin);
    }

    /// <summary>The board outline: the layer-20 (Dimension) wires of <c>&lt;plain&gt;</c>, CHAINED
    /// end to end into one closed loop (the segments arrive in any order and either direction).
    /// A curved outline wire is flattened to its chord with a note.</summary>
    private static List<Vector2d> ReadOutline(XElement board, Action<string> note)
    {
        var segments = new List<(Vector2d A, Vector2d B)>();
        var plain = board.Element("plain");
        if (plain is not null)
            foreach (var wire in plain.Elements("wire"))
            {
                if ((int)Num(wire, "layer") != 20)
                    continue;
                if (wire.Attribute("curve") is not null)
                    note("A curved outline wire was flattened to its chord.");
                segments.Add((
                    new Vector2d(Num(wire, "x1"), Num(wire, "y1")),
                    new Vector2d(Num(wire, "x2"), Num(wire, "y2"))));
            }
        if (segments.Count < 3)
            throw new FormatException(
                "This Eagle board has no usable outline: fewer than 3 layer-20 (Dimension) wires "
                + "in <plain>. A board needs a closed outline to build.");

        // Chain by endpoint: start anywhere, repeatedly append the segment sharing the current end.
        var loop = new List<Vector2d> { segments[0].A, segments[0].B };
        var used = new bool[segments.Count];
        used[0] = true;
        for (int appended = 1; appended < segments.Count; appended++)
        {
            var current = loop[^1];
            int found = -1;
            bool flip = false;
            for (int i = 0; i < segments.Count; i++)
            {
                if (used[i]) continue;
                if ((segments[i].A - current).Length <= Weld) { found = i; flip = false; break; }
                if ((segments[i].B - current).Length <= Weld) { found = i; flip = true; break; }
            }
            if (found < 0)
                throw new FormatException(
                    "This Eagle board's layer-20 outline does not chain into one closed loop (a "
                    + $"segment end at ({current.X:0.###}, {current.Y:0.###}) matches no other "
                    + "segment). A board needs a closed outline to build.");
            used[found] = true;
            loop.Add(flip ? segments[found].A : segments[found].B);
        }
        if ((loop[^1] - loop[0]).Length > Weld)
            throw new FormatException(
                "This Eagle board's layer-20 outline is not closed (the chained loop's last point "
                + "does not return to its first). A board needs a closed outline to build.");
        loop.RemoveAt(loop.Count - 1);   // PcbBoard wants no repeated closing point
        return loop;
    }
}
