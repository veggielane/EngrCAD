namespace EngrCAD.Ecad.Tests;

/// <summary>Transcribed (minimal but realistic) KiCad interchange text for the reader tests —
/// an 0805 resistor and a generic SOIC-8, plus a through-hole connector, a deliberately short
/// footprint for the mismatch test, and a symbol carrying features the reader must NAME.</summary>
internal static class KiCadFixtures
{
    // ---- 0805 resistor: two passive pins, a body rectangle ------------------

    internal const string ResistorSym = """
(kicad_symbol_lib
  (version 20211014)
  (generator kicad_symbol_editor)
  (symbol "R_0805"
    (property "Reference" "R" (at 2.032 0 90)
      (effects (font (size 1.27 1.27))))
    (property "Value" "R_0805" (at 0 0 90)
      (effects (font (size 1.27 1.27))))
    (property "Footprint" "Resistor_SMD:R_0805_2012Metric" (at -1.778 0 90)
      (effects (font (size 1.27 1.27)) hide))
    (property "Datasheet" "~" (at 0 0 0)
      (effects (font (size 1.27 1.27)) hide))
    (symbol "R_0805_0_1"
      (rectangle (start -1.016 2.54) (end 1.016 -2.54)
        (stroke (width 0.254) (type default))
        (fill (type none))))
    (symbol "R_0805_1_1"
      (pin passive line (at 0 3.81 270) (length 1.27)
        (name "~" (effects (font (size 1.27 1.27))))
        (number "1" (effects (font (size 1.27 1.27)))))
      (pin passive line (at 0 -3.81 90) (length 1.27)
        (name "~" (effects (font (size 1.27 1.27))))
        (number "2" (effects (font (size 1.27 1.27))))))))
""";

    internal const string ResistorMod = """
(footprint "R_0805_2012Metric"
  (version 20211014)
  (generator pcbnew)
  (layer "F.Cu")
  (attr smd)
  (fp_text reference "REF**" (at 0 -1.65) (layer "F.SilkS")
    (effects (font (size 1 1) (thickness 0.15))))
  (pad "1" smd roundrect (at -0.9125 0) (size 1.025 1.4)
    (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.243902))
  (pad "2" smd roundrect (at 0.9125 0) (size 1.025 1.4)
    (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.243902)))
""";

    // ---- SOIC-8: eight pins (left/right), a body rectangle and a text label --

    internal const string Soic8Sym = """
(kicad_symbol_lib
  (version 20211014)
  (generator kicad_symbol_editor)
  (symbol "IC_SOIC8"
    (property "Reference" "U" (at 0 7.62 0)
      (effects (font (size 1.27 1.27))))
    (property "Value" "IC_SOIC8" (at 0 -7.62 0)
      (effects (font (size 1.27 1.27))))
    (property "Footprint" "Package_SO:SOIC-8_3.9x4.9mm_P1.27mm" (at 0 0 0)
      (effects (font (size 1.27 1.27)) hide))
    (symbol "IC_SOIC8_0_1"
      (rectangle (start -5.08 5.08) (end 5.08 -5.08)
        (stroke (width 0.254) (type default))
        (fill (type background)))
      (text "IC" (at 0 0 0) (effects (font (size 1.27 1.27)))))
    (symbol "IC_SOIC8_1_1"
      (pin input line (at -7.62 3.81 0) (length 2.54)
        (name "IN1" (effects (font (size 1.27 1.27))))
        (number "1" (effects (font (size 1.27 1.27)))))
      (pin input line (at -7.62 1.27 0) (length 2.54)
        (name "IN2" (effects (font (size 1.27 1.27))))
        (number "2" (effects (font (size 1.27 1.27)))))
      (pin input line (at -7.62 -1.27 0) (length 2.54)
        (name "IN3" (effects (font (size 1.27 1.27))))
        (number "3" (effects (font (size 1.27 1.27)))))
      (pin power_in line (at -7.62 -3.81 0) (length 2.54)
        (name "GND" (effects (font (size 1.27 1.27))))
        (number "4" (effects (font (size 1.27 1.27)))))
      (pin output line (at 7.62 -3.81 180) (length 2.54)
        (name "OUT4" (effects (font (size 1.27 1.27))))
        (number "5" (effects (font (size 1.27 1.27)))))
      (pin output line (at 7.62 -1.27 180) (length 2.54)
        (name "OUT3" (effects (font (size 1.27 1.27))))
        (number "6" (effects (font (size 1.27 1.27)))))
      (pin output line (at 7.62 1.27 180) (length 2.54)
        (name "OUT2" (effects (font (size 1.27 1.27))))
        (number "7" (effects (font (size 1.27 1.27)))))
      (pin power_in line (at 7.62 3.81 180) (length 2.54)
        (name "VCC" (effects (font (size 1.27 1.27))))
        (number "8" (effects (font (size 1.27 1.27))))))))
""";

