using System.Text;

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

    /// <summary>
    /// The BOM as an aligned text table: quantity, item, kind, and where the occurrences
    /// are (the first few paths, then a count of the rest).
    /// </summary>
    public string ToText(int pathsShown = 3)
    {
        if (Lines.Count == 0)
            return "(empty bill of materials)";

        var rows = Lines
            .Select(line => (
                Qty: line.Quantity.ToString(),
                line.Item,
                Kind: line.IsHardware ? "catalogue" : "made",
                Where: Where(line, pathsShown)))
            .ToList();

        int qty = Math.Max(3, rows.Max(r => r.Qty.Length));
        int item = Math.Max(4, rows.Max(r => r.Item.Length));
        int kind = Math.Max(4, rows.Max(r => r.Kind.Length));

        var text = new StringBuilder();
        text.Append("QTY".PadLeft(qty)).Append("  ").Append("ITEM".PadRight(item)).Append("  ")
            .Append("KIND".PadRight(kind)).Append("  ").Append("WHERE").Append('\n');
        foreach (var row in rows)
        {
            text.Append(row.Qty.PadLeft(qty)).Append("  ").Append(row.Item.PadRight(item)).Append("  ")
                .Append(row.Kind.PadRight(kind)).Append("  ").Append(row.Where).Append('\n');
        }
        text.Append('\n')
            .Append($"{Lines.Count} item{(Lines.Count == 1 ? "" : "s")}, ")
            .Append($"{TotalQuantity} occurrence{(TotalQuantity == 1 ? "" : "s")}").Append('\n');
        return text.ToString();
    }

    private static string Where(BomLine line, int pathsShown)
    {
        if (pathsShown <= 0 || line.Paths.Count == 0)
            return "";
        if (line.Paths.Count <= pathsShown)
            return string.Join(", ", line.Paths);
        return string.Join(", ", line.Paths.Take(pathsShown)) + $", +{line.Paths.Count - pathsShown} more";
    }

    /// <summary>The BOM as CSV (header row; every occurrence path listed, ';'-separated
    /// inside the quoted field) — RFC 4180 quoting, so item names may contain commas.</summary>
    public string ToCsv()
    {
        var csv = new StringBuilder("Quantity,Item,Kind,Paths\n");
        foreach (var line in Lines)
        {
            csv.Append(line.Quantity).Append(',')
               .Append(Quote(line.Item)).Append(',')
               .Append(line.IsHardware ? "catalogue" : "made").Append(',')
               .Append(Quote(string.Join(";", line.Paths))).Append('\n');
        }
        return csv.ToString();
    }

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
