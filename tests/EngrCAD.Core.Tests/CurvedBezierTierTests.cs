using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// The CUBIC half of the curved 2D tier: a Bézier crosses the arrangement, the boolean and
/// the area integral unflattened, exactly as a line and an arc do.
///
/// <para>The oracles are closed forms wherever one exists. The workhorse is the ARCH: the
/// cubic (0,0) → (0,h) → (w,h) → (w,0) closed by the straight chord back along y = 0 encloses
/// exactly <c>0.6·w·h</c>, because ∫y dx = 18wh∫t²(1−t)² dt = 18wh·B(3,3) = 3wh/5. That is a
/// rational number a chorded route cannot reach and a wrong Green's term cannot fake.</para>
/// </summary>
public class CurvedBezierTierTests
{
    private const double Width = 4;
    private const double Height = 3;

    private static CurvedEdge2d Arch() =>
        CurvedEdge2d.Bezier((0, 0), (0, Height), (Width, Height), (Width, 0));

    /// <summary>The arch closed by its chord — signed area exactly 0.6·w·h.</summary>
    private static CurvedRegion2d ArchRegion() =>
        new([Arch(), CurvedEdge2d.Line((Width, 0), (0, 0))]);

    [Fact]
    public void TheGreensTermIsExactForACubic()
    {
        // x·y′ − y·x′ is a QUINTIC, so integrating it term by term is arithmetic rather than
        // a quadrature — the answer is the rational 3wh/5 to round-off.
        Assert.Equal(0.6 * Width * Height, ArchRegion().Area, 12);
    }

    [Fact]
    public void ADiscretisedArchNeverReachesTheClosedForm()
    {
        // The point of the tier, stated as a table: flattening is a FLOOR, not a tolerance.
        double exact = 0.6 * Width * Height;
        double previous = double.PositiveInfinity;
        foreach (int chords in (int[])[8, 32, 128])
        {
            var points = new List<Vector2d>();
            for (int i = 0; i < chords; i++)
                points.Add(Arch().PointAt((double)i / chords));
            points.Add(new Vector2d(Width, 0));
            double error = Math.Abs(new Region2d(points).Area - exact);
            Assert.True(error > 0, "a flattened arch cannot be exact");
            Assert.True(error < previous, $"{chords} chords should be closer than the previous row");
            previous = error;
        }
        // ...and the exact route is not merely closer, it is exact.
        Assert.Equal(exact, ArchRegion().Area, 12);
    }

    [Fact]
    public void BoundsAreTheCurvesNotTheControlPolygons()
    {
        // The peak is at t = 0.5, where y = 0.75·h — strictly inside the control polygon's
        // own box of height h, so a hull bound would be measurably loose.
        var box = Arch().Bounds();
        Assert.Equal(0, box.Min.X, 12);
        Assert.Equal(0, box.Min.Y, 12);
        Assert.Equal(Width, box.Max.X, 12);
        Assert.Equal(0.75 * Height, box.Max.Y, 12);
    }

    [Fact]
    public void SubIsExactBecauseACubicIsDeterminedByItsHermiteData()
    {
        var arch = Arch();
        var piece = arch.Sub(0.2, 0.7);
        for (int i = 0; i <= 50; i++)
        {
            double s = i / 50.0;
            var expected = arch.PointAt(0.2 + 0.5 * s);
            var actual = piece.PointAt(s);
            Assert.Equal(expected.X, actual.X, 12);
            Assert.Equal(expected.Y, actual.Y, 12);
        }
    }

    [Fact]
    public void ParityIsConsistentAcrossAChainsJoints()
    {
        var region = ArchRegion();
        Assert.True(region.Contains((2, 1)));
        Assert.False(region.Contains((2, 2.4)));
        Assert.False(region.Contains((-1, 1)));
        // A ray whose ordinate lands exactly on a joint must still be counted once: the
        // stored-endpoint rule is what makes it so (the sin(2 pi) lesson, for a cubic).
        Assert.False(region.Contains((-1, 0)));
        Assert.False(region.Contains((Width + 1, 0)));
    }

