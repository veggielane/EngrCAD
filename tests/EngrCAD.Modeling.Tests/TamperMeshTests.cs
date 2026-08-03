using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The tamper mesh's deliverable is a GUARANTEE, so most of this file measures rather than
/// exercises: the route is a continuous bijection over its lattice (exact integer arithmetic),
/// the largest empty gap is found by certified branch and bound and held against the closed
/// form <c>½·hypot(pitchX, pitchY)</c>, the blind-corner configuration the closed form needs is
/// shown to EXIST rather than assumed, the copper area is an identity, and everything the
/// design refuses is refused by name.
/// </summary>
public class TamperMeshTests
{
    private static Aabb Wall(double width = 100, double height = 60) =>
        new(new Vector3d(0, 0, 0), new Vector3d(width, height, 0));

    // ---- the lattice: exact integer claims ----

    [Theory]
    [InlineData(0, 7, 3)]
    [InlineData(1, 5, 4)]
    [InlineData(2, 3, 2)]
    [InlineData(2, 1, 1)]
    [InlineData(3, 4, 1)]
    [InlineData(4, 2, 3)]
    public void TheTiledRouteIsAContinuousBijectionOverItsLattice(int order, int blocksX, int blocksY)
    {
        var route = TiledHilbertRoute.Build(order, blocksX, blocksY);
        int m = 1 << order;

        Assert.Equal(m * m * blocksX * blocksY, route.Length);

        // Bijective: every cell of the rectangle exactly once.
        var seen = new HashSet<(int, int)>(route.Length);
        foreach (var site in route)
        {
            Assert.InRange(site.X, 0, blocksX * m - 1);
            Assert.InRange(site.Y, 0, blocksY * m - 1);
            Assert.True(seen.Add((site.X, site.Y)), $"site {site} is visited twice");
        }

        // Continuous: every step is one lattice step, decided by Core's own exact integer test.
        for (int i = 1; i < route.Length; i++)
        {
            Assert.True(
                SpaceFillingCurve.AreNeighbours(SpaceFillingFamily.Hilbert, route[i - 1], route[i]),
                $"step {i} from {route[i - 1]} to {route[i]} is not a lattice step");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void OneBlockIsCoresOwnHilbertCurve(int order)
    {
        // The reduction that makes the tiling a generalisation rather than a second curve.
        var tiled = TiledHilbertRoute.Build(order, 1, 1);
        var core = SpaceFillingCurve.LatticeSites(SpaceFillingFamily.Hilbert, order);

        Assert.Equal(core.Count, tiled.Length);
        for (int i = 0; i < core.Count; i++)
            Assert.Equal(core[i], tiled[i]);
    }

    [Fact]
    public void BothTerminalsLieOnTheFootprintBoundary()
    {
        // A continuity monitor needs two ends, and a connector needs them at the edge.
        foreach (var (blocksHint, nets) in new[] { (100.0, 1), (100.0, 2), (40.0, 1) })
        {
            var mesh = TamperMesh.Over(Wall(blocksHint, 60), pitch: 5, traceWidth: 0.4, nets);
            foreach (var net in mesh.Nets)
            {
                foreach (var terminal in new[] { net.TerminalA, net.TerminalB })
                {
                    double toEdge = Math.Min(
                        Math.Min(terminal.X - mesh.Footprint.Min.X, mesh.Footprint.Max.X - terminal.X),
                        Math.Min(terminal.Y - mesh.Footprint.Min.Y, mesh.Footprint.Max.Y - terminal.Y));
                    // Cell CENTRES, so a terminal sits half a pitch in from the edge at most.
                    Assert.True(
                        toEdge <= Math.Max(mesh.PitchX, mesh.PitchY) / 2 + 1e-9,
                        $"terminal {terminal} is {toEdge} from the nearest footprint edge");
                }
            }
        }
    }

    // ---- the closed form, and the configuration it needs ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void TheMeasuredGapIsTheCellCircumradius_ForOneNet(int blockOrder)
    {
        var mesh = TamperMesh.Over(Wall(), pitch: 4, traceWidth: 0.6, nets: 1, blockOrder: blockOrder);
        var guarantee = mesh.Guarantee;

        // The closed form: every cell has the route through its centre, so no point of the
        // footprint is further from the route than a cell's circumradius — and that bound is
        // ATTAINED, which is what the next test establishes independently.
        double closedForm = 0.5 * Math.Sqrt(mesh.PitchX * mesh.PitchX + mesh.PitchY * mesh.PitchY);

        // The branch and bound returns a certified LOWER bound plus a bracket, so the true
        // maximum lies in [GapRadius, GapRadius + Uncertainty]. The closed form must sit there.
        Assert.InRange(closedForm, guarantee.GapRadius, guarantee.GapRadius + guarantee.Uncertainty + 1e-12);
        Assert.True(guarantee.Uncertainty <= Math.Min(mesh.PitchX, mesh.PitchY) * 1e-8);

        // And the two ends of the band come off it by arithmetic, so they are stated together.
        Assert.Equal(2 * guarantee.GapRadius - mesh.TraceWidth, guarantee.TouchDiameter, 12);
        Assert.Equal(2 * guarantee.GapRadius + mesh.TraceWidth, guarantee.SeverDiameter, 12);
    }

    [Fact]
    public void ABlindCornerExists_SoTheCircumradiusBoundIsAttainedRatherThanAssumed()
    {
        // A dual-grid corner is h/2 from the route when ANY of the (up to four) cell pairs
        // meeting there is consecutive on it, and the full circumradius when none is. The
        // guarantee's closed form is only tight if the second case occurs, so it is counted.
        static int BlindInteriorCorners(TamperMeshLayout mesh)
        {
            var order = new Dictionary<(int, int), int>(mesh.Route.Count);
            for (int i = 0; i < mesh.Route.Count; i++)
                order[(mesh.Route[i].X, mesh.Route[i].Y)] = i;

            int blind = 0;
            for (int i = 1; i < mesh.CellsX; i++)
            for (int j = 1; j < mesh.CellsY; j++)
            {
                (int, int)[] cells = [(i - 1, j - 1), (i, j - 1), (i - 1, j), (i, j)];
                bool consecutive = false;
                foreach (var a in cells)
                foreach (var b in cells)
                {
                    if (Math.Abs(a.Item1 - b.Item1) + Math.Abs(a.Item2 - b.Item2) == 1
                        && order[a] + 1 == order[b])
                    {
                        consecutive = true;
                    }
                }
                if (!consecutive)
                    blind++;
            }
            return blind;
        }

        // One plain Hilbert block: none below order 3, and they appear and multiply above it.
        Assert.Equal(0, BlindInteriorCorners(Block(2)));
        Assert.Equal(1, BlindInteriorCorners(Block(3)));
        Assert.Equal(9, BlindInteriorCorners(Block(4)));
        Assert.Equal(47, BlindInteriorCorners(Block(5)));

        // Which does NOT weaken the guarantee at low order: the footprint's own four corners
        // touch a single cell, so the circumradius is reached there whatever the route does.
        var low = Block(2);
        Assert.InRange(
            0.5 * Math.Sqrt(low.PitchX * low.PitchX + low.PitchY * low.PitchY),
            low.Guarantee.GapRadius, low.Guarantee.GapRadius + low.Guarantee.Uncertainty + 1e-12);

        static TamperMeshLayout Block(int order) => TamperMesh.Over(
            Wall(64, 64), pitch: 64.0 / (1 << order), traceWidth: 0.2, nets: 1, blockOrder: order);
    }

    [Fact]
    public void TheMeasuredGapAgreesWithAnIndependentBruteForceScan()
    {
        // The branch and bound is certified by a Lipschitz argument; this checks the argument
        // by a method that shares nothing with it — a dense scan, recomputing the distance to
        // the centrelines from scratch. A scan can only ever find LESS than the true maximum.
        var mesh = TamperMesh.Over(Wall(60, 40), pitch: 5, traceWidth: 0.5, nets: 2, blockOrder: 2);
        var guarantee = mesh.Guarantee;

        double worst = 0;
        const int samples = 400;
        for (int i = 0; i <= samples; i++)
        for (int j = 0; j <= samples; j++)
        {
            var p = new Vector2d(
                mesh.Footprint.Min.X + (mesh.Footprint.Max.X - mesh.Footprint.Min.X) * i / samples,
                mesh.Footprint.Min.Y + (mesh.Footprint.Max.Y - mesh.Footprint.Min.Y) * j / samples);
            worst = Math.Max(worst, DistanceToCopper(mesh, p));
        }

        Assert.True(
            worst <= guarantee.GapRadius + guarantee.Uncertainty,
            $"the scan found {worst}, past the certified bracket [{guarantee.GapRadius}, "
            + $"{guarantee.GapRadius + guarantee.Uncertainty}]");
        // And the scan gets close, so the bound is not vacuous.
        Assert.True(worst > guarantee.GapRadius * 0.98, $"the scan only reached {worst}");
    }

    [Fact]
    public void EveryDrillAtTheSeverDiameterCutsANet_AndOneBelowTheTouchDiameterCanMiss()
    {
        var mesh = TamperMesh.Over(Wall(60, 40), pitch: 5, traceWidth: 0.5, nets: 2, blockOrder: 2);
        var guarantee = mesh.Guarantee;
        double half = mesh.TraceWidth / 2;

        // Sever: a drill of diameter d centred at c cuts a net iff its disc contains a whole
        // cross-section, i.e. dist(c, centreline) <= d/2 - w/2. At d = SeverDiameter that is
        // exactly the measured worst gap, so it must hold EVERYWHERE.
        const int samples = 200;
        for (int i = 0; i <= samples; i++)
        for (int j = 0; j <= samples; j++)
        {
            var p = new Vector2d(
                mesh.Footprint.Min.X + (mesh.Footprint.Max.X - mesh.Footprint.Min.X) * i / samples,
                mesh.Footprint.Min.Y + (mesh.Footprint.Max.Y - mesh.Footprint.Min.Y) * j / samples);
            Assert.True(
                DistanceToCopper(mesh, p) <= guarantee.SeverDiameter / 2 - half + guarantee.Uncertainty,
                $"a drill of {guarantee.SeverDiameter} at {p} would not cut a net");
        }

        // Touch: at the weakest point a drill just under the touch diameter misses the copper
        // entirely — dist > d/2 + w/2 — which is what makes the band a band rather than a
        // single number.
        double justUnder = guarantee.TouchDiameter - 1e-6;
        Assert.True(DistanceToCopper(mesh, guarantee.WeakestPoint) > justUnder / 2 + half);
        Assert.True(guarantee.Defeats(guarantee.SeverDiameter));
        Assert.False(guarantee.Defeats(guarantee.TouchDiameter));
    }

    [Fact]
    public void ThePitchSizedForADrillDefeatsThatDrill()
    {
        // The design equation, closing its own loop: ask for the pitch, build at it, and read
        // the MEASURED guarantee back.
        foreach (double drill in new[] { 3.0, 5.0, 8.0 })
        {
            double pitch = TamperMesh.PitchForDrill(drill, traceWidth: 0.5);
            var mesh = TamperMesh.Over(Wall(), pitch, traceWidth: 0.5, nets: 1);

            Assert.True(mesh.PitchX <= pitch && mesh.PitchY <= pitch);
            Assert.True(
                mesh.Guarantee.Defeats(drill),
                $"a {drill} drill is not defeated at pitch {pitch}: sever needs "
                + $"{mesh.Guarantee.SeverDiameter}");
        }
    }

    [Fact]
    public void MoreNetsShrinkTheGap()
    {
        // Interleaved nets are there for the isolation monitor, but they also put more copper
        // in the way; the measurement says how much. Reported rather than claimed.
        double previous = double.PositiveInfinity;
        foreach (int nets in new[] { 1, 2, 3, 4 })
        {
            var mesh = TamperMesh.Over(Wall(), pitch: 4, traceWidth: 0.3, nets, blockOrder: 2);
            double gap = mesh.Guarantee.GapRadius;
            Assert.True(gap < previous, $"{nets} nets did not improve on {previous}");
            previous = gap;
        }
    }

    // ---- the copper ----

    [Fact]
    public void TheCopperAreaIsLengthTimesWidthExactly()
    {
        // A mitered ribbon's area is exactly its centreline length times its width: at every
        // corner the outer miter triangle is congruent to the inner notch. That identity is
        // only true if the ribbon does not overlap itself, so it doubles as the simplicity
        // check the boolean-free construction needs.
        foreach (int nets in new[] { 1, 3 })
        {
            var mesh = TamperMesh.Over(Wall(), pitch: 4, traceWidth: 0.45, nets, blockOrder: 2);
            foreach (var net in mesh.Nets)
                Assert.Equal(net.Length * net.TraceWidth, net.CopperArea, 9);
        }
    }

    [Fact]
    public void TheBooleanFreeRibbonAgreesWithRegion2dOffsetsOwnStroke()
    {
        // Two constructions of one shape: the ribbon is built from its two mitered offsets
        // with no boolean at all, while Region2dOffset.Stroke unions a slab per segment
        // through the arrangement. They must agree — and the stroke is far too slow to use at
        // mesh scale (O(E^2) in the arrangement), which is why the ribbon exists.
        //
        // The path here turns the SAME way at every corner on purpose. An alternating one
        // exposes a measured defect in Stroke, filed rather than worked around: it offers the
        // corner fill on both sides as AddCornerJoin(v, left0, left1) and (v, -left0, -left1),
        // but cross(-a, -b) == cross(a, b), so the gate admits both wedges or neither and a
        // CLOCKWISE corner loses its genuine outer fill. The deficit is exactly
        // (clockwise corners) x w^2/4, confirmed on six paths.
        Vector2d[] path = [new(0, 0), new(6, 0), new(6, 6), new(0, 6), new(0, 2), new(4, 2)];
        const double width = 0.6;
        var net = new TamperNet(0, path, width);

        double length = 0;
        for (int i = 1; i < path.Length; i++)
            length += path[i].DistanceTo(path[i - 1]);

        var stroked = Region2dOffset.Stroke(path, width, StrokeCap.Butt, OffsetJoin.Miter);
        Assert.Equal(length * width, stroked.Sum(r => r.Area), 9);
        Assert.Equal(length * width, net.CopperArea, 9);
    }

    [Fact]
    public void TheCopperStaysInsideTheFootprint()
    {
        var mesh = TamperMesh.Over(Wall(), pitch: 4, traceWidth: 0.5, nets: 3, blockOrder: 2);
        foreach (var net in mesh.Nets)
        {
            foreach (var p in net.Outline.Outer)
            {
                Assert.InRange(p.X, mesh.Footprint.Min.X - 1e-9, mesh.Footprint.Max.X + 1e-9);
                Assert.InRange(p.Y, mesh.Footprint.Min.Y - 1e-9, mesh.Footprint.Max.Y + 1e-9);
            }
        }
    }

    [Fact]
    public void NeighbouringNetsAreExactlyTheIsolationGapApart()
    {
        var mesh = TamperMesh.Over(Wall(), pitch: 4, traceWidth: 0.4, nets: 2, blockOrder: 2);

        double closest = double.PositiveInfinity;
        foreach (var p in mesh.Nets[0].Path)
        {
            for (int i = 1; i < mesh.Nets[1].Path.Count; i++)
                closest = Math.Min(closest, PointToSegment(p, mesh.Nets[1].Path[i - 1], mesh.Nets[1].Path[i]));
        }

        // Centreline to centreline is one pitch divided by the net count; the copper gap is
        // that less one full trace width, which is what an isolation monitor is watching.
        Assert.Equal(Math.Min(mesh.PitchX, mesh.PitchY) / 2, closest, 9);
        Assert.Equal(closest - mesh.TraceWidth, mesh.IsolationGap, 9);
        Assert.True(mesh.IsolationGap > 0);
    }

    // ---- the honest Hilbert-versus-serpentine number ----

    [Fact]
    public void TheStraightRunIsBoundedForEveryBlockOrderAboveZero()
    {
        // What Hilbert buys is NOT a better drill guarantee — a serpentine at the same pitch
        // measures the same circumradius. It is that the free space has no long straight
        // channel for a slot or a saw to run down, and this is that claim as a number.
        var serpentine = TamperMesh.Over(Wall(), pitch: 4, traceWidth: 0.4, nets: 1, blockOrder: 0);
        var hilbert = TamperMesh.Over(Wall(), pitch: 4, traceWidth: 0.4, nets: 1, blockOrder: 2);

        // Counted in CELLS. Core's own saturation test counts STEPS, which is one fewer.
        Assert.Equal(serpentine.CellsX, serpentine.LongestStraightRun);
        Assert.Equal(3, TamperMesh.Over(Wall(16, 16), 4, 0.4, 1, blockOrder: 2).LongestStraightRun);
        Assert.Equal(4, TamperMesh.Over(Wall(64, 64), 4, 0.4, 1, blockOrder: 4).LongestStraightRun);
        Assert.Equal(4, hilbert.LongestStraightRun);

        // ... and the guarantee really is the same, so the choice is about the channel alone.
        double serpentineForm = 0.5 * Math.Sqrt(
            serpentine.PitchX * serpentine.PitchX + serpentine.PitchY * serpentine.PitchY);
        Assert.InRange(
            serpentineForm,
            serpentine.Guarantee.GapRadius,
            serpentine.Guarantee.GapRadius + serpentine.Guarantee.Uncertainty + 1e-12);
    }

    // ---- placement, export, determinism ----

    [Fact]
    public void AFacePlacementHoldsTheStatedClearanceFromTheWallEdge()
    {
        var mesh = TamperMesh.Over(Sketch.Rectangle(100, 60), pitch: 4, traceWidth: 0.5, clearance: 1.5);

        Assert.Equal(-48.5, mesh.Footprint.Min.X, 9);
        Assert.Equal(48.5, mesh.Footprint.Max.X, 9);
        Assert.Equal(-28.5, mesh.Footprint.Min.Y, 9);
        Assert.Equal(28.5, mesh.Footprint.Max.Y, 9);
    }

    [Fact]
    public void TheDxfCarriesOneLayerPerNetPlusTheFootprint()
    {
        var mesh = TamperMesh.Over(Wall(40, 40), pitch: 6, traceWidth: 0.4, nets: 2, blockOrder: 1);
        var dxf = mesh.ToDxf();

        Assert.Equal(["FOOTPRINT", "NET0", "NET1"], dxf.Layers.OrderBy(l => l).ToArray());
        Assert.Equal(DxfUnits.Millimetres, dxf.Units);
    }

    [Fact]
    public void TwoBuildsAgreeBitForBit()
    {
        var a = TamperMesh.Over(Wall(), pitch: 4, traceWidth: 0.5, nets: 2, blockOrder: 2);
        var b = TamperMesh.Over(Wall(), pitch: 4, traceWidth: 0.5, nets: 2, blockOrder: 2);

        for (int n = 0; n < a.Nets.Count; n++)
        {
            Assert.Equal(a.Nets[n].Path.Count, b.Nets[n].Path.Count);
            for (int i = 0; i < a.Nets[n].Path.Count; i++)
            {
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(a.Nets[n].Path[i].X),
                    BitConverter.DoubleToInt64Bits(b.Nets[n].Path[i].X));
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(a.Nets[n].Path[i].Y),
                    BitConverter.DoubleToInt64Bits(b.Nets[n].Path[i].Y));
            }
        }
        Assert.Equal(a.Guarantee.GapRadius, b.Guarantee.GapRadius);
    }

