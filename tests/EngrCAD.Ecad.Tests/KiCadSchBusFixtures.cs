using System.Globalization;
using System.Text;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Hand-written KiCad <c>.kicad_sch</c> text for the BUS import tests. A bus is a labelled bundle of
/// signal nets (<c>DATA[0..7]</c> is the eight nets DATA0..DATA7); each member is ripped off the bus
/// by a <c>(bus_entry …)</c> onto a signal wire that carries the member's own local label. So the
/// member nets are reconstructed exactly as any labelled wire is — the fixtures are geometrically
/// faithful (a Device:R at <c>(x, 60, 0)</c> puts pin "1" at <c>(x, 56.19)</c>, and each tap's ripped
/// wire runs from that pin down to a bus entry at <c>(x, 40)</c> on the bus, labelled with its
/// member).
/// </summary>
internal static class KiCadSchBusFixtures
{
    // A minimal embedded library — just Device:R, pins "1"/"2" at (0, ±3.81).
    private const string LibSymbols = """
      (lib_symbols
        (symbol "Device:R" (pin_numbers hide) (pin_names (offset 0)) (in_bom yes) (on_board yes)
          (property "Reference" "R" (at 2.032 0 90) (effects (font (size 1.27 1.27))))
          (property "Value" "R" (at 0 0 90) (effects (font (size 1.27 1.27))))
          (symbol "R_1_1"
            (pin passive line (at 0 3.81 270) (length 1.27) (name "~") (number "1"))
            (pin passive line (at 0 -3.81 90) (length 1.27) (name "~") (number "2")))))
      """;

    internal static string Sheet(string title, string body) =>
        $$"""
        (kicad_sch
          (version 20230121)
          (generator eeschema)
          (uuid "00000000-0000-0000-0000-000000000002")
          (paper "A4")
          (title_block (title "{{title}}"))
        {{LibSymbols}}
        {{body}}
          (sheet_instances (path "/" (page "1")))
        )
        """;

    /// <summary>Builds a bus sheet: a horizontal bus wire at y = 40 carrying <paramref name="busLabel"/>,
    /// plus one TAP per entry — a Device:R at <c>(X, 60)</c> whose pin "1" is ripped up to the bus by a
    /// bus entry and labelled with its <c>Member</c>; pin "2" is left no-connect (so Check stays clean).</summary>
    internal static string BusSheet(
        string title, string busLabel, IEnumerable<(string Ref, int X, string Member)> taps,
        string extra = "")
    {
        var list = taps.ToList();
        int left = list.Min(t => t.X) - 6;
        int right = list.Max(t => t.X) + 6;
        var sb = new StringBuilder();
        sb.AppendLine($"  (bus (pts (xy {left} 40) (xy {right} 40)))");
        sb.AppendLine($"  (label \"{busLabel}\" (at {left + 1} 40 0) (effects (font (size 1.27 1.27))))");
        foreach (var (r, x, member) in list)
        {
            sb.AppendLine($"  (symbol (lib_id \"Device:R\") (at {x} 60 0) "
                + $"(property \"Reference\" \"{r}\" (at {x + 3} 60 0)) (property \"Value\" \"R\" (at {x + 3} 62 0)))");
            sb.AppendLine($"  (wire (pts (xy {x} 56.19) (xy {x} 42.54)))");
            sb.AppendLine($"  (bus_entry (at {x} 42.54) (size 0 -2.54))");
            sb.AppendLine($"  (label \"{member}\" (at {x} 49 0) (effects (font (size 1.27 1.27))))");
            sb.AppendLine($"  (no_connect (at {x} 63.81))");
        }
        sb.Append(extra);
        return Sheet(title, sb.ToString());
    }

    // Taps NAME0..NAME7 (or any range) each ripped off a bus, labelled with the member — the taps are
    // independent of the bus label's DIRECTION, which is what the reversed-range test turns on.
    private static IEnumerable<(string Ref, int X, string Member)> VectorTaps(string baseName, int lo, int hi)
    {
        int x = 100;
        for (int i = lo; i <= hi; i++, x += 10)
            yield return ($"R{i.ToString(CultureInfo.InvariantCulture)}", x, baseName + i.ToString(CultureInfo.InvariantCulture));
    }

    // ---- membership expansion (two-sided: each member joins TWO pins) -------
    // DATA[0..3] tapped on both sides — DATA_i = { RA_i.1, RB_i.1 } by member label.
    internal static readonly string DataBusTwoSided = BusSheet("databus", "DATA[0..3]", new[]
    {
        ("RA0", 100, "DATA0"), ("RA1", 110, "DATA1"), ("RA2", 120, "DATA2"), ("RA3", 130, "DATA3"),
        ("RB0", 160, "DATA0"), ("RB1", 170, "DATA1"), ("RB2", 180, "DATA2"), ("RB3", 190, "DATA3"),
    });

