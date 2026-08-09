using EngrCAD.Core;
using EngrCAD.Ecad;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Ecad.Tests;

public class PcbLayoutTests
{
    // ---- the placement transform is exactly the assembly transform math -----

    [Fact]
    public void WorldOfEachPlacement_IsBitIdenticalToTheFlattenedInstance()
    {
        var layout = PcbFixtures.Layout();
        var instances = layout.ToAssembly().Flatten();

        foreach (var placement in layout.Placements)
        {
            var instance = instances.Single(i => i.Path.EndsWith("/" + placement.Reference));
            // The board frame ∘ P world equals the assembly's own PartInstance.World, bit for bit.
            Assert.True(instance.World.Equals(layout.WorldOf(placement)),
                $"{placement.Reference}: {instance.World} vs {layout.WorldOf(placement)}");
        }
    }

    [Fact]
    public void Pads_AreAtTheirPTransformedCentres()
    {
        var layout = PcbFixtures.Layout();
        var pads = layout.PlacedPads();

        // R1 at (5, 0), no rotation, top: pad "1" local (-1, 0) → world (4, 0) on top copper.
        var r1Pad1 = pads.Single(p => p.Name == "R1.1");
        Assert.Equal(4.0, r1Pad1.World.X, 9);
        Assert.Equal(0.0, r1Pad1.World.Y, 9);

        // J1 at (-8, 4), rotated 90°, top: pad "1" local (-1.27, 0) → world (-8, 2.73).
        var j1Pad1 = pads.Single(p => p.Name == "J1.1");
        Assert.Equal(-8.0, j1Pad1.World.X, 9);
        Assert.Equal(2.73, j1Pad1.World.Y, 9);

        // The full 3D point matches WorldOf applied to the local pad centre.
        var world = layout.WorldOf(layout.Placements[0]);   // R1
        var p = world.TransformPoint(new Vector3d(-1.0, 0, 0));
        Assert.Equal(1.6, p.Z, 9);   // top copper at the thickness
    }

    // ---- through-hole drills the board; SMD drills nothing ------------------

    [Fact]
    public void ThroughHoleComponent_DrillsTheBoardByExactlyItsHoleCylinders()
    {
        var layout = PcbFixtures.Layout();   // J1 is a 2-pad through-hole header (Ø0.9 drills)
        var board = PcbFixtures.Board();

        // Two board mounting holes (Ø3) plus J1's two Ø0.9 through-hole pads.
        double expected =
            board.ExpectedPlateVolume() - 2 * System.Math.PI * 0.45 * 0.45 * 1.6;
        Assert.Equal(expected, layout.ExpectedPlateVolume(), 9);

        double measured = new Part("plate", layout.Plate()).MassProperties().Volume;
        Assert.Equal(expected, measured, expected * 1e-4);
    }

    [Fact]
    public void SmdComponent_DrillsNothing_TheSmdPlateIsIdenticalToTheBareBoard()
    {
        var board = PcbFixtures.Board();
        var sch = new Schematic();
        sch.Add("R1", PcbFixtures.SmdResistor());
        var smdOnly = new PcbLayout(sch, board);
        smdOnly.Place("R1", 0, 0);   // an SMD part

        // The SMD placement adds no drills, so its plate is the bare board's, bit for bit.
        Assert.Equal(board.ExpectedPlateVolume(), smdOnly.ExpectedPlateVolume());
        Assert.Equal(board.Plate().ToMesh().Volume(), smdOnly.Plate().ToMesh().Volume());
    }

    // ---- bottom-side placement is a genuine reflection ----------------------

    [Fact]
    public void BottomPartTransform_IsAReflectionWhoseSquareIsTheIdentity()
    {
        var mirror = PcbLayout.PartTransform(CopperSide.Bottom);
        Assert.True((mirror * mirror).Equals(Matrix4d.Identity));   // Mirror(Mirror(x)) == x
        Assert.Equal(-1.0, mirror.Determinant, 12);                 // a reflection
        Assert.Equal(1.0, PcbLayout.PartTransform(CopperSide.Top).Determinant, 12);
    }

