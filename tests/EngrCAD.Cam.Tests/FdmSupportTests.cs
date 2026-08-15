using EngrCAD.Cam;
using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// FDM supports — columns under the measured overhang field. The fixture with teeth is the
/// TABLE (a slab on a column): supports must exist exactly below the slab's underside, stand
/// the stated XY gap clear of the column, and stop at the underside — a support above it, or
/// one fused to the wall, is the classic silent slicer failure. The 45° wedge pins the angle
/// comparison's direction AND that a slanted overhang's supports track its own height.
/// </summary>
public class FdmSupportTests
{
    private const double UndersideZ = 8;

    /// <summary>A 20×10 slab on a 4×10 column: the underside at z = 8 overhangs everywhere
    /// but the column contact.</summary>
    private static Shape Table() =>
        Shape.Box(4, 10, 8).Translate(0, 0, 4) | Shape.Box(20, 10, 2).Translate(0, 0, 9);

    private static PrinterProfile SupportProfile => new(
        NozzleDiameter: 0.8, LayerHeight: 0.5, InfillDensity: 0, SupportOverhangAngle: 45);

    [Fact]
    public void Table_GetsSupportsOnlyBelowTheUnderside()
    {
        var sliced = FdmSlicer.Slice(Table(), SupportProfile);

        // Supports run from the bed to the layer whose top IS the underside — and not above:
        // the slab prints onto the last support layer, and a support inside the slab would be
        // a collision.
        Assert.Contains(sliced.Layers[0].Paths, p => p.Role == SlicePathRole.Support);
        var lastSupport = sliced.Layers.Last(
            l => l.Paths.Any(p => p.Role == SlicePathRole.Support));
        Assert.Equal(UndersideZ, lastSupport.Z, 9);
        foreach (var layer in sliced.Layers.Where(l => l.Z > UndersideZ + 1e-9))
            Assert.DoesNotContain(layer.Paths, p => p.Role == SlicePathRole.Support);
    }

    [Fact]
    public void Table_SupportsStayInsideTheOverhangFootprint()
    {
        var sliced = FdmSlicer.Slice(Table(), SupportProfile);
        foreach (var point in SupportPoints(sliced))
        {
            Assert.InRange(point.X, -10 - 1e-6, 10 + 1e-6);
            Assert.InRange(point.Y, -5 - 1e-6, 5 + 1e-6);
        }
    }

    [Fact]
    public void Table_SupportsStandTheGapClearOfThePart()
    {
        var sliced = FdmSlicer.Slice(Table(), SupportProfile);
        double gap = sliced.Profile.SupportGap;
        int checkedPoints = 0;
        foreach (var layer in sliced.Layers)
        {
            foreach (var path in layer.Paths.Where(p => p.Role == SlicePathRole.Support))
            {
                foreach (var point in path.Points)
                {
                    foreach (var region in layer.Regions)
                    {
                        Assert.False(Inside(region, point),
                            $"support point {point} lies INSIDE the part at layer {layer.Index}");
                        // The grown section's corner arcs are inscribed (within the offset's
                        // 1e-3 arc tolerance), so a support point may sit up to that much
                        // closer than the stated gap — never more.
                        Assert.True(DistanceToBoundary(region, point) >= gap - 2e-3,
                            $"support point {point} is {DistanceToBoundary(region, point):0.####} "
                            + $"from the part at layer {layer.Index}; the gap is {gap}");
                        checkedPoints++;
                    }
                }
            }
        }
        Assert.True(checkedPoints > 50, "the fixture must actually exercise the clearance");
    }

    [Fact]
    public void SupportsAreOffByDefault_AndAbsentWithNothingOverhanging()
    {
        // Default profile: no Support paths however much overhangs.
        var plain = FdmSlicer.Slice(Table(), new PrinterProfile(NozzleDiameter: 0.8, LayerHeight: 0.5, InfillDensity: 0));
        Assert.DoesNotContain(
            plain.Layers.SelectMany(l => l.Paths), p => p.Role == SlicePathRole.Support);

        // Supports STATED on a shape with no overhang: the box's bottom rests on the bed
        // (nothing of it is above any layer, so it excludes itself with no special case) and
        // the G-code is byte-identical to the unstated profile's.
        var box = Shape.Box(10, 10, 5);
        var with = FdmSlicer.Slice(box, SupportProfile);
        var without = FdmSlicer.Slice(box, new PrinterProfile(NozzleDiameter: 0.8, LayerHeight: 0.5, InfillDensity: 0));
        Assert.DoesNotContain(
            with.Layers.SelectMany(l => l.Paths), p => p.Role == SlicePathRole.Support);
        Assert.Equal(GcodeWriter.Write(without), GcodeWriter.Write(with));
    }

