using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// The exact curved STROKE. Every oracle here is a closed form, which is what the tier buys:
/// the polygonal <see cref="Region2dOffset.Stroke"/> can only inscribe its round joins and
/// caps, so its areas are short of the truth by a sagitta that no parameter removes, while a
/// stroke made of annular sectors and exact half-discs IS the path's Minkowski sum with a
/// disc and can be asserted as an equality.
/// </summary>
public class CurvedRegion2dStrokeTests
{
    private const double Tight = 1e-9;

    // ---- straight paths: the two tiers must agree where nothing is curved ----

    [Fact]
    public void StraightSegment_ButtCap_IsTheExactRectangle()
    {
        var stroke = CurvedRegion2dOffset.Stroke(
            [CurvedEdge2d.Line((0, 0), (10, 0))], width: 2, StrokeCap.Butt);
        var region = Assert.Single(stroke);
        Assert.Equal(20, region.Area, Tight);
        Assert.Equal(0, region.Bounds.Min.X, Tight);
        Assert.Equal(-1, region.Bounds.Min.Y, Tight);
        Assert.Equal(10, region.Bounds.Max.X, Tight);
        Assert.Equal(1, region.Bounds.Max.Y, Tight);
    }

    [Fact]
    public void StraightSegment_SquareCaps_ExtendHalfTheWidth()
    {
        var stroke = CurvedRegion2dOffset.Stroke(
            [CurvedEdge2d.Line((0, 0), (10, 0))], width: 2, StrokeCap.Square);
        var region = Assert.Single(stroke);
        Assert.Equal(24, region.Area, Tight); // 12 x 2
        Assert.Equal(-1, region.Bounds.Min.X, Tight);
        Assert.Equal(11, region.Bounds.Max.X, Tight);
    }

    /// <summary>
    /// The capsule is EXACT here — L*w + pi*(w/2)^2 to the last few bits — where the
    /// polygonal twin can only inscribe the two half-discs. That difference is the tier.
    /// </summary>
    [Fact]
    public void StraightSegment_RoundCaps_AreTheExactCapsule()
    {
        var stroke = CurvedRegion2dOffset.Stroke(
            [CurvedEdge2d.Line((0, 0), (10, 0))], width: 2, StrokeCap.Round);
        var region = Assert.Single(stroke);
        Assert.Equal(20 + Math.PI, region.Area, Tight);

        var polygonal = Region2dOffset.Stroke(
            [new Vector2d(0, 0), new Vector2d(10, 0)], width: 2, StrokeCap.Round);
        double inscribed = polygonal.Sum(r => r.Area);
        Assert.True(inscribed < region.Area, "the inscribed capsule must be the smaller one");
        Assert.Equal(region.Area, inscribed, 0.02); // and only by a sagitta
    }

    // ---- arcs: the annular sector, asserted against its closed form ----

    /// <summary>
    /// A quarter arc's slab is the annular sector between r ± w/2, whose area is
    /// (sweep/2)((r + w/2)^2 − (r − w/2)^2) = sweep*r*w — the squares cancel, which is why
    /// this is an equality rather than a bound. The two round caps add exactly one disc of
    /// radius w/2 between them, and they meet the sector along its radial ends without
    /// overlapping it, so the areas ADD.
    /// </summary>
    /// <remarks>The 3*pi/2 row also drives the HALVING path — a sector wider than half a turn
    /// is split so no primitive is ever an annulus — and its two pieces must rejoin with no
    /// seam, which an inexact shared radial would show up as a missing sliver.</remarks>
    [Theory]
    [InlineData(Math.PI / 2)]
    [InlineData(Math.PI / 3)]
    [InlineData(2 * Math.PI / 3)]
    [InlineData(3 * Math.PI / 2)]
    public void ArcWithRoundCaps_IsTheSectorPlusOneDisc(double sweep)
    {
        const double radius = 8, width = 3;
        var stroke = CurvedRegion2dOffset.Stroke(
            [CurvedEdge2d.Arc((0, 0), radius, 0.4, sweep)], width, StrokeCap.Round);
        var region = Assert.Single(stroke);
        double expected = sweep * radius * width + Math.PI * width * width / 4;
        Assert.Equal(expected, region.Area, expected * 1e-12);
    }