    [Fact]
    public void BottomPlacement_HangsBelowTheBoard_TopSitsAbove()
    {
        var sch = new Schematic();
        sch.Add("R1", PcbFixtures.SmdResistor());
        sch.Add("R2", PcbFixtures.SmdResistor());
        var layout = new PcbLayout(sch, PcbFixtures.Board());
        layout.Place("R1", 0, 0, 0, CopperSide.Top);
        layout.Place("R2", 0, 8, 0, CopperSide.Bottom);

        var body = PcbFixtures.SmdResistor().Body!();   // local z ∈ [0, 0.5]

        var top = body.Transform(layout.WorldOf(layout.Placements[0])).Bounds();
        Assert.True(top.Min.Z >= 1.6 - 1e-9, $"top body min z {top.Min.Z} should sit on the top face");

        var bottom = body.Transform(layout.WorldOf(layout.Placements[1])).Bounds();
        Assert.True(bottom.Max.Z <= 1e-9, $"bottom body max z {bottom.Max.Z} should hang below z=0");

        // The reflection shows up in the sign of the world determinant.
        Assert.True(layout.WorldOf(layout.Placements[1]).Determinant < 0);
        Assert.True(layout.WorldOf(layout.Placements[0]).Determinant > 0);
    }

    [Fact]
    public void BottomThroughHole_LandsAtTheSameWorldXyAsTopWould()
    {
        // A through-hole serves both faces, so a bottom placement's TH pads keep the same x, y.
        var sch = new Schematic();
        sch.Add("J1", PcbFixtures.ThroughHoleHeader());
        var top = new PcbLayout(sch, PcbFixtures.Board());
        top.Place("J1", 3, -2, 45, CopperSide.Top);
        var bottom = new PcbLayout(sch, PcbFixtures.Board());
        bottom.Place("J1", 3, -2, 45, CopperSide.Bottom);

        var topPad = top.PlacedPads().Single(p => p.Name == "J1.1").World;
        var bottomPad = bottom.PlacedPads().Single(p => p.Name == "J1.1").World;
        Assert.Equal(topPad.X, bottomPad.X, 9);
        Assert.Equal(topPad.Y, bottomPad.Y, 9);
    }

    // ---- the one-declaration identity: pin ↔ pad ----------------------------

    [Fact]
    public void CleanLayout_PassesTheIdentityCheck_EveryPinCoveredByOnePad()
    {
        var report = PcbFixtures.Layout().Check();

        Assert.True(report.Ok, report.ToString());
        Assert.True(report.IdentityHolds);
        Assert.Equal(4, report.PlacedPadCount);   // R1 (2) + J1 (2)
        Assert.Equal(4, report.PlacedPinCount);
        Assert.Equal(4, report.PinsCoveredByExactlyOnePad);
        Assert.Empty(report.PadsWithoutPins);
        Assert.Empty(report.PinsWithoutPads);
        Assert.Empty(report.PadsOffBoard);
    }

    [Fact]
    public void PadWithNoPin_IsNamed()
    {
        var def = new PartDefinition("R_BAD", "R",
            [new Pin("1"), new Pin("2")],
            new Footprint("F", [
                Pad.Smd("1", new Vector2d(-1, 0), 1, 1),
                Pad.Smd("2", new Vector2d(1, 0), 1, 1),
                Pad.Smd("3", new Vector2d(0, 2), 1, 1),   // pad 3 has no pin
            ]));
        var sch = new Schematic();
        sch.Add("R1", def);
        var layout = new PcbLayout(sch, PcbFixtures.Board());
        layout.Place("R1", 0, 0);

        var report = layout.Check();
        Assert.False(report.Ok);
        Assert.False(report.IdentityHolds);
        Assert.Contains("R1.3", report.PadsWithoutPins);
    }

