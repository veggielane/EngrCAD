using EngrCAD.Cam;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// Solid top/bottom shells. The fixture with teeth is the STEP: a plateau whose top is
/// exposed on one side and carries a tower on the other, so the solid/sparse split must
/// land exactly at the tower's wall — a slicer that shells whole layers or none passes a
/// plain box and fails here.
/// </summary>
public class FdmShellTests
{
    private static PrinterProfile Shelled(int top, int bottom) => new(
        NozzleDiameter: 0.8, LayerHeight: 0.5, WallCount: 1, InfillDensity: 0.2,
        TopSolidLayers: top, BottomSolidLayers: bottom);

    /// <summary>A 20×20×4 plateau carrying a 10×20 tower on its left half (x ≤ 0) up to
    /// z = 8: the plateau's top is exposed for x ≥ 0 only.</summary>
    private static Shape Step() =>
        Shape.Box(20, 20, 4).Translate(0, 0, 2) | Shape.Box(10, 20, 8).Translate(-5, 0, 4);

    [Fact]
    public void TheStep_SplitsSolidFromSparse_AtTheTowerWall()
    {
        var sliced = FdmSlicer.Slice(Step(), Shelled(top: 2, bottom: 0));

        // Layers 6 and 7 sit within two layers of the exposed plateau top: solid skin on
        // the exposed half (x >= 0), sparse under the tower (x <= 0) — the split exactly
        // at the tower's wall, which is what the neighbour-difference computes.
        foreach (int index in new[] { 6, 7 })
        {
            var layer = sliced.Layers[index];
            var solid = layer.Paths.Where(p => p.Role == SlicePathRole.SolidInfill)
                .SelectMany(p => p.Points).ToList();
            var sparse = layer.Paths.Where(p => p.Role == SlicePathRole.Infill)
                .SelectMany(p => p.Points).ToList();
            Assert.True(solid.Count > 10, $"layer {index} must carry solid skin");
            Assert.True(sparse.Count > 0, $"layer {index} must keep sparse fill under the tower");
            Assert.All(solid, p => Assert.True(p.X >= -1e-6,
                $"solid point at x={p.X:0.###} on layer {index} is under the tower"));
            Assert.All(sparse, p => Assert.True(p.X <= 1e-6,
                $"sparse point at x={p.X:0.###} on layer {index} is on the exposed half"));
        }

        // A layer safely below the plateau top is covered by full layers above: no skin.
        Assert.DoesNotContain(sliced.Layers[4].Paths, p => p.Role == SlicePathRole.SolidInfill);

        // The tower's own top two layers are wholly solid (nothing above covers them).
        foreach (int index in new[] { 14, 15 })
        {
            var layer = sliced.Layers[index];
            Assert.Contains(layer.Paths, p => p.Role == SlicePathRole.SolidInfill);
            Assert.DoesNotContain(layer.Paths, p => p.Role == SlicePathRole.Infill);
        }
    }

    [Fact]
    public void BottomLayers_AreWhollySolid_AndDensityZeroStillGetsSkins()
    {
        var sliced = FdmSlicer.Slice(Shape.Box(16, 12, 5), Shelled(top: 2, bottom: 2));
        foreach (int index in new[] { 0, 1 })
        {
            Assert.Contains(sliced.Layers[index].Paths, p => p.Role == SlicePathRole.SolidInfill);
            Assert.DoesNotContain(sliced.Layers[index].Paths, p => p.Role == SlicePathRole.Infill);
        }
        Assert.Contains(sliced.Layers[5].Paths, p => p.Role == SlicePathRole.Infill);

        // Zero sparse density still lays the skins — a vase-like part keeps its lids.
        var hollow = FdmSlicer.Slice(Shape.Box(16, 12, 5),
            Shelled(top: 1, bottom: 1) with { InfillDensity = 0 });
        Assert.Contains(hollow.Layers[0].Paths, p => p.Role == SlicePathRole.SolidInfill);
        Assert.DoesNotContain(hollow.Layers[4].Paths, p => p.Role == SlicePathRole.Infill);
        Assert.Contains(hollow.Layers[^1].Paths, p => p.Role == SlicePathRole.SolidInfill);
    }

    [Fact]
    public void SolidSkin_FillsAtTheBeadSpacing()
    {
        // On a wholly solid layer, adjacent scan lines sit one bead apart: the covered
        // area (length x bead) approaches the core's own area the way the stage-1
        // coverage test measures.
        var sliced = FdmSlicer.Slice(Shape.Box(16, 12, 5), Shelled(top: 1, bottom: 1));
        var bottom = sliced.Layers[0];
        double skinLength = bottom.Paths
            .Where(p => p.Role == SlicePathRole.SolidInfill).Sum(p => p.Length);
        double core = (16 - 2 * 1.2) * (12 - 2 * 1.2); // inset by walls + half-bead
        double ratio = skinLength * 0.8 / core;
        Assert.InRange(ratio, 0.70, 1.02);
    }

    [Fact]
    public void ShellsOff_IsByteIdentical_AndTheIdentityHoldsWithThemOn()
    {
        var plain = new PrinterProfile(NozzleDiameter: 0.8, LayerHeight: 0.5, WallCount: 1,
            InfillDensity: 0.2);
        Assert.Equal(
            GcodeWriter.Write(FdmSlicer.Slice(Step(), plain)),
            GcodeWriter.Write(FdmSlicer.Slice(Step(),
                plain with { TopSolidLayers = 0, BottomSolidLayers = 0 })));

        var shelled = FdmSlicer.Slice(Step(), Shelled(top: 2, bottom: 2));
        string gcode = GcodeWriter.Write(shelled);
        Assert.Equal(gcode, GcodeWriter.Write(FdmSlicer.Slice(Step(), Shelled(top: 2, bottom: 2))));

        var decoded = GcodeReader.Read(gcode);
        double identity = decoded.DepositionLength
            * shelled.Profile.BeadArea / shelled.Profile.FilamentArea;
        Assert.Equal(identity, decoded.FilamentUsed, identity * 1e-3);
    }

    [Fact]
    public void NegativeShellCounts_RefuseByName()
    {
        Assert.Contains("TopSolidLayers", Assert.Throws<ArgumentException>(() =>
            FdmSlicer.Slice(Step(), Shelled(top: -1, bottom: 0))).Message);
        Assert.Contains("BottomSolidLayers", Assert.Throws<ArgumentException>(() =>
            FdmSlicer.Slice(Step(), Shelled(top: 0, bottom: -2))).Message);
    }
}
