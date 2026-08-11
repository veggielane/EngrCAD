namespace EngrCAD.Ecad.Tests;

/// <summary>Transcribed (minimal but realistic) KiCad <c>.kicad_pcb</c> board text for the whole-board
/// reader tests — a 2-layer board with three footprints (an SMD resistor, an SMD capacitor and a
/// through-hole header), four routed track segments on three nets, a stitching via, a ground zone that
/// joins the GND pads, and the net table. Coordinates are exact millimetres from the file.</summary>
internal static class KiCadPcbFixtures
{
    // ---- the clean, fully-routed board --------------------------------------
    // 30 x 24 x 1.6 board. R1 (6,6), C1 (24,6), J1 (15,18) laid out so the three nets route without
    // crossing on one layer:
    //   VCC : R1.1 (5.0875,6) -> J1.1 (13.73,18)  by two track segments (up the left, across the top)
    //   SIG : R1.2 (6.9125,6) -> C1.1 (23.0875,6)  by one straight track across the middle
    //   GND : C1.2 (24.9125,6) <-> J1.2 (16.27,18) by a top-side ground zone (the ONLY GND copper)
    // plus a GND stitching via. Tracks are 0.4 mm (the round trace cap reads a fraction of the width,
    // so a KiCad-default 0.25 mm track trips the DRC's conservative trace-width measure at 0.15 mm).

    internal const string Board = """
(kicad_pcb
  (version 20221018)
  (generator pcbnew)
  (general
    (thickness 1.6)
  )
  (paper "A4")
  (title_block
    (title "demo-board")
  )
  (layers
    (0 "F.Cu" signal)
    (31 "B.Cu" signal)
    (32 "B.Adhes" user)
    (33 "F.Adhes" user)
    (36 "B.SilkS" user)
    (37 "F.SilkS" user)
    (44 "Edge.Cuts" user)
  )
  (setup)
  (net 0 "")
  (net 1 "GND")
  (net 2 "VCC")
  (net 3 "SIG")

  (footprint "Resistor_SMD:R_0805_2012Metric" (layer "F.Cu")
    (at 6 6 0)
    (property "Reference" "R1" (at 0 -1.5 0) (layer "F.SilkS"))
    (pad "1" smd roundrect (at -0.9125 0) (size 1.025 1.4)
      (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.243902) (net 2 "VCC"))
    (pad "2" smd roundrect (at 0.9125 0) (size 1.025 1.4)
      (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.243902) (net 3 "SIG"))
  )
  (footprint "Capacitor_SMD:C_0805_2012Metric" (layer "F.Cu")
    (at 24 6 0)
    (property "Reference" "C1" (at 0 -1.5 0) (layer "F.SilkS"))
    (pad "1" smd roundrect (at -0.9125 0) (size 1.025 1.4)
      (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.243902) (net 3 "SIG"))
    (pad "2" smd roundrect (at 0.9125 0) (size 1.025 1.4)
      (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.243902) (net 1 "GND"))
  )
  (footprint "Connector_PinHeader:PinHeader_1x02_P2.54mm_Vertical" (layer "F.Cu")
    (at 15 18 0)
    (property "Reference" "J1" (at 0 -2 0) (layer "F.SilkS"))
    (pad "1" thru_hole rect (at -1.27 0) (size 1.7 1.7) (drill 1.0)
      (layers "*.Cu" "*.Mask") (net 2 "VCC"))
    (pad "2" thru_hole circle (at 1.27 0) (size 1.7 1.7) (drill 1.0)
      (layers "*.Cu" "*.Mask") (net 1 "GND"))
  )

  (gr_line (start 0 0) (end 30 0) (stroke (width 0.1) (type solid)) (layer "Edge.Cuts"))
  (gr_line (start 30 0) (end 30 24) (stroke (width 0.1) (type solid)) (layer "Edge.Cuts"))
  (gr_line (start 30 24) (end 0 24) (stroke (width 0.1) (type solid)) (layer "Edge.Cuts"))
  (gr_line (start 0 24) (end 0 0) (stroke (width 0.1) (type solid)) (layer "Edge.Cuts"))

  (segment (start 5.0875 6) (end 5.0875 18) (width 0.4) (layer "F.Cu") (net 2))
  (segment (start 5.0875 18) (end 13.73 18) (width 0.4) (layer "F.Cu") (net 2))
  (segment (start 6.9125 6) (end 23.0875 6) (width 0.4) (layer "F.Cu") (net 3))

  (via (at 4 20) (size 0.8) (drill 0.4) (layers "F.Cu" "B.Cu") (net 1))

  (zone (net 1) (net_name "GND") (layer "F.Cu")
    (hatch edge 0.508)
    (connect_pads (clearance 0.2))
    (polygon
      (pts (xy 0 0) (xy 30 0) (xy 30 24) (xy 0 24))
    )
  )
)
""";