    [Fact]
    public void ArcWithButtCaps_IsExactlyTheAnnularSector()
    {
        const double radius = 8, width = 3, sweep = Math.PI / 2;
        var stroke = CurvedRegion2dOffset.Stroke(
            [CurvedEdge2d.Arc((0, 0), radius, 0, sweep)], width, StrokeCap.Butt);
        var region = Assert.Single(stroke);
        Assert.Equal(sweep * radius * width, region.Area, 1e-11);
        // The band reaches r + w/2 = 9.5 and no further: the bounds see the true arc extreme,
        // not a chord, because nothing was flattened.
        Assert.Equal(9.5, region.Bounds.Max.X, Tight);
        Assert.Equal(9.5, region.Bounds.Max.Y, Tight);
        Assert.Equal(0, region.Bounds.Min.X, Tight);
    }

    /// <summary>
    /// A clockwise arc is the same swept set as its reverse — the slab is two-sided, so the
    /// turn direction may not leak into the answer. (It does leak into
    /// <see cref="CurvedRegion2dOffset.Offset"/>, legitimately: there the material side
    /// decides which way "outward" points.)
    /// </summary>
    [Fact]
    public void ArcStroke_IsIndependentOfTheTurnDirection()
    {
        var forward = CurvedEdge2d.Arc((0, 0), 8, 0, Math.PI / 2);
        double a = CurvedRegion2dOffset.Stroke([forward], 3, StrokeCap.Round).Sum(r => r.Area);
        double b = CurvedRegion2dOffset.Stroke([forward.Reversed()], 3, StrokeCap.Round).Sum(r => r.Area);
        Assert.Equal(a, b, a * 1e-12);
    }

    /// <summary>
    /// A stroke wider than the arc's own diameter swallows the centre. The band's inner rim
    /// is then gone and the slab becomes the pie SECTOR of radius r + w/2 — still exact, and
    /// the assertion is that the area is the sector's rather than the annulus formula's
    /// (which would need a negative inner radius to be squared and would come out too small).
    /// </summary>
    [Fact]
    public void StrokeWiderThanTheArc_BecomesTheExactPieSector()
    {
        const double radius = 1, width = 6, sweep = Math.PI / 2; // half = 3 > r
        var stroke = CurvedRegion2dOffset.Stroke(
            [CurvedEdge2d.Arc((0, 0), radius, 0, sweep)], width, StrokeCap.Butt);
        var region = Assert.Single(stroke);
        double outer = radius + width / 2;
        Assert.Equal(sweep * outer * outer / 2, region.Area, 1e-11);
        Assert.Equal(0, region.Bounds.Min.X, Tight); // the sector reaches the centre
        Assert.Equal(0, region.Bounds.Min.Y, Tight);
    }

    // ---- circuits ----

    /// <summary>
    /// The cleanest exactness statement in the file: stroking a full circle of radius R with
    /// width w is the annulus between R ± w/2, whose area is exactly 2*pi*R*w — the squares
    /// cancel — and it carries a HOLE, which is the union doing its own topology.
    /// </summary>
    [Fact]
    public void FullCircle_StrokesToTheExactAnnulus()
    {
        const double radius = 10, width = 2;
        var stroke = CurvedRegion2dOffset.Stroke(
            [CurvedEdge2d.Circle((0, 0), radius)], width, StrokeCap.Butt);
        var region = Assert.Single(stroke);
        Assert.Equal(2 * Math.PI * radius * width, region.Area, 1e-9);
        var hole = Assert.Single(region.Holes);
        Assert.All(hole, edge => Assert.Equal(radius - width / 2, edge.Radius, Tight));
        Assert.Equal(radius + width / 2, region.Bounds.Max.X, Tight);
    }

