using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The MBB beam — the canonical topology-optimisation result — and RELEASING it into a
/// usable, exportable solid.
///
/// <para><b>Nothing here asserts that the picture looks like an arch</b>, for the same reason
/// <see cref="TopologyOptimizerTests"/> asserts no truss: a plausible picture is this feature's
/// failure mode. The arch is checked by the one property it must have — left-right SYMMETRY,
/// which a wrong boundary condition breaks — and the release is checked by MEASURED trades:
/// smoothing's volume delta and surface displacement inside a band, remeshing's triangle-shape
/// improvement as a number, and the delivered solid round-tripping through a real STL by
/// signed-tetrahedral volume (which catches a wrong winding or a hole).</para>
/// </summary>
public sealed class TopologyReleaseTests(TopologyReleaseTests.OptimisedMbb fixture, ITestOutputHelper output)
    : IClassFixture<TopologyReleaseTests.OptimisedMbb>
{
    /// <summary>Optimises the MBB beam ONCE and shares the result across the release tests, which
    /// only differ in what they ask <see cref="TopologyResult.Release"/> for.</summary>
    public sealed class OptimisedMbb
    {
        public OptimisedMbb()
        {
            Model = TopologyFixtures.MbbBeam(1, out var mesh);
            Mesh = mesh;
            Result = TopologyOptimizer.Minimize(Model, new TopologyOptions
            {
                VolumeFraction = 0.5,
                FilterRadius = 6.0,
                Filter = TopologyFilter.Sensitivity,
                MaxIterations = 60,
            });
        }

        public StructuralModel Model { get; }
        public AnalysisMesh Mesh { get; }
        public TopologyResult Result { get; }
    }

    // ---- The MBB structure itself ----

    /// <summary>
    /// <b>The optimum is left-right SYMMETRIC — the property the arch must have, and the one a
    /// wrong boundary condition breaks.</b> The symmetric supports make the exact optimum
    /// symmetric, so the binned density and its mirror agree; moving the LOAD off-centre is the
    /// mutation, and it breaks the symmetry by an order of magnitude. Symmetry, not "it looks
    /// like an arch", is the checkable form of "matches the published result".
    /// </summary>
    [Fact]
    public void MbbBeam_OptimisesToASymmetricArch()
    {
        var model = TopologyFixtures.MbbBeam(1, out var mesh);
        var result = TopologyOptimizer.Minimize(model, new TopologyOptions
        {
            VolumeFraction = 0.5, FilterRadius = 6.0, Filter = TopologyFilter.Sensitivity, MaxIterations = 60,
        });

        var binned = TopologyFixtures.MbbBinned(mesh, result.Density);
        double asymmetry = TopologyFixtures.MeanAbsoluteDifference(binned, TopologyFixtures.MirrorX(binned));

        // The arch, drawn from the top row down — for the eye, not the assertion.
        for (int j = 5; j >= 0; j--)
        {
            var row = "  |";
            for (int i = 0; i < 16; i++)
            {
                double d = binned[i + j * 16];
                row += d > 0.6 ? "#" : d > 0.3 ? "+" : d > 0.1 ? "." : " ";
            }
            output.WriteLine(row + "|");
        }
        output.WriteLine($"symmetric arch: mean |density - mirror| = {asymmetry:F4}");

        Assert.True(asymmetry < 0.05, $"the arch is not symmetric: {asymmetry:F4}");

        // The mutation: an off-centre load is not the MBB, and its answer is visibly asymmetric.
        var skewed = TopologyFixtures.MbbBeam(1, out var skewMesh);
        // Add a second load off to one side, breaking the symmetry of the problem.
        skewed.Force(Facets.And(Facets.Tag(StructuredTetMesh.ZMax),
            Facets.InBox(new Aabb((TopologyFixtures.MbbSpan * 0.7, -1, TopologyFixtures.MbbDepth - 1),
                (TopologyFixtures.MbbSpan * 0.9, TopologyFixtures.MbbThickness + 1, TopologyFixtures.MbbDepth + 1)))),
            new Vector3d(0, 0, -3000));
        var skewResult = TopologyOptimizer.Minimize(skewed, new TopologyOptions
        {
            VolumeFraction = 0.5, FilterRadius = 6.0, Filter = TopologyFilter.Sensitivity, MaxIterations = 60,
        });
        var skewBinned = TopologyFixtures.MbbBinned(skewMesh, skewResult.Density);
        double skewAsymmetry = TopologyFixtures.MeanAbsoluteDifference(skewBinned, TopologyFixtures.MirrorX(skewBinned));
        output.WriteLine($"off-centre load asymmetry = {skewAsymmetry:F4}");
        Assert.True(skewAsymmetry > 3 * asymmetry, $"{skewAsymmetry:F4} against {asymmetry:F4}");
    }

    /// <summary>
    /// <b>Refining the MBB mesh at FIXED <c>r_min</c> converges on the same structure</b> — the
    /// second, stronger fixture the filter's mesh-independence is measured on (the cantilever is
    /// the first). Compared in the one grid two meshes share.
    /// </summary>
    [Fact]
    public void MbbBeam_RefiningAtFixedRadius_KeepsTheStructure()
    {
        double[] Run(int level)
        {
            var model = TopologyFixtures.MbbBeam(level, out var mesh);
            var result = TopologyOptimizer.Minimize(model, new TopologyOptions
            {
                VolumeFraction = 0.5, FilterRadius = 6.0, Filter = TopologyFilter.Density, MaxIterations = 120,
            });
            return TopologyFixtures.MbbBinned(mesh, result.Density);
        }

        var levels = new[] { Run(0), Run(1), Run(2) };
        double first = TopologyFixtures.MeanAbsoluteDifference(levels[0], levels[1]);
        double second = TopologyFixtures.MeanAbsoluteDifference(levels[1], levels[2]);
        output.WriteLine($"MBB refinement: {first:F5} then {second:F5}");
        Assert.True(first < 0.08, $"coarse-to-mid {first:F5}");
        Assert.True(second < 0.06, $"mid-to-fine {second:F5}");
    }

    // ---- Releasing the result ----

    /// <summary>
    /// <b>The IsoSurface stage is the extracted level set, verbatim.</b> No smoothing, no
    /// remesh: the delivered mesh IS <see cref="TopologyResult.ExtractSurface"/>, so its volume
    /// is the iso-surface's and nothing has moved.
    /// </summary>
    [Fact]
    public void Release_IsoSurfaceStage_IsTheExactLevelSet()
    {
        var released = fixture.Result.Release(new TopologyReleaseOptions
        {
            Stage = TopologyReleaseStage.IsoSurface,
        });
        Assert.True(released.IsClosed);
        Assert.Equal(released.IsoVolume, released.FinalVolume);
        Assert.Equal(0, released.SmoothingMaxDisplacement);
        Assert.Equal(0, released.SurfaceMaxDisplacementFromIso);
        Assert.Equal(
            MeshMassProperties.Compute(fixture.Result.ExtractSurface(0.5)).Volume,
            released.FinalVolume, 9);
    }

    /// <summary>
    /// <b>Smoothing MOVES the surface, within a measured band — the trade a reader must be able
    /// to see rather than be told about.</b> Fairing a stair-stepped thin structure shrinks it
    /// (the steps are material on the convex side), so the volume delta is negative and bounded,
    /// and the max displacement is a fraction of an element. Both are asserted inside a band, not
    /// eyeballed — a smoothing that silently melts the part is the failure mode this guards.
    /// </summary>
    [Fact]
    public void Release_SmoothingMovesTheSurfaceWithinAStatedBand()
    {
        var released = fixture.Result.Release(new TopologyReleaseOptions
        {
            Stage = TopologyReleaseStage.Smoothed,
        });
        output.WriteLine(released.ToText());

        Assert.True(released.IsClosed);
        // The default is gentle: it fairs the steps for a few percent of volume, not a melt.
        double shrink = -released.SmoothingVolumeDelta / released.IsoVolume;
        Assert.InRange(shrink, 0.005, 0.15);
        // The surface moved off the iso-surface, but by a fraction of the element size (6), not
        // the wholesale collapse that TimeStep 1 would produce.
        Assert.InRange(released.SmoothingMaxDisplacement, 0.05, 2.0);
        Assert.True(released.SmoothingMeanDisplacement < released.SmoothingMaxDisplacement);
        // Nothing but smoothing ran, so the delivered surface is the smoothed one exactly.
        Assert.Equal(released.SmoothedVolume, released.FinalVolume);
        Assert.Equal(released.SmoothingMaxDisplacement, released.SurfaceMaxDisplacementFromIso);
    }

    /// <summary>
    /// <b>Remeshing improves triangle SHAPE without moving the surface — its whole benefit is a
    /// number, and it is the number.</b> The iso-surface is a mess of slivers; the remesh drops
    /// the sliver count by a large factor and lifts the mean smallest-angle, while staying on the
    /// faired surface to round-off (it redistributes vertices, it does not fair). The volume
    /// barely changes.
    /// </summary>
    [Fact]
    public void Release_RemeshingImprovesTriangleShape_WithoutMovingTheSurface()
    {
        var released = fixture.Result.Release();  // full pipeline
        output.WriteLine(released.ToText());
        output.WriteLine($"iso mean-min angle {released.IsoSurfaceQuality.MeanMinAngleDegrees:F1}, "
            + $"final {released.FinalQuality.MeanMinAngleDegrees:F1}");

        var iso = released.IsoSurfaceQuality;
        var final = released.FinalQuality;
        // The headline: far fewer slivers, and a much better mean triangle.
        Assert.True(final.SliverCount < 0.3 * iso.SliverCount,
            $"slivers {iso.SliverCount} -> {final.SliverCount}");
        Assert.True(final.MeanMinAngleDegrees > iso.MeanMinAngleDegrees + 15,
            $"mean min angle {iso.MeanMinAngleDegrees:F1} -> {final.MeanMinAngleDegrees:F1}");
        Assert.True(final.MeanRadiusRatio > iso.MeanRadiusRatio + 0.1,
            $"mean radius ratio {iso.MeanRadiusRatio:F3} -> {final.MeanRadiusRatio:F3}");
        // Remeshing REDISTRIBUTES onto the smoothed surface — it does not move it. The remeshed
        // vertices sit on the smoothed surface to round-off, so the whole extra shape change
        // between smoothed and delivered is negligible against the model.
        Assert.True(released.RemeshingMaxDisplacement < 1e-6 * TopologyFixtures.MbbSpan,
            $"remesh moved the surface by {released.RemeshingMaxDisplacement:G4}");
        Assert.True(Math.Abs(released.RemeshingVolumeDelta) < 0.1 * released.SmoothedVolume);
    }

    /// <summary>
    /// <b>The delivered solid is <c>Validate</c>-clean, closed, and exports to a REAL STL whose
    /// signed-tetrahedral volume round-trips.</b> This is the STL-writer oracle: decode the
    /// bytes, sum the signed tetrahedral volume, and it must equal the delivered volume — one
    /// assertion that catches a wrong winding (the volume would flip sign) or a hole (it would
    /// not close). The triangle count is asserted too, so a silently empty export fails.
    /// </summary>
    [Fact]
    public void Release_DeliversAValidateCleanClosedSolidThatExportsToStl()
    {
        var released = fixture.Result.Release();
        // Validate throws on a non-manifold or torn mesh; the release must not produce one.
        released.Mesh.Validate();
        Assert.True(released.IsClosed);
        Assert.True(released.Mesh.FaceCount > 100, $"only {released.Mesh.FaceCount} faces");

        using var stream = new MemoryStream();
        StlWriter.Write(released.Mesh, stream);
        Assert.True(stream.Length > 84, "STL smaller than its own header");
        stream.Position = 0;
        var readback = StlReader.Read(stream).RequireMesh();

        // The oracle: sum the signed tetrahedral volume of the decoded facets. A wrong winding
        // flips its sign; a hole leaves it open and wrong.
        double writtenVolume = MeshMassProperties.Compute(readback).Volume;
        output.WriteLine($"STL {stream.Length} bytes, readback volume {writtenVolume:G8} "
            + $"vs delivered {released.FinalVolume:G8}");
        Assert.True(readback.IsClosed, "the exported STL is not a closed solid");
        Assert.True(writtenVolume > 0, "the exported STL is inside-out (negative volume)");
        // Binary STL is float32, so the round-trip is exact only to float precision — a relative
        // check at that grade, which still catches a wrong winding (sign flip) or a hole
        // (open, wrong magnitude). Measured relative error is ~1e-8.
        Assert.True(Math.Abs(writtenVolume - released.FinalVolume) / released.FinalVolume < 1e-5,
            $"{writtenVolume:G10} against {released.FinalVolume:G10}");
    }

    /// <summary>
    /// <b>The delivered volume is compared to the extracted one, so the trade is a NUMBER, and
    /// the two stage deltas add up to it exactly.</b> Releasing spends volume in two places —
    /// fairing (a lot) and remeshing (a little) — and their sum is the whole difference between
    /// the iso-surface and the deliverable, an identity a caller can read off rather than
    /// discover.
    /// </summary>
    [Fact]
    public void Release_DeliveredVolumeIsAccountedForByTheTwoStages()
    {
        var released = fixture.Result.Release();
        Assert.True(released.FinalVolume < released.IsoVolume, "the release should remove, not add, material");
        Assert.Equal(
            released.IsoVolume + released.SmoothingVolumeDelta + released.RemeshingVolumeDelta,
            released.FinalVolume, 6);
        output.WriteLine($"iso {released.IsoVolume:G6} -> smoothed {released.SmoothedVolume:G6} "
            + $"-> delivered {released.FinalVolume:G6} ({released.VolumeFraction:P2})");
    }

    /// <summary>
    /// <b>Islands are REPORTED, not silently dropped.</b> Extraction marches every tetrahedron
    /// above the threshold, so a field with a disconnected blob comes back as more than one
    /// component — and this counts them, so a caller can keep the largest rather than have the
    /// release quietly delete a floating member. Built as a hand-crafted field with two separated
    /// solid regions, so the count is a fact rather than luck.
    /// </summary>
    [Fact]
    public void Release_ReportsIslandsRatherThanDroppingThem()
    {
        var model = TopologyFixtures.Bar();
        var mesh = model.Mesh;
        var options = new TopologyOptions { VolumeFraction = 0.5, FilterRadius = 8.0 };
        var (_, volumes) = TopologyOptimizer.BuildEvaluator(model, options);
        // Two disconnected solid blocks: x < 0.3L and x > 0.7L are full, the middle is void.
        var density = new double[mesh.ElementCount];
        for (int e = 0; e < density.Length; e++)
        {
            var nodes = mesh.Element(e);
            double x = 0.25 * (mesh.Position(nodes[0]).X + mesh.Position(nodes[1]).X
                + mesh.Position(nodes[2]).X + mesh.Position(nodes[3]).X);
            density[e] = x < 0.3 * TopologyFixtures.BarLength || x > 0.7 * TopologyFixtures.BarLength
                ? 1.0 : 1e-3;
        }
        var result = new TopologyResult(
            model.Mesh, model, null, options, density, density, volumes, [], TopologyStop.Converged, 1, 1);

        var released = result.Release(new TopologyReleaseOptions { Stage = TopologyReleaseStage.IsoSurface });
        output.WriteLine($"components {released.ComponentCount}, iso faces {released.IsoSurface.FaceCount}");
        Assert.Equal(2, released.ComponentCount);
        // Neither island was dropped: the surface bounds both blocks.
        var components = MeshConnectedComponents.Find(released.IsoSurface);
        Assert.All(components, c => Assert.True(c.SignedVolume > 0));
    }

    /// <summary>
    /// <b>A guard fires: a threshold no material reaches is refused by name</b> rather than
    /// producing an empty mesh. The message is <see cref="TopologyResult.ExtractSurface"/>'s, so
    /// the release inherits the extraction's contract rather than restating it.
    /// </summary>
    [Fact]
    public void Release_AboveEveryDensity_IsRefusedByName()
    {
        // A uniform low field: no node reaches the threshold, so the level set is empty.
        var model = TopologyFixtures.Bar();
        var options = new TopologyOptions { VolumeFraction = 0.5, FilterRadius = 8.0 };
        var (_, volumes) = TopologyOptimizer.BuildEvaluator(model, options);
        var field = new double[model.Mesh.ElementCount];
        Array.Fill(field, 0.2);
        var result = new TopologyResult(
            model.Mesh, model, null, options, field, field, volumes, [], TopologyStop.Converged, 1, 1);

        var ex = Assert.Throws<FeaException>(() =>
            result.Release(new TopologyReleaseOptions { Threshold = 0.9 }));
        Assert.Contains("No material survives", ex.Message);
    }
}