    [Fact]
    public void PinWithNoPad_IsNamed()
    {
        var def = new PartDefinition("U_BAD", "U",
            [new Pin("1"), new Pin("2"), new Pin("3")],   // three pins
            new Footprint("F", [
                Pad.Smd("1", new Vector2d(-1, 0), 1, 1),
                Pad.Smd("2", new Vector2d(1, 0), 1, 1),   // only two pads
            ]));
        var sch = new Schematic();
        sch.Add("U1", def);
        var layout = new PcbLayout(sch, PcbFixtures.Board());
        layout.Place("U1", 0, 0);

        var report = layout.Check();
        Assert.False(report.Ok);
        Assert.Contains("U1.3", report.PinsWithoutPads);
    }

    [Fact]
    public void PadOffTheBoardOutline_IsNamed()
    {
        var layout = PcbFixtures.Layout();
        // Re-place R1 far off the 50×40 board.
        var sch = new Schematic();
        sch.Add("R1", PcbFixtures.SmdResistor());
        var offBoard = new PcbLayout(sch, PcbFixtures.Board());
        offBoard.Place("R1", 100, 0);   // way off the plate

        var report = offBoard.Check();
        Assert.False(report.Ok);
        Assert.Contains(report.PadsOffBoard, s => s.StartsWith("R1.1"));
    }

    [Fact]
    public void ComponentWithNoFootprint_IsNamed()
    {
        var sch = new Schematic();
        sch.Add("D1", PcbFixtures.SmdResistor());              // has a footprint
        sch.Add("Q1", new PartDefinition("Q_NOFP", "Q", [new Pin("1")]));  // no footprint
        var layout = new PcbLayout(sch, PcbFixtures.Board());
        layout.Place("D1", 0, 0);
        layout.Place("Q1", 6, 0);

        var report = layout.Check();
        Assert.False(report.Ok);
        Assert.Contains(report.MissingFootprints, s => s.StartsWith("Q1"));
    }

    [Fact]
    public void ViaInAKeepOut_IsNamed()
    {
        // A board with a via keep-out around the origin, and a via drilled inside it.
        var board = new PcbBoard(
            [
                new Vector2d(-25, -20), new Vector2d(25, -20),
                new Vector2d(25, 20), new Vector2d(-25, 20),
            ],
            thickness: 1.6,
            holes: [new BoardHole(new Vector2d(0, 0), 0.4, BoardHoleKind.Via)],
            keepOuts: [new KeepOut("K1", KeepOutKind.Via,
                [new Vector2d(-3, -3), new Vector2d(3, -3), new Vector2d(3, 3), new Vector2d(-3, 3)])]);
        var layout = new PcbLayout(PcbFixtures.Circuit(), board);

        var report = layout.Check();
        Assert.False(report.Ok);
        Assert.Contains(report.HolesInKeepOut, s => s.Contains("K1") && s.Contains("via"));
    }

    [Fact]
    public void UnplacedComponent_IsReported_ButDoesNotFailTheCheck()
    {
        var sch = PcbFixtures.Circuit();   // R1 and J1
        var layout = new PcbLayout(sch, PcbFixtures.Board());
        layout.Place("R1", 5, 0);          // J1 left unplaced

        var report = layout.Check();
        Assert.True(report.Ok);            // a partial layout is legal
        Assert.Contains("J1", report.UnplacedComponents);
    }

    // ---- placement refusals by name -----------------------------------------

    [Fact]
    public void PlacingAnUnknownReference_IsRefusedByName()
    {
        var layout = new PcbLayout(PcbFixtures.Circuit(), PcbFixtures.Board());
        var ex = Assert.Throws<System.ArgumentException>(() => layout.Place("ZZ9", 0, 0));
        Assert.Contains("ZZ9", ex.Message);
        Assert.Contains("not a component", ex.Message);
    }

