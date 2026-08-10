using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Ecad;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Multilayer stackups, placement at any layer, and embedded (enclosed / open-cavity) components.
/// The bar is higher than usual (ECAD fails plausibly): exact thickness/z/volume/containment
/// oracles, the 2-layer/surface case bit-identical where it is a pure generalization, refusals by
/// name, scale invariance, and every guard shown to fire.
/// </summary>
public class PcbMultilayerTests
{
    // A 4-layer build-up: Top / prepreg / In1 / core / In2 / prepreg / Bottom.
    private const double Cu = 0.035, Prepreg = 0.2, Core = 1.13;
    private static LayerStackup FourLayer() => LayerStackup.FourLayer(Cu, Prepreg, Core);

    // The exact total (the stackup's own bottom-up sum), so comparisons against it are bit-exact.
    private static readonly double Total4 = FourLayer().TotalThickness;   // ≈ 1.67

    private static PcbBoard MultiBoard(LayerStackup? stackup = null) => new(
        [
            new Vector2d(-25, -20), new Vector2d(25, -20),
            new Vector2d(25, 20), new Vector2d(-25, 20),
        ],
        stackup ?? FourLayer());

    // A couple of embeddable SMD resistors (body + footprint), wired.
    private static Schematic EmbedCircuit()
    {
        var sch = new Schematic("embed");
        var u1 = sch.Add("U1", PcbFixtures.SmdResistor());
        var u2 = sch.Add("U2", PcbFixtures.SmdResistor());
        var u3 = sch.Add("U3", PcbFixtures.SmdResistor());
        sch.Connect("VCC", u1.Pin("1"), u2.Pin("1"));
        sch.Connect("GND", u1.Pin("2"), u3.Pin("2"));
        return sch;
    }

    // ---- 1. the LayerStackup: thickness and z oracles ------------------------

    [Fact]
    public void TotalThickness_IsExactlyTheSumOfEveryLayerThickness()
    {
        var s = FourLayer();
        double sum = s.Layers.Sum(l => l.Thickness);
        Assert.Equal(sum, s.TotalThickness);          // exact
        Assert.Equal(Total4, s.TotalThickness);
        Assert.Equal(4, s.Coppers.Count);
    }

    [Fact]
    public void EachCopperZ_IsItsAccumulatedOffset_TopAtSurfaceBottomAtZero()
    {
        var s = FourLayer();

        Assert.Equal(Total4, s.Top.Z);                // top copper at the top surface, exactly
        Assert.Equal("Top", s.Top.Name);
        Assert.Equal(0.0, s.Bottom.Z);                // bottom copper at 0, exactly
        Assert.Equal("Bottom", s.Bottom.Name);

        // Inner coppers at their slab midplanes, strictly between 0 and total, ordered.
        double in1 = s.Copper("In1")!.Value.Z;
        double in2 = s.Copper("In2")!.Value.Z;
        Assert.Equal(Cu + Prepreg + Cu / 2, in2, 12);        // midplane of the In2 slab
        Assert.Equal(Total4 - Cu - Prepreg - Cu / 2, in1, 12);   // midplane of the In1 slab
        Assert.True(0 < in2 && in2 < in1 && in1 < Total4);
    }

    [Fact]
    public void SixLayerStackup_HasSixCoppersOrderedTopToBottom()
    {
        var s = LayerStackup.SixLayer(Cu, Prepreg, Core);
        Assert.Equal(6, s.Coppers.Count);
        Assert.Equal(6 * Cu + 2 * Prepreg + 3 * Core, s.TotalThickness);
        Assert.Equal(s.TotalThickness, s.Top.Z);
        Assert.Equal(0.0, s.Bottom.Z);
        // strictly descending z
        var zs = s.Coppers.Select(c => c.Z).ToList();
        for (int i = 1; i < zs.Count; i++)
            Assert.True(zs[i] < zs[i - 1]);
    }

    // ---- 2. the 2-layer / surface case is bit-identical (a generalization) ---

