using System.Text;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// One line of a <see cref="Bom">bill of materials</see>: a distinct <see cref="Part"/>,
/// how many times it occurs, and where those occurrences are.
/// <para>Lines group by part <b>reference</b>, which is the document model's own notion of
/// "the same thing": an assembly holds references, so N placements of one part (or one
/// <see cref="HardwareComponent"/>, whose <see cref="HardwareComponent.ToPart"/> is
/// cached) are one line with quantity N. Two separately constructed parts that happen to
/// share a name stay two lines — honestly, because they are two parts;
/// <see cref="Bom.ByItem"/> rolls those together for a purchasing view.</para>
/// </summary>
/// <param name="Part">The part this line counts.</param>
/// <param name="Quantity">How many occurrences reference it.</param>
/// <param name="Paths">The occurrence paths of those instances, in flattening order.</param>
public sealed record BomLine(Part Part, int Quantity, IReadOnlyList<string> Paths)
{
    /// <summary>The line's item name: the catalogue designation for hardware, else the
    /// part name (which for hardware is the designation anyway).</summary>
    public string Item => Part.Hardware?.Designation ?? Part.Name;

    /// <summary>The catalogue item this line is, when the part came from one — null for
    /// designed parts. Its presence is what makes a line "bought-in".</summary>
    public HardwareComponent? Hardware => Part.Hardware;

    /// <summary>True when the line is a catalogue component rather than a designed part.</summary>
    public bool IsHardware => Part.Hardware is not null;

    /// <summary>What the part is made of, when it says — the whole material, not just its
    /// name, so a purchasing view can reach the density and a report the datasheet figure.
    /// A projection of <see cref="Part.Material"/>, so no line record had to change.</summary>
    public Material? Material => Part.Material;

    /// <summary>The stock length this part is cut from, in millimetres, when it states one
    /// (frame members via <see cref="Weldment"/>); null otherwise. A projection of
    /// <see cref="Part.CutLength"/> — the same pattern as <see cref="Material"/> — and the
    /// reason a BOM of a weldment IS its cut list.</summary>
    public double? CutLength => Part.CutLength;

    /// <summary>
    /// The mass of ONE of these parts, in grams, or null when the part states no material.
    /// <para><b>Evaluates geometry</b> (it measures the part's cached solid or display mesh),
    /// which is why nothing in a default BOM calls it: a bill of materials is a cheap
    /// document-model walk and must stay one. <see cref="Bom.ToText"/> and
    /// <see cref="Bom.ToCsv"/> take it as an opt-in.</para>
    /// </summary>
    public double? UnitMassGrams => Part.MassGrams();

    /// <summary>The mass of this line's whole quantity, in grams; null when the part states
    /// no material. Same evaluation cost as <see cref="UnitMassGrams"/>, once.</summary>
    public double? TotalMassGrams => UnitMassGrams * Quantity;
}

/// <summary>
/// A node of a <see cref="Bom.Structured">structured (indented) bill of materials</see>:
/// one item at one level of the assembly tree, with the sub-items it contains.
/// </summary>
/// <param name="Name">The item's own name (the part or assembly name — NOT the occurrence
/// name, which auto-suffixes; repeat placements are counted, not listed separately).</param>
/// <param name="Part">The part, when this node is a leaf.</param>
/// <param name="Assembly">The sub-assembly, when this node is a branch.</param>
/// <param name="Quantity">Occurrences of this item inside ONE instance of its parent.</param>
/// <param name="TotalQuantity">Occurrences of this item in the whole tree —
/// <see cref="Quantity"/> multiplied down the chain of parents.</param>
/// <param name="Children">Sub-items (empty for a part).</param>
public sealed record BomNode(
    string Name,
    Part? Part,
    Assembly? Assembly,
    int Quantity,
    int TotalQuantity,
    IReadOnlyList<BomNode> Children)
{
    /// <summary>This node and every descendant, depth-first (the root first).</summary>
    public IEnumerable<(BomNode Node, int Depth)> Flatten(int depth = 0)
    {
        yield return (this, depth);
        foreach (var child in Children)
        {
            foreach (var descendant in child.Flatten(depth + 1))
                yield return descendant;
        }
    }
}