    [Fact]
    public void ALineCutsACubicAtTheParametersItsCubicSays()
    {
        var cut = CurvedEdge2d.Line((-1, 0.5 * Height), (Width + 1, 0.5 * Height));
        var contacts = CurveIntersection2d.Intersect(cut, Arch(), 1e-9);
        Assert.Equal(2, contacts.Count);
        foreach (var contact in contacts)
        {
            // Solved as roots of the SIGNED DISTANCE polynomial, so the point is on the line
            // to round-off rather than to the arrangement's tolerance.
            Assert.Equal(0.5 * Height, contact.Point.Y, 12);
            Assert.Equal(0.0, Arch().DistanceTo(contact.Point), 12);
        }
    }

    [Fact]
    public void ATangentLineIsOneTouchAndNotTwoCrossings()
    {
        // The arch's peak: the horizontal there is a DOUBLE root, which a sign change cannot
        // see. It comes back as the polynomial's critical point, once.
        var tangent = CurvedEdge2d.Line((-1, 0.75 * Height), (Width + 1, 0.75 * Height));
        var contacts = CurveIntersection2d.Intersect(tangent, Arch(), 1e-9);
        Assert.Single(contacts);
        Assert.Equal(Width / 2, contacts[0].Point.X, 9);
        Assert.Equal(0.75 * Height, contacts[0].Point.Y, 9);
    }

    [Fact]
    public void ACircleCutsACubicAtItsOwnRadius()
    {
        var circle = CurvedEdge2d.Circle((Width / 2, 0.75 * Height), 1.0);
        var contacts = CurveIntersection2d.Intersect(circle, Arch(), 1e-9);
        Assert.Equal(2, contacts.Count);
        foreach (var contact in contacts)
        {
            Assert.Equal(1.0, (contact.Point - new Vector2d(Width / 2, 0.75 * Height)).Length, 9);
            Assert.Equal(0.0, Arch().DistanceTo(contact.Point), 9);
        }
    }

    [Fact]
    public void TwoCubicsMeetOncePerCrossing()
    {
        var other = CurvedEdge2d.Bezier((0, 2), (Width / 3, -2), (2 * Width / 3, 5), (Width, 1));
        var contacts = CurveIntersection2d.Intersect(Arch(), other, 1e-9);
        Assert.Equal(2, contacts.Count);
        foreach (var contact in contacts)
        {
            // The subdivision ISOLATES and the Newton polish DECIDES, so every leaf of one
            // cluster lands on the same root and the crossing is reported once, at machine
            // precision rather than at the isolation box's size.
            double gap = (Arch().PointAt(contact.Ta) - other.PointAt(contact.Tb)).Length;
            Assert.True(gap < 1e-12, $"crossing gap {gap} should be at round-off, not at the tolerance");
        }
    }

    [Fact]
    public void OverlappingCubicsAreDedupedRatherThanSubdivided()
    {
        // Two restrictions of one cubic. SameCarrier answers in closed form, which is also
        // what stops the subdivision recursing on a curve against itself.
        var arch = Arch();
        Assert.True(CurveIntersection2d.SameCarrier(arch, arch.Sub(0.2, 0.8), 1e-9));
        Assert.True(CurveIntersection2d.SameCarrier(arch, arch.Sub(0.8, 0.2), 1e-9));   // reversed
        Assert.False(CurveIntersection2d.SameCarrier(
            arch, CurvedEdge2d.Bezier((0, 0), (0, Height + 1), (Width, Height), (Width, 0)), 1e-9));
    }

    // ---- the boolean ----