    [Fact]
    public void DefaultTwoLayerBoard_HasNoLayerStackupAndTheStage2Geometry()
    {
        var board = PcbBoard.Rectangle(50, 40, 1.6);   // the stage-2 default construction
        Assert.Null(board.LayerStackup);               // built the copper-only way
        Assert.Equal(1.6, board.Stackup.Top.Z);
        Assert.Equal(0.0, board.Stackup.Bottom.Z);

        // Bit-identical plate to a stage-2 board built the same way (nothing changed on this path).
        double v = new Part("p", board.Plate()).MassProperties().Volume;
        Assert.Equal(2000.0 * 1.6, v, 2000.0 * 1.6 * 1e-6);
    }

    [Fact]
    public void ATwoLayerLayoutSerializesWithoutLayerStackupOrEmbeddingFields()
    {
        var json = PcbFixtures.Layout().Save();   // plain two-layer, surface placements
        Assert.DoesNotContain("\"layerStackup\"", json);
        Assert.DoesNotContain("\"stackup\"", json);
        Assert.DoesNotContain("\"layer\"", json);
        Assert.DoesNotContain("\"embedding\"", json);
        Assert.DoesNotContain("\"cavityClearance\"", json);
    }

    [Fact]
    public void SurfacePlacement_SeatsAtTheOuterFace_UnchangedBitForBit()
    {
        var board = MultiBoard();
        var sch = new Schematic();
        sch.Add("R1", PcbFixtures.SmdResistor());
        sch.Add("R2", PcbFixtures.SmdResistor());
        var layout = new PcbLayout(sch, board);
        layout.Place("R1", 0, 0, 0, CopperSide.Top);
        layout.Place("R2", 0, 8, 0, CopperSide.Bottom);

        // Top seats at total, Bottom at 0 — exactly, whatever inner layers exist.
        Assert.Equal(Total4, layout.SeatZ(layout.Placements[0]));
        Assert.Equal(0.0, layout.SeatZ(layout.Placements[1]));

        // A surface part with no embedded cavities leaves the plate unchanged bit-for-bit.
        Assert.Equal(board.ExpectedPlateVolume(), layout.ExpectedPlateVolume());
    }

    // ---- 3. placement at ANY layer seats at that layer's z -------------------

    [Fact]
    public void EmbeddedPlacement_SeatsAtItsInnerLayerZ_BitExact()
    {
        var board = MultiBoard();
        var layout = new PcbLayout(EmbedCircuit(), board);
        layout.Embed("U1", "In2", 0, 0);

        double in2 = board.Stackup.Coppers.Single(c => c.Name == "In2").Z;
        Assert.Equal(in2, layout.SeatZ(layout.Placements[0]));   // exact

        // The body's local origin lands on the seat plane (WorldOf maps local z=0 to seat z).
        var p = layout.WorldOf(layout.Placements[0]).TransformPoint(new Vector3d(0, 0, 0));
        Assert.Equal(in2, p.Z, 12);
    }

    // ---- 4. embedded / enclosed cavities: exact volume + containment ---------

    [Fact]
    public void EnclosedCavity_RemovesExactlyItsPocketVolume_AndIsAnInternalVoid()
    {
        var board = MultiBoard();
        var layout = new PcbLayout(EmbedCircuit(), board);
        layout.Embed("U1", "In2", 0, 0, cavityClearance: 0.1, embedding: Embedding.Enclosed);

        var cavity = Assert.Single(layout.Cavities());

        // Closed-form plate volume: outline area × total − the cavity's pocket.
        double expected = 2000.0 * Total4 - cavity.RemovedVolume;
        Assert.Equal(expected, layout.ExpectedPlateVolume(), 9);

        // The exact B-Rep boolean matches it to the mass-properties grade, and the plate is closed
        // (the enclosed cavity is a valid internal void).
        var mesh = layout.Plate().ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(expected, mesh.Volume(), System.Math.Abs(expected) * 1e-4);

        // The plate's outer bounds are the un-cut prism — the void is internal.
        var b = layout.Plate().Bounds();
        Assert.Equal(0.0, b.Min.Z, 6);
        Assert.Equal(Total4, b.Max.Z, 6);
    }

