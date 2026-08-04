using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Sheet metal v1: the K-factor bend model, folded geometry by topology surgery, and the
/// flat pattern as bookkeeping over the flange tree.
///
/// <para>The load-bearing oracle is the <b>folded-versus-flat volume identity</b>. The
/// folded body's bend is an annular sector of area <c>θ·T·(R + T/2)</c> per unit width;
/// the flat blank spends <c>BA·T = θ·T·(R + K·T)</c> there. Those agree EXACTLY at
/// K = 0.5 and differ by <c>θ·T²·(0.5 − K)</c> per unit width otherwise — a closed form,
/// not a fudge, and a stronger test than "the two volumes are roughly equal" because it
/// pins the K-factor's effect in both direction and magnitude.</para>
/// </summary>
public class SheetMetalTests
{
    private const double Thickness = 1.5;
    private const double Radius = 2.0;
    private const double PlateX = 80;
    private const double PlateY = 50;

    /// <summary>A rectangle in the XY plane with its lower-left corner at the origin, so
    /// segment 1 is the +X edge and segment 2 the +Y edge.</summary>
    private static Sketch Plate(double x = PlateX, double y = PlateY) =>
        Sketch.Polygon([new(0, 0), new(x, 0), new(x, y), new(0, y)]);

    private static SheetMetalSpec Spec(double k = SheetMaterials.MildSteel) =>
        new(Thickness, Radius, k);

    private static double FoldedVolume(SheetMetalBody body) =>
        BrepMassProperties.Compute(body.Solid.ToBrep()).Volume;

    // -------------------------------------------------------------------- bend model

    [Fact]
    public void BendAllowanceIsTheNeutralAxisArcLength()
    {
        // A 90-degree bend, R = 2, T = 1.5, K = 0.44: BA = (pi/2)(2 + 0.66) = 4.1783.
        double allowance = SheetMetalSpec.BendAllowance(Math.PI / 2, 2, 1.5, 0.44);
        Assert.Equal(Math.PI / 2 * (2 + 0.44 * 1.5), allowance, 12);
    }

    [Fact]
    public void BendDeductionIsTwoSetbacksLessTheAllowance()
    {
        const double angle = Math.PI / 2;
        double setback = SheetMetalSpec.OutsideSetback(angle, Radius, Thickness);
        double allowance = SheetMetalSpec.BendAllowance(angle, Radius, Thickness, 0.44);
        Assert.Equal(2 * setback - allowance,
            SheetMetalSpec.BendDeduction(angle, Radius, Thickness, 0.44), 12);
        // The setback of a square bend is exactly R + T (tan 45 = 1).
        Assert.Equal(Radius + Thickness, setback, 12);
    }