    [Fact]
    public void ThresholdComparesTheRightWay_OnAFortyFiveDegreeWedge()
    {
        // A 45° underside slant: triangle (0,10)-(10,0)-(10,10) in the xz plane, extruded.
        // Its overhang facets have −n·Z exactly sin 45° — past a 40° threshold, under a 50°.
        var wedge = Wedge();
        var supported = FdmSlicer.Slice(wedge, SupportProfile with { SupportOverhangAngle = 40 });
        Assert.Contains(supported.Layers.SelectMany(l => l.Paths),
            p => p.Role == SlicePathRole.Support);

        var unsupported = FdmSlicer.Slice(wedge, SupportProfile with { SupportOverhangAngle = 50 });
        Assert.DoesNotContain(unsupported.Layers.SelectMany(l => l.Paths),
            p => p.Role == SlicePathRole.Support);
    }

    [Fact]
    public void SlantedOverhang_SupportsTrackTheSlantsOwnHeight()
    {
        // The slant line is z = 10 − x, so the overhang still ABOVE a layer's top zc is
        // x ≤ 10 − zc: a support column may only stand where material remains above it —
        // the per-layer facet clip, asserted directly. (Per-facet bounding would leave every
        // column running to each facet's own lowest point instead.)
        var sliced = FdmSlicer.Slice(Wedge(), SupportProfile with { SupportOverhangAngle = 40 });
        int supportLayers = 0;
        foreach (var layer in sliced.Layers)
        {
            var points = layer.Paths.Where(p => p.Role == SlicePathRole.Support)
                .SelectMany(p => p.Points).ToList();
            if (points.Count == 0)
                continue;
            supportLayers++;
            foreach (var point in points)
                Assert.True(point.X <= 10 - layer.Z + 1e-6,
                    $"support at x={point.X:0.###} on layer with top z={layer.Z} has no "
                    + $"overhang material above it (the slant ends at x={10 - layer.Z:0.###})");
        }
        Assert.True(supportLayers >= 5, "the wedge must exercise several clipped layers");
    }

    [Fact]
    public void SupportedSlice_KeepsTheExtrusionIdentity_AndIsDeterministic()
    {
        var first = GcodeWriter.Write(FdmSlicer.Slice(Table(), SupportProfile));
        var second = GcodeWriter.Write(FdmSlicer.Slice(Table(), SupportProfile));
        Assert.Equal(first, second);

        var decoded = GcodeReader.Read(first);
        double identity = decoded.DepositionLength
            * SupportProfile.BeadArea / SupportProfile.FilamentArea;
        Assert.True(decoded.FilamentUsed > 0);
        Assert.Equal(identity, decoded.FilamentUsed, identity * 1e-3);
    }

    [Fact]
    public void UnusableSupportSettings_RefuseByName()
    {
        var negative = Assert.Throws<ArgumentException>(() =>
            FdmSlicer.Slice(Table(), SupportProfile with { SupportOverhangAngle = -5 }));
        Assert.Contains("SupportOverhangAngle", negative.Message);

        var pastVertical = Assert.Throws<ArgumentException>(() =>
            FdmSlicer.Slice(Table(), SupportProfile with { SupportOverhangAngle = 95 }));
        Assert.Contains("SupportOverhangAngle", pastVertical.Message);

        var spacing = Assert.Throws<ArgumentException>(() =>
            FdmSlicer.Slice(Table(), SupportProfile with { SupportSpacing = 0 }));
        Assert.Contains("SupportSpacing", spacing.Message);
    }

    private static Shape Wedge()
    {
        var plane = SketchPlane.At(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ);
        var triangle = Sketch.Polygon([(0, 10), (10, 0), (10, 10)]);
        return Shape.Extrude(triangle, 10, plane);
    }

    private static IEnumerable<Vector2d> SupportPoints(SlicedPart sliced) =>
        sliced.Layers.SelectMany(l => l.Paths)
            .Where(p => p.Role == SlicePathRole.Support)
            .SelectMany(p => p.Points);

    private static bool Inside(Region2d region, Vector2d p)
    {
        if (!InsideLoop(region.Outer, p))
            return false;
        foreach (var hole in region.Holes)
        {
            if (InsideLoop(hole, p))
                return false;
        }
        return true;

        static bool InsideLoop(IReadOnlyList<Vector2d> loop, Vector2d p)
        {
            bool inside = false;
            for (int i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                if (a.Y > p.Y == b.Y > p.Y)
                    continue;
                if (p.X < a.X + (p.Y - a.Y) / (b.Y - a.Y) * (b.X - a.X))
                    inside = !inside;
            }
            return inside;
        }
    }

    private static double DistanceToBoundary(Region2d region, Vector2d p)
    {
        double best = double.PositiveInfinity;
        Walk(region.Outer);
        foreach (var hole in region.Holes)
            Walk(hole);
        return best;

        void Walk(IReadOnlyList<Vector2d> loop)
        {
            for (int i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                var d = b - a;
                double len2 = d.Dot(d);
                double t = len2 > 0 ? Math.Clamp((p - a).Dot(d) / len2, 0, 1) : 0;
                best = Math.Min(best, (p - (a + d * t)).Length);
            }
        }
    }
}
