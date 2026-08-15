using EngrCAD.Cam;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// Variable layer heights: an explicit bottom-up table, and the adaptive schedule from
/// the stair-step cusp criterion. The identity with teeth is that the slicer's
/// flow-and-height-aware filament total matches the decoder's — the per-layer bead
/// areas agreeing between model and file.
/// </summary>
public class FdmLayerHeightTests
{
    [Fact]
    public void AnExplicitTable_SetsEachLayersOwnHeight()
    {
        var profile = new PrinterProfile(NozzleDiameter: 0.8, LayerHeight: 0.4,
            WallCount: 1, InfillDensity: 0.2);
        var table = Enumerable.Repeat(0.2, 5).Concat(Enumerable.Repeat(0.5, 6)).ToList();
        var sliced = FdmSlicer.Slice(Shape.Box(10, 10, 4), profile, layerHeights: table);

        Assert.Equal(11, sliced.Layers.Count);
        Assert.Equal(-2 + 0.2, sliced.Layers[0].Z, 12);
        Assert.Equal(-2 + 1.0, sliced.Layers[4].Z, 12);
        Assert.Equal(2, sliced.Layers[^1].Z, 9); // 5×0.2 + 6×0.5 = 4 exactly
        Assert.Equal(0.2, sliced.Layers[0].HeightOr(profile), 12);
        Assert.Equal(0.5, sliced.Layers[^1].HeightOr(profile), 12);

        // The identity, per layer: the writer's E arithmetic reads each layer's own
        // stadium, and the decoder's total matches the model's.
        var decoded = GcodeReader.Read(GcodeWriter.Write(sliced));
        Assert.Equal(sliced.FilamentUsed, decoded.FilamentUsed, sliced.FilamentUsed * 1e-3);
        // A thin layer really deposits less: the global single-ratio identity must FAIL.
        double naive = decoded.DepositionLength * profile.BeadArea / profile.FilamentArea;
        Assert.True(Math.Abs(naive - decoded.FilamentUsed) > decoded.FilamentUsed * 0.02,
            "mixed heights must show in the E arithmetic, or the table did nothing");
    }

    [Fact]
    public void AShortOrUnprintableTable_RefusesByName()
    {
        var profile = new PrinterProfile(NozzleDiameter: 0.8, LayerHeight: 0.4);
        Assert.Contains("short", Assert.Throws<ArgumentException>(() =>
            FdmSlicer.Slice(Shape.Box(10, 10, 4), profile,
                layerHeights: [0.5, 0.5])).Message);
        Assert.Contains("bead", Assert.Throws<ArgumentException>(() =>
            FdmSlicer.Slice(Shape.Box(10, 10, 4), profile,
                layerHeights: [1.5, 1.5, 1.5])).Message);
    }

    [Fact]
    public void AdaptiveHeights_ThinWhereTheSurfaceFlattens()
    {
        // A sphere: vertical at the equator (max height), flattening toward the poles
        // (the cusp criterion thins the layers there).
        var heights = FdmSlicer.AdaptiveLayerHeights(
            Shape.Sphere(8), minHeight: 0.1, maxHeight: 0.4, cuspHeight: 0.1);

        Assert.True(heights.Sum() >= 16 - 1e-9, "the schedule must cover the part");
        int mid = heights.Count / 2;
        Assert.Equal(0.4, heights[mid], 9); // the equator is vertical: the maximum
        Assert.True(heights[0] < 0.4, "the bottom pole flattens: thinner layers");
        Assert.True(heights[^1] < 0.4, "the top pole flattens: thinner layers");
        Assert.InRange(heights[^1], 0.1, 0.2);

        // The schedule slices, and the per-layer identity holds through the decoder.
        var profile = new PrinterProfile(NozzleDiameter: 0.8, LayerHeight: 0.4,
            WallCount: 1, InfillDensity: 0);
        var sliced = FdmSlicer.Slice(Shape.Sphere(8), profile, layerHeights: heights);
        Assert.Equal(heights.Count, sliced.Layers.Count);
        var decoded = GcodeReader.Read(GcodeWriter.Write(sliced));
        Assert.Equal(sliced.FilamentUsed, decoded.FilamentUsed, sliced.FilamentUsed * 1e-3);

        Assert.Contains("cuspHeight", Assert.Throws<ArgumentException>(() =>
            FdmSlicer.AdaptiveLayerHeights(Shape.Sphere(8), 0.1, 0.4, 0)).Message);
    }
}
