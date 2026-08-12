using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Gerber X2 attributes (opt-in). X2 adds METADATA — the <c>%TO.N,&lt;net&gt;*%</c> object attribute (a
/// board house's net-compare datum) and a <c>%TF.GenerationSoftware%</c> file attribute — WITHOUT
/// changing the geometry. So the oracle is that stripping the X2 attribute lines recovers the plain
/// Gerber byte-for-byte, the default (off) output is byte-identical to before, and the X2 file
/// round-trips its copper through the reader exactly (attributes are ignored).
/// </summary>
public sealed class PcbGerberX2Tests
{
    private static PartDefinition Res() => new(
        "R", "R",
        [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive)],
        new Footprint("R", [Pad.Smd("1", new Vector2d(-1, 0), 1.2, 1.2), Pad.Smd("2", new Vector2d(1, 0), 1.2, 1.2)]));

    private static PcbLayout Routed()
    {
        var sch = new Schematic("x2");
        var r = sch.Add("R1", Res());
        var u = sch.Add("U1", Res());
        sch.Connect("VCC", r.Pin("1"), u.Pin("1"));
        sch.Connect("GND", r.Pin("2"), u.Pin("2"));
        var layout = new PcbLayout(sch, PcbBoard.Rectangle(40, 20, 1.6));
        layout.Place("R1", -10, 0);
        layout.Place("U1", 10, 0);
        string top = layout.Board.Stackup.Coppers[0].Name;
        layout.AddTrace("VCC", top, 0.3, [new Vector2d(-9, 1), new Vector2d(9, 1)]);
        layout.AddTrace("GND", top, 0.3, [new Vector2d(-9, -1), new Vector2d(9, -1)]);
        return layout;
    }

    private static string Top(FabricationOutput o) => o.CopperLayers[0].Gerber;

    // Strip the X2 attribute commands (%TF / %TO / %TA / %TD) — what is left is the plain geometry.
    private static string StripX2(string gerber) => string.Join("\n",
        gerber.Split('\n').Where(l =>
            !(l.StartsWith("%TF") || l.StartsWith("%TO") || l.StartsWith("%TA") || l.StartsWith("%TD"))));

    [Fact]
    public void X2_EmitsTheNetObjectAttributeAndGenerationSoftware()
    {
        string g = Top(PcbGerberExport.Generate(Routed(), includeX2: true));
        Assert.Contains("%TF.GenerationSoftware,EngrCAD,EngrCAD*%", g);
        Assert.Contains("%TF.FileFunction,Copper,L1,Top*%", g);   // the top copper layer's role
        Assert.Contains("%TO.N,VCC*%", g);   // the VCC trace's object attribute
        Assert.Contains("%TO.N,GND*%", g);
    }

    [Fact]
    public void X2Off_IsByteIdenticalAndStrippingX2RecoversThePlainGerber()
    {
        string plain = Top(PcbGerberExport.Generate(Routed()));
        string offExplicit = Top(PcbGerberExport.Generate(Routed(), includeX2: false));
        string x2 = Top(PcbGerberExport.Generate(Routed(), includeX2: true));

        // Off is exactly the pre-X2 output, and carries no attributes.
        Assert.Equal(plain, offExplicit);
        Assert.DoesNotContain("%TO", plain);
        Assert.DoesNotContain("%TF.Generation", plain);

        // X2 ADDS ONLY attribute lines — stripping them recovers the plain Gerber byte-for-byte.
        Assert.NotEqual(plain, x2);                 // it really did add something
        Assert.Equal(plain, StripX2(x2));           // and only attribute lines
    }

    [Fact]
    public void AnX2Gerber_RoundTripsItsCopperExactly_TheReaderIgnoresAttributes()
    {
        var plain = PcbGerberExport.Generate(Routed());
        var x2 = PcbGerberExport.Generate(Routed(), includeX2: true);

        // The reader does not choke on the attributes, and recovers the SAME copper as the plain file
        // (X2 changes no geometry).
        var plainCopper = GerberReader.Read(Top(plain)).Copper;
        var x2Copper = GerberReader.Read(Top(x2)).Copper;
        Assert.Equal(plainCopper.Count, x2Copper.Count);
        Assert.Equal(plainCopper.Sum(r => r.Area), x2Copper.Sum(r => r.Area), 9);
    }
}