/// <summary>
/// One row of a family table: the bill of materials a part's model produces under one of its
/// <see cref="ConfigurationSet">configurations</see>.
/// </summary>
/// <param name="Configuration">The configuration this row was measured under.</param>
/// <param name="Bom">The bill of materials. Its item names, quantities and paths are frozen;
/// its lines' MASS projections are not (see <see cref="Bom.ByConfiguration(Part, Func{Bom},
/// bool)"/>).</param>
/// <param name="TotalMassGrams">The whole list's mass in grams, measured WHILE this
/// configuration was active — null when mass was not asked for, or when no line stated a
/// material.</param>
/// <param name="Warnings">Anything the activation reported: a value the history declined, or
/// a regeneration that did not complete.</param>
public sealed record ConfigurationBom(
    string Configuration, Bom Bom, double? TotalMassGrams, IReadOnlyList<string> Warnings);

/// <summary>
/// A bill of materials: what a scene, tab or assembly is made of and how many of each.
///
/// <code>
/// var bom = Bom.For(assembly);
/// Console.WriteLine(bom.ToText());
/// File.WriteAllText("bom.csv", bom.ToCsv());
/// </code>
///
/// <para><b>Built from the flattening, like everything else.</b> A BOM counts
/// <see cref="PartInstance"/>s — the same list viewers render and exporters write — so
/// nested sub-assemblies roll up for free: an instance is an instance however deep it
/// sits, and its <see cref="PartInstance.Path"/> says where it is. There is no second
/// traversal of the assembly tree to keep in step with <see cref="Assembly.Flatten()"/>.
/// The one place the tree itself is walked is <see cref="Structured"/>, whose per-level
/// quantities are checked against the flat totals by test.</para>
/// </summary>
public sealed class Bom
{
    private Bom(IReadOnlyList<BomLine> lines) => Lines = lines;

    /// <summary>The lines, ordered by item name then by first appearance.</summary>
    public IReadOnlyList<BomLine> Lines { get; }

    /// <summary>Distinct parts listed.</summary>
    public int LineCount => Lines.Count;

    /// <summary>Total number of occurrences across every line.</summary>
    public int TotalQuantity => Lines.Sum(l => l.Quantity);

    /// <summary>The catalogue lines only (parts that came from a
    /// <see cref="HardwareComponent"/>) — the "bought-in" half of the list.</summary>
    public IEnumerable<BomLine> Hardware => Lines.Where(l => l.IsHardware);

    /// <summary>The designed (non-catalogue) lines.</summary>
    public IEnumerable<BomLine> Manufactured => Lines.Where(l => !l.IsHardware);

    /// <summary>The bill of materials for one assembly (paths rooted at its name).</summary>
    public static Bom For(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return For(assembly.Flatten());
    }

    /// <summary>The bill of materials for one tab — loose parts and assemblies together.</summary>
    public static Bom For(Tab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return For(tab.Instances());
    }

    /// <summary>The bill of materials for a whole scene (every tab).</summary>
    public static Bom For(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return For(scene.AllInstances);
    }

    /// <summary>The bill of materials for any instance list — the general entry point
    /// (<c>assembly.Flatten()</c>, <c>tab.Instances()</c>, or a filtered subset).</summary>
    public static Bom For(IEnumerable<PartInstance> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);
        var order = new List<Part>();
        var paths = new Dictionary<Part, List<string>>();   // Part has reference identity
        foreach (var instance in instances)
        {
            if (!paths.TryGetValue(instance.Part, out var list))
            {
                paths[instance.Part] = list = [];
                order.Add(instance.Part);
            }
            list.Add(instance.Path);
        }

