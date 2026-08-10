using EngrCAD.Core;
using EngrCAD.Ecad;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// The PCB exploded-view decomposition (<see cref="PcbLayout.ToExplodedAssembly"/>): the board
/// sliced into per-layer slabs, fanned along the stackup normal, with the components lifting off.
/// The bar is the house style (ECAD fails plausibly): factor-0 bit-identity to the un-exploded
/// board, an exact reassembly-to-the-plate oracle, pure-Z / stack-order / factor-independent-count
/// assertions, the embedded dogleg, determinism, and the copper-only guard shown to fire.
/// </summary>
public class PcbExplodeTests
{
    private const double Cu = 0.035, Prepreg = 0.2, Core = 1.13;
    private static LayerStackup FourLayer() => LayerStackup.FourLayer(Cu, Prepreg, Core);

    private static PcbBoard MultiBoard(LayerStackup? stackup = null) => new(
        [
            new Vector2d(-25, -20), new Vector2d(25, -20),
            new Vector2d(25, 20), new Vector2d(-25, 20),
        ],
        stackup ?? FourLayer(),
        holes: [
            new BoardHole(new Vector2d(-22, -17), 3.0, BoardHoleKind.Mounting),
            new BoardHole(new Vector2d(22, 17), 3.0, BoardHoleKind.Mounting),
        ]);

    // A surface layout on a 4-layer board: a top SMD, a bottom SMD, a top through-hole header.
    private static PcbLayout SurfaceLayout()
    {
        var sch = new Schematic("stack");
        sch.Add("R1", PcbFixtures.SmdResistor(), "330");
        sch.Add("R2", PcbFixtures.SmdResistor(), "1k");
        sch.Add("J1", PcbFixtures.ThroughHoleHeader());
        var layout = new PcbLayout(sch, MultiBoard());
        layout.Place("R1", 8, 6, 0, CopperSide.Top);
        layout.Place("R2", -8, -6, 0, CopperSide.Bottom);
        layout.Place("J1", -14, 0, 90, CopperSide.Top);
        return layout;
    }

    // A layout with a buried die: an embedded part on an inner layer plus a surface part.
    private static PcbLayout EmbeddedLayout()
    {
        var sch = new Schematic("embed");
        sch.Add("U1", PcbFixtures.SmdResistor());
        sch.Add("R1", PcbFixtures.SmdResistor());
        var layout = new PcbLayout(sch, MultiBoard());
        layout.Embed("U1", "In2", 0, 0, cavityClearance: 0.15);   // buried on inner layer In2
        layout.Place("R1", 14, 0, 0, CopperSide.Top);
        return layout;
    }

    private static Vector3d Origin(in Matrix4d m) => m.TransformPoint(Vector3d.Zero);

    private static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static void AssertBitIdentical(in Matrix4d a, in Matrix4d b)
    {
        double[] ea = [a.M11, a.M12, a.M13, a.M14, a.M21, a.M22, a.M23, a.M24,
            a.M31, a.M32, a.M33, a.M34, a.M41, a.M42, a.M43, a.M44];
        double[] eb = [b.M11, b.M12, b.M13, b.M14, b.M21, b.M22, b.M23, b.M24,
            b.M31, b.M32, b.M33, b.M34, b.M41, b.M42, b.M43, b.M44];
        for (int i = 0; i < 16; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(ea[i]), BitConverter.DoubleToInt64Bits(eb[i]));
    }

    // ---- 1. factor 0: the exploded assembly IS the assembled board -----------