    // ---- the same board WITHOUT the ground zone -----------------------------
    // GND's two pads (C1.2 an SMD land on F.Cu, J1.2 a through-hole) are then joined by NOTHING, so GND
    // reads as an unrouted ratsnest — the mutation that proves the zone is what connects them.

    internal const string BoardNoZone = """
(kicad_pcb
  (version 20221018)
  (generator pcbnew)
  (general (thickness 1.6))
  (layers
    (0 "F.Cu" signal)
    (31 "B.Cu" signal)
    (44 "Edge.Cuts" user)
  )
  (net 0 "")
  (net 1 "GND")
  (net 2 "VCC")
  (net 3 "SIG")

  (footprint "Resistor_SMD:R_0805_2012Metric" (layer "F.Cu")
    (at 6 6 0)
    (property "Reference" "R1" (at 0 -1.5 0) (layer "F.SilkS"))
    (pad "1" smd roundrect (at -0.9125 0) (size 1.025 1.4) (layers "F.Cu" "F.Paste" "F.Mask") (net 2 "VCC"))
    (pad "2" smd roundrect (at 0.9125 0) (size 1.025 1.4) (layers "F.Cu" "F.Paste" "F.Mask") (net 3 "SIG"))
  )
  (footprint "Capacitor_SMD:C_0805_2012Metric" (layer "F.Cu")
    (at 24 6 0)
    (property "Reference" "C1" (at 0 -1.5 0) (layer "F.SilkS"))
    (pad "1" smd roundrect (at -0.9125 0) (size 1.025 1.4) (layers "F.Cu" "F.Paste" "F.Mask") (net 3 "SIG"))
    (pad "2" smd roundrect (at 0.9125 0) (size 1.025 1.4) (layers "F.Cu" "F.Paste" "F.Mask") (net 1 "GND"))
  )
  (footprint "Connector_PinHeader:PinHeader_1x02_P2.54mm_Vertical" (layer "F.Cu")
    (at 15 18 0)
    (property "Reference" "J1" (at 0 -2 0) (layer "F.SilkS"))
    (pad "1" thru_hole rect (at -1.27 0) (size 1.7 1.7) (drill 1.0) (layers "*.Cu" "*.Mask") (net 2 "VCC"))
    (pad "2" thru_hole circle (at 1.27 0) (size 1.7 1.7) (drill 1.0) (layers "*.Cu" "*.Mask") (net 1 "GND"))
  )

  (gr_line (start 0 0) (end 30 0) (layer "Edge.Cuts") (stroke (width 0.1) (type solid)))
  (gr_line (start 30 0) (end 30 24) (layer "Edge.Cuts") (stroke (width 0.1) (type solid)))
  (gr_line (start 30 24) (end 0 24) (layer "Edge.Cuts") (stroke (width 0.1) (type solid)))
  (gr_line (start 0 24) (end 0 0) (layer "Edge.Cuts") (stroke (width 0.1) (type solid)))

  (segment (start 5.0875 6) (end 5.0875 18) (width 0.4) (layer "F.Cu") (net 2))
  (segment (start 5.0875 18) (end 13.73 18) (width 0.4) (layer "F.Cu") (net 2))
  (segment (start 6.9125 6) (end 23.0875 6) (width 0.4) (layer "F.Cu") (net 3))
)
""";

    // ---- a board with a deliberate SHORT ------------------------------------
    // Two SMD pads on DIFFERENT nets overlapping (centres 0.6 mm apart, 1.0 mm pads) — a copper short
    // the DRC must FIND. There is no board outline geometry that could hide it.