        var lines = order
            .Select(part => new BomLine(part, paths[part].Count, paths[part]))
            .OrderBy(line => line.Item, StringComparer.OrdinalIgnoreCase)
            .ThenBy(line => order.IndexOf(line.Part))
            .ToList();
        return new Bom(lines);
    }

    // ---- per configuration ----------------------------------------------

    /// <summary>
    /// One <see cref="Bom">bill of materials</see> per configuration of a part — the family
    /// table.
    ///
    /// <para><b>What actually differs between configurations, on a document whose parts are
    /// SHARED.</b> A <see cref="Bom"/> groups by part REFERENCE, and a configuration changes
    /// a part's parameters rather than replacing the object, so the configured part is the
    /// same line in every row. What CAN differ is the rest of the model: a
    /// <c>ComponentFeature</c> places catalogue hardware, so an M4 variant lists M4 screws
    /// and an M10 variant M10 ones, and a suppressed placement drops its occurrence
    /// altogether. That is why each row is built from a fresh flatten rather than from one
    /// captured instance list.</para>
    ///
    /// <para><b>Item names, quantities and paths are frozen; per-line MASS is not.</b>
    /// <see cref="BomLine.UnitMassGrams"/> is a lazy projection that measures the part's
    /// CURRENT geometry, and this walk restores the part when it is done — so reading it off
    /// a returned row afterwards reports the restored configuration's mass for every row.
    /// Ask for <paramref name="mass"/> and read
    /// <see cref="ConfigurationBom.TotalMassGrams"/>, which was measured while that
    /// configuration was active.</para>
    /// </summary>
    /// <param name="part">The configured part.</param>
    /// <param name="build">How to build one bill of materials — re-invoked per
    /// configuration, so it must RE-read the model (<c>() =&gt; Bom.For(scene)</c>).</param>
    /// <param name="mass">Measures each row's total mass while its configuration is active
    /// (evaluates geometry — the BOM's own opt-in rule).</param>
    public static IReadOnlyList<ConfigurationBom> ByConfiguration(
        Part part, Func<Bom> build, bool mass = false)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(build);
        var set = part.Configurations ?? throw new ArgumentException(
            $"Part '{part.Name}' has no feature history, so it has no configurations.", nameof(part));
        if (set.Count == 0)
            throw new ArgumentException($"Part '{part.Name}' has no configurations.", nameof(part));

        // A family table is an ANALYSIS, not an edit: it drives the model through every
        // configuration and puts it back exactly as it found it (DesignStudy's rule), which
        // is why the live parameter values are captured rather than the active
        // configuration's stored ones.
        string? active = set.Active;
        string parameters = part.History!.SaveParameters();
        var rows = new List<ConfigurationBom>(set.Count);
        try
        {
            foreach (var configuration in set)
            {
                var applied = set.Activate(configuration.Name);
                var warnings = new List<string>(applied.Warnings);
                if (!applied.Succeeded)
                    warnings.Add($"the model did not rebuild:\n{applied.Regeneration}");
                var bom = build();
                double? total = null;
                if (mass)
                {
                    var known = bom.Lines.Select(line => line.TotalMassGrams).Where(m => m is not null).ToList();
                    if (known.Count > 0)
                        total = known.Sum(m => m!.Value);
                }
                rows.Add(new ConfigurationBom(configuration.Name, bom, total, warnings));
            }
        }
        finally
        {
            set.SetActiveName(active);
            part.History!.LoadParameters(parameters);
            part.Regenerate();
        }
        return rows;
    }

    /// <summary>A bill of materials per configuration over a whole scene.</summary>
    public static IReadOnlyList<ConfigurationBom> ByConfiguration(Part part, Scene scene, bool mass = false)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return ByConfiguration(part, () => For(scene), mass);
    }

    /// <summary>A bill of materials per configuration over one tab.</summary>
    public static IReadOnlyList<ConfigurationBom> ByConfiguration(Part part, Tab tab, bool mass = false)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return ByConfiguration(part, () => For(tab), mass);
    }

    /// <summary>A bill of materials per configuration over one assembly.</summary>
    public static IReadOnlyList<ConfigurationBom> ByConfiguration(Part part, Assembly assembly, bool mass = false)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return ByConfiguration(part, () => For(assembly), mass);
    }

    /// <summary>The family table as text: one section per configuration, each the ordinary
    /// <see cref="ToText(int, bool)"/> table, with the frozen total mass in the heading when
    /// one was measured.</summary>
    public static string ToText(
        IEnumerable<ConfigurationBom> configurations, int pathsShown = 3)
    {
        ArgumentNullException.ThrowIfNull(configurations);
        var text = new StringBuilder();
        foreach (var row in configurations)
        {
            text.Append("== ").Append(row.Configuration);
            if (row.TotalMassGrams is { } grams)
                text.Append($" — {grams:0.###} g");
            text.Append(" ==\n");
            foreach (string warning in row.Warnings)
                text.Append("   ! ").Append(warning).Append('\n');
            text.Append(row.Bom.ToText(pathsShown)).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>
    /// The same list rolled up by item name — the purchasing view, where two separately
    /// constructed but identically designated components are one order line. Returns
    /// <c>(item, quantity, hardware)</c> triples; <c>hardware</c> is the component when
    /// every part behind the name is that same catalogue item.
    /// </summary>
    public IReadOnlyList<(string Item, int Quantity, HardwareComponent? Hardware)> ByItem() =>
    [
        .. Lines
            .GroupBy(line => line.Item, StringComparer.OrdinalIgnoreCase)
            .Select(group => (
                Item: group.Key,
                Quantity: group.Sum(line => line.Quantity),
                Hardware: group.Select(line => line.Hardware).FirstOrDefault(h => h is not null)))
            .OrderBy(entry => entry.Item, StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>
    /// The indented bill of materials: the assembly tree with per-level quantities.
    /// Occurrences at one level that reference the same part or the same sub-assembly
    /// collapse into one node whose <see cref="BomNode.Quantity"/> is the count;
    /// <see cref="BomNode.TotalQuantity"/> multiplies that down the chain of parents, so
    /// a sub-assembly placed twice doubles everything inside it. The leaf totals agree
    /// with <see cref="For(Assembly)"/> by construction — both count the same occurrences.
    /// </summary>
    public static BomNode Structured(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return Node(assembly, quantity: 1, parentTotal: 1);
    }

    private static BomNode Node(Assembly assembly, int quantity, int parentTotal)
    {
        int total = quantity * parentTotal;
        var children = new List<BomNode>();
        // Group by referenced item (reference identity), keeping first-appearance order —
        // an assembly's occurrence list IS the design's order and a BOM should read the
        // same way.
        var seen = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        var order = new List<object>();
        foreach (var occurrence in assembly.Occurrences)
        {
            object item = (object?)occurrence.Part ?? occurrence.SubAssembly!;
            if (seen.TryGetValue(item, out int count))
                seen[item] = count + 1;
            else
            {
                seen[item] = 1;
                order.Add(item);
            }
        }

        foreach (var item in order)
        {
            int count = seen[item];
            children.Add(item switch
            {
                Part part => new BomNode(part.Name, part, null, count, count * total, []),
                Assembly sub => Node(sub, count, total),
                _ => throw new InvalidOperationException("An occurrence places a part or an assembly."),
            });
        }
        return new BomNode(assembly.Name, null, assembly, quantity, total, children);
    }

    /// <summary>True when at least one line's part states a <see cref="Part.Material"/> —
    /// what decides whether the reports carry a MATERIAL column.</summary>
    public bool HasMaterials => Lines.Any(l => l.Material is not null);

    /// <summary>True when at least one line's part states a <see cref="Part.CutLength"/> —
    /// what decides whether the reports carry a CUT column. A scene with no frame members
    /// prints byte-identically what it always did (the MATERIAL-column rule).</summary>
    public bool HasCutLengths => Lines.Any(l => l.CutLength is not null);

    /// <summary>
    /// The BOM as an aligned text table: quantity, item, kind, and where the occurrences
    /// are (the first few paths, then a count of the rest).
    ///
    /// <para>A <b>MATERIAL</b> column appears when any line's part states one, and not
    /// otherwise — a column that would be empty on every row is not printed, so a scene that
    /// uses no materials produces byte-identically what it always did. <b>MASS</b> and
    /// <b>TOTAL</b> columns (in grams, with a total at the foot) are opt-in rather than
    /// automatic, because they are the only part of a bill of materials that evaluates
    /// geometry.</para>
    /// </summary>
    /// <param name="pathsShown">How many occurrence paths to list before "+N more".</param>
    /// <param name="mass">Adds per-item and per-line mass in grams. Costs one mass-property
    /// evaluation per distinct part (over the caches a <see cref="Part"/> already holds);
    /// lines whose part states no material show "-".</param>
    public string ToText(int pathsShown = 3, bool mass = false)
    {
        if (Lines.Count == 0)
            return "(empty bill of materials)";

        bool materials = HasMaterials;
        bool cuts = HasCutLengths;
        var rows = Lines
            .Select(line => (
                Qty: line.Quantity.ToString(),
                line.Item,
                Kind: line.IsHardware ? "catalogue" : "made",
                Material: line.Material?.Name ?? "-",
                Cut: Millimetres(line.CutLength),
                Unit: mass ? Grams(line.UnitMassGrams) : "",
                Total: mass ? Grams(line.TotalMassGrams) : "",
                Where: Where(line, pathsShown)))
            .ToList();

        int qty = Math.Max(3, rows.Max(r => r.Qty.Length));
        int item = Math.Max(4, rows.Max(r => r.Item.Length));
        int kind = Math.Max(4, rows.Max(r => r.Kind.Length));
        int material = Math.Max(8, rows.Max(r => r.Material.Length));
        int cut = Math.Max(8, rows.Max(r => r.Cut.Length));
        int unit = Math.Max(8, rows.Max(r => r.Unit.Length));
        int total = Math.Max(9, rows.Max(r => r.Total.Length));

        var text = new StringBuilder();
        text.Append("QTY".PadLeft(qty)).Append("  ").Append("ITEM".PadRight(item)).Append("  ")
            .Append("KIND".PadRight(kind)).Append("  ");
        if (materials)
            text.Append("MATERIAL".PadRight(material)).Append("  ");
        if (cuts)
            text.Append("CUT (mm)".PadLeft(cut)).Append("  ");
        if (mass)
            text.Append("MASS (g)".PadLeft(unit)).Append("  ").Append("TOTAL (g)".PadLeft(total)).Append("  ");
        text.Append("WHERE").Append('\n');

        foreach (var row in rows)
        {
            text.Append(row.Qty.PadLeft(qty)).Append("  ").Append(row.Item.PadRight(item)).Append("  ")
                .Append(row.Kind.PadRight(kind)).Append("  ");
            if (materials)
                text.Append(row.Material.PadRight(material)).Append("  ");
            if (cuts)
                text.Append(row.Cut.PadLeft(cut)).Append("  ");
            if (mass)
                text.Append(row.Unit.PadLeft(unit)).Append("  ").Append(row.Total.PadLeft(total)).Append("  ");
            text.Append(row.Where).Append('\n');
        }

        text.Append('\n')
            .Append($"{Lines.Count} item{(Lines.Count == 1 ? "" : "s")}, ")
            .Append($"{TotalQuantity} occurrence{(TotalQuantity == 1 ? "" : "s")}");
        if (cuts)
        {
            // The cut-list bottom line: total stock across quantities. Only the lines
            // stating a cut length contribute, and like the mass footer it says so
            // when some do not.
            var cutLines = Lines.Where(l => l.CutLength is not null).ToList();
            double stock = cutLines.Sum(l => l.CutLength!.Value * l.Quantity);
            text.Append($", {stock:0.##} mm of stock");
            if (cutLines.Count < Lines.Count)
                text.Append($" over the {cutLines.Count} of {Lines.Count} items stating a cut length");
        }
        if (mass)
        {
            // Sum only the lines that HAVE a mass, and say so when some do not: a total that
            // silently skipped the unstated parts would read as the assembly's weight.
            var known = Lines.Select(l => l.TotalMassGrams).Where(m => m is not null).ToList();
            if (known.Count > 0)
            {
                text.Append($", {known.Sum(m => m!.Value):0.###} g");
                if (known.Count < Lines.Count)
                    text.Append($" over the {known.Count} of {Lines.Count} items stating a material");
            }
        }
        text.Append('\n');
        return text.ToString();
    }

    private static string Grams(double? grams) => grams is { } g ? g.ToString("0.###") : "-";

    private static string Millimetres(double? millimetres) =>
        millimetres is { } mm ? mm.ToString("0.##") : "-";

    private static string Where(BomLine line, int pathsShown)
    {
        if (pathsShown <= 0 || line.Paths.Count == 0)
            return "";
        if (line.Paths.Count <= pathsShown)
            return string.Join(", ", line.Paths);
        return string.Join(", ", line.Paths.Take(pathsShown)) + $", +{line.Paths.Count - pathsShown} more";
    }

    /// <summary>The BOM as CSV (header row; every occurrence path listed, ';'-separated
    /// inside the quoted field) — RFC 4180 quoting, so item names may contain commas.
    /// <para>A <c>Material</c> column appears when any line states one, and mass columns when
    /// <paramref name="mass"/> is asked for — the same two rules
    /// <see cref="ToText(int, bool)"/> follows, so a scene with no materials writes exactly
    /// the file it always did.</para></summary>
    /// <param name="mass">Adds <c>UnitMassGrams</c> and <c>TotalMassGrams</c> columns; empty
    /// for a line whose part states no material. Evaluates geometry.</param>
    public string ToCsv(bool mass = false)
    {
        bool materials = HasMaterials;
        bool cuts = HasCutLengths;
        var csv = new StringBuilder("Quantity,Item,Kind");
        if (materials)
            csv.Append(",Material");
        if (cuts)
            csv.Append(",CutLengthMm");
        if (mass)
            csv.Append(",UnitMassGrams,TotalMassGrams");
        csv.Append(",Paths\n");

        foreach (var line in Lines)
        {
            csv.Append(line.Quantity).Append(',')
               .Append(Quote(line.Item)).Append(',')
               .Append(line.IsHardware ? "catalogue" : "made");
            if (materials)
                csv.Append(',').Append(Quote(line.Material?.Name ?? ""));
            if (cuts)
                csv.Append(',').Append(Csv(line.CutLength)); // unknown = EMPTY cell, never a zero
            if (mass)
            {
                csv.Append(',').Append(Csv(line.UnitMassGrams))
                   .Append(',').Append(Csv(line.TotalMassGrams));
            }
            csv.Append(',').Append(Quote(string.Join(";", line.Paths))).Append('\n');
        }
        return csv.ToString();
    }

    // An unknown mass is an EMPTY cell, not a zero: a spreadsheet sums zeros silently.
    private static string Csv(double? grams) =>
        grams is { } g ? g.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "";

    /// <summary>The structured BOM as indented text (one line per node, level quantity
    /// and rolled-up total).</summary>
    public static string ToText(BomNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var text = new StringBuilder();
        foreach (var (node, depth) in root.Flatten())
        {
            text.Append(node.TotalQuantity.ToString().PadLeft(5)).Append("  ")
                .Append(new string(' ', depth * 2)).Append(node.Name);
            if (depth > 0 && node.Quantity > 1)
                text.Append($"  (x{node.Quantity} per parent)");
            text.Append('\n');
        }
        return text.ToString();
    }

    private static string Quote(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    public override string ToString() => ToText();
}
