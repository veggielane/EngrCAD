using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// NATIVE arcs carried end to end from the exact curved 2D tier: a `MillPass` whose segments
/// ARE arcs, so <see cref="CncGcodeWriter"/> transcribes a `G2`/`G3` from what the geometry
/// stated instead of recovering a circle from chords.
///
/// <para><b>The assertion with teeth is the exact PERIMETER.</b> A 40x24 plate with r6 corners
/// profiled outside by a Ø6 tool has a tool-centre outline of exactly `2(28 + 12) + 2*pi*9`,
/// and the decoded arc-native program reaches it at the file's own coordinate quantum — no
/// chord deficit at all. The chorded route cannot: its arcs are inscribed polygons, so it is
/// short by a deficit no tessellation density removes. The comparison is against the CLOSED
/// FORM rather than against the chorded file, because the decoder expands an arc at 5 degrees,
/// which is coarser than the source chords and reads a perfect arc as SHORTER — the recorded
/// lesson that made `GcodeMove.PathLength` the right measure for a program carrying arcs.</para>
///
/// <para>That one number is also the HANDEDNESS check, which is why no separate test states
/// one: a `G2` written where a `G3` was meant sweeps the long way round the same circle, so a
/// quarter corner would decode as three quarters and the perimeter would miss by whole
/// radians rather than by round-off.</para>
/// </summary>
public class CncArcNativeTests
{
    private const double PlateWidth = 40;
    private const double PlateHeight = 24;
    private const double CornerRadius = 6;
    private const double ToolDiameter = 6;

    /// <summary>The tool-centre outline of the outside profile: the straights shortened by two
    /// corner radii, the corners at the compensated radius r + D/2.</summary>
    private static readonly double ExactPerimeter =
        2 * ((PlateWidth - 2 * CornerRadius) + (PlateHeight - 2 * CornerRadius))
        + 2 * Math.PI * (CornerRadius + ToolDiameter / 2);

    private static CurvedRegion2d ExactPlate() =>
        Sketch.RoundedRectangle(PlateWidth, PlateHeight, CornerRadius).ToCurvedRegions()[0];

    private static Region2d FlatPlate() =>
        Sketch.RoundedRectangle(PlateWidth, PlateHeight, CornerRadius).ToRegions()[0];

    private static MillOperation ExactProfile() =>
        CncMill.Profile(ExactPlate(), new MillTool(ToolDiameter), depth: 2, ProfileSide.Outside);

    private static MillOperation FlatProfile() =>
        CncMill.Profile(FlatPlate(), new MillTool(ToolDiameter), depth: 2, ProfileSide.Outside);

    private static double PathLength(string gcode) =>
        GcodeReader.Read(gcode).Moves.Where(m => !m.Rapid).Sum(m => m.PathLength);

    private static double ChordLength(string gcode) =>
        GcodeReader.Read(gcode).Moves.Where(m => !m.Rapid).Sum(m => m.XyLength);

    [Fact]
    public void TheArcNativeProgram_DecodesToTheExactClosedFormPerimeter()
    {
        double measured = PathLength(CncGcodeWriter.Write([ExactProfile()]));
        Assert.Equal(ExactPerimeter, measured, 9);
    }

    [Fact]
    public void TheChordedRoute_IsShortOfThatPerimeter_AtAnyDensity()
    {
        // The mutation that gives the headline its teeth: the same plate profiled through the
        // FLATTENED tier reads short, and refining the flattening only shrinks the deficit
        // toward a floor it never reaches, because an inscribed polygon is shorter than its arc.
        double coarse = PathLength(CncGcodeWriter.Write([CncMill.Profile(
            Sketch.RoundedRectangle(PlateWidth, PlateHeight, CornerRadius).ToRegions(1e-2)[0],
            new MillTool(ToolDiameter), depth: 2, ProfileSide.Outside)]));
        double fine = PathLength(CncGcodeWriter.Write([FlatProfile()]));

        Assert.True(coarse < ExactPerimeter, $"coarse {coarse:0.######} vs {ExactPerimeter:0.######}");
        Assert.True(fine < ExactPerimeter, $"fine {fine:0.######} vs {ExactPerimeter:0.######}");
        Assert.True(fine > coarse, "a finer flattening should close some of the deficit");
    }

