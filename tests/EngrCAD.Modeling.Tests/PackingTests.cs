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

    // ---- rotation and outline nesting are OPT-IN: the default path may not move ----

    /// <summary>An L bracket: 40 x 34 box with a 28 x 22 notch at the top right. Its box
    /// wastes 45% of its own footprint, which is what makes it the fixture where outline
    /// nesting has something to win.</summary>
    private static Shape Bracket() => Shape.Extrude(Sketch.Start(0, 0)
        .LineTo(40, 0).LineTo(40, 12).LineTo(12, 12).LineTo(12, 34).LineTo(0, 34).Close(), 6);

    private static Shape[] Brackets(int count) =>
        [.. Enumerable.Range(0, count).Select(_ => Bracket())];

    /// <summary>The v1 placements, captured bit for bit BEFORE rotation and outline nesting
    /// existed. Every new path is opt-in, and this is the check that says so: a default pack
    /// must reproduce these exactly, not merely closely.</summary>
    [Fact]
    public void Pack_WithNoOptions_ReproducesTheCommittedV1PlacementsBitForBit()
    {
        var parts = new[]
        {
            Shape.Box(20, 10, 5),
            Shape.Box(12, 16, 5),
            Shape.Cylinder(6, 8),
            Bracket(),
            Shape.Extrude(Sketch.RoundedRectangle(26, 12, 3), 2),
        };
        long[] goldenX =
        [
            0x4045000000000000, 0x404A000000000000, 0x4050C00000000000,
            0x4008000000000000, 0x4030000000000000,
        ];
        long[] goldenY =
        [
            0x4046800000000000, 0x4026000000000000, 0x4022000000000000,
            0x4008000000000000, 0x4046FFFFFFFFFCB4,
        ];

        var layout = Packing.Pack(parts, 90, 70, gap: 3);
        for (int i = 0; i < parts.Length; i++)
        {
            Assert.Equal(goldenX[i], BitConverter.DoubleToInt64Bits(layout.Placements[i].Offset.X));
            Assert.Equal(goldenY[i], BitConverter.DoubleToInt64Bits(layout.Placements[i].Offset.Y));
            Assert.Equal(0, layout.Placements[i].RotationDegrees);
        }
        Assert.Equal(PackRotation.None, layout.Rotation);
        Assert.Equal(PackNesting.BoundingBox, layout.Nesting);

        // The options overload with its own defaults is the SAME code path, not a near copy.
        var viaOptions = Packing.Pack(parts, 90, 70, new PackOptions { Gap = 3 });
        for (int i = 0; i < parts.Length; i++)
        {
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(layout.Placements[i].Offset.X),
                BitConverter.DoubleToInt64Bits(viaOptions.Placements[i].Offset.X));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(layout.Placements[i].Offset.Y),
                BitConverter.DoubleToInt64Bits(viaOptions.Placements[i].Offset.Y));
        }
    }

    [Fact]
    public void FreeRotation_IsRefusedByName()
    {
        var exception = Assert.Throws<NotSupportedException>(() => Packing.Pack(
            [Shape.Box(10, 10, 5)], 40, 40, new PackOptions { Rotation = PackRotation.Free }));
        Assert.Contains("Free rotation", exception.Message);
        Assert.Contains("finite", exception.Message);
        Assert.Contains("Quarter", exception.Message);
    }

    /// <summary>A quarter turn is a sign swap, so a turned part's measured bounds are the
    /// transposed footprint EXACTLY — no <c>cos</c> anywhere in the placement.</summary>
    [Fact]
    public void QuarterRotation_IsExact_SoATurnedPartsBoundsAreTheTransposedFootprint()
    {
        // 40 wide against a 40-wide plate with a gap: the turn is forced, not preferred.
        var parts = Brackets(4);
        var layout = Packing.Pack(parts, 40, 200, new PackOptions
        {
            Gap = 2,
            Rotation = PackRotation.Quarter,
        });
        Assert.Contains(layout.Placements, p => p.RotationDegrees == 90);

        var placed = layout.Apply(parts);
        for (int i = 0; i < parts.Length; i++)
        {
            var placement = layout.Placements[i];
            var bounds = placed[i].Bounds();
            Assert.Equal(placement.Footprint.Min.X + placement.Offset.X, bounds.Min.X, 9);
            Assert.Equal(placement.Footprint.Min.Y + placement.Offset.Y, bounds.Min.Y, 9);
            if (placement.RotationDegrees is 90 or 270)
            {
                Assert.Equal(34, placement.Footprint.Size.X, 9);
                Assert.Equal(40, placement.Footprint.Size.Y, 9);
            }
        }
    }

    /// <summary>Four 40 x 10 bars fit a 50 x 45 plate only side by side, i.e. only turned.
    /// The upright shelf packer refuses, which is what makes this a measurement of rotation
    /// rather than of the packer.</summary>
    [Fact]
    public void QuarterRotation_FitsAPlateTheUprightShelfPackerRefuses()
    {
        var bars = Enumerable.Range(0, 4).Select(_ => Shape.Box(40, 10, 4)).ToArray();
        Assert.Throws<InvalidOperationException>(
            () => Packing.Pack(bars, 50, 45, new PackOptions { Gap = 2 }));

        var layout = Packing.Pack(bars, 50, 45, new PackOptions
        {
            Gap = 2,
            Rotation = PackRotation.Quarter,
        });
        Assert.All(layout.Placements, p => Assert.Equal(90, p.RotationDegrees));
        Assert.True(layout.UsedDepth <= 45, $"used depth {layout.UsedDepth}");
    }

    // ---- outline nesting ----

    /// <summary>Grow each placed outline by half the gap and intersect: an empty intersection
    /// IS "these parts are at least `gap` apart", through the same exact 2D machinery the
    /// packer separates them with rather than a restated distance test.</summary>
    private static void AssertNoPairIsCloserThanTheGap(PackLayout layout)
    {
        var grown = new List<IReadOnlyList<Core.Geometry2.Region2d>>();
        for (int i = 0; i < layout.Placements.Count; i++)
            grown.Add(Core.Geometry2.Region2dOffset.Offset(layout.PlacedOutline(i), layout.Gap / 2));
        for (int i = 0; i < grown.Count; i++)
        {
            for (int j = i + 1; j < grown.Count; j++)
            {
                double shared = Core.Geometry2.Region2dBoolean
                    .Intersection(grown[i], grown[j]).Sum(region => region.Area);
                Assert.True(shared <= 1e-9, $"parts {i} and {j} share {shared} of grown area");
            }
        }
    }

    [Fact]
    public void OutlineNesting_KeepsEveryPairAtLeastTheGapApartAndInsideThePlate()
    {
        var parts = Brackets(6);
        var layout = Packing.Pack(parts, 86, 300, new PackOptions
        {
            Gap = 3,
            Nesting = PackNesting.Outline,
            Rotation = PackRotation.Quarter,
        });

        AssertNoPairIsCloserThanTheGap(layout);
        foreach (var placement in layout.Placements)
        {
            Assert.True(placement.Footprint.Min.X + placement.Offset.X >= 3 - 1e-9);
            Assert.True(placement.Footprint.Min.Y + placement.Offset.Y >= 3 - 1e-9);
            Assert.True(placement.Footprint.Max.X + placement.Offset.X <= 83 + 1e-9);
            Assert.True(placement.Footprint.Max.Y + placement.Offset.Y <= 297 + 1e-9);
        }
    }

    /// <summary>The oracle: on a fixture whose boxes waste 45% of their own footprint,
    /// nesting to the outline must use measurably less plate. Reported as utilisation —
    /// packed outline area over the plate strip consumed — so both settings are compared on
    /// one number that means something.</summary>
    [Fact]
    public void OutlineNesting_BeatsBoxNestingWhereTheBoxWastesRoom()
    {
        var parts = Brackets(6);
        var box = Packing.Pack(parts, 86, 300, new PackOptions
        {
            Gap = 3,
            Rotation = PackRotation.Quarter,
        });
        var outline = Packing.Pack(parts, 86, 300, new PackOptions
        {
            Gap = 3,
            Nesting = PackNesting.Outline,
            Rotation = PackRotation.Quarter,
        });

        // The fixture must actually have room to win, or the comparison proves nothing.
        Assert.True(box.PackedArea < 0.6 * box.FootprintArea,
            $"outline {box.PackedArea} against boxes {box.FootprintArea}");
        Assert.Equal(box.PackedArea, outline.PackedArea, 6);
        Assert.True(outline.UsedDepth < 0.9 * box.UsedDepth,
            $"outline used {outline.UsedDepth}, boxes used {box.UsedDepth}");
        Assert.True(outline.Utilisation > box.Utilisation + 0.05,
            $"outline {outline.Utilisation:P1}, boxes {box.Utilisation:P1}");
    }

    /// <summary>A through hole is a hole in the silhouette, so the raster leaves it free and
    /// small parts nest INSIDE the ring — something no bounding box can express.</summary>
    [Fact]
    public void OutlineNesting_PlacesSmallPartsInsideAThroughHole()
    {
        var ring = Shape.Extrude(Sketch.Circle(30).WithHole(Sketch.Circle(20)), 4);
        var disc = Shape.Cylinder(7, 4);
        var parts = new[] { ring, disc, disc, disc };

        var layout = Packing.Pack(parts, 70, 200, new PackOptions
        {
            Gap = 2,
            Nesting = PackNesting.Outline,
        });
        AssertNoPairIsCloserThanTheGap(layout);

        var ringPlacement = layout.Placements[0];
        var centre = new Vector2d(
            (ringPlacement.Footprint.Min.X + ringPlacement.Footprint.Max.X) / 2 + ringPlacement.Offset.X,
            (ringPlacement.Footprint.Min.Y + ringPlacement.Footprint.Max.Y) / 2 + ringPlacement.Offset.Y);
        for (int i = 1; i < parts.Length; i++)
        {
            var placement = layout.Placements[i];
            var discCentre = new Vector2d(
                (placement.Footprint.Min.X + placement.Footprint.Max.X) / 2 + placement.Offset.X,
                (placement.Footprint.Min.Y + placement.Footprint.Max.Y) / 2 + placement.Offset.Y);
            // Bore radius 20, disc radius 7, gap 2 -> a nested disc's centre is within 11.
            Assert.True((discCentre - centre).Length <= 11 + 1e-9,
                $"disc {i} sits {(discCentre - centre).Length:F2} from the ring centre");
        }

        // And the whole plate strip is then just the ring's own footprint plus its gaps
        // (60 + 2 x 2, up to the raster's own cell), against 80 for the box packer, which
        // has to give the discs a row of their own.
        Assert.InRange(layout.UsedDepth, 63, 64.5);
        var boxed = Packing.Pack(parts, 70, 200, new PackOptions { Gap = 2 });
        Assert.Equal(80, boxed.UsedDepth, 6);
    }

    /// <summary>The raster QUANTIZES placements, so a fit with no slack at all is refused
    /// while the box packer takes it. That is the conservative direction (a refusal, never an
    /// overlap) and it is pinned rather than left to be discovered.</summary>
    [Fact]
    public void OutlineNesting_RefusesAZeroSlackFitTheBoxPackerTakes()
    {
        // Four 40 x 10 bars turned upright span exactly 4 x 10 + 5 x 2 = 50, the plate width.
        var bars = Enumerable.Range(0, 4).Select(_ => Shape.Box(40, 10, 4)).ToArray();
        var box = Packing.Pack(bars, 50, 45, new PackOptions
        {
            Gap = 2,
            Rotation = PackRotation.Quarter,
        });
        Assert.Equal(44, box.UsedDepth, 6);

        var exception = Assert.Throws<InvalidOperationException>(() => Packing.Pack(
            bars, 50, 45, new PackOptions
            {
                Gap = 2,
                Nesting = PackNesting.Outline,
                Rotation = PackRotation.Quarter,
            }));
        Assert.Contains("do not fit", exception.Message);
        Assert.Contains("raster", exception.Message);
    }

    /// <summary>Determinism is asserted on the WHOLE placement list bit for bit — two
    /// searches can reach one plate by different routes, and only the routes would show
    /// it.</summary>
    [Fact]
    public void OutlineNesting_IsDeterministicBitForBit()
    {
        var parts = Brackets(6);
        var options = new PackOptions
        {
            Gap = 3,
            Nesting = PackNesting.Outline,
            Rotation = PackRotation.Quarter,
        };
        var first = Packing.Pack(parts, 86, 300, options);
        var second = Packing.Pack(parts, 86, 300, options);
        for (int i = 0; i < parts.Length; i++)
        {
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(first.Placements[i].Offset.X),
                BitConverter.DoubleToInt64Bits(second.Placements[i].Offset.X));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(first.Placements[i].Offset.Y),
                BitConverter.DoubleToInt64Bits(second.Placements[i].Offset.Y));
            Assert.Equal(first.Placements[i].RotationDegrees, second.Placements[i].RotationDegrees);
        }
    }

    [Fact]
    public void Resolution_PastTheRasterLimit_RefusesNamingIt()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => Packing.Pack(
            [Shape.Box(10, 10, 5)], 400, 400, new PackOptions
            {
                Nesting = PackNesting.Outline,
                Resolution = 0.01,
            }));
        Assert.Contains("4096", exception.Message);
        Assert.Contains("coarser", exception.Message);
    }

    /// <summary>The reported areas are what a caller compares two settings on, so they are
    /// measured from the placed outlines rather than restated.</summary>
    [Fact]
    public void PlacedOutline_CarriesTheSameAreaTheLayoutReports()
    {
        var parts = Brackets(3);
        var layout = Packing.Pack(parts, 86, 200, new PackOptions
        {
            Gap = 3,
            Nesting = PackNesting.Outline,
            Rotation = PackRotation.Quarter,
        });
        double placed = 0;
        for (int i = 0; i < parts.Length; i++)
            placed += layout.PlacedOutline(i).Sum(region => region.Area);
        Assert.Equal(layout.PackedArea, placed, 6);
        Assert.True(layout.FootprintArea > layout.PackedArea);
    }

    [Fact]
    public void Apply_TurnsThenTranslates()
    {
        var parts = new[] { Bracket() };
        var layout = Packing.Pack(parts, 40, 60, new PackOptions
        {
            Gap = 2,
            Rotation = PackRotation.Quarter,
        });
        // 40 wide does not fit a 40-wide plate with a gap; the turn is forced.
        Assert.Equal(90, layout.Placements[0].RotationDegrees);

        var placed = layout.Apply(parts)[0];
        var bounds = placed.Bounds();
        Assert.Equal(2, bounds.Min.X, 6);
        Assert.Equal(2, bounds.Min.Y, 6);
        Assert.Equal(36, bounds.Max.X, 6);
        Assert.Equal(42, bounds.Max.Y, 6);
        Assert.Equal(parts[0].ToMesh().Volume(), placed.ToMesh().Volume(), 6);
    }
}
