using EngrCAD.BRep;
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
            HATCH
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
        // HATCH is genuinely unread here (the fixture was MTEXT until the reader
        // learned it — an unknown-entity fixture must stay unknown to keep testing).
        Assert.Contains(document.Diagnostics, d => d.Contains("HATCH"));
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

    // ---- SPLINE: the exact route for cubics ----------------------------------

    private static Sketch Wave() => Sketch.Start(0, 0)
        .BezierTo((3, 4), (7, -4), (10, 0))
        .LineTo(10, -3).LineTo(0, -3).Close();

    /// <summary>
    /// The whole point of the mode: the exact route's area survives to the digit, where
    /// the flattened route above manages 2 decimals at a 1e-4 chord tolerance. Nothing is
    /// approximated in either direction — a cubic Bézier IS a clamped degree-3 B-spline
    /// with four control points.
    /// </summary>
    [Fact]
    public void SplineMode_RoundTripsACubicExactly()
    {
        var document = new DxfDocument();
        document.Add(Wave(), layer: "profile", curves: DxfCurveMode.Spline);

        using var writer = new StringWriter();
        document.Save(writer);

        var loaded = DxfDocument.Load(new StringReader(writer.ToString()));
        Assert.Empty(loaded.Diagnostics);
        var sketch = Assert.Single(loaded.ToSketches(out var diagnostics));
        Assert.Empty(diagnostics);
        Assert.Equal(Wave().Area(), sketch.Area(), 9);
    }

    /// <summary>
    /// The cubic becomes one SPLINE and the three straight segments one open LWPOLYLINE —
    /// a chain, which the reader closes by endpoint. Entity ORDER is deliberately not
    /// asserted: `Sketch` normalizes winding, so which end of the loop comes first is the
    /// sketch's business rather than the writer's, and the chain closes either way.
    /// </summary>
    [Fact]
    public void SplineMode_BreaksTheLoopIntoAChainAtEachCubic()
    {
        var document = new DxfDocument();
        document.Add(Wave(), curves: DxfCurveMode.Spline);

        Assert.Equal(2, document.Entities.Count);
        var spline = Assert.Single(document.Entities.OfType<DxfSpline>());
        Assert.Equal(3, spline.Degree);
        Assert.Equal(4, spline.ControlPoints.Count);
        Assert.Equal(8, spline.Knots.Count);
        Assert.False(spline.IsRational);
        // The control points are the sketch's own, in whichever traversal sense it settled
        // on — so the interior pair is (3, 4)/(7, -4) or its reverse, with nothing computed.
        Assert.Contains(new Vector2d(3, 4), spline.ControlPoints);
        Assert.Contains(new Vector2d(7, -4), spline.ControlPoints);

        var run = Assert.Single(document.Entities.OfType<DxfPolyline>());
        Assert.False(run.Closed);
        // Three straight segments, so four stated vertices: an open polyline has no wrap
        // to supply its last point.
        Assert.Equal(4, run.Points.Count);

        // The two entities really do form ONE closed loop: each end of the spline meets
        // an end of the run.
        Assert.Equal(run.Points[0], spline.ControlPoints[^1]);
        Assert.Equal(run.Points[^1], spline.ControlPoints[0]);
    }

    /// <summary>
    /// The mode only ever affects Béziers, which is what makes it safe to reach for: a
    /// sketch with none writes byte-for-byte the same file either way.
    /// </summary>
    [Fact]
    public void SplineMode_ChangesNothingForASketchWithNoCubics()
    {
        string Write(DxfCurveMode mode)
        {
            var document = new DxfDocument();
            document.Add(SlottedPlate(), layer: "outline", curves: mode);
            document.Add(Sketch.Circle((5, -3), 7), layer: "bore", curves: mode);
            using var writer = new StringWriter();
            document.Save(writer);
            return writer.ToString();
        }

        Assert.Equal(Write(DxfCurveMode.Flatten), Write(DxfCurveMode.Spline), ignoreLineEndingDifferences: false);
    }

    /// <summary>A polybezier written as ONE multi-segment spline (what several exporters
    /// emit) is already in Bézier form, so its control points split four at a time with
    /// nothing computed.</summary>
    [Fact]
    public void MultiSegmentBezierSpline_SplitsIntoItsSegments()
    {
        // Two cubics sharing (10, 0): knots clamped at both ends with an interior triple.
        var document = new DxfDocument();
        document.Add(new DxfSpline(
            [(0, 0), (3, 4), (7, -4), (10, 0), (13, 4), (17, -4), (20, 0)],
            3,
            [0, 0, 0, 0, 1, 1, 1, 2, 2, 2, 2]));
        document.Add(new DxfLine((20, 0), (20, -5)));
        document.Add(new DxfLine((20, -5), (0, -5)));
        document.Add(new DxfLine((0, -5), (0, 0)));

        var sketch = Assert.Single(document.ToSketches(out var diagnostics));
        Assert.Empty(diagnostics);

        // Two cubics + three lines. Each cubic is antisymmetric about its own midpoint, so
        // it contributes exactly the area of the trapezoid under its chord: the region is
        // the 20 x 5 rectangle exactly.
        Assert.Equal(100, sketch.Area(), 9);
    }

    [Fact]
    public void DegreeOneSpline_IsAPolyline()
    {
        var document = new DxfDocument();
        document.Add(new DxfSpline([(0, 0), (10, 0), (10, 10), (0, 10)], 1, [0, 0, 1, 2, 3, 3]));
        document.Add(new DxfLine((0, 10), (0, 0)));

        var sketch = Assert.Single(document.ToSketches(out var diagnostics));
        Assert.Empty(diagnostics);
        Assert.Equal(100, sketch.Area(), 9);
    }

    /// <summary>
    /// Two-sided agreement between a set of converted cubics and the source spline: the
    /// largest distance from a point of the cubics to the spline, and the largest from a
    /// point of the spline to the cubics. Set-wise on purpose — a sketch normalises its
    /// winding, so which cubic covers which span and in which sense is its own business,
    /// while "the same curve" is not.
    /// </summary>
    private static (double FromCurves, double FromSpline) Hausdorff(
        IReadOnlyList<BezierCurve2d> cubics, NurbsCurve2d source, int samples = 200)
    {
        double fromCurves = 0, fromSpline = 0;
        foreach (var cubic in cubics)
        {
            for (int i = 0; i <= samples; i++)
            {
                var p = cubic.PointAt((double)i / samples);
                fromCurves = Math.Max(
                    fromCurves, Nearest(p, source.PointAt, source.Domain.Start, source.Domain.End));
            }
        }
        for (int j = 0; j <= samples; j++)
        {
            double u = source.Domain.Start + (source.Domain.End - source.Domain.Start) * j / samples;
            var q = source.PointAt(u);
            fromSpline = Math.Max(fromSpline, cubics.Min(c => Nearest(q, c.PointAt, 0, 1)));
        }
        return (fromCurves, fromSpline);

        // Coarse scan then golden-section refinement — the true closest point, so the
        // measurement carries no sampling error of its own and 1e-9 means 1e-9.
        static double Nearest(Vector2d p, Func<double, Vector2d> eval, double a, double b)
        {
            const int scan = 64;
            int best = 0;
            double bestDistance = double.PositiveInfinity;
            for (int i = 0; i <= scan; i++)
            {
                double d = p.DistanceTo(eval(a + (b - a) * i / scan));
                if (d < bestDistance)
                {
                    (bestDistance, best) = (d, i);
                }
            }
            double lo = a + (b - a) * Math.Max(best - 1, 0) / scan;
            double hi = a + (b - a) * Math.Min(best + 1, scan) / scan;
            const double phi = 0.6180339887498949;
            for (int k = 0; k < 90; k++)
            {
                double m1 = hi - (hi - lo) * phi, m2 = lo + (hi - lo) * phi;
                if (p.DistanceTo(eval(m1)) < p.DistanceTo(eval(m2)))
                    hi = m2;
                else
                    lo = m1;
            }
            return Math.Min(bestDistance, p.DistanceTo(eval((lo + hi) / 2)));
        }
    }

    /// <summary>
    /// What cannot convert is refused for the SKETCH's reason rather than the
    /// decomposition's, and the messages say so: both of these decompose into exact Bézier
    /// segments, and neither result has a sketch segment to live in.
    /// </summary>
    [Fact]
    public void SplinesWithNoExactSketchForm_AreReportedForTheSketchsOwnReason()
    {
        var rational = new DxfDocument();
        rational.Add(new DxfSpline(
            [(0, 0), (3, 4), (7, -4), (10, 0)], 3, [0, 0, 0, 0, 1, 1, 1, 1], [1, 2, 2, 1]));
        Assert.Empty(rational.ToSketches(out var rationalDiagnostics));
        var rationalMessage = Assert.Single(rationalDiagnostics);
        Assert.Contains("rational", rationalMessage);
        Assert.Contains("POLYNOMIAL", rationalMessage);

        // Degree 4: the decomposition handles any degree; a sketch's highest polynomial
        // segment is a cubic, and degree reduction is not exact.
        var quartic = new DxfDocument();
        quartic.Add(new DxfSpline(
            [(0, 0), (3, 4), (7, -4), (10, 0), (13, 4)], 4, [0, 0, 0, 0, 0, 1, 1, 1, 1, 1]));
        Assert.Empty(quartic.ToSketches(out var quarticDiagnostics));
        var quarticMessage = Assert.Single(quarticDiagnostics);
        Assert.Contains("CUBIC", quarticMessage);
        Assert.Contains("Nothing here is a limit of the decomposition", quarticMessage);
    }

    /// <summary>
    /// The dangerous shape, and the reason the old reader refused it rather than trusting
    /// the control point COUNT: seven control points is Bézier-compatible (3k + 1), so a
    /// reader splitting them four at a time hands back a plausible, silently wrong profile.
    /// It is a genuine B-spline — clamped, interior knots 1, 2, 3 rather than triples — and
    /// it now DECOMPOSES, into the four cubics its four spans carry.
    ///
    /// <para>The mutation is what gives this teeth: the naive split shares the curve's
    /// endpoints, so only an interior sample separates them, and it is measured here rather
    /// than assumed.</para>
    /// </summary>
    [Fact]
    public void ClampedCubicWithSingleInteriorKnots_DecomposesRatherThanMisSplitting()
    {
        Vector2d[] control = [(0, 0), (3, 4), (7, -4), (10, 0), (13, 4), (17, -4), (20, 0)];
        double[] knots = [0, 0, 0, 0, 1, 2, 3, 4, 4, 4, 4];

        var document = new DxfDocument();
        document.Add(new DxfSpline(control, 3, knots));
        document.Add(new DxfLine((20, 0), (20, -5)));
        document.Add(new DxfLine((20, -5), (0, -5)));
        document.Add(new DxfLine((0, -5), (0, 0)));

        var sketch = Assert.Single(document.ToSketches(out var diagnostics));
        Assert.Empty(diagnostics);

        // Four spans -> four cubics, plus the three lines.
        Assert.Equal(7, sketch.Segments.Count);

        // The converted cubics ARE the source curve, asserted BOTH ways: nothing they carry
        // is off the spline, and nothing of the spline is missing from them. A one-way check
        // would pass a conversion that dropped a whole span.
        var source = new NurbsCurve2d(3, control, null, knots);
        var cubics = sketch.ToCurves().OfType<BezierCurve2d>().ToList();
        Assert.Equal(4, cubics.Count);
        var (fromCurves, fromSpline) = Hausdorff(cubics, source);
        Assert.True(fromCurves < 1e-9, $"the converted cubics drift off the spline by {fromCurves}");
        Assert.True(fromSpline < 1e-9, $"the spline is uncovered by up to {fromSpline}");

        // The mutation: the naive four-at-a-time split shares the endpoints and is a
        // different curve in between.
        var naive = new BezierCurve2d(control[0], control[1], control[2], control[3]);
        double naiveMiss = 0;
        for (int i = 0; i <= 64; i++)
            naiveMiss = Math.Max(naiveMiss, naive.PointAt(i / 64.0).DistanceTo(source.PointAt(i / 64.0)));
        Assert.True(naiveMiss > 1, $"the naive split is only {naiveMiss} away — the fixture cannot see it");
    }

    /// <summary>
    /// A UNIFORM (unclamped) cubic — the shape a periodic exporter emits, and the one the
    /// reader used to leave on the floor. Its domain is the interior spans only, so the
    /// converted chain starts and ends where the CURVE does rather than at the first and
    /// last control points.
    /// </summary>
    [Fact]
    public void AUniformUnclampedSpline_ConvertsExactly()
    {
        Vector2d[] control = [(0, 0), (3, 4), (7, -4), (10, 0), (12, 3)];
        double[] knots = [0, 1, 2, 3, 4, 5, 6, 7, 8];
        var source = new NurbsCurve2d(3, control, null, knots);

        var document = new DxfDocument();
        document.Add(new DxfSpline(control, 3, knots));
        // Two spans, so two cubics — an open chain, which the reader reports rather than
        // inventing a closing segment for.
        Assert.Empty(document.ToSketches(out var openDiagnostics));
        Assert.Contains(openDiagnostics, d => d.Contains("do not close"));

        // Close it with a line and read the geometry back.
        var start = source.PointAt(source.Domain.Start);
        var end = source.PointAt(source.Domain.End);
        document.Add(new DxfLine(end, start));
        var sketch = Assert.Single(document.ToSketches(out var closedDiagnostics));
        Assert.Empty(closedDiagnostics);
        Assert.Equal(3, sketch.Segments.Count);

        var cubics = sketch.ToCurves().OfType<BezierCurve2d>().ToList();
        Assert.Equal(2, cubics.Count);
        var (fromCurves, fromSpline) = Hausdorff(cubics, source);
        Assert.True(fromCurves < 1e-9, $"the converted cubics drift off the spline by {fromCurves}");
        Assert.True(fromSpline < 1e-9, $"the spline is uncovered by up to {fromSpline}");
    }

    /// <summary>
    /// The general decomposition reaches the narrow case bit-identically — no arithmetic
    /// runs where an interior knot's multiplicity already equals the degree — which is why
    /// routing every spline through it changed nothing for the files this library writes.
    /// </summary>
    [Fact]
    public void ABezierFormSpline_ReadsBackWithBitIdenticalControlPoints()
    {
        Vector2d[] control = [(0, 0), (3.25, 4.5), (7.125, -4.75), (10, 0)];
        var document = new DxfDocument();
        document.Add(new DxfSpline(control, 3, [0, 0, 0, 0, 1, 1, 1, 1]));
        document.Add(new DxfLine((10, 0), (10, -5)));
        document.Add(new DxfLine((10, -5), (0, -5)));
        document.Add(new DxfLine((0, -5), (0, 0)));

        var sketch = Assert.Single(document.ToSketches(out var diagnostics));
        Assert.Empty(diagnostics);
        var cubic = Assert.Single(sketch.ToCurves().OfType<BezierCurve2d>());
        Vector2d[] read = [cubic.PointAt(0), cubic.ControlPoints[1], cubic.ControlPoints[2], cubic.PointAt(1)];
        bool forward = read[0] == control[0];
        for (int i = 0; i < 4; i++)
        {
            var expected = control[forward ? i : 3 - i];
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected.X), BitConverter.DoubleToInt64Bits(read[i].X));
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Y), BitConverter.DoubleToInt64Bits(read[i].Y));
        }
    }

    [Fact]
    public void MalformedSplines_AreRefusedAtConstructionAndAtLoad()
    {
        var document = new DxfDocument();

        // Wrong knot count for the degree and control point count.
        Assert.Throws<ArgumentException>(
            () => document.Add(new DxfSpline([(0, 0), (1, 1), (2, 0), (3, 1)], 3, [0, 0, 0, 1, 1, 1])));

        // The reader states the count it wanted rather than guessing at the curve.
        const string dxf = """
            0
            SECTION
            2
            ENTITIES
            0
            SPLINE
            8
            0
            71
            3
            10
            0
            20
            0
            0
            ENDSEC
            0
            EOF
            """;
        var loaded = DxfDocument.Load(new StringReader(dxf));
        Assert.Empty(loaded.Entities);
        Assert.Contains(loaded.Diagnostics, d => d.Contains("SPLINE") && d.Contains("knot"));
    }

    // ---- $INSUNITS ------------------------------------------------------------

    /// <summary>A file that does not SAY what its numbers mean leaves every reader to
    /// guess — the same duty the LTYPE table has.</summary>
    [Fact]
    public void Writer_DeclaresItsUnits()
    {
        var document = new DxfDocument();
        document.Add(Sketch.Circle(5));
        using var writer = new StringWriter();
        document.Save(writer);

        string text = writer.ToString();
        Assert.Contains("$INSUNITS", text);
        Assert.Equal(DxfUnits.Millimetres, DxfDocument.Load(new StringReader(text)).Units);
    }

    /// <summary>
    /// The failure a unit declaration exists to prevent is a file that imports CLEANLY at
    /// the wrong scale, so an inch file is rescaled rather than merely reported — and the
    /// document comes back labelled millimetres, so re-saving it is correct rather than
    /// declaring inches over millimetre coordinates.
    /// </summary>
    [Fact]
    public void InchFile_IsScaledToMillimetres()
    {
        var inches = new DxfDocument { Units = DxfUnits.Inches };
        inches.Add(Sketch.Circle((2, 0), 1));                    // a 1 inch radius at x = 2 in
        using var writer = new StringWriter();
        inches.Save(writer);

        var loaded = DxfDocument.Load(new StringReader(writer.ToString()));
        var circle = Assert.IsType<DxfCircle>(Assert.Single(loaded.Entities));
        Assert.Equal(25.4, circle.Radius, 9);
        Assert.Equal(50.8, circle.Center.X, 9);
        Assert.Equal(DxfUnits.Millimetres, loaded.Units);
        Assert.Contains(loaded.Diagnostics, d => d.Contains("Inches") && d.Contains("25.4"));
    }

    /// <summary>A bulge is tan(sweep/4) — an ANGLE — so a uniform rescale must move the
    /// vertices and leave it alone, or every arc changes shape.</summary>
    [Fact]
    public void RescalingAFile_MovesVerticesAndLeavesBulgesAlone()
    {
        var inches = new DxfDocument { Units = DxfUnits.Inches };
        inches.Add(SlottedPlate());
        using var writer = new StringWriter();
        inches.Save(writer);

        var loaded = DxfDocument.Load(new StringReader(writer.ToString()));
        var sketch = Assert.Single(loaded.ToSketches(out var diagnostics));
        Assert.Empty(diagnostics);
        // Area scales by the square of the factor, exactly — which only holds if the
        // corner arcs kept their sweeps.
        Assert.Equal(SlottedPlate().Area() * 25.4 * 25.4, sketch.Area(), 6);
    }

    /// <summary>
    /// Unitless (0) is the file's honest "no claim" and is the value a great many real
    /// files carry. Inventing a factor for it would be the silent mis-scaling the whole
    /// feature exists to prevent, so the coordinates are left exactly as written.
    /// </summary>
    [Fact]
    public void UnitlessFile_IsNotScaled()
    {
        const string dxf = """
            0
            SECTION
            2
            HEADER
            9
            $INSUNITS
            70
            0
            0
            ENDSEC
            0
            SECTION
            2
            ENTITIES
            0
            CIRCLE
            8
            0
            10
            2
            20
            0
            40
            1
            0
            ENDSEC
            0
            EOF
            """;
        var loaded = DxfDocument.Load(new StringReader(dxf));
        var circle = Assert.IsType<DxfCircle>(Assert.Single(loaded.Entities));
        Assert.Equal(1, circle.Radius);
        Assert.Equal(2, circle.Center.X);
        Assert.Equal(DxfUnits.Unitless, loaded.Units);
        Assert.DoesNotContain(loaded.Diagnostics, d => d.Contains("scaled"));
    }

    /// <summary>A file with no header at all says nothing, which is not the same as
    /// saying millimetres — so nothing is scaled and the property reports the silence.</summary>
    [Fact]
    public void FileWithNoHeader_ReportsUnstatedUnits()
    {
        var document = new DxfDocument();
        document.Add(new DxfLine((0, 0), (10, 0)));
        var loaded = DxfDocument.Load(new StringReader("0\nSECTION\n2\nENTITIES\n0\nLINE\n8\n0\n10\n0\n20\n0\n11\n10\n21\n0\n0\nENDSEC\n0\nEOF"));

        Assert.Equal(DxfUnits.Unitless, loaded.Units);
        var line = Assert.IsType<DxfLine>(Assert.Single(loaded.Entities));
        Assert.Equal(10, line.End.X);
    }

    [Fact]
    public void UnknownUnitCode_IsNamedAndNothingIsScaled()
    {
        const string dxf = """
            0
            SECTION
            2
            HEADER
            9
            $INSUNITS
            70
            19
            0
            ENDSEC
            0
            SECTION
            2
            ENTITIES
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
        var loaded = DxfDocument.Load(new StringReader(dxf));
        Assert.Equal(5, Assert.IsType<DxfCircle>(Assert.Single(loaded.Entities)).Radius);
        Assert.Contains(loaded.Diagnostics, d => d.Contains("19"));
    }
}
