using System.Xml;
using System.Xml.Linq;

namespace EngrCAD.Ecad;

/// <summary>The result of reading a whole Eagle schematic — the reconstructed connectivity graph
/// plus the reader's diagnostics (the <c>KiCadSchematic</c> convention).</summary>
/// <param name="Schematic">The reconstructed schematic (components + nets).</param>
/// <param name="Diagnostics">What was skipped, approximated, or looked wrong — never thrown for
/// per-element dirt (the readers-never-throw-on-dirty culture).</param>
public sealed record EagleSchematic(
    Schematic Schematic, IReadOnlyList<string> Diagnostics);

/// <summary>
/// Whole Eagle <c>.sch</c> schematic import — the schematic twin of the
/// <see cref="EagleLibraryReader">.lbr component reader</see>, and the STRUCTURAL opposite of the
/// KiCad one: where a <c>.kicad_sch</c> only DRAWS its netlist (connectivity reconstructed from
/// wire geometry by union-find), an Eagle schematic DECLARES it — every <c>&lt;net&gt;</c> lists
/// its <c>&lt;pinref part gate pin&gt;</c> terminals explicitly — so the import is a resolution,
/// not a reconstruction, and the wire geometry is never consulted for connectivity at all.
///
/// <para><b>Parts resolve through the embedded libraries.</b> A <c>.sch</c> carries its own
/// <c>&lt;libraries&gt;</c> (each the same content as a <c>.lbr</c>'s), read by the SHARED
/// <see cref="EagleLibraryReader.ReadLibraryElement"/> — so the pin/pad/symbol unification is the
/// .lbr reader's verbatim, definitions interned per (library, deviceset, device). A part whose
/// device cannot be assembled (the typical case being a SUPPLY symbol — its deviceset has no
/// connects, so its pin is unmapped) is REPORTED and skipped, never thrown; its pinrefs are then
/// skipped by name. The net keeps its own declared name either way, which is why a skipped supply
/// marker costs nothing — <c>GND</c> is the net element's own name, not the symbol's.</para>
///
/// <para><b>A pinref names the SYMBOL PIN, resolved by NAME first.</b> An Eagle pin is named in
/// the symbol's own vocabulary (<c>"1"</c>, <c>"VCC"</c>) while the loaded
/// <see cref="PartDefinition"/>'s pin NUMBERS are pads (the .lbr connect map) — so
/// <c>&lt;pinref pin="VCC"&gt;</c> resolves to the pin whose NAME is VCC (falling back to the
/// number for symbols whose names are the pads), which is what lands it on pad 8. Nets with one
/// resolvable pin become <see cref="Schematic.Stub"/>s; nets group by NAME across every sheet and
/// segment (Eagle nets are global to the schematic).</para>
///
/// <para><b>Refused BY NAME</b>: malformed XML, a non-<c>&lt;eagle&gt;</c> root, and a
/// <c>.lbr</c> / <c>.brd</c> handed here. Reported, never thrown: an unloadable part, a pinref
/// to a skipped/unknown part, a pinref naming a pin the part does not have, a pin claimed by two
/// nets (the first net keeps it), and a net with no resolvable pins.</para>
/// </summary>
public static class EagleSchematicReader
{
    /// <summary>Reads an Eagle <c>.sch</c> file's XML text.</summary>
    /// <exception cref="FormatException">The XML is malformed, the root is not <c>&lt;eagle&gt;</c>,
    /// or the file is a library / board rather than a schematic — refused by name.</exception>
    public static EagleSchematic Read(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException ex)
        {
            throw new FormatException($"Not a valid Eagle .sch: the XML is malformed — {ex.Message}", ex);
        }

        var root = document.Root
            ?? throw new FormatException("The Eagle .sch XML has no root element.");
        if (!string.Equals(root.Name.LocalName, "eagle", StringComparison.Ordinal))
            throw new FormatException(
                $"Not an Eagle file: the root element is '{root.Name.LocalName}', expected 'eagle'.");

        var drawing = root.Element("drawing")
            ?? throw new FormatException(
                "The Eagle file has no <drawing> — it is not a well-formed Eagle document.");
        var schematicElement = drawing.Element("schematic");
        if (schematicElement is null)
        {
            string kind = drawing.Element("board") is not null ? "a board (.brd)"
                : drawing.Element("library") is not null
                    ? "a component library (.lbr) — use EagleLibraryReader for libraries"
                    : "not a schematic";
            throw new FormatException(
                $"This Eagle file has no <schematic>: it is {kind}. Only Eagle schematics (.sch) "
                + "are read here.");
        }

        var diagnostics = new List<string>();
        var once = new HashSet<string>(StringComparer.Ordinal);
        void Note(string message) { if (once.Add(message)) diagnostics.Add(message); }

        // ---- the embedded libraries (each the same content as a .lbr's <library>) ----------
        var libraries = new Dictionary<string, EagleLibrary>(StringComparer.Ordinal);
        var librariesElement = schematicElement.Element("libraries");
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

        // ---- parts: refdes -> a definition interned per (library, deviceset, device) --------
        var schematic = new Schematic("");
        var partDefs = new Dictionary<(string, string, string), PartDefinition>();
        var components = new Dictionary<string, Component>(StringComparer.Ordinal);
        var partsElement = schematicElement.Element("parts");
        if (partsElement is not null)
            foreach (var part in partsElement.Elements("part"))
            {
                string? name = part.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(name))
                {
                    Note("A <part> has no name and was skipped.");
                    continue;
                }
                string libName = part.Attribute("library")?.Value ?? "";
                string deviceSet = part.Attribute("deviceset")?.Value ?? "";
                string device = part.Attribute("device")?.Value ?? "";
                string value = part.Attribute("value")?.Value ?? "";