    /// <summary>
    /// A closed chain is stroked as a circuit: the closing joint gets its joins and NO caps.
    /// A butt-capped square circuit is therefore the exact frame — four full-width slabs plus
    /// four corner squares — where treating it as an open path would leave a notch at the
    /// start corner. This is the documented contract difference from the polygonal twin.
    /// </summary>
    [Fact]
    public void ClosedChain_IsStrokedAsACircuitWithNoNotch()
    {
        const double side = 10, width = 2;
        var square = new[]
        {
            CurvedEdge2d.Line((0, 0), (side, 0)),
            CurvedEdge2d.Line((side, 0), (side, side)),
            CurvedEdge2d.Line((side, side), (0, side)),
            CurvedEdge2d.Line((0, side), (0, 0)),
        };
        var stroke = CurvedRegion2dOffset.Stroke(square, width, StrokeCap.Butt, OffsetJoin.Miter);
        var region = Assert.Single(stroke);
        // Outer square of side+width minus inner square of side-width.
        double expected = (side + width) * (side + width) - (side - width) * (side - width);
        Assert.Equal(expected, region.Area, 1e-9);
        Assert.Single(region.Holes);
        Assert.Equal(-width / 2, region.Bounds.Min.X, Tight);
    }

    /// <summary>
    /// The notch the circuit rule removes, MEASURED rather than asserted in prose. The same
    /// square through the polygonal twin — whose input can only spell closure by repeating
    /// the first point — leaves the start corner without a join, so it is short by exactly
    /// the 1x1 outer miter square there. Pinned so the filed residual against
    /// <see cref="Region2dOffset.Stroke"/> cannot rot into a guess.
    /// </summary>
    [Fact]
    public void ThePolygonalTwinKeepsTheCircuitNotch()
    {
        const double side = 10, width = 2;
        var square = new[]
        {
            CurvedEdge2d.Line((0, 0), (side, 0)),
            CurvedEdge2d.Line((side, 0), (side, side)),
            CurvedEdge2d.Line((side, side), (0, side)),
            CurvedEdge2d.Line((0, side), (0, 0)),
        };
        double curved = CurvedRegion2dOffset
            .Stroke(square, width, StrokeCap.Butt, OffsetJoin.Miter).Sum(r => r.Area);
        double polygonal = Region2dOffset.Stroke(
            [new Vector2d(0, 0), new Vector2d(side, 0), new Vector2d(side, side),
             new Vector2d(0, side), new Vector2d(0, 0)],
            width, StrokeCap.Butt, OffsetJoin.Miter).Sum(r => r.Area);

        Assert.Equal(144 - 64, curved, 1e-9);
        // Short by the one corner square the repeated point cannot claim a join for.
        Assert.Equal(curved - width / 2 * (width / 2), polygonal, 1e-9);
    }