    internal const string ShortedBoard = """
(kicad_pcb
  (version 20221018)
  (generator pcbnew)
  (general (thickness 1.6))
  (layers
    (0 "F.Cu" signal)
    (31 "B.Cu" signal)
    (44 "Edge.Cuts" user)
  )
  (net 0 "")
  (net 1 "A")
  (net 2 "B")

  (footprint "TP" (layer "F.Cu")
    (at 8 8 0)
    (property "Reference" "TP1" (at 0 -1 0) (layer "F.SilkS"))
    (pad "1" smd rect (at 0 0) (size 1.0 1.0) (layers "F.Cu" "F.Paste" "F.Mask") (net 1 "A"))
  )
  (footprint "TP" (layer "F.Cu")
    (at 8.6 8 0)
    (property "Reference" "TP2" (at 0 -1 0) (layer "F.SilkS"))
    (pad "1" smd rect (at 0 0) (size 1.0 1.0) (layers "F.Cu" "F.Paste" "F.Mask") (net 2 "B"))
  )

  (gr_line (start 0 0) (end 16 0) (layer "Edge.Cuts") (stroke (width 0.1) (type solid)))
  (gr_line (start 16 0) (end 16 16) (layer "Edge.Cuts") (stroke (width 0.1) (type solid)))
  (gr_line (start 16 16) (end 0 16) (layer "Edge.Cuts") (stroke (width 0.1) (type solid)))
  (gr_line (start 0 16) (end 0 0) (layer "Edge.Cuts") (stroke (width 0.1) (type solid)))
)
""";

    // ---- a single 90°-rotated footprint -------------------------------------
    // Exercises rotation exactness: pad "1" local (-1, 0) placed at (10, 10) rotated 90° lands at
    // (10, 9) (rotate (-1,0) by +90° CCW gives (0,-1)).

    internal const string RotatedFootprintBoard = """
(kicad_pcb
  (version 20221018)
  (generator pcbnew)
  (general (thickness 1.6))
  (layers
    (0 "F.Cu" signal)
    (31 "B.Cu" signal)
    (44 "Edge.Cuts" user)
  )
  (net 0 "")
  (net 1 "N")

  (footprint "R" (layer "F.Cu")
    (at 10 10 90)
    (property "Reference" "R1" (at 0 0 0) (layer "F.SilkS"))
    (pad "1" smd rect (at -1 0) (size 1.0 1.0) (layers "F.Cu" "F.Paste" "F.Mask") (net 1 "N"))
    (pad "2" smd rect (at 1 0) (size 1.0 1.0) (layers "F.Cu" "F.Paste" "F.Mask") (net 0 ""))
  )

  (gr_rect (start 0 0) (end 20 20) (layer "Edge.Cuts") (stroke (width 0.1) (type solid)))
)
""";

    // ---- a bottom-side footprint + an arc track -----------------------------
    // A B.Cu footprint (side maps to Bottom) and an arc track (flattened), plus a straight track — for
    // the covered-subset breadth test.

    internal const string ArcAndBottomBoard = """
(kicad_pcb
  (version 20221018)
  (generator pcbnew)
  (general (thickness 1.6))
  (layers
    (0 "F.Cu" signal)
    (31 "B.Cu" signal)
    (44 "Edge.Cuts" user)
  )
  (net 0 "")
  (net 1 "N")

  (footprint "TP" (layer "B.Cu")
    (at 4 4 0)
    (property "Reference" "TP1" (at 0 -1 0) (layer "B.SilkS"))
    (pad "1" smd circle (at 0 0) (size 1.0 1.0) (layers "B.Cu" "B.Paste" "B.Mask") (net 1 "N"))
  )
  (footprint "TP" (layer "B.Cu")
    (at 12 4 0)
    (property "Reference" "TP2" (at 0 -1 0) (layer "B.SilkS"))
    (pad "1" smd circle (at 0 0) (size 1.0 1.0) (layers "B.Cu" "B.Paste" "B.Mask") (net 1 "N"))
  )

  (gr_rect (start 0 0) (end 16 16) (layer "Edge.Cuts") (stroke (width 0.1) (type solid)))

  (arc (start 4 4) (mid 8 6) (end 12 4) (width 0.3) (layer "B.Cu") (net 1))
)
""";
}