    [Fact]
    public void Factor0_ComponentInstances_AreBitIdenticalToTheUnexplodedAssembly()
    {
        var layout = SurfaceLayout();
        var exploded = layout.ToExplodedAssembly().Flatten(0);
        var plain = layout.ToAssembly().Flatten();

        // Every placed component appears in BOTH assemblies at a bit-identical pose (matched by
        // reference designator). The exploded assembly also carries the board sliced into layers,
        // which the plain one carries as one "board" part — so we compare the component occurrences,
        // and each against WorldOf(placement), which the assembly math is bit-identical to.
        int matched = 0;
        foreach (var placement in layout.Placements)
        {
            var p = plain.Single(i => Leaf(i.Path) == placement.Reference);
            var e = exploded.Single(i => Leaf(i.Path) == placement.Reference);
            AssertBitIdentical(p.World, e.World);
            AssertBitIdentical(layout.WorldOf(placement), e.World);
            matched++;
        }
        Assert.Equal(3, matched);                                // R1 + R2 + J1
    }

    // ---- 2. reassembly: the slabs tile the plate exactly ---------------------

    [Fact]
    public void LayerSlabs_TileTheStackupZRangeExactly()
    {
        var stackup = FourLayer();
        var extents = stackup.Extents;
        // The two outer extremes on the faces exactly, plus contiguity, IS a complete exact proof
        // that the ranges PARTITION [0, TotalThickness] — no gap, no overlap.
        Assert.Equal(stackup.TotalThickness, extents[0].High);   // top face, exactly
        Assert.Equal(0.0, extents[^1].Low);                      // bottom face, exactly
        for (int i = 0; i < extents.Count; i++)
        {
            Assert.True(extents[i].High > extents[i].Low);       // positive, non-degenerate
            if (i + 1 < extents.Count)
                Assert.Equal(extents[i].Low, extents[i + 1].High);   // contiguous: no gap, no overlap
        }
    }

    [Fact]
    public void UnionOfLayerParts_EqualsThePlate_ByTotalVolume()
    {
        var layout = new PcbLayout(new Schematic("bare"), MultiBoard());
        var assembly = layout.ToExplodedAssembly();

        // One slab per physical layer (7 for a 4-layer build): copper films + dielectric cores.
        int physicalLayers = layout.Board.LayerStackup!.Layers.Count;
        var slabs = assembly.Occurrences.Take(physicalLayers).ToList();
        Assert.Equal(7, physicalLayers);
        Assert.All(slabs, o => Assert.NotNull(o.Part));

        double slabSum = slabs.Sum(o => o.Part!.MassProperties().Volume);
        double plate = layout.ExpectedPlateVolume();
        // Mass-properties grade: each drilled slab's tessellated volume approaches its closed form
        // from below by the same per-unit-height chord deficit the plate's holes carry, so the sum
        // reassembles the plate (~3.3e3 mm^3).
        Assert.True(Math.Abs(slabSum - plate) / plate < 1e-3);
    }

    // ---- 3. pure Z, stack order, factor-independent count --------------------

    [Fact]
    public void EveryLayerAndSurfaceOffset_IsPureAlongTheStackupNormal()
    {
        var layout = SurfaceLayout();                            // identity BoardFrame => world +Z
        var assembly = layout.ToExplodedAssembly();
        int nonNull = 0;
        foreach (var occ in assembly.Occurrences)
        {
            if (occ.ExplodeOffset is not { } offset)
                continue;                                        // the datum layer stays put
            nonNull++;
            Assert.Equal(0.0, offset.X);                         // pure Z: no X, no Y
            Assert.Equal(0.0, offset.Y);
            Assert.NotEqual(0.0, offset.Z);
            // and no embedded parts here, so no ExplodePath is set
            Assert.Empty(occ.ExplodePath);
        }
        // 7 layers (one datum stays) + 3 components = 6 + 3 offsets set.
        Assert.Equal(9, nonNull);
    }