    private static CurvedRegion2d Box(double x0, double y0, double x1, double y1) =>
        new(
        [
            CurvedEdge2d.Line((x0, y0), (x1, y0)),
            CurvedEdge2d.Line((x1, y0), (x1, y1)),
            CurvedEdge2d.Line((x1, y1), (x0, y1)),
            CurvedEdge2d.Line((x0, y1), (x0, y0)),
        ]);

    [Fact]
    public void ABooleanKeepsCubicsAsCubics()
    {
        var cut = CurvedRegion2dBoolean
            .Intersection(ArchRegion(), Box(-1, -1, Width + 1, 0.5 * Height))
            .Single();
        Assert.Contains(cut.Outer, e => e.IsBezier);
        // Two cubic pieces (either flank), two straight pieces (the chord and the cut).
        Assert.Equal(2, cut.Outer.Count(e => e.IsBezier));
    }

    [Fact]
    public void ACutAndUncutArchAgreeOnTheMaterialBetweenThem()
    {
        // Inclusion-exclusion over a curved boolean, which holds exactly for the regions the
        // tier produces and needs no second algorithm to check against.
        var arch = ArchRegion();
        var window = Box(1, -1, 3, 1.5);
        double union = CurvedRegion2dBoolean.Union(arch, window).Sum(r => r.Area);
        double intersection = CurvedRegion2dBoolean.Intersection(arch, window).Sum(r => r.Area);
        Assert.Equal(arch.Area + window.Area, union + intersection, 9);
    }

    [Fact]
    public void ABooleanThatSplitsACubicFusesItBackOnTheWayOut()
    {
        // The cut runs clear of the arch, so the intersection is the arch itself and every
        // piece the arrangement had to split must come back fused.
        var arch = ArchRegion();
        var window = Box(-5, -5, Width + 5, Height + 5);
        var result = CurvedRegion2dBoolean.Intersection(arch, window).Single();
        Assert.Equal(arch.Area, result.Area, 9);
        Assert.Equal(2, result.Outer.Count);
        Assert.Single(result.Outer, e => e.IsBezier);
    }

