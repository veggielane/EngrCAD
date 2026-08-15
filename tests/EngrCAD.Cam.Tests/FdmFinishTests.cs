using EngrCAD.Cam;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// The FDM finish wave: infill patterns, the raft, the support Z-gap and interface
/// layers, enforcer/blocker shapes, bridges, monotonic skins, ironing and the
/// dimensional compensations — each with the assertion that has teeth for it, and the
/// unset profile byte-identical throughout (covered by the incumbent suite).
/// </summary>
public class FdmFinishTests
{
    private static Shape Plate() => Shape.Box(20, 15, 4) - Shape.Cylinder(3, 10);

    private static Shape Table() =>
        Shape.Box(4, 10, 8).Translate(0, 0, 4) | Shape.Box(20, 10, 2).Translate(0, 0, 9);

    [Fact]
    public void InfillPatterns_HoldTheirDensity_AndTheirShapes()
    {
        var box = Shape.Box(20, 20, 2);
        double reference = Sliced(InfillPattern.Rectilinear).Layers[2].Paths
            .Where(p => p.Role == SlicePathRole.Infill).Sum(p => p.Length);
        Assert.True(reference > 50);

        // Every pattern lays roughly the SAME material at one stated density — the
        // spacing-scales-with-direction-count rule, measured rather than asserted.
        foreach (var pattern in new[]
        {
            InfillPattern.Grid, InfillPattern.Triangles, InfillPattern.Concentric,
            InfillPattern.Gyroid, InfillPattern.Hilbert,
        })
        {
            double length = Sliced(pattern).Layers[2].Paths
                .Where(p => p.Role == SlicePathRole.Infill).Sum(p => p.Length);
            Assert.InRange(length / reference, 0.6, 1.7);
        }

        // Shape claims: grid lays BOTH directions on one layer; gyroid changes with z
        // (it is a 3D surface); concentric is closed loops; every pattern stays inside
        // the innermost wall's core.
        var grid = Sliced(InfillPattern.Grid).Layers[2].Paths
            .Where(p => p.Role == SlicePathRole.Infill).ToList();
        var directions = grid.Select(p => Math.Abs(
            Math.Atan2(p.Points[^1].Y - p.Points[0].Y, p.Points[^1].X - p.Points[0].X)))
            .Distinct().Count();
        Assert.True(directions >= 2, "grid must lay two directions on one layer");

        var gyroid = Sliced(InfillPattern.Gyroid);
        Assert.NotEqual(
            gyroid.Layers[1].Paths.Where(p => p.Role == SlicePathRole.Infill).Sum(p => p.Length),
            gyroid.Layers[4].Paths.Where(p => p.Role == SlicePathRole.Infill).Sum(p => p.Length),
            5);

        Assert.All(Sliced(InfillPattern.Concentric).Layers[2].Paths
            .Where(p => p.Role == SlicePathRole.Infill), p => Assert.True(p.IsClosed));

        SlicedPart Sliced(InfillPattern pattern) => FdmSlicer.Slice(box, new PrinterProfile(
            NozzleDiameter: 0.8, LayerHeight: 0.4, WallCount: 1, InfillDensity: 0.25,
            InfillPattern: pattern));
    }

    [Fact]
    public void TheRaft_LiftsThePart_AndCarriesTheAdhesion()
    {
        var profile = new PrinterProfile(NozzleDiameter: 0.8, LayerHeight: 0.5,
            InfillDensity: 0, RaftLayers: 2, RaftMargin: 3, SkirtLoops: 1);
        var sliced = FdmSlicer.Slice(Shape.Box(10, 10, 4), profile);

        // Two raft layers first (solid fill grown by the margin), then the part LIFTED
        // by the raft's height while its geometry stands still.
        Assert.All(sliced.Layers.Take(2).SelectMany(l => l.Paths)
            .Where(p => p.Role is SlicePathRole.Raft), _ => { });
        Assert.Contains(sliced.Layers[0].Paths, p => p.Role == SlicePathRole.Raft);
        Assert.Contains(sliced.Layers[0].Paths, p => p.Role == SlicePathRole.Skirt);
        Assert.Contains(sliced.Layers[1].Paths, p => p.Role == SlicePathRole.Raft);
        Assert.DoesNotContain(sliced.Layers[2].Paths, p => p.Role == SlicePathRole.Raft);

        // The raft footprint spans the part plus the margin.
        double raftMaxX = sliced.Layers[0].Paths
            .Where(p => p.Role == SlicePathRole.Raft).SelectMany(p => p.Points).Max(p => p.X);
        Assert.InRange(raftMaxX, 7.5, 8.01);

        var plain = FdmSlicer.Slice(Shape.Box(10, 10, 4), profile with { RaftLayers = 0, SkirtLoops = 0 });
        Assert.Equal(plain.Layers[0].Z + 2 * 0.5, sliced.Layers[2].Z, 9);
        Assert.Equal(plain.Layers.Count + 2, sliced.Layers.Count);
    }

