using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The opt-in "keep only the largest connected component" release filter, verified on a field
/// built to have two disconnected blobs of KNOWN sizes.
///
/// <para>Two passive-solid blocks at the ends with a passive-void gap between them make a
/// density field whose iso-surface is genuinely two components — one bigger than the other. The
/// release then either keeps both (the default, reporting the count) or keeps the larger and
/// drops the smaller, which is the exact volume difference asserted here.</para>
/// </summary>
public sealed class TopologyReleaseComponentTests(ITestOutputHelper output)
{
    /// <summary>A field pinned solid over two end blocks (the first larger than the last) with
    /// a void gap between them, so its iso-surface has two components.</summary>
    private static TopologyResult TwoBlobs()
    {
        var model = TopologyFixtures.Bar();
        double L = TopologyFixtures.BarLength;
        return TopologyOptimizer.Minimize(model, new TopologyOptions
        {
            VolumeFraction = 0.5,
            FilterRadius = 6.0,
            Filter = TopologyFilter.None,     // physical == design, so the pins are exact
            MaxIterations = 1,                // the field is the pins; no search needed
            SolidRegion = c => c.X < L * 0.2 || c.X > L * 0.85,   // 2 cells + 1 cell
            VoidRegion = c => c.X >= L * 0.2 && c.X <= L * 0.85,  // the gap
        });
    }

    [Fact]
    public void TheDefaultKeepsEveryComponentAndReportsTheCount()
    {
        var released = TwoBlobs().Release(
            new TopologyReleaseOptions { Stage = TopologyReleaseStage.IsoSurface });
        output.WriteLine(released.ToText());
        Assert.Equal(2, released.ComponentCount);
        int delivered = MeshConnectedComponents.Find(released.IsoSurface).Count;
        Assert.Equal(2, delivered);
    }

    [Fact]
    public void KeepLargestComponentDropsTheIslandButStillReportsIt()
    {
        var result = TwoBlobs();
        var all = result.Release(
            new TopologyReleaseOptions { Stage = TopologyReleaseStage.IsoSurface });
        var largest = result.Release(new TopologyReleaseOptions
        {
            Stage = TopologyReleaseStage.IsoSurface,
            KeepLargestComponentOnly = true,
        });

        // The extraction still found two, so the caller can see an island was dropped…
        Assert.Equal(2, largest.ComponentCount);
        // …but the delivered surface is now a single blob.
        Assert.Single(MeshConnectedComponents.Find(largest.IsoSurface));

        // The kept blob is the LARGER of the two, and the volume dropped is exactly the smaller.
        var components = MeshConnectedComponents.Find(all.IsoSurface)
            .Select(c => Math.Abs(c.SignedVolume)).OrderByDescending(v => v).ToList();
        output.WriteLine(
            $"components {string.Join(", ", components.Select(v => v.ToString("G4")))}; "
            + $"all vol {all.IsoVolume:G6}, largest-only {largest.IsoVolume:G6}");
        Assert.Equal(components[0], largest.IsoVolume, 1e-6 * components[0]);
        Assert.Equal(components[0] + components[1], all.IsoVolume, 1e-6 * all.IsoVolume);
        Assert.True(largest.IsoVolume < all.IsoVolume, "nothing was dropped");
    }

    [Fact]
    public void KeepLargestIsANoOpOnASingleComponent()
    {
        // A plain optimised bar is one connected block; the flag then changes nothing.
        var single = TopologyOptimizer.Minimize(TopologyFixtures.Bar(),
            new TopologyOptions { VolumeFraction = 0.5, FilterRadius = 6.0 });
        var off = single.Release(new TopologyReleaseOptions { Stage = TopologyReleaseStage.IsoSurface });
        var on = single.Release(new TopologyReleaseOptions
        {
            Stage = TopologyReleaseStage.IsoSurface,
            KeepLargestComponentOnly = true,
        });
        Assert.Equal(1, off.ComponentCount);
        Assert.Equal(off.IsoVolume, on.IsoVolume, 1e-12 * off.IsoVolume);
    }
}
