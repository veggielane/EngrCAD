using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// <see cref="DxfDocument"/>: sketches out as exact LWPOLYLINE bulges / CIRCLEs,
/// entities back in as sketches (closed polylines directly, loose LINE/ARC chained at
/// the weld tier), diagnostics instead of exceptions for what cannot map.
/// The strongest check is the round trip: a line+arc sketch's exact <c>Area()</c>
/// must survive save → load → ToSketches bit-tight, because bulge = tan(sweep/4) is
/// an exact arc encoding.
/// </summary>
public class DxfTests
{
    private static Sketch SlottedPlate() => Sketch.RoundedRectangle(40, 24, 6);

    [Fact]
    public void SketchRoundTrip_PreservesExactArea()
    {
        var document = new DxfDocument();
        document.Add(SlottedPlate(), layer: "outline");

        using var writer = new StringWriter();
        document.Save(writer);

        var loaded = DxfDocument.Load(new StringReader(writer.ToString()));
        Assert.Empty(loaded.Diagnostics);
        var sketches = loaded.ToSketches(out var diagnostics);
        Assert.Empty(diagnostics);
        var sketch = Assert.Single(sketches);
        Assert.Equal(SlottedPlate().Area(), sketch.Area(), 9);
    }

    [Fact]
    public void Circle_BecomesACircleEntityAndComesBack()
    {
        var document = new DxfDocument();
        document.Add(Sketch.Circle((5, -3), 7), layer: "bore");
        var circle = Assert.IsType<DxfCircle>(Assert.Single(document.Entities));
        Assert.Equal(new Vector2d(5, -3), circle.Center);
        Assert.Equal(7, circle.Radius);

        using var writer = new StringWriter();
        document.Save(writer);
        var sketch = Assert.Single(DxfDocument.Load(new StringReader(writer.ToString())).ToSketches());
        Assert.Equal(Math.PI * 49, sketch.Area(), 9);
    }

    [Fact]
    public void SketchWithHole_WritesOneLoopPerEntity()
    {
        var document = new DxfDocument();
        document.Add(Sketch.Rectangle(30, 20).WithHole(Sketch.Circle(4)), layer: "profile");
        Assert.Equal(2, document.Entities.Count);
        Assert.IsType<DxfPolyline>(document.Entities[0]);
        Assert.IsType<DxfCircle>(document.Entities[1]);

        // Back in: two sketches (nesting is deliberately the caller's decision).
        using var writer = new StringWriter();
        document.Save(writer);
        Assert.Equal(2, DxfDocument.Load(new StringReader(writer.ToString())).ToSketches().Count);
    }

    [Fact]
    public void LooseLinesAndArcs_ChainIntoAClosedSketch()
    {
        // A 10x10 square with one quarter-circle corner, drawn as loose entities in
        // scrambled order with one line reversed.
        var document = new DxfDocument();
        document.Add(new DxfLine((0, 0), (10, 0)));
        document.Add(new DxfLine((10, 6), (10, 0)));                    // reversed
        document.Add(new DxfArc((6, 6), 4, 0, 90));                     // (10,6) -> (6,10) CCW
        document.Add(new DxfLine((6, 10), (0, 10)));
        document.Add(new DxfLine((0, 10), (0, 0)));

        var sketches = document.ToSketches(out var diagnostics);
        Assert.Empty(diagnostics);
        var sketch = Assert.Single(sketches);
        // Square minus the corner outside the quarter circle: 100 - (16 - 4pi).
        Assert.Equal(100 - 16 + 4 * Math.PI, sketch.Area(), 9);
    }

    [Fact]
    public void UnclosedChains_AreReportedNotInvented()
    {
        var document = new DxfDocument();
        document.Add(new DxfLine((0, 0), (10, 0)));
        document.Add(new DxfLine((10, 0), (10, 5)));   // never closes

        var sketches = document.ToSketches(out var diagnostics);
        Assert.Empty(sketches);
        Assert.Contains(diagnostics, d => d.Contains("do not close"));
    }

    /// <summary>
    /// An entity kind outside the 2D drawing vocabulary is counted into the diagnostics
    /// and skipped — never guessed at. (TEXT used to be one of these and is now read, so
    /// the case is made with MTEXT, which genuinely is not supported.)
    /// </summary>
    [Fact]
    public void UnknownEntities_AreSkippedWithDiagnostics()
    {
        const string dxf = """
            0
            SECTION
            2
            ENTITIES
            0
            MTEXT
            8
            notes
            1
            hello
            0
            CIRCLE
            8
            0
            10
            0
            20
            0
            40
            5
            0
            ENDSEC
            0
            EOF
            """;
        var document = DxfDocument.Load(new StringReader(dxf));
        Assert.Single(document.Entities);
        Assert.Contains(document.Diagnostics, d => d.Contains("MTEXT"));
    }

    [Fact]
    public void LayerFilter_SelectsEntities()
    {
        var document = new DxfDocument();
        document.Add(Sketch.Circle(3), layer: "a");
        document.Add(Sketch.Circle(5), layer: "b");
        Assert.Equal(["a", "b"], document.Layers);
        var only = Assert.Single(document.ToSketches(out _, layer: "b"));
        Assert.Equal(Math.PI * 25, only.Area(), 9);
    }

    [Fact]
    public void BezierSketch_FlattensWithinChordTolerance()
    {
        var wave = Sketch.Start(0, 0)
            .BezierTo((3, 4), (7, -4), (10, 0))
            .LineTo(10, -3).LineTo(0, -3).Close();
        var document = new DxfDocument();
        document.Add(wave, chordTolerance: 1e-4);

        using var writer = new StringWriter();
        document.Save(writer);
        var sketch = Assert.Single(DxfDocument.Load(new StringReader(writer.ToString())).ToSketches(out var diag));
        Assert.Empty(diag);
        // Flattened, so approximate — but within a generous multiple of the tolerance
        // times the curve length.
        Assert.Equal(wave.Area(), sketch.Area(), 2);
    }
}