    /// <summary>
    /// The polygonal twin closes the notch when it is TOLD the path is a circuit. It is a
    /// flag rather than a first-point-equals-last-point guess because a list of POINTS cannot
    /// express closure: repeating the first point is the only spelling available and it is
    /// ambiguous with a path that genuinely returns to where it started and stops. Both
    /// spellings agree once the flag is set.
    /// </summary>
    [Fact]
    public void ThePolygonalTwinClosesTheNotchWhenToldItIsACircuit()
    {
        const double side = 10, width = 2;
        Vector2d[] corners =
            [new(0, 0), new(side, 0), new(side, side), new(0, side)];
        Vector2d[] repeated = [.. corners, new Vector2d(0, 0)];

        double open = Region2dOffset
            .Stroke(repeated, width, StrokeCap.Butt, OffsetJoin.Miter).Sum(r => r.Area);
        double circuit = Region2dOffset
            .Stroke(corners, width, StrokeCap.Butt, OffsetJoin.Miter, closed: true).Sum(r => r.Area);
        double circuitRepeated = Region2dOffset
            .Stroke(repeated, width, StrokeCap.Butt, OffsetJoin.Miter, closed: true).Sum(r => r.Area);

        Assert.Equal(79, open, 1e-9);
        Assert.Equal(80, circuit, 1e-9);
        Assert.Equal(circuit, circuitRepeated, 1e-9);

        // ...and it now agrees with the curved twin, whose chain-of-edges input made closure
        // structural all along.
        var square = new[]
        {
            CurvedEdge2d.Line((0, 0), (side, 0)),
            CurvedEdge2d.Line((side, 0), (side, side)),
            CurvedEdge2d.Line((side, side), (0, side)),
            CurvedEdge2d.Line((0, side), (0, 0)),
        };
        Assert.Equal(
            CurvedRegion2dOffset.Stroke(square, width, StrokeCap.Butt, OffsetJoin.Miter).Sum(r => r.Area),
            circuit, 1e-9);
    }

    /// <summary>
    /// A circuit takes no caps, which is what the flag buys under a ROUND cap: an open stroke
    /// of the same points puts a half-disc at each end of the repeated start point, and a
    /// circuit does not.
    /// </summary>
    [Fact]
    public void ACircuitTakesNoCaps()
    {
        Vector2d[] corners = [new(0, 0), new(10, 0), new(10, 10), new(0, 10)];
        double circuit = Region2dOffset.Stroke(corners, 2, StrokeCap.Round, closed: true).Sum(r => r.Area);
        double butt = Region2dOffset.Stroke(corners, 2, StrokeCap.Butt, closed: true).Sum(r => r.Area);
        // Round joins are the only difference between the two on a circuit, and the caps are
        // absent from both — so the cap style cannot move the answer at all.
        Assert.Equal(butt, circuit, 1e-12);
    }

