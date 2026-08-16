using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The fill's own claims: the achieved spacing is reported rather than the request echoed, a
/// run steps exactly that spacing, every point clears the wall by the stated clearance (decided
/// against the sketch's EXACT signed distance, so there is no tolerance in the clip), coverage
/// is measured through the stroke rather than inferred from the path length, and the two ways a
/// fill can silently miss are refused by name.
/// </summary>
public class SpaceFillingInfillTests
{
    private static Sketch Plate() => Sketch.Rectangle(60, 40).WithHole(Sketch.Circle(8));

    [Fact]
    public void TheAchievedSpacingIsReported_NotTheRequest()
    {
        var fill = SpaceFillingInfill.Fill(Plate(), 5.0);

        Assert.Equal(5.0, fill.RequestedSpacing);
        Assert.True(fill.Spacing < fill.RequestedSpacing);
        // 60 wide, so the bounding square is 60 and 60/2^4 = 3.75 is the first cell size at or
        // under 5. The order is an integer, so the surplus HAS to land somewhere; it lands here.
        Assert.Equal(4, fill.Order);
        Assert.Equal(3.75, fill.Spacing, 12);
    }

    [Fact]
    public void WithinARun_ConsecutivePointsAreExactlyTheAchievedSpacingApart()
    {
        foreach (var family in new[]
        {
            SpaceFillingFamily.Hilbert, SpaceFillingFamily.Moore,
            SpaceFillingFamily.Peano, SpaceFillingFamily.Gosper,
        })
        {
            var fill = SpaceFillingInfill.Fill(Plate(), 5.0, family);
            Assert.NotEmpty(fill.Runs);
            foreach (var run in fill.Runs)
            {
                for (int i = 1; i < run.Count; i++)
                    Assert.Equal(fill.Spacing, run[i].DistanceTo(run[i - 1]), 9);
            }
            // The reported length is the sum over runs and nothing else, so it can be checked
            // against the step count without re-walking the geometry.
            int steps = fill.Runs.Sum(r => Math.Max(0, r.Count - 1));
            Assert.Equal(steps * fill.Spacing, fill.Length, 9);
        }
    }

    [Fact]
    public void EveryPointClearsTheWall_ByTheExactSignedDistance()
    {
        var plate = Plate();
        var field = new SketchRegion(plate);
        foreach (double clearance in new[] { 0.0, 1.0, 3.0 })
        {
            var fill = SpaceFillingInfill.Fill(plate, 6.0, clearance: clearance);
            foreach (var run in fill.Runs)
            foreach (var point in run)
            {
                // Exact: the field is the sketch's own signed distance, so this is the very
                // number the clip compared, not a re-derivation of it.
                Assert.True(
                    field.SignedDistance(point) <= -clearance,
                    $"({point.X}, {point.Y}) reads {field.SignedDistance(point)} against -{clearance}");
            }
            Assert.True(fill.PointCount > 0);
        }
    }

    [Fact]
    public void TheHoleIsRespected()
    {
        var fill = SpaceFillingInfill.Fill(Plate(), 4.0, clearance: 0);
        foreach (var run in fill.Runs)
        foreach (var point in run)
            Assert.True(point.Length >= 8, $"({point.X}, {point.Y}) is inside the Ø16 bore");
    }

    [Fact]
    public void AMooreFillOfItsOwnSquare_IsOneClosedRun()
    {
        // The square exactly matches the curve's own footprint and nothing is clipped, so the
        // clip leaves ONE run and the Moore curve's closure survives it: the last point is a
        // step from the first, which no Hilbert fill of the same square gives.
        var square = Sketch.Rectangle(40, 40);
        var moore = SpaceFillingInfill.Fill(square, 3.0, SpaceFillingFamily.Moore, clearance: 0);
        Assert.Single(moore.Runs);
        Assert.Equal(0, moore.TravelMoves);
        Assert.Equal(moore.Spacing, moore.Runs[0][^1].DistanceTo(moore.Runs[0][0]), 9);

        var hilbert = SpaceFillingInfill.Fill(square, 3.0, SpaceFillingFamily.Hilbert, clearance: 0);
        Assert.Single(hilbert.Runs);
        Assert.True(hilbert.Runs[0][^1].DistanceTo(hilbert.Runs[0][0]) > hilbert.Spacing * 2);
    }