    [Fact]
    public void EnclosedComponentBody_IsStrictlyContainedInTheBoardVolume()
    {
        var board = MultiBoard();
        var layout = new PcbLayout(EmbedCircuit(), board);
        layout.Embed("U1", "In2", 0, 0);

        var bounds = layout.EmbeddedBodyBounds(layout.Placements[0]);
        Assert.True(StrictlyInsideExtruded(board, bounds),
            $"enclosed body {bounds.Min}..{bounds.Max} should be strictly inside [0,{Total4}]");

        // The cavity itself is internal — it reaches neither surface.
        var cavity = Assert.Single(layout.Cavities());
        Assert.True(cavity.ZLow > 0 && cavity.ZHigh < Total4);
    }

    [Fact]
    public void SurfaceComponentBody_IsProud_NotContained()
    {
        var board = MultiBoard();
        var sch = new Schematic();
        sch.Add("R1", PcbFixtures.SmdResistor());
        var layout = new PcbLayout(sch, board);
        layout.Place("R1", 0, 0, 0, CopperSide.Top);

        var body = PcbFixtures.SmdResistor().Body!().Transform(layout.WorldOf(layout.Placements[0])).Bounds();
        Assert.True(body.Max.Z > Total4 + 1e-9);                  // proud of the top surface
        Assert.False(StrictlyInsideExtruded(board, body));
    }

    [Fact]
    public void OpenCavity_BreaksTheSideSurface_AndRemovesTheWellVolume()
    {
        var board = MultiBoard();
        var layout = new PcbLayout(EmbedCircuit(), board);
        layout.Embed("U1", "In2", 0, 0, embedding: Embedding.OpenCavity);   // open to the top face

        var cavity = Assert.Single(layout.Cavities());

        // The well reaches the top surface (breaks it) — NOT an internal void.
        Assert.Equal(Total4, cavity.ZHigh, 9);
        Assert.True(cavity.ZLow > 0);

        // Removed volume is the lateral pocket × depth to the surface (a closed form).
        double in2 = board.Stackup.Coppers.Single(c => c.Name == "In2").Z;
        double lateral = cavity.RemovedVolume / (Total4 - in2);
        double expected = 2000.0 * Total4 - lateral * (Total4 - in2);
        Assert.Equal(expected, layout.ExpectedPlateVolume(), 9);

        var mesh = layout.Plate().ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(expected, mesh.Volume(), System.Math.Abs(expected) * 1e-4);
    }

    [Fact]
    public void NoEmbeddedComponents_LeavesThePlateVolumeUnchanged()
    {
        var board = MultiBoard();
        var sch = new Schematic();
        sch.Add("R1", PcbFixtures.SmdResistor());
        var layout = new PcbLayout(sch, board);
        layout.Place("R1", 0, 0);   // a surface part — no cavity

        Assert.Empty(layout.Cavities());
        Assert.Equal(board.ExpectedPlateVolume(), layout.ExpectedPlateVolume());
        Assert.Equal(2000.0 * Total4, layout.ExpectedPlateVolume(), 9);
    }

    // ---- 5. the one-declaration identity holds ACROSS layers -----------------

    [Fact]
    public void EmbeddedComponentPads_MapToTheirLayerAndPassTheIdentityCheck()
    {
        var board = MultiBoard();
        var layout = new PcbLayout(EmbedCircuit(), board);
        layout.Embed("U1", "In2", 0, 0);
        layout.Place("U2", 8, 0, 0, CopperSide.Top);       // U2 surface (top)
        layout.Place("U3", -8, 0, 0, CopperSide.Top);

        // The identity check is the geometric lift of the pin count — it passes across layers.
        var check = layout.Check();
        Assert.True(check.Ok, check.ToString());
        Assert.True(check.IdentityHolds);

        // U1's SMD pads land on In2; U2/U3's on Top; none on the other inner layer or bottom.
        var layers = layout.CopperLayers();
        var in2 = layers.Single(l => l.Name == "In2");
        var top = layers.Single(l => l.Name == "Top");
        Assert.Contains(in2.Pads, p => p.Name == "U1.1");
        Assert.Contains(in2.Pads, p => p.Name == "U1.2");
        Assert.DoesNotContain(top.Pads, p => p.Name == "U1.1");
        Assert.Contains(top.Pads, p => p.Name == "U2.1");

        // The copper on In2 sits at the inner layer's z (the seam DRC/routing consume).
        double in2z = board.Stackup.Coppers.Single(c => c.Name == "In2").Z;
        Assert.Equal(in2z, in2.Z);
    }

