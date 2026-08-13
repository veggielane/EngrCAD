namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Hand-written multi-sheet KiCad <c>.kicad_sch</c> text for the HIERARCHICAL import tests
/// (<see cref="KiCadSchReader.ReadProjectFrom"/>). Every sheet embeds the same <c>lib_symbols</c>
/// (a <c>Device:R</c> plus the power symbols), and, exactly as in the flat fixtures, a
/// <c>Device:R</c> at <c>(x y 0)</c> puts pin "1" at <c>(x, y − 3.81)</c> and pin "2" at
/// <c>(x, y + 3.81)</c> — so a reconstructed net is right ONLY IF the geometry is read right, and
/// the cross-sheet STITCH is right only if a sheet pin is name-matched to the sub-sheet's
/// hierarchical label.
///
/// <para>A project is a <c>sheetfile → text</c> map. Each fixture is a <c>(root, map)</c> pair.</para>
/// </summary>
internal static class KiCadSchHierFixtures
{
    private const string LibSymbols = """
      (lib_symbols
        (symbol "Device:R" (pin_numbers hide) (pin_names (offset 0)) (in_bom yes) (on_board yes)
          (property "Reference" "R" (at 2.032 0 90) (effects (font (size 1.27 1.27))))
          (property "Value" "R" (at 0 0 90) (effects (font (size 1.27 1.27))))
          (symbol "R_0_1"
            (rectangle (start -1.016 -2.54) (end 1.016 2.54)
              (stroke (width 0.254) (type default)) (fill (type none))))
          (symbol "R_1_1"
            (pin passive line (at 0 3.81 270) (length 1.27) (name "~") (number "1"))
            (pin passive line (at 0 -3.81 90) (length 1.27) (name "~") (number "2"))))
        (symbol "power:VCC" (power) (pin_numbers hide) (pin_names (offset 0) hide) (on_board yes)
          (property "Reference" "#PWR" (at 0 -3.81 0) (effects (font (size 1.27 1.27)) hide))
          (property "Value" "VCC" (at 0 3.556 0) (effects (font (size 1.27 1.27))))
          (symbol "VCC_1_1"
            (pin power_in line (at 0 0 90) (length 0) (name "VCC") (number "1"))))
        (symbol "power:GND" (power) (pin_numbers hide) (pin_names (offset 0) hide) (on_board yes)
          (property "Reference" "#PWR" (at 0 -6.35 0) (effects (font (size 1.27 1.27)) hide))
          (property "Value" "GND" (at 0 -3.81 0) (effects (font (size 1.27 1.27))))
          (symbol "GND_1_1"
            (pin power_in line (at 0 0 270) (length 0) (name "GND") (number "1"))))
      )
      """;

    private static string Sheet(string title, string body) =>
        $$"""
        (kicad_sch
          (version 20230121)
          (generator eeschema)
          (uuid "00000000-0000-0000-0000-{{title}}")
          (paper "A4")
          (title_block (title "{{title}}"))
        {{LibSymbols}}
        {{body}}
          (sheet_instances (path "/" (page "1")))
        )
        """;

    // ==== 1. cross-sheet stitch (a root + one sub-sheet, port "VOUT") ==========
    // Root R1: pin2 (100,63.81) wired to sheet-pin "VOUT" at (100,70). pin1 no_connect.
    // Sub  R2: pin1 (100,56.19) wired to hierarchical_label "VOUT" at (100,50). pin2 no_connect.
    // Stitched: VOUT = { R1.2, sub/R2.1 }.
    private const string StitchRootBody = """
      (symbol (lib_id "Device:R") (at 100 60 0)
        (property "Reference" "R1" (at 103 60 0)) (property "Value" "10k" (at 103 62 0)))
      (sheet (at 130 55) (size 30 20)
        (property "Sheetname" "sub" (at 130 54 0))
        (property "Sheetfile" "sub.kicad_sch" (at 130 76 0))
        (pin "VOUT" input (at 100 70 0)))
      (wire (pts (xy 100 63.81) (xy 100 70)))
      (no_connect (at 100 56.19))
      """;

    private static string StitchSubBody(string portName) => $"""
      (symbol (lib_id "Device:R") (at 100 60 0)
        (property "Reference" "R2" (at 103 60 0)) (property "Value" "20k" (at 103 62 0)))
      (wire (pts (xy 100 56.19) (xy 100 50)))
      (hierarchical_label "{portName}" (at 100 50 0) (shape input))
      (no_connect (at 100 63.81))
      """;

    internal static readonly (string Root, IReadOnlyDictionary<string, string> Map) Stitch =
        ("root.kicad_sch", new Dictionary<string, string>
        {
            ["root.kicad_sch"] = Sheet("stitch", StitchRootBody),
            ["sub.kicad_sch"] = Sheet("sub", StitchSubBody("VOUT")),
        });

