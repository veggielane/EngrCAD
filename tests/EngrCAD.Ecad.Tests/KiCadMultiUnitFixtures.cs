namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Transcribed (minimal but realistic) KiCad interchange text for the MULTI-UNIT symbol tests — a
/// dual op-amp drawn as THREE unit sub-symbols (amp A, amp B, a power unit) under one package, its
/// matching SOIC-8 footprint, and a <c>.kicad_sch</c> that places units A, B and the power unit as
/// three <c>(symbol …)</c> instances SHARING the reference designator "U1" (each with its own
/// <c>(unit N)</c>). Plus two symbols the reader must NAME: one whose units disagree about a pin, and
/// one carrying a De Morgan alternate body style.
/// </summary>
internal static class KiCadMultiUnitFixtures
{
    // ---- a dual op-amp: unit A (amp 1: pins 1,2,3), unit B (amp 2: pins 5,6,7),
    //      unit C (the power unit: pins 4 V-, 8 V+). Footprint = the SOIC-8 (pads 1..8).
    //      The sub-symbol names encode <name>_<unit>_<style>, so _1_1/_2_1/_3_1 are units 1/2/3.
    internal const string DualOpampSym = """
(kicad_symbol_lib
  (version 20211014)
  (generator kicad_symbol_editor)
  (symbol "Dual_Opamp"
    (property "Reference" "U" (at 0 5.08 0)
      (effects (font (size 1.27 1.27))))
    (property "Value" "Dual_Opamp" (at 0 -5.08 0)
      (effects (font (size 1.27 1.27))))
    (property "Footprint" "Package_SO:SOIC-8_3.9x4.9mm_P1.27mm" (at 0 0 0)
      (effects (font (size 1.27 1.27)) hide))
    (symbol "Dual_Opamp_1_1"
      (polyline (pts (xy -5.08 5.08) (xy -5.08 -5.08) (xy 5.08 0) (xy -5.08 5.08))
        (stroke (width 0.254) (type default)) (fill (type background)))
      (pin output line (at 7.62 0 180) (length 2.54)
        (name "~" (effects (font (size 1.27 1.27))))
        (number "1" (effects (font (size 1.27 1.27)))))
      (pin input line (at -7.62 -2.54 0) (length 2.54)
        (name "-" (effects (font (size 1.27 1.27))))
        (number "2" (effects (font (size 1.27 1.27)))))
      (pin input line (at -7.62 2.54 0) (length 2.54)
        (name "+" (effects (font (size 1.27 1.27))))
        (number "3" (effects (font (size 1.27 1.27))))))
    (symbol "Dual_Opamp_2_1"
      (polyline (pts (xy -5.08 5.08) (xy -5.08 -5.08) (xy 5.08 0) (xy -5.08 5.08))
        (stroke (width 0.254) (type default)) (fill (type background)))
      (pin input line (at -7.62 2.54 0) (length 2.54)
        (name "+" (effects (font (size 1.27 1.27))))
        (number "5" (effects (font (size 1.27 1.27)))))
      (pin input line (at -7.62 -2.54 0) (length 2.54)
        (name "-" (effects (font (size 1.27 1.27))))
        (number "6" (effects (font (size 1.27 1.27)))))
      (pin output line (at 7.62 0 180) (length 2.54)
        (name "~" (effects (font (size 1.27 1.27))))
        (number "7" (effects (font (size 1.27 1.27))))))
    (symbol "Dual_Opamp_3_1"
      (pin power_in line (at 0 -7.62 90) (length 2.54)
        (name "V-" (effects (font (size 1.27 1.27))))
        (number "4" (effects (font (size 1.27 1.27)))))
      (pin power_in line (at 0 7.62 270) (length 2.54)
        (name "V+" (effects (font (size 1.27 1.27))))
        (number "8" (effects (font (size 1.27 1.27))))))))
""";

    /// <summary>The SOIC-8 footprint (pads 1..8), matching the dual op-amp's union of unit pins.</summary>
    internal const string Soic8Mod = KiCadFixtures.Soic8Mod;

