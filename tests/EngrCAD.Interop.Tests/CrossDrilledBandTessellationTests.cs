using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Trimmed BAND-WITH-HOLES tessellation — a periodic band carrying interior hole loops,
/// which is what every cross-drilled bore wall is. The band is unrolled at a seam clear
/// of the holes and then SWEPT into u-monotone slabs; these tests exist because ear
/// clipping it, which is what used to happen, cannot do better than a fan.
/// <para>The reason is structural, not a matter of degree. Both ring loops of an extruded
/// or cylindrical band lie at a constant v — the surface's v-domain ends, bit-identically
/// — so consecutive ring samples are EXACTLY collinear in uv, and the clipper refuses
/// exactly-straight corners because they would be zero-area ears. The only clippable ears
/// left are the unrolled rectangle's own four corners, so the clipper fans the whole band
/// from a corner, and curvature refinement then bisects those long fan chords into
/// slivers whose normals are the boundary's binormal rather than the surface's. The docs'
/// oblique-section housing rendered its main bore as a crumpled sheet because of it.</para>
/// <para>Measured on that housing (44x44x30 box, r=13 Z-bore, r=5 X-bore) at
/// segmentsPerCircle 128: the bore wall went from 12 164 triangles with a worst
/// facet-vs-surface normal agreement of 0.0198 (an 88.9-degree sliver) to 416 triangles
/// at 0.99981, and the whole mesh from 13 480 triangles to 1 732.</para>
/// </summary>
public class CrossDrilledBandTessellationTests
{
    /// <summary>The docs' <c>render:section-oblique</c> housing (viewer.md), verbatim.</summary>
    private static Shape Housing() =>
        Shape.Box(44, 44, 30)
        - Shape.Cylinder(13, 40)
        - Shape.Cylinder(5, 60).RotateY(Math.PI / 2);

    private const double BoreRadius = 13;

    /// <summary>
    /// Bicylinder (generalized Steinmetz) volume for radii a &lt;= b with intersecting
    /// perpendicular axes: the section at fixed y along the common perpendicular is a
    /// 2*sqrt(a^2-y^2) x 2*sqrt(b^2-y^2) rectangle, so V = 4a^2 * int cos^2 t *
    /// sqrt(b^2 - a^2 sin^2 t) dt after y = a sin t — elliptic for a &lt; b, so composite
    /// Simpson on the substituted (non-singular) integrand. Same law as
    /// <see cref="CrossDrillBooleanTests"/>, restated so this file stands alone.
    /// </summary>
    private static double BicylinderVolume(double a, double b)
    {
        const int n = 20000;
        double h = Math.PI / n, sum = 0;
        for (int i = 0; i <= n; i++)
        {
            double t = -Math.PI / 2 + i * h;
            double f = Math.Cos(t) * Math.Cos(t) * Math.Sqrt(b * b - a * a * Math.Sin(t) * Math.Sin(t));
            sum += (i == 0 || i == n ? 1 : i % 2 == 1 ? 4 : 2) * f;
        }
        return 4 * a * a * sum * h / 3;
    }

    /// <summary>
    /// Inclusion–exclusion: both bores run right through the box, so each removes its full
    /// prism and their lens — entirely inside the box and inside the Z-bore's height — is
    /// subtracted twice and must be added back. 40 699.916265.
    /// </summary>
    private static double AnalyticVolume() =>
        44.0 * 44 * 30
        - Math.PI * BoreRadius * BoreRadius * 30
        - Math.PI * 5 * 5 * 44
        + BicylinderVolume(5, BoreRadius);

