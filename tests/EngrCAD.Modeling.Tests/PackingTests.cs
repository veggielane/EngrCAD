using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>2D bin packing of silhouette footprints (build123d's <c>pack</c>): the
/// deterministic shelf layout, the gap and plate contracts, footprints that honor
/// overhangs, and the loud does-not-fit refusals.</summary>
public class PackingTests
{
    [Fact]
    public void Pack_PlacesEverythingInsideThePlateWithTheGap()
    {
        var parts = new[]
        {
            Shape.Box(20, 10, 5),
            Shape.Box(12, 16, 5),
            Shape.Cylinder(6, 8),
        };
        var layout = Packing.Pack(parts, 60, 40, gap: 2);

        Assert.Equal(3, layout.Placements.Count);
        for (int i = 0; i < parts.Length; i++)
        {
            var placement = layout.Placements[i];
            Assert.Equal(i, placement.Index); // placements come back in input order
            double minX = placement.Footprint.Min.X + placement.Offset.X;
            double minY = placement.Footprint.Min.Y + placement.Offset.Y;
            double maxX = placement.Footprint.Max.X + placement.Offset.X;
            double maxY = placement.Footprint.Max.Y + placement.Offset.Y;
            Assert.True(minX >= 2 - 1e-6 && maxX <= 58 + 1e-6, $"part {i} x range [{minX}, {maxX}]");
            Assert.True(minY >= 2 - 1e-6 && maxY <= 38 + 1e-6, $"part {i} y range [{minY}, {maxY}]");
        }

        // Pairwise: placed footprints never overlap (the shelf guarantees a full gap).
        for (int i = 0; i < parts.Length; i++)
        {
            for (int j = i + 1; j < parts.Length; j++)
            {
                var a = layout.Placements[i];
                var b = layout.Placements[j];
                bool separatedX =
                    a.Footprint.Max.X + a.Offset.X + 2 <= b.Footprint.Min.X + b.Offset.X + 1e-6 ||
                    b.Footprint.Max.X + b.Offset.X + 2 <= a.Footprint.Min.X + a.Offset.X + 1e-6;
                bool separatedY =
                    a.Footprint.Max.Y + a.Offset.Y + 2 <= b.Footprint.Min.Y + b.Offset.Y + 1e-6 ||
                    b.Footprint.Max.Y + b.Offset.Y + 2 <= a.Footprint.Min.Y + a.Offset.Y + 1e-6;
                Assert.True(separatedX || separatedY, $"parts {i} and {j} overlap");
            }
        }
    }

    [Fact]
    public void Pack_IsDeterministic()
    {
        var parts = new[]
        {
            Shape.Box(20, 10, 5),
            Shape.Box(12, 16, 5),
            Shape.Box(12, 16, 5),
            Shape.Cylinder(6, 8),
        };
        var first = Packing.Pack(parts, 60, 60, gap: 2);
        var second = Packing.Pack(parts, 60, 60, gap: 2);
        for (int i = 0; i < parts.Length; i++)
        {
            Assert.Equal(first.Placements[i].Offset.X, second.Placements[i].Offset.X);
            Assert.Equal(first.Placements[i].Offset.Y, second.Placements[i].Offset.Y);
        }
    }

    [Fact]
    public void Footprint_IsTheSilhouette_SoOverhangsGetRoom()
    {
        // A mushroom: a 10×10 cap on a 2×2 stem. If the footprint were the base, two of
        // them could land 4 apart and their caps would collide; the silhouette footprint
        // forces at least cap + gap.
        var mushroom = Shape.Box(2, 2, 8) | Shape.Box(10, 10, 2).Translate(0, 0, 4);
        var layout = Packing.Pack([mushroom, mushroom], 40, 16, gap: 2);

        Assert.Equal(10, layout.Placements[0].Footprint.Size.X, 6);
        double dx = Math.Abs(layout.Placements[1].Offset.X - layout.Placements[0].Offset.X);
        Assert.True(dx >= 12 - 1e-6, $"caps would collide at dx = {dx}");
    }

    [Fact]
    public void Apply_TranslatesInXYOnly()
    {
        var parts = new[] { Shape.Box(10, 10, 6), Shape.Box(10, 10, 6) };
        var layout = Packing.Pack(parts, 40, 20, gap: 2);
        var placed = layout.Apply(parts);

        Assert.Equal(parts.Length, placed.Count);
        for (int i = 0; i < placed.Count; i++)
        {
            var bounds = placed[i].Bounds();
            // z untouched (the box is origin-centered: −3..3), xy inside the plate.
            Assert.Equal(-3, bounds.Min.Z, 6);
            Assert.Equal(3, bounds.Max.Z, 6);
            Assert.True(bounds.Min.X >= 2 - 1e-6 && bounds.Max.X <= 38 + 1e-6);
        }
        Assert.Equal(parts[0].ToMesh().Volume(), placed[0].ToMesh().Volume(), 9);
    }

    [Fact]
    public void Pack_ThatDoesNotFit_RefusesNamingThePart()
    {
        var parts = new[] { Shape.Box(20, 20, 5), Shape.Box(20, 20, 5) };
        var exception = Assert.Throws<InvalidOperationException>(
            () => Packing.Pack(parts, 30, 30, gap: 2));
        Assert.Contains("do not fit", exception.Message);
        Assert.Contains("plate", exception.Message);
    }

    [Fact]
    public void Pack_PartWiderThanThePlate_RefusesUpFront()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => Packing.Pack([Shape.Box(50, 5, 5)], 30, 60, gap: 2));
        Assert.Contains("too wide", exception.Message);
    }
}