    [Fact]
    public void StackOrder_EqualsExplodeOrder_Monotonically()
    {
        var layout = SurfaceLayout();
        var stackup = layout.Board.LayerStackup!;
        var assembly = layout.ToExplodedAssembly();
        int n = stackup.Layers.Count;
        var layerOccs = assembly.Occurrences.Take(n).ToList();   // top-most first, as Layers

        // Original mid-z and final z (mid + the pure-Z offset) both strictly DESCEND top -> bottom.
        for (int i = 0; i + 1 < n; i++)
        {
            double midA = (stackup.Extents[i].Low + stackup.Extents[i].High) / 2;
            double midB = (stackup.Extents[i + 1].Low + stackup.Extents[i + 1].High) / 2;
            double finalA = midA + (layerOccs[i].ExplodeOffset?.Z ?? 0);
            double finalB = midB + (layerOccs[i + 1].ExplodeOffset?.Z ?? 0);
            Assert.True(midA > midB);                            // a layer above another in the stack
            Assert.True(finalA > finalB);                        // ... is above it when exploded
            // the offset magnitude also descends (top layer travels furthest, bottom is the datum)
            Assert.True((layerOccs[i].ExplodeOffset?.Z ?? 0) > (layerOccs[i + 1].ExplodeOffset?.Z ?? 0));
        }
        Assert.Null(layerOccs[^1].ExplodeOffset);                // the bottom layer IS the datum
    }

    [Fact]
    public void InstanceCountAndOrder_AreIndependentOfTheFactor()
    {
        var assembly = SurfaceLayout().ToExplodedAssembly();
        var f0 = assembly.Flatten(0);
        var f5 = assembly.Flatten(0.5);
        var f1 = assembly.Flatten(1);
        Assert.Equal(f0.Count, f5.Count);
        Assert.Equal(f0.Count, f1.Count);
        for (int i = 0; i < f0.Count; i++)
        {
            Assert.Equal(f0[i].Path, f5[i].Path);                // same order, same parts
            Assert.Equal(f0[i].Path, f1[i].Path);
            Assert.Same(f0[i].Part, f1[i].Part);
        }
    }

    // ---- 4. factor 1: each layer moved by its offset, each component lifted ---

    [Fact]
    public void Factor1_DisplacesEachLayerByExactlyItsOffset_AndLiftsComponents()
    {
        var layout = SurfaceLayout();
        var assembly = layout.ToExplodedAssembly();
        var f0 = assembly.Flatten(0);
        var f1 = assembly.Flatten(1);
        var byPath0 = f0.ToDictionary(i => i.Path);
        var occByPath = new Dictionary<string, Occurrence>();
        foreach (var occ in assembly.Occurrences)
            occByPath[$"{assembly.Name}/{occ.Name}"] = occ;

        foreach (var inst in f1)
        {
            var disp = Origin(inst.World) - Origin(byPath0[inst.Path].World);
            var expected = occByPath[inst.Path].ExplodeOffset ?? Vector3d.Zero;
            Assert.True((disp - expected).Length < 1e-9);        // moved by exactly its offset
        }

        // The top surface part lifts +Z, the bottom one drops -Z.
        var r1 = f1.Single(i => Leaf(i.Path) == "R1");
        var r1_0 = f0.Single(i => Leaf(i.Path) == "R1");
        Assert.True(Origin(r1.World).Z - Origin(r1_0.World).Z > 0.5);
        var r2 = f1.Single(i => Leaf(i.Path) == "R2");
        var r2_0 = f0.Single(i => Leaf(i.Path) == "R2");
        Assert.True(Origin(r2.World).Z - Origin(r2_0.World).Z < -0.5);
    }

    // ---- 5. the embedded dogleg ----------------------------------------------

