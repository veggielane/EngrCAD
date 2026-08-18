using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// A section through a plane FLUSH with a planar face. The fixture with teeth is the fused STEP
/// BLOCK: a slab of footprint A under a boss of footprint B ⊂ A, sectioned at the step plane.
/// The naive repair — flush faces contribute their own regions, everything transversal sections
/// as before, union — returns exactly <c>A∖B</c> there, which is neither limit; the limit from
/// below is A and the limit from above is B.
/// </summary>
public class FlushSectionTests
{
    private const double SlabX = 40, SlabY = 30, SlabZ = 10;
    private const double BossX = 20, BossY = 15, BossZ = 10;

    private const double AreaA = SlabX * SlabY;          // 1200 — the limit from below
    private const double AreaB = BossX * BossY;          //  300 — the limit from above
    private const double AreaNaive = AreaA - AreaB;      //  900 — what the naive repair returns

    /// <summary>Slab z ∈ [0, 10] fused with a boss z ∈ [10, 20]; the step plane is z = 10.</summary>
    private static Shape StepBlock() =>
        Shape.Box(SlabX, SlabY, SlabZ).Translate(0, 0, SlabZ / 2)
        | Shape.Box(BossX, BossY, BossZ).Translate(0, 0, SlabZ + BossZ / 2);

    private static SketchPlane StepPlane() =>
        new(Frame3d.FromOrthonormal(new Vector3d(0, 0, SlabZ), Vector3d.UnitX, Vector3d.UnitY));

    private static double TotalArea(IEnumerable<Region2d> regions) => regions.Sum(r => r.Area);

    // ---- the decision ----

    [Fact]
    public void TheStepBlocksTwoLimitsAreTheSlabAndTheBoss_AndNeitherIsWhatTheNaiveRepairReturns()
    {
        var limits = PlanarSection.FlushLimitsOf(StepBlock().ToBrep(), StepPlane().Frame);

        Assert.Equal(AreaA, TotalArea(limits.Below), 1e-6 * AreaA);
        Assert.Equal(AreaB, TotalArea(limits.Above), 1e-6 * AreaB);
        Assert.Equal(AreaA, TotalArea(limits.Union()), 1e-6 * AreaA);

        // The mutation that proves the fixture bites: A∖B is 900 and is what a flush-faces-plus-
        // transversal union produces. Nothing here may return it.
        foreach (double area in new[] { TotalArea(limits.Below), TotalArea(limits.Above), TotalArea(limits.Union()) })
            Assert.True(Math.Abs(area - AreaNaive) > 1, $"a limit read {area}, which is A∖B");

        // ...and A∖B is an ANNULUS, so the union having no hole is the structural half of the
        // same statement (an area comparison alone could be met by a different region).
        var union = limits.Union();
        Assert.Single(union);
        Assert.Empty(union[0].Holes);
    }

    [Fact]
    public void TheFlushPolicyRoutesThroughTheSameLimits()
    {
        var solid = StepBlock().ToBrep();
        var plane = StepPlane().Frame;
        Assert.Equal(AreaA, TotalArea(PlanarSection.OfSolid(solid, plane, flush: FlushSection.Below)), 1e-6 * AreaA);
        Assert.Equal(AreaB, TotalArea(PlanarSection.OfSolid(solid, plane, flush: FlushSection.Above)), 1e-6 * AreaB);
        Assert.Equal(AreaA, TotalArea(PlanarSection.OfSolid(solid, plane, flush: FlushSection.Union)), 1e-6 * AreaA);
    }

    [Fact]
    public void ShapeSectionSpeaksTheSameVocabulary()
    {
        var shape = StepBlock();
        var plane = StepPlane();
        Assert.Equal(AreaA, TotalArea(shape.Section(plane, flush: FlushSection.Below)), 1e-6 * AreaA);
        Assert.Equal(AreaB, TotalArea(shape.Section(plane, flush: FlushSection.Above)), 1e-6 * AreaB);

        // ...and the exact tier stays exact: a drilled plate sectioned flush with its own top
        // face reads the closed form 60*40 - pi*r^2, which a flattened section cannot.
        const double r = 6;
        var drilled = Shape.Box(60, 40, 12).Translate(0, 0, 6) - Shape.Cylinder(r, 40).Translate(0, 0, 6);
        var top = new SketchPlane(Frame3d.FromOrthonormal(new Vector3d(0, 0, 12), Vector3d.UnitX, Vector3d.UnitY));
        double exact = 60 * 40 - Math.PI * r * r;
        double curved = drilled.SectionExact(top, flush: FlushSection.Below).Sum(x => x.Area);
        Assert.Equal(exact, curved, 1e-9 * exact);

        // The flattened tier is over by the inscribed n-gon's own deficit — the same gap
        // SectionExact exists to close — so it is measurably WORSE, never equal.
        double flat = drilled.Section(top, flush: FlushSection.Below).Sum(x => x.Area);
        Assert.True(flat > exact, "a flattened bore rim is an inscribed polygon and over-reports the area");
    }