    /// <summary>
    /// Fold report over every non-planar face, tessellated through the trimmed path.
    /// <para>The reference normal is the mean of the surface normals at the triangle's
    /// three VERTICES, never the normal at its centroid: a centroid sits a sagitta inside
    /// a curved surface, far past the 1e-6 inverse-evaluation tolerance, so projecting it
    /// fails and a centroid-based assertion silently checks nothing. The vertices are
    /// exact surface points by construction. (An earlier fold hunt on the mitered fillets
    /// reported zero folds where there were 808, for exactly this reason.)</para>
    /// </summary>
    private static (int Folds, double WorstDot, int Triangles) FoldReport(
        BrepSolid solid, int segmentsPerCircle, int curveSamples)
    {
        var edgePolylines = new Dictionary<BrepEdge, List<Vector3d>>();
        foreach (var edge in solid.Edges)
            edgePolylines[edge] = BRepTessellator.SampleEdge(edge, segmentsPerCircle, curveSamples);

        int folds = 0, triangles = 0;
        double worst = 1;
        foreach (var face in solid.Faces)
        {
            if (face.Surface is PlaneSurface)
                continue;
            var polygons = new List<IReadOnlyList<Vector3d>>();
            Assert.True(
                TrimmedFaceTessellator.TryTessellate(
                    face, edgePolylines, segmentsPerCircle, curveSamples, polygons, out string? why),
                $"the trimmed path refused a cross-drilled band: {why}");

            foreach (var polygon in polygons)
            {
                triangles++;
                var normal = (polygon[1] - polygon[0]).Cross(polygon[2] - polygon[0]);
                var exact = Vector3d.Zero;
                foreach (var p in polygon)
                {
                    Assert.True(
                        face.Surface.TryProjectPoint(p, out var uv, FaceGeometry.InverseEvaluationTolerance),
                        $"a band vertex at {p} does not lie on its own face's surface");
                    exact += face.Surface.NormalAt(uv.X, uv.Y).Normalized();
                }
                Assert.True(normal.Length > 0, "a band triangle is degenerate");
                double dot = normal.Normalized().Dot(exact.Normalized());
                worst = Math.Min(worst, dot);
                if (dot <= 0)
                    folds++;
            }
        }
        return (folds, worst, triangles);
    }

    /// <summary>
    /// The fold is a structural defect, not a sampling artefact, so it must be absent at
    /// every density — including the coarse end, where the ear clipper's slivers were
    /// worst relative to the face.
    /// </summary>
    [Theory]
    [InlineData(16, 12)]
    [InlineData(32, 24)]
    [InlineData(64, 48)]
    [InlineData(128, 96)]
    public void CrossDrilledBoreWall_ContainsNoFoldedOrNearTangentTriangles(
        int segmentsPerCircle, int curveSamples)
    {
        var (folds, worstDot, _) = FoldReport(Housing().ToBrep(), segmentsPerCircle, curveSamples);

        Assert.Equal(0, folds);
        // A facet spanning one natural step of a 16-gon still agrees with the surface to
        // cos(pi/16) = 0.98; the slivers this replaces sat between 0.0086 and 0.067, i.e.
        // very nearly PERPENDICULAR to the surface they were meant to approximate. The
        // bar is set well below the measured 0.643 (at 16 segments) so it tracks the
        // structural property, not the arithmetic.
        Assert.True(worstDot > 0.5, $"worst facet-vs-surface normal agreement was {worstDot}");
    }

    /// <summary>
    /// A polygon with n boundary vertices and h holes triangulates into exactly n + 2h − 2
    /// triangles, and the sweep hits that up to a little curvature refinement. The ear
    /// clipper's fan, by contrast, left long chords that refinement then bisected
    /// quadratically: at 32 segments the bore wall's 198 boundary samples became 734
    /// triangles instead of the structural 200.
    /// </summary>
    [Fact]
    public void CrossDrilledBoreWall_EmitsAboutOneTrianglePerBoundarySample()
    {
        var solid = Housing().ToBrep();
        var edgePolylines = new Dictionary<BrepEdge, List<Vector3d>>();
        foreach (var edge in solid.Edges)
            edgePolylines[edge] = BRepTessellator.SampleEdge(edge, 32, 24);

        // The main bore wall is the only face carrying both winding rings and hole loops.
        var bore = solid.Faces.Single(f => f.Loops.Count == 4);
        int samples = bore.Loops.Sum(l => BRepTessellator.LoopPolyline(l, edgePolylines).Count);

        var polygons = new List<IReadOnlyList<Vector3d>>();
        Assert.True(TrimmedFaceTessellator.TryTessellate(bore, edgePolylines, 32, 24, polygons));
        Assert.InRange(polygons.Count, samples - 2, (int)(1.5 * samples));

        // Every shared edge sample must survive verbatim, or the neighbouring cap face
        // cannot weld to this one.
        var used = new HashSet<Vector3d>(polygons.SelectMany(p => p));
        foreach (var loop in bore.Loops)
        {
            foreach (var sample in BRepTessellator.LoopPolyline(loop, edgePolylines))
                Assert.Contains(sample, used);
        }
    }