    // The mutation that bites: the SAME project with the sub's hierarchical label renamed so the
    // parent sheet pin "VOUT" no longer matches it — the cross-sheet net must SPLIT.
    internal static readonly (string Root, IReadOnlyDictionary<string, string> Map) StitchBroken =
        ("root.kicad_sch", new Dictionary<string, string>
        {
            ["root.kicad_sch"] = Sheet("stitch", StitchRootBody),
            ["sub.kicad_sch"] = Sheet("sub", StitchSubBody("VOUT_X")),
        });

    // ==== 2. local-label scoping (two sheets, each a local "CLK", no tie) ======
    private static string ClkSheet(string labelKind) => Sheet("clk", $$"""
      (symbol (lib_id "Device:R") (at 100 60 0)
        (property "Reference" "RC" (at 103 60 0)) (property "Value" "R" (at 103 62 0)))
      (symbol (lib_id "Device:R") (at 120 60 0)
        (property "Reference" "RD" (at 123 60 0)) (property "Value" "R" (at 123 62 0)))
      (wire (pts (xy 100 63.81) (xy 120 63.81)))
      ({{labelKind}} "CLK" (at 110 63.81 0) (effects (font (size 1.27 1.27))))
      (no_connect (at 100 56.19))
      (no_connect (at 120 56.19))
      """);

    private static string ClkRoot(string labelKind, string subFile) => Sheet("clk-root", $$"""
      (symbol (lib_id "Device:R") (at 100 60 0)
        (property "Reference" "RA" (at 103 60 0)) (property "Value" "R" (at 103 62 0)))
      (symbol (lib_id "Device:R") (at 120 60 0)
        (property "Reference" "RB" (at 123 60 0)) (property "Value" "R" (at 123 62 0)))
      (wire (pts (xy 100 63.81) (xy 120 63.81)))
      ({{labelKind}} "CLK" (at 110 63.81 0) (effects (font (size 1.27 1.27))))
      (no_connect (at 100 56.19))
      (no_connect (at 120 56.19))
      (sheet (at 150 55) (size 30 20)
        (property "Sheetname" "SubB" (at 150 54 0))
        (property "Sheetfile" "{{subFile}}" (at 150 76 0)))
      """);

    // Local "CLK" in both root and sub, with NO port tie → TWO nets.
    internal static readonly (string Root, IReadOnlyDictionary<string, string> Map) LocalClk =
        ("root.kicad_sch", new Dictionary<string, string>
        {
            ["root.kicad_sch"] = ClkRoot("label", "subB.kicad_sch"),
            ["subB.kicad_sch"] = ClkSheet("label"),
        });

    // The SAME shape with GLOBAL "CLK" in both → ONE net across the hierarchy.
    internal static readonly (string Root, IReadOnlyDictionary<string, string> Map) GlobalClk =
        ("root.kicad_sch", new Dictionary<string, string>
        {
            ["root.kicad_sch"] = ClkRoot("global_label", "subB.kicad_sch"),
            ["subB.kicad_sch"] = ClkSheet("global_label"),
        });

    // ==== 3. multi-instance (one sub-sheet file placed TWICE) =================
    // amp.kicad_sch: RA.2 (100,63.81) — RB.1 (100,86.19) wired, local label "INT". The other pins
    // no_connect. Placed as "Amp1" and "Amp2" → four distinct components, distinct internal nets.
    private const string AmpBody = """
      (symbol (lib_id "Device:R") (at 100 60 0)
        (property "Reference" "RA" (at 103 60 0)) (property "Value" "R" (at 103 62 0)))
      (symbol (lib_id "Device:R") (at 100 90 0)
        (property "Reference" "RB" (at 103 90 0)) (property "Value" "R" (at 103 92 0)))
      (wire (pts (xy 100 63.81) (xy 100 86.19)))
      (label "INT" (at 100 75 0) (effects (font (size 1.27 1.27))))
      (no_connect (at 100 56.19))
      (no_connect (at 100 93.81))
      """;

    private const string TwiceRootBody = """
      (sheet (at 40 40) (size 30 20)
        (property "Sheetname" "Amp1" (at 40 39 0))
        (property "Sheetfile" "amp.kicad_sch" (at 40 61 0)))
      (sheet (at 90 40) (size 30 20)
        (property "Sheetname" "Amp2" (at 90 39 0))
        (property "Sheetfile" "amp.kicad_sch" (at 90 61 0)))
      """;

    internal static readonly (string Root, IReadOnlyDictionary<string, string> Map) Twice =
        ("root.kicad_sch", new Dictionary<string, string>
        {
            ["root.kicad_sch"] = Sheet("twice", TwiceRootBody),
            ["amp.kicad_sch"] = Sheet("amp", AmpBody),
        });

    // ==== 4. recursion / missing file ========================================
    // A sheet that references itself → refused by name.
    internal static readonly (string Root, IReadOnlyDictionary<string, string> Map) SelfReferential =
        ("self.kicad_sch", new Dictionary<string, string>
        {
            ["self.kicad_sch"] = Sheet("self", """
              (sheet (at 40 40) (size 30 20)
                (property "Sheetname" "again" (at 40 39 0))
                (property "Sheetfile" "self.kicad_sch" (at 40 61 0)))
              """),
        });