    [Fact]
    public void EmbeddedComponent_DoglegsStraightOutOfTheCavity_ThenSpreadsAside()
    {
        var layout = EmbeddedLayout();
        var assembly = layout.ToExplodedAssembly();
        var u1 = assembly.Occurrences.Single(o => o.Name == "U1");

        // It carries a dogleg (a surface part never does).
        Assert.NotEmpty(u1.ExplodePath);
        Assert.Single(u1.ExplodePath);

        // Leg 1 — the early motion is PURE +Z (straight up out of the cavity), no lateral yet.
        var early = u1.ExplodeDisplacement(1e-3);
        Assert.True(early.Z > 0);
        Assert.True(Math.Sqrt(early.X * early.X + early.Y * early.Y) < 1e-9);

        // The FINAL offset carries the lateral spread — that lateral leg IS the dogleg, which is why
        // an embedded offset is the one that is not pure Z.
        var offset = u1.ExplodeOffset!.Value;
        Assert.True(offset.Z > 0);
        Assert.True(Math.Sqrt(offset.X * offset.X + offset.Y * offset.Y) > 0.1);
        Assert.True((u1.ExplodeDisplacement(1) - offset).Length < 1e-12);   // reaches it exactly at 1
        Assert.Equal(Vector3d.Zero, u1.ExplodeDisplacement(0));             // and is home at 0

        // The surface part beside it stays pure Z.
        var r1 = assembly.Occurrences.Single(o => o.Name == "R1");
        Assert.Equal(0.0, r1.ExplodeOffset!.Value.X);
        Assert.Equal(0.0, r1.ExplodeOffset!.Value.Y);
        Assert.Empty(r1.ExplodePath);
    }

    [Fact]
    public void WithACavity_TheSlabsStillReassembleToThePlate()
    {
        var layout = EmbeddedLayout();
        var assembly = layout.ToExplodedAssembly();
        int physicalLayers = layout.Board.LayerStackup!.Layers.Count;
        double slabSum = assembly.Occurrences.Take(physicalLayers).Sum(o => o.Part!.MassProperties().Volume);
        double plate = layout.ExpectedPlateVolume();
        Assert.True(Math.Abs(slabSum - plate) / plate < 1e-3);   // cavity milled out of its slab too
    }

    // ---- 6. determinism ------------------------------------------------------

    [Fact]
    public void ToExplodedAssembly_IsDeterministic()
    {
        var layout = SurfaceLayout();
        var a = layout.ToExplodedAssembly();
        var b = layout.ToExplodedAssembly();
        Assert.Equal(a.Occurrences.Count, b.Occurrences.Count);
        for (int i = 0; i < a.Occurrences.Count; i++)
        {
            Assert.Equal(a.Occurrences[i].Name, b.Occurrences[i].Name);
            var oa = a.Occurrences[i].ExplodeOffset;
            var ob = b.Occurrences[i].ExplodeOffset;
            Assert.Equal(oa.HasValue, ob.HasValue);
            if (oa is { } va && ob is { } vb)
            {
                // bit-for-bit (deterministic arithmetic; no ordering that is not a function of the model)
                Assert.Equal(BitConverter.DoubleToInt64Bits(va.X), BitConverter.DoubleToInt64Bits(vb.X));
                Assert.Equal(BitConverter.DoubleToInt64Bits(va.Y), BitConverter.DoubleToInt64Bits(vb.Y));
                Assert.Equal(BitConverter.DoubleToInt64Bits(va.Z), BitConverter.DoubleToInt64Bits(vb.Z));
            }
        }
    }

    // ---- 7. guards shown to fire ---------------------------------------------

    [Fact]
    public void CopperOnlyBoard_IsRefusedByName()
    {
        // A board built the copper-only way carries a null LayerStackup: no physical layers to slice.
        var board = new PcbBoard(
            [new Vector2d(-10, -10), new Vector2d(10, -10), new Vector2d(10, 10), new Vector2d(-10, 10)],
            thickness: 1.6, stackup: PcbStackup.TwoLayer(1.6));
        Assert.Null(board.LayerStackup);
        var layout = new PcbLayout(new Schematic("s"), board);

        var ex = Assert.Throws<ArgumentException>(() => layout.ToExplodedAssembly());
        Assert.Contains("LayerStackup", ex.Message);
        Assert.Contains("copper-only", ex.Message);
    }

    [Fact]
    public void NegativeSpacing_IsRefusedByName()
    {
        var layout = new PcbLayout(new Schematic("s"), MultiBoard());
        var ex = Assert.Throws<ArgumentException>(() => layout.ToExplodedAssembly(spacing: -1));
        Assert.Contains("spacing", ex.Message);
    }
}