    internal const string Soic8Mod = """
(footprint "SOIC-8_3.9x4.9mm_P1.27mm"
  (version 20211014)
  (generator pcbnew)
  (layer "F.Cu")
  (attr smd)
  (pad "1" smd roundrect (at -2.475 -1.905) (size 1.95 0.6)
    (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.25))
  (pad "2" smd roundrect (at -2.475 -0.635) (size 1.95 0.6)
    (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.25))
  (pad "3" smd roundrect (at -2.475 0.635) (size 1.95 0.6)
    (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.25))
  (pad "4" smd roundrect (at -2.475 1.905) (size 1.95 0.6)
    (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.25))
  (pad "5" smd roundrect (at 2.475 1.905) (size 1.95 0.6)
    (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.25))
  (pad "6" smd roundrect (at 2.475 0.635) (size 1.95 0.6)
    (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.25))
  (pad "7" smd roundrect (at 2.475 -0.635) (size 1.95 0.6)
    (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.25))
  (pad "8" smd roundrect (at 2.475 -1.905) (size 1.95 0.6)
    (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.25)))
""";

    /// <summary>The SOIC-8 footprint MISSING pad "8" — for the identity mismatch test.</summary>
    internal const string Soic8ModMissingPad8 = """
(footprint "SOIC-8_3.9x4.9mm_P1.27mm"
  (version 20211014)
  (generator pcbnew)
  (layer "F.Cu")
  (attr smd)
  (pad "1" smd roundrect (at -2.475 -1.905) (size 1.95 0.6)
    (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.25))
  (pad "2" smd roundrect (at -2.475 -0.635) (size 1.95 0.6)
    (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.25))
  (pad "3" smd roundrect (at -2.475 0.635) (size 1.95 0.6)
    (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.25))
  (pad "4" smd roundrect (at -2.475 1.905) (size 1.95 0.6)
    (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.25))
  (pad "5" smd roundrect (at 2.475 1.905) (size 1.95 0.6)
    (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.25))
  (pad "6" smd roundrect (at 2.475 0.635) (size 1.95 0.6)
    (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.25))
  (pad "7" smd roundrect (at 2.475 -0.635) (size 1.95 0.6)
    (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.25))
  (pad "99" smd roundrect (at 4.0 0) (size 1.95 0.6)
    (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.25)))
""";

    // ---- a through-hole 2-pin connector: drill + a rect pin-1 pad ------------

    internal const string ConnectorMod = """
(footprint "Conn_01x02_P2.54mm"
  (version 20211014)
  (generator pcbnew)
  (layer "F.Cu")
  (attr through_hole)
  (pad "1" thru_hole rect (at 0 0) (size 1.7 1.7) (drill 1.0)
    (layers "*.Cu" "*.Mask"))
  (pad "2" thru_hole circle (at 2.54 0) (size 1.7 1.7) (drill 1.0)
    (layers "*.Cu" "*.Mask")))
""";

    // ---- a symbol carrying features the reader must NAME --------------------

    internal const string ExoticSym = """
(kicad_symbol_lib
  (version 20211014)
  (generator kicad_symbol_editor)
  (symbol "EXOTIC"
    (property "Reference" "Q" (at 0 0 0)
      (effects (font (size 1.27 1.27))))
    (symbol "EXOTIC_0_1"
      (bezier (pts (xy 0 0) (xy 1 1) (xy 2 0) (xy 3 1))
        (stroke (width 0.2) (type default)) (fill (type none))))
    (symbol "EXOTIC_1_1"
      (pin no_connect line (at 0 5.08 270) (length 1.27)
        (name "NC" (effects (font (size 1.27 1.27))))
        (number "1" (effects (font (size 1.27 1.27))))
        (alternate "ALT" input line))
      (pin passive line (at 0 -5.08 90) (length 1.27)
        (name "P" (effects (font (size 1.27 1.27))))
        (number "2" (effects (font (size 1.27 1.27))))))))
""";
}
