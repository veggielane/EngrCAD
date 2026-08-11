using System.Linq;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Multi-unit symbols: a dual op-amp is ONE physical package (one footprint, one reference
/// designator) drawn as several schematic symbols — amp A, amp B, a power unit. The bar is higher
/// than usual because a wrong merge SILENTLY mis-wires an IC: the headline is that the three
/// same-refdes instances merge into ONE component whose pins span every unit, and each unit's pins
/// land where THAT unit is placed. The mutation is the old behaviour — two components — which the
/// tests assert against directly (exactly one component, the full pin set).
/// </summary>
public sealed class KiCadMultiUnitTests
{
    // ==== 1. the definition carries the right units (from the .kicad_sym) =====

    [Fact]
    public void DualOpamp_ParsesToThreeUnits_WithTheUnionOfPins()
    {
        var part = ComponentLibrary.Read(KiCadMultiUnitFixtures.DualOpampSym, KiCadMultiUnitFixtures.Soic8Mod);
        var def = part.Definition;

        Assert.True(def.IsMultiUnit);
        Assert.Equal(3, def.Units.Count);

        // Each unit carries only its own pins (amp A: 1,2,3; amp B: 5,6,7; power: 4,8).
        Assert.Equal(new[] { "1", "2", "3" }, def.Units[0].PinNumbers);
        Assert.Equal(new[] { "5", "6", "7" }, def.Units[1].PinNumbers);
        Assert.Equal(new[] { "4", "8" }, def.Units[2].PinNumbers);

        // The definition's Pins are the UNION across units (the netlist terminals of the package).
        Assert.Equal(
            new[] { "1", "2", "3", "5", "6", "7", "4", "8" },
            def.Pins.Select(p => p.Number));

        // Symbol is the FIRST unit (a representative); Units carries all of them.
        Assert.Same(def.Units[0], def.Symbol);

        // The identity spans the units: symbol pin N (across all units) == pad N == netlist pin N.
        Assert.True(part.Identity.Ok, part.Identity.ToString());
    }

    [Fact]
    public void DualOpamp_UnitPinsAnchorAtTheirOwnPositions()
    {
        var part = ComponentLibrary.Read(KiCadMultiUnitFixtures.DualOpampSym, KiCadMultiUnitFixtures.Soic8Mod);
        var def = part.Definition;

        // Amp A's output (pin 1) and amp B's output (pin 7) share the same LOCAL anchor — it is the
        // per-unit PLACEMENT in a schematic that separates them (verified in the merge test below).
        Assert.Equal(new Vector2d(7.62, 0), def.Units[0].PinNumbered("1").Anchor);
        Assert.Equal(new Vector2d(7.62, 0), def.Units[1].PinNumbered("7").Anchor);
        Assert.Equal(PinType.Power, def.Units[2].PinNumbered("8").Type);   // the power unit's pins
    }

    // ==== 2. the PartDefinition units API (direct, no reader) =================

    [Fact]
    public void PartDefinition_WithUnits_DerivesTheUnionAndTheRepresentativeSymbol()
    {
        var unitA = new Symbol("Op",
            [new SymbolPin("1", "", default, SymbolPinDirection.Left, 1, PinType.Output)]);
        var unitB = new Symbol("Op",
            [new SymbolPin("2", "", default, SymbolPinDirection.Right, 1, PinType.Input)]);

        var d = new PartDefinition("Op", "U", [new Pin("1"), new Pin("2")], units: new[] { unitA, unitB });

        Assert.True(d.IsMultiUnit);
        Assert.Equal(2, d.Units.Count);
        Assert.Same(unitA, d.Units[0]);
        Assert.Same(unitB, d.Units[1]);
        Assert.Same(unitA, d.Symbol);   // the first unit is the representative symbol
    }

    [Fact]
    public void PartDefinition_SingleUnit_IsByteIdentical_UnitsDerivedFromTheSymbol()
    {
        var symbol = new Symbol("R",
        [
            new SymbolPin("1", "", new Vector2d(0, 3.81), SymbolPinDirection.Down, 1.27, PinType.Passive),
            new SymbolPin("2", "", new Vector2d(0, -3.81), SymbolPinDirection.Up, 1.27, PinType.Passive),
        ]);
        var d = new PartDefinition("R", "R", [new Pin("1"), new Pin("2")], symbol: symbol);

        // The incumbent single-symbol construction is unchanged: Symbol is the same object, Units is
        // the one-element list [Symbol], and IsMultiUnit is false.
        Assert.Same(symbol, d.Symbol);
        Assert.False(d.IsMultiUnit);
        Assert.Single(d.Units);
        Assert.Same(symbol, d.Units[0]);

        // A symbol-less definition has no units at all — the 3-arg construction is unchanged.
        var bare = new PartDefinition("R", "R", [new Pin("1"), new Pin("2")]);
        Assert.Null(bare.Symbol);
        Assert.Empty(bare.Units);
    }

    [Fact]
    public void PartDefinition_RefusesBothSymbolAndUnits_AndAnEmptyUnitsList()
    {
        var sym = new Symbol("R", [new SymbolPin("1", "", default, SymbolPinDirection.Left, 1, PinType.Passive)]);
        Assert.Throws<ArgumentException>(() =>
            new PartDefinition("R", "R", [new Pin("1")], symbol: sym, units: new[] { sym }));
        Assert.Throws<ArgumentException>(() =>
            new PartDefinition("R", "R", [new Pin("1")], units: []));
    }

    // ==== 3. the instance merge — ONE component, ALL pins ====================

    [Fact]
    public void MultiUnitSheet_MergesThreeInstances_IntoOneComponentWithAllPins()
    {
        var sch = KiCadSchReader.Read(KiCadMultiUnitFixtures.MultiUnitSheet).Schematic;

        // The mutation that bites: the OLD reader made three separate components ("U1", "U1_1",
        // "U1_2"). It is now exactly ONE component "U1" carrying the WHOLE package's pins.
        Assert.Single(sch.Components);
        var u1 = Assert.Single(sch.Components);
        Assert.Equal("U1", u1.ReferenceDesignator);
        Assert.Equal("LM358", u1.Value);
        Assert.True(u1.Definition.IsMultiUnit);
        Assert.Equal(8, u1.AllPins.Count());
        Assert.Equal(
            new[] { "1", "2", "3", "5", "6", "7", "4", "8" },
            u1.Definition.Pins.Select(p => p.Number));
    }

    // ==== 4. nets across units are distinct and land on the right pins =======

    [Fact]
    public void MultiUnitSheet_NetsAcrossUnitsAreDistinct_AndLandOnTheRightPins()
    {
        var sch = KiCadSchReader.Read(KiCadMultiUnitFixtures.MultiUnitSheet).Schematic;

        // A net wired to amp A's OUTPUT (pin 1) and one wired to amp B's INPUT (pin 5) are distinct
        // nets on the SAME component — this is only right if each unit's pins were placed at that
        // unit's own location.
        Assert.Equal("OUTA", NetOf(sch, "U1", "1")!.Name);
        Assert.Equal("INB", NetOf(sch, "U1", "5")!.Name);
        Assert.False(SameNet(sch, ("U1", "1"), ("U1", "5")));

        // The LINK net physically SPANS the two amp units: it joins amp A's pin 3 (placed at
        // (92.38, 97.46)) to amp B's pin 7 (placed at (157.62, 100)). Had the merge placed both units
        // at one location, the orthogonal wire between their true positions could not reach both.
        Assert.True(SameNet(sch, ("U1", "3"), ("U1", "7")));
        var link = NetOf(sch, "U1", "3")!;
        Assert.Equal(NetKind.Signal, link.Kind);
        Assert.Equal(2, link.Pins.Count);

        // The power unit's pins reach the VCC / GND rails.
        Assert.Equal("VCC", NetOf(sch, "U1", "8")!.Name);
        Assert.Equal("GND", NetOf(sch, "U1", "4")!.Name);
    }

    [Fact]
    public void MultiUnitSheet_IsClean_TheCountingIdentityHolds()
    {
        var sch = KiCadSchReader.Read(KiCadMultiUnitFixtures.MultiUnitSheet).Schematic;
        var report = sch.Check();
        Assert.True(report.Ok, report.ToString());
        Assert.Equal(report.TotalPins, report.PinsCoveredOnce);
        Assert.Equal(8, report.TotalPins);

        // Every component's pins still match its lib_symbol by number (identity across units).
        Assert.True(PinIdentity.Check(sch.Components[0].Definition).Ok);
    }

    // ==== 5. persistence — a multi-unit definition round-trips byte-identical =

    [Fact]
    public void MultiUnitDefinition_RoundTripsSaveLoadSave_ByteIdentical()
    {
        var part = ComponentLibrary.Read(KiCadMultiUnitFixtures.DualOpampSym, KiCadMultiUnitFixtures.Soic8Mod);
        var sch = new Schematic("dual");
        sch.Add("U1", part.Definition, "LM358");

        var json = sch.Save();
        Assert.Contains("\"units\"", json);   // the per-unit symbols are written under "units"
        Assert.Equal(json, Schematic.Load(json).Save());

        // The reloaded definition still carries the three units and the union of pins.
        var reloaded = Schematic.Load(json).Components[0].Definition;
        Assert.Equal(3, reloaded.Units.Count);
        Assert.Equal(8, reloaded.Pins.Count);
        Assert.True(reloaded.IsMultiUnit);
    }

    [Fact]
    public void SingleUnitDefinition_StillWritesTheSymbolKey_NotUnits()
    {
        // A single-unit part persists exactly as before — the "symbol" key, never "units".
        var part = ComponentLibrary.Read(KiCadFixtures.ResistorSym, KiCadFixtures.ResistorMod);
        var sch = new Schematic("r");
        sch.Add("R1", part.Definition, "330");
        var json = sch.Save();
        Assert.Contains("\"symbol\"", json);
        Assert.DoesNotContain("\"units\"", json);
        Assert.Equal(json, Schematic.Load(json).Save());
    }

    // ==== 6. the board side is ONE component with all pads ===================

    [Fact]
    public void MultiUnitComponent_OnTheBoard_IsOneComponentWithAllPads()
    {
        var part = ComponentLibrary.Read(KiCadMultiUnitFixtures.DualOpampSym, KiCadMultiUnitFixtures.Soic8Mod);
        var sch = new Schematic("board");
        sch.Add("U1", part.Definition, "LM358");

        var board = new PcbBoard(
            [new Vector2d(-10, -8), new Vector2d(10, -8), new Vector2d(10, 8), new Vector2d(-10, 8)],
            thickness: 1.6);
        var layout = new PcbLayout(sch, board);
        layout.Place("U1", 0, 0, rotationDegrees: 0, side: CopperSide.Top);

        // One package, one footprint, all eight pads — the multi-unit split is a schematic concern
        // the board never sees.
        var report = layout.Check();
        Assert.True(report.Ok, report.ToString());
        Assert.Equal(8, report.PlacedPadCount);
        Assert.Equal(8, report.PlacedPinCount);
    }

    // ==== 7. inconsistent units + De Morgan alternates are reported ==========

    [Fact]
    public void InconsistentUnits_AreReportedByName_AndReconciledToTheFirst()
    {
        // Two units both claim pin "1" — one input "A", the other output "B". The reader keeps the
        // first (never throws) and NAMES the disagreement rather than mis-reading it silently.
        var symbol = KiCadSymbolReader.Read(KiCadMultiUnitFixtures.InconsistentUnitsSym);

        Assert.Contains(symbol.Diagnostics, d => d.Contains("disagree about pin '1'"));
        Assert.Single(symbol.Pins);
        Assert.Equal("1", symbol.Pins[0].Number);
        Assert.Equal(PinType.Input, symbol.Pins[0].Type);   // the first unit won
    }

    [Fact]
    public void DeMorganAlternateBodyStyle_IsIgnoredWithANamedDiagnostic()
    {
        // The Gate has a default body (_1_1) and a De Morgan alternate (_1_2, style 2). The alternate
        // is out of scope — ignored by name — leaving a single-unit part with its three pins.
        var symbol = KiCadSymbolReader.Read(KiCadMultiUnitFixtures.DeMorganSym);

        Assert.Contains(symbol.Diagnostics,
            d => d.Contains("De Morgan") || d.Contains("alternate body style"));
        Assert.Single(symbol.Units);
        Assert.Equal(new[] { "1", "2", "3" }, symbol.Symbol.PinNumbers);
    }

    [Fact]
    public void PinIdentity_SpansUnits_NamingAPinNoUnitDraws()
    {
        // Pins 1,2,3 across two units; but pin 3 is drawn by no unit (units draw 1 and 2 only).
        var unitA = new Symbol("U",
            [new SymbolPin("1", "", default, SymbolPinDirection.Left, 1, PinType.Input)]);
        var unitB = new Symbol("U",
            [new SymbolPin("2", "", default, SymbolPinDirection.Right, 1, PinType.Output)]);
        var d = new PartDefinition("U", "U",
            [new Pin("1"), new Pin("2"), new Pin("3")], units: new[] { unitA, unitB });

        var report = PinIdentity.Check(d);
        Assert.False(report.Ok);
        Assert.Contains("3", report.PinsWithoutSymbolPin);   // no unit draws pin 3

        // But pins 1 and 2 — spread across the two units — are BOTH recognised.
        Assert.DoesNotContain("1", report.PinsWithoutSymbolPin);
        Assert.DoesNotContain("2", report.PinsWithoutSymbolPin);
    }

    // ==== 8. determinism =====================================================

    [Fact]
    public void MultiUnitSheet_ReadIsDeterministic()
    {
        var a = KiCadSchReader.Read(KiCadMultiUnitFixtures.MultiUnitSheet).Schematic.Save();
        var b = KiCadSchReader.Read(KiCadMultiUnitFixtures.MultiUnitSheet).Schematic.Save();
        Assert.Equal(a, b);
    }

    // ---- helpers ------------------------------------------------------------

    private static Net? NetOf(Schematic sch, string refDes, string number) =>
        sch.ToNetlist().NetOf(sch.Find(refDes)!.Pin(number));

    private static bool SameNet(Schematic sch, (string Ref, string Pin) a, (string Ref, string Pin) b)
    {
        var netlist = sch.ToNetlist();
        var na = netlist.NetOf(sch.Find(a.Ref)!.Pin(a.Pin));
        var nb = netlist.NetOf(sch.Find(b.Ref)!.Pin(b.Pin));
        return na is not null && ReferenceEquals(na, nb);
    }
}
