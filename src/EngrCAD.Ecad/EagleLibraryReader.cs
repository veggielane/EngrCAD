using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using EngrCAD.Core;

namespace EngrCAD.Ecad;

/// <summary>
/// One loadable device of an <see cref="EagleLibrary"/> — a <c>deviceset</c>/<c>device</c> pair,
/// which is the unit an Eagle library places on a board: a schematic <c>symbol</c> bound to a
/// <c>package</c> (footprint) through the deviceset's <c>connect</c> map. Its <see cref="Name"/>
/// is the deviceset name concatenated with the device name (Eagle's own full device name), which
/// is what <see cref="EagleLibrary.Load(string)"/> resolves.
/// </summary>
/// <param name="Name">The full device name (<c>devicesetName + deviceName</c>).</param>
/// <param name="DeviceSet">The owning deviceset's name.</param>
/// <param name="Package">The referenced package (footprint) name, or the empty string for a
/// symbol-only device that binds no package.</param>
/// <param name="Prefix">The reference-designator prefix from the deviceset (<c>"R"</c>, <c>"U"</c>).</param>
public sealed record EagleDeviceInfo(string Name, string DeviceSet, string Package, string Prefix);

/// <summary>
/// A parsed Eagle component library (a classic <c>.lbr</c> XML file). It holds the library's
/// symbols, packages and devicesets, and resolves one device into a <see cref="PartDefinition"/>
/// (pins + footprint + symbol, unified by pin NUMBER) via <see cref="Load(string)"/>.
/// <para>Parse once (<see cref="EagleLibraryReader.Read(string)"/>), load many — the intermediate
/// symbols/packages/devicesets are held so a library with several devices is read once.</para>
/// </summary>
public sealed class EagleLibrary
{
    private readonly Dictionary<string, EagleSymbol> _symbols;
    private readonly Dictionary<string, EaglePackage> _packages;
    private readonly List<EagleDeviceSet> _deviceSets;
    private readonly Dictionary<string, (EagleDeviceSet Set, EagleDevice Device)> _byName;

    /// <summary>The library's name (from <c>&lt;library&gt;</c>'s parent <c>&lt;drawing&gt;</c>
    /// name, if any) — Eagle libraries carry no name on the element itself, so this is the file's
    /// intent and defaults to the empty string.</summary>
    public string Name { get; }

    /// <summary>Every loadable device in the library, in file order (deviceset then device).</summary>
    public IReadOnlyList<EagleDeviceInfo> Devices { get; }

    /// <summary>What the reader ignored or approximated while parsing the library — the
    /// <c>StepReader.Diagnostics</c> convention (an ignored <c>&lt;hole&gt;</c>/<c>&lt;via&gt;</c>,
    /// an unsupported graphic, a multi-gate deviceset). Per-device diagnostics are added on
    /// <see cref="Load(string)"/>.</summary>
    public IReadOnlyList<string> Diagnostics { get; }

    internal EagleLibrary(
        string name,
        Dictionary<string, EagleSymbol> symbols,
        Dictionary<string, EaglePackage> packages,
        List<EagleDeviceSet> deviceSets,
        List<string> diagnostics)
    {
        Name = name;
        _symbols = symbols;
        _packages = packages;
        _deviceSets = deviceSets;
        Diagnostics = diagnostics;

        _byName = new Dictionary<string, (EagleDeviceSet, EagleDevice)>(StringComparer.Ordinal);
        var devices = new List<EagleDeviceInfo>();
        foreach (var set in deviceSets)
            foreach (var device in set.Devices)
            {
                string full = set.Name + device.Name;
                devices.Add(new EagleDeviceInfo(full, set.Name, device.Package, set.Prefix));
                // First spelling wins on the rare name clash; the listing still shows both.
                _byName.TryAdd(full, (set, device));
            }
        Devices = devices;
    }

