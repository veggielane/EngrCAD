using System.Globalization;
using System.Text;
using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// IGES import. There is no <c>IgesWriter</c> — the format is import-only by design — so
/// the write-then-read round-trip the STEP tests lean on is unavailable, and every fixture
/// here is a hand-built 80-column card deck. <see cref="IgesDeck"/> lays the columns out
/// so a test reads as the entity it is describing rather than as string arithmetic.
/// </summary>
public class IgesReaderTests
{
    // ---- record layer ----

    [Fact]
    public void AMinimalFileParsesItsGlobalSection()
    {
        var result = IgesReader.Read(new IgesDeck().WithLine((0, 0, 0), (10, 0, 0)).ToText());
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("unsupported"));
        Assert.Single(result.Curves);
    }

    [Theory]
    // Column 73 is the section letter on every card; anything else is not IGES.
    [InlineData('X', "section letter")]
    public void ABadSectionLetterIsRefusedByName(char letter, string expected)
    {
        string text = new IgesDeck().WithLine((0, 0, 0), (1, 0, 0)).ToText();
        int index = text.IndexOf("G      1", StringComparison.Ordinal);
        text = string.Concat(text.AsSpan(0, index), letter.ToString(), text.AsSpan(index + 1));

        var error = Assert.Throws<FormatException>(() => IgesReader.Read(text));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void ANonNumericSequenceNumberIsRefusedAsProbablyNotIges()
    {
        var error = Assert.Throws<FormatException>(
            () => IgesReader.Read("this is a plain text file, not a card deck\n"));
        Assert.Contains("probably not an IGES file", error.Message);
    }

    [Fact]
    public void AnOddDirectoryCardCountIsRefusedAsTruncated()
    {
        string text = new IgesDeck().WithLine((0, 0, 0), (1, 0, 0)).ToText();
        var lines = text.Split('\n').ToList();
        // Drop the second directory card: every entity occupies exactly two.
        lines.RemoveAt(lines.FindLastIndex(l => l.Length > 72 && l[72] == 'D'));

        var error = Assert.Throws<FormatException>(
            () => IgesReader.Read(string.Join('\n', lines)));
        Assert.Contains("two", error.Message);
        Assert.Contains("truncated", error.Message);
    }

    [Fact]
    public void ADirectoryPairDeclaringTwoEntityTypesIsRefused()
    {
        string text = new IgesDeck().WithLine((0, 0, 0), (1, 0, 0)).ToText();
        var lines = text.Split('\n').ToList();
        int second = lines.FindLastIndex(l => l.Length > 72 && l[72] == 'D');
        lines[second] = "     999" + lines[second][8..];

        var error = Assert.Throws<FormatException>(() => IgesReader.Read(string.Join('\n', lines)));
        Assert.Contains("do not belong to one entity", error.Message);
    }

    [Fact]
    public void AMissingTerminateSectionIsReportedNotFatal()
    {
        string text = new IgesDeck().WithLine((0, 0, 0), (1, 0, 0)).ToText();
        text = string.Join('\n', text.Split('\n').Where(l => !(l.Length > 72 && l[72] == 'T')));

        var result = IgesReader.Read(text);
        Assert.Single(result.Curves);
        Assert.Contains(result.Diagnostics, d => d.Contains("Terminate"));
    }

    [Fact]
    public void HollerithStringsMayContainTheParameterDelimiter()
    {
        // The classic silent-corruption case: an author field with a comma in it shifts
        // every later Global parameter by one unless the Hollerith count is honoured. Here
        // that would move the unit flag and change every coordinate.
        var deck = new IgesDeck { Author = "Lane, Chris" };
        var result = IgesReader.Read(deck.WithLine((0, 0, 0), (10, 0, 0)).ToText());

        var line = Assert.IsType<Line3d>(Assert.Single(result.Curves));
        Assert.Equal(10.0, line.End.X, 12);
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("scaled"));
    }

    [Fact]
    public void NonDefaultDelimitersAreHonoured()
    {
        var deck = new IgesDeck { ParameterDelimiter = '|', RecordDelimiter = '#' };
        var result = IgesReader.Read(deck.WithLine((1, 2, 3), (4, 5, 6)).ToText());
        var line = Assert.IsType<Line3d>(Assert.Single(result.Curves));
        Assert.Equal(new Vector3d(4, 5, 6), line.End);
    }

    [Fact]
    public void FortranStyleDExponentsParse()
    {
        var deck = new IgesDeck();
        deck.RawEntity(110, "110,0.0,0.0,0.0,1.5D+01,0.0,0.0;");
        var line = Assert.IsType<Line3d>(Assert.Single(IgesReader.Read(deck.ToText()).Curves));
        Assert.Equal(15.0, line.End.X, 12);
    }

    // ---- units ----

    [Theory]
    [InlineData(2, 1.0)]      // millimetre
    [InlineData(1, 25.4)]     // inch
    [InlineData(6, 1000.0)]   // metre
    [InlineData(10, 10.0)]    // centimetre
    public void UnitsAreScaledToMillimetres(int flag, double factor)
    {
        var deck = new IgesDeck { UnitFlag = flag };
        var result = IgesReader.Read(deck.WithLine((0, 0, 0), (2, 0, 0)).ToText());
        var line = Assert.IsType<Line3d>(Assert.Single(result.Curves));
        Assert.Equal(2 * factor, line.End.X, 9);
        if (factor == 1.0)
        {
            // Exact-== semantic guard: a millimetre file is bit-identical, not "scaled by 1".
            Assert.DoesNotContain(result.Diagnostics, d => d.Contains("scaled"));
        }
        else
        {
            Assert.Contains(result.Diagnostics, d => d.Contains("scaled by"));
        }
    }

    [Fact]
    public void AnUnknownUnitFlagFallsBackToMillimetresWithADiagnostic()
    {
        var deck = new IgesDeck { UnitFlag = 42 };
        var result = IgesReader.Read(deck.WithLine((0, 0, 0), (2, 0, 0)).ToText());
        Assert.Equal(2.0, ((Line3d)result.Curves[0]).End.X, 12);
        Assert.Contains(result.Diagnostics, d => d.Contains("millimetres assumed"));
    }

    // ---- curves ----

    [Fact]
    public void CircularArcsCarryTheirSweepAndStartAtPhaseZero()
    {
        var deck = new IgesDeck();
        // Centre (0,0), start (5,0), end (0,5): a quarter turn CCW in the z = 0 plane.
        deck.RawEntity(100, "100,0.0,0.0,0.0,5.0,0.0,0.0,5.0;");
        var result = IgesReader.Read(deck.ToText());

        var segment = Assert.IsType<CurveSegment>(Assert.Single(result.Curves));
        var circle = Assert.IsType<Circle3d>(segment.Base);
        Assert.Equal(5.0, circle.Radius, 12);
        // The circle's frame is built from the START point, so the arc begins exactly at
        // u = 0 — the phase-alignment rule the rest of the kernel depends on.
        Assert.Equal(new Vector3d(5, 0, 0), segment.PointAt(0));
        Assert.Equal(Math.PI / 2, segment.BaseEnd, 12);
        AssertClose(new Vector3d(0, 5, 0), segment.PointAt(1));
    }

    [Fact]
    public void AnArcWhoseStartEqualsItsEndIsAFullCircle()
    {
        var deck = new IgesDeck();
        deck.RawEntity(100, "100,0.0,0.0,0.0,3.0,0.0,3.0,0.0;");
        var circle = Assert.IsType<Circle3d>(Assert.Single(IgesReader.Read(deck.ToText()).Curves));
        Assert.Equal(3.0, circle.Radius, 12);
    }

    [Fact]
    public void RationalBSplineCurvesImportWithTheirWeightsAndKnots()
    {
        // A quarter circle as a rational quadratic: the classic conic B-spline, and the
        // one whose weights must survive or the curve bulges.
        var deck = new IgesDeck();
        deck.RawEntity(126,
            "126,2,2,1,0,0,0,"                         // K = 2, degree 2, planar, open
            + "0.0,0.0,0.0,1.0,1.0,1.0,"               // 6 knots
            + "1.0,0.70710678118654752,1.0,"           // 3 weights
            + "1.0,0.0,0.0,1.0,1.0,0.0,0.0,1.0,0.0,"   // 3 control points
            + "0.0,1.0,0.0,0.0,1.0;");                 // v0, v1, normal
        var curve = Assert.IsType<NurbsCurve>(Assert.Single(IgesReader.Read(deck.ToText()).Curves));

        Assert.Equal(2, curve.Degree);
        Assert.Equal(3, curve.ControlPoints.Count);
        Assert.Equal(6, curve.Knots.Count);
        Assert.Equal(Math.Sqrt(0.5), curve.Weights[1], 12);
        // The midpoint of a rational quarter circle is at 45 degrees on the unit circle;
        // a dropped weight would put it at the control polygon's midpoint instead.
        AssertClose(new Vector3d(Math.Sqrt(0.5), Math.Sqrt(0.5), 0), curve.PointAt(0.5), 1e-9);
    }

    [Fact]
    public void AConicArcWithNegativeDiscriminantImportsAsAnEllipse()
    {
        // x^2/16 + y^2/9 = 1  =>  9x^2 + 16y^2 - 144 = 0
        var deck = new IgesDeck();
        deck.RawEntity(104, "104,9.0,0.0,16.0,0.0,0.0,-144.0,0.0,4.0,0.0,-4.0,0.0;", form: 1);
        var result = IgesReader.Read(deck.ToText());

        var curve = Assert.Single(result.Curves);
        var ellipse = curve as Ellipse3d ?? (Ellipse3d)((CurveSegment)curve).Base;
        Assert.Equal(4.0, ellipse.SemiAxisX.Length, 9);
        Assert.Equal(3.0, ellipse.SemiAxisY.Length, 9);
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("form"));
    }

    [Fact]
    public void AConicArcWithPositiveDiscriminantImportsAsAHyperbola()
    {
        // x^2/4 - y^2/9 = 1  =>  9x^2 - 4y^2 - 36 = 0
        var deck = new IgesDeck();
        deck.RawEntity(104, "104,9.0,0.0,-4.0,0.0,0.0,-36.0,0.0,2.0,0.0,4.0,4.2426407;", form: 2);
        var hyperbola = Assert.IsType<Hyperbola3d>(Assert.Single(IgesReader.Read(deck.ToText()).Curves));

        Assert.Equal(2.0, hyperbola.SemiAxisX.Length, 6);
        Assert.Equal(3.0, hyperbola.SemiAxisY.Length, 6);
        // The domain runs between the two given endpoints' own parameters.
        AssertClose(new Vector3d(2, 0, 0), hyperbola.PointAt(0), 1e-6);
    }

    [Fact]
    public void AConicArcWithAZeroSquaredTermImportsAsAParabola()
    {
        // y^2 = 4x  =>  y^2 - 4x = 0, focal length 1, apex at the origin.
        var deck = new IgesDeck();
        deck.RawEntity(104, "104,0.0,0.0,1.0,-4.0,0.0,0.0,0.0,1.0,2.0,1.0,-2.0;", form: 3);
        var parabola = Assert.IsType<Parabola3d>(Assert.Single(IgesReader.Read(deck.ToText()).Curves));

        Assert.Equal(1.0, parabola.FocalLength, 9);
        AssertClose(Vector3d.Zero, parabola.Apex, 1e-9);
        // The apex axis points toward the focus, i.e. into +x where the parabola opens.
        Assert.True(parabola.XDirection.Dot(Vector3d.UnitX) > 0.99);
    }

    [Fact]
    public void AConicArcWhoseFormContradictsItsCoefficientsBelievesTheCoefficients()
    {
        // The ellipse above, mislabelled as a hyperbola — a real and common defect.
        var deck = new IgesDeck();
        deck.RawEntity(104, "104,9.0,0.0,16.0,0.0,0.0,-144.0,0.0,4.0,0.0,-4.0,0.0;", form: 2);
        var result = IgesReader.Read(deck.ToText());

        var curve = result.Curves[0];
        Assert.True(curve is Ellipse3d || ((CurveSegment)curve).Base is Ellipse3d);
        Assert.Contains(result.Diagnostics, d => d.Contains("form 2") && d.Contains("form 1"));
    }

    // ---- transformation matrices ----

    [Fact]
    public void A124TransformationIsAppliedToTheDefiningData()
    {
        var deck = new IgesDeck();
        // A quarter turn about +z followed by a translation of (10, 0, 0).
        int matrix = deck.RawEntity(124, "124,0.,-1.,0.,10.,1.,0.,0.,0.,0.,0.,1.,0.;");
        deck.RawEntity(110, "110,1.0,0.0,0.0,3.0,0.0,0.0;", transform: matrix);

        var line = Assert.IsType<Line3d>(Assert.Single(IgesReader.Read(deck.ToText()).Curves));
        // (1,0,0) -> (10,1,0) and (3,0,0) -> (10,3,0): rotated then translated, and the
        // result is still an exact Line3d rather than a wrapper.
        AssertClose(new Vector3d(10, 1, 0), line.Start);
        AssertClose(new Vector3d(10, 3, 0), line.End);
    }

    [Fact]
    public void ATransformedConicKeepsItsType()
    {
        // A rigid placement must not turn an Ellipse3d into a TransformedCurve wrapping
        // one: the tessellator and BrepQueries both branch on the type.
        var deck = new IgesDeck();
        int matrix = deck.RawEntity(124, "124,0.,-1.,0.,3.,1.,0.,0.,0.,0.,0.,1.,0.;");
        deck.RawEntity(104, "104,9.0,0.0,16.0,0.0,0.0,-144.0,0.0,4.0,0.0,-4.0,0.0;",
            form: 1, transform: matrix);

        var curve = Assert.Single(IgesReader.Read(deck.ToText()).Curves);
        var ellipse = curve as Ellipse3d ?? (Ellipse3d)((CurveSegment)curve).Base;
        AssertClose(new Vector3d(3, 0, 0), ellipse.Center, 1e-9);
        Assert.Equal(4.0, ellipse.SemiAxisX.Length, 9);
    }

    [Fact]
    public void ATransformedCircularArcStaysAnExactCircle()
    {
        var deck = new IgesDeck();
        int matrix = deck.RawEntity(124, "124,1.,0.,0.,0.,0.,0.,-1.,0.,0.,1.,0.,5.;");
        deck.RawEntity(100, "100,0.0,0.0,0.0,2.0,0.0,0.0,2.0;", transform: matrix);

        var segment = Assert.IsType<CurveSegment>(Assert.Single(IgesReader.Read(deck.ToText()).Curves));
        var circle = Assert.IsType<Circle3d>(segment.Base);
        Assert.Equal(2.0, circle.Radius, 12);
        AssertClose(new Vector3d(0, 0, 5), circle.Center);
    }

    // ---- surfaces ----

    [Fact]
    public void ABilinearBSplineSurfaceImportsWithTheCorrectControlGridOrientation()
    {
        // A 2x2 bilinear patch that is NOT symmetric under transposition, so a transposed
        // read (the load-bearing ordering bug in entity 128) fails rather than passing.
        var deck = new IgesDeck();
        deck.RawEntity(128,
            "128,1,1,1,1,0,0,1,0,"
            + "0.,0.,1.,1.,"                       // u knots
            + "0.,0.,1.,1.,"                       // v knots
            + "1.,1.,1.,1.,"                       // weights
            + "0.,0.,0.,"                          // (0,0)
            + "10.,0.,0.,"                         // (1,0) — u varies fastest
            + "0.,4.,0.,"                          // (0,1)
            + "10.,4.,7.,"                         // (1,1)
            + "0.,1.,0.,1.;");
        var surface = Assert.IsType<NurbsSurface>(
            Assert.Single(IgesReader.Read(deck.ToText()).Surfaces));

        Assert.Equal(1, surface.DegreeU);
        Assert.Equal(1, surface.DegreeV);
        AssertClose(new Vector3d(0, 0, 0), surface.PointAt(0, 0));
        AssertClose(new Vector3d(10, 0, 0), surface.PointAt(1, 0));
        AssertClose(new Vector3d(0, 4, 0), surface.PointAt(0, 1));
        AssertClose(new Vector3d(10, 4, 7), surface.PointAt(1, 1));
    }

    [Fact]
    public void ASurfaceOfRevolutionBecomesARevolvedSurface()
    {
        var deck = new IgesDeck();
        int axis = deck.RawEntity(110, "110,0.,0.,0.,0.,0.,1.;");           // the z axis
        int generator = deck.RawEntity(110, "110,4.,0.,0.,4.,0.,10.;");     // a vertical line at r = 4
        deck.RawEntity(120, $"120,{axis},{generator},0.0,{Fmt(Math.PI)};");

        var surface = Assert.IsType<RevolvedSurface>(
            Assert.Single(IgesReader.Read(deck.ToText()).Surfaces));
        Assert.Equal(Math.PI, surface.Angle, 9);
        AssertClose(new Vector3d(0, 0, 1), surface.AxisDirection);
        // Half a turn of a radius-4 cylinder: u = pi lands on the far side.
        AssertClose(new Vector3d(-4, 0, 0), surface.PointAt(Math.PI, 0), 1e-9);
    }

    [Fact]
    public void ATabulatedCylinderBecomesAnExtrusion()
    {
        var deck = new IgesDeck();
        int directrix = deck.RawEntity(100, "100,0.0,0.0,0.0,6.0,0.0,6.0,0.0;");  // full circle r = 6
        deck.RawEntity(122, $"122,{directrix},6.0,0.0,12.0;");

        var surface = Assert.IsType<ExtrudedSurface>(
            Assert.Single(IgesReader.Read(deck.ToText()).Surfaces));
        // The generatrix runs from the directrix start (6,0,0) to the terminate point.
        AssertClose(new Vector3d(0, 0, 12), surface.Direction);
    }

    [Fact]
    public void ARuledSurfaceBecomesATwoSectionLoft()
    {
        var deck = new IgesDeck();
        int lower = deck.RawEntity(110, "110,0.,0.,0.,10.,0.,0.;");
        int upper = deck.RawEntity(110, "110,0.,0.,5.,10.,0.,5.;");
        deck.RawEntity(118, $"118,{lower},{upper},0,0;");

        var surface = Assert.IsType<LoftedSurface>(
            Assert.Single(IgesReader.Read(deck.ToText()).Surfaces));
        Assert.Equal(2, surface.Sections.Count);
        AssertClose(new Vector3d(5, 0, 2.5), surface.PointAt(0.5, 0.5), 1e-9);
    }

    [Fact]
    public void APlaneEntityBecomesAPlaneSurface()
    {
        var deck = new IgesDeck();
        deck.RawEntity(108, "108,0.,0.,1.,7.,0,0.,0.,7.,0.;");
        var plane = Assert.IsType<PlaneSurface>(
            Assert.Single(IgesReader.Read(deck.ToText()).Surfaces));
        AssertClose(new Vector3d(0, 0, 7), plane.Origin);
        Assert.Equal(1.0, Math.Abs(plane.Normal.Dot(Vector3d.UnitZ)), 12);
    }

    // ---- trimmed surfaces ----

    [Fact]
    public void ATrimmedSurfaceBecomesAFaceWithItsOuterLoopFirst()
    {
        var deck = TrimmedPatchDeck(withHole: true);
        var result = IgesReader.Read(deck.ToText());

        var face = Assert.Single(result.Faces);
        Assert.IsType<NurbsSurface>(face.Surface);
        Assert.Equal(2, face.Loops.Count);
        // The outer loop is Loops[0] because the entity DECLARED it (N1/PTO), not because
        // of its area — the read-the-declaration rule.
        Assert.Equal(4, face.OuterLoop.Coedges.Count);
        Assert.Equal(4, face.Loops[1].Coedges.Count);

        Assert.NotNull(result.Solid);
        Assert.True(result.IsFaceSoup);
        Assert.Contains(result.Diagnostics, d => d.Contains("ShapeHealing.Heal"));
    }

    [Fact]
    public void ATrimmedSurfacesCurvesAreNotAlsoReportedAsLooseGeometry()
    {
        // A curve consumed by a boundary must not appear twice; otherwise a consumer
        // drawing the wireframe would double every trim edge.
        var result = IgesReader.Read(TrimmedPatchDeck(withHole: false).ToText());
        Assert.Empty(result.Curves);
        Assert.Empty(result.Surfaces);
        Assert.Single(result.Faces);
    }

    [Fact]
    public void ALoopsConsecutivePiecesShareTheirJointVertex()
    {
        // The loop must CHAIN — a face whose coedges do not meet end-to-start is not a
        // trim, it is four unrelated curves.
        var face = Assert.Single(IgesReader.Read(TrimmedPatchDeck(withHole: false).ToText()).Faces);
        var coedges = face.OuterLoop.Coedges;
        for (int i = 0; i < coedges.Count; i++)
        {
            Assert.Same(
                coedges[i].EndVertex,
                coedges[(i + 1) % coedges.Count].StartVertex);
        }
    }

    [Fact]
    public void TheImportedShellIsHonestlyAFaceSoup()
    {
        // IGES has no shared topology, so an imported shell's edges are used ONCE. Saying
        // so is the whole contract: a consumer that skipped ShapeHealing would otherwise
        // discover it as a tessellation crack three stages later.
        var result = IgesReader.Read(TrimmedPatchDeck(withHole: false).ToText());
        Assert.NotNull(result.Solid);
        var solid = result.Solid!;
        Assert.True(result.IsFaceSoup);

        var error = Assert.Throws<InvalidOperationException>(solid.Validate);
        Assert.Contains("manifold", error.Message);

        // And healing is the documented next step, so it must actually see the problem.
        var report = ShapeHealing.Analyze(solid);
        Assert.True(report.OpenLoopsBefore > 0 || !report.IsManifold);
    }

    [Fact]
    public void ABoundaryWithOnlyAParameterSpaceCurveIsRefusedByName()
    {
        var deck = TrimmedPatchDeck(withHole: false, modelSpaceBoundary: false);
        var result = IgesReader.Read(deck.ToText());
        Assert.Empty(result.Faces);
        Assert.Contains(result.Diagnostics, d => d.Contains("model-space"));
    }

    // ---- unknown entities ----

    [Fact]
    public void AnUnsupportedEntityTypeIsSkippedOnceNamingTheFirstOffender()
    {
        var deck = new IgesDeck();
        deck.RawEntity(212, "212,0,1,1.0,1.0,0,0.0,0.0,0.0,0.0,1H?;"); // general note
        deck.RawEntity(212, "212,0,1,1.0,1.0,0,0.0,0.0,0.0,0.0,1H!;");
        deck.Line((0, 0, 0), (1, 0, 0));

        var result = IgesReader.Read(deck.ToText());
        Assert.Single(result.Curves);
        // Deduped: two offenders of one type produce ONE diagnostic naming the first.
        var skips = result.Diagnostics.Where(d => d.Contains("unsupported")).ToList();
        Assert.Single(skips);
        Assert.Contains("212", skips[0]);
    }

    [Fact]
    public void AFileWithNoGeometryReportsEmptyRatherThanThrowing()
    {
        // A header-only file is legal IGES; an empty Directory Entry section is the
        // reader's "nothing here" diagnostic, not a structural failure.
        var result = IgesReader.Read(new IgesDeck().ToText());
        Assert.Null(result.Solid);
        Assert.Empty(result.Curves);
        Assert.Empty(result.Faces);
        Assert.Contains(result.Diagnostics, d => d.Contains("No supported geometry"));
    }

    [Fact]
    public void ABadEntityCostsOnlyItself()
    {
        var deck = new IgesDeck();
        deck.RawEntity(100, "100,0.0,0.0,0.0,0.0,0.0,0.0,0.0;"); // zero radius
        deck.Line((0, 0, 0), (4, 0, 0));

        var result = IgesReader.Read(deck.ToText());
        Assert.Single(result.Curves);
        Assert.Contains(result.Diagnostics, d => d.Contains("zero radius"));
    }

    // ---- helpers ----

    private static void AssertClose(in Vector3d expected, in Vector3d actual, double tolerance = 1e-12)
    {
        Assert.True(
            expected.DistanceTo(actual) <= tolerance,
            $"expected {expected}, got {actual} (distance {expected.DistanceTo(actual):E3})");
    }

    private static string Fmt(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A 128 patch trimmed by a four-line outer boundary and, optionally, a
    /// four-line inner one — the shape of every real surfacing file.</summary>
    private static IgesDeck TrimmedPatchDeck(bool withHole, bool modelSpaceBoundary = true)
    {
        var deck = new IgesDeck();
        int surface = deck.RawEntity(128,
            "128,1,1,1,1,0,0,1,0,0.,0.,1.,1.,0.,0.,1.,1.,1.,1.,1.,1.,"
            + "0.,0.,0.,20.,0.,0.,0.,20.,0.,20.,20.,0.,0.,1.,0.,1.;");

        int outer = deck.RawEntity(102, Composite(deck, Rectangle((2, 2), (18, 18), 0)));
        int outerBoundary = modelSpaceBoundary
            ? deck.RawEntity(142, $"142,0,{surface},0,{outer},3;")
            : deck.RawEntity(142, $"142,0,{surface},{outer},0,1;");

        if (!withHole)
        {
            deck.RawEntity(144, $"144,{surface},1,0,{outerBoundary};");
            return deck;
        }

        int inner = deck.RawEntity(102, Composite(deck, Rectangle((7, 7), (13, 13), 0)));
        int innerBoundary = deck.RawEntity(142, $"142,0,{surface},0,{inner},3;");
        deck.RawEntity(144, $"144,{surface},1,1,{outerBoundary},{innerBoundary};");
        return deck;
    }

    private static (Vector3d Start, Vector3d End)[] Rectangle(
        (double X, double Y) low, (double X, double Y) high, double z)
    {
        var a = new Vector3d(low.X, low.Y, z);
        var b = new Vector3d(high.X, low.Y, z);
        var c = new Vector3d(high.X, high.Y, z);
        var d = new Vector3d(low.X, high.Y, z);
        return [(a, b), (b, c), (c, d), (d, a)];
    }

    private static string Composite(IgesDeck deck, (Vector3d Start, Vector3d End)[] segments)
    {
        var pointers = segments.Select(s => deck.Line(s.Start, s.End)).ToList();
        return $"102,{pointers.Count}," + string.Join(",", pointers) + ";";
    }
}

