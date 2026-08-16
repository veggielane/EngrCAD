using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// G2/G3 arc fitting: the offset machinery places its corner vertices INSCRIBED on one
/// circle about the corner centre, so the circle fitted through them RECOVERS the arc the
/// chording lost — verified by the fitted I/J radius, by the fitted toolpath never leaving
/// the source polyline by more than the sagitta cap (the no-gouge form, which is what
/// caught the mirror-symmetry phantom: four exactly-concyclic points spanning a straight
/// side), and by the decoder's own arc expansion round-tripping hand-written half circles.
/// </summary>
public class CncArcFittingTests
{
    private static MillOperation RoundedProfile()
    {
        var region = Sketch.RoundedRectangle(40, 24, 6).ToRegions()[0];
        return CncMill.Profile(region, new MillTool(6), depth: 2, ProfileSide.Outside);
    }

    [Fact]
    public void TheFittedArcs_AreTheFourCorners_AtTheCompensatedRadius()
    {
        // A 40x24 plate with r6 corners profiled outside by a Ø6 tool: the tool-centre
        // corners are arcs of radius 6 + 3 = 9, and ONLY the corners are arcs — the
        // straight sides must stay line moves (the sagitta cap's whole job).
        string gcode = CncGcodeWriter.Write([RoundedProfile()], arcFitting: true);
        var arcLines = gcode.Split('\n')
            .Where(l => l.StartsWith("G2 ") || l.StartsWith("G3 ")).ToList();
        Assert.Equal(4, arcLines.Count);

        foreach (var line in arcLines)
        {
            double i = Word(line, 'I'), j = Word(line, 'J');
            Assert.Equal(9.0, Math.Sqrt(i * i + j * j), 3);
        }

        static double Word(string line, char w)
        {
            int at = line.IndexOf(' ' + w.ToString(), StringComparison.Ordinal);
            Assert.True(at >= 0, $"missing {w} in '{line}'");
            int start = at + 2, end = start;
            while (end < line.Length && line[end] != ' ')
                end++;
            return double.Parse(line[start..end],
                System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    [Fact]
    public void TheFittedToolpath_NeverLeavesTheSourcePolyline_AndMatchesTheClosedForm()
    {
        var op = RoundedProfile();
        var chorded = GcodeReader.Read(CncGcodeWriter.Write([op]));
        var fitted = GcodeReader.Read(CncGcodeWriter.Write([op], arcFitting: true));

        // No-gouge: every decoded point of the arc-fitted program lies within the sagitta
        // cap (1e-3) plus the coordinate quantum's own slop of the chorded polyline. This
        // is the assertion that caught the mirror-symmetry phantom, whose 675 mm arc
        // bulged 0.027 mm across a straight side while passing the on-circle test —
        // because its four points genuinely WERE concyclic.
        var polyline = chorded.Moves.Where(m => !m.Rapid)
            .Select(m => new Vector2d(m.To.X, m.To.Y)).ToList();
        foreach (var move in fitted.Moves.Where(m => !m.Rapid))
        {
            var p = new Vector2d(move.To.X, move.To.Y);
            double d = polyline.Select((_, k) => k == 0 ? double.MaxValue
                : DistanceToSegment(p, polyline[k - 1], polyline[k])).Min();
            Assert.True(d < 2e-3, $"fitted point ({p.X:0.####},{p.Y:0.####}) sits {d:0.####} off the chorded path");
        }

        // And the decoded cut length agrees with the profile's own closed form —
        // 2·(28 + 12) straight plus one full compensated corner circle — to the coarser
        // of the two samplings' chord deficit.
        double length = fitted.Moves.Where(m => !m.Rapid).Sum(m => m.XyLength);
        Assert.Equal(2 * (28.0 + 12.0) + 2 * Math.PI * 9.0, length, 0.05);

        static double DistanceToSegment(Vector2d p, Vector2d a, Vector2d b)
        {
            var ab = b - a;
            double len2 = ab.Dot(ab);
            double t = len2 <= 0 ? 0 : Math.Clamp((p - a).Dot(ab) / len2, 0, 1);
            return (p - (a + ab * t)).Length;
        }
    }

    [Fact]
    public void TheDecoder_ExpandsArcs_AndRefusesTheAmbiguousFormsByName()
    {
        // A hand-written half circle, radius 10, both directions: the decoded cut length
        // is the approach (10) plus pi*10 to the 5-degree expansion's own chord deficit.
        var cw = GcodeReader.Read("G21\nG90\nG1 X10 Y0 F100\nG2 X-10 Y0 I-10 J0\n");
        double total = cw.Moves.Where(m => !m.Rapid).Sum(m => m.XyLength);
        Assert.Equal(10 + Math.PI * 10, total, 0.01);

        var ccw = GcodeReader.Read("G21\nG90\nG1 X10 Y0 F100\nG3 X-10 Y0 I-10 J0\n");
        // CCW passes through (0, +10); CW through (0, -10): the direction is real.
        Assert.Contains(ccw.Moves, m => m.To.Y > 9);
        Assert.Contains(cw.Moves, m => m.To.Y < -9);

        Assert.Contains("R-form", Assert.Throws<FormatException>(() =>
            GcodeReader.Read("G2 X10 Y0 R5\n")).Message);
        Assert.Contains("centre", Assert.Throws<FormatException>(() =>
            GcodeReader.Read("G2 X10 Y0\n")).Message);
        Assert.Contains("radius", Assert.Throws<FormatException>(() =>
            GcodeReader.Read("G1 X10 Y0\nG2 X-10 Y3 I-10 J0\n")).Message);
    }

    [Fact]
    public void ArcFittingOff_IsByteIdentical()
    {
        var op = RoundedProfile();
        Assert.Equal(
            CncGcodeWriter.Write([op]),
            CncGcodeWriter.Write([op], 5, false, false));
    }
}