    /// <summary>Resolves one device into a <see cref="LoadedPart"/> — the assembled
    /// <see cref="PartDefinition"/> (pins numbered by the pads its <c>connect</c>s name, its
    /// footprint and its symbol), the <see cref="PinIdentity"/> report and the diagnostics.</summary>
    /// <param name="deviceName">The full device name (see <see cref="EagleDeviceInfo.Name"/>); an
    /// absent name is refused BY NAME, listing the devices the library does carry.</param>
    /// <exception cref="FormatException">The device is absent, references a missing symbol or
    /// package, is a multi-gate device (unsupported in v1), or has a symbol pin with no
    /// <c>connect</c> (an unmapped pin) — each refused by name.</exception>
    public LoadedPart Load(string deviceName)
    {
        ArgumentNullException.ThrowIfNull(deviceName);
        if (!_byName.TryGetValue(deviceName, out var found))
            throw new FormatException(
                $"The Eagle library has no device '{deviceName}'. It has: "
                + (Devices.Count == 0 ? "(none)" : string.Join(", ", Devices.Select(d => d.Name))) + ".");
        var (set, device) = found;
        return EagleLibraryReader.Assemble(this, set, device);
    }

    internal EagleSymbol? Symbol(string name) => _symbols.GetValueOrDefault(name);

    internal EaglePackage? Package(string name) => _packages.GetValueOrDefault(name);
}

/// <summary>
/// Reads an Eagle component library (a classic <c>.lbr</c> XML file) into an
/// <see cref="EagleLibrary"/> and one of its devices into a <see cref="PartDefinition"/> — the
/// SECOND component-interchange reader beside the KiCad one, producing the SAME
/// <see cref="LoadedPart"/> unified by pin NUMBER.
///
/// <para>Eagle files are real XML, so this rides the BCL's <see cref="XDocument"/> rather than a
/// hand-rolled parser (the <c>ThreeMfWriter</c>/<c>AmfWriter</c> precedent for XML formats), in the
/// <c>StepReader</c>/<c>IgesReader</c> ethos: structure validated up front (malformed XML, a file
/// that is a <c>.brd</c>/<c>.sch</c> rather than a library, a missing required element — each
/// refused BY NAME), the COMMON subset mapped, and everything else ignored or approximated with a
/// named diagnostic.</para>
///
/// <para><b>The <c>&lt;connect gate pin pad&gt;</c> map is what unifies the three.</b> An Eagle
/// symbol's pins are named in the symbol's own vocabulary (<c>"1"</c>, <c>"VCC"</c>); a package's
/// pads are numbered (<c>"1"</c>, <c>"A5"</c>); and the deviceset's <c>connect</c>s bind them —
/// symbol pin <c>"VCC"</c> → pad <c>"8"</c>. So the loaded part's pin NUMBER is the PAD number, its
/// name is the symbol pin's name, and its symbol pin, footprint pad and netlist pin all carry the
/// same number: <see cref="PinIdentity"/> then verifies the three agree.</para>
///
/// <para><b>Covered:</b> symbol <c>wire</c> (line, or arc via a <c>curve</c>)/<c>rectangle</c>/
/// <c>circle</c>/<c>polygon</c>/<c>text</c> graphics and <c>pin</c>s (name, position, <c>rot</c> →
/// direction, <c>length</c>, <c>direction</c> → <see cref="PinType"/>); package <c>smd</c> pads and
/// <c>pad</c> plated through-holes of the standard shapes (round/square/octagon/long) with their
/// drill; and the deviceset/device <c>connect</c> mapping. Eagle coordinates are stored in the XML
/// in MILLIMETRES, so pad centres and pin anchors are carried EXACTLY.</para>
///
/// <para><b>Ignored/refused BY NAME:</b> a package <c>&lt;hole&gt;</c>/<c>&lt;via&gt;</c> (not a
/// pad — noted), a graphic kind outside the covered set (noted), a multi-gate deviceset (a gate
/// array — refused at <see cref="EagleLibrary.Load(string)"/>), a symbol pin with no <c>connect</c>
/// (an unmapped pin — refused), and the newer Eagle/Fusion XML variants and whole <c>.brd</c>/
/// <c>.sch</c> board/schematic import (refused at the root).</para>
/// </summary>
public static class EagleLibraryReader
{
    /// <summary>Reads a <c>.lbr</c> file's XML text into an <see cref="EagleLibrary"/>.</summary>
    /// <param name="xml">The <c>.lbr</c> file text.</param>
    /// <exception cref="FormatException">The XML is malformed, the file is not an Eagle library
    /// (its root is not <c>&lt;eagle&gt;</c>, or it is a board/schematic rather than a library), or
    /// a required element is missing — refused by name.</exception>
    public static EagleLibrary Read(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException ex)
        {
            throw new FormatException($"Not a valid Eagle .lbr: the XML is malformed — {ex.Message}", ex);
        }