/// <summary>
/// Builds an IGES card deck column by column. Fixture files are deliberately NOT committed
/// (the repo commits none, for any format) — a deck built in the test is readable, and the
/// column arithmetic that makes IGES fiddly lives in exactly one place.
/// </summary>
internal sealed class IgesDeck
{
    private readonly List<(int Type, string Parameters, int Form, int Transform)> _entities = [];

    public char ParameterDelimiter { get; init; } = ',';
    public char RecordDelimiter { get; init; } = ';';
    public int UnitFlag { get; init; } = 2;
    public string Author { get; init; } = "EngrCAD";

    /// <summary>Adds an entity from raw parameter text (already delimited with the DEFAULT
    /// delimiters; they are substituted on the way out).</summary>
    public int RawEntity(int type, string parameters, int form = 0, int transform = 0)
    {
        _entities.Add((type, parameters, form, transform));
        return 1 + 2 * (_entities.Count - 1); // the DE pointer: the first card's sequence number
    }

    public int Line(in Vector3d start, in Vector3d end) => RawEntity(110,
        $"110,{N(start.X)},{N(start.Y)},{N(start.Z)},{N(end.X)},{N(end.Y)},{N(end.Z)};");

    public IgesDeck WithLine(in Vector3d start, in Vector3d end)
    {
        Line(start, end);
        return this;
    }

    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public string ToText()
    {
        var text = new StringBuilder();
        int sequence = 1;

        void Card(string data, char section, ref int number) =>
            text.Append(data.PadRight(72)).Append(section)
                .Append(number++.ToString(CultureInfo.InvariantCulture).PadLeft(7)).Append('\n');

        int s = 1;
        Card("Written by IgesDeck for tests.", 'S', ref s);

        // Global: the delimiters are themselves parameters 1 and 2, in Hollerith form.
        string global = string.Join(ParameterDelimiter,
            $"1H{ParameterDelimiter}", $"1H{RecordDelimiter}", "7Htestsrc", "8Htest.igs",
            "7HEngrCAD", "5H0.1.0", "32", "38", "6", "308", "15", "7Htestrcv",
            "1.0", UnitFlag.ToString(CultureInfo.InvariantCulture), "2HMM",
            "1", "0.01", "13H250101.000000", "1E-9", "1000.0",
            $"{Author.Length}H{Author}", "7HEngrCAD", "11", "0") + RecordDelimiter;
        int g = 1;
        foreach (var chunk in Chunks(global, 72))
            Card(chunk, 'G', ref g);

        // Directory entries: two cards per entity, nine 8-column fields each.
        int d = 1;
        int parameterLine = 1;
        var parameterText = new List<(int Owner, string Text)>();
        for (int i = 0; i < _entities.Count; i++)
        {
            var (type, parameters, form, transform) = _entities[i];
            string body = Substitute(parameters);
            var chunks = Chunks(body, 64).ToList();
            Card(
                F(type) + F(parameterLine) + F(0) + F(0) + F(0) + F(0) + F(transform) + F(0) + F(0),
                'D', ref d);
            Card(
                F(type) + F(0) + F(0) + F(chunks.Count) + F(form) + F(0) + F(0)
                    + "".PadLeft(8) + F(0),
                'D', ref d);
            parameterText.Add((1 + 2 * i, body));
            parameterLine += chunks.Count;
        }

        int p = 1;
        foreach (var (owner, body) in parameterText)
        {
            foreach (var chunk in Chunks(body, 64))
            {
                text.Append(chunk.PadRight(64))
                    .Append(owner.ToString(CultureInfo.InvariantCulture).PadLeft(8))
                    .Append('P')
                    .Append(p++.ToString(CultureInfo.InvariantCulture).PadLeft(7))
                    .Append('\n');
            }
        }

        text.Append($"S{(s - 1),6}G{(g - 1),6}D{(d - 1),6}P{(p - 1),6}".PadRight(72))
            .Append('T').Append("1".PadLeft(7)).Append('\n');
        _ = sequence;
        return text.ToString();
    }

    private string Substitute(string parameters) => ParameterDelimiter == ',' && RecordDelimiter == ';'
        ? parameters
        : parameters.Replace(',', ParameterDelimiter).Replace(';', RecordDelimiter);

    private static string F(int value) => value.ToString(CultureInfo.InvariantCulture).PadLeft(8);

    private static IEnumerable<string> Chunks(string text, int size)
    {
        if (text.Length == 0)
        {
            yield return "";
            yield break;
        }
        for (int i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }
}