    [Fact]
    public void PadsOfNet_ResolvesAnEmbeddedPadOnItsInnerLayer()
    {
        var board = MultiBoard();
        var layout = new PcbLayout(EmbedCircuit(), board);
        layout.Embed("U1", "In2", 0, 0);
        layout.Place("U2", 8, 0);   // surface top

        var vcc = layout.Schematic.Nets.Single(n => n.Name == "VCC");   // U1.1 and U2.1
        var pads = layout.PadsOfNet(vcc);
        Assert.Equal(2, pads.Count);
        Assert.Contains(pads, p => p.Name == "U1.1");
        Assert.Contains(pads, p => p.Name == "U2.1");

        // The embedded pad's layer copper is the inner one (v1: the identity is per the pad's OWN
        // layer — cross-layer via/microvia stitching is a later stage).
        var in2 = layout.CopperLayers().Single(l => l.Name == "In2");
        Assert.Contains(in2.Pads, p => p.Name == "U1.1");
    }

    // ---- 6. the copper DRC is N-layer aware ----------------------------------

    private static CopperFeature F(string? net, string source, CurvedRegion2d region, string layer) =>
        new(layer, net, source, region);

    private static CurvedRegion2d Disc(double cx, double cy, double r) =>
        CurvedRegion2d.Disc(new Vector2d(cx, cy), r);

    [Fact]
    public void InnerLayerClearanceViolation_IsFound()
    {
        // Two Ø1 pads of different nets on the INNER layer In1, gap 0.10 < 0.15.
        var model = new PcbCopperModel(MultiBoard(),
        [
            F("A", "P1", Disc(0, 0, 0.5), "In1"),
            F("B", "P2", Disc(1.1, 0, 0.5), "In1"),
        ]);

        var report = PcbDrc.Check(model, DrcRuleSet.Default with { MinCopperClearance = 0.15 });
        var hit = Assert.Single(report.OfRule(DrcRule.Clearance));
        Assert.Equal("In1", hit.Layer);
        Assert.Contains("'A'", hit.Message);
        Assert.Contains("'B'", hit.Message);
    }

    [Fact]
    public void ShortBetweenTwoInnerLayerNets_IsFoundAndNamesThem()
    {
        var model = new PcbCopperModel(MultiBoard(),
        [
            F("VCC", "U1.1", Disc(0, 0, 0.5), "In2"),
            F("GND", "U2.1", Disc(0.5, 0, 0.5), "In2"),   // overlap
        ]);

        var report = PcbDrc.Check(model);
        var hit = Assert.Single(report.OfRule(DrcRule.Short));
        Assert.Equal("In2", hit.Layer);
        Assert.Contains("'VCC'", hit.Message);
        Assert.Contains("'GND'", hit.Message);
    }

    [Fact]
    public void ACleanMultilayerBoard_ReportsZeroViolations()
    {
        var model = new PcbCopperModel(MultiBoard(),
        [
            F("A", "P1", Disc(-10, 0, 0.5), "Top"),
            F("B", "P2", Disc(10, 0, 0.5), "Bottom"),
            F("C", "P3", Disc(-10, 5, 0.5), "In1"),
            F("D", "P4", Disc(10, 5, 0.5), "In2"),
        ]);
        var report = PcbDrc.Check(model);
        Assert.True(report.Ok, string.Join("; ", report.Messages));
        Assert.Empty(report.Violations);
    }

    [Fact]
    public void InnerLayerNetsDoNotClearAgainstOtherLayers_TheDrcIsPerLayerInPlane()
    {
        // Coincident discs of different nets but on DIFFERENT layers — not a short (in-plane rule).
        var model = new PcbCopperModel(MultiBoard(),
        [
            F("A", "P1", Disc(0, 0, 0.5), "In1"),
            F("B", "P2", Disc(0, 0, 0.5), "In2"),
        ]);
        var report = PcbDrc.Check(model);
        Assert.False(report.Has(DrcRule.Short));
        Assert.False(report.Has(DrcRule.Clearance));
    }