    [Fact]
    public void CoverageIsMeasured_RisesWithOrder_AndNeverExceedsLengthTimesWidth()
    {
        var plate = Plate();
        double previous = 0;
        foreach (double spacing in new[] { 8.0, 5.0, 3.0 })
        {
            var fill = SpaceFillingInfill.Fill(plate, spacing);
            double covered = fill.CoveredArea();

            // A stroke of length L and width w cannot cover more than L*w plus its two caps —
            // it covers LESS wherever the path turns back over itself, which is the whole
            // reason coverage is measured rather than taken as L*w.
            double capArea = Math.PI * fill.Spacing * fill.Spacing / 4;
            Assert.True(
                covered <= fill.Length * fill.Spacing + fill.Runs.Count * capArea + 1e-9,
                $"covered {covered} against L*w {fill.Length * fill.Spacing}");
            Assert.True(covered <= fill.RegionArea + 1e-9);
            Assert.True(covered > previous, $"spacing {spacing}: {covered} did not beat {previous}");
            previous = covered;
        }
        Assert.True(previous / Plate().ToRegions().Sum(r => r.Area) > 0.75);
    }

    // ---- the two silent misses, both refused ----

    [Fact]
    public void ARegionThinnerThanTheSpacing_IsRefusedByName()
    {
        var thin = Sketch.Rectangle(80, 1.5);
        var error = Assert.Throws<ArgumentException>(() => SpaceFillingInfill.Fill(thin, 3.0));
        Assert.Contains("would miss it entirely", error.Message);
        Assert.Contains("clearance: 0", error.Message);

        // Dropping the clearance does NOT rescue it, and the second message is the one that
        // says why: the achieved spacing is set by the plate's LENGTH (the curve's footprint is
        // its bounding square), so at 80 x 1.5 the cells are 2.5 wide and no centre row lands
        // in a 1.5-thick strip however the clearance is set.
        var stepped = Assert.Throws<ArgumentException>(
            () => SpaceFillingInfill.Fill(thin, 3.0, clearance: 0));
        Assert.Contains("stepped over it", stepped.Message);

        // The mutation control: a plate thick enough for the SAME request fills happily, so the
        // refusal is about the thinness rather than about long plates.
        Assert.True(SpaceFillingInfill.Fill(Sketch.Rectangle(80, 12), 3.0).PointCount > 0);
    }

    [Fact]
    public void APieceTheLatticeStepsOver_IsRefusedByName()
    {
        // A region in two pieces: a U-shaped outline and a detached square in its mouth (a
        // "hole" that lies outside its outer loop is a piece, not a hole — Region2d.FromLoops
        // sorts that out by containment depth). The bounding square is [0, 32]^2 whatever the
        // small piece does, so at a 3.0 request the cells are exactly 2 wide with centres at
        // odd coordinates; the piece is sized and placed so that eroding it by the 1.0
        // clearance leaves room at 14.2..14.8 by 20.2..20.8, which contains no centre at all.
        // Alignment, not tolerance — the same family as the recorded lattice traps.
        var u = Sketch.Start(0, 0).LineTo(32, 0).LineTo(32, 32).LineTo(24, 32)
                     .LineTo(24, 8).LineTo(8, 8).LineTo(8, 32).LineTo(0, 32).Close();
        var detached = Sketch.Start(13.2, 19.2).LineTo(15.8, 19.2)
                             .LineTo(15.8, 21.8).LineTo(13.2, 21.8).Close();

        var error = Assert.Throws<ArgumentException>(
            () => SpaceFillingInfill.Fill(u.WithHole(detached), 3.0));
        Assert.Contains("stepped over it", error.Message);
        Assert.Contains("14.2", error.Message);

        // The fixture must still CARRY the configuration it exists to test: the U on its own
        // fills happily, so the refusal is about the piece rather than about the outline.
        Assert.True(SpaceFillingInfill.Fill(u, 3.0).PointCount > 0);
    }