    [Fact]
    public void PlacingAComponentTwice_IsRefusedByName()
    {
        var layout = new PcbLayout(PcbFixtures.Circuit(), PcbFixtures.Board());
        layout.Place("R1", 0, 0);
        var ex = Assert.Throws<System.ArgumentException>(() => layout.Place("R1", 5, 0));
        Assert.Contains("R1", ex.Message);
        Assert.Contains("already placed", ex.Message);
    }

    // ---- a net resolves to specific copper regions --------------------------

    [Fact]
    public void EachNetResolvesToItsPinsCopper()
    {
        var layout = PcbFixtures.Layout();
        var vcc = layout.Schematic.Nets.Single(n => n.Name == "VCC");   // J1.1 and R1.1

        var pads = layout.PadsOfNet(vcc);
        Assert.Equal(2, pads.Count);
        Assert.Contains(pads, p => p.Name == "J1.1");
        Assert.Contains(pads, p => p.Name == "R1.1");

        // Each of the net's two pins resolves to exactly one copper location.
        Assert.Equal(vcc.Pins.Count, pads.Count);
    }

    // ---- copper layers -------------------------------------------------------

    [Fact]
    public void CopperLayers_PlaceSmdOnItsSideAndThroughHoleOnEveryLayer()
    {
        var layout = PcbFixtures.Layout();   // R1 SMD top, J1 through-hole top
        var layers = layout.CopperLayers();

        var top = layers.Single(l => l.Name == "Top");
        var bottom = layers.Single(l => l.Name == "Bottom");

        // R1's two SMD pads are on top only; J1's two through-hole pads on both layers.
        Assert.Contains(top.Pads, p => p.Name == "R1.1");
        Assert.DoesNotContain(bottom.Pads, p => p.Name == "R1.1");
        Assert.Contains(top.Pads, p => p.Name == "J1.1");
        Assert.Contains(bottom.Pads, p => p.Name == "J1.1");
        Assert.Equal(4, top.Pads.Count);      // 2 SMD + 2 TH
        Assert.Equal(2, bottom.Pads.Count);   // 2 TH
    }

    // ---- the assembly, its flatten, and a BOM over it -----------------------

    [Fact]
    public void ToAssembly_FlattensToTheBoardAndOnePlacedComponentEach()
    {
        var layout = PcbFixtures.Layout();
        var instances = layout.ToAssembly().Flatten();

        Assert.Contains(instances, i => i.Path.EndsWith("/board"));
        Assert.Contains(instances, i => i.Path.EndsWith("/R1"));
        Assert.Contains(instances, i => i.Path.EndsWith("/J1"));
        Assert.Equal(3, instances.Count);
    }

    [Fact]
    public void Bom_CountsTheBoardAndTheComponents()
    {
        var bom = Bom.For(PcbFixtures.Layout().ToAssembly());

        Assert.Contains(bom.Lines, l => l.Item == "board" && l.Quantity == 1);
        Assert.Contains(bom.Lines, l => l.Item == "R_0805" && l.Quantity == 1);
        Assert.Contains(bom.Lines, l => l.Item == "HDR_1x2" && l.Quantity == 1);
    }

    [Fact]
    public void IdenticalComponentsOnOneSide_ShareAPartAndRollUpInTheBom()
    {
        var resistor = PcbFixtures.SmdResistor();   // ONE reusable definition, three placements
        var sch = new Schematic();
        sch.Add("R1", resistor);
        sch.Add("R2", resistor);
        sch.Add("R3", resistor);
        var layout = new PcbLayout(sch, PcbFixtures.Board());
        layout.Place("R1", -6, 0);
        layout.Place("R2", 0, 0);
        layout.Place("R3", 6, 0);

        // Identical components (one definition, one side) share a part — N occurrences.
        var bom = Bom.For(layout.ToAssembly());
        Assert.Contains(bom.Lines, l => l.Item == "R_0805" && l.Quantity == 3);
    }
}
