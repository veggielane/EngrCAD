using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

/// <summary>
/// The remesher's shape measure. Both figures are pinned against closed forms first — an
/// equilateral triangle is exactly 60° and exactly ratio 1, a right isosceles one exactly 45°
/// and 2 − √2 — because a quality metric that agrees with itself proves nothing.
/// </summary>
public class TriangleQualityTests
{
    private static HalfEdgeMesh OneTriangle(Vector3d a, Vector3d b, Vector3d c) =>
        HalfEdgeMesh.Build([a, b, c], [new[] { 0, 1, 2 }]);

    [Fact]
    public void AnEquilateralTriangleIsExactlyTheIdealOnBothMeasures()
    {
        double h = Math.Sqrt(3) / 2;
        var report = TriangleQuality.Analyze(OneTriangle((0, 0, 0), (1, 0, 0), (0.5, h, 0)));

        Assert.Equal(1, report.TriangleCount);
        Assert.Equal(60, report.MinAngleDegrees, 12);
        Assert.Equal(1.0, report.MinRadiusRatio, 12);
        Assert.Equal(0, report.SliverCount);
        Assert.Equal(0, report.ConstrainedCount);
    }

    /// <summary>
    /// A right isosceles triangle — the shape a square quad fans into, so this is the number
    /// every grid mesh in the project should read. For legs of 1 a right triangle's inradius is
    /// (a + b − c)/2 = (2 − √2)/2 and its circumradius is the hypotenuse's half, √2/2, so
    /// 2r/R = 2√2 − 2 = 0.828427 exactly.
    /// </summary>
    [Fact]
    public void ARightIsoscelesTriangleMatchesItsClosedForm()
    {
        var report = TriangleQuality.Analyze(OneTriangle((0, 0, 0), (1, 0, 0), (0, 1, 0)));

        Assert.Equal(45, report.MinAngleDegrees, 12);
        Assert.Equal(2 * Math.Sqrt(2) - 2, report.MinRadiusRatio, 12);
    }

    /// <summary>
    /// The measure has to SEE a needle: an angle near zero and a ratio near zero, from a
    /// triangle whose area is perfectly respectable in absolute terms. Scale-free, so it reads
    /// the same at three decades — a dimensionless ratio has no absolute quantity in it to
    /// compare, which is why no relative degeneracy floor is needed here.
    /// </summary>
    [Theory]
    [InlineData(1e-3)]
    [InlineData(1.0)]
    [InlineData(1e3)]
    public void ANeedleIsSeenAtEveryScale(double scale)
    {
        var report = TriangleQuality.Analyze(OneTriangle(
            (0, 0, 0), (scale, 0, 0), (scale * 0.5, scale * 0.001, 0)));

        Assert.True(report.MinAngleDegrees < 0.2, $"{report.MinAngleDegrees} degrees");
        Assert.True(report.MinRadiusRatio < 0.01, $"ratio {report.MinRadiusRatio}");
        Assert.Equal(1, report.SliverCount);
    }

    /// <summary>A face with a zero-length edge has no shape; it scores 0 rather than NaN or a skip.</summary>
    [Fact]
    public void ACollapsedTriangleScoresZeroRatherThanBeingSkipped()
    {
        var mesh = HalfEdgeMesh.Build(
            [(0, 0, 0), (1, 0, 0), (0, 1, 0), (2, 0, 0)],
            [new[] { 0, 1, 2 }, new[] { 1, 3, 2 }]);
        var moved = HalfEdgeMesh.Build(
            [(0, 0, 0), (0, 0, 0), (0, 1, 0), (2, 0, 0)],
            [new[] { 0, 1, 2 }, new[] { 1, 3, 2 }]);

        Assert.Equal(2, TriangleQuality.Analyze(mesh).TriangleCount);
        var report = TriangleQuality.Analyze(moved);
        Assert.Equal(2, report.TriangleCount);
        Assert.Equal(0, report.MinAngleDegrees);
        Assert.Equal(0, report.MinRadiusRatio);
    }

    /// <summary>
    /// The partition. A fully pinned triangle leaves the free population entirely — and the
    /// numbers must be exactly those of the mesh without it, or the partition is doing more
    /// than partitioning.
    /// </summary>
    [Fact]
    public void FullyPinnedTrianglesLeaveTheFreePopulationUntouched()
    {
        double h = Math.Sqrt(3) / 2;
        // One equilateral triangle and one needle sharing an edge.
        var mesh = HalfEdgeMesh.Build(
            [(0, 0, 0), (1, 0, 0), (0.5, h, 0), (0.5, -0.001, 0)],
            [new[] { 0, 1, 2 }, new[] { 0, 3, 1 }]);

        var all = TriangleQuality.Analyze(mesh);
        Assert.Equal(2, all.TriangleCount);
        Assert.Equal(0, all.ConstrainedCount);
        Assert.True(all.MinAngleDegrees < 0.2);   // the needle sets the worst

        // Pin the needle's three corners and it stops counting against the free population,
        // whose figures then read exactly the equilateral triangle's.
        var partitioned = TriangleQuality.Analyze(mesh, [0, 1, 3]);
        Assert.Equal(2, partitioned.TriangleCount);
        Assert.Equal(1, partitioned.ConstrainedCount);
        Assert.Equal(1, partitioned.FreeCount);
        Assert.Equal(60, partitioned.MinAngleDegrees, 12);
        Assert.Equal(1.0, partitioned.MinRadiusRatio, 12);
        Assert.Equal(0, partitioned.SliverCount);
        // Reported, never hidden.
        Assert.Equal(1, partitioned.ConstrainedSliverCount);
        Assert.True(partitioned.MinConstrainedAngleDegrees < 0.2);
    }

