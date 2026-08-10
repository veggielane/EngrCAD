---
title: "Loading a component (symbol + footprint)"
---

A `PartDefinition` in EngrCAD carries three views of one part: its **pins** (the netlist
terminals), its **footprint** (the copper pads the board places), and — new here — its 2D
**schematic symbol** (the drawn shape a schematic sheet wires to). Stage 1 could declare pins
and a footprint by hand; a real library is *imported*, so EngrCAD reads a component from the
**KiCad** interchange (`.kicad_sym` + `.kicad_mod`) so it arrives with all three at once.

## The one identity: one part, one set of pin NUMBERS

The three representations are **one identity by pin number**: symbol pin `"1"` == footprint pad
`"1"` == netlist pin `"1"`. That is the whole point of loading symbol and footprint together —
and a `PinIdentity` check verifies it, naming any symbol pin with no pad, pad with no pin, or
pin with neither. It is the one-declaration rule (the schematic's source of truth) extended to
the drawn symbol.

## Loading a resistor

`ComponentLibrary.Read` (text) / `ComponentLibrary.Load` (files) reads the symbol and the
footprint and unifies them:

```csharp run:ecad-library
// A minimal KiCad symbol library (.kicad_sym) — one resistor, two passive pins, a body.
var symText = """
(kicad_symbol_lib (version 20211014) (generator kicad_symbol_editor)
  (symbol "R_0805"
    (property "Reference" "R" (at 2.032 0 90) (effects (font (size 1.27 1.27))))
    (property "Value" "R_0805" (at 0 0 90) (effects (font (size 1.27 1.27))))
    (property "Footprint" "Resistor_SMD:R_0805_2012Metric" (at 0 0 0)
      (effects (font (size 1.27 1.27)) hide))
    (symbol "R_0805_0_1"
      (rectangle (start -1.016 2.54) (end 1.016 -2.54)
        (stroke (width 0.254) (type default)) (fill (type none))))
    (symbol "R_0805_1_1"
      (pin passive line (at 0 3.81 270) (length 1.27)
        (name "~" (effects (font (size 1.27 1.27))))
        (number "1" (effects (font (size 1.27 1.27)))))
      (pin passive line (at 0 -3.81 90) (length 1.27)
        (name "~" (effects (font (size 1.27 1.27))))
        (number "2" (effects (font (size 1.27 1.27))))))))
""";

// The matching KiCad footprint (.kicad_mod) — two SMD pads, in millimetres.
var modText = """
(footprint "R_0805_2012Metric" (version 20211014) (generator pcbnew) (layer "F.Cu") (attr smd)
  (pad "1" smd roundrect (at -0.9125 0) (size 1.025 1.4)
    (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.243902))
  (pad "2" smd roundrect (at 0.9125 0) (size 1.025 1.4)
    (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.243902)))
""";

var part = ComponentLibrary.Read(symText, modText);
var def = part.Definition;

// It arrives with pins, a footprint AND a symbol — one part, one set of pin numbers.
Console.WriteLine($"{def.Name}: {def.Pins.Count} pins, "
    + $"{def.Footprint!.Pads.Count} pads, {def.Symbol!.Pins.Count} symbol pins");

if (def.Pins.Count != 2) throw new Exception("expected two pins");
if (def.ReferencePrefix != "R") throw new Exception("expected reference prefix R");

// The identity holds: symbol pin "1" == pad "1" == netlist pin "1".
if (!part.Identity.Ok) throw new Exception(part.Identity.ToString());

// A symbol pin's ANCHOR is where a wire lands. Pin 1 sits above the body, pointing down.
var pin1 = def.Symbol.PinNumbered("1");
Console.WriteLine($"pin 1 anchor {pin1.Anchor}, points {pin1.Direction}, "
    + $"meets body at {pin1.Inner}");

// The pad coordinates are STATED in the file, so they are carried exactly.
if (def.Footprint.Pads[0].Center.X != -0.9125) throw new Exception("pad geometry drifted");

// A loaded part is data now: it round-trips through the schematic file byte-for-byte.
var sch = new Schematic("one resistor");
sch.Add("R1", def, value: "330");
var json = sch.Save();
if (Schematic.Load(json).Save() != json)
    throw new Exception("the loaded symbol is not a persistence fixed point");
```

The symbol is the representation a drawn schematic **sheet** consumes — each `SymbolPin` carries
the point where a wire lands (`Anchor`), the direction it points into the body, and its length,
so routing a wire to a placed symbol is exact.

## When the symbol and footprint disagree

A `PinIdentity.Check` names every mismatch. If a footprint were missing pad `"8"` and carried a
stray pad `"99"`, the report would say so by number — `pin '8' has no footprint pad`,
`footprint pad '99' is not a pin of the definition` — so a wrong pairing fails loudly rather
than shipping a part whose copper does not match its schematic.

## What the reader covers

The reader maps the **common subset** and NAMES anything else rather than mis-reading it:

- **Symbol** (`.kicad_sym`): the `Reference`/`Footprint` properties, nested unit sub-symbols
  recursed for graphics and pins, graphic `rectangle`/`circle`/`arc`/`polyline`/`text`, and
  `pin`s (electrical type → `PinType`, name, number, position, angle → direction, length). A
  bezier graphic, an alternate pin function, or an electrical type with no exact `PinType` is
  ignored **with a named diagnostic**.
- **Footprint** (`.kicad_mod`): SMD and plated through-hole pads of the standard shapes
  (`circle`/`rect`/`roundrect`/`oval`) with their `at`/`size`/`drill`. A pad rotation (not
  carried by a footprint pad), a `trapezoid`/`custom` shape, or an oval drill is approximated
  **with a note**.

Malformed input — a file that is not a KiCad symbol library or footprint, an unbalanced
parenthesis, an unterminated string — is refused **by name** (the `StepReader`/`IgesReader`
rule). Eagle `.lbr`, IPC-7351 footprint *generation*, and the KiCad 3D model reference
(`.wrl`/`.step`) are later work; the path to a 3D model is noted, not loaded.