    // A root referencing a subsheet file that is NOT in the map → reported, not thrown. R1's two
    // pins are no_connect, so the flattened root is Check-clean.
    internal static readonly (string Root, IReadOnlyDictionary<string, string> Map) MissingSub =
        ("root.kicad_sch", new Dictionary<string, string>
        {
            ["root.kicad_sch"] = Sheet("missing", """
              (symbol (lib_id "Device:R") (at 100 60 0)
                (property "Reference" "R1" (at 103 60 0)) (property "Value" "R" (at 103 62 0)))
              (no_connect (at 100 56.19))
              (no_connect (at 100 63.81))
              (sheet (at 40 40) (size 30 20)
                (property "Sheetname" "gone" (at 40 39 0))
                (property "Sheetfile" "gone.kicad_sch" (at 40 61 0)))
              """),
        });

    // A hierarchical project whose sheet carries a BUS — used to be refused, now imports (per-sheet
    // buses are supported in the hierarchical path too). Just a bus + its label, so it flattens to
    // an empty, clean schematic.
    internal static readonly (string Root, IReadOnlyDictionary<string, string> Map) RootWithBus =
        ("root.kicad_sch", new Dictionary<string, string>
        {
            ["root.kicad_sch"] = Sheet("busroot", """
              (bus (pts (xy 90 40) (xy 160 40)))
              (label "DATA[0..3]" (at 95 40 0) (effects (font (size 1.27 1.27))))
              """),
        });

    // ==== 5. buses ACROSS sheets (a BUS sheet pin carries its members) =========
    // Root: bus DATA[0..1] with taps RA0 → DATA0, RA1 → DATA1, and a BUS sheet pin "DATA[0..1]" on
    // the subsheet node. Sub: hierarchical bus label "DATA[0..1]" on its own bus, taps RB0 → DATA0,
    // RB1 → DATA1. Stitched member-by-member: DATA0 = { RA0.1, bus/RB0.1 }, DATA1 = { RA1.1,
    // bus/RB1.1 } — and the two members stay DISTINCT (the stitch is per member, never a bundle
    // short). Each tap: pin 1 at (x, 56.19) wired to (x, 42.54), a bus entry up to the bus at
    // (x, 40), the member label on the ripped wire at (x, 49); pin 2 no_connect.
    private static string BusTap(string refDes, int x, string member) => $"""
      (symbol (lib_id "Device:R") (at {x} 60 0)
        (property "Reference" "{refDes}" (at {x + 3} 60 0)) (property "Value" "R" (at {x + 3} 62 0)))
      (wire (pts (xy {x} 56.19) (xy {x} 42.54)))
      (bus_entry (at {x} 42.54) (size 0 -2.54))
      (label "{member}" (at {x} 49 0) (effects (font (size 1.27 1.27))))
      (no_connect (at {x} 63.81))
      """;

    private static string BusRootBody(string portName) => $"""
      (bus (pts (xy 90 40) (xy 160 40)))
      (label "DATA[0..1]" (at 95 40 0) (effects (font (size 1.27 1.27))))
      {BusTap("RA0", 100, "DATA0")}
      {BusTap("RA1", 110, "DATA1")}
      (sheet (at 130 55) (size 30 20)
        (property "Sheetname" "bus" (at 130 54 0))
        (property "Sheetfile" "bus.kicad_sch" (at 130 76 0))
        (pin "{portName}" input (at 150 40 0)))
      """;

    private static string BusSubBody(string hierName) => $"""
      (bus (pts (xy 90 40) (xy 130 40)))
      (hierarchical_label "{hierName}" (at 90 40 0) (shape input))
      {BusTap("RB0", 100, "DATA0")}
      {BusTap("RB1", 110, "DATA1")}
      """;

    internal static readonly (string Root, IReadOnlyDictionary<string, string> Map) BusStitch =
        ("root.kicad_sch", new Dictionary<string, string>
        {
            ["root.kicad_sch"] = Sheet("busstitch", BusRootBody("DATA[0..1]")),
            ["bus.kicad_sch"] = Sheet("bus", BusSubBody("DATA[0..1]")),
        });

    // The mutation that bites: the child's hierarchical bus label renamed off the parent's bus sheet
    // pin — the bundle must NOT stitch, and both dangling directions are reported.
    internal static readonly (string Root, IReadOnlyDictionary<string, string> Map) BusStitchBroken =
        ("root.kicad_sch", new Dictionary<string, string>
        {
            ["root.kicad_sch"] = Sheet("busstitch", BusRootBody("DATA[0..1]")),
            ["bus.kicad_sch"] = Sheet("bus", BusSubBody("ADDR[0..1]")),
        });
}