    [Fact]
    public void AKFactorOutsideTheSheetIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SheetMetalSpec.BendAllowance(Math.PI / 2, 2, 1.5, 1.2));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SheetMetalSpec.BendAllowance(Math.PI / 2, 2, 1.5, 0));
    }

    // ----------------------------------------------------------------- folded solid

    [Fact]
    public void ABaseFlangeIsExactlyItsSketchExtrudedToThickness()
    {
        var body = SheetMetalBody.Base(Plate(), Spec());
        var solid = body.Solid.ToBrep();
        solid.Validate();
        Assert.Equal(PlateX * PlateY * Thickness, BrepMassProperties.Compute(solid).Volume, 6);
    }

    [Fact]
    public void AFoldedSheetIsBRepNative()
    {
        var body = SheetMetalBody.Base(Plate(), Spec())
            .WithFlange(SheetFlangeTarget.BaseEdge(1), length: 25);
        var report = body.Solid.Explain(TargetRep.Brep);
        Assert.True(report.IsConvertible);
        Assert.All(report.Entries, e => Assert.Equal(NodeSupport.Native, e.Support));
    }

    [Fact]
    public void ANinetyDegreeFlangesTipLandsWhereClosedFormPutsIt()
    {
        // Bend-outside: the bend's tangent line IS the named edge, so the flange's outer
        // face lands at x = 80 + (R + T) and the outer virtual sharp is on the plate's
        // own bottom plane there. A 25 mm flange measured from it reaches z = 25 exactly.
        const double flangeLength = 25;
        var body = SheetMetalBody.Base(Plate(), Spec())
            .WithFlange(SheetFlangeTarget.BaseEdge(1), flangeLength);

        var bounds = Aabb.Empty;
        foreach (var vertex in body.Solid.ToBrep().Vertices)
            bounds = bounds.Union(vertex.Position);

        Assert.Equal(PlateX + Radius + Thickness, bounds.Max.X, 9);
        Assert.Equal(flangeLength, bounds.Max.Z, 9);
    }

    [Fact]
    public void ADownFlangeMirrorsAnUpFlangeThroughTheSheet()
    {
        var up = SheetMetalBody.Base(Plate(), Spec())
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 25);
        var down = SheetMetalBody.Base(Plate(), Spec())
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 25, direction: SheetBendDirection.Down);

        Assert.Equal(FoldedVolume(up), FoldedVolume(down), 6);

        var bounds = Aabb.Empty;
        foreach (var vertex in down.Solid.ToBrep().Vertices)
            bounds = bounds.Union(vertex.Position);
        // The flange now hangs below the sheet: its outer face reaches z = -(25 - T).
        Assert.Equal(-(25 - Thickness), bounds.Min.Z, 9);
    }

    // -------------------------------------------------------- the volume identity oracle

    [Fact]
    public void AtKOfAHalf_TheFoldedVolumeEqualsTheFlatBlanksExactly()
    {
        var body = SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
            .WithFlange(SheetFlangeTarget.BaseEdge(1), length: 25);

        double folded = FoldedVolume(body);
        double flat = body.Unfold().Volume;
        // Relative tolerance set by the tessellate-then-Richardson mass properties, not by
        // the bend model: the bend band is the only curved surface in the part.
        Assert.Equal(1.0, folded / flat, 6);
    }

    [Theory]
    [InlineData(SheetMaterials.SoftAluminium)]
    [InlineData(SheetMaterials.Aluminium)]
    [InlineData(SheetMaterials.MildSteel)]
    [InlineData(SheetMaterials.Stainless)]
    public void AwayFromKOfAHalf_TheGapIsExactlyWhatTheFormulaPredicts(double k)
    {
        const double angle = Math.PI / 2;
        var body = SheetMetalBody.Base(Plate(), Spec(k))
            .WithFlange(SheetFlangeTarget.BaseEdge(1), length: 25);

        double folded = FoldedVolume(body);
        double flat = body.Unfold().Volume;
        // Per unit width the folded bend holds theta*T*(R + T/2) and the blank spends
        // theta*T*(R + K*T); the whole difference is that, times the flange's width.
        double predicted = PlateY * angle * Thickness * Thickness * (0.5 - k);
        // The blank's volume is exact; the folded one is tessellate-then-Richardson, whose
        // grade on a curved solid is ~1e-8 relative — that, not a chosen epsilon, is what
        // the residual has to clear. Measured here: 4.9e-6 on a folded volume of 6.1e3.
        Assert.True(
            Math.Abs((folded - flat) - predicted) < 1e-8 * folded,
            $"predicted {predicted:g12}, measured {folded - flat:g12}");
        Assert.True(folded > flat, "K below 0.5 must make the blank SHORTER than the folded material.");
    }

    [Fact]
    public void TwoOppositeFlanges_KeepTheIdentityThroughBothBends()
    {
        var body = SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 25)
            .WithFlange(SheetFlangeTarget.BaseEdge(3), 18);

        Assert.Equal(1.0, FoldedVolume(body) / body.Unfold().Volume, 6);
    }

    [Fact]
    public void AFlangeOnAFlange_KeepsTheIdentityThroughTheChain()
    {
        var body = SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 25)
            .WithFlange(SheetFlangeTarget.FlangeTip(0), 12);

        var solid = body.Solid.ToBrep();
        solid.Validate();
        Assert.Equal(1.0, BrepMassProperties.Compute(solid).Volume / body.Unfold().Volume, 6);
    }

    [Fact]
    public void AnInsetFlange_KeepsTheIdentityAndLeavesTheEdgeIntactEitherSide()
    {
        var body = SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 25, startOffset: 10, width: 30);

        var solid = body.Solid.ToBrep();
        solid.Validate();
        Assert.Equal(1.0, BrepMassProperties.Compute(solid).Volume / body.Unfold().Volume, 6);
    }

    /// <summary>
    /// A flange running to ONE end of its edge — the ordinary shop case v1 refused as "a
    /// corner in disguise". It is not one: the flush end splices the flange's cross-section
    /// into the neighbouring wall, the inset end caps and stubs, and the two share no
    /// coedge. Both orientations are exercised because which end of the edge is the
    /// surgery's Q0 depends on the bend axis, and a rule that only works one way round
    /// would pass a single-sided test.
    /// </summary>
    [Theory]
    [InlineData(0.0, 30.0)]                     // flush at the edge's start
    [InlineData(PlateY - 30.0, 30.0)]           // flush at its end
    public void AFlangeFlushAtOneEndOnly_KeepsTheIdentity(double startOffset, double width)
    {
        var body = SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 25, startOffset: startOffset, width: width);

        var solid = body.Solid.ToBrep();
        solid.Validate();
        Assert.Equal(1.0, BrepMassProperties.Compute(solid).Volume / body.Unfold().Volume, 6);
    }

    /// <summary>
    /// What a half-inset flange must weigh, in closed form. Against the full-width flange
    /// it is short by exactly the strip of blank it does not carry — <c>(edge − width)</c>
    /// by <c>(allowance + wall)</c> by the thickness — and against a doubly-inset flange of
    /// the same width it weighs the SAME, since only the wall stub moved. Between them
    /// those two say the flush end kept its wall and the inset end capped, rather than
    /// either rule having been applied twice.
    /// </summary>
    [Fact]
    public void AHalfInsetFlangeWeighsExactlyWhatTheClosedFormSays()
    {
        const double width = 30, length = 25;
        SheetMetalBody Flanged(double? startOffset, double? span) =>
            SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
                .WithFlange(SheetFlangeTarget.BaseEdge(1), length, startOffset: startOffset ?? 0, width: span);

        double allowance = SheetMetalSpec.BendAllowance(
            Math.PI / 2, Radius, Thickness, SheetMaterials.Coined);
        double wall = length - SheetMetalSpec.OutsideSetback(Math.PI / 2, Radius, Thickness);
        double missing = (PlateY - width) * (allowance + wall) * Thickness;

        Assert.Equal(missing, FoldedVolume(Flanged(0, null)) - FoldedVolume(Flanged(0, width)), 4);
        Assert.Equal(FoldedVolume(Flanged(10, width)), FoldedVolume(Flanged(0, width)), 4);
    }

    // ------------------------------------------------------------------ bend reliefs

    /// <summary>
    /// A relief is a NOTCH IN THE BLANK, so the first thing to pin is that the blank's own
    /// area falls by exactly the notches' closed-form area — which is also the assertion
    /// that catches the two ways the notch could be drawn wrong (an inward-turning arc
    /// swept the wrong way, or a depth taken outward) since both would ADD area rather than
    /// remove it.
    /// </summary>
    [Theory]
    [InlineData(SheetReliefKind.Rectangular)]
    [InlineData(SheetReliefKind.Obround)]
    public void AReliefRemovesExactlyItsOwnAreaFromTheBlank(SheetReliefKind kind)
    {
        var relief = new BendRelief(kind, Width: 3, Depth: 4);
        var body = SheetMetalBody.Base(Plate(), Spec())
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 25, startOffset: 10, width: 30, relief: relief);

        double notch = relief.AreaOf(3, 4);
        Assert.Equal(PlateX * PlateY - 2 * notch, body.BaseOutline.Area(), 9);
        // The dome is an exact arc, not a chord run: an obround outline carries one.
        Assert.Equal(kind == SheetReliefKind.Obround ? 2 : 0,
            body.BaseOutline.ToCurves().Count(c => c is Arc2d));
    }

    /// <summary>
    /// The blank's notch and the folded body's notch are the SAME declaration, so the
    /// folded solid must lose exactly what the blank lost — <c>area × thickness</c> per
    /// notch — against the identical flange with no relief. That is the assertion that says
    /// the relief reached the solid at all, and it is exact because the notch is cut from a
    /// prismatic region.
    /// </summary>
    [Fact]
    public void AReliefRemovesTheSameMaterialFromTheFoldedBodyAsFromTheBlank()
    {
        var relief = BendRelief.Rectangular(width: 2.5, depth: 5);
        SheetMetalBody Flanged(BendRelief? r) =>
            SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
                .WithFlange(SheetFlangeTarget.BaseEdge(1), 25, startOffset: 10, width: 30, relief: r);

        double notches = 2 * relief.AreaOf(2.5, 5) * Thickness;
        Assert.Equal(notches, FoldedVolume(Flanged(null)) - FoldedVolume(Flanged(relief)), 5);
        Assert.Equal(notches, Flanged(null).Unfold().Volume - Flanged(relief).Unfold().Volume, 9);

        var solid = Flanged(relief).Solid.ToBrep();
        solid.Validate();
    }

    /// <summary>
    /// <b>The oracle extended, and this is the point of it.</b> A relief takes the same
    /// material out of the folded body and out of the blank, so it cannot move the
    /// folded-versus-flat discrepancy at all: the gap is still
    /// <c>Σ width·θ·T²·(0.5 − K)</c>, with the relief contributing nothing. A relief that
    /// notched only one of the two views — the failure a "the volumes are close enough"
    /// test would wave through — shows up here as a gap wrong by the notch's whole volume,
    /// which at these dimensions is a hundred times the residual.
    /// </summary>
    [Theory]
    [InlineData(SheetMaterials.SoftAluminium)]
    [InlineData(SheetMaterials.Coined)]
    public void AReliefLeavesTheFoldedVersusFlatDiscrepancyUNCHANGED(double k)
    {
        const double width = 30;
        var body = SheetMetalBody.Base(Plate(), Spec(k))
            .WithFlange(
                SheetFlangeTarget.BaseEdge(1), 25, startOffset: 10, width: width,
                relief: BendRelief.Obround(width: 3, depth: 4));

        double folded = FoldedVolume(body);
        double flat = body.Unfold().Volume;
        double predicted = width * (Math.PI / 2) * Thickness * Thickness * (0.5 - k);
        Assert.True(
            Math.Abs((folded - flat) - predicted) < 1e-8 * folded,
            $"predicted {predicted:g12}, measured {folded - flat:g12}");
    }

    /// <summary>
    /// A relieved flange is FLUSH against the notches' own walls, which is what makes it
    /// need no new surgery at all: the wall between the two notches IS the bend line.
    /// Counted off each solid against its own blank, since the two blanks differ — a flange
    /// spliced flush replaces one wall with its five faces (+4), while an inset one also
    /// builds two wall stubs and two end caps (+8).
    /// </summary>
    [Fact]
    public void AReliefTurnsAnInsetFlangeIntoAFlushOneOnTheNotchedWall()
    {
        SheetMetalBody Flanged(BendRelief? r) =>
            SheetMetalBody.Base(Plate(), Spec())
                .WithFlange(SheetFlangeTarget.BaseEdge(1), 25, startOffset: 10, width: 30, relief: r);

        int Blank(SheetMetalBody body) =>
            Shape.Extrude(body.BaseOutline, Thickness).ToBrep().Faces.Count();

        var inset = Flanged(null);
        var relieved = Flanged(BendRelief.Rectangular(2, 5));
        Assert.Equal(Blank(inset) + 8, inset.Solid.ToBrep().Faces.Count());
        Assert.Equal(Blank(relieved) + 4, relieved.Solid.ToBrep().Faces.Count());
    }

    [Fact]
    public void AReliefOnAFlangeSpanningItsWholeEdgeIsRefusedByName()
    {
        var body = SheetMetalBody.Base(Plate(), Spec());
        var exception = Assert.Throws<ArgumentException>(() => body.WithFlange(
            SheetFlangeTarget.BaseEdge(1), 25, relief: BendRelief.Rectangular()));
        Assert.Contains("no parent material beside it", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A relief on a flange's TIP. There is no sketch to notch there — a flange's wall is
    /// built from four corners — so the notches travel with the PARENT's construction, and
    /// what they buy is the same thing a base-edge relief buys: between them the child runs
    /// the full width of a tip face that is still four-sided, so it arrives at the surgery
    /// as an ordinary FLUSH flange.
    ///
    /// <para>The oracles are exact on both views and on both notch shapes: the blank loses
    /// exactly the notches' own closed-form area, the folded body exactly that times the
    /// thickness, and the folded-versus-flat discrepancy does not move at all — a relief
    /// takes the same material out of each, wherever it is cut.</para>
    /// </summary>
    [Theory]
    [InlineData(SheetReliefKind.Rectangular)]
    [InlineData(SheetReliefKind.Obround)]
    public void AReliefOnAFlangesTipRemovesExactlyItsOwnNotchesFromBothViews(SheetReliefKind kind)
    {
        const double width = 4, depth = 6;
        SheetMetalBody Body(BendRelief? relief) =>
            SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
                .WithFlange(SheetFlangeTarget.BaseEdge(1), 25)
                .WithFlange(
                    SheetFlangeTarget.FlangeTip(0), 12, startOffset: 12, width: 26, relief: relief);

        var plain = Body(null);
        var relieved = Body(new BendRelief(kind, width, depth));
        var solid = relieved.Solid.ToBrep();
        solid.Validate();

        double notch = new BendRelief(kind, width, depth).AreaOf(width, depth);
        Assert.Equal(plain.Unfold().Area - 2 * notch, relieved.Unfold().Area, 9);
        Assert.Equal(
            FoldedVolume(plain) - 2 * notch * Thickness, BrepMassProperties.Compute(solid).Volume, 3);

        // ... so at K = 0.5 the two views still agree exactly, notches and all.
        Assert.Equal(1.0, BrepMassProperties.Compute(solid).Volume / relieved.Unfold().Volume, 6);
    }

    /// <summary>The notched tip face is what the CHILD then bends on, and it has to be
    /// four-sided for the surgery to treat it as a plain flush wall — pinned through a
    /// grandchild flange, since that is the only thing that asks.</summary>
    [Fact]
    public void ATipReliefLeavesAWallAGrandchildFlangeCanBendOn()
    {
        var body = SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 25)
            .WithFlange(
                SheetFlangeTarget.FlangeTip(0), 14, startOffset: 12, width: 26,
                relief: BendRelief.Obround(3, 5))
            .WithFlange(SheetFlangeTarget.FlangeTip(1), 8);

        var solid = body.Solid.ToBrep();
        solid.Validate();
        Assert.Equal(1.0, BrepMassProperties.Compute(solid).Volume / body.Unfold().Volume, 6);
        Assert.Equal(3, body.Unfold().Bends.Count);
    }

    /// <summary>A tip relief flush at ONE end cuts ONE notch, not two — the same
    /// independent-ends rule an inset base flange follows, one level in. Measured against
    /// the un-relieved twin, so the count is a closed form rather than a face tally.</summary>
    [Fact]
    public void ATipReliefFlushAtOneEndCutsOneNotch()
    {
        SheetMetalBody Body(BendRelief? relief) =>
            SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
                .WithFlange(SheetFlangeTarget.BaseEdge(1), 25)
                .WithFlange(
                    SheetFlangeTarget.FlangeTip(0), 12, startOffset: 0, width: 40, relief: relief);

        var plain = Body(null);
        var relieved = Body(BendRelief.Rectangular(4, 6));
        relieved.Solid.ToBrep().Validate();

        Assert.Equal(plain.Unfold().Area - 4 * 6, relieved.Unfold().Area, 9);
        Assert.Equal(FoldedVolume(plain) - 4 * 6 * Thickness, FoldedVolume(relieved), 3);
        Assert.Equal(1.0, FoldedVolume(relieved) / relieved.Unfold().Volume, 6);
    }

    [Fact]
    public void ATipReliefDeeperThanItsParentsWallIsRefusedNamingBoth()
    {
        var body = SheetMetalBody.Base(Plate(), Spec())
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 25);
        var exception = Assert.Throws<ArgumentException>(() => body.WithFlange(
            SheetFlangeTarget.FlangeTip(0), 10, startOffset: 5, width: 20,
            relief: BendRelief.Rectangular(2, 40)));
        Assert.Contains("cut it in two rather than relieve it", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnObroundReliefShallowerThanItsOwnRadiusIsRefusedNamingBoth()
    {
        var body = SheetMetalBody.Base(Plate(), Spec());
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => body.WithFlange(
            SheetFlangeTarget.BaseEdge(1), 25, startOffset: 10, width: 30,
            relief: BendRelief.Obround(width: 6, depth: 2)));
        Assert.Contains("deepest point", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReliefThatWouldRunOffTheEdgeIsRefused()
    {
        var body = SheetMetalBody.Base(Plate(), Spec());
        var exception = Assert.Throws<ArgumentException>(() => body.WithFlange(
            SheetFlangeTarget.BaseEdge(1), 25, startOffset: 1, width: 30,
            relief: BendRelief.Rectangular(width: 4, depth: 5)));
        Assert.Contains("run off the end", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A relief that reaches past the far side of its parent — or into a hole in it — is
    /// refused NAMING THE POINT, because the failure without the guard is silent: a notch
    /// is drawn as a detour in the outline, so one running out of the parent leaves a
    /// self-intersecting blank whose SIGNED area still reads base-minus-notches and whose
    /// extrusion still validates. Measured before the guard: a 200-deep relief on this
    /// 80×50 plate gave area 2800 (exactly 4000 − 2·600) and an 18-face solid that passed
    /// <c>Validate</c>.
    /// </summary>
    [Fact]
    public void AReliefReachingOutOfItsParentIsRefusedNamingThePoint()
    {
        var deep = Assert.Throws<ArgumentException>(() =>
            SheetMetalBody.Base(Plate(), Spec()).WithFlange(
                SheetFlangeTarget.BaseEdge(1), 25, startOffset: 10, width: 30,
                relief: BendRelief.Rectangular(width: 3, depth: PlateX + 10)));
        Assert.Contains("self-intersecting", deep.Message, StringComparison.Ordinal);

        // A hole counts as outside: the notch's own corner lands in it.
        var holed = Plate().WithHole(Sketch.Circle(new Vector2d(70, 25), 8));
        var intoHole = Assert.Throws<ArgumentException>(() =>
            SheetMetalBody.Base(holed, Spec()).WithFlange(
                SheetFlangeTarget.BaseEdge(1), 25, startOffset: 20, width: 10,
                relief: BendRelief.Rectangular(width: 3, depth: 12)));
        Assert.Contains("outside the base sketch", intoHole.Message, StringComparison.Ordinal);

        // ... and a notch that stops short of the hole is fine, so the guard is not
        // simply refusing every relief on a holed plate.
        var fine = SheetMetalBody.Base(holed, Spec()).WithFlange(
            SheetFlangeTarget.BaseEdge(1), 25, startOffset: 20, width: 10,
            relief: BendRelief.Rectangular(width: 3, depth: 2));
        Assert.Equal(PlateX * PlateY - 64 * Math.PI - 2 * 6, fine.BaseOutline.Area(), 9);
    }

    /// <summary>Two flanges that do not overlap can still have reliefs that do — which is
    /// why the overlap test compares OCCUPIED stretches rather than spans.</summary>
    [Fact]
    public void TwoFlangesWhoseRELIEFSOverlapAreRefused()
    {
        var body = SheetMetalBody.Base(Plate(), Spec())
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 20, startOffset: 5, width: 15,
                relief: BendRelief.Rectangular(width: 3, depth: 4));
        // The spans [5, 20] and [22, 40] are clear of one another; the reliefs at [20, 23]
        // and [19, 22] are not.
        var exception = Assert.Throws<ArgumentException>(() => body.WithFlange(
            SheetFlangeTarget.BaseEdge(1), 20, startOffset: 22, width: 18,
            relief: BendRelief.Rectangular(width: 3, depth: 4)));
        Assert.Contains("bend relief counts as part", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReliefDefaultsAreOneThicknessWideAndRPlusTDeep()
    {
        var relief = new BendRelief();
        var spec = Spec();
        Assert.Equal(Thickness, relief.WidthFor(spec), 12);
        Assert.Equal(Radius + Thickness, relief.DepthFor(spec), 12);
        // A per-flange radius override reaches the default depth, since the depth exists to
        // clear THIS bend's tangent region rather than the sheet's nominal one.
        Assert.Equal(5 + Thickness, relief.DepthFor(spec, 5), 12);
    }

    [Fact]
    public void HolesInTheBaseSketch_CarryThroughToBothTheSolidAndTheBlank()
    {
        var sketch = Plate().WithHole(Sketch.Circle(new Vector2d(20, 25), 5));
        var body = SheetMetalBody.Base(sketch, Spec(SheetMaterials.Coined))
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 25);

        var flat = body.Unfold();
        Assert.Single(flat.Outline.Holes);
        Assert.Equal(1.0, FoldedVolume(body) / flat.Volume, 6);
    }

    // -------------------------------------------------------------- closed corners

    /// <summary>
    /// The closed corner, and its oracle is an EXACT DISCREPANCY the same way the bend
    /// model's is. Mitring makes each flange's material run to the miter plane rather than
    /// stopping at the sheet's corner, so the folded body gains exactly the first moment of
    /// that flange's own cross-section about the corner line — <c>((R+T)³ − R³)/3</c> from
    /// the bend's annular sector plus <c>T·L·(R + T/2)</c> from the wall — while the BLANK
    /// is untouched. That the blank cannot supply it is precisely what "an unrelieved
    /// corner shares material" means, and predicting it in closed form is what separates
    /// this from "the corner looks closed".
    /// </summary>
    [Fact]
    public void AClosedCornerAddsExactlyTheCrossSectionsFirstMomentAboutTheCorner()
    {
        const double length = 22, radius = 2.5;
        double wall = length - SheetMetalSpec.OutsideSetback(Math.PI / 2, radius, Thickness);

        // The "open" reference cannot be built as one body -- two full-width flanges on
        // adjacent edges is exactly what the corner declaration exists for -- so it is the
        // two one-flange bodies less the plate they share. Exact, and it makes the
        // comparison a statement about the CORNER rather than about two models.
        double OpenFolded() =>
            FoldedVolume(SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
                .WithFlange(SheetFlangeTarget.BaseEdge(1), length, bendRadius: radius))
            + FoldedVolume(SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
                .WithFlange(SheetFlangeTarget.BaseEdge(2), length, bendRadius: radius))
            - PlateX * PlateY * Thickness;
        double OpenFlat() =>
            SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
                .WithFlange(SheetFlangeTarget.BaseEdge(1), length, bendRadius: radius).Unfold().Volume
            + SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
                .WithFlange(SheetFlangeTarget.BaseEdge(2), length, bendRadius: radius).Unfold().Volume
            - PlateX * PlateY * Thickness;
        var closed = SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
            .WithCorner(
                SheetFlangeTarget.BaseEdge(1), SheetFlangeTarget.BaseEdge(2), length,
                bendRadius: radius);

        var solid = closed.Solid.ToBrep();
        solid.Validate();

        // Per flange: the bend's annular sector contributes the integral of its own radial
        // over [R, R+T] x [0, pi/2], and the wall its area times its centroid offset.
        double perFlange = (Math.Pow(radius + Thickness, 3) - Math.Pow(radius, 3)) / 3
                         + Thickness * wall * (radius + Thickness / 2);
        double added = 2 * perFlange;

        Assert.Equal(OpenFlat(), closed.Unfold().Volume, 9);   // the blank is untouched

        // The residual bar is the tessellate-then-Richardson mass properties' own grade
        // (~1e-7 relative on a curved solid), not a chosen epsilon: the corner's two bend
        // bands and the miter's two elliptical cuts are the only curved surfaces here.
        // Measured: 2.07e-3 on a folded volume of 1.07e4, i.e. 1.9e-7 relative.
        double folded = FoldedVolume(closed);
        Assert.True(
            Math.Abs(folded - (OpenFolded() + added)) < 1e-6 * folded,
            $"predicted {OpenFolded() + added:g12}, measured {folded:g12}");

        // ... so the folded-versus-flat discrepancy moves by exactly the shared material.
        double openGap = OpenFolded() - OpenFlat();
        double closedGap = folded - closed.Unfold().Volume;
        Assert.True(
            Math.Abs((closedGap - openGap) - added) < 1e-6 * folded,
            $"the corner must move the folded-vs-flat gap by exactly {added:g12}, not " +
            $"{closedGap - openGap:g12}");
    }

    /// <summary>
    /// "Closed" has to mean welded rather than merely near, so the assertion is
    /// TOPOLOGICAL: the mitred pair is one two-manifold solid whose face count is exactly
    /// the two flanges' own (no cap and no corner patch — nothing lies in the miter plane),
    /// and the sheet's own corner edge has fallen away because both walls were consumed.
    /// </summary>
    [Fact]
    public void AClosedCornerWeldsRatherThanButting()
    {
        var closed = SheetMetalBody.Base(Plate(), Spec())
            .WithCorner(SheetFlangeTarget.BaseEdge(1), SheetFlangeTarget.BaseEdge(2), 20);
        var solid = closed.Solid.ToBrep();
        solid.Validate();       // two-manifold: every edge used exactly twice

        // A plain plate has 6 faces; each flange adds 5 (two bend bands, two wall faces, a
        // tip) and consumes its wall. A mitred pair adds NO end caps at the corner, so the
        // count is exactly 6 - 2 + 10 = 14.
        Assert.Equal(14, solid.Faces.Count());

        // The two flanges' walls genuinely touch: the miter's own inside-wall corner is a
        // single vertex shared by both flanges' wall faces.
        double radius = Radius;
        var expected = new Vector3d(PlateX + radius, PlateY + radius, Thickness + radius);
        var shared = solid.Vertices.Where(v => v.Position.DistanceTo(expected) < 1e-9).ToList();
        Assert.Single(shared);
        // ... and the sheet's own corner edge has fallen away: nothing runs from the top
        // face's corner to the bottom face's, because both side walls were consumed.
        Assert.DoesNotContain(solid.Edges, e =>
            e.IsLinear(out var a, out var b)
            && SheetMetalBody.JoinsSamePoints(
                a, b, (PlateX, PlateY, 0), (PlateX, PlateY, Thickness)));
    }

    /// <summary>A corner is exact at any bend angle and any corner angle, because the
    /// miter's ellipse and its wall corner are closed forms rather than an intersection.
    /// Measured on a non-square plate at a non-square bend.</summary>
    [Theory]
    [InlineData(90.0)]
    [InlineData(70.0)]
    [InlineData(110.0)]
    public void AClosedCornerIsExactAtAnyBendAngle(double angle)
    {
        const double length = 20, radius = 2;
        double wall = length - SheetMetalSpec.OutsideSetback(angle * Math.PI / 180, radius, Thickness);

        SheetMetalBody One(int edge) =>
            SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
                .WithFlange(SheetFlangeTarget.BaseEdge(edge), length, angle, bendRadius: radius);
        double plate = PlateX * PlateY * Thickness;
        double openFolded = FoldedVolume(One(1)) + FoldedVolume(One(2)) - plate;
        double openFlat = One(1).Unfold().Volume + One(2).Unfold().Volume - plate;

        var closed = SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
            .WithCorner(
                SheetFlangeTarget.BaseEdge(1), SheetFlangeTarget.BaseEdge(2), length, angle,
                bendRadius: radius);

        var solid = closed.Solid.ToBrep();
        solid.Validate();
        Assert.True(BrepMassProperties.Compute(solid).Volume > openFolded,
            "a mitred corner can only ADD material to two flanges that stopped at the corner");
        Assert.Equal(openFlat, closed.Unfold().Volume, 9);
        Assert.True(wall > 0);
    }

    [Fact]
    public void ACornerOfMismatchedFlangesIsRefusedNamingWhy()
    {
        var plate = SheetMetalBody.Base(Plate(), Spec());

        // Edges that do not share a corner: "which corner" would otherwise be a guess.
        var apart = Assert.Throws<NotSupportedException>(() =>
            plate.WithCorner(SheetFlangeTarget.BaseEdge(1), SheetFlangeTarget.BaseEdge(3), 20)
                .Solid.ToBrep());
        Assert.Contains("exactly ONE shared point", apart.Message, StringComparison.Ordinal);

        // Two full-width flanges on ADJACENT edges declared separately still refuse, and
        // the refusal now names the corner declaration as the way out — which is the point:
        // the pair has to be located before either is built.
        var separate = Assert.Throws<NotSupportedException>(() =>
            plate.WithFlange(SheetFlangeTarget.BaseEdge(1), 20)
                 .WithFlange(SheetFlangeTarget.BaseEdge(2), 20)
                 .Solid.ToBrep());
        Assert.Contains("CORNER", separate.Message, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------------- louvres

    private static SheetMetalBody Louvred(
        double k = SheetMaterials.Coined, double angle = 45, double? clearance = null,
        double width = 20, double lance = 12) =>
        SheetMetalBody.Base(Plate(), Spec(k)).WithLouvre(new SheetLouvre(
            new Vector2d(40, 25), new Vector2d(1, 0), width, lance, angle,
            Clearance: clearance));

    /// <summary>
    /// The louvre's own term in the volume identity, and it is the SAME term an ordinary
    /// bend contributes: <c>W·θ·T²·(0.5 − K)</c>. That it comes out unchanged is the whole
    /// claim — the parent loses the tab's footprint, the tab returns it as a bend plus a
    /// wall, and the two cancel down to the K-factor's own discrepancy.
    /// </summary>
    [Theory]
    [InlineData(SheetMaterials.SoftAluminium)]
    [InlineData(SheetMaterials.MildSteel)]
    [InlineData(SheetMaterials.Coined)]
    public void ALouvreContributesExactlyOneMoreBendToTheVolumeIdentity(double k)
    {
        const double width = 20, angle = 45 * Math.PI / 180;
        var body = Louvred(k);
        var solid = body.Solid.ToBrep();
        solid.Validate();

        double predicted = width * angle * Thickness * Thickness * (0.5 - k);
        double measured = BrepMassProperties.Compute(solid).Volume - body.Unfold().Volume;
        Assert.True(
            Math.Abs(measured - predicted) < 1e-8 * BrepMassProperties.Compute(solid).Volume,
            $"predicted {predicted:g12}, measured {measured:g12}");
    }

    /// <summary>
    /// What a LANCE does to the blank, exactly: it removes its own KERF and nothing else.
    /// The tab stays — a lance separates material rather than removing it — so the blank's
    /// area falls by <c>c·(W + 2L + 2c)</c>, while the FOLDED parent gives up the whole
    /// opening. Both closed forms, and the pair is what says the two views differ by
    /// precisely the lance and not by an accident of bookkeeping.
    /// </summary>
    [Fact]
    public void ALanceCostsTheBlankItsKerfAndTheFoldedParentTheWholeOpening()
    {
        const double width = 20, lance = 12, clearance = 0.2;
        var body = Louvred(clearance: clearance);

        double kerf = clearance * (width + 2 * lance + 2 * clearance);
        Assert.Equal(PlateX * PlateY - kerf, body.Unfold().Area, 9);
        Assert.Equal(PlateX * PlateY - kerf, body.BaseOutline.Area(), 9);

        double opening = (width + 2 * clearance) * (lance + clearance);
        Assert.Equal(PlateX * PlateY - opening, body.FoldedOutline.Area(), 9);
    }

    /// <summary>
    /// <b>The clearance does not move the discrepancy at all</b>, because the kerf is
    /// removed from BOTH views identically — the bend-relief rule, applied to a lance. That
    /// is the assertion with teeth: a construction that took the kerf out of one view only
    /// would still produce a plausible solid and a plausible blank.
    /// </summary>
    [Fact]
    public void TheLanceClearanceDoesNotMoveTheFoldedVersusFlatDiscrepancy()
    {
        double Gap(double clearance)
        {
            var body = Louvred(SheetMaterials.MildSteel, clearance: clearance);
            return BrepMassProperties.Compute(body.Solid.ToBrep()).Volume - body.Unfold().Volume;
        }

        Assert.Equal(Gap(0.05), Gap(0.4), 6);
    }

    /// <summary>The tab has to be in the right PLACE, not merely of the right size: its tip
    /// is where the bend model's closed form puts it, and the parent really does have an
    /// opening under it (the folded outline's own hole).</summary>
    [Fact]
    public void ALouvresTabStandsWhereTheBendModelPutsIt()
    {
        const double angle = 60 * Math.PI / 180, lance = 12;
        var body = Louvred(angle: 60);
        var solid = body.Solid.ToBrep();
        solid.Validate();

        double allowance = SheetMetalSpec.BendAllowance(angle, Radius, Thickness, SheetMaterials.Coined);
        double wall = lance - allowance;
        // Axis one inside radius above the top face; the inside surface leaves the bend at
        // the end radial and the wall runs on from there.
        double tipX = 40 + Radius * Math.Sin(angle) + wall * Math.Cos(angle);
        double tipZ = Thickness + Radius - Radius * Math.Cos(angle) + wall * Math.Sin(angle);
        Assert.Equal(tipZ, solid.Vertices.Max(v => v.Position.Z), 9);

        // Both corners of the tab's inside tip, at the ends of a 20-wide lance about y = 25.
        foreach (double y in (ReadOnlySpan<double>)[15, 35])
        {
            var expected = new Vector3d(tipX, y, tipZ);
            Assert.True(
                solid.Vertices.Any(v => v.Position.DistanceTo(expected) < 1e-9),
                $"no tab tip vertex at {expected}");
        }
        Assert.True(tipX < 40 + lance, "the tab must stay inside its own opening at this angle");
    }

    /// <summary>Louvres compose with flanges and with each other, in both directions and on
    /// two axes — the case a single-louvre test cannot see, since the bend axis and the
    /// opening direction are what a sign error would flip.</summary>
    [Fact]
    public void SeveralLouvresComposeWithAFlange()
    {
        var body = SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 20)
            .WithLouvre(new Vector2d(25, 20), new Vector2d(1, 0), 16, 10)
            .WithLouvre(new Vector2d(55, 35), new Vector2d(0, 1), 16, 10, 60, SheetBendDirection.Down);

        var solid = body.Solid.ToBrep();
        solid.Validate();
        var flat = body.Unfold();
        Assert.Equal(3, flat.Bends.Count);   // one flange, two louvres
        Assert.Equal(1.0, BrepMassProperties.Compute(solid).Volume / flat.Volume, 6);
    }

    /// <summary>A louvre carries no edge NAME, so a mirror only has to reflect a point and a
    /// direction — asserted on vertex SETS, since a volume comparison passes a tab formed
    /// the wrong way round.</summary>
    [Fact]
    public void AMirroredLouvreIsTheExactReflection()
    {
        static SheetMetalBody Body() =>
            SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
                .WithLouvre(new Vector2d(30, 18), new Vector2d(1, 0), 14, 9, 50);

        var plain = Body().Solid.ToBrep();
        var mirrored = Body().Solid.Mirror((0, 0, 0), (1, 0, 0)).ToBrep();
        mirrored.Validate();

        var reflected = plain.Vertices
            .Select(v => new Vector3d(-v.Position.X, v.Position.Y, v.Position.Z))
            .ToList();
        foreach (var vertex in mirrored.Vertices)
        {
            Assert.True(
                reflected.Any(p => p.DistanceTo(vertex.Position) < 1e-9),
                $"mirrored vertex {vertex.Position} has no counterpart in the reflected original");
        }
    }

    /// <summary>
    /// <b>A zero-width lance is not a solid, and that is a theorem rather than a limit.</b>
    /// The tab's own side face would be coincident with the wall of the opening it came out
    /// of, everywhere the bend band still lies inside that opening — two coplanar faces with
    /// opposite normals touching over an area.
    /// </summary>
    [Fact]
    public void AZeroWidthLanceIsRefusedAsCoincidentBoundary()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => Louvred(clearance: 0));
        Assert.Contains("coincident", exception.Message, StringComparison.Ordinal);
        Assert.Contains("manifold", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A SHALLOW tab reaches FURTHER out than the flat material it came from — by the
    /// thickness the bend swings it through — so it can run into the sheet beyond its own
    /// opening while still below the sheet's own face. Refused in closed form, and the
    /// fixture is a real NEAR MISS rather than an absurd one: 8 degrees on a 4 mm lance
    /// overhangs a 0.02 mm kerf by 0.048 mm, where the same lance at 45 degrees clears it
    /// by more than a millimetre. The overhang peaks near <c>atan(T(1−K)/L)</c> and is
    /// about <c>T²(1−K)²/2L</c>, so it is a thick-sheet, short-lance, tight-kerf failure —
    /// which is exactly why the default clearance hides it and a fixture has to state one.
    /// </summary>
    [Fact]
    public void AShallowTabThatWouldRunIntoTheSheetIsRefusedInClosedForm()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => Louvred(angle: 8, clearance: 0.02, lance: 4));
        Assert.Contains("run into the material", exception.Message, StringComparison.Ordinal);

        // The same lance and kerf at a steeper angle is fine, which is what makes the
        // refusal about the geometry rather than about the dimensions.
        Louvred(angle: 45, clearance: 0.02, lance: 4).Solid.ToBrep().Validate();
    }

    [Fact]
    public void ALanceShorterThanItsOwnBendAllowanceIsRefusedNamingBoth()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => Louvred(angle: 90, lance: 2));
        Assert.Contains("develops", exception.Message, StringComparison.Ordinal);
        Assert.Contains("FLAT", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALouvreRunningOffTheSheetOrOverlappingAnotherIsRefused()
    {
        var off = Assert.Throws<ArgumentException>(() =>
            SheetMetalBody.Base(Plate(), Spec()).WithLouvre(
                new Vector2d(75, 25), new Vector2d(1, 0), 20, 12));
        Assert.Contains("strictly inside the blank", off.Message, StringComparison.Ordinal);

        var overlap = Assert.Throws<ArgumentException>(() =>
            SheetMetalBody.Base(Plate(), Spec())
                .WithLouvre(new Vector2d(40, 25), new Vector2d(1, 0), 20, 12)
                .WithLouvre(new Vector2d(45, 25), new Vector2d(1, 0), 20, 12));
        Assert.Contains("overlapping footprints", overlap.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------- multi-body sheets / welded assemblies

    /// <summary>
    /// A welded sheet assembly is several PARTS, each with its own flange tree and its own
    /// blank — and this is the end-to-end proof that the document model already expresses
    /// it: two folded panels in an assembly, placed and counted, each unfolding to its own
    /// blank through the ONE seam the CLI and the viewer button both read.
    ///
    /// <para>The assertion with teeth is the MASS: the assembly weighs exactly the two
    /// blanks' volumes times the density (at K = 0.5, where the folded and flat volumes
    /// agree exactly), which is only true if each body kept its own material and neither
    /// blank was counted twice.</para>
    /// </summary>
    [Fact]
    public void AWeldedAssemblyOfSheetPartsUnfoldsOneBlankPerBody()
    {
        var side = SheetMetalBody.Base(Plate(60, 40), Spec(SheetMaterials.Coined))
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 20);
        var lid = SheetMetalBody.Base(Plate(60, 25), Spec(SheetMaterials.Coined))
            .WithFlange(SheetFlangeTarget.BaseEdge(3), 12, direction: SheetBendDirection.Down);

        var scene = new Scene();
        var assembly = new Assembly("chassis");
        var sidePart = new Part("side", side.Solid).Of(Materials.Aluminium6061);
        var lidPart = new Part("lid", lid.Solid).Of(Materials.Aluminium6061);
        assembly.Add(sidePart);
        assembly.Add(lidPart, Frame3d.FromXY((0, 60, 0), Vector3d.UnitX, Vector3d.UnitY));
        scene.AddTab("Model").Add(assembly);

        // One blank per BODY, in scene order, each once.
        var blanks = SheetMetalFeatures.UnfoldAll(scene);
        Assert.Equal(2, blanks.Count);
        Assert.Equal(["side", "lid"], blanks.Select(b => b.Part.Name));

        // The BOM sees two items, and the assembly weighs exactly the two blanks.
        var bom = Bom.For(scene.Tabs[0]);
        Assert.Equal(2, bom.LineCount);
        double density = Materials.Aluminium6061.Density;
        Assert.Equal(
            blanks.Sum(b => b.Flat.Volume) * density,
            scene.AllInstances.MassProperties().Mass, 6);
    }

    /// <summary>A boolean of two sheet solids is a SOLID, not a sheet part — it carries no
    /// flange tree, so it has no blank, and saying so is the boundary rather than a
    /// limitation. Weld parts in an assembly, not in the geometry.</summary>
    [Fact]
    public void AUnionOfTwoSheetBodiesHasNoFlatPattern()
    {
        var a = SheetMetalBody.Base(Plate(40, 30), Spec()).Solid;
        var b = SheetMetalBody.Base(Plate(40, 30), Spec()).Solid.Translate(0, 60, 0);

        Assert.NotNull(SheetMetalFeatures.TryUnfold(new Part("a", a)));
        Assert.Null(SheetMetalFeatures.TryUnfold(new Part("welded", a | b)));
        Assert.Empty(SheetMetalFeatures.UnfoldAll([new Part("welded", a | b)]));
    }

    /// <summary>A panel placed four times is ONE blank cut four times, not four blanks —
    /// de-duplication by part reference, the rule the BOM already follows.</summary>
    [Fact]
    public void ARepeatedSheetPartYieldsOneBlank()
    {
        var panel = new Part("panel", SheetMetalBody.Base(Plate(40, 30), Spec())
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 12).Solid);
        var assembly = new Assembly("stack");
        for (int i = 0; i < 4; i++)
            assembly.Add(panel, Frame3d.FromXY((0, 0, 10 * i), Vector3d.UnitX, Vector3d.UnitY));
        var scene = new Scene();
        scene.AddTab("Model").Add(assembly);

        Assert.Equal(4, scene.AllInstances.Count());
        Assert.Single(SheetMetalFeatures.UnfoldAll(scene));
    }

    // --------------------------------------------------------------- mirrored placement

    /// <summary>
    /// A mirrored sheet part is the EXACT reflection of the unmirrored one, and that is the
    /// only oracle worth having: a flange tree is ordered and quoted on named edges, so a
    /// reflection has to move the NAMES (segment indices, span offsets, cutout coordinates)
    /// and every one of those remaps is a chance to produce a plausible different part.
    /// Comparing vertex SETS through the reflection catches all of them at once, where a
    /// volume comparison would pass a tree flipped the wrong way round.
    /// </summary>
    [Fact]
    public void AMirroredSheetIsTheExactReflectionOfTheOriginal()
    {
        // Deliberately asymmetric in every remapped quantity: a flange on one base edge
        // only, inset asymmetrically, with a chained flange and an off-centre cutout.
        static SheetMetalBody Body() =>
            SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
                .WithFlange(SheetFlangeTarget.BaseEdge(1), 25, startOffset: 8, width: 26, cutouts:
                    [Sketch.Circle(2.5).Placed((7, 9), (1, 0))])
                .WithFlange(SheetFlangeTarget.FlangeTip(0), 10, startOffset: 3, width: 12);

        var plain = Body().Solid.ToBrep();
        var mirrored = Body().Solid.Mirror((0, 0, 0), (1, 0, 0)).ToBrep();
        mirrored.Validate();

        Assert.Equal(plain.Faces.Count(), mirrored.Faces.Count());
        Assert.Equal(plain.Edges.Count(), mirrored.Edges.Count());
        Assert.Equal(
            BrepMassProperties.Compute(plain).Volume,
            BrepMassProperties.Compute(mirrored).Volume, 6);

        // Every vertex of the mirrored solid is the reflection of one of the original's.
        var reflected = plain.Vertices
            .Select(v => new Vector3d(-v.Position.X, v.Position.Y, v.Position.Z))
            .ToList();
        foreach (var vertex in mirrored.Vertices)
        {
            Assert.True(
                reflected.Any(p => p.DistanceTo(vertex.Position) < 1e-9),
                $"mirrored vertex {vertex.Position} has no counterpart in the reflected original");
        }
    }

    /// <summary>A mirror is Native now, in the Explain report as well as in the geometry —
    /// and Mirror(Mirror(x)) is the original, which is what proves the remaps are an
    /// involution rather than merely self-consistent.</summary>
    [Fact]
    public void AMirroredSheetIsBRepNativeAndMirroringTwiceIsTheIdentity()
    {
        var body = SheetMetalBody.Base(Plate(), Spec())
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 25, startOffset: 8, width: 26);

        var report = body.Solid.Mirror((0, 0, 0), (1, 0, 0)).Explain(TargetRep.Brep);
        Assert.True(report.IsConvertible);
        Assert.All(report.Entries, e => Assert.Equal(NodeSupport.Native, e.Support));

        var twice = body.Solid.Mirror((0, 0, 0), (1, 0, 0)).Mirror((0, 0, 0), (1, 0, 0)).ToBrep();
        twice.Validate();
        var once = body.Solid.ToBrep();
        Assert.Equal(once.Faces.Count(), twice.Faces.Count());
        foreach (var vertex in twice.Vertices)
        {
            Assert.True(
                once.Vertices.Any(v => v.Position.DistanceTo(vertex.Position) < 1e-9),
                $"a doubly mirrored sheet must be the original; {vertex.Position} is not on it");
        }
    }

    /// <summary>A sketch's mirror restores its winding by REVERSING the loop, so a segment
    /// at index i lands at n - 1 - i — the remap a flange target has to make. Pinned
    /// directly, since a body-level test could pass with an index map that happens to be
    /// symmetric on a rectangle.</summary>
    [Fact]
    public void AMirroredSketchKeepsItsAreaAndReversesItsSegmentOrder()
    {
        var sketch = Sketch.Start(0, 0)
            .LineTo(40, 0)
            .ArcTo((40, 20), 12, clockwise: false)
            .LineTo(0, 20)
            .Close()
            .WithHole(Sketch.Circle(3).Placed((10, 10), (1, 0)));

        var mirrored = sketch.Mirrored();
        Assert.Equal(sketch.Area(), mirrored.Area(), 9);
        Assert.Equal(sketch.Segments.Count, mirrored.Segments.Count);
        var curves = sketch.ToCurves();
        var flipped = mirrored.ToCurves();
        for (int i = 0; i < curves.Count; i++)
        {
            // Original segment i is at n - 1 - i, traversed the other way, reflected in x.
            var mine = curves[i].PointAt(curves[i].Domain.Start);
            var theirs = flipped[curves.Count - 1 - i];
            var theirEnd = theirs.PointAt(theirs.Domain.End);
            Assert.Equal(-mine.X, theirEnd.X, 9);
            Assert.Equal(mine.Y, theirEnd.Y, 9);
        }
    }

    // ------------------------------------------- hems, jogs and curls (multi-bend forms)

    /// <summary>
    /// A hem is TWO bends in the same direction, and the number that has to be right is the
    /// GAP — measured on the built solid rather than assumed from the arithmetic that set
    /// the intermediate leg. The returned leg's facing surface must sit exactly
    /// <c>gap</c> above the sheet's own quoted face, and it must run back OVER the sheet
    /// (the direction claim a one-sided test would miss).
    /// </summary>
    [Theory]
    [InlineData(SheetBendDirection.Up)]
    [InlineData(SheetBendDirection.Down)]
    public void AHemReturnsOverTheSheetAtExactlyTheDeclaredGap(SheetBendDirection direction)
    {
        const double gap = 6, returnLength = 20, radius = 1.0;
        var body = SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
            .WithHem(SheetFlangeTarget.BaseEdge(1), returnLength, gap, bendRadius: radius,
                direction: direction);

        Assert.Equal(2, body.Flanges.Count);
        var solid = body.Solid.ToBrep();
        solid.Validate();
        Assert.Equal(1.0, BrepMassProperties.Compute(solid).Volume / body.Unfold().Volume, 6);

        // The sheet's quoted face is z = T for an Up hem and z = 0 for a Down one; the
        // returned leg faces it across exactly the gap, on the same side the fold went.
        int sign = direction == SheetBendDirection.Up ? 1 : -1;
        double quoted = direction == SheetBendDirection.Up ? Thickness : 0;
        double facing = quoted + sign * gap;
        var onLeg = solid.Vertices
            .Where(v => Math.Abs(v.Position.Z - facing) < 1e-6)
            .ToList();
        Assert.NotEmpty(onLeg);
        // ... and the leg runs BACK over the plate: its far end is inboard of the bend line.
        Assert.True(onLeg.Min(v => v.Position.X) < PlateX,
            "a hem's returned leg must fold back over the sheet, not away from it");
    }

    [Fact]
    public void AClosedHemIsRefusedNamingTheCoincidentFaces()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            SheetMetalBody.Base(Plate(), Spec())
                .WithHem(SheetFlangeTarget.BaseEdge(1), 20, gap: 2 * Radius));
        Assert.Contains("CLOSED hem", exception.Message, StringComparison.Ordinal);
        Assert.Contains("coincident", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A jog steps the sheet sideways and leaves it PARALLEL to itself. Both halves are
    /// measured on the built solid: the step is exactly the declared offset, and the far
    /// leg's own faces are parallel to the sheet's (which is what the two bends being equal
    /// and opposite buys, and what a single-bend model could not produce).
    /// </summary>
    [Theory]
    [InlineData(90.0)]
    [InlineData(45.0)]
    [InlineData(120.0)]
    public void AJogStepsByExactlyItsOffsetAndLeavesTheSheetParallel(double angle)
    {
        const double offset = 12, run = 20;
        var body = SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
            .WithJog(SheetFlangeTarget.BaseEdge(1), offset, run, angle);

        var solid = body.Solid.ToBrep();
        solid.Validate();
        Assert.Equal(1.0, BrepMassProperties.Compute(solid).Volume / body.Unfold().Volume, 6);

        // The far leg is the only material past the jog: its two planar faces are
        // horizontal (parallel to the sheet) and sit at the offset above it.
        var horizontal = solid.Faces
            .Where(f => f.IsPlanar(out _, out var n) && Math.Abs(Math.Abs(n.Z) - 1) < 1e-9)
            .Select(f => f.Bounds().Center.Z)
            .ToList();
        Assert.Contains(horizontal, z => Math.Abs(z - (Thickness + offset)) < 1e-9);
        Assert.Contains(horizontal, z => Math.Abs(z - (offset)) < 1e-9);
    }

    [Fact]
    public void AJogSmallerThanItsOwnBendsIsRefusedNamingTheMinimum()
    {
        // At 90 degrees the two bends alone step 2R + T = 5.5, so 4 is impossible.
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            SheetMetalBody.Base(Plate(), Spec()).WithJog(SheetFlangeTarget.BaseEdge(1), 4, 20));
        Assert.Contains("5.5", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A curl is a CHAIN of hits, so what it produces is a polygonal roll rather than a
    /// cylinder — which is what the API says and what this measures: the declared total
    /// turn arrives as the sum of the segments' turns, read off the built solid's own
    /// faces. A part rolled 270 degrees has its far leg pointing back at the sheet.
    /// </summary>
    [Fact]
    public void ACurlTurnsThroughItsDeclaredTotalAsAChainOfBends()
    {
        const int segments = 6;
        var body = SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
            .WithCurl(SheetFlangeTarget.BaseEdge(1), 270, segments, bendRadius: 1.0, legLength: 0.8);

        Assert.Equal(segments, body.Flanges.Count);
        var solid = body.Solid.ToBrep();
        solid.Validate();
        Assert.Equal(1.0, BrepMassProperties.Compute(solid).Volume / body.Unfold().Volume, 6);

        // Each hit turns 45 degrees, so the last leg's own direction is 270 degrees from
        // the sheet's: it points back inboard and DOWNWARD. Read off the last flange's
        // tip face, whose normal is the leg's own direction.
        var flat = body.Unfold();
        Assert.Equal(segments, flat.Bends.Count);
        Assert.All(flat.Bends, b => Assert.Equal(45, b.AngleDegrees, 9));
    }

    [Fact]
    public void ACurlOfOneSegmentOrPastAHalfTurnPerBendIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SheetMetalBody.Base(Plate(), Spec()).WithCurl(SheetFlangeTarget.BaseEdge(1), 270, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SheetMetalBody.Base(Plate(), Spec()).WithCurl(SheetFlangeTarget.BaseEdge(1), 400, 2));
    }

    // ------------------------------------------------------- cutouts on a flange wall

    /// <summary>
    /// A flange cutout is ONE declaration reaching BOTH views, exactly as a bend relief is:
    /// the folded wall is punched and the blank gains the same hole through the flange's
    /// own rigid frame. So the load-bearing assertion is the volume-identity one — the
    /// folded-versus-flat discrepancy must be UNCHANGED, since the cutout removes the same
    /// material from each — beside two exact statements about how much each view lost.
    /// A blanket "the two volumes agree" waves through a cutout that reached only one.
    /// </summary>
    [Theory]
    [InlineData(SheetMaterials.Coined)]
    [InlineData(SheetMaterials.MildSteel)]
    [InlineData(SheetMaterials.SoftAluminium)]
    public void AFlangeCutoutRemovesTheSameMaterialFromBothViews(double k)
    {
        const double length = 25, radius = 4;
        var plain = SheetMetalBody.Base(Plate(), Spec(k))
            .WithFlange(SheetFlangeTarget.BaseEdge(1), length);
        var holed = SheetMetalBody.Base(Plate(), Spec(k))
            .WithFlange(SheetFlangeTarget.BaseEdge(1), length,
                cutouts: [Sketch.Circle(radius).Placed((25, 8), (1, 0))]);

        double area = Math.PI * radius * radius;
        var solid = holed.Solid.ToBrep();
        solid.Validate();

        // The blank loses exactly the circle; the folded body loses exactly circle x T.
        Assert.Equal(plain.Unfold().Area - area, holed.Unfold().Area, 9);
        Assert.Equal(FoldedVolume(plain) - area * Thickness, FoldedVolume(holed), 3);

        // ... and therefore the discrepancy the K-factor owns does not move at all.
        double plainGap = FoldedVolume(plain) - plain.Unfold().Volume;
        double holedGap = FoldedVolume(holed) - holed.Unfold().Volume;
        Assert.True(
            Math.Abs(holedGap - plainGap) < 1e-8 * FoldedVolume(plain),
            $"a cutout must not move the folded-vs-flat gap: {plainGap:g12} -> {holedGap:g12}");
    }

    /// <summary>The hole has to be in the right PLACE, not merely of the right size — and
    /// the place is what a rigid frame pair is for. Measured on the folded solid's own
    /// bounds and on the blank's own signed distance, two independent readings of one
    /// declaration.</summary>
    [Fact]
    public void AFlangeCutoutLandsWhereItsLocalCoordinatesSay()
    {
        // Local (x, y): x runs BACK along the bend line from the flange's far end, y out
        // from the tangent line. So x = 10 is at plate y = 40, and y = 6 is 6 above the
        // bend's tangent (z = T + R + 6).
        var body = SheetMetalBody.Base(Plate(), Spec())
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 25,
                cutouts: [Sketch.Circle(3).Placed((10, 6), (1, 0))]);

        var blank = body.Unfold().Outline;
        var hole = Assert.Single(blank.Holes);
        // In the blank the flange's frame origin sits at the far tangent line, so the hole
        // is 10 back along the edge (y = 40) and 6 past the bend zone.
        double allowance = SheetMetalSpec.BendAllowance(
            Math.PI / 2, Radius, Thickness, SheetMaterials.MildSteel);
        Assert.Equal(PlateX + allowance + 6, hole.Bounds.Center.X, 9);
        Assert.Equal(PlateY - 10, hole.Bounds.Center.Y, 9);

        // And on the folded solid: the bore's rim vertices sit on the wall at z centred on
        // T + R + 6, on both faces of the wall.
        var solid = body.Solid.ToBrep();
        solid.Validate();
        var rim = solid.Vertices
            .Where(v => Math.Abs(v.Position.Y - (PlateY - 10)) < 5)
            .Where(v => v.Position.X > PlateX + Radius - 1e-9)
            .ToList();
        Assert.NotEmpty(rim);
        Assert.All(rim, v => Assert.Equal(Thickness + Radius + 6, v.Position.Z, 6));
    }

    [Fact]
    public void ACutoutThatWouldCrossTheBendIsRefusedNamingTheDevelopment()
    {
        var exception = Assert.Throws<NotSupportedException>(() =>
            SheetMetalBody.Base(Plate(), Spec())
                .WithFlange(SheetFlangeTarget.BaseEdge(1), 25,
                    cutouts: [Sketch.Circle(3).Placed((20, 2), (1, 0))]));
        Assert.Contains("BEND", exception.Message, StringComparison.Ordinal);
        Assert.Contains("development", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACutoutReachingTheWallsEdgeOrOverlappingASiblingIsRefused()
    {
        // Past the tip: that is a change to the flange's OUTLINE, not a hole through it.
        Assert.Throws<ArgumentException>(() =>
            SheetMetalBody.Base(Plate(), Spec())
                .WithFlange(SheetFlangeTarget.BaseEdge(1), 25, cutouts:
                    [Sketch.Circle(3).Placed((20, 19), (1, 0))]));

        // Two cutouts whose extents meet: refused, conservatively and by name.
        var overlap = Assert.Throws<ArgumentException>(() =>
            SheetMetalBody.Base(Plate(), Spec())
                .WithFlange(SheetFlangeTarget.BaseEdge(1), 25, cutouts:
                [
                    Sketch.Circle(3).Placed((20, 9), (1, 0)),
                    Sketch.Circle(3).Placed((24, 9), (1, 0)),
                ]));
        Assert.Contains("overlapping extents", overlap.Message, StringComparison.Ordinal);
    }

    /// <summary>Several cutouts of several kinds, on a flange that itself carries a child
    /// flange — the composition test, since a cutout must not disturb the tip edge a child
    /// bends on.</summary>
    [Fact]
    public void SeveralCutoutsComposeWithAChainedFlange()
    {
        var body = SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 30, cutouts:
            [
                Sketch.Circle(3).Placed((10, 8), (1, 0)),
                Sketch.Rectangle(8, 5).Placed((30, 12), (1, 0)),
                Sketch.Slot(10, 4).Placed((25, 5), (1, 0)),
            ])
            .WithFlange(SheetFlangeTarget.FlangeTip(0), 12);

        var solid = body.Solid.ToBrep();
        solid.Validate();
        var flat = body.Unfold();
        Assert.Equal(3, flat.Outline.Holes.Count);
        Assert.Equal(1.0, BrepMassProperties.Compute(solid).Volume / flat.Volume, 6);
    }

    /// <summary>A wall cutout is a constructor INPUT (a sketch is authored geometry, not a
    /// number an editor can offer), so what has to hold is the `SaveInputs` contract: it
    /// round-trips exactly through the public curve vocabulary and rebuilds the same
    /// holes. A flange with none writes NO inputs record, which is what keeps a history
    /// saved before cutouts existed loadable.</summary>
    [Fact]
    public void FlangeCutoutsRoundTripThroughTheFeatureRegistry()
    {
        var history = History(
            new BaseFlangeFeature(Plate()) { Thickness = Thickness, BendRadius = Radius },
            new EdgeFlangeFeature(
                Sketch.Circle(3).Placed((12, 8), (1, 0)),
                Sketch.Rectangle(6, 4).Placed((30, 10), (1, 0)))
            {
                Length = 25,
                Edge = PlusXTopEdge(),
            });
        Assert.True(history.Regenerate().Succeeded);

        string saved = history.SaveHistory();
        Assert.Contains("\"cutouts\"", saved, StringComparison.Ordinal);

        var loaded = FeatureHistory.LoadHistory(saved);
        // The lambda-backed EDGE is the documented opaque case and warns; that is not what
        // this test is about, and the cutouts must come back regardless.
        Assert.Contains(loaded.Warnings, w => w.Contains("Edge", StringComparison.Ordinal));
        var rebuilt = Assert.IsType<EdgeFlangeFeature>(loaded.History.Features[1]);
        Assert.Equal(2, rebuilt.Cutouts.Count);
        Assert.Equal(Math.PI * 9, rebuilt.Cutouts[0].Area(), 9);
        Assert.Equal(24, rebuilt.Cutouts[1].Area(), 9);
        // The rebuilt cutouts drive the same geometry, read off a body built from THEM.
        Assert.Equal(
            SheetMetalBody.Base(Plate(), Spec())
                .WithFlange(SheetFlangeTarget.BaseEdge(1), 25, cutouts: rebuilt.Cutouts)
                .Unfold().Area,
            SheetMetalBody.Base(Plate(), Spec())
                .WithFlange(SheetFlangeTarget.BaseEdge(1), 25, cutouts:
                [
                    Sketch.Circle(3).Placed((12, 8), (1, 0)),
                    Sketch.Rectangle(6, 4).Placed((30, 10), (1, 0)),
                ])
                .Unfold().Area,
            12);

        // A flange with NO cutouts writes no inputs record at all, so a history saved
        // before cutouts existed still loads (the factory reads a missing record as none).
        var plain = History(
            new BaseFlangeFeature(Plate()) { Thickness = Thickness, BendRadius = Radius },
            new EdgeFlangeFeature { Length = 25, Edge = PlusXTopEdge() });
        Assert.DoesNotContain("\"cutouts\"", plain.SaveHistory(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the flat pattern

    [Fact]
    public void TheFlatLengthIsTheOutsideLegsLessTheBendDeduction()
    {
        const double angle = Math.PI / 2;
        const double flangeLength = 25;
        var body = SheetMetalBody.Base(Plate(), Spec())
            .WithFlange(SheetFlangeTarget.BaseEdge(1), flangeLength);

        double setback = SheetMetalSpec.OutsideSetback(angle, Radius, Thickness);
        double allowance = SheetMetalSpec.BendAllowance(angle, Radius, Thickness, SheetMaterials.MildSteel);
        double deduction = SheetMetalSpec.BendDeduction(angle, Radius, Thickness, SheetMaterials.MildSteel);

        // Walking the blank: the base's flat run, the bend's allowance, then the wall.
        double expected = PlateX + allowance + (flangeLength - setback);
        // And the textbook form: outside leg 1 + outside leg 2 - BD. Leg 1 runs from the
        // plate's far edge to the virtual sharp, i.e. PlateX + setback.
        Assert.Equal(expected, (PlateX + setback) + flangeLength - deduction, 9);

        var flat = body.Unfold();
        Assert.Equal(expected, flat.Outline.Bounds.Size.X, 9);
        Assert.Equal(PlateY, flat.Outline.Bounds.Size.Y, 9);
    }

    [Fact]
    public void ASmallerKFactorShortensTheBlankByExactlyTheFormula()
    {
        var loose = SheetMetalBody.Base(Plate(), Spec(SheetMaterials.SoftAluminium))
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 25);
        var coined = SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 25);

        double looseLength = loose.Unfold().Outline.Bounds.Size.X;
        double coinedLength = coined.Unfold().Outline.Bounds.Size.X;
        // BA = theta*(R + K*T), so the difference is theta*T*(K1 - K2) and nothing else.
        Assert.Equal(Math.PI / 2 * Thickness * (0.5 - SheetMaterials.SoftAluminium),
            coinedLength - looseLength, 9);
        Assert.True(looseLength < coinedLength, "A lower K puts the neutral axis nearer the inside, shortening the blank.");
    }

    [Fact]
    public void EveryBendReportsItsZoneAndDirectionOnTheFlat()
    {
        var body = SheetMetalBody.Base(Plate(), Spec())
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 25)
            .WithFlange(SheetFlangeTarget.BaseEdge(3), 18, direction: SheetBendDirection.Down);

        var flat = body.Unfold();
        Assert.Equal(2, flat.Bends.Count);
        Assert.True(flat.Bends[0].Up);
        Assert.False(flat.Bends[1].Up);
        foreach (var bend in flat.Bends)
        {
            // The two tangent lines are one allowance apart, and the centre line runs
            // halfway between them.
            Assert.Equal(bend.Allowance, bend.StartTangent.DistanceTo(bend.StartFar), 9);
            var (centreStart, _) = bend.CenterLine;
            Assert.Equal(bend.Allowance / 2, bend.StartTangent.DistanceTo(centreStart), 9);
        }
    }

    /// <summary>The bend table is read off the same <see cref="FlatBendLine"/> records the
    /// drawing's bend zones are drawn from, so every column is checkable against the bend
    /// model rather than against a second derivation.</summary>
    [Fact]
    public void TheBendTableReportsEveryBendOffTheFlatPatternsOwnRecords()
    {
        var body = SheetMetalBody.Base(Plate(), Spec())
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 25)
            .WithFlange(SheetFlangeTarget.BaseEdge(3), 18, angleDegrees: 120,
                direction: SheetBendDirection.Down);

        var flat = body.Unfold();
        var lines = flat.BendTable().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);                       // header plus one row per bend
        Assert.StartsWith("BEND", lines[0], StringComparison.Ordinal);
        Assert.Contains("UP", lines[1], StringComparison.Ordinal);
        Assert.Contains("DOWN", lines[2], StringComparison.Ordinal);

        // The bend line spans the whole edge, and the allowance is the spec's own.
        Assert.Equal(PlateY, flat.Bends[0].Length, 9);
        Assert.Contains(
            SheetMetalSpec.BendAllowance(Math.PI / 2, Radius, Thickness, SheetMaterials.MildSteel)
                .ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
            lines[1], StringComparison.Ordinal);

        // A part with no bends says so with a header and nothing else, rather than an
        // empty string a reader would take for a failure.
        Assert.Single(SheetMetalBody.Base(Plate(), Spec()).Unfold().BendTable()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void TheFlatPatternExportsAsDxfWithTheBlankAndTheBendsOnSeparateLayers()
    {
        var body = SheetMetalBody.Base(Plate().WithHole(Sketch.Circle(new Vector2d(20, 25), 5)), Spec())
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 25);

        var document = body.Unfold().ToDxf();
        Assert.Contains("CUT", document.Layers);
        Assert.Contains("BEND", document.Layers);
        Assert.Equal(DxfLineTypes.Center.Name, document.LayerLineTypes["BEND"]);
        // Blank outline + bore, plus the bend zone's two tangent lines.
        Assert.Equal(2, document.Entities.Count(e => e.Layer == "CUT"));
        Assert.Equal(2, document.Entities.Count(e => e.Layer == "BEND"));

        // Round-trips: reading the CUT layer back gives the blank at the same extent.
        var buffer = new StringWriter();
        document.Save(buffer);
        var reread = DxfDocument.Load(new StringReader(buffer.ToString()));
        var blank = reread.ToSketches("CUT").OrderByDescending(s => s.Area()).First();
        Assert.Equal(body.Unfold().Outline.Bounds.Size.X, blank.Bounds.Size.X, 6);
    }

    // ---------------------------------------------------------------------- refusals

    [Fact]
    public void ABendAlongACurvedEdgeIsRefusedByName()
    {
        var rounded = Sketch.RoundedRectangle(PlateX, PlateY, 8);
        var body = SheetMetalBody.Base(rounded, Spec());
        int arcIndex = rounded.ToCurves().Select((c, i) => (c, i)).First(p => p.c is Arc2d).i;

        var exception = Assert.Throws<NotSupportedException>(
            () => body.WithFlange(SheetFlangeTarget.BaseEdge(arcIndex), 20));
        Assert.Contains("must be STRAIGHT", exception.Message, StringComparison.Ordinal);
        // The refusal states a THEOREM rather than a missing surface type, which is the
        // whole difference: a curved bend line is not something this kernel has not got
        // round to, it is something no flat blank can produce.
        Assert.Contains("isometry", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Gaussian curvature", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reason a curved bend line is refused, MEASURED rather than asserted: the band a
    /// circular bend line would sweep is a torus segment, and a torus has non-zero Gaussian
    /// curvature everywhere a flat sheet's is zero — so the material would have to stretch.
    /// Read off the kernel's own surface rather than from the formula, by comparing the
    /// area a torus band actually has against the area the flat blank would spend on it
    /// (which is what a bend allowance is): they differ, and by a straight bend's own
    /// standard they must not.
    /// </summary>
    [Fact]
    public void ACurvedBendLineWouldNotBeAnIsometryOfTheSheet()
    {
        const double rho = 30, radius = 2, angle = Math.PI / 2;

        // A STRAIGHT bend of width w spends exactly w x BA of blank and has exactly that
        // much neutral-surface area: an isometry, which is what makes the unfold work.
        double allowance = SheetMetalSpec.BendAllowance(angle, radius, Thickness, SheetMaterials.Coined);
        double straightWidth = 2 * Math.PI * rho;
        double straightArea = straightWidth * allowance;

        // The same bend run round a circle of radius rho is a TORUS band: by Pappus its
        // neutral-surface area is the generator's length times the distance its CENTROID
        // travels, and the centroid does not sit on the bend line — so the area is not the
        // blank's, and the gap is the material that would have to come from somewhere.
        double neutral = radius + SheetMaterials.Coined * Thickness;   // neutral-surface radius
        double centroidOffset = neutral * (1 - Math.Cos(angle / 2)) * 2 / angle * Math.Sin(angle / 2);
        double torusArea = allowance * 2 * Math.PI * (rho + centroidOffset);

        Assert.True(
            Math.Abs(torusArea - straightArea) > 1e-6 * straightArea,
            "if these agreed, folding along a curve would be an isometry and the refusal would be wrong");
        // ... and the gap is what a fabricator has to stretch or shrink: about 3% here.
        Assert.InRange(Math.Abs(torusArea - straightArea) / straightArea, 0.01, 0.10);
    }

    [Fact]
    public void AFlangeShorterThanItsOwnSetbackIsRefusedNamingBoth()
    {
        var body = SheetMetalBody.Base(Plate(), Spec());
        var exception = Assert.Throws<ArgumentException>(
            () => body.WithFlange(SheetFlangeTarget.BaseEdge(1), length: 2));
        Assert.Contains("outer virtual sharp", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHemIsRefusedRatherThanApproximated()
    {
        // A hem is TWO bends, so a single 180-degree fold stays refused — and the refusal
        // now names the call that does build one, rather than the version that did not.
        var body = SheetMetalBody.Base(Plate(), Spec());
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => body.WithFlange(SheetFlangeTarget.BaseEdge(1), 20, angleDegrees: 180));
        Assert.Contains(nameof(SheetMetalBody.WithHem), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoFlangesOverlappingOnOneEdgeAreRefused()
    {
        var body = SheetMetalBody.Base(Plate(), Spec())
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 20, startOffset: 5, width: 20);
        var exception = Assert.Throws<ArgumentException>(
            () => body.WithFlange(SheetFlangeTarget.BaseEdge(1), 20, startOffset: 15, width: 20));
        Assert.Contains("cannot share the same stretch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFlangeOnAFlangesSideIsRefusedAsACornerInteraction()
    {
        var body = SheetMetalBody.Base(Plate(), Spec())
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 25);
        var exception = Assert.Throws<NotSupportedException>(
            () => body.WithFlange(new EdgeFlange(new SheetFlangeTarget(0, 1), 10)));
        Assert.Contains("corner", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TwoFlangesMeetingAtAPlateCornerAreRefusedAtLowering()
    {
        var body = SheetMetalBody.Base(Plate(), Spec())
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 25)
            .WithFlange(SheetFlangeTarget.BaseEdge(2), 25);
        var exception = Assert.Throws<NotSupportedException>(() => body.Solid.ToBrep());
        Assert.Contains("CORNER", exception.Message, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------- selectors and sites

    [Fact]
    public void EveryFlangeableEdgeIsListedAndMatchesAPickedBrepEdge()
    {
        var body = SheetMetalBody.Base(Plate(), Spec())
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 25);

        // Four base segments plus the flange's tip.
        Assert.Equal(5, body.Sites.Count);

        var solid = body.Solid.ToBrep();
        var tipSite = body.Sites.Single(s => s.Target.ParentFlange == 0);
        var edge = solid.Edges.Single(e =>
            e.IsLinear(out var a, out var b)
            && ((a.DistanceTo(tipSite.Start) < 1e-9 && b.DistanceTo(tipSite.End) < 1e-9)
                || (a.DistanceTo(tipSite.End) < 1e-9 && b.DistanceTo(tipSite.Start) < 1e-9)));
        Assert.Equal(tipSite.Target, body.SiteFor(edge).Target);
    }

    // ------------------------------------------------------------ feature integration

    /// <summary>The +X edge of the top face, named the way a design would.</summary>
    private static EdgeSetRef PlusXTopEdge(double x = PlateX) =>
        SheetMetalFeatures.EdgeBetween((x, 0, Thickness), (x, PlateY, Thickness));

    private static FeatureHistory History(params Feature[] features)
    {
        var history = new FeatureHistory();
        foreach (var feature in features)
            history.Add(feature);
        return history;
    }

    private static string Errors(RegenerationResult result) =>
        string.Join(" ", result.Statuses.Select(s => s.Error).Where(e => e is not null));

    [Fact]
    public void ASheetPartRegeneratesThroughTheFeatureHistory()
    {
        var history = History(
            new BaseFlangeFeature(Plate()) { Thickness = Thickness, BendRadius = Radius, KFactor = SheetMaterials.Coined },
            new EdgeFlangeFeature { Length = 25, Edge = PlusXTopEdge() });

        var result = history.Regenerate();
        Assert.True(result.Succeeded, result.ToString());

        var body = SheetMetalFeatures.BodyOf(result.Body, "test");
        Assert.Single(body.Flanges);
        Assert.Equal(1.0, BrepMassProperties.Compute(result.Body!.ToBrep()).Volume / body.Unfold().Volume, 6);
    }

    /// <summary>
    /// The flange overrides mean what <see cref="EdgeFlange"/> means, in the same
    /// spelling: an unset <see cref="EdgeFlangeFeature.BendRadius"/> is <c>null</c> and
    /// takes the body's, a stated one is used verbatim — and both survive a whole-history
    /// round trip, which is what needed the JSON seam to carry nullable parameters at all.
    /// </summary>
    [Fact]
    public void FlangeOverrides_AreNullWhenUnset_AndRoundTrip()
    {
        var registry = new FeatureRegistry();
        var inherited = new EdgeFlangeFeature { Length = 25, Edge = PlusXTopEdge() };
        Assert.Null(inherited.BendRadius);
        Assert.Null(inherited.Width);

        var history = History(
            new BaseFlangeFeature(Plate()) { Thickness = Thickness, BendRadius = Radius },
            new EdgeFlangeFeature { Length = 25, Edge = PlusXTopEdge(), BendRadius = 3.5 });

        // The stated radius reaches the flange tree.
        var body = SheetMetalFeatures.BodyOf(history.Regenerate().Body, "test");
        Assert.Equal(3.5, Assert.Single(body.Flanges).BendRadius);

        // ... and survives a parameter file. (The Edge is a lambda-backed query in this
        // fixture, so re-loading its opaque marker warns — the documented behaviour, and
        // the reason this asserts on the radius rather than on an empty warning list.)
        string saved = history.SaveParameters();
        Assert.Contains("\"BendRadius\": 3.5", saved, StringComparison.Ordinal);
        Assert.Single(history.LoadParameters(saved));
        Assert.Equal(3.5, ((EdgeFlangeFeature)history.Features[1]).BendRadius);
        Assert.Equal(saved, history.SaveParameters());

        // Clearing it is a value JSON can state, which is the whole point of the nullable
        // spelling: "inherit" is null, not a number standing in for it.
        Assert.Empty(history.LoadParameters(
            """{ "EdgeFlangeFeature": { "BendRadius": null } }"""));
        Assert.Null(((EdgeFlangeFeature)history.Features[1]).BendRadius);
        Assert.Equal(
            Radius,
            Assert.Single(SheetMetalFeatures.BodyOf(history.Regenerate().Body, "test").Flanges)
                .BendRadius ?? Radius);
    }

    /// <summary>
    /// <see cref="EdgeFlangeFeature.Relief"/> is a NULLABLE <see cref="SheetReliefKind"/>
    /// now that a dropdown can say "unset" (<c>ParamEditors.EnumChoices</c>), so the SECOND
    /// enum this used to need — and the drift a second spelling invites — is gone. Every
    /// kind, and the null, is still driven through a real regeneration and read back off
    /// the flange tree BY NAME, since the map from parameter to declaration is what a
    /// reader has to trust.
    /// </summary>
    [Fact]
    public void EveryReliefOptionReachesTheFlangeTreeAsItsOwnKind()
    {
        SheetReliefKind?[] options = [null, .. Enum.GetValues<SheetReliefKind>().Cast<SheetReliefKind?>()];
        foreach (var option in options)
        {
            var history = History(
                new BaseFlangeFeature(Plate()) { Thickness = Thickness, BendRadius = Radius },
                new EdgeFlangeFeature
                {
                    Length = 25,
                    Edge = PlusXTopEdge(),
                    StartOffset = 10,
                    Width = 30,
                    Relief = option,
                });
            var result = history.Regenerate();
            Assert.True(result.Succeeded, $"{option}: {result}");

            var flange = Assert.Single(SheetMetalFeatures.BodyOf(result.Body, "test").Flanges);
            if (option is null)
            {
                Assert.Null(flange.Relief);
                continue;
            }
            Assert.Equal(option.ToString(), flange.Relief!.Kind.ToString());
        }
    }

    /// <summary>
    /// A relief NOTCHES the blank, so the edge a site names arrives as pieces of itself and
    /// a selector can only pick one of them. A piece still names the same site — the
    /// flange's own offset and width say where on it the bend goes — which is what lets a
    /// second flange be placed on an edge an earlier relief has already cut into.
    /// </summary>
    [Fact]
    public void ASecondFlangeCanBeNamedOnAPieceOfAnAlreadyNotchedEdge()
    {
        // The first flange spans [5, 20] with a one-thickness relief either side, so the
        // top face's +X edge survives as [0, 3.5] and [21.5, 50].
        var history = History(
            new BaseFlangeFeature(Plate()) { Thickness = Thickness, BendRadius = Radius },
            new EdgeFlangeFeature
            {
                Length = 20, Edge = PlusXTopEdge(), StartOffset = 5, Width = 15,
                Relief = SheetReliefKind.Rectangular,
            },
            new EdgeFlangeFeature
            {
                Length = 20, StartOffset = 30, Width = 15,
                Edge = SheetMetalFeatures.EdgeBetween(
                    (PlateX, 20 + Thickness, Thickness), (PlateX, PlateY, Thickness)),
            });

        var result = history.Regenerate();
        Assert.True(result.Succeeded, result.ToString());
        var body = SheetMetalFeatures.BodyOf(result.Body, "test");
        // Both flanges landed on base edge 1, which is the mapping the piece had to carry.
        Assert.Equal(2, body.Flanges.Count);
        Assert.All(body.Flanges, f => Assert.Equal(1, f.Target.EdgeIndex));
        Assert.Equal(2, body.Unfold().Bends.Count);
        result.Body!.ToBrep().Validate();
    }

    /// <summary>The relief's own dimensions are ordinary optional parameters: stated ones
    /// reach the tree, unset ones inherit, and both survive the JSON seam.</summary>
    [Fact]
    public void ReliefDimensionsAreOptionalParametersAndRoundTrip()
    {
        var history = History(
            new BaseFlangeFeature(Plate()) { Thickness = Thickness, BendRadius = Radius },
            new EdgeFlangeFeature
            {
                Length = 25, Edge = PlusXTopEdge(), StartOffset = 10, Width = 30,
                Relief = SheetReliefKind.Obround, ReliefWidth = 4,
            });
        Assert.True(history.Regenerate().Succeeded);

        var flange = Assert.Single(SheetMetalFeatures.BodyOf(history.Regenerate().Body, "test").Flanges);
        Assert.Equal(4, flange.Relief!.Width);
        Assert.Null(flange.Relief.Depth);   // unset: the body's R + T

        string saved = history.SaveParameters();
        Assert.Contains("\"Relief\": \"Obround\"", saved, StringComparison.Ordinal);
        Assert.Contains("\"ReliefWidth\": 4", saved, StringComparison.Ordinal);
        Assert.Single(history.LoadParameters(saved));
        Assert.Equal(saved, history.SaveParameters());
    }

    [Fact]
    public void EditingTheFlangeLengthReRunsOnlyTheFlangeAndMovesTheBlank()
    {
        var history = History(
            new BaseFlangeFeature(Plate()) { Thickness = Thickness, BendRadius = Radius },
            new EdgeFlangeFeature { Length = 25, Edge = PlusXTopEdge() });
        Assert.True(history.Regenerate().Succeeded);
        double before = SheetMetalFeatures.TryUnfold(history.Regenerate().Body)!.Outline.Bounds.Size.X;

        var longer = History(
            new BaseFlangeFeature(Plate()) { Thickness = Thickness, BendRadius = Radius },
            new EdgeFlangeFeature { Length = 40, Edge = PlusXTopEdge() });
        double after = SheetMetalFeatures.TryUnfold(longer.Regenerate().Body)!.Outline.Bounds.Size.X;

        // Only the wall grew, so the blank grows by exactly the length difference.
        Assert.Equal(15.0, after - before, 9);
    }

    [Fact]
    public void AThicknessEditReSeatsTheFlangeOnTheNewTopFace()
    {
        // The selector names the edge on the ORIGINAL top face; a thicker sheet moves it,
        // which is exactly the case a persisted index would get wrong. Here the design
        // re-states the query against the new geometry, and the regeneration says so
        // loudly instead of folding on the wrong edge.
        var history = History(
            new BaseFlangeFeature(Plate()) { Thickness = 3, BendRadius = Radius },
            new EdgeFlangeFeature { Length = 25, Edge = SheetMetalFeatures.EdgeBetween((PlateX, 0, 3), (PlateX, PlateY, 3)) });
        Assert.True(history.Regenerate().Succeeded);
    }

    [Fact]
    public void AFlangeFeatureOnANonSheetBodyIsRefusedByName()
    {
        var history = History(
            // Geometrically identical to the base flange, so the edge query DOES resolve
            // and the refusal is genuinely about the missing flange tree.
            new ExtrudeSketchFeature(Plate()) { Height = Thickness },
            new EdgeFlangeFeature { Length = 25, Edge = PlusXTopEdge(PlateX) });
        var result = history.Regenerate();
        Assert.False(result.Succeeded);
        Assert.Contains("flange tree", Errors(result), StringComparison.Ordinal);
    }

    [Fact]
    public void ASheetHistorySavesAndLoadsThroughTheFeatureRegistry()
    {
        var history = History(
            new BaseFlangeFeature(Plate()) { Thickness = Thickness, BendRadius = Radius, KFactor = SheetMaterials.Coined });
        Assert.True(history.Regenerate().Succeeded);

        // The base flange writes its blank exactly (InputJson.SaveSketch), so the registry
        // must be able to rebuild it — a record that saves but cannot load is worse than
        // one that refuses to save.
        var loaded = FeatureHistory.LoadHistory(history.SaveHistory());
        Assert.True(loaded.Complete, string.Join("; ", loaded.Warnings));
        Assert.Equal(
            SheetMetalFeatures.BodyOf(history.Regenerate().Body, "test").Unfold().Area,
            SheetMetalFeatures.BodyOf(loaded.History.Regenerate().Body, "test").Unfold().Area, 9);
    }

    [Fact]
    public void TheFluentFlangeOverloadAgreesWithTheRecordsOwnDefaults()
    {
        // Two default sets for the same options is a drift waiting to happen; this is the
        // test that makes them one.
        var target = SheetFlangeTarget.BaseEdge(1);
        var viaRecord = SheetMetalBody.Base(Plate(), Spec()).WithFlange(new EdgeFlange(target, 25));
        var viaOverload = SheetMetalBody.Base(Plate(), Spec()).WithFlange(target, 25);
        Assert.Equal(viaRecord.Flanges[0], viaOverload.Flanges[0]);
    }

    [Fact]
    public void APlacedSheetIsRefusedForTheRightReason()
    {
        var placed = SheetMetalBody.Base(Plate(), Spec()).Solid.Translate(0, 0, 10);
        var exception = Assert.Throws<NotSupportedException>(
            () => SheetMetalFeatures.BodyOf(placed, "test"));
        Assert.Contains("PLACED", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAmbiguousEdgeQueryIsRefusedNamingWhatIsAvailable()
    {
        var history = History(
            new BaseFlangeFeature(Plate()) { Thickness = Thickness, BendRadius = Radius },
            new EdgeFlangeFeature { Length = 25, Edge = EdgeSetRef.Convex });
        var result = history.Regenerate();
        Assert.False(result.Succeeded);
        Assert.Contains("exactly one bend line", Errors(result), StringComparison.Ordinal);
    }

    // --------------------------------------------------------------------- placement

    [Fact]
    public void APlacedSheetScalesItsThicknessRadiusAndFlangeTogether()
    {
        var body = SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 25);

        double plain = FoldedVolume(body);
        var scaled = body.Solid.Transform(Matrix4d.CreateScale(2)).ToBrep();
        Assert.Equal(8 * plain, BrepMassProperties.Compute(scaled).Volume, 4);
    }

    [Fact]
    public void ASheetOnANonWorldPlaneFoldsTheSameShape()
    {
        var plane = SketchPlane.At((10, -4, 7), new Vector3d(0, 1, 0), new Vector3d(0, 0, 1));
        var placed = SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined), plane)
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 25);
        var flat = SheetMetalBody.Base(Plate(), Spec(SheetMaterials.Coined))
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 25);

        Assert.Equal(FoldedVolume(flat), FoldedVolume(placed), 5);
        Assert.Equal(flat.Unfold().Area, placed.Unfold().Area, 9);
    }
}