    /// <summary>Nothing that already worked moves: the default is <c>closed: false</c>, and
    /// an open stroke is bit-for-bit what it always was.</summary>
    [Fact]
    public void TheDefaultStaysTheOpenStroke()
    {
        Vector2d[] path = [new(0, 0), new(10, 0), new(10, 6)];
        var byDefault = Region2dOffset.Stroke(path, 2, StrokeCap.Round, OffsetJoin.Round);
        var stated = Region2dOffset.Stroke(
            path, 2, StrokeCap.Round, OffsetJoin.Round,
            Region2dOffset.DefaultMiterLimit, Region2dOffset.DefaultArcTolerance, closed: false);
        Assert.Equal(byDefault.Count, stated.Count);
        for (int i = 0; i < byDefault.Count; i++)
        {
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(byDefault[i].Area),
                BitConverter.DoubleToInt64Bits(stated[i].Area));
        }
    }

    /// <summary>
    /// Every CLOCKWISE joint of a stroked path keeps its outer corner fill. Negating both of
    /// a joint's normals does NOT flip the turn — <c>Cross(-a, -b) == Cross(a, b)</c> exactly
    /// — so offering only <c>(l0, l1)</c> and <c>(-l0, -l1)</c> refuses both wedges at a right
    /// turn and the outer one is the genuine gap. The deficit is exactly one miter corner per
    /// clockwise joint, which is what this measures: a right turn and a left turn of the same
    /// angle must stroke to the same area.
    /// </summary>
    [Fact]
    public void ClockwiseJointsKeepTheirOuterFill()
    {
        const double width = 2;
        var left = new[]
        {
            CurvedEdge2d.Line((0, 0), (10, 0)),
            CurvedEdge2d.Line((10, 0), (10, 10)),
        };
        var right = new[]
        {
            CurvedEdge2d.Line((0, 0), (10, 0)),
            CurvedEdge2d.Line((10, 0), (10, -10)),
        };
        double leftArea = CurvedRegion2dOffset
            .Stroke(left, width, StrokeCap.Butt, OffsetJoin.Miter).Sum(r => r.Area);
        double rightArea = CurvedRegion2dOffset
            .Stroke(right, width, StrokeCap.Butt, OffsetJoin.Miter).Sum(r => r.Area);

        // At a right angle the inner overlap square and the outer miter square are congruent
        // (both (w/2)²), so a correctly mitered elbow measures exactly the two slabs' area:
        // 2·L·w. Miss the outer fill and it reads (w/2)² short.
        Assert.Equal(2 * 10 * width, leftArea, 1e-9);
        Assert.Equal(leftArea, rightArea, 1e-9);
    }

    /// <summary>
    /// With round joins and round caps the two readings of a circuit agree as SETS — a full
    /// disc at the closing vertex contains the join wedge — so the contract difference above
    /// is invisible in exactly the configuration where it does not matter. Asserted by
    /// comparing the circuit against the same chain stroked with a duplicated closing edge
    /// (which makes the closure an ordinary interior joint).
    /// </summary>
    [Fact]
    public void RoundCircuit_AgreesWithTheSamePathClosedByHand()
    {
        var triangle = new[]
        {
            CurvedEdge2d.Line((0, 0), (12, 0)),
            CurvedEdge2d.Line((12, 0), (6, 9)),
            CurvedEdge2d.Line((6, 9), (0, 0)),
        };
        double circuit = CurvedRegion2dOffset.Stroke(triangle, 2).Sum(r => r.Area);
        // The same material as an OPEN path that walks one extra edge past the closure, so
        // the closing corner is an interior joint rather than a circuit joint.
        var overlapped = new[] { triangle[0], triangle[1], triangle[2], triangle[0] };
        double byHand = CurvedRegion2dOffset.Stroke(overlapped, 2).Sum(r => r.Area);
        Assert.Equal(circuit, byHand, circuit * 1e-9);
    }

    // ---- cross-checks against the other constructions of the same set ----

    /// <summary>
    /// The strongest check available, because it does not go through a formula at all:
    /// stroking a simple closed loop by w is the SAME SET as growing the region it bounds by
    /// w/2 and taking away the region shrunk by w/2. `Stroke` and `Offset` reach it by
    /// different primitives (full-width slabs and two-sided joins against one-sided slabs,
    /// plus the complement trick for the shrink), so agreement is two constructions checking
    /// each other rather than one checking its own arithmetic.
    /// </summary>
    [Theory]
    [InlineData(OffsetJoin.Round)]
    [InlineData(OffsetJoin.Miter)]
    [InlineData(OffsetJoin.Chamfer)]
    public void StrokingALoop_EqualsTheGrownRegionMinusTheShrunkOne(OffsetJoin join)
    {
        const double side = 10, width = 2;
        var loop = new[]
        {
            CurvedEdge2d.Line((0, 0), (side, 0)),
            CurvedEdge2d.Line((side, 0), (side, side)),
            CurvedEdge2d.Line((side, side), (0, side)),
            CurvedEdge2d.Line((0, side), (0, 0)),
        };
        double stroked = CurvedRegion2dOffset.Stroke(loop, width, StrokeCap.Butt, join).Sum(r => r.Area);

        var region = new CurvedRegion2d(loop);
        double grown = CurvedRegion2dOffset.Offset(region, width / 2, join).Sum(r => r.Area);
        double shrunk = CurvedRegion2dOffset.Offset(region, -width / 2, join).Sum(r => r.Area);
        Assert.Equal(grown - shrunk, stroked, Math.Abs(stroked) * 1e-9);
    }

    /// <summary>
    /// The same identity where the loop CARRIES an arc, so the annular-sector slab and the
    /// one-sided offset sector have to agree about the same curved band.
    /// </summary>
    [Fact]
    public void StrokingADisc_EqualsTheGrownDiscMinusTheShrunkOne()
    {
        const double radius = 7, width = 3;
        var disc = CurvedRegion2d.Disc((2, -1), radius);
        double stroked = CurvedRegion2dOffset
            .Stroke([.. disc.Outer], width, StrokeCap.Butt).Sum(r => r.Area);
        double grown = CurvedRegion2dOffset.Offset(disc, width / 2).Sum(r => r.Area);
        double shrunk = CurvedRegion2dOffset.Offset(disc, -width / 2).Sum(r => r.Area);
        Assert.Equal(grown - shrunk, stroked, Math.Abs(stroked) * 1e-9);
        Assert.Equal(2 * Math.PI * radius * width, stroked, 1e-9);
    }

    /// <summary>
    /// The seam case, and the reason it is safe. At a TANGENT-CONTINUOUS joint the two
    /// tangents are the same mathematical direction reached by different arithmetic, so in
    /// doubles they are not bit-equal — 6.1e-17 apart at this stadium's line-into-arc joints
    /// (cos(pi/2) is not 0), 2.4e-16 at a full circle's own seam (sin(2*pi) is not 0) — and
    /// the join's EXACT-zero cross test therefore does not skip them: a sliver sector of area
    /// ~1e-16 is emitted, exactly as <see cref="CurvedRegion2dOffset.Offset"/> has always
    /// emitted one at a full-circle seam. It is harmless because the arrangement SNAPS at
    /// 1e-9, so the sliver's vertices merge to one node and it contributes nothing: the
    /// tier's single tolerance doing its job rather than luck. Pinned here on a stadium,
    /// whose four joints are all tangent-continuous, by the strongest oracle available — an
    /// inexact answer would show up in BOTH assertions.
    /// </summary>
    [Fact]
    public void ATangentContinuousCircuit_IsUnharmedByItsSeamSliver()
    {
        const double length = 20, radius = 6, width = 3;
        var stadium = new[]
        {
            CurvedEdge2d.Line((-length / 2, -radius), (length / 2, -radius)),
            CurvedEdge2d.Arc((length / 2, 0), radius, -Math.PI / 2, Math.PI),
            CurvedEdge2d.Line((length / 2, radius), (-length / 2, radius)),
            CurvedEdge2d.Arc((-length / 2, 0), radius, Math.PI / 2, Math.PI),
        };
        double stroked = CurvedRegion2dOffset
            .Stroke(stadium, width, StrokeCap.Butt).Sum(r => r.Area);

        // Grow minus shrink, the independent construction.
        var region = new CurvedRegion2d(stadium);
        double grown = CurvedRegion2dOffset.Offset(region, width / 2).Sum(r => r.Area);
        double shrunk = CurvedRegion2dOffset.Offset(region, -width / 2).Sum(r => r.Area);
        Assert.Equal(grown - shrunk, stroked, Math.Abs(stroked) * 1e-9);
        // And the closed form: 2*L*w for the two straight runs, 2*pi*R*w for the two ends.
        Assert.Equal(2 * length * width + 2 * Math.PI * radius * width, stroked, 1e-8);
    }

    /// <summary>
    /// What the tier removes is a FLOOR, not a tolerance. Stroking an arc through the
    /// polygonal twin means flattening the arc first, so its answer is short of the truth by
    /// an amount set by the chord count and by nothing else; the curved answer is the limit
    /// those approach. Asserted as a monotone approach to the exact value, which is a claim
    /// no single polygonal run can make about itself.
    /// </summary>
    [Fact]
    public void ThePolygonalTwinApproachesTheCurvedAnswerFromBelow()
    {
        const double radius = 8, width = 3, sweep = Math.PI / 2;
        var arc = CurvedEdge2d.Arc((0, 0), radius, 0, sweep);
        double exact = CurvedRegion2dOffset
            .Stroke([arc], width, StrokeCap.Round).Sum(r => r.Area);

        double previous = 0;
        foreach (int chords in new[] { 4, 8, 16, 32 })
        {
            var points = new List<Vector2d>();
            for (int i = 0; i <= chords; i++)
                points.Add(arc.PointAt((double)i / chords));
            double flattened = Region2dOffset
                .Stroke(points, width, StrokeCap.Round).Sum(r => r.Area);
            Assert.True(flattened < exact, $"{chords} chords must under-measure the exact stroke");
            Assert.True(flattened > previous, $"{chords} chords must improve on the coarser run");
            previous = flattened;
        }
        // Still short at 32 chords: the deficit is the inscribed geometry, not noise.
        Assert.True(exact - previous > 1e-4, "a flattened stroke never reaches the exact one");
    }

    // ---- joints, self-crossing and refusals ----

    /// <summary>
    /// A tangent-continuous joint — a line running into an arc, the commonest joint a sketch
    /// makes — needs no join primitive at all: the two outward normals are equal, so the
    /// exact-zero cross test skips it. The oracle is the closed form of the whole footprint.
    /// </summary>
    [Fact]
    public void TangentContinuousJoint_NeedsNoCornerFill()
    {
        const double radius = 5, width = 2, run = 7;
        // A line along +x into a quarter arc that starts tangent to it.
        var line = CurvedEdge2d.Line((-run, 0), (0, 0));
        var arc = CurvedEdge2d.Arc((0, radius), radius, -Math.PI / 2, Math.PI / 2);
        Assert.Equal(0, line.End.DistanceTo(arc.Start), Tight);
        var stroke = CurvedRegion2dOffset.Stroke([line, arc], width, StrokeCap.Butt);
        var region = Assert.Single(stroke);
        // The two slabs meet along the shared radial and do not overlap, so a corner fill
        // would be visible as extra area — there is none.
        double expected = run * width + (Math.PI / 2) * radius * width;
        Assert.Equal(expected, region.Area, expected * 1e-12);
    }

    [Fact]
    public void RightAngleMiterJoint_IsTheExactSquareCorner()
    {
        var path = new[]
        {
            CurvedEdge2d.Line((0, 0), (10, 0)),
            CurvedEdge2d.Line((10, 0), (10, 10)),
        };
        var region = Assert.Single(
            CurvedRegion2dOffset.Stroke(path, width: 2, StrokeCap.Butt, OffsetJoin.Miter));
        // Two 10x2 slabs overlapping in the 1x1 square [9,10]x[0,1], plus the 1x1 OUTER miter
        // square at [10,11]x[-1,0]. The inner miter is offered too and adds nothing, which is
        // the point of offering both sides.
        Assert.Equal(20 + 20 - 1 + 1, region.Area, Tight);
        Assert.Equal(11, region.Bounds.Max.X, Tight);
    }

    /// <summary>
    /// An exact 180-degree reversal fills BOTH sides — that is what the two-sided join offer
    /// exists for — so a doubled-back path gets a round nose rather than a square end. The
    /// path stops short of its own start so this measures the REVERSAL and not the circuit
    /// rule.
    /// </summary>
    [Fact]
    public void DoubledBackPath_GetsARoundNose()
    {
        var path = new[]
        {
            CurvedEdge2d.Line((0, 0), (10, 0)),
            CurvedEdge2d.Line((10, 0), (2, 0)),
        };
        var region = Assert.Single(
            CurvedRegion2dOffset.Stroke(path, width: 2, StrokeCap.Butt));
        // The return slab lies inside the outbound one; the reversal's two half-discs make a
        // full disc at x = 10, of which only the half beyond it is new material.
        Assert.Equal(20 + Math.PI / 2, region.Area, Tight);
        Assert.Equal(11, region.Bounds.Max.X, Tight);
    }

    /// <summary>
    /// An out-and-back path that DOES return to its start is a circuit, so both ends are
    /// reversal joints and neither is capped — a round nose at each end even under
    /// <see cref="StrokeCap.Butt"/>, which is the honest answer (a butt cap there would cut
    /// through material the path genuinely sweeps).
    /// </summary>
    [Fact]
    public void OutAndBackToTheStart_IsACircuitWithTwoNoses()
    {
        var path = new[]
        {
            CurvedEdge2d.Line((0, 0), (10, 0)),
            CurvedEdge2d.Line((10, 0), (0, 0)),
        };
        var region = Assert.Single(
            CurvedRegion2dOffset.Stroke(path, width: 2, StrokeCap.Butt));
        Assert.Equal(20 + Math.PI, region.Area, Tight);
        Assert.Equal(-1, region.Bounds.Min.X, Tight);
        Assert.Equal(11, region.Bounds.Max.X, Tight);
    }

    /// <summary>A self-crossing path needs no special handling — the union covers the overlap
    /// once — and a path that loops encloses a hole.</summary>
    [Fact]
    public void SelfCrossingPath_UnionsInsteadOfFailing()
    {
        var path = new[]
        {
            CurvedEdge2d.Line((0, 0), (10, 0)),
            CurvedEdge2d.Line((10, 0), (10, 10)),
            CurvedEdge2d.Line((10, 10), (5, -5)),
        };
        var stroke = CurvedRegion2dOffset.Stroke(path, width: 1);
        var region = Assert.Single(stroke);
        Assert.True(region.Area > 0);
        Assert.Single(region.Holes); // the loop the crossing encloses
    }

    [Fact]
    public void ZeroLengthEdges_AreDroppedRatherThanRefused()
    {
        var path = new[]
        {
            CurvedEdge2d.Line((0, 0), (0, 0)),
            CurvedEdge2d.Line((0, 0), (10, 0)),
            CurvedEdge2d.Line((10, 0), (10, 0)),
        };
        var region = Assert.Single(CurvedRegion2dOffset.Stroke(path, 2, StrokeCap.Butt));
        Assert.Equal(20, region.Area, Tight);
    }

    [Fact]
    public void AGapInTheChain_IsRefusedByName()
    {
        var path = new[]
        {
            CurvedEdge2d.Line((0, 0), (10, 0)),
            CurvedEdge2d.Line((10.5, 0), (20, 0)),
        };
        var error = Assert.Throws<ArgumentException>(
            () => CurvedRegion2dOffset.Stroke(path, 2));
        Assert.Contains("not a chain", error.Message);
        Assert.Contains("edge 0 ends", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void ANonPositiveWidth_IsRefused(double width) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CurvedRegion2dOffset.Stroke([CurvedEdge2d.Line((0, 0), (1, 0))], width));

    [Fact]
    public void AnEmptyPath_IsRefused() =>
        Assert.Throws<ArgumentException>(() => CurvedRegion2dOffset.Stroke([], 2));

    /// <summary>Every threshold in the stroke is relative to the width or to the geometry, so
    /// the answer scales exactly with the model — the repo's scale-free rule, measured.</summary>
    [Theory]
    [InlineData(1e-3)]
    [InlineData(1.0)]
    [InlineData(1e3)]
    public void Strokes_AreScaleFree(double scale)
    {
        var path = new[]
        {
            CurvedEdge2d.Line((0, 0), (10 * scale, 0)),
            CurvedEdge2d.Arc((10 * scale, 4 * scale), 4 * scale, -Math.PI / 2, Math.PI / 2),
        };
        double area = CurvedRegion2dOffset.Stroke(path, 2 * scale).Sum(r => r.Area);
        // 10x2 slab + the sector sweep*r*w = (pi/2)*4*2 + one full disc of radius 1 from the
        // two round caps.
        double expected = (10 * 2 + (Math.PI / 2) * 4 * 2 + Math.PI) * scale * scale;
        Assert.Equal(expected, area, Math.Abs(expected) * 1e-9);
    }
}