    [Fact]
    public void ThroughHoleCopperReachesInnerLayers_AndTheDrcChecksThemPerLayer()
    {
        // A through-hole pad is copper on EVERY layer, so two headers of different nets placed with
        // overlapping pads short on the inner layers too — proof FromLayout populates inner copper
        // and the DRC checks it per layer. (Two EMBEDDED parts cannot be this close: their cavities
        // would overlap and Embed refuses that, which is the emergent minimum-spacing property.)
        var board = MultiBoard();
        var sch = new Schematic();
        sch.Add("J1", PcbFixtures.ThroughHoleHeader());
        sch.Add("J2", PcbFixtures.ThroughHoleHeader());   // unconnected → distinct nets, must clear
        var layout = new PcbLayout(sch, board);
        layout.Place("J1", 0, 0);
        layout.Place("J2", 1.2, 0);   // J1.2 at (1.27,0) vs J2.1 at (-0.07,0)... pads overlap

        var report = PcbDrc.Check(layout);
        Assert.True(report.Has(DrcRule.Short) || report.Has(DrcRule.Clearance),
            string.Join("; ", report.Messages));
        // The same fault appears on the inner layers, not only on the outer copper.
        Assert.Contains(report.Violations, v => v.Layer == "In1");
        Assert.Contains(report.Violations, v => v.Layer == "In2");
    }

    [Theory]
    [InlineData(2.4, true)]    // pad left edge 1.9, wall at 1.7 → gap 0.2 < 0.25 → violation
    [InlineData(2.5, false)]   // pad left edge 2.0 → gap 0.3 > 0.25 → clean
    public void CopperTooCloseToACavityWall_IsFound(double foreignCx, bool violation)
    {
        var board = MultiBoard();
        var layout = new PcbLayout(EmbedCircuit(), board);
        layout.Embed("U1", "In2", 0, 0, cavityClearance: 0.1);
        var cavity = Assert.Single(layout.Cavities());   // rect centred at 0, right wall near x=1.7

        // A foreign disc (radius 0.5) of another net on the same seat layer, near the wall.
        var model = new PcbCopperModel(board,
            [F("N", "T1", Disc(foreignCx, 0, 0.5), "In2")],
            drills: null, cavities: [cavity]);

        var report = PcbDrc.Check(model, DrcRuleSet.Default with { MinCopperToEdge = 0.25 });
        Assert.Equal(violation, report.Has(DrcRule.CavityClearance));
        if (violation)
        {
            var hit = Assert.Single(report.OfRule(DrcRule.CavityClearance));
            Assert.Contains("U1", hit.Message);
            Assert.Contains("T1", hit.Message);
        }
    }

    [Fact]
    public void AnEmbeddedPartsOwnPadsAreExemptFromItsOwnCavityWall()
    {
        // U1's own pads sit inside U1's cavity by construction — never flagged against its wall.
        var board = MultiBoard();
        var layout = new PcbLayout(EmbedCircuit(), board);
        layout.Embed("U1", "In2", 0, 0);

        var report = PcbDrc.Check(layout);
        Assert.False(report.Has(DrcRule.CavityClearance),
            string.Join("; ", report.Messages));
    }

    // ---- 7. persistence: a byte-identical fixed point + refusals -------------

    private static PcbLayout RichMultilayer()
    {
        var board = new PcbBoard(
            [
                new Vector2d(-25, -20), new Vector2d(25, -20),
                new Vector2d(25, 20), new Vector2d(-25, 20),
            ],
            FourLayer(),
            holes: [new BoardHole(new Vector2d(-22, -17), 3.0, BoardHoleKind.Mounting)]);
        var layout = new PcbLayout(EmbedCircuit(), board);
        layout.Embed("U1", "In2", 3, 2, rotationDegrees: 30, embedding: Embedding.Enclosed,
            cavityClearance: 0.15);
        layout.Embed("U2", "In1", -8, 4, embedding: Embedding.OpenCavity, side: CopperSide.Top);
        layout.Place("U3", 8, -4, 0, CopperSide.Bottom);
        return layout;
    }

    [Fact]
    public void MultilayerSaveLoadSave_IsAByteIdenticalFixedPoint()
    {
        var library = PcbFixtures.Library();
        var s1 = RichMultilayer().Save();
        var s2 = PcbLayout.Load(s1, library).Save();
        Assert.Equal(s1, s2);
    }

