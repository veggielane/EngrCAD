using System.Globalization;
using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Ecad;

/// <summary>
/// One bus ENTRY — the little diagonal stub that rips a member wire off (or onto) a bus. It is
/// two points: <see cref="OnBus"/> on the thick bus wire and <see cref="OnWire"/> on the signal
/// wire, drawn as one segment from one to the other (the standard 45° entry when the caller
/// places the two points at 45°). An entry is DRAWING SUGAR only — it is never part of the wire
/// graph, so it can never merge two signal nets (see <see cref="SchematicBus"/>).
/// </summary>
/// <param name="OnBus">The point on the thick bus wire, sheet mm.</param>
/// <param name="OnWire">The point on the signal wire the entry rips to, sheet mm.</param>
public readonly record struct SchematicBusEntry(Vector2d OnBus, Vector2d OnWire);

/// <summary>
/// A caller-declared BUS on a schematic sheet: a labelled bundle of signal nets drawn as a THICK
/// wire with diagonal ENTRY stubs ripping members off it. The label is a bus-VECTOR
/// <c>NAME[first..last]</c> (KiCad's notation, member <c>NAME</c>+i — so <c>DATA[0..3]</c> is
/// DATA0..DATA3, and a reversed <c>DATA[3..0]</c> the same four in the drawn direction), or — via
/// <see cref="Group"/> — arbitrary group / alias text (<c>"{SDA SCL}"</c>, <c>"PCI"</c>) with the
/// member names stated explicitly.
///
/// <para><b>It is DRAWING SUGAR</b> — the members are the signal wires' OWN labels, exactly as
/// the <see cref="KiCadSchReader">bus import</see> found: on one sheet a ripped tap's net is its
/// own local label, so the bus draws a bundle but connects NOTHING. Its line-work is
/// EXCLUDED from <see cref="SchematicDrawing.Verify"/>'s signal-connectivity reconstruction (a
/// bus wire touching two member wires cannot merge their nets), which is what keeps the drawn
/// sheet an honest view of the graph.</para>
///
/// <para><b>Caller-declared, never auto-routed</b>: the caller gives the bus wire polyline and
/// the entry stubs — the schematic sheet is caller-placed line work (the documented contract),
/// exactly as a <see cref="SchematicPlacement"/> gives symbol poses.</para>
/// </summary>
public sealed class SchematicBus
{
    /// <summary>Declares a bus.</summary>
    /// <param name="name">The bus base name (<c>"DATA"</c>); the members are <c>NAME</c>+i.</param>
    /// <param name="first">The first vector index (inclusive).</param>
    /// <param name="last">The last vector index (inclusive); may be below <paramref name="first"/>
    /// for a reversed range, which expands to the same members in the drawn direction.</param>
    /// <param name="path">The bus wire polyline, sheet mm — at least two points, rendered THICK.</param>
    /// <param name="entries">The diagonal entry stubs ripping members off the bus (may be empty).</param>
    /// <param name="labelPosition">Where the <c>NAME[first..last]</c> label anchors; null anchors it
    /// at the LAST path point (the bus's open end).</param>
    /// <param name="labelAnchor">Which end of the label the position is.</param>
    /// <exception cref="ArgumentException">The name is empty, or the path has fewer than two
    /// points — each refused BY NAME.</exception>
    public SchematicBus(
        string name, int first, int last,
        IReadOnlyList<Vector2d> path,
        IReadOnlyList<SchematicBusEntry>? entries = null,
        Vector2d? labelPosition = null,
        SheetTextAnchor labelAnchor = SheetTextAnchor.Left)
        : this(
            name,
            string.Create(CultureInfo.InvariantCulture, $"{name}[{first}..{last}]"),
            ExpandMembers(name, first, last), path, entries, labelPosition, labelAnchor)
    {
        First = first;
        Last = last;
    }