    /// <summary>
    /// A band is a strip, so its cost is linear in the sample count. The ear-clipped path
    /// made it quadratic by refining the chords its fan left behind: 2 844 -&gt; 13 480
    /// triangles for one doubling, against 824 -&gt; 1 732 now.
    /// </summary>
    [Fact]
    public void CrossDrilledHousing_CostGrowsLinearlyWithTheSampleCount()
    {
        int coarse = BRepTessellator.Tessellate(Housing().ToBrep(), 64, 48).FaceCount;
        int fine = BRepTessellator.Tessellate(Housing().ToBrep(), 128, 96).FaceCount;

        Assert.True(fine < 3000, $"a cross-drilled housing tessellated to {fine} triangles");
        Assert.True(fine < 2.5 * coarse, $"triangle count went {coarse} -> {fine} for a 2x sample count");
    }

    /// <summary>
    /// The mesh inscribes both bores, so it removes slightly too little material and its
    /// volume approaches the analytic solid from ABOVE, halving the sampling step
    /// quartering the excess.
    /// <para>This is the assertion that fails on the ear-clipped path, and it fails in the
    /// way that matters: that path does not converge at all. Measured excess over the
    /// analytic 40 699.916 at segmentsPerCircle 32/64/128/256 was 61.19 / 18.60 / 13.40 /
    /// 11.25 — ratios 3.29, then 1.39, then 1.19, stalling around 11 because refinement's
    /// monotone-decrease rule cuts the cascade wherever it likes and a mesh whose accuracy
    /// depends on refinement has no convergence guarantee. The swept path measures 76.20 /
    /// 21.49 / 5.97 / 1.82, i.e. ratios 3.55, 3.60, 3.27.</para>
    /// <para>The ratio is a little under 4 for an honest reason: the cross-bore breakout
    /// curves are tracer polylines baked into the B-Rep at boolean time, so their sampling
    /// does NOT refine with segmentsPerCircle and their fixed chordal error is a floor
    /// under the total.</para>
    /// </summary>
    [Fact]
    public void CrossDrilledHousingVolume_ConvergesQuadraticallyFromOutside()
    {
        double expected = AnalyticVolume();
        double coarse = BRepTessellator.Tessellate(Housing().ToBrep(), 64, 48).Volume() - expected;
        double fine = BRepTessellator.Tessellate(Housing().ToBrep(), 128, 96).Volume() - expected;

        Assert.True(coarse > 0 && fine > 0, "inscribed bores remove too little, so the mesh is the larger solid");
        Assert.True(coarse / expected < 1e-3, $"coarse excess {coarse / expected:E2} of the analytic volume");
        double ratio = coarse / fine;
        Assert.True(ratio > 2.5, $"expected near-quadratic convergence, got a ratio of {ratio}");
    }

    /// <summary>Closed, two-manifold and Euler-clean at both ends of the density range.</summary>
    [Theory]
    [InlineData(16, 12)]
    [InlineData(256, 192)]
    public void CrossDrilledHousing_TessellatesClosedAtAnyDensity(int segmentsPerCircle, int curveSamples)
    {
        var mesh = BRepTessellator.Tessellate(Housing().ToBrep(), segmentsPerCircle, curveSamples);
        mesh.Validate();
        Assert.True(mesh.IsClosed, "a cross-drilled housing must tessellate closed at any density");
        // Two through-bores meeting in a lens: an X-shaped tunnel with 4 openings, so a
        // genus-3 handlebody. (Same reasoning as CrossDrillBooleanTests.)
        Assert.Equal(2 - 2 * 3, mesh.EulerCharacteristic);
    }
}