    [Fact]
    public void TheConductorExtrudesToAClosedSolid()
    {
        var mesh = TamperMesh.Over(Wall(30, 20), pitch: 5, traceWidth: 0.5, nets: 1, blockOrder: 1);
        var solid = mesh.Nets[0].Conductor(0.035).ToMesh();

        Assert.True(solid.IsClosed);
        Assert.Equal(mesh.Nets[0].CopperArea * 0.035, MeshMassProperties.Compute(solid).Volume, 6);
    }

    // ---- refusals ----

    [Fact]
    public void ATraceAtOrAboveThePitchIsRefusedAsAShort()
    {
        var refusal = Assert.Throws<ArgumentException>(
            () => TamperMesh.Over(Wall(), pitch: 4, traceWidth: 2.5, nets: 2));
        Assert.Contains("electrical short", refusal.Message);

        // Exactly at the gap is refused too: touching copper is a short, not a tolerance.
        var mesh = TamperMesh.Over(Wall(), pitch: 4, traceWidth: 0.1, nets: 2);
        double gap = Math.Min(mesh.PitchX, mesh.PitchY) / 2;
        Assert.Throws<ArgumentException>(() => TamperMesh.Over(Wall(), 4, gap, nets: 2));
    }

    [Fact]
    public void ADrillNoWiderThanTheTraceIsRefusedByName()
    {
        var refusal = Assert.Throws<ArgumentOutOfRangeException>(
            () => TamperMesh.PitchForDrill(drillDiameter: 0.5, traceWidth: 0.6));
        Assert.Contains("no positive solution", refusal.Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => TamperMesh.PitchForDrill(0.6, 0.6));
    }