    [Fact]
    public void RoundTrip_PreservesTheStackupAndTheEmbeddedPlacements()
    {
        var loaded = PcbLayout.Load(RichMultilayer().Save(), PcbFixtures.Library());

        Assert.NotNull(loaded.Board.LayerStackup);
        Assert.Equal(Total4, loaded.Board.Thickness, 12);
        Assert.Equal(4, loaded.Board.Stackup.Coppers.Count);
        Assert.Equal(7, loaded.Board.LayerStackup!.Layers.Count);

        var u1 = loaded.Placements.Single(p => p.Reference == "U1");
        Assert.Equal("In2", u1.Layer);
        Assert.Equal(Embedding.Enclosed, u1.Embedding);
        Assert.Equal(0.15, u1.CavityClearance);
        Assert.Equal(30, u1.RotationDegrees);

        var u2 = loaded.Placements.Single(p => p.Reference == "U2");
        Assert.Equal("In1", u2.Layer);
        Assert.Equal(Embedding.OpenCavity, u2.Embedding);

        // The embedded geometry survives — the plate still builds with its cavities.
        Assert.Equal(2, loaded.Cavities().Count);
    }

    // ---- refusals, by name, every guard shown to fire ------------------------

    [Fact]
    public void PlacingOnANonexistentLayer_IsRefusedByName()
    {
        var layout = new PcbLayout(EmbedCircuit(), MultiBoard());
        var ex = Assert.Throws<System.ArgumentException>(() => layout.Embed("U1", "InX", 0, 0));
        Assert.Contains("InX", ex.Message);
    }

    [Fact]
    public void AnEnclosedCavityThatWouldBreachTheSurface_IsRefusedByName()
    {
        // A 0.5-tall part enclosed on In1 (z≈1.4175) needs 0.6 above it → 2.02 > 1.67 → breach.
        var layout = new PcbLayout(EmbedCircuit(), MultiBoard());
        var ex = Assert.Throws<System.ArgumentException>(() =>
            layout.Embed("U1", "In1", 0, 0, embedding: Embedding.Enclosed));
        Assert.Contains("breach the board surface", ex.Message);
    }

    [Fact]
    public void AnEmbeddedCavityThatWouldBreachTheOutline_IsRefusedByName()
    {
        // Place the part hard against the board edge so its cavity rectangle runs off the outline.
        var layout = new PcbLayout(EmbedCircuit(), MultiBoard());
        var ex = Assert.Throws<System.ArgumentException>(() =>
            layout.Embed("U1", "In2", 24.8, 0));
        Assert.Contains("breach the board outline", ex.Message);
    }

    [Fact]
    public void TwoOverlappingCavities_AreRefusedByName()
    {
        var layout = new PcbLayout(EmbedCircuit(), MultiBoard());
        layout.Embed("U1", "In2", 0, 0);
        var ex = Assert.Throws<System.ArgumentException>(() =>
            layout.Embed("U2", "In2", 0.5, 0));   // its cavity overlaps U1's
        Assert.Contains("overlaps the cavity for 'U1'", ex.Message);
    }

    [Fact]
    public void CavitiesOnDifferentLayersDoNotOverlap_WhenTheirZRangesAreDisjoint()
    {
        // Two dies stacked at different depths (a 6-layer board): U1 enclosed on the low inner
        // layer In4, U2 enclosed on In3 above it — same xy, so their footprints overlap laterally,
        // but their z-ranges are disjoint, so the overlap check (z AND lateral) allows both.
        var board = MultiBoard(LayerStackup.SixLayer(Cu, Prepreg, Core));
        var layout = new PcbLayout(EmbedCircuit(), board);
        layout.Embed("U1", "In4", 0, 0);   // seat ≈ 0.25, cavity ≈ [0.25, 0.85]
        layout.Embed("U2", "In3", 0, 0);   // seat ≈ 1.42, cavity ≈ [1.42, 2.02] — disjoint in z
        Assert.Equal(2, layout.Cavities().Count);

        // And two enclosed cavities that DO overlap in z (same layer) are still refused.
        var ex = Assert.Throws<System.ArgumentException>(() => layout.Embed("U3", "In4", 0.5, 0));
        Assert.Contains("overlaps", ex.Message);
    }