    // ---- the mutation that bites: RA2's tap is RELABELLED DATA2 -> DATA5 ----
    // A membership-blind (positional) importer would still put RA2 on DATA2 and pass the first test;
    // reading each tap's OWN label puts RA2 on DATA5 (which is also not a member of DATA[0..3]).
    internal static readonly string DataBusRelabelled = BusSheet("databus", "DATA[0..3]", new[]
    {
        ("RA0", 100, "DATA0"), ("RA1", 110, "DATA1"), ("RA2", 120, "DATA5"), ("RA3", 130, "DATA3"),
        ("RB0", 160, "DATA0"), ("RB1", 170, "DATA1"), ("RB2", 180, "DATA2"), ("RB3", 190, "DATA3"),
    });

    // ---- anonymous bus GROUP {SDA SCL DATA[0..1]} : members SDA, SCL, DATA0, DATA1 (a vector
    //      token expands inside the group), each tapped on both sides.
    internal static readonly string GroupBusTwoSided = BusSheet("groupbus", "{SDA SCL DATA[0..1]}", new[]
    {
        ("RA0", 100, "SDA"), ("RA1", 110, "SCL"), ("RA2", 120, "DATA0"), ("RA3", 130, "DATA1"),
        ("RB0", 160, "SDA"), ("RB1", 170, "SCL"), ("RB2", 180, "DATA0"), ("RB3", 190, "DATA1"),
    });

    // A group {SDA SCL} whose RA1 tap is labelled XYZ — NOT a member of the group.
    internal static readonly string GroupBusNonMember = BusSheet("groupbus", "{SDA SCL}", new[]
    {
        ("RA0", 100, "SDA"), ("RA1", 110, "XYZ"),
        ("RB0", 160, "SDA"), ("RB1", 170, "SCL"),
    });

    /// <summary>A one-tap bus sheet for a given group label — used to drive the nested-group refusal.</summary>
    internal static string GroupSheet(string busLabel) =>
        BusSheet("nested", busLabel, new[] { ("R0", 100, "A") });

    // ---- a NAMED bus ALIAS: (bus_alias "PCI" (members AD0 AD1 DATA[0..1])), the bus wire labelled by
    //      the bare alias name "PCI", tapped on both sides. A member DATA[0..1] expands inside the alias.
    internal static readonly string AliasBusTwoSided = BusSheet("aliasbus", "PCI", new[]
    {
        ("RA0", 100, "AD0"), ("RA1", 110, "AD1"), ("RA2", 120, "DATA0"), ("RA3", 130, "DATA1"),
        ("RB0", 160, "AD0"), ("RB1", 170, "AD1"), ("RB2", 180, "DATA0"), ("RB3", 190, "DATA1"),
    }, extra: "  (bus_alias \"PCI\" (members \"AD0\" \"AD1\" \"DATA[0..1]\"))\n");

    // ---- range parsing -----------------------------------------------------
    internal static readonly string VectorForward = BusSheet("vec", "NAME[0..7]", VectorTaps("NAME", 0, 7));
    internal static readonly string VectorReversed = BusSheet("vec", "NAME[7..0]", VectorTaps("NAME", 0, 7));
    internal static readonly string VectorOutOfRange = BusSheet("vec", "NAME[0..7]",
        VectorTaps("NAME", 0, 7).Append(("R8", 180, "NAME8")));

    // A bus wire + a bus-vector label with a MALFORMED range (refused at Read).
    internal static string BadRangeSheet(string busLabel) => Sheet("badrange",
        $"""
          (bus (pts (xy 90 40) (xy 160 40)))
          (label "{busLabel}" (at 95 40 0) (effects (font (size 1.27 1.27))))
        """);

    // ---- buses must not cross-contaminate a plain signal net ---------------
    // The DATA bus PLUS an unrelated plain net SIG = { RS1.1, RS2.1 } wired directly.
    internal static readonly string BusPlusPlainNet = BusSheet("mixed", "DATA[0..3]", new[]
    {
        ("RA0", 100, "DATA0"), ("RA1", 110, "DATA1"),
        ("RB0", 160, "DATA0"), ("RB1", 170, "DATA1"),
    }, extra: """
      (symbol (lib_id "Device:R") (at 100 120 0) (property "Reference" "RS1" (at 103 120 0)) (property "Value" "R" (at 103 122 0)))
      (symbol (lib_id "Device:R") (at 130 120 0) (property "Reference" "RS2" (at 133 120 0)) (property "Value" "R" (at 133 122 0)))
      (wire (pts (xy 100 116.19) (xy 130 116.19)))
      (label "SIG" (at 115 116.19 0) (effects (font (size 1.27 1.27))))
      (no_connect (at 100 123.81))
      (no_connect (at 130 123.81))
    """);

    // ---- a dangling bus entry (its wire side touches no wire) — REPORTED ---
    internal static readonly string DanglingBusEntry = Sheet("dangling", """
      (bus (pts (xy 90 40) (xy 160 40)))
      (label "DATA[0..3]" (at 95 40 0) (effects (font (size 1.27 1.27))))
      (bus_entry (at 100 40) (size 0 2.54))
    """);
}