    [Fact]
    public void SupportZGap_LeavesAirUnderTheOverhang_AndInterfaceDensifies()
    {
        var basic = new PrinterProfile(NozzleDiameter: 0.8, LayerHeight: 0.5,
            InfillDensity: 0, SupportOverhangAngle: 45);
        double underside = 8;

        // With a one-layer Z gap the last supported layer stops one layer short.
        var gapped = FdmSlicer.Slice(Table(), basic with { SupportZGap = 0.5 });
        double lastGapped = gapped.Layers
            .Last(l => l.Paths.Any(p => p.Role == SlicePathRole.Support)).Z;
        Assert.Equal(underside - 0.5, lastGapped, 9);

        // Interface layers densify near the contact: the topmost supported layer's line
        // count rises against the same layer without interfaces.
        var interfaced = FdmSlicer.Slice(Table(), basic with { SupportInterfaceLayers = 2 });
        var plain = FdmSlicer.Slice(Table(), basic);
        int Lines(SlicedPart s, double z) => s.Layers.First(l => Math.Abs(l.Z - z) < 1e-9)
            .Paths.Count(p => p.Role == SlicePathRole.Support);
        Assert.True(Lines(interfaced, underside) > Lines(plain, underside),
            "the interface must lay more, denser lines near the contact");
        Assert.Equal(Lines(interfaced, 2), Lines(plain, 2)); // far below: unchanged
    }

    [Fact]
    public void BlockersMask_AndEnforcersForce()
    {
        var basic = new PrinterProfile(NozzleDiameter: 0.8, LayerHeight: 0.5,
            InfillDensity: 0, SupportOverhangAngle: 45);

        // A blocker over the slab's right half removes exactly those columns.
        var blocked = FdmSlicer.Slice(Table(), basic, supportModifiers: new FdmSupportModifiers(
            Blockers: [Shape.Box(12, 12, 10).Translate(6, 0, 4)]));
        var points = blocked.Layers.SelectMany(l => l.Paths)
            .Where(p => p.Role == SlicePathRole.Support).SelectMany(p => p.Points).ToList();
        Assert.True(points.Count > 0, "the unblocked half keeps its supports");
        Assert.All(points, q => Assert.True(q.X <= 0 + 1e-6,
            $"a support at x={q.X:0.##} stands inside the blocker"));

        // An enforcer forces support under a 30-degree chamfer a 45-degree threshold
        // ignores: the mutation that proves the enforcer acts.
        var wedge = Shape.Extrude(
            Sketch.Polygon([new Vector2d(0, 10), new Vector2d(10, 0), new Vector2d(10, 10)]),
            10, SketchPlane.At(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ));
        var none = FdmSlicer.Slice(wedge, basic with { SupportOverhangAngle = 50 });
        Assert.DoesNotContain(none.Layers.SelectMany(l => l.Paths),
            p => p.Role == SlicePathRole.Support);
        var forced = FdmSlicer.Slice(wedge, basic with { SupportOverhangAngle = 50 },
            supportModifiers: new FdmSupportModifiers(
                Enforcers: [Shape.Box(30, 30, 30).Translate(5, -5, 5)]));
        Assert.Contains(forced.Layers.SelectMany(l => l.Paths),
            p => p.Role == SlicePathRole.Support);
    }

    [Fact]
    public void Bridges_SpanTheAir_AlongTheLongAxis()
    {
        // The table's slab underside is air over the gap beside the column: with
        // DetectBridges the first slab layer's unsupported skin becomes Bridge runs laid
        // along the slab's long axis (x), where the plain slice calls them skin.
        var profile = new PrinterProfile(NozzleDiameter: 0.8, LayerHeight: 0.5,
            InfillDensity: 0.15, TopSolidLayers: 1, BottomSolidLayers: 1,
            DetectBridges: true, BridgeSpeed: 25);
        var sliced = FdmSlicer.Slice(Table(), profile);
        var slabFirst = sliced.Layers.First(l => Math.Abs(l.Z - 8.5) < 1e-9);
        var bridges = slabFirst.Paths.Where(p => p.Role == SlicePathRole.Bridge).ToList();
        Assert.True(bridges.Count > 3, "the slab's underside must bridge");
        // Long-axis fill: every bridge run is horizontal (constant y).
        Assert.All(bridges, b => Assert.Equal(b.Points[0].Y, b.Points[^1].Y, 9));

        var off = FdmSlicer.Slice(Table(), profile with { DetectBridges = false });
        Assert.DoesNotContain(
            off.Layers.SelectMany(l => l.Paths), p => p.Role == SlicePathRole.Bridge);
    }

