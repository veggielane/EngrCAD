namespace EngrCAD.Ecad;

/// <summary>
/// A part loaded from interchange files: the assembled <see cref="PartDefinition"/> (pins +
/// footprint + symbol, unified by pin NUMBER), the <see cref="PinIdentity"/> report of whether
/// its three representations agree, and the diagnostics the readers accumulated.
/// </summary>
/// <param name="Definition">The assembled part type.</param>
/// <param name="Identity">The symbol == footprint == netlist identity check.</param>
/// <param name="Diagnostics">What the symbol and footprint readers ignored or approximated.</param>
public sealed record LoadedPart(
    PartDefinition Definition,
    PinIdentityReport Identity,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Loads a component from KiCad interchange so it arrives with its schematic SYMBOL, its
/// FOOTPRINT and its pins — unified by pin NUMBER into one <see cref="PartDefinition"/> (symbol
/// pin "1" == pad "1" == netlist pin "1"). This is the point of loading symbol and footprint
/// together: the one-declaration identity extended to the drawn symbol, verified by a
/// <see cref="PinIdentity"/> check whose result travels on the returned <see cref="LoadedPart"/>.
///
/// <para>The <see cref="PartDefinition.Body"/> (the 3D model) stays absent — a KiCad
/// <c>.wrl</c>/<c>.step</c> model reference is out of v1 scope (its path is noted but not
/// loaded).</para>
/// </summary>
public static class ComponentLibrary
{
    /// <summary>Loads a part from a symbol file and (optionally) a footprint file on disk.</summary>
    /// <param name="symPath">The <c>.kicad_sym</c> path.</param>
    /// <param name="modPath">The <c>.kicad_mod</c> path, or null for a symbol-only (data-only
    /// plus symbol) part.</param>
    /// <param name="symbolName">The library symbol to read; null reads the first.</param>
    public static LoadedPart Load(string symPath, string? modPath = null, string? symbolName = null)
    {
        ArgumentNullException.ThrowIfNull(symPath);
        string symText = File.ReadAllText(symPath);
        string? modText = modPath is null ? null : File.ReadAllText(modPath);
        return Read(symText, modText, symbolName);
    }

    /// <summary>Loads a part, resolving its footprint from a KiCad <c>.pretty</c> directory: the
    /// symbol's referenced footprint name (<c>"Lib:Name"</c>) picks <c>Name.kicad_mod</c> from
    /// the directory. When the file is absent, a symbol-only part is returned with a diagnostic.</summary>
    /// <param name="symPath">The <c>.kicad_sym</c> path.</param>
    /// <param name="prettyDirectory">A directory of <c>.kicad_mod</c> files (a <c>.pretty</c>).</param>
    /// <param name="symbolName">The library symbol to read; null reads the first.</param>
    public static LoadedPart LoadFromPretty(string symPath, string prettyDirectory, string? symbolName = null)
    {
        ArgumentNullException.ThrowIfNull(symPath);
        ArgumentNullException.ThrowIfNull(prettyDirectory);
        var symbol = KiCadSymbolReader.Read(File.ReadAllText(symPath), symbolName);

        string? modText = null;
        var extra = new List<string>();
        if (symbol.FootprintName is { } reference)
        {
            // "Lib:Name" -> "Name.kicad_mod"
            string bare = reference.Contains(':') ? reference[(reference.IndexOf(':') + 1)..] : reference;
            string modPath = Path.Combine(prettyDirectory, bare + ".kicad_mod");
            if (File.Exists(modPath))
                modText = File.ReadAllText(modPath);
            else
                extra.Add(
                    $"Footprint '{reference}' was not found as '{bare}.kicad_mod' in "
                    + $"'{prettyDirectory}'; loaded symbol-only.");
        }
        else
        {
            extra.Add("The symbol references no footprint; loaded symbol-only.");
        }

        return Assemble(symbol, modText is null ? null : KiCadFootprintReader.Read(modText), extra);
    }

    /// <summary>Loads a part from the TEXT of a symbol file and (optionally) a footprint file —
    /// the file-free overload the docs and tests use.</summary>
    /// <param name="symText">The <c>.kicad_sym</c> text.</param>
    /// <param name="modText">The <c>.kicad_mod</c> text, or null for symbol-only.</param>
    /// <param name="symbolName">The library symbol to read; null reads the first.</param>
    public static LoadedPart Read(string symText, string? modText = null, string? symbolName = null)
    {
        ArgumentNullException.ThrowIfNull(symText);
        var symbol = KiCadSymbolReader.Read(symText, symbolName);
        var footprint = modText is null ? null : KiCadFootprintReader.Read(modText);
        return Assemble(symbol, footprint, []);
    }

    private static LoadedPart Assemble(KiCadSymbol symbol, KiCadFootprint? footprint, List<string> extra)
    {
        var diagnostics = new List<string>(extra);
        diagnostics.AddRange(symbol.Diagnostics);
        if (footprint is not null)
        {
            diagnostics.AddRange(footprint.Diagnostics);
            // A referenced footprint name that does not match the loaded footprint is worth a
            // note — the pair may be mismatched (the identity check is by NUMBER, not name, so
            // this cannot fail the load, only inform).
            if (symbol.FootprintName is { } reference)
            {
                string bare = reference.Contains(':') ? reference[(reference.IndexOf(':') + 1)..] : reference;
                if (!string.Equals(bare, footprint.Footprint.Name, StringComparison.Ordinal))
                    diagnostics.Add(
                        $"The symbol references footprint '{reference}' but the footprint loaded is "
                        + $"'{footprint.Footprint.Name}'.");
            }
        }

        var definition = new PartDefinition(
            symbol.Symbol.Name, symbol.ReferencePrefix, symbol.Pins,
            footprint: footprint?.Footprint, body: null, symbol: symbol.Symbol);

        var identity = PinIdentity.Check(definition);
        return new LoadedPart(definition, identity, diagnostics);
    }
}