    [Fact]
    public void TheEmittedArcs_AreTheCompensatedCorners_SummingToAWholeTurn()
    {
        string gcode = CncGcodeWriter.Write([ExactProfile()]);
        var arcs = gcode.Split('\n')
            .Where(l => l.StartsWith("G2 ") || l.StartsWith("G3 ")).ToList();
        Assert.NotEmpty(arcs);

        // Every arc rides the compensated radius, and together they turn exactly once — the
        // four corners of a rounded rectangle, however the offset chose to split them.
        double sweep = 0;
        foreach (var line in arcs)
        {
            double i = Word(line, 'I'), j = Word(line, 'J');
            Assert.Equal(CornerRadius + ToolDiameter / 2, Math.Sqrt(i * i + j * j), 3);
            sweep += ArcSweepOf(gcode, line);
        }
        Assert.Equal(2 * Math.PI, Math.Abs(sweep), 4);
    }

    [Fact]
    public void APolygonalConstruction_CarriesNoArcs_AndEmitsNoneWithoutTheFitter()
    {
        var op = FlatProfile();
        Assert.All(op.Passes, p => Assert.Null(p.Arcs));

        string gcode = CncGcodeWriter.Write([op]);
        Assert.DoesNotContain("G2 ", gcode);
        Assert.DoesNotContain("G3 ", gcode);
    }

    [Fact]
    public void TheArcNativePolyline_IsTheFlattenedPath_SoEveryPolylineConsumerIsUntouched()
    {
        var op = ExactProfile();
        var pass = Assert.Single(op.Passes);

        // The points are the exact offset outline flattened at the stated chord tolerance, so
        // the pass's own CutLength — the number every polyline consumer reads — is short of the
        // exact perimeter by that flattening's inscribed deficit and by nothing else.
        Assert.True(pass.CutLength < ExactPerimeter);
        Assert.True(ExactPerimeter - pass.CutLength < 1e-2,
            $"the chord deficit should be the flattening's own: "
            + $"{ExactPerimeter - pass.CutLength:0.######}");

        // The DECODED chord length is shorter still, and that is the decoder rather than the
        // path: it expands each stated arc at 5 degrees, coarser than the 1e-3 source chords.
        // Which is exactly why PathLength, not XyLength, is what an arc program is judged by.
        double decodedChords = ChordLength(CncGcodeWriter.Write([op]));
        Assert.True(decodedChords < pass.CutLength,
            $"decoded {decodedChords:0.######} vs source {pass.CutLength:0.######}");

        // And every carried span indexes real points of that polyline.
        foreach (var arc in pass.Arcs!)
        {
            Assert.InRange(arc.Start, 0, pass.Points.Count - 1);
            Assert.InRange(arc.End, arc.Start + 1, pass.Points.Count);
            for (int k = arc.Start; k <= arc.End; k++)
            {
                var p = pass.Points[k % pass.Points.Count];
                double r = new Vector2d(p.X - arc.Center.X, p.Y - arc.Center.Y).Length;
                Assert.Equal(CornerRadius + ToolDiameter / 2, r, 9);
            }
        }
    }

    [Fact]
    public void AnArcNativeProfile_NeverGouges_AndItsStockSimulationStillRuns()
    {
        var region = ExactPlate();
        var flat = region.ToRegion(CncMill.ArcChordTolerance);
        var op = ExactProfile();

        // The no-gouge claim is unchanged and point by point: an OUTSIDE profile keeps every
        // pass point at least a tool radius outside the outline.
        foreach (var pass in op.Passes)
        foreach (var p in pass.Points)
        {
            double d = CncMill.DistanceToBoundary(flat, new Vector2d(p.X, p.Y));
            Assert.True(d > ToolDiameter / 2 - 1e-6, $"gouge: {d:0.######}");
        }

        // And the stock simulation — a pure polyline consumer — reads it with nothing new.
        var stock = Shape.Box(80, 60, 3).Translate(-40, -30, -3);
        var states = CncStock.Simulate(stock, [op], states: 2);
        Assert.Equal(2, states.Count);
    }