    [Fact]
    public void ACubicRegionRefusesToBeOffsetByName()
    {
        // A cubic's offset is an algebraic curve of degree 10, so the exact tier has no
        // primitive for it and says so rather than raising the wrong slab.
        var error = Assert.Throws<ArgumentException>(
            () => CurvedRegion2dOffset.Offset(ArchRegion(), 0.5));
        Assert.Contains("cubic", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("degree 10", error.Message);
    }

    // ---- the fan comparator ----

    [Fact]
    public void TheGraphJetReproducesTheCurvatureRuleForLinesAndArcs()
    {
        var jet = new double[CurveJet2d.Coefficients];

        CurveJet2d.GraphCoefficients(CurvedEdge2d.Line((0, 0), (1, 0)), atStart: true, jet);
        Assert.All(jet, value => Assert.Equal(0, value));

        // a2 = kappa/2 for any curve, and for a circle the rest are functions of kappa alone:
        // a4 = kappa^3/8, a6 = kappa^5/16, with the odd ones exactly zero.
        const double radius = 5;
        CurveJet2d.GraphCoefficients(
            CurvedEdge2d.Arc((0, radius), radius, -Math.PI / 2, 0.5), atStart: true, jet);
        double kappa = 1 / radius;
        Assert.Equal(kappa / 2, jet[0], 12);
        Assert.Equal(0, jet[1], 12);
        Assert.Equal(kappa * kappa * kappa / 8, jet[2], 12);
        Assert.Equal(Math.Pow(kappa, 5) / 16, jet[4], 12);
    }

    [Fact]
    public void TheGraphJetsFirstCoefficientIsTheCurvatureForACubicToo()
    {
        var arch = Arch();
        var jet = new double[CurveJet2d.Coefficients];
        CurveJet2d.GraphCoefficients(arch, atStart: true, jet);
        Assert.Equal(arch.SignedCurvatureAt(0) / 2, jet[0], 12);

        // Leaving the END travels the other way, which negates the curvature.
        CurveJet2d.GraphCoefficients(arch, atStart: false, jet);
        Assert.Equal(-arch.SignedCurvatureAt(1) / 2, jet[0], 12);
    }

    [Fact]
    public void TwoCubicsOsculatingToSecondOrderAreStillOrderedByTheJet()
    {
        // Both leave the origin along +x with the SAME curvature (y'' = 2 at 0 in each), so
        // the tangent and a2 both tie and only a3 separates them. Built in power basis and
        // converted, so the agreement is exact rather than arranged.
        var gentle = FromPowers(0, 1, 1, 0.5);
        var steep = FromPowers(0, 1, 1, 3.0);

        var gentleJet = new double[CurveJet2d.Coefficients];
        var steepJet = new double[CurveJet2d.Coefficients];
        CurveJet2d.GraphCoefficients(gentle, atStart: true, gentleJet);
        CurveJet2d.GraphCoefficients(steep, atStart: true, steepJet);

        Assert.Equal(gentleJet[0], steepJet[0], 12);           // a2 ties: they osculate
        Assert.True(steepJet[1] > gentleJet[1] + 1e-9);        // a3 separates them
    }

    [Fact]
    public void AnArrangementOrdersOsculatingCubicsWithoutGuessing()
    {
        // Two arches sharing both endpoints and TANGENT AND CURVATURE at the left one — the
        // configuration the lines-and-arcs tie-break provably could not decide. Their lens is
        // a single cell whose area is the difference of the two Green's terms.
        //
        // The construction is exact rather than fitted: with P0 and P1 shared, the curvature
        // at P0 is proportional to (P1 − P0) × (P2 − 2P1 + P0), so every P2 on one LINE gives
        // the same curvature — (3, 2) and (5, 4) both do.
        var lower = CurvedEdge2d.Bezier((0, 0), (1, 1), (3, 2), (4, 0));
        var upper = CurvedEdge2d.Bezier((0, 0), (1, 1), (5, 4), (4, 0));
        Assert.Equal(lower.TangentAt(0), upper.TangentAt(0));
        Assert.Equal(lower.SignedCurvatureAt(0), upper.SignedCurvatureAt(0));

        // The fixture must CARRY the configuration: the two curves meet only at their shared
        // endpoints, so the region between them really is one cell.
        var meetings = CurveIntersection2d.Intersect(lower, upper, 1e-9);
        Assert.Equal(2, meetings.Count);
        Assert.All(meetings, c => Assert.True(
            c.Point.DistanceTo((0, 0)) < 1e-9 || c.Point.DistanceTo((4, 0)) < 1e-9,
            $"the two cubics meet away from their endpoints, at {c.Point}"));

        var arrangement = new CurvedArrangement2d();
        arrangement.Insert(lower);
        arrangement.Insert(upper);
        var cells = arrangement.ExtractCells();
        var lens = Assert.Single(cells);

        var anchor = Vector2d.Zero;
        double expected = Math.Abs(
            upper.SignedAreaTerm(anchor) - lower.SignedAreaTerm(anchor));
        Assert.Equal(expected, Math.Abs(lens.Area), 9);
    }

    /// <summary>A cubic from its power-basis coefficients (c0 + c1 t + c2 t² + c3 t³ in y,
    /// with x = t), so a prescribed contact order is exact rather than fitted.</summary>
    private static CurvedEdge2d FromPowers(double c0, double c1, double c2, double c3)
    {
        // x(t) = t: control points 0, 1/3, 2/3, 1. y from the power basis by inverting
        // the Bezier-to-power map.
        double y0 = c0;
        double y1 = c0 + c1 / 3;
        double y2 = c0 + 2 * c1 / 3 + c2 / 3;
        double y3 = c0 + c1 + c2 + c3;
        return CurvedEdge2d.Bezier((0, y0), (1.0 / 3, y1), (2.0 / 3, y2), (1, y3));
    }
}
