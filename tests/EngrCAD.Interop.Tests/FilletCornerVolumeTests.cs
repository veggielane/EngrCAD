using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Analytic volumes for sharp-corner rim fillets. A prism whose top rim is filleted with
/// radius r has, at depth t below the top, the cross-section of the base polygon offset
/// inward by δ(t) = r − √(r² − t²) with MITERED corners, so the offset-polygon area law
/// applies exactly:
/// <para>A(δ) = A₀ − P₀·δ + δ²·Σᵢ tan(θᵢ/2), θᵢ the signed exterior turn at corner i.</para>
/// Integrating over t ∈ [0, r] with ∫δ = r²(1 − π/4) and ∫δ² = r³(5/3 − π/2) gives
/// <para>V = A₀·h − P₀·r²(1 − π/4) + (Σ tan(θᵢ/2))·r³(5/3 − π/2)</para>
/// — the corner term is what the miter contributes, and it carries the reflex corners'
/// negative tangents, so an L-shape is covered by the same formula as a box.
/// </summary>
public class FilletCornerVolumeTests
{
    /// <summary>Exact filleted-prism volume from the base polygon (CCW, in the XY plane).</summary>
    private static double AnalyticVolume(IReadOnlyList<Vector2d> polygon, double height, double radius)
    {
        int n = polygon.Count;
        double area = 0, perimeter = 0, cornerSum = 0;
        for (int i = 0; i < n; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % n];
            area += a.X * b.Y - b.X * a.Y;
            perimeter += (b - a).Length;

            var previous = polygon[(i + n - 1) % n];
            var incoming = (a - previous).Normalized();
            var outgoing = (b - a).Normalized();
            // Signed turn: positive for a convex (left) corner on a CCW loop.
            double turn = Math.Atan2(incoming.Cross(outgoing), incoming.Dot(outgoing));
            cornerSum += Math.Tan(turn / 2);
        }
        area /= 2;
        return area * height
            - perimeter * radius * radius * (1 - Math.PI / 4)
            + cornerSum * radius * radius * radius * (5.0 / 3 - Math.PI / 2);
    }

    private static BrepSolid FilletedPrism(IReadOnlyList<Vector2d> polygon, double height, double radius)
    {
        var profile = Profile.FromLoop(polygon, Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY));
        var prism = SolidFactory.Extrude(profile, Vector3d.UnitZ * height);
        var top = prism.PlanarFacesWithNormal(Vector3d.UnitZ).Single();
        return Filleting.FilletRim(prism, top, radius);
    }

    // The trimmed-band tessellation approximates the quarter cylinders from inside, so the
    // mesh volume is a slight under-estimate; 5e-5 relative is the measured envelope at
    // these sample counts (worst case ~2.2e-5), three decades above the discrepancy any
    // wrong corner geometry would produce (a mis-built corner is O(r³) per corner, ~1e-2
    // relative here). It was 3e-4 while mitered bands were ear-clipped: the clipper left
    // long interior diagonals whose midpoint refinement stopped short wherever the
    // monotone-decrease rule cut a cascade, so the facets sagged further inside than the
    // chords alone would. Zipping the band's paired boundary chains removed that slack.
    private const double VolumeTolerance = 5e-5;

    private static void AssertFilletedPrism(
        IReadOnlyList<Vector2d> polygon, double height, double radius, int expectedFaces, int curveSamples = 48)
    {
        var solid = FilletedPrism(polygon, height, radius);
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(expectedFaces, solid.Faces.Count());
        Assert.Equal(polygon.Count, solid.Edges.Count(e => e.Curve is Ellipse3d));

        var mesh = BRepTessellator.Tessellate(solid, curveSamples * 2, curveSamples);
        mesh.Validate();
        Assert.True(mesh.IsClosed, "a sharp-corner fillet must tessellate closed");
        Assert.Equal(2, mesh.EulerCharacteristic);

        double expected = AnalyticVolume(polygon, height, radius);
        Assert.True(Math.Abs(mesh.Volume() - expected) / expected < VolumeTolerance,
            $"filleted volume {mesh.Volume()} vs analytic {expected}");
    }

    [Fact]
    public void BoxTopRimFillet_MatchesAnalyticVolume()
    {
        Vector2d[] rectangle = [new(0, 0), new(4, 0), new(4, 3), new(0, 3)];
        // bottom + 4 sides + shrunk top + 4 bands.
        AssertFilletedPrism(rectangle, height: 2, radius: 0.4, expectedFaces: 10);
    }

    [Fact]
    public void HexagonalPlateFillet_NonRightAngleMiters()
    {
        // 120° corners: the miter ellipse's long semi-axis is r / cos(30°), not r·√2.
        var hexagon = Enumerable.Range(0, 6)
            .Select(i => new Vector2d(2 * Math.Cos(i * Math.PI / 3), 2 * Math.Sin(i * Math.PI / 3)))
            .ToArray();
        AssertFilletedPrism(hexagon, height: 1, radius: 0.25, expectedFaces: 14);
    }

    [Fact]
    public void LShapedPlateFillet_ReflexCornerMatchesAnalyticVolume()
    {
        // The vertex at (1,1) turns clockwise on a CCW loop: tan(θ/2) is negative there,
        // and the band must reach PAST its edge's end to meet the miter.
        Vector2d[] ell = [new(0, 0), new(3, 0), new(3, 1), new(1, 1), new(1, 3), new(0, 3)];
        AssertFilletedPrism(ell, height: 1, radius: 0.2, expectedFaces: 14);
    }

    /// <summary>
    /// Steiner's formula for the convex opening (K ⊖ B_r) ⊕ B_r: with K the eroded body,
    /// V = V₀ + A₀·r + (r²/2)·Σ ℓₑ·θₑ + (4π/3)·r³ — the last term because the exterior
    /// solid angles of the spherical corner patches sum to a whole sphere, so a filleted
    /// box's eight octants are exactly one ball.
    /// </summary>
    private static double AnalyticRoundedBox(double w, double d, double h, double r)
    {
        double a = w - 2 * r, b = d - 2 * r, c = h - 2 * r;
        return a * b * c                                     // eroded box
            + r * 2 * (a * b + a * c + b * c)                // 6 slabs
            + Math.PI * r * r * (a + b + c)                  // 12 quarter cylinders
            + 4.0 / 3 * Math.PI * r * r * r;                 // 8 octants = one ball
    }

    [Fact]
    public void WholeSolidFillet_MatchesSteinersFormula()
    {
        double w = 4, d = 3, h = 2, r = 0.4;
        var rounded = Filleting.FilletAllEdges(
            SolidFactory.MakeBox(new Aabb((0, 0, 0), (w, d, h))), r);
        rounded.Validate();

        var mesh = BRepTessellator.Tessellate(rounded, 64, 64);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);

        double expected = AnalyticRoundedBox(w, d, h, r);
        Assert.True(Math.Abs(mesh.Volume() - expected) / expected < 1e-4,
            $"rounded box volume {mesh.Volume()} vs Steiner {expected}");
    }

    [Fact]
    public void WholeSolidFillet_ConvergesQuadratically()
    {
        // Every face is on the natural grid path (the bands and corner patches are
        // full-domain by construction), so the facets converge cleanly from inside —
        // halving the chord length must quarter the deficit.
        double w = 4, d = 3, h = 2, r = 0.4;
        var rounded = Filleting.FilletAllEdges(
            SolidFactory.MakeBox(new Aabb((0, 0, 0), (w, d, h))), r);
        double expected = AnalyticRoundedBox(w, d, h, r);

        double coarse = expected - BRepTessellator.Tessellate(rounded, 32, 32).Volume();
        double fine = expected - BRepTessellator.Tessellate(rounded, 64, 64).Volume();
        Assert.True(coarse > 0 && fine > 0, "inscribed facets stay inside the exact solid");
        double ratio = coarse / fine;
        Assert.True(ratio is > 3.5 and < 4.5, $"second-order convergence expected, got a ratio of {ratio}");
    }

    [Fact]
    public void FilletConvergesTowardTheAnalyticVolumeFromInside()
    {
        Vector2d[] rectangle = [new(0, 0), new(4, 0), new(4, 3), new(0, 3)];
        var solid = FilletedPrism(rectangle, 2, 0.4);
        double expected = AnalyticVolume(rectangle, 2, 0.4);

        double coarse = BRepTessellator.Tessellate(solid, 16, 8).Volume();
        double fine = BRepTessellator.Tessellate(solid, 64, 32).Volume();
        Assert.True(coarse < fine, "inscribed facets: refining must add volume");
        Assert.True(fine < expected, "the faceted solid stays inside the exact one");
        Assert.True((expected - fine) / expected < VolumeTolerance);
    }
}
