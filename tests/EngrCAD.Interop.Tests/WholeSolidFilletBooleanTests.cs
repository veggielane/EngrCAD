using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Booleans on whole-solid fillets (<see cref="Filleting.FilletAllEdges"/> output).
/// One configuration works and is locked as a regression test; the other fails loudly
/// with a DIAGNOSED cause, locked so a future fix flips the assertion deliberately.
/// </summary>
public class WholeSolidFilletBooleanTests
{
    private static BrepSolid RoundedBox() =>
        Filleting.FilletAllEdges(SolidFactory.MakeBox(new Aabb((0, 0, 0), (20, 14, 8))), 2);

    [Fact]
    public void CenterDrill_ThroughTheCaps_IsExact()
    {
        // The tool crosses only the shrunk top and bottom PLANES (interior circles →
        // hole loops + wrap-split tool wall), so the whole pipeline is the drilled-
        // plate one and the volume is closed form: Steiner minus the bore.
        var result = BrepBoolean.Difference(
            RoundedBox(), Shape.Cylinder(3, 30).Translate((10, 7, 4)).ToBrep());
        result.Validate();
        var mesh = BRepTessellator.Tessellate(result, 96, 48);
        mesh.Validate();
        Assert.True(mesh.IsClosed);

        double r = 2, a = 20 - 2 * r, b = 14 - 2 * r, c = 8 - 2 * r;
        double steiner = a * b * c
            + r * 2 * (a * b + a * c + b * c)
            + Math.PI * r * r * (a + b + c)
            + 4.0 / 3 * Math.PI * r * r * r;
        double expected = steiner - Math.PI * 9 * 8;
        Assert.True(Math.Abs(mesh.Volume() - expected) / expected < 1e-3,
            $"volume {mesh.Volume()} vs {expected}");
    }

    [Fact]
    public void BandCrossingDrill_StillRefusesLoudly()
    {
        // DIAGNOSED (see todo.md): the tool's intersection with an edge band is a
        // closed tracer loop that leaves the band's quarter domain and closes on the
        // extended carrier. The on-band runs now SEED boundary crossings (the
        // extrapolated pseudo-samples reach the domain edge), but RefineCrossing
        // demands a true 3D intersection to 1e-11 and a tracer polyline is CHORDAL —
        // mid-chord it sits a sagitta (~1e-4) off the exact tangency edge, so the two
        // curves are skew, refinement rejects every seed, the band never splits, and
        // the whole band mis-classifies while its planar neighbours split — cracking
        // the result along the full tangency edges. The fix needs exact edge-vs-tool-
        // surface crossings plus crossing-snapped polyline segment ends on BOTH solids;
        // until then the boolean refuses loudly rather than emitting the crack.
        var error = Assert.Throws<BrepBooleanException>(() => BrepBoolean.Difference(
            RoundedBox(), Shape.Cylinder(3, 30).Translate((10, 1.2, 4)).ToBrep()));
        Assert.Contains("unclosed solid", error.Message);
    }
}