    [Fact]
    public void MonotonicSkins_KeepScanOrderAndOneDirection()
    {
        var profile = new PrinterProfile(NozzleDiameter: 0.8, LayerHeight: 0.5,
            WallCount: 1, InfillDensity: 0.2, TopSolidLayers: 1, BottomSolidLayers: 1,
            MonotonicSkins: true);
        var top = FdmSlicer.Slice(Shape.Box(16, 12, 4), profile).Layers[^1];
        var skins = top.Paths.Where(p => p.Role == SlicePathRole.SolidInfill).ToList();
        Assert.True(skins.Count > 5);
        // ONE direction: every run points the same way as the first (never reversed by a
        // linker); scan ORDER: starts march monotonically along the scan axis.
        var d0 = (skins[0].Points[^1] - skins[0].Points[0]).Normalized();
        var scanAxis = new Vector2d(-d0.Y, d0.X);
        Assert.All(skins, s => Assert.True(
            (s.Points[^1] - s.Points[0]).Dot(d0) > 0, "a skin run was reversed"));
        for (int i = 1; i < skins.Count; i++)
            Assert.True(skins[i].Points[0].Dot(scanAxis)
                >= skins[i - 1].Points[0].Dot(scanAxis) - 1e-9,
                "monotonic skins must keep their scanline order");
    }

    [Fact]
    public void Ironing_SweepsOnlyTheExposedTop_AtItsStatedFlow()
    {
        var profile = new PrinterProfile(NozzleDiameter: 0.8, LayerHeight: 0.5,
            WallCount: 1, InfillDensity: 0.2, TopSolidLayers: 1, BottomSolidLayers: 0,
            IroningFlow: 0.15);
        var sliced = FdmSlicer.Slice(Shape.Box(16, 12, 4), profile);

        var top = sliced.Layers[^1].Paths.Where(p => p.Role == SlicePathRole.Ironing).ToList();
        Assert.True(top.Count > 10, "the exposed top must be ironed");
        Assert.All(top, i => Assert.Equal(0.15, i.Flow));
        Assert.DoesNotContain(sliced.Layers[1].Paths, p => p.Role == SlicePathRole.Ironing);

        // The decoder sees the reduced flow: total filament is LESS than the plain
        // identity by exactly the ironing paths' flow deficit.
        var decoded = GcodeReader.Read(GcodeWriter.Write(sliced));
        double expected = sliced.Layers.SelectMany(l => l.Paths)
            .Sum(p => p.Length * p.Flow) * profile.BeadArea / profile.FilamentArea;
        Assert.Equal(expected, decoded.FilamentUsed, expected * 1e-3);

        Assert.Contains("TopSolidLayers", Assert.Throws<ArgumentException>(() =>
            FdmSlicer.Slice(Shape.Box(16, 12, 4),
                profile with { TopSolidLayers = 0 })).Message);
    }

    [Fact]
    public void Compensations_MoveTheDimensionsTheyName()
    {
        var basic = new PrinterProfile(NozzleDiameter: 0.8, LayerHeight: 0.5, WallCount: 1,
            InfillDensity: 0);

        // Elephant foot: layer 0's outer wall pulls in by the stated amount; layer 2 not.
        var elephant = FdmSlicer.Slice(Plate(), basic with { ElephantFootCompensation = 0.3 });
        var plain = FdmSlicer.Slice(Plate(), basic);
        double OuterMaxX(SlicedPart s, int layer) => s.Layers[layer].Paths
            .Where(p => p.Role == SlicePathRole.Wall).SelectMany(p => p.Points).Max(q => q.X);
        Assert.Equal(OuterMaxX(plain, 0) - 0.3, OuterMaxX(elephant, 0), 9);
        Assert.Equal(OuterMaxX(plain, 2), OuterMaxX(elephant, 2), 9);

        // XY compensation grows every layer; hole compensation grows only the bore.
        var xy = FdmSlicer.Slice(Plate(), basic with { XYCompensation = 0.2 });
        Assert.Equal(OuterMaxX(plain, 2) + 0.2, OuterMaxX(xy, 2), 9);

        double BoreRadius(SlicedPart s)
        {
            var bore = s.Layers[2].Paths.Where(p => p.Role == SlicePathRole.Wall
                && p.Points.All(q => q.Length < 6)).SelectMany(p => p.Points);
            return bore.Max(q => q.Length);
        }
        var hole = FdmSlicer.Slice(Plate(), basic with { HoleCompensation = 0.25 });
        Assert.Equal(BoreRadius(plain) + 0.25, BoreRadius(hole), 2);
        Assert.Equal(OuterMaxX(plain, 2), OuterMaxX(hole, 2), 9); // the outline untouched
    }
}