        var root = document.Root
            ?? throw new FormatException("The Eagle .lbr XML has no root element.");
        if (!string.Equals(root.Name.LocalName, "eagle", StringComparison.Ordinal))
            throw new FormatException(
                $"Not an Eagle file: the root element is '{root.Name.LocalName}', expected 'eagle'.");

        var drawing = root.Element("drawing")
            ?? throw new FormatException("The Eagle file has no <drawing> — it is not a well-formed Eagle document.");
        var library = drawing.Element("library");
        if (library is null)
        {
            // A .brd carries <board>, a .sch carries <schematic>; neither is a component library.
            string kind = drawing.Element("board") is not null ? "a board (.brd)"
                : drawing.Element("schematic") is not null ? "a schematic (.sch)"
                : "not a component library";
            throw new FormatException(
                $"This Eagle file has no <library>: it is {kind}. Only Eagle component libraries "
                + "(.lbr) are read; whole-board/schematic import is out of scope.");
        }

        var diagnostics = new List<string>();

        var packages = new Dictionary<string, EaglePackage>(StringComparer.Ordinal);
        var packagesElement = library.Element("packages");
        if (packagesElement is not null)
            foreach (var package in packagesElement.Elements("package"))
            {
                var parsed = ParsePackage(package, diagnostics);
                if (parsed is not null)
                    packages[parsed.Name] = parsed;
            }

        var symbols = new Dictionary<string, EagleSymbol>(StringComparer.Ordinal);
        var symbolsElement = library.Element("symbols");
        if (symbolsElement is not null)
            foreach (var symbol in symbolsElement.Elements("symbol"))
            {
                var parsed = ParseSymbol(symbol, diagnostics);
                if (parsed is not null)
                    symbols[parsed.Name] = parsed;
            }

        var deviceSets = new List<EagleDeviceSet>();
        var deviceSetsElement = library.Element("devicesets");
        if (deviceSetsElement is not null)
            foreach (var deviceSet in deviceSetsElement.Elements("deviceset"))
            {
                var parsed = ParseDeviceSet(deviceSet, diagnostics);
                if (parsed is not null)
                    deviceSets.Add(parsed);
            }

        // Eagle carries no library name on the element; use the description's first line if there
        // is one, else the empty string — a name is not a property that round-trips here.
        string name = "";