    [Fact]
    public void RefusesByName()
    {
        var plate = Plate();
        Assert.Throws<ArgumentNullException>(() => SpaceFillingInfill.Fill(null!, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpaceFillingInfill.Fill(plate, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpaceFillingInfill.Fill(plate, 3, clearance: -1));

        var zorder = Assert.Throws<ArgumentOutOfRangeException>(
            () => SpaceFillingInfill.Fill(plate, 3, SpaceFillingFamily.ZOrder));
        Assert.Contains("spatial ORDERING", zorder.Message);

        // A self-intersecting outline is refused by Region2d's own simplicity guard, which
        // names the crossing segments — "inside" is exactly what the clip is asking, and a
        // bow tie has no answer that does not depend on an arbitrary fill rule.
        var bowTie = Sketch.Start(0, 0).LineTo(10, 0).LineTo(0, 6).LineTo(4, 6).Close();
        var crossing = Assert.Throws<ArgumentException>(() => SpaceFillingInfill.Fill(bowTie, 1));
        Assert.Contains("crosses itself", crossing.Message);
    }

    [Fact]
    public void TheReportIsSelfConsistent()
    {
        var fill = SpaceFillingInfill.Fill(Plate(), 4.0);
        Assert.Equal(fill.Runs.Sum(r => r.Count), fill.PointCount);
        Assert.Equal(fill.Runs.Count(r => r.Count < 2), fill.IsolatedPoints);
        Assert.Equal(Math.Max(0, fill.Runs.Count - 1), fill.TravelMoves);

        // RegionArea is the FLATTENED area, not Sketch.Area()'s exact one: the footprint is
        // polygonal too, so the two sides of CoveredFraction are measured the same way. The
        // flattened plate reads 2198.97 against the exact 2198.94, the chord error of the bore.
        Assert.Equal(Plate().ToRegions().Sum(r => r.Area), fill.RegionArea, 9);
        Assert.True(fill.RegionArea > Plate().Area());
        Assert.Equal(fill.Curve.Spacing, fill.Spacing);
        Assert.Equal(fill.Curve.Order, fill.Order);
    }

    // ---- the tiled footprint, the run linker and the neck measure ----

    [Fact]
    public void ATILEDFillStopsWastingTheCurveOnALongThinPlate()
    {
        // The residual the tiled footprint exists for, measured on both sides: a square
        // footprint over an 80 x 12 plate generates a curve that is mostly outside it.
        var plate = Sketch.Rectangle(80, 12);

        var square = SpaceFillingInfill.Fill(plate, 3.0);
        var tiled = SpaceFillingInfill.Fill(plate, 3.0, tiled: true);

        Assert.True(square.Waste > 0.8, $"the square footprint wasted only {square.Waste:P1}");
        Assert.True(tiled.Waste < 0.25, $"the tiled footprint wasted {tiled.Waste:P1}");
        Assert.True(tiled.Curve.Points.Count < square.Curve.Points.Count / 4);

        // And the achieved spacing stops being set by the plate's LENGTH.
        Assert.True(tiled.Curve.SpacingY <= 3.0);
        Assert.True(tiled.Curve.SpacingY > square.Spacing);
    }

    [Fact]
    public void ATiledFillStillClearsTheWallAtEveryPoint()
    {
        var plate = Sketch.Rectangle(80, 12);
        var fill = SpaceFillingInfill.Fill(plate, 3.0, tiled: true, clearance: 1.0);
        var field = new SketchRegion(plate);

        foreach (var run in fill.Runs)
        {
            foreach (var p in run)
                Assert.True(field.SignedDistance(p) <= -1.0 + 1e-12);
        }
    }

    [Fact]
    public void ATiledFillIsRefusedForAFamilyWithNoBlockEndsToLink()
    {
        var plate = Sketch.Rectangle(80, 12);
        foreach (var family in new[] { SpaceFillingFamily.Moore, SpaceFillingFamily.Peano, SpaceFillingFamily.Gosper })
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(
                () => SpaceFillingInfill.Fill(plate, 3.0, family, tiled: true));
            Assert.Contains("Hilbert", error.Message);
        }
    }

    [Fact]
    public void TheLINKERShortensTheTravelAndIsAPermutation()
    {
        // A plate with a big bore, so the clip leaves the curve in many runs and the order it
        // came back in leaves ends to pick up.
        var plate = Sketch.Rectangle(60, 40).WithHole(Sketch.Circle(11));
        var fill = SpaceFillingInfill.Fill(plate, 2.0);
        Assert.True(fill.Runs.Count > 4, $"only {fill.Runs.Count} runs — the fixture is not exercising the linker");

        var linkage = fill.Link();

        Assert.Equal(fill.Runs.Count, linkage.Order.Count);
        Assert.Equal(fill.Runs.Count, linkage.Order.Select(o => o.Index).Distinct().Count());
        Assert.True(linkage.TravelLength <= linkage.SourceOrderTravelLength + 1e-9,
            $"linked {linkage.TravelLength} against curve order {linkage.SourceOrderTravelLength}");
        Assert.True(linkage.Improvement >= 1.0);

        // Reordering never drops or duplicates a point, which is the property that makes a
        // linker safe to apply: it can shorten the travel and cannot lose a pass.
        var reordered = linkage.Reorder(fill.Runs);
        Assert.Equal(fill.PointCount, reordered.Sum(r => r.Count));
    }

    [Fact]
    public void TheLinkerIsDeterministicAndReversesRunsWhereThatIsShorter()
    {
        var plate = Sketch.Rectangle(60, 40).WithHole(Sketch.Circle(11));
        var a = SpaceFillingInfill.Fill(plate, 2.0).Link();
        var b = SpaceFillingInfill.Fill(plate, 2.0).Link();

        Assert.Equal(a.Order, b.Order);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.TravelLength), BitConverter.DoubleToInt64Bits(b.TravelLength));