                if (!libraries.TryGetValue(libName, out var library))
                {
                    Note($"Part '{name}' references library '{libName}', which the schematic does "
                        + "not embed; the part was skipped.");
                    continue;
                }

                var key = (libName, deviceSet, device);
                if (!partDefs.TryGetValue(key, out var definition))
                {
                    // An unloadable device — the typical case being a SUPPLY symbol, whose deviceset
                    // has no connects so its pin is unmapped — is reported and skipped, never thrown.
                    // Connectivity survives because an Eagle net carries its OWN name.
                    try
                    {
                        var loaded = library.Load(deviceSet + device);
                        foreach (var d in loaded.Diagnostics)
                            Note($"Part '{name}' ({deviceSet}{device}): {d}");
                        partDefs[key] = definition = loaded.Definition;
                    }
                    catch (FormatException ex)
                    {
                        Note($"Part '{name}' ({deviceSet}{device}) could not be loaded and was "
                            + $"skipped: {ex.Message}");
                        continue;
                    }
                }
                components[name] = schematic.Add(name, definition, value);
            }

        // ---- nets: DECLARED terminals, grouped by NAME across every sheet and segment -------
        // (Eagle nets are global to the schematic — one name is one net wherever it appears.)
        var pinsByNet = new Dictionary<string, List<PinRef>>(StringComparer.Ordinal);
        var netOrder = new List<string>();
        var claimed = new Dictionary<PinRef, string>();
        var sheetsElement = schematicElement.Element("sheets");
        if (sheetsElement is not null)
            foreach (var sheet in sheetsElement.Elements("sheet"))
            {
                var netsElement = sheet.Element("nets");
                if (netsElement is null)
                    continue;
                foreach (var net in netsElement.Elements("net"))
                {
                    string? netName = net.Attribute("name")?.Value;
                    if (string.IsNullOrEmpty(netName))
                    {
                        Note("A <net> has no name and was skipped.");
                        continue;
                    }
                    if (!pinsByNet.TryGetValue(netName, out var pins))
                    {
                        pinsByNet[netName] = pins = [];
                        netOrder.Add(netName);
                    }
                    foreach (var segment in net.Elements("segment"))
                        foreach (var pinref in segment.Elements("pinref"))
                            ResolvePinref(pinref, netName, components, pins, claimed, Note);
                }
            }

        foreach (var netName in netOrder)
        {
            var pins = pinsByNet[netName];
            if (pins.Count >= 2)
                schematic.Connect(netName, pins);
            else if (pins.Count == 1)
                schematic.Stub(netName, pins[0]);
            else
                Note($"Net '{netName}' has no resolvable pins (its pinrefs were all skipped or "
                    + "absent); no net was created.");
        }

        return new EagleSchematic(schematic, diagnostics);
    }

    /// <summary>Reads an Eagle <c>.sch</c> file from disk.</summary>
    public static EagleSchematic ReadFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Read(File.ReadAllText(path));
    }

    /// <summary>Resolves one <c>&lt;pinref part gate pin&gt;</c> to a component pin — the pinref
    /// names the SYMBOL PIN, so it resolves by pin NAME first (the .lbr unification put the symbol
    /// pin's name on <see cref="Pin.Name"/> and its pad on <see cref="Pin.Number"/>), falling back
    /// to the NUMBER for symbols whose pin names are the pads themselves.</summary>
    private static void ResolvePinref(
        XElement pinref, string netName, Dictionary<string, Component> components,
        List<PinRef> pins, Dictionary<PinRef, string> claimed, Action<string> note)
    {
        string? partName = pinref.Attribute("part")?.Value;
        string? pinName = pinref.Attribute("pin")?.Value;
        if (string.IsNullOrEmpty(partName) || string.IsNullOrEmpty(pinName))
        {
            note($"Net '{netName}' has a pinref with no part or pin name; it was skipped.");
            return;
        }
        if (!components.TryGetValue(partName, out var component))
        {
            note($"Net '{netName}' references part '{partName}', which was skipped or not "
                + "declared; the pinref was skipped.");
            return;
        }

        var definition = component.Definition;
        string? number = null;
        foreach (var p in definition.Pins)
            if (string.Equals(p.Name, pinName, StringComparison.Ordinal))
            {
                number = p.Number;
                break;
            }
        if (number is null)
            foreach (var p in definition.Pins)
                if (string.Equals(p.Number, pinName, StringComparison.Ordinal))
                {
                    number = p.Number;
                    break;
                }
        if (number is null)
        {
            note($"Net '{netName}' references pin '{pinName}' of part '{partName}', which its "
                + $"device ('{definition.Name}') does not have; the pinref was skipped.");
            return;
        }

        var pin = component.Pin(number);
        if (claimed.TryGetValue(pin, out var owner))
        {
            if (!string.Equals(owner, netName, StringComparison.Ordinal))
                note($"Pin {pin} appears in net '{netName}' but is already on net '{owner}'; the "
                    + "first net kept it.");
            return;   // already claimed (by this net via another segment, or by another net)
        }
        claimed[pin] = netName;
        pins.Add(pin);
    }
}
