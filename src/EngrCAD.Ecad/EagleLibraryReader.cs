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
/// <param name="Package3dUrn">The URN of the 3D package this device binds (its first
/// <c>&lt;package3dinstance&gt;</c>), or null for a classic device with no 3D binding — the
/// managed-library (Eagle 9 / Fusion) <c>&lt;packages3d&gt;</c> vocabulary; resolve it through
/// <see cref="EagleLibrary.Packages3d"/>.</param>
public sealed record EagleDeviceInfo(
    string Name, string DeviceSet, string Package, string Prefix, string? Package3dUrn = null);

/// <summary>
/// One managed-library (Eagle 9 / Fusion) 3D-package BINDING — a <c>&lt;package3d&gt;</c> element:
/// a named 3D package identified by a URN, bound to the library's 2D packages through its
/// <c>&lt;packageinstances&gt;</c>. <b>The binding is data the file carries; the model FILE itself
/// usually is not</b> — a managed library keeps its 3D geometry in Fusion's cloud, keyed by the
/// URN — so the reader surfaces the binding and attaches a <c>ComponentModel3D</c> ONLY when a
/// caller-supplied resolver finds a LOCAL file for the URN (see
/// <see cref="EagleLibrary.Load(string, Func{EaglePackage3d, string?})"/>); otherwise the URN is
/// recorded in the diagnostics by name, never guessed at.
/// </summary>
/// <param name="Name">The 3D package's name (e.g. <c>"RESC2012X70"</c>).</param>
/// <param name="Urn">Its identity (e.g. <c>"urn:adsk.eagle:package:23620/2"</c>) — what a device's
/// <c>&lt;package3dinstance&gt;</c> references and what a resolver keys on.</param>
/// <param name="Type">The file's own type token verbatim (<c>"model"</c> = a real 3D model,
/// <c>"box"</c> = an auto-generated extruded box; carried as a string rather than an enum so an
/// unrecognised token is data, not a refusal).</param>
/// <param name="PackageNames">The 2D package names its <c>&lt;packageinstance&gt;</c>s bind.</param>
public sealed record EaglePackage3d(
    string Name, string Urn, string Type, IReadOnlyList<string> PackageNames);

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
    private readonly Dictionary<string, EaglePackage3d> _packages3dByUrn;

    /// <summary>The library's name (from <c>&lt;library&gt;</c>'s parent <c>&lt;drawing&gt;</c>
    /// name, if any) — Eagle libraries carry no name on the element itself, so this is the file's
    /// intent and defaults to the empty string.</summary>
    public string Name { get; }

    /// <summary>Every loadable device in the library, in file order (deviceset then device).</summary>
    public IReadOnlyList<EagleDeviceInfo> Devices { get; }

    /// <summary>The managed-library (Eagle 9 / Fusion) <c>&lt;packages3d&gt;</c> 3D-package
    /// bindings, in file order — empty for a classic library. Each binds a URN to 2D package
    /// names; the model FILE itself is Fusion cloud content, which is why loading takes a
    /// resolver (see <see cref="Load(string, Func{EaglePackage3d, string?})"/>).</summary>
    public IReadOnlyList<EaglePackage3d> Packages3d { get; }

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
        List<EaglePackage3d> packages3d,
        List<string> diagnostics)
    {
        Name = name;
        _symbols = symbols;
        _packages = packages;
        _deviceSets = deviceSets;
        Packages3d = packages3d;
        Diagnostics = diagnostics;

        _packages3dByUrn = new Dictionary<string, EaglePackage3d>(StringComparer.Ordinal);
        foreach (var p3d in packages3d)
            _packages3dByUrn.TryAdd(p3d.Urn, p3d);   // first spelling wins on a urn clash

        _byName = new Dictionary<string, (EagleDeviceSet, EagleDevice)>(StringComparer.Ordinal);
        var devices = new List<EagleDeviceInfo>();
        foreach (var set in deviceSets)
            foreach (var device in set.Devices)
            {
                string full = set.Name + device.Name;
                devices.Add(new EagleDeviceInfo(
                    full, set.Name, device.Package, set.Prefix,
                    device.Package3dUrns.Count > 0 ? device.Package3dUrns[0] : null));
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
    public LoadedPart Load(string deviceName) => Load(deviceName, modelResolver: null);

    /// <summary>Resolves one device into a <see cref="LoadedPart"/>, resolving its managed-library
    /// 3D binding through <paramref name="modelResolver"/>. A device's
    /// <c>&lt;package3dinstance&gt;</c> binds a <see cref="EaglePackage3d"/> by URN whose model
    /// FILE usually lives in Fusion's cloud — so the resolver is the caller saying where a LOCAL
    /// copy is: it takes the bound <see cref="EaglePackage3d"/> and returns a local file path, or
    /// null for "no local copy". A <c>ComponentModel3D</c> is attached to the definition ONLY when
    /// the resolver returns a path that exists; otherwise (no resolver, no path, or a path that
    /// does not exist) the binding is recorded in the diagnostics naming the URN, never guessed
    /// into geometry.</summary>
    /// <param name="deviceName">The full device name (see <see cref="EagleDeviceInfo.Name"/>).</param>
    /// <param name="modelResolver">Maps a bound 3D package to a local model file path
    /// (<c>.stl</c>/<c>.obj</c>/<c>.off</c>/<c>.wrl</c>/<c>.step</c>), or null when none exists
    /// locally. Null = no resolver (the binding is diagnosed, not attached).</param>
    /// <exception cref="FormatException">As <see cref="Load(string)"/>.</exception>
    public LoadedPart Load(string deviceName, Func<EaglePackage3d, string?>? modelResolver)
    {
        ArgumentNullException.ThrowIfNull(deviceName);
        if (!_byName.TryGetValue(deviceName, out var found))
            throw new FormatException(
                $"The Eagle library has no device '{deviceName}'. It has: "
                + (Devices.Count == 0 ? "(none)" : string.Join(", ", Devices.Select(d => d.Name))) + ".");
        var (set, device) = found;
        return EagleLibraryReader.Assemble(this, set, device, modelResolver);
    }

    internal EagleSymbol? Symbol(string name) => _symbols.GetValueOrDefault(name);

    internal EaglePackage? Package(string name) => _packages.GetValueOrDefault(name);

    internal EaglePackage3d? Package3d(string urn) => _packages3dByUrn.GetValueOrDefault(urn);
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
/// drill; the deviceset/device <c>connect</c> mapping; and the managed-library (Eagle 9 / Fusion)
/// <c>&lt;packages3d&gt;</c> 3D-package BINDINGS (<see cref="EaglePackage3d"/> — a device's
/// <c>&lt;package3dinstance&gt;</c> names a 3D package by URN, whose model FILE is Fusion cloud
/// content: a <c>ComponentModel3D</c> is attached only when a caller-supplied resolver finds a
/// local file, and the URN is recorded in the diagnostics by name otherwise). Eagle coordinates
/// are stored in the XML in MILLIMETRES, so pad centres and pin anchors are carried EXACTLY.</para>
///
/// <para><b>The newer Eagle 9 / Fusion managed format is TOLERATED rather than forked</b>: its
/// schema drift is additive attributes (<c>urn</c>/<c>library_version</c>/
/// <c>library_locally_modified</c> on library/package/symbol/deviceset), which a reader that reads
/// attributes by name never sees, plus the <c>&lt;packages3d&gt;</c> vocabulary above; a
/// version-9+ file gets a diagnostic NAMING the declared version so the managed provenance is
/// visible rather than silent.</para>
///
/// <para><b>Ignored/refused BY NAME:</b> a package <c>&lt;hole&gt;</c>/<c>&lt;via&gt;</c> (not a
/// pad — noted), a graphic kind outside the covered set (noted), a multi-gate deviceset (a gate
/// array — refused at <see cref="EagleLibrary.Load(string)"/>), a symbol pin with no <c>connect</c>
/// (an unmapped pin — refused), and a whole <c>.brd</c>/<c>.sch</c> handed here (refused at the
/// root, signposted to <see cref="EagleBoardReader"/>/<see cref="EagleSchematicReader"/>).</para>
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
            string kind = drawing.Element("board") is not null
                ? "a board (.brd) — use EagleBoardReader for whole-board import"
                : drawing.Element("schematic") is not null
                    ? "a schematic (.sch) — use EagleSchematicReader for whole-schematic import"
                    : "not a component library";
            throw new FormatException(
                $"This Eagle file has no <library>: it is {kind}. Only Eagle component libraries "
                + "(.lbr) are read here.");
        }

        return ReadLibraryElement(library, "", VersionNote(root));
    }

    /// <summary>The managed-format version diagnostic for an Eagle 9+ root, or null for a classic
    /// (pre-9) file so the classic path's diagnostics are exactly what they always were. Eagle 9
    /// is where the Fusion-managed format (urn attributes, <c>&lt;packages3d&gt;</c>) begins, so
    /// the note makes the provenance visible rather than silent.</summary>
    internal static string? VersionNote(XElement eagleRoot)
    {
        string? version = eagleRoot.Attribute("version")?.Value;
        if (string.IsNullOrEmpty(version))
            return null;
        int dot = version.IndexOf('.');
        string majorToken = dot >= 0 ? version[..dot] : version;
        if (!int.TryParse(majorToken, NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            || major < 9)
            return null;
        return $"The file declares Eagle version {version} — the newer managed (Fusion) format. "
            + "Its urn attributes and <packages3d> bindings are read; other schema drift is "
            + "additive attributes the reader does not consult.";
    }

    /// <summary>Parses one <c>&lt;library&gt;</c> ELEMENT into an <see cref="EagleLibrary"/> — the
    /// shared core: a <c>.lbr</c>'s single library, or one of a schematic's embedded
    /// <c>&lt;libraries&gt;</c> entries (which carry the SAME content under a <c>name</c>).</summary>
    /// <param name="library">The <c>&lt;library&gt;</c> element.</param>
    /// <param name="name">The library's name (empty for a bare <c>.lbr</c>).</param>
    /// <param name="versionNote">An optional leading diagnostic (the Eagle 9+ managed-format note —
    /// only the <c>.lbr</c> entry point supplies one, since the version attribute lives on the
    /// document root the embedded-library callers own).</param>
    internal static EagleLibrary ReadLibraryElement(
        XElement library, string name, string? versionNote = null)
    {
        var diagnostics = new List<string>();
        if (versionNote is not null)
            diagnostics.Add(versionNote);

        var packages = new Dictionary<string, EaglePackage>(StringComparer.Ordinal);
        var packagesElement = library.Element("packages");
        if (packagesElement is not null)
            foreach (var package in packagesElement.Elements("package"))
            {
                var parsed = ParsePackage(package, diagnostics);
                if (parsed is not null)
                    packages[parsed.Name] = parsed;
            }

        // The managed-library (Eagle 9 / Fusion) 3D-package bindings. Parsed AFTER the packages so
        // an instance naming an absent package can be diagnosed against what the library carries.
        var packages3d = new List<EaglePackage3d>();
        var packages3dElement = library.Element("packages3d");
        if (packages3dElement is not null)
            foreach (var package3d in packages3dElement.Elements("package3d"))
            {
                string p3dName = package3d.Attribute("name")?.Value ?? "";
                string? urn = package3d.Attribute("urn")?.Value;
                if (string.IsNullOrEmpty(urn))
                {
                    // The urn IS a package3d's identity (it is what a device's binding references),
                    // so one without it cannot be bound and is noted rather than half-kept.
                    diagnostics.Add(
                        $"A <package3d>{(p3dName.Length > 0 ? $" '{p3dName}'" : "")} has no urn "
                        + "(its identity) and was ignored.");
                    continue;
                }
                string type = package3d.Attribute("type")?.Value ?? "";
                var instanceNames = new List<string>();
                foreach (var instance in package3d.Element("packageinstances")?.Elements("packageinstance") ?? [])
                {
                    string? instanceName = instance.Attribute("name")?.Value;
                    if (string.IsNullOrEmpty(instanceName))
                        continue;
                    instanceNames.Add(instanceName);
                    if (!packages.ContainsKey(instanceName))
                        diagnostics.Add(
                            $"3D package '{p3dName}' ({urn}) binds package '{instanceName}', which "
                            + "the library does not contain.");
                }
                packages3d.Add(new EaglePackage3d(p3dName, urn, type, instanceNames));
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

        return new EagleLibrary(name, symbols, packages, deviceSets, packages3d, diagnostics);
    }

    /// <summary>Reads a <c>.lbr</c> file from disk.</summary>
    public static EagleLibrary ReadFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Read(File.ReadAllText(path));
    }

    /// <summary>Reads a <c>.lbr</c> file's text and loads one device — the convenience over
    /// <see cref="Read(string)"/> then <see cref="EagleLibrary.Load(string)"/>. The optional
    /// <paramref name="modelResolver"/> maps a bound <see cref="EaglePackage3d"/> to a local model
    /// file (see <see cref="EagleLibrary.Load(string, Func{EaglePackage3d, string?})"/>).</summary>
    public static LoadedPart Load(
        string xml, string deviceName, Func<EaglePackage3d, string?>? modelResolver = null) =>
        Read(xml).Load(deviceName, modelResolver);

    /// <summary>Reads a <c>.lbr</c> file from disk and loads one device.</summary>
    public static LoadedPart LoadFile(
        string path, string deviceName, Func<EaglePackage3d, string?>? modelResolver = null) =>
        ReadFile(path).Load(deviceName, modelResolver);

    // ---- assembling a device into a PartDefinition --------------------------

    internal static LoadedPart Assemble(
        EagleLibrary library, EagleDeviceSet set, EagleDevice device,
        Func<EaglePackage3d, string?>? modelResolver)
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

        // The managed-library 3D binding: a <package3dinstance> names an EaglePackage3d by URN.
        // The binding is DATA the file carries; the model FILE is Fusion cloud content, so a
        // ComponentModel3D is attached ONLY when the caller's resolver finds a local file that
        // exists — otherwise the URN is recorded in the diagnostics by name, never guessed.
        ComponentModel3D? model = null;
        foreach (var urn in device.Package3dUrns)
        {
            var package3d = library.Package3d(urn);
            if (package3d is null)
            {
                diagnostics.Add(
                    $"Device '{fullName}': binds 3D package urn '{urn}', which the library's "
                    + "<packages3d> does not declare; the binding was recorded without a model.");
                continue;
            }
            if (device.Package.Length > 0
                && !package3d.PackageNames.Contains(device.Package, StringComparer.Ordinal))
                diagnostics.Add(
                    $"Device '{fullName}': 3D package '{package3d.Name}' ({package3d.Urn}) does not "
                    + $"list package '{device.Package}' among its package instances; the device's "
                    + "own binding was honoured.");
            if (model is not null)
            {
                diagnostics.Add(
                    $"Device '{fullName}': binds a further 3D package '{package3d.Name}' "
                    + $"({package3d.Urn}); a definition carries ONE model, so it was recorded and "
                    + "not attached.");
                continue;
            }

            string? localPath = modelResolver?.Invoke(package3d);
            if (localPath is null)
            {
                diagnostics.Add(
                    $"Device '{fullName}': 3D package '{package3d.Name}' ({package3d.Urn}"
                    + $"{(package3d.Type.Length > 0 ? $", type '{package3d.Type}'" : "")}) is "
                    + "managed content whose model file lives in Fusion's cloud; "
                    + (modelResolver is null
                        ? "pass a modelResolver to EagleLibrary.Load to attach a local copy. "
                        : "the resolver found no local copy. ")
                    + "The binding is recorded and no 3D model is attached.");
            }
            else if (!File.Exists(localPath))
            {
                diagnostics.Add(
                    $"Device '{fullName}': the model resolver returned '{localPath}' for 3D package "
                    + $"'{package3d.Name}' ({package3d.Urn}), but no such file exists; the binding "
                    + "is recorded and no 3D model is attached.");
            }
            else
            {
                // The Eagle package3d carries no placement in the XML (Fusion aligns the model to
                // the footprint origin), so the local file seats at the identity placement.
                model = ComponentModel3D.FromFile(localPath);
            }
        }

        var definition = new PartDefinition(
            fullName, set.Prefix, pins, footprint, body: null, symbol: symbolValue, model: model);
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

            // The managed-library 3D binding: which package3d urn(s) this device references.
            var package3dUrns = new List<string>();
            foreach (var instance in
                device.Element("package3dinstances")?.Elements("package3dinstance") ?? [])
            {
                string? urn = instance.Attribute("package3d_urn")?.Value;
                if (string.IsNullOrEmpty(urn))
                    libraryDiagnostics.Add(
                        $"Deviceset '{name}' device '{deviceName}': a <package3dinstance> has no "
                        + "package3d_urn and was ignored.");
                else
                    package3dUrns.Add(urn);
            }

            devices.Add(new EagleDevice(deviceName, package, connects, package3dUrns));
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

/// <summary>A parsed Eagle device: the package it binds, the connects mapping symbol pins to
/// pads, and the managed-library 3D-package urns its <c>&lt;package3dinstance&gt;</c>s bind
/// (empty for a classic device).</summary>
internal sealed record EagleDevice(
    string Name, string Package, List<EagleConnect> Connects, List<string> Package3dUrns);

/// <summary>A parsed Eagle connect: gate + symbol pin name → package pad — the mapping that unifies
/// the three representations by pad NUMBER.</summary>
internal readonly record struct EagleConnect(string Gate, string Pin, string Pad);