    [Fact]
    public void ANonRectangularWallIsRefusedByName()
    {
        // A round or cut-out wall would break the route into runs, and a broken net cannot be
        // monitored — so it is refused rather than filled with a fill that reports runs.
        foreach (var face in new[]
        {
            Sketch.Circle(40),
            Sketch.Rectangle(100, 60).WithHole(Sketch.Circle(6)),
            Sketch.Polygon([(0, 0), (60, 0), (60, 40), (30, 55), (0, 40)]),
        })
        {
            var refusal = Assert.Throws<ArgumentException>(
                () => TamperMesh.Over(face, pitch: 4, traceWidth: 0.5));
            Assert.Contains("cannot be monitored for continuity", refusal.Message);
        }
    }

    [Fact]
    public void AConformalPlacementOnACurvedWallIsNotOfferedAtAll()
    {
        // Stated as a boundary rather than approximated: the only placement in the API is a
        // planar footprint, so a doubly-curved wall cannot be spelled by accident.
        Assert.Null(typeof(TamperMesh).GetMethod("OverSurface"));
    }

    [Fact]
    public void TheCellCapIsRefusedWithTheFinestPitchItAllows()
    {
        var refusal = Assert.Throws<ArgumentOutOfRangeException>(
            () => TamperMesh.Over(Wall(), pitch: 0.05, traceWidth: 0.01));
        Assert.Contains("cell cap", refusal.Message);
        Assert.Contains("FINEST pitch", refusal.Message);

        // An absurdly fine pitch must REPORT the cap rather than walk to it one block at a
        // time: the block count is estimated in floating point and checked against the cap
        // before any integer refinement runs.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TamperMesh.Over(Wall(), pitch: 1e-12, traceWidth: 1e-13));
    }

