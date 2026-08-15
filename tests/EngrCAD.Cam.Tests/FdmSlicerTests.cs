using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Cam;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// The FDM slicer (CAM stage 1). The oracles are the campaign's own: the layer grid as exact
/// arithmetic, wall perimeters against closed forms (an inward offset of a rectangle is a
/// rectangle, so wall 0's centreline perimeter is exactly 2(a − w) + 2(b − w)), the wall's
/// clearance from the section boundary as an EXACT point-by-point signed-distance claim (the
/// no-gouge analogue), infill alternation and containment, and determinism — because a toolpath
/// diff is how a CAM regression is caught.
/// </summary>
public sealed class FdmSlicerTests
{
    private static PrinterProfile Profile(double density = 0.2) => new(
        LayerHeight: 0.25, InfillDensity: density, HotendTemperature: 0, BedTemperature: 0);

    /// <summary>Min distance from a point to the region's boundary (outer + holes).</summary>
    private static double DistanceToBoundary(Region2d region, Vector2d p)
    {
        double best = double.PositiveInfinity;
        Visit(region.Outer);
        foreach (var hole in region.Holes)
            Visit(hole);
        return best;

        void Visit(IReadOnlyList<Vector2d> loop)
        {
            for (int i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                var ab = b - a;
                double len2 = ab.X * ab.X + ab.Y * ab.Y;
                double t = len2 > 0
                    ? Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2, 0, 1)
                    : 0;
                var q = new Vector2d(a.X + t * ab.X, a.Y + t * ab.Y);
                best = Math.Min(best, (p - q).Length);
            }
        }
    }

    [Fact]
    public void ABox_SlicesToTheArithmeticLayerGrid_WithExactSections()
    {
        var box = Shape.Box(20, 10, 8);
        var part = FdmSlicer.Slice(box, Profile());

        // 8 / 0.25 = 32 layers, sectioned at the exact mid-layer heights.
        Assert.Equal(32, part.Layers.Count);
        double minZ = box.Bounds().Min.Z;
        for (int i = 0; i < part.Layers.Count; i++)
        {
            Assert.Equal(minZ + (i + 0.5) * 0.25, part.Layers[i].SectionZ, 12);
            Assert.Equal(minZ + (i + 1) * 0.25, part.Layers[i].Z, 12);
        }

        // Every layer's section is the exact 20×10 rectangle.
        Assert.All(part.Layers, l => Assert.Equal(200.0, l.Regions.Sum(r => r.Area), 9));
    }

    [Fact]
    public void WallLoops_AreExactInsets_AndClearTheBoundaryByHalfABead()
    {
        var part = FdmSlicer.Slice(Shape.Box(20, 10, 8), Profile(density: 0));
        var layer = part.Layers[3];
        var region = layer.Regions.Single();

        // Wall 0 (the outer wall): centreline inset w/2 = 0.2, so its perimeter is exactly
        // 2·(20 − 0.4) + 2·(10 − 0.4) — an inward offset of a rectangle keeps sharp corners
        // whatever the join style, so this is a closed form, not an approximation.
        var wall0 = layer.Paths.Where(p => p is { Role: SlicePathRole.Wall, WallIndex: 0 }).ToList();
        Assert.Single(wall0);
        Assert.Equal(2 * 19.6 + 2 * 9.6, wall0[0].Length, 9);

        // Wall 1 sits one bead further in.
        var wall1 = layer.Paths.Where(p => p is { Role: SlicePathRole.Wall, WallIndex: 1 }).ToList();
        Assert.Single(wall1);
        Assert.Equal(2 * 18.8 + 2 * 8.8, wall1[0].Length, 9);

        // The no-gouge analogue, as an exact claim: every wall-0 point exactly half a bead from
        // the section boundary (an offset polygon vertex is exactly the offset distance from
        // the boundary it was offset from).
        foreach (var p in wall0[0].Points)
            Assert.Equal(0.2, DistanceToBoundary(region, p), 9);

        // Print order per region: innermost wall first, the outer wall last onto settled
        // neighbours.
        int first1 = layer.Paths.ToList().FindIndex(p => p.WallIndex == 1);
        int first0 = layer.Paths.ToList().FindIndex(p => p.WallIndex == 0);
        Assert.True(first1 < first0);
    }

    [Fact]
    public void AHole_GetsItsOwnWallLoops()
    {
        // A plate with a bore: each shell produces the shrunk outer loop AND the grown hole loop.
        var plate = Shape.Box(20, 20, 4) - Shape.Cylinder(4, 10);
        var part = FdmSlicer.Slice(plate, Profile(density: 0) with { WallCount = 1 });
        var layer = part.Layers[7];
        var walls = layer.Paths.Where(p => p.Role == SlicePathRole.Wall).ToList();
        Assert.Equal(2, walls.Count);

        // The outer loop's perimeter is the exact rectangle inset; the hole loop's is the bore
        // circle GROWN by half a bead (radius 4.2), a chorded circle — asserted as a band
        // between the inscribed polygon and the true circumference.
        var byLength = walls.OrderByDescending(w => w.Length).ToList();
        Assert.Equal(4 * 19.6, byLength[0].Length, 9);
        Assert.InRange(byLength[1].Length, 2 * Math.PI * 4.2 * 0.995, 2 * Math.PI * 4.2 + 1e-9);

        // Every hole-wall point clears the bore's own boundary by exactly half a bead.
        foreach (var p in byLength[1].Points)
            Assert.Equal(0.2, DistanceToBoundary(layer.Regions.Single(), p), 3);
    }

    [Fact]
    public void Infill_AlternatesDirection_AndStaysInsideTheWalls()
    {
        var part = FdmSlicer.Slice(Shape.Box(20, 10, 8), Profile(density: 0.4));

        Vector2d DirectionOf(SliceLayer layer)
        {
            var run = layer.Paths.First(p => p.Role == SlicePathRole.Infill);
            var d = run.Points[^1] - run.Points[0];
            double len = d.Length;
            return new Vector2d(Math.Abs(d.X / len), Math.Abs(d.Y / len));
        }

        // ±45°: successive layers are perpendicular, both diagonal.
        var d0 = DirectionOf(part.Layers[0]);
        var d1 = DirectionOf(part.Layers[1]);
        Assert.Equal(Math.Sqrt(0.5), d0.X, 12);
        Assert.Equal(Math.Sqrt(0.5), d0.Y, 12);
        Assert.Equal(Math.Sqrt(0.5), d1.X, 12);
        // Perpendicular in the signed sense: the raw directions' dot is ~0.
        var r0 = part.Layers[0].Paths.First(p => p.Role == SlicePathRole.Infill);
        var r1 = part.Layers[1].Paths.First(p => p.Role == SlicePathRole.Infill);
        var v0 = r0.Points[^1] - r0.Points[0];
        var v1 = r1.Points[^1] - r1.Points[0];
        Assert.Equal(0.0, (v0.X * v1.X + v0.Y * v1.Y) / (v0.Length * v1.Length), 9);

        // Containment: every infill endpoint at least (WallCount + 0.5)·bead − ε from the
        // section boundary (it was clipped to the region inset by exactly that much).
        var layer = part.Layers[0];
        double inset = 0.4 * (2 + 0.5);
        foreach (var run in layer.Paths.Where(p => p.Role == SlicePathRole.Infill))
            foreach (var p in run.Points)
                Assert.True(DistanceToBoundary(layer.Regions.Single(), p) >= inset - 1e-6);
    }

    [Fact]
    public void SolidInfill_CoversTheCore_MeasuredNotAssumed()
    {
        // 100% density: infill lines one bead apart tile the core. The extruded volume is the
        // deposited-bead bookkeeping, so the sanity band is stated with its deviations
        // ATTRIBUTED: the stadium bead under-fills a solid slab by its corner deficit
        // (1 − BeadArea/(w·h) ≈ 10.7% at w = 2h) and the scan grid leaves edge margins of up
        // to half a spacing — so the ratio sits BELOW 1 by roughly those, never above.
        var part = FdmSlicer.Slice(Shape.Box(20, 10, 8), Profile(density: 1.0));
        double ratio = part.ExtrudedVolume / (20.0 * 10 * 8);
        Assert.InRange(ratio, 0.70, 1.0);
    }

    [Fact]
    public void PrintDirection_SelectsTheBuildOrientation()
    {
        // The same box printed on its side: +X up means the 20 mm axis builds vertically —
        // 80 layers at 0.25 — and every section is the 8×10 face.
        var box = Shape.Box(20, 10, 8);
        var onSide = FdmSlicer.Slice(box, Profile(), new Vector3d(1, 0, 0));
        Assert.Equal(80, onSide.Layers.Count);
        Assert.All(onSide.Layers, l => Assert.Equal(80.0, l.Regions.Sum(r => r.Area), 6));
        Assert.Equal(Vector3d.UnitX, onSide.PrintDirection);

        // +Z is the identity fast path: byte-identical to passing no direction at all.
        Assert.Equal(
            GcodeWriter.Write(FdmSlicer.Slice(Shape.Box(12, 9, 3), Profile())),
            GcodeWriter.Write(FdmSlicer.Slice(Shape.Box(12, 9, 3), Profile(), Vector3d.UnitZ)));

        // Upside down (−Z): the antiparallel case has no unique minimal rotation, so it turns π
        // about the one arbitrary-perpendicular convention — deterministic, and for a box the
        // layer grid is unchanged.
        var flipped = FdmSlicer.Slice(box, Profile(), new Vector3d(0, 0, -1));
        Assert.Equal(32, flipped.Layers.Count);
        Assert.All(flipped.Layers, l => Assert.Equal(200.0, l.Regions.Sum(r => r.Area), 6));

        // A zero direction is refused by name.
        Assert.Contains("direction", Assert.Throws<ArgumentException>(() =>
            FdmSlicer.Slice(box, Profile(), Vector3d.Zero)).Message);
    }

    [Fact]
    public void TheSlice_IsDeterministic()
    {
        string once = GcodeWriter.Write(FdmSlicer.Slice(Shape.Box(12, 9, 3), Profile()));
        string twice = GcodeWriter.Write(FdmSlicer.Slice(Shape.Box(12, 9, 3), Profile()));
        Assert.Equal(once, twice);
    }

    [Fact]
    public void BrimAndSkirt_LiveOnTheFirstLayerOnly_AndAreWriteOnlyWhenStated()
    {
        // Off by default: no adhesion paths anywhere.
        var plain = FdmSlicer.Slice(Shape.Box(12, 9, 3), Profile(density: 0));
        Assert.DoesNotContain(plain.Layers.SelectMany(l => l.Paths),
            p => p.Role is SlicePathRole.Brim or SlicePathRole.Skirt);

        // BrimWidth 2 at bead 0.4 = 5 rings; 2 skirt loops standing SkirtGap clear. Both on
        // layer 0 only, skirt first (the nozzle primes clear of the part), brim outermost-in
        // (it finishes AT the part's outline).
        var part = FdmSlicer.Slice(Shape.Box(12, 9, 3),
            Profile(density: 0) with { BrimWidth = 2.0, SkirtLoops = 2, SkirtGap = 5 });
        var first = part.Layers[0].Paths;
        Assert.Equal(5, first.Count(p => p.Role == SlicePathRole.Brim));
        Assert.Equal(2, first.Count(p => p.Role == SlicePathRole.Skirt));
        Assert.DoesNotContain(part.Layers.Skip(1).SelectMany(l => l.Paths),
            p => p.Role is SlicePathRole.Brim or SlicePathRole.Skirt);
        Assert.Equal(SlicePathRole.Skirt, first[0].Role);
        int lastBrim = first.ToList().FindLastIndex(p => p.Role == SlicePathRole.Brim);
        int firstWall = first.ToList().FindIndex(p => p.Role == SlicePathRole.Wall);
        Assert.True(lastBrim < firstWall);

        // The innermost brim ring's perimeter is the outline grown by bead/2 — round-join
        // corner arcs, so the length sits within the inscribed band of 2(a+b) + 2π·inset.
        var innermost = first.Where(p => p.Role == SlicePathRole.Brim).Last();
        double exact = 2 * (12 + 9) + 2 * Math.PI * 0.2;
        Assert.InRange(innermost.Length, exact * 0.99, exact + 1e-9);

        // And the extrusion bookkeeping identity still holds with adhesion paths in the file.
        var decoded = GcodeReader.Read(GcodeWriter.Write(part));
        Assert.Equal(part.FilamentUsed, decoded.FilamentUsed, part.FilamentUsed * 1e-3);
    }

    [Fact]
    public void TheRefusals_NameTheirNumbers()
    {
        // A layer taller than the bead: the stadium cross-section degenerates.
        Assert.Contains("bead", Assert.Throws<ArgumentException>(() =>
            FdmSlicer.Slice(Shape.Box(5, 5, 5),
                new PrinterProfile(LayerHeight: 0.5, BeadWidth: 0.4))).Message);
        Assert.Throws<ArgumentException>(() =>
            FdmSlicer.Slice(Shape.Box(5, 5, 5), new PrinterProfile(InfillDensity: 1.5)));
        Assert.Throws<ArgumentException>(() =>
            FdmSlicer.Slice(Shape.Box(5, 5, 5), new PrinterProfile(NozzleDiameter: 0)));
    }
}
