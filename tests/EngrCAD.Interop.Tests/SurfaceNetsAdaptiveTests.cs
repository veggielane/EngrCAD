using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Adaptive output: the uniform grid costs one quad per cell whatever the surface is
/// doing, and <see cref="SurfaceNetsOptions.SimplifyTolerance"/> merges the cells whose
/// merged quadric still describes the same surface.
/// <para>
/// The headline is the identity, not the ratio: a box comes back as <b>SIX QUADS</b> with
/// its volume EXACTLY 1000 at a tolerance a thousandth of a cell — a flat region is
/// described by one plane at any size, so collapsing it is provably lossless and the
/// tolerance never has to be spent on it.
/// </para>
/// </summary>
public class SurfaceNetsAdaptiveTests(ITestOutputHelper output)
{
    private static SurfaceNetsOptions At(double toleranceCells, double cell) =>
        new() { SimplifyTolerance = toleranceCells * cell };

    /// <summary>
    /// The lossless case: every face of a box is one plane, so the whole thing is six
    /// quads however fine the grid — and the volume is still exactly the box's, because a
    /// merged quadric of coplanar samples has an exact zero minimiser and the collapse
    /// spends none of the tolerance.
    /// </summary>
    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(96)]
    public void ABoxCollapsesToSixQuads(int resolution)
    {
        var box = Sdf.Box(10, 10, 10);
        var region = new Aabb((-7, -7, -7), (7, 7, 7));
        double cell = 14.0 / resolution;
        var full = SurfaceNets.Polygonize(box, region, resolution);
        var mesh = SurfaceNets.Polygonize(box, region, resolution, null, At(0.001, cell));
        mesh.Validate();

        output.WriteLine($"resolution {resolution}: {full.FaceCount} faces -> {mesh.FaceCount}, " +
                         $"volume {mesh.Volume():F9}");
        Assert.Equal(6, mesh.FaceCount);
        Assert.Equal(8, mesh.VertexCount);
        Assert.True(mesh.IsClosed);
        Assert.Equal(1000.0, mesh.Volume(), 6);
    }

    /// <summary>
    /// The tolerance is a LENGTH and it is honoured monotonically: a tighter one keeps
    /// more faces and a looser one fewer, with the volume error tracking it. A sphere has
    /// no flat region at all, so at a thousandth of a cell NOTHING collapses — which is
    /// the right answer and the one that says the criterion is measuring the surface
    /// rather than counting cells.
    /// </summary>
    [Fact]
    public void TheToleranceIsHonouredMonotonically()
    {
        var sphere = Sdf.Sphere(5);
        var region = new Aabb((-7, -7, -7), (7, 7, 7));
        double cell = 14.0 / 64;
        double exact = 4.0 / 3.0 * Math.PI * 125;

        var full = SurfaceNets.Polygonize(sphere, region, 64);
        int previous = int.MaxValue;
        double previousError = 0;
        foreach (double tolerance in new[] { 0.001, 0.01, 0.05, 0.2 })
        {
            var mesh = SurfaceNets.Polygonize(sphere, region, 64, null, At(tolerance, cell));
            mesh.Validate();
            double error = Math.Abs(mesh.Volume() - exact) / exact;
            output.WriteLine($"tol {tolerance,6} cells: {mesh.FaceCount,6} faces, " +
                             $"{mesh.VertexCount,6} vertices, volume error {error:P4}");
            Assert.True(mesh.FaceCount <= previous, "a looser tolerance must not keep more faces");
            Assert.True(error >= previousError - 1e-12, "…nor be more accurate");
            Assert.True(mesh.IsClosed);
            previous = mesh.FaceCount;
            previousError = error;
        }
        Assert.Equal(full.FaceCount, SurfaceNets
            .Polygonize(sphere, region, 64, null, At(0.001, cell)).FaceCount);
    }

    /// <summary>
    /// Cracks are structurally impossible and this says why in one assertion: the
    /// connectivity is the uniform walk's face buffer RE-INDEXED, never re-derived, so
    /// every surviving face's corners are corners the uniform mesh already had. A T-junction
    /// would need a vertex that is on one face's boundary and not on its neighbour's, and
    /// there is no step here that could invent one.
    /// </summary>
    [Theory]
    [InlineData("box")]
    [InlineData("sphere")]
    [InlineData("csg")]
    [InlineData("shell")]
    public void TheResultIsClosedAndManifold(string name)
    {
        var (field, region) = SurfaceNetsSharpFeatureTests.Case(name);
        double cell = region.Size[region.LongestAxis] / 64;
        foreach (double tolerance in new[] { 0.01, 0.05, 0.2, 1.0 })
        {
            var mesh = SurfaceNets.Polygonize(field, region, 64, null, At(tolerance, cell));
            Assert.True(mesh.IsClosed, $"{name} at tolerance {tolerance} came back open");
            Assert.Empty(mesh.NonManifoldVertices());
            // Every face is a triangle or a quad — a collapse can only shorten a quad.
            foreach (var face in mesh.Faces)
                Assert.InRange(face.Vertices().Count(), 3, 4);
        }
    }

    /// <summary>
    /// A drilled box: the flat faces collapse hard, the bore's rim does not, and the volume
    /// still converges on the closed form. That is what "adaptive" is supposed to mean —
    /// resolution follows the geometry rather than the grid.
    /// </summary>
    [Fact]
    public void ADrilledBoxKeepsItsBoreAndCollapsesItsFaces()
    {
        var field = Sdf.Box(6, 6, 6) - Sdf.Cylinder(2, 9);
        var region = new Aabb((-5, -5, -5), (5, 5, 5));
        double cell = 10.0 / 64;
        double exact = 216 - Math.PI * 4 * 6;

        var full = SurfaceNets.Polygonize(field, region, 64);
        var mesh = SurfaceNets.Polygonize(field, region, 64, null, At(0.05, cell));
        mesh.Validate();

        output.WriteLine($"{full.FaceCount} faces -> {mesh.FaceCount} " +
                         $"({(double)full.FaceCount / mesh.FaceCount:F1}x), " +
                         $"volume error {(mesh.Volume() - exact) / exact:P4}");
        Assert.True(mesh.FaceCount * 5 < full.FaceCount, $"only {full.FaceCount} -> {mesh.FaceCount}");
        Assert.True(Math.Abs(mesh.Volume() - exact) / exact < 0.01);
        // The bore is still round: no vertex of the result may be far off the field.
        Assert.True(mesh.Vertices.Max(v => Math.Abs(field.Evaluate(v.Position))) < 0.05 * cell + 0.05);
    }

    /// <summary>
    /// Determinism, which a clustering pass could lose in three places at once (dictionary
    /// order, the revert loop, and the compaction's numbering). Two runs must agree bit for
    /// bit, positions and indices alike.
    /// </summary>
    [Fact]
    public void TheAdaptivePassIsDeterministic()
    {
        var (field, region) = SurfaceNetsSharpFeatureTests.Case("csg");
        var options = At(0.1, region.Size[region.LongestAxis] / 48);
        var (p1, f1) = SurfaceNets.Polygonize(field, region, 48, null, options).ToIndexed();
        var (p2, f2) = SurfaceNets.Polygonize(field, region, 48, null, options).ToIndexed();

        Assert.Equal(p1, p2);
        Assert.Equal(f1.Count, f2.Count);
        for (int i = 0; i < f1.Count; i++)
            Assert.Equal(f1[i], f2[i]);
    }

    /// <summary>
    /// A level budget of zero leaves the uniform mesh BIT for bit — the neutral setting is
    /// neutral rather than nearly so, which is what makes the compaction's original-order
    /// numbering load-bearing (first-use order would renumber a mesh nothing was done to).
    /// </summary>
    [Fact]
    public void ZeroLevelsIsTheUniformMesh()
    {
        var (field, region) = SurfaceNetsSharpFeatureTests.Case("csg");
        var (uniform, uniformFaces) = SurfaceNets.Polygonize(field, region, 41).ToIndexed();
        var (p, f) = SurfaceNets
            .Polygonize(field, region, 41, null,
                new SurfaceNetsOptions { SimplifyTolerance = 1, MaxSimplifyLevels = 0 })
            .ToIndexed();

        Assert.Equal(uniform, p);
        Assert.Equal(uniformFaces.Count, f.Count);
        for (int i = 0; i < f.Count; i++)
            Assert.Equal(uniformFaces[i], f[i]);
    }

    /// <summary>
    /// A tolerance of ZERO is not the identity and should not be: a cluster whose merged
    /// quadric reads exactly zero has every swallowed plane passing through the merged
    /// point, which is the definition of a collapse that describes the same surface. So it
    /// removes faces (3 316 → 2 711 on this fixture) and moves essentially no geometry.
    /// <para>
    /// "Essentially" is the honest word and the measurement says how much: the volume moves
    /// by 1.9e-6 relative, not by zero, because an exactly-zero reading of a quadratic form
    /// is a ROUND-OFF FLOOR rather than a proof of coplanarity — the error is a difference
    /// of large cancelling products at the minimiser, so planes that are coplanar to within
    /// the arithmetic read exactly 0.0 and are collapsed. That is the right behaviour at a
    /// stated tolerance of zero, and it is why the bar here is a small relative bound with
    /// the number in it rather than an equality that would be a claim about round-off.
    /// </para>
    /// </summary>
    [Fact]
    public void ZeroToleranceIsLosslessRatherThanInert()
    {
        var (field, region) = SurfaceNetsSharpFeatureTests.Case("csg");
        var uniform = SurfaceNets.Polygonize(field, region, 41);
        var mesh = SurfaceNets.Polygonize(
            field, region, 41, null, new SurfaceNetsOptions { SimplifyTolerance = 0 });
        mesh.Validate();

        output.WriteLine($"{uniform.FaceCount} faces -> {mesh.FaceCount}, " +
                         $"volume {uniform.Volume():F12} -> {mesh.Volume():F12}");
        Assert.True(mesh.FaceCount < uniform.FaceCount, "an exactly-zero-error collapse should happen");
        Assert.True(Math.Abs(mesh.Volume() - uniform.Volume()) < 1e-5 * uniform.Volume(),
            $"{uniform.Volume()} -> {mesh.Volume()}");
    }
}