        return new EagleLibrary(name, symbols, packages, deviceSets, diagnostics);
    }

    /// <summary>Reads a <c>.lbr</c> file from disk.</summary>
    public static EagleLibrary ReadFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Read(File.ReadAllText(path));
    }

    /// <summary>Reads a <c>.lbr</c> file's text and loads one device — the convenience over
    /// <see cref="Read(string)"/> then <see cref="EagleLibrary.Load(string)"/>.</summary>
    public static LoadedPart Load(string xml, string deviceName) => Read(xml).Load(deviceName);

    /// <summary>Reads a <c>.lbr</c> file from disk and loads one device.</summary>
    public static LoadedPart LoadFile(string path, string deviceName) => ReadFile(path).Load(deviceName);

    // ---- assembling a device into a PartDefinition --------------------------

    internal static LoadedPart Assemble(EagleLibrary library, EagleDeviceSet set, EagleDevice device)
    {
        var diagnostics = new List<string>();
        string fullName = set.Name + device.Name;

        // v1 handles single-gate devices; a gate array is refused BY NAME.
        if (set.Gates.Count != 1)
            throw new FormatException(
                $"Eagle device '{fullName}' has {set.Gates.Count} gates; multi-gate devices "
                + "(gate arrays) are not supported in v1.");
        var gate = set.Gates[0];

        var symbol = library.Symbol(gate.SymbolName)
            ?? throw new FormatException(
                $"Eagle device '{fullName}' gate '{gate.Name}' references symbol '{gate.SymbolName}', "
                + "which the library does not contain.");

        // The connect map for this gate: symbol pin name -> pad. Duplicates keep the first.
        var padOfPin = new Dictionary<string, string>(StringComparer.Ordinal);
        var symbolPinNames = symbol.Pins.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var connect in device.Connects)
        {
            if (!string.Equals(connect.Gate, gate.Name, StringComparison.Ordinal))
            {
                diagnostics.Add(
                    $"Device '{fullName}': a connect names gate '{connect.Gate}', not the device's "
                    + $"gate '{gate.Name}'; it was ignored.");
                continue;
            }
            if (!symbolPinNames.Contains(connect.Pin))
                diagnostics.Add(
                    $"Device '{fullName}': connect names symbol pin '{connect.Pin}', which symbol "
                    + $"'{symbol.Name}' does not have; it was ignored.");
            if (!padOfPin.TryAdd(connect.Pin, connect.Pad))
                diagnostics.Add(
                    $"Device '{fullName}': symbol pin '{connect.Pin}' is connected more than once; "
                    + "the first pad was kept.");
        }

        // Build the pins and symbol pins from the symbol's pins, each numbered by the PAD it
        // connects to. A symbol pin with no connect cannot be given a number — refused by name.
        var pins = new List<Pin>();
        var symbolPins = new List<SymbolPin>();
        var usedPads = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ep in symbol.Pins)
        {
            if (!padOfPin.TryGetValue(ep.Name, out var pad))
                throw new FormatException(
                    $"Eagle device '{fullName}': symbol pin '{ep.Name}' has no <connect>, so it has "
                    + "no pad number — an unmapped pin.");
            if (!usedPads.Add(pad))
            {
                diagnostics.Add(
                    $"Device '{fullName}': pad '{pad}' is used by more than one symbol pin; the "
                    + $"extra pin '{ep.Name}' was dropped (v1 carries one pin per pad).");
                continue;
            }
            pins.Add(new Pin(pad, ep.Name, ep.Type));
            symbolPins.Add(new SymbolPin(pad, ep.Name, ep.Anchor, ep.Direction, ep.Length, ep.Type));
        }

        var symbolValue = new Symbol(symbol.Name, symbolPins, symbol.Graphics);

        // The footprint comes from the PACKAGE (independently of the connects), so a connect naming
        // a pad the package lacks, or a package pad no connect names, surfaces as a PinIdentity
        // mismatch reported by number — not silently.
        Footprint? footprint = null;
        if (device.Package.Length > 0)
        {
            var package = library.Package(device.Package)
                ?? throw new FormatException(
                    $"Eagle device '{fullName}' references package '{device.Package}', which the "
                    + "library does not contain.");
            footprint = new Footprint(package.Name, package.Pads);
            diagnostics.AddRange(package.Diagnostics);
        }

        diagnostics.AddRange(symbol.Diagnostics);

        var definition = new PartDefinition(
            fullName, set.Prefix, pins, footprint, body: null, symbol: symbolValue);
        var identity = PinIdentity.Check(definition);
        return new LoadedPart(definition, identity, diagnostics);
    }

    // ---- packages -----------------------------------------------------------

    private static EaglePackage? ParsePackage(XElement package, List<string> libraryDiagnostics)
    {
        string? name = package.Attribute("name")?.Value;
        if (string.IsNullOrEmpty(name))
        {
            libraryDiagnostics.Add("An Eagle <package> has no name and was ignored.");
            return null;
        }

        var diagnostics = new List<string>();
        var pads = new List<Pad>();
        foreach (var element in package.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "smd":
                    if (TrySmd(element, name, diagnostics, out var smd))
                        pads.Add(smd);
                    break;
                case "pad":
                    if (TryPad(element, name, diagnostics, out var pad))
                        pads.Add(pad);
                    break;
                case "hole":
                    diagnostics.Add(
                        $"Package '{name}': a <hole> is a non-plated mounting hole, not a pad, and "
                        + "was ignored.");
                    break;
                case "via":
                    diagnostics.Add(
                        $"Package '{name}': a <via> inside a package was ignored (it is not a pad).");
                    break;
                    // wire/rectangle/circle/polygon/text/description are silkscreen/courtyard/doc —
                    // not copper lands — and are skipped, as the KiCad footprint reader skips
                    // fp_line/fp_text.
            }
        }

        libraryDiagnostics.AddRange(diagnostics);
        return new EaglePackage(name, pads, diagnostics);
    }

    private static bool TrySmd(XElement element, string packageName, List<string> diagnostics, out Pad pad)
    {
        pad = default;
        string number = element.Attribute("name")?.Value ?? "";
        if (number.Length == 0)
        {
            diagnostics.Add($"Package '{packageName}': an <smd> pad has no name and was ignored.");
            return false;
        }
        double dx = Attr(element, "dx");
        double dy = Attr(element, "dy");
        if (dx <= 0 || dy <= 0)
        {
            diagnostics.Add(
                $"Package '{packageName}': smd pad '{number}' has no positive size (dx/dy) and was ignored.");
            return false;
        }
        var center = new Vector2d(Attr(element, "x"), Attr(element, "y"));
        NoteRotation(element, packageName, number, diagnostics);

        // Eagle roundness is a percentage (0-100) of the corner radius; >0 is a rounded rectangle.
        double roundness = Attr(element, "roundness");
        var shape = roundness > 0 ? PadShape.RoundedRectangle : PadShape.Rectangular;

        // Constructed via the record (not the validating factory) so a malformed pad IMPORTS and
        // the DRC reports it (the "readers never throw on dirty geometry" culture).
        pad = new Pad(number, center, dx, dy, shape, PadKind.Smd, 0);
        return true;
    }

    private static bool TryPad(XElement element, string packageName, List<string> diagnostics, out Pad pad)
    {
        pad = default;
        string number = element.Attribute("name")?.Value ?? "";
        if (number.Length == 0)
        {
            diagnostics.Add($"Package '{packageName}': a <pad> has no name and was ignored.");
            return false;
        }
        double drill = Attr(element, "drill");
        var center = new Vector2d(Attr(element, "x"), Attr(element, "y"));
        NoteRotation(element, packageName, number, diagnostics);

        // Eagle pad shape: round (default) / square / octagon / long / offset.
        string shapeToken = element.Attribute("shape")?.Value ?? "round";
        var (shape, shapeKnown) = shapeToken switch
        {
            "round" => (PadShape.Round, true),
            "square" => (PadShape.Rectangular, true),
            "long" => (PadShape.Oval, true),
            "octagon" => (PadShape.Round, false),   // no exact octagon — nearest is round, noted
            "offset" => (PadShape.Oval, false),      // an offset long pad — nearest is oval, noted
            _ => (PadShape.Round, false),
        };
        if (!shapeKnown)
            diagnostics.Add(
                $"Package '{packageName}': pad '{number}' shape '{shapeToken}' mapped to {shape}.");

        // The pad diameter: Eagle states it explicitly (diameter) or auto-sizes it from the drill
        // and the design rules. When absent, a nominal ring (0.25 mm each side) is used and NOTED,
        // since the design rules are not in the file.
        double diameter = Attr(element, "diameter");
        if (diameter <= 0)
        {
            diameter = drill + 0.5;
            diagnostics.Add(
                $"Package '{packageName}': through-hole pad '{number}' states no diameter; a nominal "
                + $"{diameter:g6} mm (drill + 0.5) was assumed.");
        }
        if (drill <= 0)
            diagnostics.Add(
                $"Package '{packageName}': through-hole pad '{number}' has no positive drill.");
        else if (drill >= diameter)
            diagnostics.Add(
                $"Package '{packageName}': through-hole pad '{number}' drill {drill:g6} leaves no "
                + "annular ring (drill ≥ pad).");

        // The drawn pad size is the diameter both ways (Eagle stores a long/oval pad's elongation
        // in the design rules, not the file), so v1 carries a square-bounded pad of the diameter.
        pad = new Pad(number, center, diameter, diameter, shape, PadKind.ThroughHole, drill);
        return true;
    }

    // ---- symbols ------------------------------------------------------------

    private static EagleSymbol? ParseSymbol(XElement symbol, List<string> libraryDiagnostics)
    {
        string? name = symbol.Attribute("name")?.Value;
        if (string.IsNullOrEmpty(name))
        {
            libraryDiagnostics.Add("An Eagle <symbol> has no name and was ignored.");
            return null;
        }

        var diagnostics = new List<string>();
        var graphics = new List<SymbolGraphic>();
        var pins = new List<EaglePin>();
        var seenPinNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var element in symbol.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "wire":
                    if (TryWire(element, out var wire))
                        graphics.Add(wire);
                    break;
                case "rectangle":
                    if (TryRectangle(element, name, diagnostics, out var rect))
                        graphics.Add(rect);
                    break;
                case "circle":
                    if (TryCircle(element, out var circle))
                        graphics.Add(circle);
                    break;
                case "polygon":
                    if (TryPolygon(element, name, diagnostics, out var poly))
                        graphics.Add(poly);
                    break;
                case "text":
                    if (TryText(element, out var text))
                        graphics.Add(text);
                    break;
                case "pin":
                    if (TryPin(element, name, seenPinNames, diagnostics, out var pin))
                        pins.Add(pin);
                    break;
                case "frame":
                case "dimension":
                    diagnostics.Add(
                        $"Symbol '{name}': a <{element.Name.LocalName}> is not supported and was ignored.");
                    break;
            }
        }

        return new EagleSymbol(name, graphics, pins, diagnostics);
    }

    private static bool TryWire(XElement element, out SymbolGraphic graphic)
    {
        graphic = default!;
        var start = new Vector2d(Attr(element, "x1"), Attr(element, "y1"));
        var end = new Vector2d(Attr(element, "x2"), Attr(element, "y2"));
        double curve = Attr(element, "curve");
        if (curve == 0)
        {
            graphic = new SymbolPolyline([start, end]);
            return true;
        }

        // A curved wire is an arc: Eagle's 'curve' is the signed included angle in degrees
        // (positive = counterclockwise). Build the start/mid/end SymbolArc from it.
        var chord = end - start;
        double length = chord.Length;
        double theta = curve * Math.PI / 180.0;
        double half = theta / 2.0;
        double sinHalf = Math.Sin(half);
        if (length < 1e-12 || Math.Abs(sinHalf) < 1e-12)
        {
            graphic = new SymbolPolyline([start, end]);   // degenerate — draw the chord
            return true;
        }
        double radius = (length / 2.0) / sinHalf;                  // signed
        double sagitta = radius * (1.0 - Math.Cos(half));          // signed with the radius
        var perpLeft = new Vector2d(-chord.Y, chord.X) / length;   // 90° CCW from the chord
        var mid = (start + end) * 0.5 + perpLeft * sagitta;
        graphic = new SymbolArc(start, mid, end);
        return true;
    }

    private static bool TryRectangle(XElement element, string symbolName, List<string> diagnostics, out SymbolRectangle rect)
    {
        double x1 = Attr(element, "x1"), y1 = Attr(element, "y1");
        double x2 = Attr(element, "x2"), y2 = Attr(element, "y2");
        if (element.Attribute("rot") is { Value.Length: > 0 } rot && RotDegrees(rot.Value) != 0)
            diagnostics.Add(
                $"Symbol '{symbolName}': a <rectangle> rotation '{rot.Value}' was ignored "
                + "(the rectangle is stored axis-aligned).");
        rect = new SymbolRectangle(
            new Vector2d(Math.Min(x1, x2), Math.Min(y1, y2)),
            new Vector2d(Math.Max(x1, x2), Math.Max(y1, y2)));
        return true;
    }

    private static bool TryCircle(XElement element, out SymbolCircle circle)
    {
        circle = new SymbolCircle(
            new Vector2d(Attr(element, "x"), Attr(element, "y")), Attr(element, "radius"));
        return circle.Radius > 0;
    }

    private static bool TryPolygon(XElement element, string symbolName, List<string> diagnostics, out SymbolPolyline poly)
    {
        poly = default!;
        var points = new List<Vector2d>();
        bool anyCurve = false;
        foreach (var vertex in element.Elements("vertex"))
        {
            points.Add(new Vector2d(Attr(vertex, "x"), Attr(vertex, "y")));
            if (Attr(vertex, "curve") != 0)
                anyCurve = true;
        }
        if (points.Count < 2)
            return false;
        // An Eagle polygon is a closed filled outline; close it so it draws as a shape, and note
        // any curved edge (v1 draws polygon edges straight).
        if (points[0] != points[^1])
            points.Add(points[0]);
        if (anyCurve)
            diagnostics.Add(
                $"Symbol '{symbolName}': a <polygon> has curved edges, drawn as straight segments in v1.");
        poly = new SymbolPolyline(points);
        return true;
    }

    private static bool TryText(XElement element, out SymbolText text)
    {
        text = default!;
        string value = element.Value ?? "";
        double size = Attr(element, "size", 1.27);
        text = new SymbolText(value, new Vector2d(Attr(element, "x"), Attr(element, "y")), size);
        return true;
    }

    private static bool TryPin(
        XElement element, string symbolName, HashSet<string> seenPinNames,
        List<string> diagnostics, out EaglePin pin)
    {
        pin = default;
        string name = element.Attribute("name")?.Value ?? "";
        if (name.Length == 0)
        {
            diagnostics.Add($"Symbol '{symbolName}': a <pin> has no name and was ignored.");
            return false;
        }
        if (!seenPinNames.Add(name))
        {
            diagnostics.Add(
                $"Symbol '{symbolName}': pin name '{name}' appears more than once; the extra was ignored.");
            return false;
        }

        var anchor = new Vector2d(Attr(element, "x"), Attr(element, "y"));
        string? rot = element.Attribute("rot")?.Value;
        if (IsMirrored(rot))
            diagnostics.Add(
                $"Symbol '{symbolName}': pin '{name}' mirror flag in rot '{rot}' was ignored.");
        var direction = SymbolPinDirectionExtensions.FromDegrees(RotDegrees(rot));

        // Eagle pin length is a named token: point/short/middle/long = 0/0.1/0.2/0.3 inch.
        string lengthToken = element.Attribute("length")?.Value ?? "long";
        double length = lengthToken switch
        {
            "point" => 0.0,
            "short" => 2.54,
            "middle" => 5.08,
            "long" => 7.62,
            _ => 7.62,
        };

        string directionToken = element.Attribute("direction")?.Value ?? "io";
        var (type, known) = MapPinType(directionToken);
        if (!known)
            diagnostics.Add(
                $"Symbol '{symbolName}': pin '{name}' direction '{directionToken}' has no exact "
                + $"PinType; mapped to {type}.");

        pin = new EaglePin(name, anchor, direction, length, type);
        return true;
    }

    private static (PinType Type, bool Known) MapPinType(string direction) => direction switch
    {
        "in" => (PinType.Input, true),
        "out" => (PinType.Output, true),
        "io" => (PinType.Bidirectional, true),
        "oc" => (PinType.OpenCollector, true),
        "hiz" => (PinType.TriState, true),
        "pas" => (PinType.Passive, true),
        "pwr" => (PinType.Power, true),
        "sup" => (PinType.Power, true),
        "nc" => (PinType.Unspecified, false),   // not-connected — a net state, not a pin type: noted
        _ => (PinType.Unspecified, false),
    };

    // ---- devicesets ---------------------------------------------------------

    private static EagleDeviceSet? ParseDeviceSet(XElement deviceSet, List<string> libraryDiagnostics)
    {
        string? name = deviceSet.Attribute("name")?.Value;
        if (name is null)   // an empty deviceset name is legal; a missing attribute is not
        {
            libraryDiagnostics.Add("An Eagle <deviceset> has no name and was ignored.");
            return null;
        }
        string prefix = deviceSet.Attribute("prefix")?.Value is { Length: > 0 } p ? p : "U";

        var gates = new List<EagleGate>();
        foreach (var gate in deviceSet.Element("gates")?.Elements("gate") ?? [])
        {
            string gateName = gate.Attribute("name")?.Value ?? "";
            string symbolName = gate.Attribute("symbol")?.Value ?? "";
            if (gateName.Length > 0 && symbolName.Length > 0)
                gates.Add(new EagleGate(gateName, symbolName));
        }
        if (gates.Count == 0)
        {
            libraryDiagnostics.Add($"Deviceset '{name}' has no usable <gate>; it was ignored.");
            return null;
        }
        if (gates.Count > 1)
            libraryDiagnostics.Add(
                $"Deviceset '{name}' has {gates.Count} gates (a gate array); its devices cannot be "
                + "loaded in v1.");

        var devices = new List<EagleDevice>();
        foreach (var device in deviceSet.Element("devices")?.Elements("device") ?? [])
        {
            string deviceName = device.Attribute("name")?.Value ?? "";
            string package = device.Attribute("package")?.Value ?? "";
            var connects = new List<EagleConnect>();
            foreach (var connect in device.Element("connects")?.Elements("connect") ?? [])
                connects.Add(new EagleConnect(
                    connect.Attribute("gate")?.Value ?? "",
                    connect.Attribute("pin")?.Value ?? "",
                    connect.Attribute("pad")?.Value ?? ""));
            devices.Add(new EagleDevice(deviceName, package, connects));
        }

        return new EagleDeviceSet(name, prefix, gates, devices);
    }

    // ---- attribute helpers --------------------------------------------------

    private static double Attr(XElement element, string name, double fallback = 0)
    {
        var attribute = element.Attribute(name);
        return attribute is not null
            && double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v : fallback;
    }

    /// <summary>The rotation degrees from an Eagle rot token (<c>"R90"</c>, <c>"MR180"</c>).</summary>
    private static double RotDegrees(string? rot)
    {
        if (string.IsNullOrEmpty(rot))
            return 0;
        int i = 0;
        while (i < rot.Length && !char.IsDigit(rot[i]) && rot[i] != '-')
            i++;
        return double.TryParse(rot.AsSpan(i), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v : 0;
    }

    private static bool IsMirrored(string? rot) => rot is not null && rot.Contains('M');

    private static void NoteRotation(XElement element, string packageName, string number, List<string> diagnostics)
    {
        string? rot = element.Attribute("rot")?.Value;
        if (rot is not null && (RotDegrees(rot) != 0 || IsMirrored(rot)))
            diagnostics.Add(
                $"Package '{packageName}': pad '{number}' rotation '{rot}' is not carried by a "
                + "footprint pad and was ignored.");
    }
}