        // MEASURED, and worth stating rather than asserting the opposite: on a space-filling
        // fill the linker reverses NOTHING, because the curve order already leaves every run
        // pointing at its successor — which is the structural reason greedy is the right
        // heuristic here and the reason the improvement is modest.
        Assert.DoesNotContain(a.Order, o => o.Reversed);
    }

    [Fact]
    public void TheLinkerDoesReverseARunWhereThatIsShorter()
    {
        // The capability, pinned on a hand-built case rather than hoped for from a fill: run 1
        // is laid backwards, so drawing it in reverse costs 1 where drawing it forwards costs 9.
        var ends = new (Vector3d Start, Vector3d End)[]
        {
            (new(0, 0, 0), new(10, 0, 0)),
            (new(20, 0, 0), new(11, 0, 0)),
        };
        var linkage = RunLinker.Link(ends);

        Assert.Equal(new LinkedRun(0, false), linkage.Order[0]);
        Assert.Equal(new LinkedRun(1, true), linkage.Order[1]);
        Assert.Equal(1.0, linkage.TravelLength, 12);
        Assert.Equal(10.0, linkage.SourceOrderTravelLength, 12);
    }

    [Fact]
    public void TheTHINNESTFeatureSeesANeckThePerPieceRefusalCannot()
    {
        // A dumbbell: two fat pads joined by a 2-wide bar. The erosion leaves ONE connected
        // piece and the curve reaches it, so both refusals stay silent — and the coverage
        // fraction alone does not say WHERE. This does.
        var dumbbell = Sketch.Start(0, 0)
            .LineTo(20, 0).LineTo(20, 9).LineTo(40, 9).LineTo(40, 0).LineTo(60, 0)
            .LineTo(60, 20).LineTo(40, 20).LineTo(40, 11).LineTo(20, 11).LineTo(20, 20)
            .LineTo(0, 20).Close();

        var fill = SpaceFillingInfill.Fill(dumbbell, 4.0, clearance: 0.5);
        var thickness = fill.ThinnestFeature(samplesPerEdge: 4);

        Assert.Equal(2.0, thickness.Minimum, 9);
        Assert.InRange(thickness.ThinnestAt.X, 20, 40);
        // The neck cannot hold a pass at this spacing and clearance, and the two numbers say so
        // together: 2 of room against a pass wanting Spacing + 2 x Clearance.
        Assert.True(thickness.Minimum < fill.Spacing + 2 * fill.Clearance);
    }
}

/// <summary>
/// The exact-footprint option: the curved-tier stroke whose round joins and caps are exact
/// sectors and half-discs, so the footprint IS the path's Minkowski sum with the bead disc
/// and the polygonal route is inscribed in it — a one-sided ordering the tests assert both
/// as the fraction inequality on a real fill and as closed-form equalities on hand-built
/// single-run paths, where the polygonal twin can only approach from below.
/// </summary>
public class ExactInfillFootprintTests
{
    private static Sketch Plate() => Sketch.Rectangle(40, 30);

    [Fact]
    public void TheExactFraction_DominatesThePolygonal_OnARealFill()
    {
        var fill = SpaceFillingInfill.Fill(Plate(), 4.0);
        double polygonal = fill.CoveredFraction();
        double exact = fill.ExactCoveredFraction();
        Assert.True(exact > polygonal,
            $"exact {exact:0.####} must exceed the inscribed polygonal {polygonal:0.####}");
        Assert.InRange(exact, polygonal, 1.0);
        // Determinism: the curved union is as deterministic as the polygonal one.
        Assert.Equal(exact, fill.ExactCoveredFraction(), 15);
    }

    [Fact]
    public void ASingleStraightRun_IsTheStadiumClosedForm_Exactly()
    {
        // A hand-built path: one straight run of length 10 at bead 2 — the exact stroke is
        // the stadium L·w + π(w/2)², an EQUALITY (round caps are exact half-discs), where
        // the polygonal twin approaches from below.
        var region = Sketch.Rectangle(40, 30).ToRegions();
        var curve = SpaceFillingCurve.Over(
            new Aabb(new Vector3d(-20, -15, 0), new Vector3d(20, 15, 0)), SpaceFillingFamily.Hilbert, 4.0);
        var path = new InfillPath(
            curve, 0, [new[] { new Vector2d(-5, 0), new Vector2d(5, 0) }], region);
        double area = path.ExactFootprint(2.0).Sum(r => r.Area);
        Assert.Equal(10 * 2 + Math.PI, area, 9);
        Assert.True(path.Footprint(2.0).Sum(r => r.Area) < area);

        // And an isolated point's exact footprint is the full round-cap disc.
        var point = new InfillPath(
            curve, 0, [new[] { new Vector2d(0, 0) }], region);
        Assert.Equal(Math.PI, point.ExactFootprint(2.0).Sum(r => r.Area), 9);
    }
}
