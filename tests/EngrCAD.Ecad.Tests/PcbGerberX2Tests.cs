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

    // ==== FileFunction on EVERY Gerber (mask / silk / paste / outline), not just copper =============

    [Fact]
    public void X2_EmitsFileFunctionOnEveryGerber_MaskSilkPasteAndOutline()
    {
        var o = PcbGerberExport.Generate(Routed(), includeX2: true);
        string top = Routed().Board.Stackup.Top.Name;
        string Side(string layer) => layer == top ? "Top" : "Bot";

        Assert.NotEmpty(o.MaskLayers);
        Assert.NotEmpty(o.PasteLayers);

        // Every non-copper Gerber names who made it AND its role, so the package is self-describing.
        foreach (var m in o.MaskLayers)
        {
            Assert.Contains("%TF.GenerationSoftware,EngrCAD,EngrCAD*%", m.Gerber);
            Assert.Contains($"%TF.FileFunction,Soldermask,{Side(m.Layer)}*%", m.Gerber);
        }
        foreach (var p in o.PasteLayers)
            Assert.Contains($"%TF.FileFunction,SolderPaste,{Side(p.Layer)}*%", p.Gerber);
        foreach (var s in o.SilkLayers)
            Assert.Contains($"%TF.FileFunction,Legend,{Side(s.Layer)}*%", s.Gerber);

        // The board outline is a NON-PLATED profile.
        Assert.Contains("%TF.FileFunction,Profile,NP*%", o.OutlineGerber);
        Assert.Contains("%TF.GenerationSoftware,EngrCAD,EngrCAD*%", o.OutlineGerber);
    }

    [Fact]
    public void X2Off_EveryNonCopperGerberIsByteIdentical_AndStrippingX2Recovers()
    {
        var plain = PcbGerberExport.Generate(Routed());
        var x2 = PcbGerberExport.Generate(Routed(), includeX2: true);

        // Off: no X2 attribute anywhere. On: only attribute lines are added (strip recovers the plain file).
        for (int i = 0; i < plain.MaskLayers.Count; i++)
        {
            Assert.DoesNotContain("%TF", plain.MaskLayers[i].Gerber);
            Assert.Equal(plain.MaskLayers[i].Gerber, StripX2(x2.MaskLayers[i].Gerber));
        }
        for (int i = 0; i < plain.PasteLayers.Count; i++)
            Assert.Equal(plain.PasteLayers[i].Gerber, StripX2(x2.PasteLayers[i].Gerber));
        for (int i = 0; i < plain.SilkLayers.Count; i++)
            Assert.Equal(plain.SilkLayers[i].Gerber, StripX2(x2.SilkLayers[i].Gerber));

        Assert.DoesNotContain("%TF", plain.OutlineGerber);
        Assert.Equal(plain.OutlineGerber, StripX2(x2.OutlineGerber));
    }

    // ==== component / pad object attributes (%TO.C / %TO.P) on copper pad flashes =================

    [Fact]
    public void X2_EmitsComponentAndPadObjectAttributesOnCopperPads()
    {
        string g = Top(PcbGerberExport.Generate(Routed(), includeX2: true));

        // Each component pad flash is tied back to its component pin (the assembly datum): %TO.C,<refdes>
        // and %TO.P,<refdes>,<pad>. R1 and U1 each have pads 1 and 2 on the top layer.
        Assert.Contains("%TO.C,R1*%", g);
        Assert.Contains("%TO.P,R1,1*%", g);
        Assert.Contains("%TO.P,R1,2*%", g);
        Assert.Contains("%TO.C,U1*%", g);
        Assert.Contains("%TO.P,U1,1*%", g);
        Assert.Contains("%TO.P,U1,2*%", g);

        // A plain (non-X2) Gerber carries none — and the traces / drills are NOT tagged with a pad.
        string plain = Top(PcbGerberExport.Generate(Routed()));
        Assert.DoesNotContain("%TO.C", plain);
        Assert.DoesNotContain("%TO.P", plain);

        // Stripping the attribute lines still recovers the plain geometry byte-for-byte (the assembly
        // attributes carry no geometry, exactly as %TO.N and %TF do).
        Assert.Equal(plain, StripX2(g));
    }

    [Fact]
    public void AnX2MaskGerber_RoundTripsItsWindowsExactly_TheReaderIgnoresAttributes()
    {
        // The non-copper round-trip oracle: the mask reader recovers the SAME windows with X2 on, so the
        // FileFunction/GenerationSoftware attributes carry no geometry.
        var plain = PcbGerberExport.Generate(Routed());
        var x2 = PcbGerberExport.Generate(Routed(), includeX2: true);
        var pm = GerberReader.Read(plain.MaskLayers[0].Gerber).Copper;
        var xm = GerberReader.Read(x2.MaskLayers[0].Gerber).Copper;
        Assert.Equal(pm.Count, xm.Count);
        Assert.Equal(pm.Sum(r => r.Area), xm.Sum(r => r.Area), 9);
    }
}
