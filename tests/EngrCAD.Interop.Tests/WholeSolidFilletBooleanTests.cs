using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Booleans on whole-solid fillets (<see cref="Filleting.FilletAllEdges"/> output). The tool
/// crossing a fillet BAND is the interesting case: its intersection with the band is a marching
/// tracer polyline, and the tracer breaks its step only AFTER the corrector leaves the domain,
/// so the curve always stopped up to one march step short of the band's tangency lines. It
/// therefore crossed no boundary at all, the band was whole-classified, and the result cracked
/// along entire tangency edges (measured: the four band curves ended 5.5e-5 and 1.1e-2 from the
/// two tangency lines and produced ZERO crossings).
///
/// <para>Two fixes, both structural. <c>BrepBoolean</c> SNAPS a traced polyline's ends onto the
/// exact solution of E(t) = S(u, v) — the boundary edge against the other solid's carrier — once,
/// before either side splits, so both solids get the identical endpoint. And the face splitter's
/// "is this stretch inside the face?" probe now takes an exact SAMPLE parameter instead of the
/// arithmetic midpoint, because a polyline is on its surface only at its vertices and a mid-chord
/// point sits a sagitta (5e-3 here) off it — the projection failed and the stretch was silently
/// discarded.</para>
/// </summary>
public class WholeSolidFilletBooleanTests
{
    private const double Rx = 20, Ry = 14, Rz = 8, R = 2;

    private static BrepSolid RoundedBox() =>
        Filleting.FilletAllEdges(SolidFactory.MakeBox(new Aabb((0, 0, 0), (Rx, Ry, Rz))), R);

    /// <summary>Steiner's formula for the opening: the eroded box grown by r.</summary>
    private static double SteinerVolume()
    {
        double a = Rx - 2 * R, b = Ry - 2 * R, c = Rz - 2 * R;
        return a * b * c
            + R * 2 * (a * b + a * c + b * c)
            + Math.PI * R * R * (a + b + c)
            + 4.0 / 3 * Math.PI * R * R * R;
    }

    private static HalfEdgeMesh Meshed(BrepSolid solid, int segments = 96)
    {
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid, segments, segments / 2);
        mesh.Validate();
        Assert.True(mesh.IsClosed, "result must tessellate closed");
        return mesh;
    }

    [Fact]
    public void CenterDrill_ThroughTheCaps_IsExact()
    {
        // The tool crosses only the shrunk top and bottom PLANES (interior circles →
        // hole loops + wrap-split tool wall), so the whole pipeline is the drilled-
        // plate one and the volume is closed form: Steiner minus the bore.
        var mesh = Meshed(BrepBoolean.Difference(
            RoundedBox(), Shape.Cylinder(3, 30).Translate((10, 7, 4)).ToBrep()));

        double expected = SteinerVolume() - Math.PI * 9 * 8;
        Assert.True(Math.Abs(mesh.Volume() - expected) / expected < 1e-3,
            $"volume {mesh.Volume()} vs {expected}");
    }

    private static BrepSolid Tool(double radius, in Vector3d at, int axis)
    {
        var cylinder = Shape.Cylinder(radius, 40);
        cylinder = axis switch
        {
            1 => cylinder.Rotate(Vector3d.UnitX, Math.PI / 2),
            2 => cylinder.Rotate(Vector3d.UnitY, Math.PI / 2),
            _ => cylinder,
        };
        return cylinder.Translate(at).ToBrep();
    }

    /// <summary>
    /// The band case. There is no closed form for it — the tool leaves through the front or
    /// bottom face as well as the caps — so it is checked by <b>inclusion–exclusion against the
    /// boolean's own intersection</b>: |A| − |A − B| must equal |A ∩ B|. That needs no second
    /// algorithm and no fitted constant, and it is a real check because the difference and the
    /// intersection keep OPPOSITE fragments of every split.
    /// </summary>
    [Theory]
    [InlineData(3.0, 10, 1.2, 4, 0)]   // drilled down through the front-bottom and front-top bands
    [InlineData(2.0, 10, 7, 1.4, 1)]   // cross-drilled along Y through both bottom bands
    [InlineData(2.0, 10, 7, 1.4, 2)]   // cross-drilled along X through the bottom band
    public void BandCrossingTool_ClosesAndBalances(double radius, double x, double y, double z, int axis)
    {
        var at = new Vector3d(x, y, z);
        double whole = Meshed(RoundedBox()).Volume();
        double difference = Meshed(BrepBoolean.Difference(RoundedBox(), Tool(radius, at, axis))).Volume();
        double intersection = Meshed(BrepBoolean.Intersection(RoundedBox(), Tool(radius, at, axis))).Volume();

        // The band curves are tracer polylines baked in at boolean time, so both solids carry
        // the same chordal approximation of the same curve; the identity closes to the size of
        // that approximation, not to machine precision (measured 0.02–0.06 % of the removed
        // volume at 96 segments/circle).
        Assert.Equal(whole - difference, intersection, 0.002 * intersection);
    }

    [Fact]
    public void BandCrossingTool_ConvergesWithTessellationDensity()
    {
        // Refusing to converge is how a fixed-sample-count floor announces itself (the lesson
        // the side-wall bore taught). Volumes here must settle, not wander.
        var volumes = new List<double>();
        foreach (int segments in (ReadOnlySpan<int>)[48, 96, 192])
        {
            volumes.Add(Meshed(
                BrepBoolean.Difference(RoundedBox(), Tool(3, new Vector3d(10, 1.2, 4), 0)), segments).Volume());
        }

        double first = Math.Abs(volumes[1] - volumes[0]);
        double second = Math.Abs(volumes[2] - volumes[1]);
        Assert.True(second < first / 2, $"steps must shrink: {first:E3} then {second:E3}");
    }

    /// <summary>
    /// Still refused, and pinned so a future fix flips it deliberately: a tool drilled ALONG a
    /// band's own axis, close enough to the front face that its intersection with the band runs
    /// the band's whole LENGTH instead of crossing it. That is the "cut chain crossing a face
    /// boundary part-way" family (todo.md), not the band-crossing one this file fixed.
    /// </summary>
    [Fact]
    public void ToolRunningAlongABandsAxis_StillRefusesLoudly()
    {
        Assert.ThrowsAny<Exception>(() =>
            BrepBoolean.Difference(RoundedBox(), Tool(2, new Vector3d(10, 1.4, 4), 2)));
    }
}