    [Theory]
    [InlineData(0, 0.5, 2)]
    [InlineData(4, 0, 2)]
    [InlineData(4, 0.5, 0)]
    [InlineData(-1, 0.5, 2)]
    public void NonsensicalInputsAreRefused(double pitch, double traceWidth, int nets)
    {
        Assert.ThrowsAny<ArgumentException>(() => TamperMesh.Over(Wall(), pitch, traceWidth, nets));
    }

    [Fact]
    public void AClearanceThatEatsTheWallIsRefused()
    {
        var refusal = Assert.Throws<ArgumentException>(
            () => TamperMesh.Over(Sketch.Rectangle(20, 8), pitch: 2, traceWidth: 0.3, clearance: 5));
        Assert.Contains("leaves nothing", refusal.Message);
    }

    // ---- helpers ----

    private static double DistanceToCopper(TamperMeshLayout mesh, in Vector2d p)
    {
        double best = double.PositiveInfinity;
        foreach (var net in mesh.Nets)
        {
            for (int i = 1; i < net.Path.Count; i++)
                best = Math.Min(best, PointToSegment(p, net.Path[i - 1], net.Path[i]));
        }
        return best;
    }

    private static double PointToSegment(in Vector2d p, in Vector2d a, in Vector2d b)
    {
        var ab = b - a;
        double t = Math.Clamp((p - a).Dot(ab) / ab.LengthSquared, 0, 1);
        return p.DistanceTo(a + ab * t);
    }
}