    // ---- the default is untouched ----

    [Fact]
    public void RefusingStaysTheDefault_AndNamesTheWayThrough()
    {
        var solid = StepBlock().ToBrep();
        var plane = StepPlane().Frame;
        var ex = Assert.Throws<NotSupportedException>(() => PlanarSection.OfSolid(solid, plane));
        Assert.Contains("flush", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FlushSection", ex.Message, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => StepBlock().Section(StepPlane()));
        Assert.Throws<NotSupportedException>(() => StepBlock().SectionExact(StepPlane()));
    }

    [Fact]
    public void ATransversalSectionIsBitIdenticalWhateverThePolicySays()
    {
        // The policy only ever fires on a flush plane, so an ordinary cut must not move by a
        // single bit under any member of the enum.
        var solid = (Shape.Box(60, 40, 20) - Shape.Cylinder(6, 40)).ToBrep();
        var plane = Frame3d.FromOrthonormal(new Vector3d(0, 0, 3.25), Vector3d.UnitX, Vector3d.UnitY);
        var baseline = PlanarSection.OfSolid(solid, plane);

        foreach (var policy in Enum.GetValues<FlushSection>())
            AssertSameBits(baseline, PlanarSection.OfSolid(solid, plane, flush: policy));

        var curvedBaseline = PlanarSection.CurvedOfSolid(solid, plane);
        foreach (var policy in Enum.GetValues<FlushSection>())
        {
            var other = PlanarSection.CurvedOfSolid(solid, plane, flush: policy);
            Assert.Equal(curvedBaseline.Count, other.Count);
            for (int i = 0; i < other.Count; i++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(curvedBaseline[i].Area),
                    BitConverter.DoubleToInt64Bits(other[i].Area));
        }

        static void AssertSameBits(IReadOnlyList<Region2d> a, IReadOnlyList<Region2d> b)
        {
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].Outer.Count, b[i].Outer.Count);
                for (int j = 0; j < a[i].Outer.Count; j++)
                {
                    Assert.Equal(BitConverter.DoubleToInt64Bits(a[i].Outer[j].X),
                        BitConverter.DoubleToInt64Bits(b[i].Outer[j].X));
                    Assert.Equal(BitConverter.DoubleToInt64Bits(a[i].Outer[j].Y),
                        BitConverter.DoubleToInt64Bits(b[i].Outer[j].Y));
                }
            }
        }
    }

    // ---- the nudge ----

    [Fact]
    public void TheNudgeIsRelativeToTheMODEL_SoTheAnswerIsScaleFree()
    {
        // Three decades, which is what the machinery underneath supports: below ~0.1 the
        // COPLANAR boolean that fuses this fixture refuses ("arrangement tracing did not
        // close"), and above ~1e3 `OfSolid` itself returns an empty section whatever the plane
        // (its crossing weld is the ABSOLUTE 1e-9 tier, which at coordinates of 1e4 is below
        // the ulp) — both measured, both pinned below, and neither introduced here.
        foreach (double s in new[] { 0.1, 1.0, 100.0 })
        {
            var shape = Shape.Box(SlabX * s, SlabY * s, SlabZ * s).Translate(0, 0, SlabZ * s / 2)
                | Shape.Box(BossX * s, BossY * s, BossZ * s).Translate(0, 0, SlabZ * s + BossZ * s / 2);
            var plane = Frame3d.FromOrthonormal(new Vector3d(0, 0, SlabZ * s), Vector3d.UnitX, Vector3d.UnitY);
            var limits = PlanarSection.FlushLimitsOf(shape.ToBrep(), plane);

            Assert.Equal(AreaA * s * s, TotalArea(limits.Below), 1e-6 * AreaA * s * s);
            Assert.Equal(AreaB * s * s, TotalArea(limits.Above), 1e-6 * AreaB * s * s);

            // The nudge stated independently, from the fixture's own known extent rather than
            // from the code that computed it.
            double diagonal = s * Math.Sqrt(SlabX * SlabX + SlabY * SlabY + (SlabZ + BossZ) * (SlabZ + BossZ));
            Assert.Equal(PlanarSection.FlushNudgeFraction * diagonal, limits.Nudge, 1e-9 * limits.Nudge);
        }
    }

    [Fact]
    public void SectioningItselfGoesQUIET_AboveAboutAThousandTimesUnitScale_WhichIsNotThisFeaturesDoing()
    {
        // A locked residual rather than a claim: a PLAIN transversal section of a PLAIN box —
        // no flush machinery anywhere — comes back EMPTY at 1e3x and 1e4x, which is the silent
        // direction for a failure to run in. Measured 1200 / 1.2e7 correct at 1x and 100x, then
        // 0 against a true 1.2e9 at 1000x. Filed in todo.md; when it is fixed this test fails
        // and names the boundary that moved.
        foreach (double s in new[] { 1.0, 100.0 })
        {
            var box = Shape.Box(40 * s, 30 * s, 20 * s).ToBrep();
            var plane = Frame3d.FromOrthonormal(new Vector3d(0, 0, 3.25 * s), Vector3d.UnitX, Vector3d.UnitY);
            Assert.Equal(1200 * s * s, TotalArea(PlanarSection.OfSolid(box, plane)), 1e-9 * 1200 * s * s);
        }

        var big = Shape.Box(40000, 30000, 20000).ToBrep();
        var far = Frame3d.FromOrthonormal(new Vector3d(0, 0, 3250), Vector3d.UnitX, Vector3d.UnitY);
        Assert.Empty(PlanarSection.OfSolid(big, far));
    }

    [Fact]
    public void ALimitIsEXACT_OnAVerticalWall_AndOffByExactlyTheNudgeOnA45DegreeOne()
    {
        // A VERTICAL wall: the section is the same region for every small nudge, so the limit is
        // reproduced exactly rather than approximated — which is the case this feature exists
        // for, since a flush face is what a vertical wall meets.
        var limits = PlanarSection.FlushLimitsOf(StepBlock().ToBrep(), StepPlane().Frame);
        Assert.Equal(AreaA, TotalArea(limits.Below), 1e-12 * AreaA);
        Assert.Equal(AreaB, TotalArea(limits.Above), 1e-12 * AreaB);

        // A 45 degree wall is where the nudge is visible, and by EXACTLY delta: a frustum whose
        // radius grows one per unit down reads pi*(r + delta)^2 one nudge below its top face.
        var frustum = Shape.Cone(bottomRadius: 20, topRadius: 10, height: 10);   // z in [-5, 5]
        var top = Frame3d.FromOrthonormal(new Vector3d(0, 0, 5), Vector3d.UnitX, Vector3d.UnitY);
        var sloped = PlanarSection.CurvedFlushLimitsOf(frustum.ToBrep(), top);
        double expected = Math.PI * (10 + sloped.Nudge) * (10 + sloped.Nudge);
        Assert.Equal(expected, sloped.Below.Sum(r => r.Area), 1e-9 * expected);
        Assert.Empty(sloped.Above);

        // ...and that it is the NUDGE rather than noise: the excess over pi*r^2 is exactly
        // pi*(2*r*delta + delta^2). The textbook FIRST-ORDER form 2*pi*r*delta is short by the
        // delta^2 term, measured at 2.9e-6 relative here — which is why the assertion carries
        // the exact expression rather than the linearisation.
        double excess = sloped.Below.Sum(r => r.Area) - Math.PI * 100;
        double d = sloped.Nudge;
        Assert.Equal(Math.PI * (2 * 10 * d + d * d), excess, 1e-9 * excess);
    }

    [Fact]
    public void AnInPlaneEDGECountsAsFlushToo()
    {
        // A plane containing a whole edge is the second configuration OfSolid refuses, and it
        // takes the same route: a box sectioned exactly at its own top face's rim.
        var box = Shape.Box(20, 20, 20).Translate(0, 0, 10);
        var top = new SketchPlane(Frame3d.FromOrthonormal(new Vector3d(0, 0, 20), Vector3d.UnitX, Vector3d.UnitY));
        Assert.True(PlanarSection.IsFlushWith(box.ToBrep(), top.Frame));
        Assert.Equal(400, TotalArea(box.Section(top, flush: FlushSection.Below)), 1e-6);
        Assert.Empty(box.Section(top, flush: FlushSection.Above));
    }

    [Fact]
    public void ATransversalPlaneIsNotFlush()
    {
        var box = Shape.Box(20, 20, 20).Translate(0, 0, 10).ToBrep();
        var mid = Frame3d.FromOrthonormal(new Vector3d(0, 0, 7.5), Vector3d.UnitX, Vector3d.UnitY);
        Assert.False(PlanarSection.IsFlushWith(box, mid));
    }

    [Fact]
    public void FlushLimitsAreDeterministic()
    {
        var solid = StepBlock().ToBrep();
        var plane = StepPlane().Frame;
        var a = PlanarSection.FlushLimitsOf(solid, plane);
        var b = PlanarSection.FlushLimitsOf(solid, plane);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.Nudge), BitConverter.DoubleToInt64Bits(b.Nudge));
        Assert.Equal(BitConverter.DoubleToInt64Bits(TotalArea(a.Below)), BitConverter.DoubleToInt64Bits(TotalArea(b.Below)));
        Assert.Equal(BitConverter.DoubleToInt64Bits(TotalArea(a.Above)), BitConverter.DoubleToInt64Bits(TotalArea(b.Above)));
    }
}
