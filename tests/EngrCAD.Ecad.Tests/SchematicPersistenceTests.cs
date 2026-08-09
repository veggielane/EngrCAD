using System.Text.RegularExpressions;
using EngrCAD.Ecad;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Ecad.Tests;

public class SchematicPersistenceTests
{
    // ---- the byte fixed point -----------------------------------------------

    [Fact]
    public void SaveLoadSave_IsAByteIdenticalFixedPoint()
    {
        var s1 = Fixtures.LedIndicator().Save();
        var s2 = Schematic.Load(s1).Save();
        Assert.Equal(s1, s2);
    }

    [Fact]
    public void SaveLoadSave_IsAByteIdenticalFixedPoint_WithNoConnectsAndStubs()
    {
        var sch = new Schematic("mixed");
        var u = sch.Add("U1", Fixtures.OpAmp());
        sch.Connect("N1", u.Pin("1"), u.Pin("2"));
        sch.Connect("N2", u.Pin("3"), u.Pin("4"));
        sch.Connect("N3", u.Pin("6"), u.Pin("7"));
        sch.Stub("T8", u.Pin("8"));
        sch.NoConnect(u.Pin("5"));

        var s1 = sch.Save();
        var s2 = Schematic.Load(s1).Save();
        Assert.Equal(s1, s2);
    }

    [Fact]
    public void TwoLoadsOfOneFile_ProduceStructurallyIdenticalGraphs()
    {
        var json = Fixtures.LedIndicator().Save();
        var a = Schematic.Load(json);
        var b = Schematic.Load(json);

        Assert.Equal(a.Name, b.Name);
        Assert.Equal(
            a.Components.Select(c => (c.ReferenceDesignator, c.Definition.Name, c.Value)),
            b.Components.Select(c => (c.ReferenceDesignator, c.Definition.Name, c.Value)));
        Assert.Equal(
            a.Nets.Select(n => (n.Name, n.Kind, string.Join(",", n.Pins.Select(p => p.ToString())))),
            b.Nets.Select(n => (n.Name, n.Kind, string.Join(",", n.Pins.Select(p => p.ToString())))));
    }

    [Fact]
    public void RoundTrip_PreservesConnectivity_Exactly()
    {
        var loaded = Schematic.Load(Fixtures.LedIndicator().Save());

        var report = loaded.Check();
        Assert.True(report.Ok, report.ToString());
        Assert.Equal(6, report.TotalPins);

        var vcc = Assert.Single(loaded.Nets, n => n.Name == "VCC");
        Assert.Equal(
            new[] { "BT1.+", "R1.1" },
            vcc.Pins.Select(p => p.ToString()).OrderBy(s => s));
    }

    // ---- definitions are interned once, shared by identity ------------------

    [Fact]
    public void ADefinitionUsedByManyComponents_IsWrittenOnce()
    {
        var sch = new Schematic();
        var r = Fixtures.Resistor();
        sch.Add("R1", r);
        sch.Add("R2", r);
        sch.Add("R3", r);   // one PartDefinition object, three placements

        var json = sch.Save();

        // Exactly one definition record.
        Assert.Single(Regex.Matches(json, "\"id\": \"d0\""));
        Assert.DoesNotContain("\"id\": \"d1\"", json);
    }

    [Fact]
    public void RoundTrip_RestoresSharedDefinitionIdentity()
    {
        var sch = new Schematic();
        var r = Fixtures.Resistor();
        sch.Add("R1", r);
        sch.Add("R2", r);

        var loaded = Schematic.Load(sch.Save());

        // The two loaded components reference the SAME definition object — a reference, not a copy.
        Assert.Same(loaded.Components[0].Definition, loaded.Components[1].Definition);
    }

    // ---- write-only-when-stated ---------------------------------------------

    [Fact]
    public void OptionalFields_AreWrittenOnlyWhenStated()
    {
        var sch = new Schematic();
        var r = sch.Add("R1", Fixtures.Resistor());          // no value
        var withValue = sch.Add("R2", Fixtures.Resistor(), "330");
        sch.Connect("N", r.Pin("1"), withValue.Pin("1"));    // Signal net
        sch.NoConnect(r.Pin("2"), withValue.Pin("2"));       // NoConnect net

        var json = sch.Save();

        Assert.DoesNotContain("\"kind\": \"Signal\"", json);   // Signal is the default
        Assert.Contains("\"kind\": \"NoConnect\"", json);      // and non-default kinds are written
        Assert.Contains("\"value\": \"330\"", json);           // a stated value is written
        Assert.Single(Regex.Matches(json, "\"value\":"));      // the empty one is omitted
    }

