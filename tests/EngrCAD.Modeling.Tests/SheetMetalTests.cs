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

    [Fact]
    public void AReliefOnAFlangesTipIsRefusedByName()
    {
        var body = SheetMetalBody.Base(Plate(), Spec())
            .WithFlange(SheetFlangeTarget.BaseEdge(1), 25);
        var exception = Assert.Throws<NotSupportedException>(() => body.WithFlange(
            SheetFlangeTarget.FlangeTip(0), 10, startOffset: 5, width: 20,
            relief: BendRelief.Rectangular()));
        Assert.Contains("base flange's edges", exception.Message, StringComparison.Ordinal);
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
        var body = SheetMetalBody.Base(Plate(), Spec());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => body.WithFlange(SheetFlangeTarget.BaseEdge(1), 20, angleDegrees: 180));
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
    /// <see cref="SheetReliefOption"/> is a SECOND spelling of <see cref="SheetReliefKind"/>
    /// (a dropdown cannot say "unset", so the feature needs its own None), and a second
    /// spelling is a drift waiting to happen. This drives EVERY member of it through a real
    /// regeneration and reads the kind back off the flange tree by NAME, so a kind added to
    /// one enum and not the other fails here rather than quietly meaning something else.
    /// </summary>
    [Fact]
    public void EveryReliefOptionReachesTheFlangeTreeAsItsOwnKind()
    {
        foreach (var option in Enum.GetValues<SheetReliefOption>())
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
            if (option == SheetReliefOption.None)
            {
                Assert.Null(flange.Relief);
                continue;
            }
            Assert.Equal(option.ToString(), flange.Relief!.Kind.ToString());
        }
        // ... and the kinds the geometry carries are exactly the options minus None.
        Assert.Equal(
            Enum.GetNames<SheetReliefKind>().Order(),
            Enum.GetNames<SheetReliefOption>().Where(n => n != nameof(SheetReliefOption.None)).Order());
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
                Relief = SheetReliefOption.Rectangular,
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
                Relief = SheetReliefOption.Obround, ReliefWidth = 4,
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
