namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Hand-written (minimal but realistic) KiCad <c>.kicad_sch</c> schematic text for the whole-sheet
/// reader tests. Every sheet embeds the same <c>lib_symbols</c> — a <c>Device:R</c>, a
/// <c>Device:C</c> and the <c>power:VCC</c> / <c>power:GND</c> symbols — and the WIRE endpoints are
/// placed at the exact positions the reader computes a placed pin's anchor to be: a
/// <c>Device:R</c> at <c>(x y 0)</c> puts pin "1" at <c>(x, y − 3.81)</c> and pin "2" at
/// <c>(x, y + 3.81)</c> (library Y-up flipped to sheet Y-down). So a net is right ONLY IF the
/// transform is right — the connectivity is the oracle for the geometry.
/// </summary>
internal static class KiCadSchFixtures
{
    // The embedded symbol library — Device:R / Device:C pins at (0, ±3.81), the power symbols'
    // pins at the origin (their connection point).
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
        (symbol "Device:C" (pin_numbers hide) (pin_names (offset 0.254)) (in_bom yes) (on_board yes)
          (property "Reference" "C" (at 0.635 2.54 0) (effects (font (size 1.27 1.27))))
          (property "Value" "C" (at 0.635 -2.54 0) (effects (font (size 1.27 1.27))))
          (symbol "C_0_1"
            (polyline (pts (xy -2.032 -0.762) (xy 2.032 -0.762))
              (stroke (width 0.508) (type default)) (fill (type none)))
            (polyline (pts (xy -2.032 0.762) (xy 2.032 0.762))
              (stroke (width 0.508) (type default)) (fill (type none))))
          (symbol "C_1_1"
            (pin passive line (at 0 3.81 270) (length 2.794) (name "~") (number "1"))
            (pin passive line (at 0 -3.81 90) (length 2.794) (name "~") (number "2"))))
        (symbol "power:VCC" (power) (pin_numbers hide) (pin_names (offset 0) hide) (on_board yes)
          (property "Reference" "#PWR" (at 0 -3.81 0) (effects (font (size 1.27 1.27)) hide))
          (property "Value" "VCC" (at 0 3.556 0) (effects (font (size 1.27 1.27))))
          (symbol "VCC_0_1"
            (polyline (pts (xy 0 0) (xy 0 2.54)) (stroke (width 0) (type default)) (fill (type none))))
          (symbol "VCC_1_1"
            (pin power_in line (at 0 0 90) (length 0) (name "VCC") (number "1"))))
        (symbol "power:GND" (power) (pin_numbers hide) (pin_names (offset 0) hide) (on_board yes)
          (property "Reference" "#PWR" (at 0 -6.35 0) (effects (font (size 1.27 1.27)) hide))
          (property "Value" "GND" (at 0 -3.81 0) (effects (font (size 1.27 1.27))))
          (symbol "GND_0_1"
            (polyline (pts (xy 0 0) (xy 0 -1.27)) (stroke (width 0) (type default)) (fill (type none))))
          (symbol "GND_1_1"
            (pin power_in line (at 0 0 270) (length 0) (name "GND") (number "1"))))
      )
      """;

    private static string Sheet(string title, string body) =>
        $$"""
        (kicad_sch
          (version 20230121)
          (generator eeschema)
          (uuid "00000000-0000-0000-0000-000000000001")
          (paper "A4")
          (title_block (title "{{title}}"))
        {{LibSymbols}}
        {{body}}
          (sheet_instances (path "/" (page "1")))
        )
        """;

    // ---- the divider: three nets, all clean (Check passes) ------------------
    // R1 (100,60): pin1 (100,56.19) VCC, pin2 (100,63.81) MID.
    // R2 (100,90): pin1 (100,86.19) MID, pin2 (100,93.81) GND.
    // C1 (130,75): pin1 (130,71.19) VCC, pin2 (130,78.81) GND (a decoupling cap).
    //   VCC = { R1.1, C1.1 } (a VCC power symbol names it)
    //   MID = { R1.2, R2.1 } (a local label names it)
    //   GND = { R2.2, C1.2 } (a GND power symbol names it)
    private const string DividerBody = """
      (symbol (lib_id "Device:R") (at 100 60 0) (unit 1)
        (property "Reference" "R1" (at 103 60 0))
        (property "Value" "10k" (at 103 62 0)))
      (symbol (lib_id "Device:R") (at 100 90 0) (unit 1)
        (property "Reference" "R2" (at 103 90 0))
        (property "Value" "20k" (at 103 92 0)))
      (symbol (lib_id "Device:C") (at 130 75 0) (unit 1)
        (property "Reference" "C1" (at 133 75 0))
        (property "Value" "100nF" (at 133 77 0)))
      (symbol (lib_id "power:VCC") (at 100 50 0)
        (property "Reference" "#PWR01" (at 100 47 0))
        (property "Value" "VCC" (at 100 46 0)))
      (symbol (lib_id "power:GND") (at 100 100 0)
        (property "Reference" "#PWR02" (at 100 104 0))
        (property "Value" "GND" (at 100 105 0)))

      (wire (pts (xy 100 50) (xy 100 56.19)))
      (wire (pts (xy 100 56.19) (xy 130 56.19)))
      (wire (pts (xy 130 56.19) (xy 130 71.19)))
      (wire (pts (xy 100 63.81) (xy 100 86.19)))
      (wire (pts (xy 100 93.81) (xy 100 100)))
      (wire (pts (xy 130 78.81) (xy 130 100)))
      (wire (pts (xy 130 100) (xy 100 100)))
      (junction (at 100 100) (diameter 0))
      (label "MID" (at 100 75 0) (effects (font (size 1.27 1.27)) (justify left bottom)))
      """;

    internal static readonly string Divider = Sheet("divider", DividerBody);

    // The SAME divider with ONE wire endpoint moved off R2.1: the MID wire now stops at (100,80)
    // instead of reaching R2.1 at (100,86.19), so R2.1 falls off the MID net — the mutation that
    // proves the reader reads GEOMETRY, not a listed netlist.
    internal static readonly string DividerBrokenWire =
        Sheet("divider", DividerBody.Replace("(xy 100 86.19)", "(xy 100 80)"));

    // ---- crossing wires: the junction rule ---------------------------------
    // A horizontal wire H joins RA.1 (90,66.19) and RB.1 (110,66.19).
    // A vertical   wire V joins RC.2 (100,58.81) and RD.1 (100,78.19).
    // They cross at (100,66.19), interior to BOTH. The other pins dangle (recorded).
    private const string CrossingBody = """
      (symbol (lib_id "Device:R") (at 90 70 0)
        (property "Reference" "RA" (at 93 70 0)) (property "Value" "R" (at 93 72 0)))
      (symbol (lib_id "Device:R") (at 110 70 0)
        (property "Reference" "RB" (at 113 70 0)) (property "Value" "R" (at 113 72 0)))
      (symbol (lib_id "Device:R") (at 100 55 0)
        (property "Reference" "RC" (at 103 55 0)) (property "Value" "R" (at 103 57 0)))
      (symbol (lib_id "Device:R") (at 100 82 0)
        (property "Reference" "RD" (at 103 82 0)) (property "Value" "R" (at 103 84 0)))

      (wire (pts (xy 90 66.19) (xy 110 66.19)))
      (wire (pts (xy 100 58.81) (xy 100 78.19)))
      """;

    internal static readonly string Crossing = Sheet("crossing", CrossingBody);

    internal static readonly string CrossingWithJunction =
        Sheet("crossing", CrossingBody + "\n  (junction (at 100 66.19) (diameter 0))");

    // ---- label equivalence -------------------------------------------------
    // Three separate stubs: RA.1 and RB.1 both carry the label "FOO" (one net); RC.1 carries
    // "BAR" (a different net). The bottom pins are marked no_connect (so Check stays clean).
    private const string LabelBody = """
      (symbol (lib_id "Device:R") (at 100 60 0)
        (property "Reference" "RA" (at 103 60 0)) (property "Value" "R" (at 103 62 0)))
      (symbol (lib_id "Device:R") (at 120 60 0)
        (property "Reference" "RB" (at 123 60 0)) (property "Value" "R" (at 123 62 0)))
      (symbol (lib_id "Device:R") (at 140 60 0)
        (property "Reference" "RC" (at 143 60 0)) (property "Value" "R" (at 143 62 0)))

      (wire (pts (xy 100 56.19) (xy 100 50)))
      (wire (pts (xy 120 56.19) (xy 120 50)))
      (wire (pts (xy 140 56.19) (xy 140 50)))
      (label "FOO" (at 100 50 0) (effects (font (size 1.27 1.27)) (justify left bottom)))
      (label "FOO" (at 120 50 0) (effects (font (size 1.27 1.27)) (justify left bottom)))
      (label "BAR" (at 140 50 0) (effects (font (size 1.27 1.27)) (justify left bottom)))
      (no_connect (at 100 63.81))
      (no_connect (at 120 63.81))
      (no_connect (at 140 63.81))
      """;

    internal static readonly string LabelEquivalence = Sheet("labels", LabelBody);

    // ---- no_connect --------------------------------------------------------
    // RA.1 and RB.1 form a signal net "SIG"; both bottom pins are deliberately unconnected.
    private const string NoConnectBody = """
      (symbol (lib_id "Device:R") (at 100 60 0)
        (property "Reference" "RA" (at 103 60 0)) (property "Value" "R" (at 103 62 0)))
      (symbol (lib_id "Device:R") (at 120 60 0)
        (property "Reference" "RB" (at 123 60 0)) (property "Value" "R" (at 123 62 0)))

      (wire (pts (xy 100 56.19) (xy 120 56.19)))
      (label "SIG" (at 110 56.19 0) (effects (font (size 1.27 1.27)) (justify left bottom)))
      (no_connect (at 100 63.81))
      (no_connect (at 120 63.81))
      """;

    internal static readonly string NoConnectSheet = Sheet("noconnect", NoConnectBody);

    // ---- out-of-scope constructs (refused by name) -------------------------
    // A bus GROUP label ({…} named group / alias) is out of scope. (A plain bus wire and bus-vector
    // labels are SUPPORTED — see KiCadSchBusFixtures.)
    internal static readonly string BusGroupSheet = Sheet("busgroup", """
      (bus (pts (xy 10 20) (xy 20 20)))
      (label "{USB DP DM}" (at 12 20 0) (effects (font (size 1.27 1.27))))
      """);

    internal static readonly string HierarchicalSheet = Sheet("hier", """
      (sheet (at 10 10) (size 20 20)
        (property "Sheetname" "sub" (at 10 9 0))
        (property "Sheetfile" "sub.kicad_sch" (at 10 33 0)))
      """);
}