    [Fact]
    public void ANegativeCavityClearance_IsRefusedByName()
    {
        var layout = new PcbLayout(EmbedCircuit(), MultiBoard());
        var ex = Assert.Throws<System.ArgumentException>(() =>
            layout.Embed("U1", "In2", 0, 0, cavityClearance: -0.1));
        Assert.Contains("non-negative", ex.Message);
    }

    [Fact]
    public void EmbeddingAComponentWithoutABody_IsRefusedByName()
    {
        var sch = new Schematic();
        sch.Add("Q1", new PartDefinition("Q_NB", "Q",
            [new Pin("1"), new Pin("2")],
            new Footprint("F", [Pad.Smd("1", new Vector2d(-1, 0), 1, 1), Pad.Smd("2", new Vector2d(1, 0), 1, 1)])));
        var layout = new PcbLayout(sch, MultiBoard());
        var ex = Assert.Throws<System.ArgumentException>(() => layout.Embed("Q1", "In2", 0, 0));
        Assert.Contains("needs a 3D body", ex.Message);
    }

    [Fact]
    public void AStackupWithANonPositiveLayerThickness_IsRefusedByName()
    {
        var ex = Assert.Throws<System.ArgumentException>(() => new LayerStackup(
        [
            StackLayer.Copper("Top", 0.035),
            StackLayer.Dielectric("Core", 0),      // non-positive
            StackLayer.Copper("Bottom", 0.035),
        ]));
        Assert.Contains("non-positive thickness", ex.Message);
    }

    [Fact]
    public void AStackupWithNoCopper_IsRefusedByName()
    {
        var ex = Assert.Throws<System.ArgumentException>(() => new LayerStackup(
            [StackLayer.Dielectric("Core", 1.6)]));
        Assert.Contains("copper", ex.Message);
    }

    [Fact]
    public void LoadingAPlacementNamingAMissingLayer_IsRefusedByName()
    {
        // Break only the placement's seat-layer reference (not the stackup's layer name).
        var json = RichMultilayer().Save().Replace("\"layer\": \"In2\"", "\"layer\": \"InGONE\"");
        var ex = Assert.Throws<FormatException>(() => PcbLayout.Load(json, PcbFixtures.Library()));
        Assert.Contains("InGONE", ex.Message);
    }

    // ---- 8. scale invariance + determinism -----------------------------------

    [Fact]
    public void TheStackupThickness_AndCopperZ_AreScaleInvariant()
    {
        static PcbBoard Build(double s) => PcbBoard.Rectangle(50 * s, 40 * s,
            LayerStackup.FourLayer(Cu * s, Prepreg * s, Core * s));

        var small = Build(1e-3);
        var unit = Build(1);
        var large = Build(1e3);

        Assert.Equal(Total4 * 1e-3, small.Thickness, Total4 * 1e-3 * 1e-9);
        Assert.Equal(Total4, unit.Thickness, 1e-12);
        Assert.Equal(Total4 * 1e3, large.Thickness, Total4 * 1e3 * 1e-9);
        // Copper z scales with the stackup.
        Assert.Equal(unit.Stackup.Coppers.Single(c => c.Name == "In2").Z * 1e3,
            large.Stackup.Coppers.Single(c => c.Name == "In2").Z, 1e-6);
    }

    [Fact]
    public void TheEmbeddedPlateVolume_IsDeterministic()
    {
        double a = RichMultilayer().ExpectedPlateVolume();
        double b = RichMultilayer().ExpectedPlateVolume();
        Assert.Equal(a, b);   // exact
    }

    // ---- helper --------------------------------------------------------------

    /// <summary>Whether an AABB is strictly inside the board's outer extruded prism (the copper-free
    /// solid the outline is extruded through, before any cavity is milled).</summary>
    private static bool StrictlyInsideExtruded(PcbBoard board, in Aabb bounds)
    {
        if (!(bounds.Min.Z > 1e-9) || !(bounds.Max.Z < board.Thickness - 1e-9))
            return false;
        double minX = board.OutlinePoints.Min(p => p.X), maxX = board.OutlinePoints.Max(p => p.X);
        double minY = board.OutlinePoints.Min(p => p.Y), maxY = board.OutlinePoints.Max(p => p.Y);
        return bounds.Min.X > minX && bounds.Max.X < maxX && bounds.Min.Y > minY && bounds.Max.Y < maxY;
    }
}