    /// <summary>
    /// A PARTIALLY pinned triangle stays in the free population — the conservative side, since
    /// the remesher had some freedom there and the alternative would hide real defects behind
    /// constraints that were not binding. Stated in the class remarks; pinned here.
    /// </summary>
    [Fact]
    public void APartiallyPinnedTriangleCountsAsFree()
    {
        var mesh = OneTriangle((0, 0, 0), (1, 0, 0), (0.5, 0.001, 0));

        Assert.Equal(0, TriangleQuality.Analyze(mesh, [0, 1]).ConstrainedCount);
        Assert.Equal(1, TriangleQuality.Analyze(mesh, [0, 1]).SliverCount);
        Assert.Equal(1, TriangleQuality.Analyze(mesh, [0, 1, 2]).ConstrainedCount);
    }

    /// <summary>
    /// Every triangle constrained means there is no free population to report a worst case
    /// over, so the figures are NaN rather than a misleading infinity or zero — the spelling
    /// <c>TetQualityReport</c> uses for the same situation.
    /// </summary>
    [Fact]
    public void AnEntirelyConstrainedMeshReportsNaNRatherThanAMisleadingNumber()
    {
        var report = TriangleQuality.Analyze(OneTriangle((0, 0, 0), (1, 0, 0), (0, 1, 0)), [0, 1, 2]);

        Assert.Equal(1, report.ConstrainedCount);
        Assert.Equal(0, report.FreeCount);
        Assert.True(double.IsNaN(report.MinAngleDegrees));
        Assert.True(double.IsNaN(report.MeanRadiusRatio));
        Assert.True(double.IsNaN(report.SliverFreeFraction));
        Assert.Equal(45, report.MinConstrainedAngleDegrees, 12);
    }

    /// <summary>
    /// Non-triangles are measured through <see cref="PolygonFan"/>, so what is reported is what
    /// every consumer of the mesh actually gets. A unit square fans into two right isosceles
    /// triangles, and the report must read exactly that.
    /// </summary>
    [Fact]
    public void QuadsAreMeasuredThroughTheProjectsOwnFanRule()
    {
        var quad = HalfEdgeMesh.Build(
            [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)],
            [new[] { 0, 1, 2, 3 }]);

        var report = TriangleQuality.Analyze(quad);

        Assert.Equal(2, report.TriangleCount);
        Assert.Equal(45, report.MinAngleDegrees, 12);
        Assert.Equal(2 * Math.Sqrt(2) - 2, report.MinRadiusRatio, 12);
    }

    [Fact]
    public void TheSliverThresholdIsRecordedAndHonoured()
    {
        var mesh = OneTriangle((0, 0, 0), (1, 0, 0), (0.5, 0.3, 0)); // min angle ~31 degrees

        var strict = TriangleQuality.Analyze(mesh, null, sliverAngleDegrees: 45);
        var lax = TriangleQuality.Analyze(mesh, null, sliverAngleDegrees: 20);

        Assert.Equal(45, strict.SliverAngleDegrees);
        Assert.Equal(1, strict.SliverCount);
        Assert.Equal(0, lax.SliverCount);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TriangleQuality.Analyze(mesh, null, sliverAngleDegrees: 61));
    }

    // ---------------------------------------------------------------- through the remesher

    /// <summary>
    /// <see cref="RemeshResult.Quality"/> reports the remesher's OWN pinned set, which is the
    /// reason the measure lives on the result rather than being left to the caller. A closed
    /// cylinder has every original vertex on a rim crease, so its remesh is where a constrained
    /// population actually appears.
    /// </summary>
    [Fact]
    public void RemeshResultCarriesItsQualityWithItsOwnConstraints()
    {
        var cylinder = MeshPrimitives.Cylinder(10, 20, 32).Triangulated();
        var result = Remesher.Remesh(cylinder, new RemeshOptions(2.0)
        {
            Iterations = 14,
            ProjectionTarget = new MeshProjectionTarget(cylinder),
        });

        Assert.NotNull(result.Quality);
        Assert.Equal(result.Mesh.FaceCount, result.Quality.TriangleCount);
        // The default guard leaves a well-shaped mesh: measured 29.19 degrees here.
        Assert.True(result.Quality.MinAngleDegrees > 20,
            $"worst free angle {result.Quality.MinAngleDegrees:F2} degrees");
        Assert.True(result.Quality.MinRadiusRatio > 0.3);
        Assert.Contains("min angle", result.Quality.ToText());
    }

    /// <summary>
    /// And the partition is not decoration: a 48-segment cylinder's rim circle starts denser
    /// than the collapse threshold, and a pinned chain cannot be coarsened (a collapse needs a
    /// free end), so triangles spanning three rim vertices stay frozen. They are what the
    /// un-partitioned worst angle had been reporting all along — 0.11° against the free
    /// population's 28.11° — which is the whole reason for measuring the two separately.
    /// </summary>
    [Fact]
    public void AFrozenCreaseChainShowsUpAsConstrainedRatherThanAsAFailure()
    {
        var cylinder = MeshPrimitives.Cylinder(10, 20, 48).Triangulated();
        var result = Remesher.Remesh(cylinder, new RemeshOptions(2.0)
        {
            Iterations = 14,
            ProjectionTarget = new MeshProjectionTarget(cylinder),
        });

        Assert.True(result.Quality.ConstrainedCount > 0,
            "the rim chain is denser than the target and cannot be coarsened");
        Assert.True(result.Quality.MinConstrainedAngleDegrees < 5,
            $"those are genuinely slivers: {result.Quality.MinConstrainedAngleDegrees:F2} degrees");
        Assert.True(result.Quality.MinAngleDegrees > 20,
            $"and the free population is fine: {result.Quality.MinAngleDegrees:F2} degrees");
    }
}