    /// <summary>Declares a bus GROUP / ALIAS — a bundle whose drawn label is arbitrary text
    /// (<c>"{SDA SCL}"</c>, or a bare alias name like <c>"PCI"</c>) and whose member net names are
    /// stated EXPLICITLY (what the group / alias means; the drawing does not parse the label). The
    /// vector constructor stays the spelling for a <c>NAME[m..n]</c> bundle.</summary>
    /// <param name="label">The drawn bundle label text.</param>
    /// <param name="members">The member net names, each a non-empty plain net name.</param>
    /// <param name="path">The bus wire polyline, sheet mm — at least two points, rendered THICK.</param>
    /// <param name="entries">The diagonal entry stubs ripping members off the bus (may be empty).</param>
    /// <param name="labelPosition">Where the label anchors; null anchors it at the LAST path point.</param>
    /// <param name="labelAnchor">Which end of the label the position is.</param>
    /// <exception cref="ArgumentException">The label is empty, the member list is empty or carries an
    /// empty name, or the path has fewer than two points — each refused BY NAME.</exception>
    public static SchematicBus Group(
        string label, IReadOnlyList<string> members,
        IReadOnlyList<Vector2d> path,
        IReadOnlyList<SchematicBusEntry>? entries = null,
        Vector2d? labelPosition = null,
        SheetTextAnchor labelAnchor = SheetTextAnchor.Left)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count == 0)
            throw new ArgumentException($"Bus group '{label}' declares no members.", nameof(members));
        if (members.Any(string.IsNullOrEmpty))
            throw new ArgumentException(
                $"Bus group '{label}' has an empty member name.", nameof(members));
        return new SchematicBus(label, label, [.. members], path, entries, labelPosition, labelAnchor);
    }

    private SchematicBus(
        string name, string label, IReadOnlyList<string> members,
        IReadOnlyList<Vector2d> path,
        IReadOnlyList<SchematicBusEntry>? entries,
        Vector2d? labelPosition,
        SheetTextAnchor labelAnchor)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count < 2)
            throw new ArgumentException(
                $"Bus '{name}' needs a wire polyline of at least two points to draw.", nameof(path));

        Name = name;
        Label = label;
        Path = [.. path];
        Entries = entries is null ? [] : [.. entries];
        LabelPosition = labelPosition ?? Path[^1];
        LabelAnchor = labelAnchor;
        Members = members;
    }

    /// <summary>The bus base name (<c>"DATA"</c>) — for a <see cref="Group"/> bus, the label text.</summary>
    public string Name { get; }

    /// <summary>The first vector index (inclusive); 0 for a <see cref="Group"/> bus (whose members
    /// are stated explicitly, not expanded from a range).</summary>
    public int First { get; }

    /// <summary>The last vector index (inclusive); 0 for a <see cref="Group"/> bus.</summary>
    public int Last { get; }

    /// <summary>The bus wire polyline, sheet mm — rendered as a THICK line.</summary>
    public IReadOnlyList<Vector2d> Path { get; }

    /// <summary>The diagonal entry stubs ripping members off the bus.</summary>
    public IReadOnlyList<SchematicBusEntry> Entries { get; }

    /// <summary>Where the vector label anchors, sheet mm.</summary>
    public Vector2d LabelPosition { get; }

    /// <summary>Which end of the label the position is.</summary>
    public SheetTextAnchor LabelAnchor { get; }

    /// <summary>The member net names — a vector bus expands <c>NAME</c>+i in the DRAWN direction (the
    /// same expansion the bus import reads), a <see cref="Group"/> bus carries the members it was
    /// declared with; either way, the bus label is never mistaken for a signal net.</summary>
    public IReadOnlyList<string> Members { get; }

    /// <summary>The drawn bundle label text — <c>NAME[first..last]</c> for a vector bus, the declared
    /// label (<c>"{SDA SCL}"</c> / an alias name) for a <see cref="Group"/> bus.</summary>
    public string Label { get; }

    private static IReadOnlyList<string> ExpandMembers(string name, int first, int last)
    {
        var members = new List<string>();
        int step = first <= last ? 1 : -1;
        for (int i = first; ; i += step)
        {
            members.Add(name + i.ToString(CultureInfo.InvariantCulture));
            if (i == last)
                break;
        }
        return members;
    }
}