    // ---- footprint round-trips exactly --------------------------------------

    [Fact]
    public void Footprint_RoundTripsWithExactPadGeometry()
    {
        var loaded = Schematic.Load(Fixtures.LedIndicator().Save());
        var footprint = loaded.Find("R1")!.Definition.Footprint;

        Assert.NotNull(footprint);
        Assert.Equal("R0805", footprint!.Name);
        Assert.Equal(2, footprint.Pads.Count);
        Assert.Equal(-1.0, footprint.Pads[0].Center.X);
        Assert.Equal(1.2, footprint.Pads[0].Width);
        Assert.Equal(PadShape.Rectangular, footprint.Pads[0].Shape);
        Assert.Equal(PadShape.RoundedRectangle, footprint.Pads[1].Shape);
    }

    // ---- the body is code: it does not travel, but a library re-attaches it -

    [Fact]
    public void Body_IsNotSerialized_AndIsReattachedByLibrary()
    {
        var sch = new Schematic();
        sch.Add("R1", new PartDefinition(
            "R_BODY", "R",
            [new Pin("1"), new Pin("2")],
            body: () => Shape.Box(2, 1.25, 0.5)));

        var json = sch.Save();

        // Loaded without a library: data-only, no body.
        Assert.Null(Schematic.Load(json).Find("R1")!.Definition.Body);

        // Loaded with a library keyed on the definition name: the body is re-attached.
        var library = new PartLibrary().Register("R_BODY", () => Shape.Box(2, 1.25, 0.5));
        var body = Schematic.Load(json, library).Find("R1")!.Definition.Body;
        Assert.NotNull(body);
        Assert.NotNull(body!());
    }

    // ---- structural refusals name the offender ------------------------------

    [Fact]
    public void ComponentReferencingAMissingDefinition_IsRefusedByName()
    {
        var sch = new Schematic();
        sch.Add("R1", Fixtures.Resistor());
        sch.Add("R2", Fixtures.Resistor());
        // Rename the (single, shared) definition's id so both components' `"def": "d0"`
        // references dangle — the definition is the source, so the instances are not loadable.
        var json = sch.Save().Replace("\"id\": \"d0\"", "\"id\": \"dGONE\"");

        var ex = Assert.Throws<FormatException>(() => Schematic.Load(json));
        Assert.Contains("d0", ex.Message);
        Assert.Contains("not loadable", ex.Message);
    }

    [Fact]
    public void NetReferencingAMissingComponent_IsRefusedByName()
    {
        var sch = new Schematic();
        var r = sch.Add("R1", Fixtures.Resistor());
        var d = sch.Add("D1", Fixtures.Led());
        sch.Connect("N", r.Pin("1"), d.Pin("A"));
        // Rename only the component record's ref (key "ref"), leaving the net's `"c": "R1"`.
        var json = sch.Save().Replace("\"ref\": \"R1\"", "\"ref\": \"RENAMED\"");

        var ex = Assert.Throws<FormatException>(() => Schematic.Load(json));
        Assert.Contains("R1", ex.Message);
    }

    [Fact]
    public void NetReferencingAMissingPin_IsRefusedByName()
    {
        var sch = new Schematic();
        var r = sch.Add("R1", Fixtures.Resistor());
        var d = sch.Add("D1", Fixtures.Led());
        sch.Connect("N", r.Pin("1"), d.Pin("K"));
        // Rename the LED's pin 'K' in its definition; the net still references 'K'.
        var json = sch.Save().Replace("\"number\": \"K\"", "\"number\": \"KK\"");

        var ex = Assert.Throws<FormatException>(() => Schematic.Load(json));
        Assert.Contains("no such pin", ex.Message);
    }

    [Fact]
    public void UnknownFormatOrVersion_IsRefusedByName()
    {
        var json = Fixtures.LedIndicator().Save();
        Assert.Throws<FormatException>(() =>
            Schematic.Load(json.Replace("\"engrcad-schematic\"", "\"something-else\"")));
        Assert.Throws<FormatException>(() =>
            Schematic.Load(json.Replace("\"version\": 1", "\"version\": 99")));
    }

    // ---- files ---------------------------------------------------------------

    [Fact]
    public void SaveFile_LoadFile_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ecad-{Guid.NewGuid():N}.json");
        try
        {
            Fixtures.LedIndicator().SaveFile(path);
            var loaded = Schematic.LoadFile(path);
            Assert.True(loaded.Check().Ok);
            Assert.Equal(File.ReadAllText(path), loaded.Save());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
