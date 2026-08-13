using System.Xml.Linq;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Whole Eagle <c>.sch</c> import (<see cref="EagleSchematicReader"/>). The structural finding is
/// that an Eagle schematic DECLARES its connectivity — every net lists its pinrefs — so the import
/// is a RESOLUTION, not a geometric reconstruction; the bar is therefore the resolution being
/// right: a pinref names the SYMBOL PIN, resolved by NAME first (the discriminating case being
/// <c>pin="VCC"</c> landing on pad "8", which a number-blind resolver cannot do), an unloadable
/// part (the multi-gate refusal) reported and skipped without breaking its nets, nets global
/// across sheets, and determinism.
/// </summary>
public sealed class EagleSchImportTests
{
    /// <summary>Wraps the .lbr fixture's own &lt;library&gt; (named "test") plus a schematic
    /// <paramref name="body"/> into a whole <c>.sch</c> document — one library source, no
    /// duplicated fixture.</summary>
    private static string Sch(string body)
    {
        var library = XDocument.Parse(EagleFixtures.Library)
            .Root!.Element("drawing")!.Element("library")!;
        library.SetAttributeValue("name", "test");
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <eagle version="7.7.0">
              <drawing>
                <schematic>
                  <libraries>
                    {library}
                  </libraries>
            {body}
                </schematic>
              </drawing>
            </eagle>
            """;
    }

    // R1/R2 (0805 resistors), U1 (the SOIC8 IC whose symbol pin VCC maps to pad 8), and Q1 (the
    // multi-gate DUAL-X, which cannot load). Nets: SIG = R1.2–R2.1, VCC = R1.1–U1.VCC,
    // GND = U1.GND–R2.2 (plus a pinref to the skipped Q1), IN = a stub on U1.IN1.
    private const string Body = """
          <parts>
            <part name="R1" library="test" deviceset="R-EU_" device="R0805" value="10k"/>
            <part name="R2" library="test" deviceset="R-EU_" device="R0805" value="4k7"/>
            <part name="U1" library="test" deviceset="IC-" device="SOIC8"/>
            <part name="Q1" library="test" deviceset="DUAL-" device="X"/>
          </parts>
          <sheets>
            <sheet>
              <nets>
                <net name="SIG" class="0">
                  <segment>
                    <wire x1="0" y1="0" x2="10" y2="0" width="0.1524" layer="91"/>
                    <pinref part="R1" gate="G$1" pin="2"/>
                    <pinref part="R2" gate="G$1" pin="1"/>
                  </segment>
                </net>
                <net name="VCC" class="0">
                  <segment>
                    <pinref part="R1" gate="G$1" pin="1"/>
                    <pinref part="U1" gate="G$1" pin="VCC"/>
                  </segment>
                </net>
                <net name="GND" class="0">
                  <segment>
                    <pinref part="U1" gate="G$1" pin="GND"/>
                    <pinref part="Q1" gate="G$1" pin="1"/>
                    <pinref part="R2" gate="G$1" pin="2"/>
                  </segment>
                </net>
                <net name="IN" class="0">
                  <segment>
                    <pinref part="U1" gate="G$1" pin="IN1"/>
                  </segment>
                </net>
              </nets>
            </sheet>
          </sheets>
        """;

    private static Net? NetOf(Schematic sch, string refDes, string pinNumber) =>
        sch.Nets.FirstOrDefault(n => n.Pins.Any(
            p => p.ReferenceDesignator == refDes && p.Number == pinNumber));

    private static void AssertNet(Schematic sch, string name, params (string Ref, string Pin)[] pins)
    {
        var net = sch.Nets.FirstOrDefault(n => n.Name == name);
        Assert.NotNull(net);
        Assert.Equal(
            pins.Select(p => $"{p.Ref}.{p.Pin}").OrderBy(s => s, StringComparer.Ordinal),
            net!.Pins.Select(p => $"{p.ReferenceDesignator}.{p.Number}")
                .OrderBy(s => s, StringComparer.Ordinal));
    }

    // ==== 1. the declared netlist resolves, pin-NAME first ====================

    [Fact]
    public void AnEagleSchematic_ResolvesItsDeclaredNets_ByPinName()
    {
        var read = EagleSchematicReader.Read(Sch(Body));
        var sch = read.Schematic;

        // R1, R2, U1 import; Q1 (multi-gate) is reported and skipped.
        Assert.Equal(new[] { "R1", "R2", "U1" }, sch.Components.Select(c => c.ReferenceDesignator));
        Assert.Equal("10k", sch.Find("R1")!.Value);

        // The DISCRIMINATING resolution: pinref pin="VCC" names the SYMBOL PIN, whose pad — hence
        // our pin NUMBER — is "8". A number-blind resolver has no pin "VCC" and would skip it.
        AssertNet(sch, "VCC", ("R1", "1"), ("U1", "8"));
        AssertNet(sch, "SIG", ("R1", "2"), ("R2", "1"));
        // GND still forms from its resolvable pins (Q1's pinref skipped by name).
        AssertNet(sch, "GND", ("U1", "4"), ("R2", "2"));

        // A one-pin net is a stub, on IN1 = pad 1.
        var stub = sch.Nets.First(n => n.Name == "IN");
        Assert.Equal(NetKind.Stub, stub.Kind);
        Assert.Equal("1", stub.Pins.Single().Number);

        Assert.Contains(read.Diagnostics, d => d.Contains("Q1") && d.Contains("2 gates"));
        Assert.Contains(read.Diagnostics, d => d.Contains("Q1") && d.Contains("skipped"));
    }

    // ==== 2. nets are GLOBAL across sheets ====================================

    [Fact]
    public void ANetNamedOnTwoSheets_IsOneNet()
    {
        const string twoSheets = """
              <parts>
                <part name="R1" library="test" deviceset="R-EU_" device="R0805"/>
                <part name="R2" library="test" deviceset="R-EU_" device="R0805"/>
              </parts>
              <sheets>
                <sheet>
                  <nets>
                    <net name="SIG"><segment><pinref part="R1" gate="G$1" pin="2"/></segment></net>
                  </nets>
                </sheet>
                <sheet>
                  <nets>
                    <net name="SIG"><segment><pinref part="R2" gate="G$1" pin="1"/></segment></net>
                  </nets>
                </sheet>
              </sheets>
            """;
        var sch = EagleSchematicReader.Read(Sch(twoSheets)).Schematic;
        // Eagle nets are global to the schematic: SIG on two sheets is ONE two-pin net.
        AssertNet(sch, "SIG", ("R1", "2"), ("R2", "1"));
        Assert.Equal(NetKind.Signal, sch.Nets.Single(n => n.Name == "SIG").Kind);
    }

    // ==== 3. the schematic is well-formed + deterministic =====================

    [Fact]
    public void TheImport_IsDeterministic_AndUnknownPinrefsAreReportedNotThrown()
    {
        var a = EagleSchematicReader.Read(Sch(Body));
        var b = EagleSchematicReader.Read(Sch(Body));
        Assert.Equal(a.Schematic.Save(), b.Schematic.Save());

        // A pinref to an unknown pin is reported by name, never thrown.
        string bad = Body.Replace("pin=\"IN1\"", "pin=\"NOPE\"");
        var read = EagleSchematicReader.Read(Sch(bad));
        Assert.Contains(read.Diagnostics, d => d.Contains("NOPE") && d.Contains("skipped"));
        Assert.Contains(read.Diagnostics, d => d.Contains("'IN'") && d.Contains("no resolvable pins"));
    }

    // ==== 4. refusals by name =================================================

    [Fact]
    public void ALibraryOrMalformedXml_IsRefusedByName()
    {
        var lbr = Assert.Throws<FormatException>(() => EagleSchematicReader.Read(EagleFixtures.Library));
        Assert.Contains("component library", lbr.Message);
        Assert.Contains("EagleLibraryReader", lbr.Message);

        var bad = Assert.Throws<FormatException>(() => EagleSchematicReader.Read("<eagle><drawing>"));
        Assert.Contains("malformed", bad.Message);

        // And the .lbr reader points a schematic at THIS reader (the two-way signpost).
        var sch = Assert.Throws<FormatException>(() => EagleLibraryReader.Read(Sch(Body)));
        Assert.Contains("EagleSchematicReader", sch.Message);
    }
}
