using System.Xml.Linq;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Whole Eagle <c>.brd</c> import (<see cref="EagleBoardReader"/>). Like the schematic import, an
/// Eagle board DECLARES its connectivity — every signal lists its contactrefs — so the strong
/// oracle is not the declaration but the CHECK against it: the imported copper (traces, vias)
/// must actually JOIN the declared pads through <see cref="PcbConnectivity"/>, which is what
/// separates a right placement transform from a plausible one. Plus: pad centres exact from the
/// file's own millimetres, the mirrored-rotation bottom side, the auto-restring via, outline
/// chaining from shuffled segments, determinism, and three-way reader signposting.
/// </summary>
public sealed class EagleBrdImportTests
{
    /// <summary>Wraps the .lbr fixture's own &lt;library&gt; (named "test") plus a board
    /// <paramref name="body"/> into a whole <c>.brd</c> document — one library source, no
    /// duplicated fixture (the <c>EagleSchImportTests.Sch</c> pattern).</summary>
    private static string Brd(string body)
    {
        var library = XDocument.Parse(EagleFixtures.Library)
            .Root!.Element("drawing")!.Element("library")!;
        library.SetAttributeValue("name", "test");
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <eagle version="7.7.0">
              <drawing>
                <board>
                  <libraries>
                    {library}
                  </libraries>
            {body}
                </board>
              </drawing>
            </eagle>
            """;
    }

    // A 40×30 board. R1/R2 (0805, top), U2 (DIL08 through-hole), R3 (0805, MIRRORED → bottom,
    // R3's rot "MR90" also carrying an angle). Signals:
    //   SIG    = R1.2–R2.1, routed by one top trace pad-centre to pad-centre;
    //   VIANET = R2.2–U2.1, routed top → via (auto-restring, no diameter) → three bottom wires
    //            skirting the DIL08's other pads;
    //   STUB   = R3.1 alone (a single-terminal signal).
    // The outline wires are deliberately SHUFFLED and one is direction-FLIPPED, so the import
    // must chain them; an airwire (layer 19) and an inner-layer wire (layer 2) ride along to be
    // skipped by name.
    private const string Body = """
          <plain>
            <wire x1="40" y1="0" x2="40" y2="30" width="0.1" layer="20"/>
            <wire x1="0" y1="0" x2="40" y2="0" width="0.1" layer="20"/>
            <wire x1="0" y1="30" x2="0" y2="0" width="0.1" layer="20"/>
            <wire x1="40" y1="30" x2="0" y2="30" width="0.1" layer="20"/>
            <text x="1" y="1" size="1" layer="25">BOARD</text>
          </plain>
          <elements>
            <element name="R1" library="test" package="R0805" value="10k" x="10" y="10"/>
            <element name="R2" library="test" package="R0805" value="4k7" x="30" y="10"/>
            <element name="U2" library="test" package="DIL08" x="20" y="22"/>
            <element name="R3" library="test" package="R0805" x="10" y="25" rot="MR90"/>
          </elements>
          <signals>
            <signal name="SIG">
              <contactref element="R1" pad="2"/>
              <contactref element="R2" pad="1"/>
              <wire x1="10.9125" y1="10" x2="29.0875" y2="10" width="0.3" layer="1"/>
              <wire x1="10.9125" y1="10" x2="29.0875" y2="10" width="0.3" layer="19"/>
            </signal>
            <signal name="VIANET">
              <contactref element="R2" pad="2"/>
              <contactref element="U2" pad="1"/>
              <wire x1="30.9125" y1="10" x2="33" y2="10" width="0.3" layer="1"/>
              <via x="33" y="10" extent="1-16" drill="0.4"/>
              <wire x1="33" y1="10" x2="14" y2="10" width="0.3" layer="16"/>
              <wire x1="14" y1="10" x2="14" y2="25.81" width="0.3" layer="16"/>
              <wire x1="14" y1="25.81" x2="16.19" y2="25.81" width="0.3" layer="16"/>
              <wire x1="20" y1="15" x2="22" y2="15" width="0.3" layer="2"/>
            </signal>
            <signal name="STUB">
              <contactref element="R3" pad="1"/>
            </signal>
          </signals>
        """;

    [Fact]
    public void TheImportedCopper_ActuallyJoinsTheDeclaredPads()
    {
        var board = EagleBoardReader.Read(Brd(Body));
        var layout = board.Layout;

        // The declaration is the file's own intent...
        var sig = layout.Schematic.Nets.Single(n => n.Name == "SIG");
        Assert.Equal(new[] { "R1.2", "R2.1" },
            sig.Pins.Select(p => $"{p.ReferenceDesignator}.{p.Number}").Order());
        var viaNet = layout.Schematic.Nets.Single(n => n.Name == "VIANET");
        Assert.Equal(new[] { "R2.2", "U2.1" },
            viaNet.Pins.Select(p => $"{p.ReferenceDesignator}.{p.Number}").Order());

        // ...and the CHECK against it is the oracle: the routed copper joins those pads. SIG is a
        // single top trace; VIANET can only close through the via (top wire → via barrel → the
        // bottom wires to the through-hole pad), so a wrong via, a wrong side or a wrong pad
        // position all break it.
        var connectivity = layout.Connectivity();
        Assert.True(connectivity.Of("SIG").IsConnected);
        Assert.True(connectivity.Of("VIANET").IsConnected);

        // And the whole imported board is manufacturable as declared (acute-angle floor at 45°,
        // the KiCad-import convention: a thin trace entering a pad makes near-90° junctions).
        var drc = PcbDrc.Check(layout, DrcRuleSet.Default with { MinAcuteAngleDegrees = 45 });
        Assert.True(drc.Ok, string.Join("; ", drc.Violations.Select(v => v.Message)));
    }

    [Fact]
    public void PadCentres_AreExact_AndTheMirroredRotation_LandsOnTheBottomSide()
    {
        var layout = EagleBoardReader.Read(Brd(Body)).Layout;

        // The file's coordinates are already millimetres, so a pad centre is EXACT: R1 pad 2 is
        // the placement (10, 10) plus the package's own (0.9125, 0).
        var r1Pad2 = layout.PlacedPads().Single(p => p.Name == "R1.2");
        Assert.Equal(10.9125, r1Pad2.World.X, 12);
        Assert.Equal(10.0, r1Pad2.World.Y, 12);

        // "MR90" = mirrored (bottom side), angle 90 carried as stated.
        var r3 = layout.Placements.Single(p => p.Reference == "R3");
        Assert.Equal(CopperSide.Bottom, r3.Side);
        Assert.Equal(90.0, r3.RotationDegrees, 12);

        // The shuffled outline chained into the 40×30 rectangle.
        var outline = layout.Board.OutlinePoints;
        Assert.Equal(4, outline.Count);
        Assert.Contains(outline, p => p.X == 0 && p.Y == 0);
        Assert.Contains(outline, p => p.X == 40 && p.Y == 30);
    }

    [Fact]
    public void AnAutoRestringVia_TakesTheEagleRule_AndSkippedLayersAreNamed()
    {
        var board = EagleBoardReader.Read(Brd(Body));

        // No diameter stated: pad = drill + 2·max(25% drill, 0.254) = 0.4 + 2·0.254 = 0.908.
        var via = board.Layout.PlacedVias().Single();
        Assert.Equal(0.4, via.DrillDiameter, 12);
        Assert.Equal(0.908, via.PadDiameter, 12);
        Assert.Equal(ViaType.Through, via.Type);

        // The airwire (layer 19) and the inner-layer wire (layer 2) were skipped BY NAME, and the
        // assumed board thickness is a note rather than a silent default.
        Assert.Contains(board.Diagnostics, d => d.Contains("layer 19"));
        Assert.Contains(board.Diagnostics, d => d.Contains("layer 2 "));
        Assert.Contains(board.Diagnostics, d => d.Contains("thickness"));
    }

    [Fact]
    public void ASignalPolygon_BecomesAPour_ThatJoinsThePlaneNet()
    {
        // A GND plane polygon covering R1.1 and U2.4, with every attribute the mapping carries:
        // isolate → clearance, rank 2 → Priority 4 (6 − rank), thermals off → direct connect,
        // orphans on → keep dead copper. GND has NO trace — the plane is its only copper.
        const string Elements = """
              <plain>
                <wire x1="0" y1="0" x2="40" y2="0" width="0.1" layer="20"/>
                <wire x1="40" y1="0" x2="40" y2="30" width="0.1" layer="20"/>
                <wire x1="40" y1="30" x2="0" y2="30" width="0.1" layer="20"/>
                <wire x1="0" y1="30" x2="0" y2="0" width="0.1" layer="20"/>
              </plain>
              <elements>
                <element name="R1" library="test" package="R0805" x="10" y="10"/>
                <element name="U2" library="test" package="DIL08" x="20" y="22"/>
              </elements>
            """;
        var board = EagleBoardReader.Read(Brd(Elements + """
              <signals>
                <signal name="GND">
                  <contactref element="R1" pad="1"/>
                  <contactref element="U2" pad="4"/>
                  <polygon width="0.2" layer="1" isolate="0.3" rank="2" thermals="off" orphans="on">
                    <vertex x="5" y="5"/>
                    <vertex x="20" y="5"/>
                    <vertex x="20" y="20"/>
                    <vertex x="5" y="20"/>
                  </polygon>
                </signal>
                <signal name="SIG">
                  <contactref element="R1" pad="2"/>
                  <contactref element="U2" pad="1"/>
                </signal>
              </signals>
            """));

        var pour = Assert.Single(board.Layout.Pours);
        Assert.Equal("GND", pour.Net);
        Assert.Equal(board.Layout.Board.Stackup.Top.Name, pour.Layer);
        Assert.Equal(0.3, pour.Clearance, 12);
        Assert.Equal(4, pour.Priority);                      // Eagle rank 2 → 6 − 2
        Assert.Equal(0, pour.ResolvedRelief.Spokes);         // thermals="off" → direct connect
        Assert.Equal(DeadCopperPolicy.Keep, pour.DeadCopper);
        Assert.Equal(4, pour.Outline!.Count);

        // The oracle with teeth: the plane is GND's ONLY copper, so the pads are joined by the
        // pour or not at all — and the same board WITHOUT the polygon is the mutation that
        // proves it (GND then reads as an unrouted ratsnest).
        Assert.True(board.Layout.Connectivity().Of("GND").IsConnected);
        var without = EagleBoardReader.Read(Brd(Elements + """
              <signals>
                <signal name="GND">
                  <contactref element="R1" pad="1"/>
                  <contactref element="U2" pad="4"/>
                </signal>
                <signal name="SIG">
                  <contactref element="R1" pad="2"/>
                  <contactref element="U2" pad="1"/>
                </signal>
              </signals>
            """));
        Assert.False(without.Layout.Connectivity().Of("GND").IsConnected);

        // And the poured board is manufacturable — the fill clears other-net copper by construction.
        var drc = PcbDrc.Check(board.Layout, DrcRuleSet.Default with { MinAcuteAngleDegrees = 45 });
        Assert.True(drc.Ok, string.Join("; ", drc.Violations.Select(v => v.Message)));
    }

    [Fact]
    public void TheImport_IsDeterministic_AndDirtIsReportedNotThrown()
    {
        Assert.Equal(EagleBoardReader.Read(Brd(Body)).Layout.Save(),
            EagleBoardReader.Read(Brd(Body)).Layout.Save());

        // An element referencing an absent package, a contactref to an unknown element, and a
        // signal whose copper has no terminal are each REPORTED, never thrown.
        var dirty = EagleBoardReader.Read(Brd("""
              <plain>
                <wire x1="0" y1="0" x2="20" y2="0" width="0.1" layer="20"/>
                <wire x1="20" y1="0" x2="20" y2="20" width="0.1" layer="20"/>
                <wire x1="20" y1="20" x2="0" y2="20" width="0.1" layer="20"/>
                <wire x1="0" y1="20" x2="0" y2="0" width="0.1" layer="20"/>
              </plain>
              <elements>
                <element name="R1" library="test" package="R0805" x="10" y="10"/>
                <element name="R9" library="test" package="NOPE" x="5" y="5"/>
              </elements>
              <signals>
                <signal name="A">
                  <contactref element="R1" pad="1"/>
                  <contactref element="GHOST" pad="1"/>
                  <contactref element="R1" pad="77"/>
                </signal>
                <signal name="ORPHAN">
                  <wire x1="2" y1="2" x2="6" y2="2" width="0.3" layer="1"/>
                </signal>
              </signals>
            """));
        Assert.Contains(dirty.Diagnostics, d => d.Contains("NOPE"));
        Assert.Contains(dirty.Diagnostics, d => d.Contains("GHOST"));
        Assert.Contains(dirty.Diagnostics, d => d.Contains("'77'"));
        Assert.Contains(dirty.Diagnostics, d => d.Contains("no resolvable contactref"));
        Assert.Empty(dirty.Layout.Traces);
        Assert.Single(dirty.Layout.Placements);
        // The one-terminal net A survives as a stub.
        Assert.Equal(NetKind.Stub, dirty.Layout.Schematic.Nets.Single(n => n.Name == "A").Kind);
    }

    [Fact]
    public void ALibraryOrSchematicOrAnOpenOutline_IsRefusedByName()
    {
        // Handed a library: refused, signposting EagleLibraryReader.
        var lbr = Assert.Throws<FormatException>(() => EagleBoardReader.Read(EagleFixtures.Library));
        Assert.Contains("EagleLibraryReader", lbr.Message);

        // Handed a schematic: refused, signposting EagleSchematicReader — and the OTHER readers
        // signpost back here, so a user holding any Eagle file is pointed at the right door.
        var schDoc = """
            <?xml version="1.0"?>
            <eagle version="7.7.0"><drawing><schematic/></drawing></eagle>
            """;
        var sch = Assert.Throws<FormatException>(() => EagleBoardReader.Read(schDoc));
        Assert.Contains("EagleSchematicReader", sch.Message);
        var fromLbr = Assert.Throws<FormatException>(
            () => EagleLibraryReader.Read(EagleFixtures.BoardFile));
        Assert.Contains("EagleBoardReader", fromLbr.Message);
        var fromSch = Assert.Throws<FormatException>(
            () => EagleSchematicReader.Read(EagleFixtures.BoardFile));
        Assert.Contains("EagleBoardReader", fromSch.Message);

        // Malformed XML, a non-eagle root, and an outline that does not close: refused by name.
        Assert.Throws<FormatException>(() => EagleBoardReader.Read("<eagle><drawing"));
        Assert.Contains("root element",
            Assert.Throws<FormatException>(
                () => EagleBoardReader.Read("<pcb><drawing><board/></drawing></pcb>")).Message);
        var open = Assert.Throws<FormatException>(() => EagleBoardReader.Read(Brd("""
              <plain>
                <wire x1="0" y1="0" x2="20" y2="0" width="0.1" layer="20"/>
                <wire x1="20" y1="0" x2="20" y2="20" width="0.1" layer="20"/>
                <wire x1="20" y1="20" x2="0" y2="20" width="0.1" layer="20"/>
              </plain>
            """)));
        Assert.Contains("outline", open.Message);
    }
}
