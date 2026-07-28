using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Trimmed BAND tessellation — a region whose loop is two boundary chains monotone in one
/// surface parameter, joined by a rung at each end. Every mitered rim-fillet band has that
/// shape, and the chains are already paired, so the correct triangulation is a zip.
/// <para>These tests exist because ear-clipping such a region is not merely wasteful, it is
/// VISIBLY wrong. The clipper's shortest-diagonal rule eats the dense boundary chains
/// first, and three consecutive samples of a smooth boundary curve span a sliver whose
/// normal is <c>T x K</c> — the curve's binormal, not the surface's. A miter ellipse meets
/// the top of a fillet tangent to the flat face, so its geodesic curvature passes through
/// zero there and that binormal is perpendicular to the surface normal, its sign pure
/// rounding noise: half the slivers faced inward and the corners rendered as dark folded
/// lenses. Measured on a 30x20x6 box filleted at r=2 (segmentsPerCircle 48, curveSamples
/// 24): 13 088 triangles with 808 of them inverted, now 280 with none.</para>
/// </summary>
public class MiteredBandTessellationTests
{
    private static BrepSolid FilletedPrism(IReadOnlyList<Vector2d> polygon, double height, double radius)
    {
        var profile = Profile.FromLoop(polygon, Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY));
        var prism = SolidFactory.Extrude(profile, Vector3d.UnitZ * height);
        var top = prism.PlanarFacesWithNormal(Vector3d.UnitZ).Single();
        return Filleting.FilletRim(prism, top, radius);
    }

    /// <summary>The docs' filleted plate: <c>Shape.Box(30, 20, 6).FilletEdges(2, topRim)</c>.</summary>
    private static BrepSolid FilletedBox() =>
        FilletedPrism([new(0, 0), new(30, 0), new(30, 20), new(0, 20)], 6, 2);

    /// <summary>The docs' L-plate (chamfer-fillet.md): the vertex at (9, 9) is REFLEX, so
    /// its band reaches past its edge's end to meet the miter.</summary>
    private static BrepSolid FilletedL() =>
        FilletedPrism([new(0, 0), new(24, 0), new(24, 9), new(9, 9), new(9, 18), new(0, 18)], 8, 2);

    /// <summary>Every mitered corner of a hexagon is 120 degrees, not 90.</summary>
    private static BrepSolid FilletedHexagon() =>
        FilletedPrism(
            [.. Enumerable.Range(0, 6).Select(i => new Vector2d(20 * Math.Cos(i * Math.PI / 3), 20 * Math.Sin(i * Math.PI / 3)))],
            8, 2.5);

    /// <summary>
    /// Every non-planar face driven through the trimmed path and audited facet by facet
    /// (see <see cref="TessellationQuality"/>, which owns the reference-normal rules this
    /// measurement depends on and is shared with the whole-corpus gate in
    /// <see cref="TessellationCorpusQualityTests"/>).
    /// </summary>
    private static TessellationQuality.SolidReport FoldReport(
        BrepSolid solid, int segmentsPerCircle, int curveSamples)
    {
        var report = TessellationQuality.AuditTrimmedPath(solid, segmentsPerCircle, curveSamples);
        Assert.True(
            report.Refusals.Count == 0,
            $"the trimmed path refused a mitered band: {string.Join("; ", report.Refusals)}");
        Assert.True(report.Unprojectable == 0, $"a band vertex is off its own surface: {report.Describe()}");
        Assert.True(report.Slivers == 0, $"a band triangle is degenerate: {report.Describe()}");
        return report;
    }

    public static TheoryData<string> Shapes => ["box", "L", "hexagon"];

    private static BrepSolid Named(string name) => name switch
    {
        "box" => FilletedBox(),
        "L" => FilletedL(),
        _ => FilletedHexagon(),
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void MiteredBands_ContainNoFoldedTriangles(string shape)
    {
        var report = FoldReport(Named(shape), 48, 24);
        Assert.Equal(0, report.Folds);
        // A chord of one natural step subtends about 1/24 of a quarter turn, so the worst
        // any honest facet can deviate is a few parts in 1e5; the folds it replaces sat at
        // dot = -0.22.
        Assert.True(report.WorstDot > 0.999, $"worst facet-vs-surface normal agreement was {report.WorstDot}");
    }

    /// <summary>Coarse and very fine densities exercise the same code, and the fold is a
    /// structural defect, not a sampling artefact — it must be absent at both ends.</summary>
    [Theory]
    [InlineData(16, 8)]
    [InlineData(384, 192)]
    public void MiteredBands_ContainNoFoldedTrianglesAtAnyDensity(int segmentsPerCircle, int curveSamples)
    {
        Assert.Equal(0, FoldReport(FilletedBox(), segmentsPerCircle, curveSamples).Folds);
    }

    /// <summary>
    /// A band is a strip, so its cost is O(curveSamples) — the ear clipper made it
    /// O(curveSamples^2) by refining the long diagonals it had left behind. Doubling the
    /// sample count must roughly double the triangle count, never quadruple it.
    /// </summary>
    [Fact]
    public void MiteredBands_CostGrowsLinearlyWithTheSampleCount()
    {
        var solid = FilletedBox();
        int coarse = BRepTessellator.Tessellate(solid, 48, 24).FaceCount;
        int fine = BRepTessellator.Tessellate(FilletedBox(), 96, 48).FaceCount;

        // 13 088 was the ear-clipped count at the coarse density alone.
        Assert.True(coarse < 1000, $"a filleted box tessellated to {coarse} triangles");
        Assert.True(fine < 2.5 * coarse, $"triangle count went {coarse} -> {fine} for a 2x sample count");
    }

    /// <summary>
    /// The band's rungs run along the extrusion direction, which is RULED: a chord there is
    /// exact, so a strip spans it with two columns of vertices however long the edge is.
    /// A zip of two n-vertex chains is 2n - 2 triangles; the small excess is curvature
    /// refinement, because the miter ellipse is sampled uniformly in its OWN parameter while
    /// the band's generator arc is sampled uniformly in the rational arc parameter, so a few
    /// chain steps come out slightly coarser than one natural grid step.
    /// <para>The invariant that matters for welding is the other assertion: every shared edge
    /// sample survives into the triangulation bit-identically.</para>
    /// </summary>
    [Fact]
    public void MiteredBand_KeepsEveryBoundarySampleAndStaysAStrip()
    {
        var solid = FilletedBox();
        var edgePolylines = new Dictionary<BrepEdge, List<Vector3d>>();
        foreach (var edge in solid.Edges)
            edgePolylines[edge] = BRepTessellator.SampleEdge(edge, 48, 24);

        int bands = 0;
        foreach (var face in solid.Faces.Where(f => f.Surface is ExtrudedSurface))
        {
            var loop = BRepTessellator.LoopPolyline(face.Loops[0], edgePolylines);
            if (loop.Count < 8)
                continue; // the flat side faces: four straight edges, nothing to zip
            bands++;
            var polygons = new List<IReadOnlyList<Vector3d>>();
            Assert.True(TrimmedFaceTessellator.TryTessellate(face, edgePolylines, 48, 24, polygons));

            Assert.InRange(polygons.Count, loop.Count - 2, 2 * (loop.Count - 2));

            var used = new HashSet<Vector3d>(polygons.SelectMany(p => p));
            foreach (var sample in loop)
                Assert.Contains(sample, used);
        }
        Assert.Equal(4, bands);
    }

    /// <summary>
    /// Closed, two-manifold and Euler-clean at a density the ear-clip path could not reach:
    /// at curveSamples 192 its refinement gave up, and the silent grid fallback handed back
    /// an OPEN mesh with no complaint.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shapes))]
    public void MiteredFillet_TessellatesClosedAtHighSampleCounts(string shape)
    {
        var mesh = BRepTessellator.Tessellate(Named(shape), 384, 192);
        mesh.Validate();
        Assert.True(mesh.IsClosed, "a mitered fillet must tessellate closed at any density");
        Assert.Equal(2, mesh.EulerCharacteristic);
    }

    /// <summary>
    /// Exact filleted-prism volume: at depth t below the top the cross-section is the base
    /// polygon offset inward by r - sqrt(r^2 - t^2) with MITERED corners, so
    /// V = A0*h - P0*r^2*(1 - pi/4) + (sum tan(theta_i/2))*r^3*(5/3 - pi/2), the corner term
    /// carrying reflex corners' negative tangents. (Same law as
    /// <see cref="FilletCornerVolumeTests"/>, restated here so this file stands alone.)
    /// </summary>
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
            cornerSum += Math.Tan(Math.Atan2(incoming.Cross(outgoing), incoming.Dot(outgoing)) / 2);
        }
        return area / 2 * height
            - perimeter * radius * radius * (1 - Math.PI / 4)
            + cornerSum * radius * radius * radius * (5.0 / 3 - Math.PI / 2);
    }

    /// <summary>
    /// The strip's facets are inscribed chords of the quarter cylinders, so the mesh volume
    /// approaches the analytic prism from BELOW and quarters its deficit per doubling. The
    /// deficit is now the honest chordal one — the ear-clipped mesh's was three times larger
    /// because its refinement stopped short wherever the monotone-decrease rule cut a
    /// cascade.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shapes))]
    public void MiteredFilletVolume_ConvergesQuadraticallyFromInside(string shape)
    {
        Vector2d[] polygon = shape switch
        {
            "box" => [new(0, 0), new(30, 0), new(30, 20), new(0, 20)],
            "L" => [new(0, 0), new(24, 0), new(24, 9), new(9, 9), new(9, 18), new(0, 18)],
            _ => [.. Enumerable.Range(0, 6).Select(i => new Vector2d(20 * Math.Cos(i * Math.PI / 3), 20 * Math.Sin(i * Math.PI / 3)))],
        };
        double height = 8, radius = shape == "hexagon" ? 2.5 : 2;
        if (shape == "box")
            (height, radius) = (6, 2);
        double expected = AnalyticVolume(polygon, height, radius);

        double coarse = expected - BRepTessellator.Tessellate(FilletedPrism(polygon, height, radius), 48, 24).Volume();
        double fine = expected - BRepTessellator.Tessellate(FilletedPrism(polygon, height, radius), 96, 48).Volume();

        Assert.True(coarse > 0 && fine > 0, "inscribed facets stay inside the exact solid");
        Assert.True(coarse / expected < 1e-4, $"coarse deficit {coarse / expected:E2} of the analytic volume");
        double ratio = coarse / fine;
        Assert.True(ratio is > 3.5 and < 4.5, $"second-order convergence expected, got a ratio of {ratio}");
    }

    /// <summary>
    /// A trimmed face the tessellator cannot handle must REFUSE, naming the face and the
    /// sample counts. It used to fall through to the surface's natural grid, which covers
    /// the whole parameter rectangle rather than the trimmed face — geometry that is not
    /// merely coarse but wrong, welding into an open mesh with no complaint. Loud refusal
    /// over silent wrong geometry.
    /// </summary>
    [Fact]
    public void TrimmedFaceThatCannotBeTessellated_RefusesLoudlyInsteadOfFallingBackToTheGrid()
    {
        // A band on a bounded planar patch whose loop leaves the surface entirely: inverse
        // evaluation cannot pull the third corner back, and the natural grid (the whole
        // parallelogram) is not this face.
        var generator = new Line3d((0, 0, 0), (1, 0, 0));
        var surface = new ExtrudedSurface(generator, (0, 0, 1));
        var a = new BrepVertex((0, 0, 0));
        var b = new BrepVertex((1, 0, 0));
        var c = new BrepVertex((0.5, 5, 0.5)); // 5 units off the surface
        var face = new BrepFace(surface,
        [
            new BrepLoop([
                new BrepCoedge(new BrepEdge(new Line3d(a.Position, b.Position), Interval.Unit, a, b), true),
                new BrepCoedge(new BrepEdge(new Line3d(b.Position, c.Position), Interval.Unit, b, c), true),
                new BrepCoedge(new BrepEdge(new Line3d(c.Position, a.Position), Interval.Unit, c, a), true),
            ]),
        ]);

        var error = Assert.Throws<NotSupportedException>(
            () => BRepTessellator.Tessellate(new BrepSolid([new BrepShell([face])]), 48, 24));
        Assert.Contains("open mesh", error.Message);
        Assert.Contains("ExtrudedSurface", error.Message);
        Assert.Contains("curveSamples=24", error.Message);
        Assert.Contains("does not pull back", error.Message);
    }
}