    [Fact]
    public void AnArcNativePocket_CarriesArcs_ClearsWithoutGouging_AndIsDeterministic()
    {
        var region = ExactPlate();
        var tool = new MillTool(4, StepDown: 3);
        var op = CncMill.Pocket(region, tool, depth: 3);
        var flat = region.ToRegion(CncMill.ArcChordTolerance);

        Assert.Contains(op.Passes, p => p.Arcs is { Count: > 0 });
        // The probe boundary is the INSCRIBED flattening, so at an arc it lies inside the true
        // outline and under-reports an interior point's clearance by at most the chord
        // tolerance — the measurement's own bound, stated rather than absorbed into a constant.
        foreach (var pass in op.Passes)
        foreach (var p in pass.Points)
        {
            double d = CncMill.DistanceToBoundary(flat, new Vector2d(p.X, p.Y));
            Assert.True(d > tool.Radius - CncMill.ArcChordTolerance, $"gouge: {d:0.######}");
        }

        string a = CncGcodeWriter.Write([op]);
        string b = CncGcodeWriter.Write([CncMill.Pocket(ExactPlate(), tool, depth: 3)]);
        Assert.Equal(a, b);
        Assert.Contains("G2 ", a);
    }

    [Fact]
    public void LeadArcsShiftTheSpans_SoTheProgramStillStatesItsCorners()
    {
        var op = CncMill.Profile(
            ExactPlate(), new MillTool(ToolDiameter), depth: 2, ProfileSide.Outside,
            leadRadius: 2);
        var pass = Assert.Single(op.Passes);
        Assert.False(pass.IsClosed);

        foreach (var arc in pass.Arcs!)
        {
            for (int k = arc.Start; k <= arc.End; k++)
            {
                var p = pass.Points[k];
                double r = new Vector2d(p.X - arc.Center.X, p.Y - arc.Center.Y).Length;
                Assert.Equal(CornerRadius + ToolDiameter / 2, r, 9);
            }
        }
        // An OUTSIDE climb profile walks its outer loop counter-clockwise, so its corners are
        // G3 — which is the direction rule showing through rather than a detail of the lead.
        string gcode = CncGcodeWriter.Write([op]);
        Assert.Equal(
            pass.Arcs!.Count,
            gcode.Split('\n').Count(l => l.StartsWith("G2 ") || l.StartsWith("G3 ")));
        Assert.Contains("G3 ", gcode);
    }

    private static double Word(string line, char w)
    {
        int at = line.IndexOf(' ' + w.ToString(), StringComparison.Ordinal);
        Assert.True(at >= 0, $"missing {w} in '{line}'");
        int start = at + 2, end = start;
        while (end < line.Length && line[end] != ' ')
            end++;
        return double.Parse(line[start..end], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The signed sweep the decoder reads for one arc line, summed off its own
    /// expansion so the measurement comes from the file rather than from the source.</summary>
    private static double ArcSweepOf(string gcode, string arcLine)
    {
        var lines = gcode.Split('\n').ToList();
        int index = lines.IndexOf(arcLine);
        Assert.True(index > 0);
        var prefix = string.Join('\n', lines.Take(index + 1));
        var moves = GcodeReader.Read(prefix).Moves;
        double sweep = 0;
        for (int i = moves.Count - 1; i >= 0; i--)
        {
            if (moves[i].ArcRadius <= 0)
                break;
            sweep += moves[i].ArcSweep;
        }
        return sweep;
    }
}