    // ---- a schematic placing the dual op-amp's THREE units under one reference designator "U1" --
    // Each Device:R-style placement transform maps a library point (Y-up) to the sheet frame
    // (Y-down); a unit placed at (x, y, 0) puts a pin at library anchor (ax, ay) at (x + ax, y - ay).
    //   U1 unit A at (100,100): pin 1 (7.62,0)->(107.62,100), pin 2 (-7.62,-2.54)->(92.38,102.54),
    //                           pin 3 (-7.62,2.54)->(92.38,97.46).
    //   U1 unit B at (150,100): pin 5 (-7.62,2.54)->(142.38,97.46), pin 6 (-7.62,-2.54)->(142.38,102.54),
    //                           pin 7 (7.62,0)->(157.62,100).
    //   U1 unit C at (100,130): pin 4 (0,-7.62)->(100,137.62), pin 8 (0,7.62)->(100,122.38).
    // Nets: OUTA={U1.1} (label), INB={U1.5} (label), LINK={U1.3,U1.7} (an orthogonal wire spanning
    // the two amp units — the discriminating test), VCC={U1.8} (power), GND={U1.4} (power);
    // pins 2 and 6 are no_connect.
    internal const string MultiUnitSheet = """
(kicad_sch
  (version 20230121)
  (generator eeschema)
  (uuid "00000000-0000-0000-0000-000000000002")
  (paper "A4")
  (title_block (title "dual opamp"))
  (lib_symbols
    (symbol "Amp:Dual_Opamp"
      (property "Reference" "U" (at 0 5.08 0) (effects (font (size 1.27 1.27))))
      (property "Value" "Dual_Opamp" (at 0 -5.08 0) (effects (font (size 1.27 1.27))))
      (property "Footprint" "Package_SO:SOIC-8_3.9x4.9mm_P1.27mm" (at 0 0 0) (effects (font (size 1.27 1.27)) hide))
      (symbol "Dual_Opamp_1_1"
        (polyline (pts (xy -5.08 5.08) (xy -5.08 -5.08) (xy 5.08 0) (xy -5.08 5.08))
          (stroke (width 0.254) (type default)) (fill (type background)))
        (pin output line (at 7.62 0 180) (length 2.54) (name "~") (number "1"))
        (pin input line (at -7.62 -2.54 0) (length 2.54) (name "-") (number "2"))
        (pin input line (at -7.62 2.54 0) (length 2.54) (name "+") (number "3")))
      (symbol "Dual_Opamp_2_1"
        (polyline (pts (xy -5.08 5.08) (xy -5.08 -5.08) (xy 5.08 0) (xy -5.08 5.08))
          (stroke (width 0.254) (type default)) (fill (type background)))
        (pin input line (at -7.62 2.54 0) (length 2.54) (name "+") (number "5"))
        (pin input line (at -7.62 -2.54 0) (length 2.54) (name "-") (number "6"))
        (pin output line (at 7.62 0 180) (length 2.54) (name "~") (number "7")))
      (symbol "Dual_Opamp_3_1"
        (pin power_in line (at 0 -7.62 90) (length 2.54) (name "V-") (number "4"))
        (pin power_in line (at 0 7.62 270) (length 2.54) (name "V+") (number "8"))))
    (symbol "power:VCC" (power) (pin_names (offset 0) hide) (on_board yes)
      (property "Reference" "#PWR" (at 0 -3.81 0) (effects (font (size 1.27 1.27)) hide))
      (property "Value" "VCC" (at 0 3.556 0) (effects (font (size 1.27 1.27))))
      (symbol "VCC_1_1"
        (pin power_in line (at 0 0 90) (length 0) (name "VCC") (number "1"))))
    (symbol "power:GND" (power) (pin_names (offset 0) hide) (on_board yes)
      (property "Reference" "#PWR" (at 0 -6.35 0) (effects (font (size 1.27 1.27)) hide))
      (property "Value" "GND" (at 0 -3.81 0) (effects (font (size 1.27 1.27))))
      (symbol "GND_1_1"
        (pin power_in line (at 0 0 270) (length 0) (name "GND") (number "1")))))

  (symbol (lib_id "Amp:Dual_Opamp") (at 100 100 0) (unit 1)
    (property "Reference" "U1" (at 100 92 0))
    (property "Value" "LM358" (at 100 108 0)))
  (symbol (lib_id "Amp:Dual_Opamp") (at 150 100 0) (unit 2)
    (property "Reference" "U1" (at 150 92 0))
    (property "Value" "LM358" (at 150 108 0)))
  (symbol (lib_id "Amp:Dual_Opamp") (at 100 130 0) (unit 3)
    (property "Reference" "U1" (at 100 138 0))
    (property "Value" "LM358" (at 100 145 0)))

  (symbol (lib_id "power:VCC") (at 100 122.38 0)
    (property "Reference" "#PWR01" (at 100 119 0))
    (property "Value" "VCC" (at 100 118 0)))
  (symbol (lib_id "power:GND") (at 100 137.62 0)
    (property "Reference" "#PWR02" (at 100 141 0))
    (property "Value" "GND" (at 100 142 0)))

  (wire (pts (xy 107.62 100) (xy 112 100)))
  (label "OUTA" (at 112 100 0) (effects (font (size 1.27 1.27)) (justify left bottom)))
  (wire (pts (xy 142.38 97.46) (xy 137 97.46)))
  (label "INB" (at 137 97.46 0) (effects (font (size 1.27 1.27)) (justify right bottom)))

  (wire (pts (xy 92.38 97.46) (xy 92.38 88)))
  (wire (pts (xy 92.38 88) (xy 157.62 88)))
  (wire (pts (xy 157.62 88) (xy 157.62 100)))

  (no_connect (at 92.38 102.54))
  (no_connect (at 142.38 102.54))
  (sheet_instances (path "/" (page "1")))
)
""";

    // ---- inconsistent units: two units both claiming pin "1", with different type AND name -----
    internal const string InconsistentUnitsSym = """
(kicad_symbol_lib
  (version 20211014)
  (generator kicad_symbol_editor)
  (symbol "BadPart"
    (property "Reference" "U" (at 0 0 0) (effects (font (size 1.27 1.27))))
    (symbol "BadPart_1_1"
      (pin input line (at -5 0 0) (length 2) (name "A") (number "1")))
    (symbol "BadPart_2_1"
      (pin output line (at 5 0 180) (length 2) (name "B") (number "1")))))
""";

    // ---- a symbol with a De Morgan alternate body style (unit 1, style 2) — ignored by name ----
    internal const string DeMorganSym = """
(kicad_symbol_lib
  (version 20211014)
  (generator kicad_symbol_editor)
  (symbol "Gate"
    (property "Reference" "U" (at 0 0 0) (effects (font (size 1.27 1.27))))
    (symbol "Gate_1_1"
      (pin input line (at -5 2.54 0) (length 2.54) (name "A") (number "1"))
      (pin input line (at -5 -2.54 0) (length 2.54) (name "B") (number "2"))
      (pin output line (at 5 0 180) (length 2.54) (name "Y") (number "3")))
    (symbol "Gate_1_2"
      (pin input line (at -5 2.54 0) (length 2.54) (name "A") (number "1"))
      (pin input line (at -5 -2.54 0) (length 2.54) (name "B") (number "2"))
      (pin output line (at 5 0 180) (length 2.54) (name "Y") (number "3")))))
""";
}