// ---- intermediate parse records (internal) ----------------------------------

/// <summary>A parsed Eagle symbol: its graphics and its raw pins (named in the symbol's own
/// vocabulary; the pad NUMBER comes later from the deviceset's connect map).</summary>
internal sealed record EagleSymbol(
    string Name, List<SymbolGraphic> Graphics, List<EaglePin> Pins, List<string> Diagnostics);

/// <summary>A parsed Eagle symbol pin — its name, its connection point (anchor), the direction it
/// points from there into the body, its length, and its electrical type.</summary>
internal readonly record struct EaglePin(
    string Name, Vector2d Anchor, SymbolPinDirection Direction, double Length, PinType Type);

/// <summary>A parsed Eagle package: its pads (the copper lands the footprint carries).</summary>
internal sealed record EaglePackage(string Name, List<Pad> Pads, List<string> Diagnostics);

/// <summary>A parsed Eagle deviceset — a symbol (through its gates) bound to packages (through its
/// devices), with a reference-designator prefix.</summary>
internal sealed record EagleDeviceSet(
    string Name, string Prefix, List<EagleGate> Gates, List<EagleDevice> Devices);

/// <summary>A parsed Eagle gate: the name that a <c>connect</c> references, and the symbol it
/// draws.</summary>
internal readonly record struct EagleGate(string Name, string SymbolName);

/// <summary>A parsed Eagle device: the package it binds and the connects mapping symbol pins to
/// pads.</summary>
internal sealed record EagleDevice(string Name, string Package, List<EagleConnect> Connects);

/// <summary>A parsed Eagle connect: gate + symbol pin name → package pad — the mapping that unifies
/// the three representations by pad NUMBER.</summary>
internal readonly record struct EagleConnect(string Gate, string Pin, string Pad);
